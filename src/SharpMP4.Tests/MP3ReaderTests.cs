using System.Buffers.Binary;
using System.Diagnostics;
using SharpISOBMFF;
using SharpMP4.Builders;
using SharpMP4.Readers;
using SharpMP4.Tracks;
using Xunit;

namespace SharpMP4.Tests;

public class MP3ReaderTests
{
    [Theory]
    [InlineData(0xfffb9000u, 417, 44100, 1152, 2, 1)]
    [InlineData(0xfffa9000u, 417, 44100, 1152, 2, 1)] // CRC
    [InlineData(0xfffb92c0u, 418, 44100, 1152, 1, 1)] // padding, mono
    [InlineData(0xfff38000u, 208, 22050, 576, 2, 2)]
    [InlineData(0xffe388c0u, 576, 8000, 576, 1, 25)]
    public void ReadsVersionsChannelsCrcAndPadding(uint header, int length, int rate, int duration, int channels, int version)
    {
        var frame = Frame(header, length);
        using var input = new MemoryStream(frame.Concat(frame).ToArray());
        var reader = new MP3Reader(input);
        Assert.Equal((uint)rate, reader.Track.Timescale);
        Assert.Equal(channels, reader.Track.ChannelCount);
        Assert.Equal(version, reader.Track.MpegVersion);
        byte[] buffer = new byte[2048];
        var first = reader.ReadSample(ref buffer, out var bytes)!;
        Assert.Equal(length, bytes);
        Assert.Equal(frame, buffer[..bytes]);
        Assert.Equal(duration, first.Duration);
        Assert.Equal(0, first.DTS);
        var second = reader.ReadSample(ref buffer, out bytes)!;
        Assert.Same(first, second);
        Assert.Equal(duration, second.DTS);
        Assert.Null(reader.ReadSample(ref buffer, out _));
    }

