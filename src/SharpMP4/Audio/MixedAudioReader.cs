using SharpISOBMFF;
using SharpMP4.Common;
using SharpMP4.Readers;
using SharpMP4.Tracks;
using SharpJaad.AAC;
using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace SharpMP4.Audio
{
    internal sealed class MixedAudioReader : IDisposable
    {
        public AACTrack Track { get; } = new AACTrack(2, 48000, 16);
        private readonly AacPcmSource original;
        private readonly PcmSource background;
        private Resampler originalResampler;
        private readonly Resampler backgroundResampler;
        private readonly ManagedAacEncoder encoder = new ManagedAacEncoder();
        private readonly float[] originalPcm = new float[2048], musicPcm = new float[2048];
        private long position, loopSamples, totalSamples;
        private readonly MediaSample sample = new MediaSample(0, 0, 1024, null);

        public MixedAudioReader(Container video, Stream music, ITemporaryStorageFactory temporaryStorageFactory)
        {
            original = new AacPcmSource(video);
            originalResampler = new Resampler(original);
            try
            {
                if (MP3Reader.IsMP3(music)) background = new Mp3PcmSource(music);
                else
                {
                    var container = new Container();
                    container.Read(new IsoStream(music, temporaryStorageFactory));
                    background = new AacPcmSource(container);
                }
                backgroundResampler = new Resampler(background);
            }
            catch { original.Dispose(); throw; }
        }

        public void SetVideoDuration(long duration, uint timescale, long totalSamples)
        {
            loopSamples = checked((long)Math.Ceiling((decimal)duration * 48000 / timescale));
            this.totalSamples = totalSamples;
        }

        public MediaSample ReadSample(ref byte[] output, out int bytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (loopSamples <= 0) throw new InvalidOperationException("Video duration must be set before reading mixed audio.");
            for (int i = 0; i < 1024; i++)
            {
                if (position >= totalSamples)
                {
                    originalPcm[2 * i] = originalPcm[2 * i + 1] = musicPcm[2 * i] = musicPcm[2 * i + 1] = 0;
                    position++;
                    continue;
                }
                if (position != 0 && position % loopSamples == 0)
                {
                    original.Reset();
                    originalResampler.Reset();
                }
                originalResampler.ReadFrame(out originalPcm[2 * i], out originalPcm[2 * i + 1]);
                backgroundResampler.ReadFrame(out musicPcm[2 * i], out musicPcm[2 * i + 1]);
                position++;
            }
            PcmMixer.Mix(originalPcm, musicPcm, originalPcm);
            if (output == null || output.Length < 1536) output = new byte[1536];
            bytes = encoder.Encode(originalPcm, output);
            sample.Data = output; sample.Duration = 1024; sample.IsRandomAccessPoint = true;
            sample.DTS = position - 1024; sample.PTS = sample.DTS;
            return sample;
        }
        public void Dispose() { original.Dispose(); background.Dispose(); }

        private abstract class PcmSource : IDisposable
        {
            public abstract int SampleRate { get; }
            public abstract bool ReadFrame(out float left, out float right);
            public virtual void Dispose() { }
        }
        private sealed class Mp3PcmSource : PcmSource
        {
            private readonly NLayer.MpegFile decoder;
            private readonly float[] buffer = new float[4096];
            private int count, cursor;
            public override int SampleRate => decoder.SampleRate;
            public Mp3PcmSource(Stream stream)
            {
                decoder = new NLayer.MpegFile(stream);
                if (decoder.Channels < 1 || decoder.Channels > 2) throw new NotSupportedException("Only mono/stereo MP3 is supported.");
            }
            public override bool ReadFrame(out float left, out float right)
            {
                if (cursor >= count) { count = decoder.ReadSamples(buffer, 0, buffer.Length); cursor = 0; }
                if (count == 0) { left = right = 0; return false; }
                left = buffer[cursor++]; right = decoder.Channels == 1 ? left : buffer[cursor++]; return true;
            }
            public override void Dispose() => decoder.Dispose();
        }
        private sealed class AacPcmSource : PcmSource
        {
            private readonly Container container;
            private readonly AACTrack track;
            private readonly byte[] configuration;
            private VideoReader reader;
            private Decoder decoder;
            private readonly SampleBuffer pcm = new SampleBuffer();
            private byte[] encoded;
            private int cursor, length;
            private readonly long primingSamples;
            public override int SampleRate => pcm.SampleRate > 0 ? pcm.SampleRate : (int)track.SamplingRate;
            public AacPcmSource(Container container)
            {
                this.container = container;
                reader = CreateReader(container);
                track = reader.GetTracks().FirstOrDefault(t => t.HandlerType == "soun") as AACTrack
                    ?? throw new NotSupportedException("Managed mixing currently supports AAC audio in MP4.");
                if (track.AudioSpecificConfig == null) throw new InvalidDataException("AAC input has no decoder configuration.");
                using var stream = new MemoryStream();
                track.AudioSpecificConfig.Write(new IsoStream(stream));
                configuration = stream.ToArray();
                decoder = new Decoder(configuration);
                pcm.SetBigEndian(false);
                // Decode the first frame so HE-AAC's actual output rate/channels are known.
                Fill();
                var trackBox = reader.Track.First(t => t.Children.OfType<TrackHeaderBox>().Single().TrackID == track.TrackID);
                var edit = trackBox.Children.OfType<EditBox>().SelectMany(e => e.Children.OfType<EditListBox>()).SingleOrDefault();
                if (edit != null)
                {
                    if (edit.EntryCount != 1 || edit.MediaTime[0] < 0) throw new NotSupportedException("Complex AAC edit lists are not supported for managed mixing.");
                    primingSamples = checked((long)((decimal)edit.MediaTime[0] * SampleRate / track.Timescale));
                    if (primingSamples > SampleRate * 10L) throw new InvalidDataException("AAC priming interval is too large.");
                    SkipPriming();
                }
            }
            private void SkipPriming()
            {
                for (long i = 0; i < primingSamples; i++)
                    if (!ReadFrame(out _, out _)) throw new InvalidDataException("AAC priming interval exceeds the audio track.");
            }
            private bool Fill()
            {
                var sample = reader.ReadSample(track.TrackID, ref encoded, out int bytes);
                if (sample == null) { length = cursor = 0; return false; }
                // SharpJaad stops at ID_END, but its bitstream must not see stale bytes from a previous packet.
                Array.Clear(encoded, bytes, encoded.Length - bytes);
                decoder.DecodeFrame(encoded, pcm);
                if (pcm.Channels < 1 || pcm.Channels > 2 || pcm.BitsPerSample != 16)
                    throw new NotSupportedException("Only mono/stereo 16-bit decoded AAC is supported.");
                length = pcm.Data.Length; cursor = 0;
                if (length == 0) throw new InvalidDataException("AAC decoder produced an empty frame.");
                return true;
            }
            public override bool ReadFrame(out float left, out float right)
            {
                if (cursor >= length && !Fill()) { left = right = 0; return false; }
                left = BinaryPrimitives.ReadInt16LittleEndian(pcm.Data.AsSpan(cursor, 2)) / 32768f; cursor += 2;
                right = left;
                if (pcm.Channels == 2) { right = BinaryPrimitives.ReadInt16LittleEndian(pcm.Data.AsSpan(cursor, 2)) / 32768f; cursor += 2; }
                return true;
            }
            public void Reset() { reader = CreateReader(container); decoder = new Decoder(configuration); cursor = length = 0; SkipPriming(); }
            private static VideoReader CreateReader(Container container)
            { var reader = new VideoReader(); reader.Parse(container); return reader; }
        }

        // Windowed-sinc polyphase resampler: 32 taps, 1024 phases, fixed-size PCM ring.
        // No complete-track buffers; EOF is padded with silence, and reset keeps the coefficient table.
        private sealed class Resampler
        {
            private readonly PcmSource source;
            private readonly float[] coefficients = new float[32 * 1024], ring = new float[64];
            private readonly int rate;
            private long outputPosition, loaded = -1;
            private bool ended;
            public Resampler(PcmSource source)
            {
                this.source = source; rate = source.SampleRate;
                if (rate < 8000 || rate > 96000) throw new NotSupportedException("Unsupported PCM sample rate.");
                double cutoff = Math.Min(1, 48000.0 / rate) * .95;
                for (int phase = 0; phase < 1024; phase++)
                {
                    double sum = 0;
                    for (int tap = 0; tap < 32; tap++)
                    {
                        double x = tap - 15 - phase / 1024.0;
                        double value = Math.Abs(x) < 1e-12 ? cutoff : Math.Sin(Math.PI * cutoff * x) / (Math.PI * x);
                        value *= .5 + .5 * Math.Cos(Math.PI * x / 16);
                        coefficients[phase * 32 + tap] = (float)value; sum += value;
                    }
                    for (int tap = 0; tap < 32; tap++) coefficients[phase * 32 + tap] /= (float)sum;
                }
            }
            public void Reset() { outputPosition = 0; loaded = -1; ended = false; Array.Clear(ring); }
            public void ReadFrame(out float left, out float right)
            {
                long numerator = checked(outputPosition * rate);
                long center = numerator / 48000;
                int phase = (int)((numerator % 48000) * 1024 / 48000);
                while (loaded < center + 16)
                {
                    float l = 0, r = 0;
                    if (!ended && !source.ReadFrame(out l, out r)) ended = true;
                    loaded++;
                    int slot = (int)(loaded % 32) * 2;
                    ring[slot] = l; ring[slot + 1] = r;
                }
                left = right = 0;
                for (int tap = 0; tap < 32; tap++)
                {
                    long index = center - 15 + tap;
                    if (index < 0) continue;
                    int slot = (int)(index % 32) * 2;
                    float weight = coefficients[phase * 32 + tap];
                    left += ring[slot] * weight; right += ring[slot + 1] * weight;
                }
                outputPosition++;
            }
        }
    }

    /// <summary>Allocation-free, SIMD PCM mixing. Equal gain gives both inputs headroom;
    /// mono duplication and sample-rate conversion happen before mixing.</summary>
    public static class PcmMixer
    {
        public static void Mix(ReadOnlySpan<float> original, ReadOnlySpan<float> background, Span<float> output)
        {
            if (original.Length != background.Length || output.Length < original.Length)
                throw new ArgumentException("PCM buffers must have equal sample counts.");
            int i = 0, width = Vector<float>.Count;
            var gain = new Vector<float>(.5f);
            for (; i <= original.Length - width; i += width)
                ((new Vector<float>(original.Slice(i, width)) + new Vector<float>(background.Slice(i, width))) * gain)
                    .CopyTo(output.Slice(i, width));
            for (; i < original.Length; i++) output[i] = (original[i] + background[i]) * .5f;
        }
    }
}
