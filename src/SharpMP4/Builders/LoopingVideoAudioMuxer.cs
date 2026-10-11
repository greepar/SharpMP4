using SharpISOBMFF;
using SharpMP4.Readers;
using SharpMP4.Common;
using SharpMP4.Tracks;
using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace SharpMP4.Builders
{
    /// <summary>
    /// Repeats the video samples of an MP4 until the end of an MP4 audio track.
    /// Both inputs are remuxed without encoding; the first video sample must be a keyframe.
    /// </summary>
    public static class LoopingVideoAudioMuxer
    {
        public static void Mux(Stream videoStream, Stream audioStream, Stream outputStream,
            CancellationToken cancellationToken = default)
        {
            Mux(videoStream, audioStream, outputStream, cancellationToken, null);
        }

        public static void Mux(Stream videoStream, Stream audioStream, Stream outputStream,
            CancellationToken cancellationToken, ITemporaryStorageFactory temporaryStorageFactory)
        {
            if (videoStream == null || !videoStream.CanSeek) throw new ArgumentException("Video input must be seekable.", nameof(videoStream));
            if (audioStream == null || !audioStream.CanSeek) throw new ArgumentException("Audio input must be seekable.", nameof(audioStream));
            if (outputStream == null || !outputStream.CanWrite) throw new ArgumentException("Output must be writable.", nameof(outputStream));

            var videoContainer = ReadContainer(videoStream, temporaryStorageFactory);
            var audioContainer = ReadContainer(audioStream, temporaryStorageFactory);
            var videoReader = CreateReader(videoContainer);
            var audioReader = CreateReader(audioContainer);
            var videoTrack = videoReader.GetTracks().FirstOrDefault(track => track.HandlerType == "vide")
                             ?? throw new InvalidDataException("Video input has no video track.");
            var audioTrack = audioReader.GetTracks().FirstOrDefault(track => track.HandlerType == "soun")
                             ?? throw new InvalidDataException("Audio input has no audio track.");
            var videoMetadata = GetTrackMetadata(videoContainer, "vide", videoTrack);
            var audioMetadata = GetTrackMetadata(audioContainer, "soun", audioTrack);
            if (videoMetadata.Timescale == 0 || audioMetadata.Timescale == 0)
                throw new InvalidDataException("Input track has no timescale.");

            long audioDuration = 0;
            int audioSampleCount = 0;
            MediaSample audioSample;
            while ((audioSample = audioReader.ReadSample(audioTrack.TrackID, false)) != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                audioSampleCount = checked(audioSampleCount + 1);
                audioDuration = checked(audioDuration + SampleDuration(audioSample, audioMetadata.DefaultSampleDuration));
            }
            if (audioDuration == 0) throw new InvalidDataException("Audio track is empty.");

            // Round up so the last video sample also covers the final audio sample.
            var targetVideoDuration = checked((long)Math.Ceiling(
                (decimal)audioDuration * videoMetadata.Timescale / audioMetadata.Timescale));
            audioReader = CreateReader(audioContainer);
            byte[] audioBuffer = null, videoBuffer = null;
            audioSample = audioReader.ReadSample(audioTrack.TrackID, ref audioBuffer, out int audioBytes);
            var firstVideoSample = videoReader.ReadSample(videoTrack.TrackID, ref videoBuffer, out int videoBytes);
            if (firstVideoSample == null || !firstVideoSample.IsRandomAccessPoint)
                throw new InvalidDataException("The video track must begin with a keyframe.");

            using var builder = new Mp4Builder(new SingleStreamOutput(outputStream));
            if (temporaryStorageFactory != null) builder.TemporaryStorageFactory = temporaryStorageFactory;
            var outputVideoTrack = new PassthroughTrack(videoTrack, videoMetadata);
            var outputAudioTrack = new PassthroughTrack(audioTrack, audioMetadata);
            builder.AddTrack(outputVideoTrack);
            builder.AddTrack(outputAudioTrack, audioSampleCount);

            long videoWritten = 0;
            long audioWritten = 0;
            var videoSample = firstVideoSample;
            while (audioSample != null || videoWritten < targetVideoDuration)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (videoWritten < targetVideoDuration
                    && (audioSample == null
                        || (decimal)videoWritten / videoMetadata.Timescale <= (decimal)audioWritten / audioMetadata.Timescale))
                {
                    if (videoSample == null)
                    {
                        videoReader = CreateReader(videoContainer);
                        videoSample = videoReader.ReadSample(videoTrack.TrackID, ref videoBuffer, out videoBytes);
                        if (videoSample == null || !videoSample.IsRandomAccessPoint)
                            throw new InvalidDataException("The video track cannot be looped from a keyframe.");
                    }

                    var duration = (int)Math.Min(
                        SampleDuration(videoSample, videoMetadata.DefaultSampleDuration),
                        targetVideoDuration - videoWritten);
                    builder.ProcessRawSample(outputVideoTrack.TrackID, videoSample.Data, 0, videoBytes, duration,
                        videoSample.IsRandomAccessPoint, videoSample.PTS - videoSample.DTS);
                    videoWritten += duration;
                    videoSample = videoReader.ReadSample(videoTrack.TrackID, ref videoBuffer, out videoBytes);
                }
                else if (audioSample != null)
                {
                    var duration = SampleDuration(audioSample, audioMetadata.DefaultSampleDuration);
                    builder.ProcessRawSample(outputAudioTrack.TrackID, audioSample.Data, 0, audioBytes, duration,
                        audioSample.IsRandomAccessPoint, audioSample.PTS - audioSample.DTS);
                    audioWritten += duration;
                    audioSample = audioReader.ReadSample(audioTrack.TrackID, ref audioBuffer, out audioBytes);
                }
            }

            builder.FinalizeMedia();
        }

        private static int SampleDuration(MediaSample sample, int fallback)
        {
            var duration = sample.Duration > 0 ? sample.Duration : fallback;
            if (duration <= 0) throw new InvalidDataException("Input sample has no duration.");
            return duration;
        }

        private static Container ReadContainer(Stream stream, ITemporaryStorageFactory temporaryStorageFactory)
        {
            var container = new Container();
            container.Read(new IsoStream(stream, temporaryStorageFactory));
            return container;
        }

        private static VideoReader CreateReader(Container container)
        {
            var reader = new VideoReader();
            reader.Parse(container);
            return reader;
        }

        private static TrackMetadata GetTrackMetadata(Container container, string handlerType, ITrack track)
        {
            var trackBox = container.Children.OfType<MovieBox>()
                .SelectMany(movie => movie.Children.OfType<TrackBox>())
                .First(box => IsoStream.ToFourCC(box.Children.OfType<MediaBox>().Single()
                    .Children.OfType<HandlerBox>().Single().HandlerType) == handlerType);
            var media = trackBox.Children.OfType<MediaBox>().Single();
            var table = media.Children.OfType<MediaInformationBox>().Single()
                .Children.OfType<SampleTableBox>().Single();
            var sampleEntry = table.Children.OfType<SampleDescriptionBox>().Single().Children.Single();
            var defaultDuration = table.Children.OfType<TimeToSampleBox>().Single()
                .SampleDelta.FirstOrDefault(delta => delta > 0);
            return new TrackMetadata(sampleEntry,
                media.Children.OfType<MediaHeaderBox>().Single().Timescale,
                defaultDuration > 0 ? checked((int)defaultDuration) : track.DefaultSampleDuration);
        }

        private sealed class TrackMetadata
        {
            public Box SampleEntry { get; }
            public uint Timescale { get; }
            public int DefaultSampleDuration { get; }

            public TrackMetadata(Box sampleEntry, uint timescale, int defaultSampleDuration)
            {
                SampleEntry = sampleEntry;
                Timescale = timescale;
                DefaultSampleDuration = defaultSampleDuration;
            }
        }

        private sealed class PassthroughTrack : TrackBase
        {
            private readonly ITrack source;
            private readonly Box sampleEntry;

            public PassthroughTrack(ITrack source, TrackMetadata metadata)
            {
                this.source = source;
                sampleEntry = metadata.SampleEntry;
                Language = source.Language;
                Timescale = metadata.Timescale;
                DefaultSampleDuration = metadata.DefaultSampleDuration;
                CompatibleBrand = source.CompatibleBrand;
                DefaultSampleFlags = source.DefaultSampleFlags;
            }

            public override string HandlerName => source.HandlerName;
            public override string HandlerType => source.HandlerType;
            public override string Language { get; set; }
            public override Box CreateSampleEntryBox() => sampleEntry;
            public override void FillTkhdBox(TrackHeaderBox tkhd) => source.FillTkhdBox(tkhd);
            public override ITrack Clone() => new PassthroughTrack(source,
                new TrackMetadata(sampleEntry, Timescale, DefaultSampleDuration));
        }
    }
}