    [Fact]
    public void SupportsVbrAndReusesBufferWithoutPerFrameAllocation()
    {
        var low = Frame(0xfffb9000, 417);
        var high = Frame(0xfffbb000, 626);
        using var input = new MemoryStream();
        for (var i = 0; i < 10000; i++) input.Write(i % 2 == 0 ? low : high);
        input.Position = 0;
        var reader = new MP3Reader(input);
        byte[] buffer = new byte[2048];
        reader.ReadSample(ref buffer, out _); // JIT/static initialization before measurement
        var originalBuffer = buffer;
        var timer = Stopwatch.StartNew();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 1; i < 10000; i++) reader.ReadSample(ref buffer, out _);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        timer.Stop();
        Console.WriteLine($"MP3 reader: {input.Length / timer.Elapsed.TotalSeconds / (1024 * 1024):F1} MiB/s, {allocated} bytes allocated for 9999 frames.");
        Assert.Same(originalBuffer, buffer);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void SkipsId3v2FooterXingAndId3v1WithoutCopyingTags()
    {
        var tag = new byte[23];
        "ID3"u8.CopyTo(tag);
        tag[3] = 4; tag[5] = 0x10; tag[9] = 3; // 3 payload bytes + 10 footer bytes
        var metadata = Frame(0xfffb9000, 417);
        "Xing"u8.CopyTo(metadata.AsSpan(36));
        var frame = Frame(0xfffb9000, 417);
        var trailer = new byte[128];
        "TAG"u8.CopyTo(trailer);
        using var input = new MemoryStream(tag.Concat(metadata).Concat(frame).Concat(trailer).ToArray());
        Assert.True(MP3Reader.IsMP3(input));
        Assert.Equal(0, input.Position);
        var reader = new MP3Reader(input);
        byte[] buffer = null!;
        Assert.NotNull(reader.ReadSample(ref buffer, out var bytes));
        Assert.Equal(frame, buffer[..bytes]);
        Assert.Null(reader.ReadSample(ref buffer, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SkipsApeTrailerWithOrWithoutHeaderAndId3v1(bool hasHeader)
    {
        var frame = Frame(0xfffb9000, 417);
        var footer = new byte[32];
        "APETAGEX"u8.CopyTo(footer);
        BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(8), 2000);
        BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(12), 48); // 16 payload bytes + footer
        BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(20), hasHeader ? 0x80000000u : 0u);
        var header = hasHeader ? footer.ToArray() : Array.Empty<byte>();
        if (hasHeader) BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), 0xa0000000);
        var id3 = new byte[128];
        "TAG"u8.CopyTo(id3);
        using var input = new MemoryStream(frame.Concat(header).Concat(new byte[16]).Concat(footer).Concat(id3).ToArray());
        var reader = new MP3Reader(input);
        byte[] buffer = null!;
        Assert.NotNull(reader.ReadSample(ref buffer, out var bytes));
        Assert.Equal(frame, buffer[..bytes]);
        Assert.Null(reader.ReadSample(ref buffer, out _));
    }

    [Fact]
    public void RejectsTruncatedTagsFramesInvalidHeadersAndFreeFormat()
    {
        Assert.Throws<InvalidDataException>(() => new MP3Reader(new MemoryStream("ID3"u8.ToArray())));
        var oversizedTag = new byte[10];
        "ID3"u8.CopyTo(oversizedTag); oversizedTag[3] = 4; oversizedTag[6] = 1;
        Assert.Throws<InvalidDataException>(() => new MP3Reader(new MemoryStream(oversizedTag)));
        Assert.Throws<InvalidDataException>(() => new MP3Reader(new MemoryStream(Frame(0xfffb9000, 20))));
        Assert.Throws<InvalidDataException>(() => new MP3Reader(new MemoryStream(Frame(0xfffb9c00, 417))));
        Assert.Throws<NotSupportedException>(() => new MP3Reader(new MemoryStream(Frame(0xfffb0000, 417))));
        var full = Frame(0xfffb9000, 417);
        using var stream = new MemoryStream(full.Concat(full[..200]).ToArray());
        var reader = new MP3Reader(stream);
        byte[] buffer = null!;
        reader.ReadSample(ref buffer, out _);
        Assert.Throws<InvalidDataException>(() => reader.ReadSample(ref buffer, out _));
    }

    [Fact]
    public void RejectsFormatChangesAndHonorsCancellation()
    {
        using var input = new MemoryStream(Frame(0xfffb9000, 417).Concat(Frame(0xfffb9400, 384)).ToArray());
        var reader = new MP3Reader(input);
        byte[] buffer = null!;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => reader.ReadSample(ref buffer, out _, cancellation.Token));
        reader.ReadSample(ref buffer, out _);
        Assert.Throws<InvalidDataException>(() => reader.ReadSample(ref buffer, out _));
    }

    [Theory]
    [InlineData(44100u, 1, 0x6b)]
    [InlineData(22050u, 2, 0x69)]
    [InlineData(8000u, 25, 0x69)]
    public void WritesCorrectMpegObjectType(uint rate, int version, int objectType)
    {
        var track = new MP3Track(1, rate, version);
        Assert.Equal(objectType, track.ObjectTypeIndication);
        Assert.Equal(track.Timescale, track.Clone().Timescale);
        Assert.NotNull(track.CreateSampleEntryBox());
    }

    [Theory]
    [InlineData(44100u, 1)]
    [InlineData(22050u, 2)]
    [InlineData(8000u, 25)]
    public void Mp4ReaderRestoresMp3TrackAndPayload(uint rate, int version)
    {
        using var output = new MemoryStream();
        var track = new MP3Track(1, rate, version);
        using var builder = new Mp4Builder(new SingleStreamOutput(output))
        {
            TemporaryStorageFactory = new TemporaryMemoryStorageFactory()
        };
        builder.AddTrack(track);
        builder.ProcessRawSample(track.TrackID, new byte[] { 1, 2, 3 }, track.DefaultSampleDuration, true);
        builder.FinalizeMedia();
        output.Position = 0;
        var container = new Container();
        container.Read(new IsoStream(output));
        var reader = new VideoReader();
        reader.Parse(container);
        var restored = Assert.IsType<MP3Track>(reader.GetTracks().Single());
        Assert.Equal(track.Timescale, restored.Timescale);
        Assert.Equal(track.MpegVersion, restored.MpegVersion);
        Assert.Equal(track.DefaultSampleDuration, restored.DefaultSampleDuration);
        Assert.Equal(new byte[] { 1, 2, 3 }, reader.ReadSample(restored.TrackID).Data);
    }

    private static byte[] Frame(uint header, int length)
    {
        var result = new byte[length];
        BinaryPrimitives.WriteUInt32BigEndian(result, header);
        return result;
    }
}
