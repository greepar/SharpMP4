using System;

namespace SharpMP4.Audio
{
    /// <summary>
    /// Streaming AAC-LC encoder for interleaved float PCM. Uses a sine-window FFT MDCT,
    /// bounded quantization search and ISO spectral Huffman coding. No native code or I/O.
    /// Buffers belong to an encoder instance; Encode performs no heap allocations.
    /// </summary>
    public sealed class ManagedAacEncoder
    {
        public const int FrameSamples = 1024;
        public int SampleRate { get; }
        public int Channels { get; }
        private readonly int[] bands;
        private readonly int maxBand, budget;
        private readonly double[][] previous;
        private readonly double[] windowed = new double[2048], folded = new double[1024];
        private readonly double[] real = new double[512], imaginary = new double[512];
        private readonly double[] spectrum = new double[1024], power = new double[1024];
        private readonly int[] quantized = new int[1024], books = new int[64];
        private static readonly double[] Window = MakeWindow();
        private static readonly double[] TwiddleReal = MakeTwiddle(false, false);
        private static readonly double[] TwiddleImaginary = MakeTwiddle(true, false);
        private static readonly double[] FftReal = MakeTwiddle(false, true);
        private static readonly double[] FftImaginary = MakeTwiddle(true, true);
        private static readonly int[] Dimensions = {4,4,4,4,2,2,2,2,2,2,2};
        private static readonly int[] Limits = {1,1,2,2,4,4,7,7,12,12,16};

