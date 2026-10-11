using SharpISOBMFF;
using SharpMP4.Audio;
using SharpMP4.Builders;
using SharpMP4.Tracks;
using Xunit;

namespace SharpMP4.Tests;

public class ManagedAudioTests
{
    [Fact]
    public void PcmMixSupportsInPlaceOutputAndScalarTail()
    {
        float[] original = Enumerable.Range(0, 37).Select(i => i / 40f).ToArray();
        float[] background = Enumerable.Range(0, 37).Select(i => -i / 80f).ToArray();
        var expected = original.Zip(background, (a, b) => (a + b) / 2).ToArray();
        PcmMixer.Mix(original, background, original);
        Assert.Equal(expected, original);
    }

    [Fact]
    public void PresentationWindowKeepsPrimingDataButTrimsMoviePresentation()
    {
        using var output = new MemoryStream();
        using (var builder = new Mp4Builder(new SingleStreamOutput(output))
        { TemporaryStorageFactory = new TemporaryMemoryStorageFactory() })
        {
            var audio = new AACTrack(2, 48000, 16);
            builder.AddTrack(audio);
            for (int i = 0; i < 4; i++) builder.ProcessRawSample(audio.TrackID, new byte[] { 0xE0 }, 0, 1, 1024, true, 0);
            Assert.Throws<ArgumentOutOfRangeException>(() => builder.SetPresentationWindow(audio.TrackID, 1024, 4096));
            builder.SetPresentationWindow(audio.TrackID, 1024, 3072);
            builder.FinalizeMedia();
        }
        output.Position = 0;
        var container = new Container(); container.Read(new IsoStream(output));
        var track = container.Children.OfType<MovieBox>().Single().Children.OfType<TrackBox>().Single();
        var edit = track.Children.OfType<EditBox>().Single().Children.OfType<EditListBox>().Single();
        Assert.Equal(1024L, edit.MediaTime.Single());
        Assert.Equal(64UL, edit.EditDuration.Single());
        var media = track.Children.OfType<MediaBox>().Single();
        Assert.Equal(4096UL, media.Children.OfType<MediaHeaderBox>().Single().Duration);
    }
}
