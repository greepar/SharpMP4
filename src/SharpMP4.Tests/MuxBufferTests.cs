using SharpISOBMFF;
using SharpMP4.Builders;
using SharpMP4.Readers;
using SharpMP4.Tracks;
using Xunit;

namespace SharpMP4.Tests;

public class MuxBufferTests
{
    [Fact]
    public void LoopingMuxPreservesVariableSizePayloadsTimingAndFinalClipping()
    {
        using var video = CreateInput(HandlerTypes.Video, [2, 3, 2], [5, 2, 4], [1, -1, 0]);
        using var audio = CreateInput("soun", [7, 8, 5], [1, 4, 2], [0, 0, 0]);
        using var output = new MemoryStream();
        LoopingVideoAudioMuxer.Mux(video, audio, output);
        output.Position = 0;
        var container = new Container();
        container.Read(new IsoStream(output));
        var reader = new VideoReader();
        reader.Parse(container);
        var videoTrack = reader.GetTracks().Single(t => t.HandlerType == HandlerTypes.Video);
        var audioTrack = reader.GetTracks().Single(t => t.HandlerType == "soun");
        int[] expectedDurations = [2, 3, 2, 2, 3, 2, 2, 3, 1];
        long dts = 0;
        for (int i = 0; i < expectedDurations.Length; i++)
        {
            var sample = reader.ReadSample(videoTrack.TrackID);
            Assert.NotNull(sample);
            Assert.Equal(dts, sample.DTS);
            Assert.Equal(expectedDurations[i], sample.Duration);
            Assert.Equal(new long[] { 1, -1, 0 }[i % 3], sample.PTS - sample.DTS);
            Assert.Equal(Enumerable.Repeat((byte)(i % 3 + 1), new[] { 5, 2, 4 }[i % 3]), sample.Data);
            dts += sample.Duration;
        }
        Assert.Equal(20, dts);
        Assert.Null(reader.ReadSample(videoTrack.TrackID));
        for (int i = 0; i < 3; i++)
        {
            var sample = reader.ReadSample(audioTrack.TrackID);
            Assert.Equal(new[] { 7, 8, 5 }[i], sample.Duration);
            Assert.Equal(Enumerable.Repeat((byte)(i + 1), new[] { 1, 4, 2 }[i]), sample.Data);
        }
        Assert.Null(reader.ReadSample(audioTrack.TrackID));
    }

    private static MemoryStream CreateInput(string handler, int[] durations, int[] sizes, long[] offsets)
    {
        var output = new MemoryStream();
        var track = new GenericTrack(new MetaDataSampleEntry(IsoStream.FromFourCC("test"))
        {
            ReservedSampleEntry = new byte[6], DataReferenceIndex = 1
        }, 1000, 2, IsoStream.FromFourCC(handler), handler);
        var builder = new Mp4Builder(new SingleStreamOutput(output))
        {
            TemporaryStorageFactory = new TemporaryMemoryStorageFactory()
        };
        builder.AddTrack(track, durations.Length);
        for (int i = 0; i < durations.Length; i++)
        {
            var buffer = Enumerable.Repeat((byte)(i + 1), sizes[i] + 8).ToArray();
            // Extra capacity must never become part of the media sample.
            builder.ProcessRawSample(track.TrackID, buffer, 2, sizes[i], durations[i], true, offsets[i]);
        }
        builder.FinalizeMedia();
        output.Position = 0;
        return output;
    }
}
