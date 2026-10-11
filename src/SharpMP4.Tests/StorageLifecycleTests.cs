using SharpISOBMFF;
using SharpMP4.Builders;
using SharpMP4.Readers;
using SharpMP4.Tracks;
using SharpMP4.Common;
using Xunit;

namespace SharpMP4.Tests;

public class StorageLifecycleTests
{
    [Fact]
    public void FragmentStorageIsCreatedPerFragmentAndDisposedAfterWriting()
    {
        using var output = new MemoryStream();
        var factory = new CountingFactory();
        using var builder = new FragmentedMp4Builder(new SingleStreamOutput(output), 4)
        {
            TemporaryStorageFactory = factory
        };
        var track = CreateTrack();
        builder.AddTrack(track);
        for (int i = 0; i < 6; i++)
            builder.ProcessRawSample(track.TrackID, [(byte)i, (byte)i], 2, true, factory);
        Assert.Equal(3, factory.Created.Count);
        Assert.Equal(2, factory.Created.Count(storage => storage.IsDisposed));
        builder.FinalizeMedia();
        Assert.All(factory.Created, storage => Assert.True(storage.IsDisposed));
        output.Position = 0;
        var container = new Container();
        container.Read(new IsoStream(output));
        var reader = new VideoReader();
        reader.Parse(container);
        byte[] buffer = new byte[10];
        for (int i = 0; i < 6; i++)
        {
            var sample = reader.ReadSample(track.TrackID, ref buffer, out int count);
            Assert.Equal(2, count);
            Assert.Equal<byte>([(byte)i, (byte)i], buffer.Take(count));
            Assert.Equal(i * 2, sample.DTS);
        }
        Assert.Null(reader.ReadSample(track.TrackID));
    }

    [Fact]
    public void NonFragmentedBuilderDisposesOwnedStorageButLeavesCallerStorageOpen()
    {
        using var output = new MemoryStream();
        var factory = new CountingFactory();
        using var builder = new Mp4Builder(new SingleStreamOutput(output)) { TemporaryStorageFactory = factory };
        var track = CreateTrack();
        builder.AddTrack(track);
        builder.ProcessRawSample(track.TrackID, [1], 2, true);
        builder.FinalizeMedia();
        Assert.True(Assert.Single(factory.Created).IsDisposed);
        using var external = new CountingStorage();
        using var secondOutput = new MemoryStream();
        using var second = new Mp4Builder(new SingleStreamOutput(secondOutput)) { Storage = external };
        var secondTrack = CreateTrack();
        second.AddTrack(secondTrack);
        second.ProcessRawSample(secondTrack.TrackID, [2], 2, true);
        second.FinalizeMedia();
        Assert.False(external.IsDisposed);
    }

    [Fact]
    public void NonSeekableReaderDisposesItsTemporaryStorageWithTheInput()
    {
        var input = new NonSeekStream([1, 2, 3, 4]);
        var factory = new CountingFactory();
        var owner = new IsoStream(input, factory);
        owner.ReadUInt8ArrayTillEnd(0, 32, out StreamMarker marker, "");
        var temporary = Assert.Single(factory.Created);
        Assert.False(temporary.IsDisposed);
        marker.Stream.SeekFromBeginning(marker.Position);
        marker.Stream.ReadBytes(4, out byte[] bytes);
        Assert.Equal<byte>([1, 2, 3, 4], bytes);
        owner.Dispose();
        Assert.True(temporary.IsDisposed);
        Assert.False(input.CanRead);
        // Markers share their owner's lifetime; disposing either again is safe.
        owner.Dispose();
        marker.Dispose();
    }

    [Fact]
    public void ReaderClosesTheInputEvenIfTemporaryStorageDisposalThrows()
    {
        var input = new NonSeekStream([1, 2, 3, 4]);
        var owner = new IsoStream(input, new ThrowingFactory());
        owner.ReadUInt8ArrayTillEnd(0, 32, out StreamMarker marker, "");
        Assert.Throws<IOException>(() => owner.Dispose());
        Assert.False(input.CanRead);
        owner.Dispose();
        marker.Dispose();
    }

    private sealed class NonSeekStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class ThrowingFactory : ITemporaryStorageFactory
    {
        public IStorage Create(IMp4Logger? logger = null) => new ThrowingStorage();
    }

    private sealed class ThrowingStorage : TemporaryMemory
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) throw new IOException("Injected disposal failure");
        }
    }

    private static GenericTrack CreateTrack() => new(new MetaDataSampleEntry(IsoStream.FromFourCC("test"))
    {
        ReservedSampleEntry = new byte[6], DataReferenceIndex = 1
    }, 1000, 2, IsoStream.FromFourCC(HandlerTypes.Video), HandlerNames.Video);

    private sealed class CountingFactory : ITemporaryStorageFactory
    {
        public List<CountingStorage> Created { get; } = [];
        public IStorage Create(IMp4Logger? logger = null)
        {
            var storage = new CountingStorage();
            Created.Add(storage);
            return storage;
        }
    }

    private sealed class CountingStorage : TemporaryMemory
    {
        public bool IsDisposed { get; private set; }
        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
