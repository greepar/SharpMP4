using SharpISOBMFF;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace SharpMP4.Tracks
{
    public abstract class H26XTrackBase : TrackBase
    {
        public override string HandlerName => HandlerNames.Video;
        public override string HandlerType => HandlerTypes.Video;
        public override string Language { get; set; } = "eng";

        public int NalLengthSize { get; set; } = 4;

        protected H26XTrackBase() : base()
        {
            DefaultSampleFlags = new SampleFlags() { SampleDependsOn = 1, SampleIsDifferenceSample = true };
            TimescaleFallback = 24000;
            FrameTickFallback = 1001;
        }

        public override IEnumerable<byte[]> ParseSample(byte[] sample)
        {
            var nalus = new List<byte[]>();
            foreach (var nalu in ParseSampleSlices(sample))
            {
                var data = nalu.ToArray();
                nalus.Add(data);
            }
            return nalus;
        }

        /// <summary>
        /// Returns NALU slices backed by the original sample. Keep the sample unchanged
        /// while consuming the slices; no NALU payload is copied.
        /// </summary>
        public IEnumerable<ReadOnlyMemory<byte>> ParseSampleSlices(byte[] sample)
        {
            if (sample == null) throw new ArgumentNullException(nameof(sample));
            return ParseSampleSlices(new ReadOnlyMemory<byte>(sample));
        }

        public IEnumerable<ReadOnlyMemory<byte>> ParseSampleSlices(ReadOnlyMemory<byte> sample)
        {
            if (NalLengthSize < 1 || NalLengthSize > 4)
                throw new InvalidDataException("NAL length size must be between 1 and 4 bytes.");
            int offset = 0;
            while (offset < sample.Length)
            {
                if (sample.Length - offset < NalLengthSize)
                    throw new InvalidDataException("Truncated NAL length prefix.");
                uint length = ReadNaluLength(sample.Span.Slice(offset, NalLengthSize));
                offset += NalLengthSize;
                if (length > sample.Length - offset)
                    throw new InvalidDataException("NAL payload exceeds the sample length.");
                yield return sample.Slice(offset, (int)length);
                offset += (int)length;
            }
        }
        private static uint ReadNaluLength(ReadOnlySpan<byte> prefix) => prefix.Length switch
        {
            1 => prefix[0],
            2 => BinaryPrimitives.ReadUInt16BigEndian(prefix),
            3 => ((uint)prefix[0] << 16) | ((uint)prefix[1] << 8) | prefix[2],
            4 => BinaryPrimitives.ReadUInt32BigEndian(prefix),
            _ => throw new InvalidDataException("Invalid NAL length prefix.")
        };
    }
}
