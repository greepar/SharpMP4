using SharpISOBMFF;
using SharpMP4.Builders;
using SharpMP4.Readers;
using SharpMP4.Tracks;
using Xunit;

namespace SharpMP4.Tests;

public class SampleTimingTests
{
    [Fact]
    public void BuilderPreservesSampleTimingAndReaderRestoresTimestamps()
    {
        using var output = new MemoryStream();
        var sampleEntry = new MetaDataSampleEntry(IsoStream.FromFourCC("test"))
        {
            ReservedSampleEntry = new byte[6],
            DataReferenceIndex = 1
        };
        var track = new GenericTrack(
            sampleEntry,
            1000,
            2,
            IsoStream.FromFourCC(HandlerTypes.Video),
            HandlerNames.Video);
        var builder = new Mp4Builder(new SingleStreamOutput(output))
        {
            MovieTimescale = 1000,
            TemporaryStorageFactory = new TemporaryMemoryStorageFactory()
        };
        builder.AddTrack(track);

        int[] durations = [2, 2, 3, 3];
        long[] compositionOffsets = [2, 0, -1, -1];
        for (int i = 0; i < durations.Length; i++)
        {
            builder.ProcessRawSample(track.TrackID, [(byte)i], durations[i], true, compositionOffsets[i]);
        }

        builder.FinalizeMedia();
        output.Position = 0;

        var container = new Container();
        container.Read(new IsoStream(output));

        var trak = container.Children.OfType<MovieBox>().Single()
            .Children.OfType<TrackBox>().Single();
        var sampleTable = trak.Children.OfType<MediaBox>().Single()
            .Children.OfType<MediaInformationBox>().Single()
            .Children.OfType<SampleTableBox>().Single();
        var stts = sampleTable.Children.OfType<TimeToSampleBox>().Single();
        var ctts = sampleTable.Children.OfType<CompositionOffsetBox>().Single();
        var elst = trak.Children.OfType<EditBox>().Single()
            .Children.OfType<EditListBox>().Single();

        Assert.Equal([2u, 2u], stts.SampleCount);
        Assert.Equal([2u, 3u], stts.SampleDelta);
        Assert.Equal(1, ctts.Version);
        Assert.Equal([1u, 1u, 2u], ctts.SampleCount);
        Assert.Equal([2, 0, -1], ctts.SampleOffset0);
        Assert.Equal([7ul], elst.EditDuration);
        Assert.Equal([2L], elst.MediaTime);

        var reader = new VideoReader();
        reader.Parse(container);
        var samples = Enumerable.Range(0, durations.Length)
            .Select(_ => reader.ReadSample(track.TrackID))
            .ToArray();

        Assert.Equal([0L, 2L, 4L, 7L], samples.Select(sample => sample!.DTS));
        Assert.Equal([2L, 2L, 3L, 6L], samples.Select(sample => sample!.PTS));
        Assert.Equal(durations, samples.Select(sample => sample!.Duration));
        Assert.Null(reader.ReadSample(track.TrackID));
    }
}
