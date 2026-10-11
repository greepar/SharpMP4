using SharpISOBMFF;
using Xunit;
using SharpMP4.Common;

namespace SharpMP4.Tests;

public class EndianIoTests
{
    [Fact]
    public void ScalarIoUsesBigEndianAndHandlesShortReads()
    {
        using var storage = new MemoryStream();
        using var writer = new IsoStream(storage);
        writer.WriteUInt32(0x89abcdefu, "");
        writer.WriteUInt64(0xfedcba9876543210ul, "");
        Assert.Equal<byte>([0x89, 0xab, 0xcd, 0xef, 0xfe, 0xdc, 0xba, 0x98, 0x76, 0x54, 0x32, 0x10], storage.ToArray());
        using var reader = new IsoStream(new ShortReadStream(storage.ToArray()));
        reader.ReadUInt32(0, ulong.MaxValue, out uint small, "");
        reader.ReadUInt64(0, ulong.MaxValue, out ulong large, "");
        Assert.Equal(0x89abcdefu, small);
        Assert.Equal(0xfedcba9876543210ul, large);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void TruncatedScalarThrowsIsoEndOfStream(int length)
    {
        using var reader = new IsoStream(new ShortReadStream(new byte[length]));
        var exception = Assert.Throws<IsoEndOfStreamException>(() => reader.ReadUInt32(0, ulong.MaxValue, out uint _, ""));
        Assert.Equal(length, exception.PaddingBytes?.Length ?? 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SpanIoWorksWithNativeAndArrayOnlyStorage(bool arrayOnly)
    {
        using var stream = new MemoryStream();
        IStorage storage = arrayOnly ? new ArrayOnlyStorage(stream) : new StreamWrapper(stream);
        using var io = new IsoStream(storage);
        byte[] payload = Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray();
        io.WriteUInt32(0x89abcdefu, "");
        io.WriteUInt64(0xfedcba9876543210ul, "");
        io.WriteBytes(payload.AsSpan());
        stream.Position = 0;
        io.ReadUInt32(0, ulong.MaxValue, out uint small, "");
        io.ReadUInt64(0, ulong.MaxValue, out ulong large, "");
        Assert.Equal(0x89abcdefu, small);
        Assert.Equal(0xfedcba9876543210ul, large);
        byte[] result = new byte[payload.Length];
        io.ReadBytes(result.AsSpan());
        Assert.Equal<byte>(payload, result);
    }

    private sealed class ArrayOnlyStorage(Stream stream) : IStorage
    {
        public IMp4Logger Logger { get; set; } = new DefaultMp4Logger();
        public bool CanStreamSeek() => stream.CanSeek;
        public void Flush() => stream.Flush();
        public long GetLength() => stream.Length;
        public long GetPosition() => stream.Position;
        public long SeekFromCurrent(long offset) => stream.Seek(offset, SeekOrigin.Current);
        public long SeekFromEnd(long offset) => stream.Seek(offset, SeekOrigin.End);
        public long SeekFromBeginning(long offset) => stream.Seek(offset, SeekOrigin.Begin);
        public void ReadExactly(byte[] data, int offset, int length) => stream.ReadExactly(data, offset, length);
        public void Write(byte[] buffer, int offset, int length) => stream.Write(buffer, offset, length);
        public int ReadByte() => stream.ReadByte();
        public void WriteByte(byte value) => stream.WriteByte(value);
        public int Read(byte[] buffer, int offset, int length) => stream.Read(buffer, offset, length);
        public void Dispose() => stream.Dispose();
    }

    private sealed class ShortReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override int Read(Span<byte> buffer) => base.Read(buffer.Slice(0, Math.Min(buffer.Length, 1)));
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));
    }
}
