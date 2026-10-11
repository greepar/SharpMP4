using SharpISOBMFF;
using SharpMP4.Builders;
using SharpMP4.Readers;
using SharpMP4.Tracks;
using Xunit;
using System.Runtime.InteropServices;

namespace SharpMP4.Tests;

public class ReaderPerformanceTests
{
    [Fact]
    public void CursorHandlesSequentialReadsForwardSkipsAndRewinds()
    {
        using var output = new MemoryStream();
        var track = new GenericTrack(new MetaDataSampleEntry(IsoStream.FromFourCC("test"))
        {
            ReservedSampleEntry = new byte[6], DataReferenceIndex = 1
        }, 1000, 2, IsoStream.FromFourCC(HandlerTypes.Video), HandlerNames.Video);
        var builder = new Mp4Builder(new SingleStreamOutput(output))
        {
            TemporaryStorageFactory = new TemporaryMemoryStorageFactory()
        };
        builder.AddTrack(track);
        int[] durations = [2, 2, 3, 4, 4, 1];
        long[] offsets = [3, 3, -2, 0, 0, -1];
        long[] dts = [0, 2, 4, 7, 11, 15];
        for (int i = 0; i < durations.Length; i++)
            builder.ProcessRawSample(track.TrackID, Enumerable.Repeat((byte)i, i + 1).ToArray(), durations[i], i % 2 == 0, offsets[i]);
        builder.FinalizeMedia();
        output.Position = 0;
        var container = new Container();
        container.Read(new IsoStream(output));
        var reader = new VideoReader();
        reader.Parse(container);
        var context = reader.Tracks[track.TrackID];
        // Group adjacent payloads into multi-sample chunks to exercise in-chunk offsets.
        context.ChunkAddressList = [context.ChunkAddressList[0], context.ChunkAddressList[2], context.ChunkAddressList[5]];
        context.FramesInChunkList = [2, 3, 1];
        // Include rereads, forward skips, and rewinds across chunk/timing boundaries.
        foreach (int index in new[] { 0, 1, 2, 3, 4, 5, 2, 2, 4, 0, 5 })
        {
            reader.Tracks[track.TrackID].SampleIndex = (uint)index;
            var sample = reader.ReadSample(track.TrackID);
            Assert.Equal(dts[index], sample.DTS);
            Assert.Equal(dts[index] + offsets[index], sample.PTS);
            Assert.Equal(durations[index], sample.Duration);
            Assert.Equal(index % 2 == 0, sample.IsRandomAccessPoint);
            Assert.Equal(Enumerable.Repeat((byte)index, index + 1), sample.Data);
        }
        reader.Tracks[track.TrackID].SampleIndex = 0;
        var metadata = reader.ReadSample(track.TrackID, false);
        Assert.Empty(metadata.Data);
        Assert.Equal(0, metadata.DTS);
        Assert.Equal(3, metadata.PTS);
        // A metadata-only read must not disturb subsequent payload addressing.
        Assert.Equal(new byte[] { 1, 1 }, reader.ReadSample(track.TrackID).Data);
        byte[] buffer = new byte[32];
        var originalBuffer = buffer;
        for (int index = 0; index < durations.Length; index++)
        {
            reader.Tracks[track.TrackID].SampleIndex = (uint)index;
            var bufferedSample = reader.ReadSample(track.TrackID, ref buffer, out int bytesRead);
            Assert.Same(originalBuffer, buffer);
            Assert.Same(buffer, bufferedSample.Data);
            Assert.Equal(index + 1, bytesRead);
            Assert.Equal(Enumerable.Repeat((byte)index, index + 1), buffer.Take(bytesRead));
            Assert.Equal(dts[index], bufferedSample.DTS);
        }
        reader.Tracks[track.TrackID].SampleIndex = 6;
        Assert.Null(reader.ReadSample(track.TrackID));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void NaluSlicesShareSampleWhileLegacyResultsOwnTheirData(int prefixLength)
    {
        var track = new H264Track { NalLengthSize = prefixLength };
        var sample = new byte[prefixLength * 2 + 5];
        sample[prefixLength - 1] = 2;
        sample[prefixLength] = 10;
        sample[prefixLength + 1] = 11;
        sample[prefixLength * 2 + 1] = 3;
        sample[prefixLength * 2 + 2] = 20;
        sample[prefixLength * 2 + 3] = 21;
        sample[prefixLength * 2 + 4] = 22;
        var slices = track.ParseSampleSlices(sample).ToArray();
        var legacy = track.ParseSample(sample).ToArray();
        Assert.Equal(2, slices.Length);
        Assert.All(slices, slice =>
        {
            Assert.True(MemoryMarshal.TryGetArray(slice, out var segment));
            Assert.Same(sample, segment.Array);
        });
        Assert.Equal(new byte[] { 10, 11 }, slices[0].ToArray());
        Assert.Equal(new byte[] { 20, 21, 22 }, slices[1].ToArray());
        sample[prefixLength] = 99;
        Assert.Equal(99, slices[0].Span[0]);
        Assert.Equal<byte>(new byte[] { 10, 11 }, legacy[0]);
        Assert.Equal<byte>(new byte[] { 20, 21, 22 }, legacy[1]);
        Assert.Empty(track.ParseSample([]));
    }

    [Fact]
    public void NaluParserRejectsTruncatedPrefixesAndPayloads()
    {
        var track = new H264Track();
        Assert.Throws<InvalidDataException>(() => track.ParseSample([0, 0]));
        Assert.Throws<InvalidDataException>(() => track.ParseSample([0, 0, 0, 2, 1]));
        track.NalLengthSize = 5;
        Assert.Throws<InvalidDataException>(() => track.ParseSample([0, 0, 0, 0, 0]));
    }
}