        public ManagedAacEncoder(int sampleRate = 48000, int channels = 2, int bitrate = 256000)
        {
            if (sampleRate != 44100 && sampleRate != 48000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (channels < 1 || channels > 2) throw new ArgumentOutOfRangeException(nameof(channels));
            if (bitrate < 64000 * channels || bitrate > 288000 * channels) throw new ArgumentOutOfRangeException(nameof(bitrate));
            SampleRate = sampleRate; Channels = channels;
            bands = Array.ConvertAll(AacTables.kSwbLong_48000, value => (int)value);
            // Keep bands up to 20 kHz. A complete band is necessary for Huffman tuples.
            while (maxBand < bands.Length - 1 && bands[maxBand + 1] * sampleRate / 2048.0 <= 20000) maxBand++;
            budget = Math.Min(6100, bitrate * FrameSamples / sampleRate / channels - 48);
            previous = new double[channels][];
            for (int ch = 0; ch < channels; ch++) previous[ch] = new double[FrameSamples];
        }

        /// <summary>Encode exactly 1024 interleaved PCM frames into a raw AAC access unit (no ADTS header).
        /// Destination must have room for 1536 bytes. Instances are not thread safe.</summary>
        public int Encode(ReadOnlySpan<float> pcm, Span<byte> destination)
        {
            if (pcm.Length != FrameSamples * Channels) throw new ArgumentException("Expected one PCM frame.", nameof(pcm));
            if (destination.Length < 1536) throw new ArgumentException("AAC output buffer must hold 1536 bytes.", nameof(destination));
            var writer = new BitWriter(destination);
            writer.Put(Channels == 2 ? 1u : 0u, 3); // CPE or SCE
            writer.Put(0, 4); // element_instance_tag
            if (Channels == 2) writer.Put(0, 1); // independent channel windows
            for (int ch = 0; ch < Channels; ch++)
            {
                Transform(pcm, ch);
                for (int i = 0; i < bands[maxBand]; i++) power[i] = Math.Pow(Math.Abs(spectrum[i]), 0.75);
                int lo = 0, hi = 255;
                while (lo < hi)
                {
                    int mid = (lo + hi) / 2;
                    if (Quantize(mid) <= budget) hi = mid;
                    else lo = mid + 1;
                }
                Quantize(lo);
                writer.Put((uint)lo, 8); // global_gain
                writer.Put(0, 4); // reserved, ONLY_LONG_SEQUENCE, sine shape
                writer.Put((uint)maxBand, 6);
                writer.Put(0, 1); // predictor_data_present
                for (int b = 0; b < maxBand;)
                {
                    int end = b + 1;
                    while (end < maxBand && books[end] == books[b]) end++;
                    writer.Put((uint)books[b], 4);
                    int length = end - b;
                    while (length >= 31) { writer.Put(31, 5); length -= 31; }
                    writer.Put((uint)length, 5);
                    b = end;
                }
                // All coded bands use the same scalefactor; dpcm = 0 (one bit).
                for (int b = 0; b < maxBand; b++) if (books[b] != 0) writer.Put(0, 1);
                writer.Put(0, 3); // no pulse, TNS or gain control
                for (int b = 0; b < maxBand; b++)
                    if (books[b] != 0) CodeBand(books[b], bands[b], bands[b + 1], ref writer, true);
            }
            writer.Put(7, 3); // ID_END
            return (writer.Position + 7) / 8;
        }

        private int Quantize(int gain)
        {
            double scale = Math.Pow(2, (100 - gain) * 3.0 / 16);
            for (int i = 0; i < bands[maxBand]; i++)
            {
                double value = power[i] * scale + 0.4054;
                if (value > 8191) return int.MaxValue;
                int q = (int)value;
                quantized[i] = spectrum[i] < 0 ? -q : q;
            }
            int bits = 22;
            var counter = new BitWriter(Span<byte>.Empty);
            for (int b = 0; b < maxBand; b++)
            {
                bool zero = true;
                for (int i = bands[b]; i < bands[b + 1]; i++) if (quantized[i] != 0) { zero = false; break; }
                books[b] = 0;
                if (zero) continue;
                int best = int.MaxValue;
                for (int book = 1; book <= 11; book++)
                {
                    int cost = CodeBand(book, bands[b], bands[b + 1], ref counter, false);
                    if (cost >= 0 && cost < best) { best = cost; books[b] = book; }
                }
                bits += best + 1;
            }
            for (int b = 0; b < maxBand;)
            {
                int end = b + 1;
                while (end < maxBand && books[end] == books[b]) end++;
                bits += 4 + 5 * ((end - b) / 31 + 1);
                b = end;
            }
            return bits;
        }

        private int CodeBand(int book, int start, int end, ref BitWriter writer, bool write)
        {
            int dim = Dimensions[book - 1], limit = Limits[book - 1], bits = 0;
            bool signed = book == 1 || book == 2 || book == 5 || book == 6;
            for (int i = start; i < end; i += dim)
            {
                int index = 0, signs = 0, signCount = 0;
                for (int j = 0; j < dim; j++)
                {
                    int value = quantized[i + j], abs = Math.Abs(value);
                    if (book != 11 && abs > limit) return -1;
                    index = index * (signed ? 2 * limit + 1 : limit + 1)
                        + (signed ? value + limit : Math.Min(abs, limit));
                    if (!signed && abs != 0) { signs = (signs << 1) | (value < 0 ? 1 : 0); signCount++; }
                }
                int count = AacTables.Bits[book - 1][index];
                bits += count + signCount;
                if (write)
                {
                    writer.Put(AacTables.Codes[book - 1][index], count);
                    writer.Put((uint)signs, signCount);
                }
                if (book == 11) for (int j = 0; j < dim; j++)
                {
                    int abs = Math.Abs(quantized[i + j]);
                    if (abs < 16) continue;
                    int width = 4;
                    while ((abs >> (width + 1)) != 0) width++;
                    int unary = width - 4;
                    bits += unary + 1 + width;
                    if (write)
                    {
                        writer.Put((1u << (unary + 1)) - 2, unary + 1);
                        writer.Put((uint)(abs - (1 << width)), width);
                    }
                }
            }
            return bits;
        }

        private void Transform(ReadOnlySpan<float> pcm, int channel)
        {
            var prev = previous[channel];
            for (int i = 0; i < FrameSamples; i++)
            {
                windowed[i] = prev[i] * Window[i];
                float value = pcm[i * Channels + channel];
                prev[i] = float.IsFinite(value) ? Math.Clamp(value, -1, 1) * 32768.0 : 0;
                windowed[1024 + i] = prev[i] * Window[2047 - (1024 + i)];
            }
            // Fold the 2048-point MDCT into a 1024-point DCT-IV, then a 512-point FFT.
            // Transform construction adapted from glint's MIT-licensed aac_mdct.cpp.
            for (int n = 0; n < 512; n++)
            {
                folded[n] = -windowed[1535 - n] - windowed[1536 + n];
                folded[512 + n] = windowed[n] - windowed[1023 - n];
            }
            for (int n = 0; n < 512; n++)
            {
                double vr = folded[2 * n], vi = folded[1023 - 2 * n];
                real[n] = vr * TwiddleReal[n] - vi * TwiddleImaginary[n];
                imaginary[n] = vr * TwiddleImaginary[n] + vi * TwiddleReal[n];
            }
            for (int i = 0; i < 512; i++)
            {
                int reverse = 0;
                for (int b = 0; b < 9; b++) reverse = (reverse << 1) | ((i >> b) & 1);
                if (reverse > i)
                {
                    (real[i], real[reverse]) = (real[reverse], real[i]);
                    (imaginary[i], imaginary[reverse]) = (imaginary[reverse], imaginary[i]);
                }
            }
            for (int length = 2; length <= 512; length <<= 1)
            {
                int half = length / 2, stride = 512 / length;
                for (int first = 0; first < 512; first += length)
                for (int j = 0; j < half; j++)
                {
                    int a = first + j, b = a + half;
                    double wr = FftReal[j * stride], wi = FftImaginary[j * stride];
                    double xr = real[b] * wr - imaginary[b] * wi, xi = real[b] * wi + imaginary[b] * wr;
                    real[b] = real[a] - xr; imaginary[b] = imaginary[a] - xi;
                    real[a] += xr; imaginary[a] += xi;
                }
            }
            for (int k = 0; k < 512; k++)
            {
                spectrum[2 * k] = Math.Sqrt(2) * (real[k] * TwiddleReal[k] - imaginary[k] * TwiddleImaginary[k]);
                spectrum[1023 - 2 * k] = -Math.Sqrt(2) * (real[k] * TwiddleImaginary[k] + imaginary[k] * TwiddleReal[k]);
            }
        }

        private static double[] MakeWindow()
        {
            var result = new double[1024];
            for (int n = 0; n < result.Length; n++) result[n] = Math.Sin(Math.PI / 2048 * (n + 0.5));
            return result;
        }
        private static double[] MakeTwiddle(bool imaginary, bool fft)
        {
            var result = new double[fft ? 256 : 512];
            for (int n = 0; n < result.Length; n++)
            {
                double angle = fft ? 2 * Math.PI * n / 512 : Math.PI * (n + 0.125) / 1024;
                result[n] = imaginary ? -Math.Sin(angle) : Math.Cos(angle);
            }
            return result;
        }

        private ref struct BitWriter
        {
            private Span<byte> data;
            public int Position { get; private set; }
            public BitWriter(Span<byte> data) { this.data = data; Position = 0; data.Clear(); }
            public void Put(uint value, int bits)
            {
                for (int i = bits - 1; i >= 0; i--)
                {
                    if (Position / 8 >= data.Length) throw new InvalidOperationException("AAC frame exceeds output buffer.");
                    data[Position / 8] |= (byte)(((value >> i) & 1) << (7 - Position % 8));
                    Position++;
                }
            }
        }
    }
}
