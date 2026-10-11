using SharpISOBMFF;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SharpMP4.Tracks
{
    /// <summary>MPEG Layer III samples, copied directly into an ISO MP4 audio track.</summary>
    public sealed class MP3Track : TrackBase
    {
        public byte ChannelCount { get; }
        public int MpegVersion { get; }
        public byte ObjectTypeIndication => MpegVersion == 1 ? (byte)0x6b : (byte)0x69;
        public override string HandlerName => HandlerNames.Sound;
        public override string HandlerType => HandlerTypes.Sound;
        public override string Language { get; set; } = "und";

        public MP3Track(byte channelCount, uint samplingRate, int mpegVersion = 1)
        {
            if (channelCount < 1 || channelCount > 2) throw new ArgumentOutOfRangeException(nameof(channelCount));
            if (mpegVersion != 1 && mpegVersion != 2 && mpegVersion != 25) throw new ArgumentOutOfRangeException(nameof(mpegVersion));
            var divisor = mpegVersion == 1 ? 1u : mpegVersion == 2 ? 2u : 4u;
            if (samplingRate != 44100 / divisor && samplingRate != 48000 / divisor && samplingRate != 32000 / divisor)
                throw new ArgumentOutOfRangeException(nameof(samplingRate));
            ChannelCount = channelCount;
            Timescale = samplingRate;
            MpegVersion = mpegVersion;
            DefaultSampleDuration = mpegVersion == 1 ? 1152 : 576;
        }

        public MP3Track(Box config, uint timescale = 0, int sampleDuration = -1)
        {
            var esds = config as ESDBox ?? config.Children?.OfType<ESDBox>().SingleOrDefault()
                ?? throw new ArgumentException("MP3 sample entry has no ES descriptor.", nameof(config));
            var decoder = esds._ES.Children.OfType<DecoderConfigDescriptor>().Single();
            if (decoder.ObjectTypeIndication != 0x69 && decoder.ObjectTypeIndication != 0x6b)
                throw new ArgumentException("ES descriptor is not MPEG audio.", nameof(config));
            var entry = config.GetParent() as AudioSampleEntry;
            var entryV1 = config.GetParent() as AudioSampleEntryV1;
            Timescale = timescale != 0 ? timescale : (entry?.Samplerate ?? entryV1?.Samplerate ?? 0) >> 16;
            ChannelCount = (byte)(entry?.Channelcount ?? entryV1?.Channelcount ?? 0);
            MpegVersion = decoder.ObjectTypeIndication == 0x6b ? 1 : Timescale < 16000 ? 25 : 2;
            DefaultSampleDuration = sampleDuration > 0 ? sampleDuration : MpegVersion == 1 ? 1152 : 576;
        }

        public override Box CreateSampleEntryBox()
        {
            var entry = new AudioSampleEntryV1(IsoStream.FromFourCC("mp4a"))
            {
                Channelcount = ChannelCount, Samplerate = Timescale << 16, Samplesize = 16,
                DataReferenceIndex = 1, ReservedSampleEntry = new byte[6], Reserved = new ushort[3],
                Children = new List<Box>()
            };
            var esds = new ESDBox();
            esds.SetParent(entry);
            entry.Children.Add(esds);
            var descriptor = new ES_Descriptor { ESID = (ushort)TrackID, Children = new List<Descriptor>() };
            esds._ES = descriptor;
            var decoder = new DecoderConfigDescriptor
            {
                ObjectTypeIndication = ObjectTypeIndication, StreamType = 5,
                BufferSizeDB = 2048, Children = new List<Descriptor>()
            };
            decoder.SetParent(descriptor);
            descriptor.Children.Add(decoder);
            var sl = new SLConfigDescriptor { Predefined = 2, Ocr = Array.Empty<byte>(), UseTimeStampsFlag = true };
            sl.SetParent(descriptor);
            descriptor.Children.Add(sl);
            return entry;
        }

        public override void FillTkhdBox(TrackHeaderBox tkhd) => tkhd.Volume = 256;
        public override ITrack Clone() => new MP3Track(ChannelCount, Timescale, MpegVersion) { Language = Language };
    }
}
