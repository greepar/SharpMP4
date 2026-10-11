using SharpISOBMFF;
using SharpMP4.Audio;
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
    /// Repeats an MP4 video a fixed number of times, optionally with an MP4 audio track or raw MP3 stream.
    /// The remux variants keep compressed samples; the mixing variant encodes audio only.
    /// The first video sample must be a keyframe.
    /// </summary>
    public static class LoopingVideoAudioMuxer
    {
        public static void Mux(Stream videoStream, Stream audioStream, Stream outputStream,
            CancellationToken cancellationToken = default)
            => Mux(videoStream, audioStream, outputStream, cancellationToken, null, 3);

        public static void Mux(Stream videoStream, Stream audioStream, Stream outputStream,
            CancellationToken cancellationToken, ITemporaryStorageFactory temporaryStorageFactory)
            => Mux(videoStream, audioStream, outputStream, cancellationToken, temporaryStorageFactory, 3);

        public static void Mux(Stream videoStream, Stream audioStream, Stream outputStream,
            int loopCount, CancellationToken cancellationToken = default,
            ITemporaryStorageFactory temporaryStorageFactory = null)
            => Mux(videoStream, audioStream, outputStream, cancellationToken, temporaryStorageFactory, loopCount);

        public static void Mux(Stream videoStream, Stream audioStream, Stream outputStream,
            CancellationToken cancellationToken, ITemporaryStorageFactory temporaryStorageFactory, int loopCount)
            => MuxCore(videoStream, audioStream, outputStream, cancellationToken, temporaryStorageFactory, loopCount, false);

        /// <summary>Preserves and loops embedded audio when present; otherwise uses the supplied background audio.</summary>
        public static void MuxPreservingEmbeddedAudio(Stream videoStream, Stream audioStream, Stream outputStream,
            CancellationToken cancellationToken = default, ITemporaryStorageFactory temporaryStorageFactory = null, int loopCount = 3)
            => MuxCore(videoStream, audioStream, outputStream, cancellationToken, temporaryStorageFactory, loopCount, true);

        /// <summary>Mixes embedded AAC with external MP3/AAC using managed streaming DSP and AAC encoding.
        /// Embedded audio plays once; the picture loops for at least loopCount passes and until background music ends.
        /// Video samples are copied unchanged. Without embedded audio, background audio is remuxed unchanged.</summary>
        public static void MuxMixingEmbeddedAudio(Stream videoStream, Stream audioStream, Stream outputStream,
            CancellationToken cancellationToken = default, ITemporaryStorageFactory temporaryStorageFactory = null, int loopCount = 3)
            => MuxCore(videoStream, audioStream, outputStream, cancellationToken, temporaryStorageFactory, loopCount, true, true);

        private static void MuxCore(Stream videoStream, Stream audioStream, Stream outputStream,
            CancellationToken cancellationToken, ITemporaryStorageFactory temporaryStorageFactory, int loopCount, bool preferEmbeddedAudio, bool mixEmbeddedAudio = false)
        {
            if (videoStream == null || !videoStream.CanSeek) throw new ArgumentException("Video input must be seekable.", nameof(videoStream));
            if (audioStream != null && !audioStream.CanSeek) throw new ArgumentException("Audio input must be seekable.", nameof(audioStream));
            if (outputStream == null || !outputStream.CanWrite) throw new ArgumentException("Output must be writable.", nameof(outputStream));

            if (loopCount < 1) throw new ArgumentOutOfRangeException(nameof(loopCount));
            cancellationToken.ThrowIfCancellationRequested();
            var videoContainer = ReadContainer(videoStream, temporaryStorageFactory);
            var videoReader = CreateReader(videoContainer);
            var embeddedAudio = preferEmbeddedAudio
                ? videoReader.GetTracks().FirstOrDefault(track => track.HandlerType == "soun") : null;
            using var mixedReader = mixEmbeddedAudio && embeddedAudio != null && audioStream != null
                ? new MixedAudioReader(videoContainer, audioStream, temporaryStorageFactory) : null;
            var mp3Reader = embeddedAudio == null && audioStream != null && MP3Reader.IsMP3(audioStream) ? new MP3Reader(audioStream) : null;
            var audioContainer = mixedReader != null ? null : embeddedAudio != null ? videoContainer
                : audioStream == null || mp3Reader != null ? null : ReadContainer(audioStream, temporaryStorageFactory);
            var audioReader = audioContainer == null ? null : CreateReader(audioContainer);
            var videoTrack = videoReader.GetTracks().FirstOrDefault(track => track.HandlerType == "vide")
                             ?? throw new InvalidDataException("Video input has no video track.");
            ITrack audioTrack = mixedReader != null ? mixedReader.Track : mp3Reader != null ? mp3Reader.Track
                : audioReader?.GetTracks().FirstOrDefault(track => track.HandlerType == "soun");
            if (audioStream != null && audioTrack == null) throw new InvalidDataException("Audio input has no audio track.");
            var videoMetadata = GetTrackMetadata(videoContainer, "vide", videoTrack);
            var audioMetadata = audioTrack == null ? null : mixedReader != null || mp3Reader != null
                ? new TrackMetadata(null, audioTrack.Timescale, audioTrack.DefaultSampleDuration)
                : GetTrackMetadata(audioContainer, "soun", audioTrack);
            if (videoMetadata.Timescale == 0 || audioMetadata?.Timescale == 0)
                throw new InvalidDataException("Input track has no timescale.");

            long videoDuration = 0;
            MediaSample measuredSample;
            while ((measuredSample = videoReader.ReadSample(videoTrack.TrackID, false)) != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                videoDuration = checked(videoDuration + SampleDuration(measuredSample, videoMetadata.DefaultSampleDuration));
            }
            if (videoDuration == 0) throw new InvalidDataException("Video track is empty.");
            var targetVideoDuration = checked(videoDuration * loopCount);
            if (mixedReader != null)
                targetVideoDuration = Math.Max(targetVideoDuration, checked((long)Math.Ceiling(
                    mixedReader.BackgroundDurationSeconds * videoMetadata.Timescale)));
            var targetAudioDuration = audioMetadata == null ? 0 : checked((long)Math.Ceiling(
                (decimal)targetVideoDuration * audioMetadata.Timescale / videoMetadata.Timescale));
            mixedReader?.SetVideoDuration(videoDuration, videoMetadata.Timescale, targetAudioDuration);
            // Keep the encoder's first overlap packet and skip its 1024-sample delay with an edit list.
            var presentedAudioDuration = targetAudioDuration;
            if (mixedReader != null) targetAudioDuration = checked(targetAudioDuration + ManagedAacEncoder.FrameSamples);
            videoReader = CreateReader(videoContainer);
            byte[] audioBuffer = null, videoBuffer = null;
            int audioBytes = 0;
            var audioSample = mixedReader != null ? mixedReader.ReadSample(ref audioBuffer, out audioBytes, cancellationToken) : mp3Reader != null ? mp3Reader.ReadSample(ref audioBuffer, out audioBytes, cancellationToken)
                : audioReader?.ReadSample(audioTrack.TrackID, ref audioBuffer, out audioBytes);
            if (audioTrack != null && audioSample == null) throw new InvalidDataException("Audio track is empty.");
            var firstVideoSample = videoReader.ReadSample(videoTrack.TrackID, ref videoBuffer, out int videoBytes);
            if (firstVideoSample == null || !firstVideoSample.IsRandomAccessPoint)
                throw new InvalidDataException("The video track must begin with a keyframe.");

            using var builder = new Mp4Builder(new SingleStreamOutput(outputStream));
            if (temporaryStorageFactory != null) builder.TemporaryStorageFactory = temporaryStorageFactory;
            var outputVideoTrack = new PassthroughTrack(videoTrack, videoMetadata);
            ITrack outputAudioTrack = audioTrack == null ? null : mixedReader != null || mp3Reader != null ? audioTrack
                : new PassthroughTrack(audioTrack, audioMetadata);
            builder.AddTrack(outputVideoTrack);
            if (outputAudioTrack != null) builder.AddTrack(outputAudioTrack);

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
                    var duration = (int)Math.Min(SampleDuration(audioSample, audioMetadata.DefaultSampleDuration),
                        targetAudioDuration - audioWritten);
                    builder.ProcessRawSample(outputAudioTrack.TrackID, audioSample.Data, 0, audioBytes, duration,
                        audioSample.IsRandomAccessPoint, audioSample.PTS - audioSample.DTS);
                    audioWritten += duration;
                    audioSample = audioWritten >= targetAudioDuration ? null
                        : mixedReader != null ? mixedReader.ReadSample(ref audioBuffer, out audioBytes, cancellationToken)
                        : mp3Reader != null ? mp3Reader.ReadSample(ref audioBuffer, out audioBytes, cancellationToken)
                        : audioReader.ReadSample(audioTrack.TrackID, ref audioBuffer, out audioBytes);
                    if (audioSample == null && embeddedAudio != null && mixedReader == null && audioWritten < targetAudioDuration)
                    {
                        audioReader = CreateReader(audioContainer);
                        audioSample = audioReader.ReadSample(audioTrack.TrackID, ref audioBuffer, out audioBytes);
                        if (audioSample == null) throw new InvalidDataException("Embedded audio track cannot be looped.");
                    }
                }
            }

            if (mixedReader != null)
                builder.SetPresentationWindow(outputAudioTrack.TrackID, ManagedAacEncoder.FrameSamples, presentedAudioDuration);
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
