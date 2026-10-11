using SharpMP4.Tracks;
using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;

namespace SharpMP4.Readers
{
    /// <summary>
    /// Streaming MPEG-1/2/2.5 Layer III reader (CBR/VBR, CRC and ID3v2 supported).
    /// Does not own the input stream. ReadSample reuses the caller's buffer and its returned
    /// MediaSample; consume the sample before reading again. No per-frame allocations.
    /// Free-format streams and changes of sampling rate/channel count are rejected.
    /// </summary>
    public sealed class MP3Reader
    {
        private static readonly int[] Mpeg1Bitrates = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 };
        private static readonly int[] Mpeg2Bitrates = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 };
        private static readonly int[] SampleRates = { 44100, 48000, 32000 };
        private readonly Stream stream;
        private readonly long audioEnd;
        private readonly MediaSample sample = new MediaSample(0, 0, 0, null);
        private long timestamp;
        public MP3Track Track { get; }

        public static bool IsMP3(Stream stream)
        {
            if (stream == null || !stream.CanSeek || !stream.CanRead) return false;
            var position = stream.Position;
            Span<byte> prefix = stackalloc byte[3];
            try
            {
                return stream.ReadAtLeast(prefix, 3, false) == 3
                    && (prefix.SequenceEqual("ID3"u8) || (prefix[0] == 0xff && (prefix[1] & 0xe0) == 0xe0));
            }
            finally { stream.Position = position; }
        }

        public MP3Reader(Stream stream)
        {
            if (stream == null || !stream.CanRead || !stream.CanSeek)
                throw new ArgumentException("MP3 input must be readable and seekable.", nameof(stream));
            this.stream = stream;
            audioEnd = FindAudioEnd();
            SkipId3Tags();
            var firstPosition = stream.Position;
            Span<byte> header = stackalloc byte[4];
            ReadExactly(header);
            var frame = ParseHeader(header);
            Track = new MP3Track(frame.Channels, (uint)frame.SampleRate, frame.Version);
            // Xing/Info and VBRI are seek/encoder metadata frames, not audio samples.
            Span<byte> firstFrame = stackalloc byte[2048];
            header.CopyTo(firstFrame);
            ReadExactly(firstFrame.Slice(4, frame.Length - 4));
            var sideInfo = frame.Version == 1 ? (frame.Channels == 1 ? 17 : 32) : (frame.Channels == 1 ? 9 : 17);
            var xingOffset = 4 + (frame.HasCrc ? 2 : 0) + sideInfo;
            var metadata = HasMarker(firstFrame.Slice(0, frame.Length), xingOffset, "Xing"u8)
                || HasMarker(firstFrame.Slice(0, frame.Length), xingOffset, "Info"u8)
                || HasMarker(firstFrame.Slice(0, frame.Length), 36, "VBRI"u8);
            if (!metadata) stream.Position = firstPosition;
        }

        public MediaSample ReadSample(ref byte[] buffer, out int bytes, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bytes = 0;
            Span<byte> header = stackalloc byte[4];
            if (stream.Position == audioEnd) return null;
            ReadExactly(header);
            var frame = ParseHeader(header);
            if (frame.SampleRate != Track.Timescale || frame.Channels != Track.ChannelCount || frame.Version != Track.MpegVersion)
                throw new InvalidDataException("MP3 stream changes its audio format.");
            if (buffer == null || buffer.Length < frame.Length) buffer = new byte[2048];
            header.CopyTo(buffer);
            ReadExactly(buffer.AsSpan(4, frame.Length - 4));
            bytes = frame.Length;
            sample.Data = buffer;
            sample.PTS = sample.DTS = timestamp;
            sample.Duration = Track.DefaultSampleDuration;
            timestamp = checked(timestamp + sample.Duration);
            return sample;
        }

        private long FindAudioEnd()
        {
            var origin = stream.Position;
            var end = stream.Length;
            Span<byte> footer = stackalloc byte[32];
            try
            {
                if (end - origin >= 128)
                {
                    stream.Position = end - 128;
                    stream.ReadExactly(footer.Slice(0, 3));
                    if (footer.Slice(0, 3).SequenceEqual("TAG"u8)) end -= 128;
                }
                if (end - origin >= 32)
                {
                    stream.Position = end - 32;
                    stream.ReadExactly(footer);
                    if (footer.Slice(0, 8).SequenceEqual("APETAGEX"u8))
                    {
                        var size = BinaryPrimitives.ReadUInt32LittleEndian(footer.Slice(12, 4));
                        var flags = BinaryPrimitives.ReadUInt32LittleEndian(footer.Slice(20, 4));
                        var hasHeader = (flags & 0x80000000) != 0;
                        var totalSize = (long)size + (hasHeader ? 32 : 0);
                        if (size < 32 || totalSize > end - origin || (flags & 0x60000000) != 0
                            || BinaryPrimitives.ReadUInt32LittleEndian(footer.Slice(8, 4)) > 2000)
                            throw new InvalidDataException("Invalid APE trailer.");
                        end -= totalSize;
                        if (hasHeader)
                        {
                            stream.Position = end;
                            stream.ReadExactly(footer);
                            if (!footer.Slice(0, 8).SequenceEqual("APETAGEX"u8)
                                || BinaryPrimitives.ReadUInt32LittleEndian(footer.Slice(12, 4)) != size)
                                throw new InvalidDataException("Invalid APE header.");
                        }
                    }
                }
                return end;
            }
            finally { stream.Position = origin; }
        }

        private void SkipId3Tags()
        {
            Span<byte> header = stackalloc byte[10];
            while (true)
            {
                var position = stream.Position;
                var count = stream.ReadAtLeast(header, 3, false);
                if (count < 3 || !header.Slice(0, 3).SequenceEqual("ID3"u8))
                {
                    stream.Position = position;
                    return;
                }
                ReadExactly(header.Slice(count));
                if (header[3] < 2 || header[3] > 4 || header[4] == 255 || (header[6] | header[7] | header[8] | header[9]) >= 128)
                    throw new InvalidDataException("Invalid ID3v2 header.");
                var size = (header[6] << 21) | (header[7] << 14) | (header[8] << 7) | header[9];
                var end = checked(position + 10 + size + (header[3] == 4 && (header[5] & 0x10) != 0 ? 10 : 0));
                if (end > audioEnd) throw new InvalidDataException("Truncated ID3v2 tag.");
                stream.Position = end;
            }
        }

        private static bool HasMarker(ReadOnlySpan<byte> data, int offset, ReadOnlySpan<byte> marker)
            => offset + marker.Length <= data.Length && data.Slice(offset, marker.Length).SequenceEqual(marker);

        private void ReadExactly(Span<byte> destination)
        {
            if (destination.Length > audioEnd - stream.Position) throw new InvalidDataException("Truncated MP3 data.");
            try { stream.ReadExactly(destination); }
            catch (EndOfStreamException ex) { throw new InvalidDataException("Truncated MP3 data.", ex); }
        }

        private static FrameHeader ParseHeader(ReadOnlySpan<byte> header)
        {
            var bits = BinaryPrimitives.ReadUInt32BigEndian(header);
            var versionBits = (int)((bits >> 19) & 3);
            var layer = (bits >> 17) & 3;
            var bitrateIndex = (int)((bits >> 12) & 15);
            var rateIndex = (int)((bits >> 10) & 3);
            if ((bits & 0xffe00000) != 0xffe00000 || versionBits == 1 || layer != 1 || bitrateIndex == 15 || rateIndex == 3 || (bits & 3) == 2)
                throw new InvalidDataException("Invalid MPEG Layer III frame header.");
            if (bitrateIndex == 0) throw new NotSupportedException("Free-format MP3 is not supported.");
            var version = versionBits == 3 ? 1 : versionBits == 2 ? 2 : 25;
            var rate = SampleRates[rateIndex] / (version == 1 ? 1 : version == 2 ? 2 : 4);
            var bitrate = (version == 1 ? Mpeg1Bitrates : Mpeg2Bitrates)[bitrateIndex] * 1000;
            var length = (version == 1 ? 144 : 72) * bitrate / rate + (int)((bits >> 9) & 1);
            return new FrameHeader(version, rate, (byte)(((bits >> 6) & 3) == 3 ? 1 : 2), length, (bits & 0x10000) == 0);
        }

        private readonly record struct FrameHeader(int Version, int SampleRate, byte Channels, int Length, bool HasCrc);
    }
}
