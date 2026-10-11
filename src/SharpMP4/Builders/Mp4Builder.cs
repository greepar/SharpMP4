using SharpISOBMFF;
using SharpISOBMFF.Extensions;
using SharpMP4.Common;
using SharpMP4.Tracks;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SharpMP4.Builders
{
    /// <summary>
    /// Creates MP4.
    /// </summary>
    public class Mp4Builder : IMp4Builder, IDisposable
    {
        private class TrackContext
        {
            public ITrack Track { get; set; }
            public ulong EndTime { get; set; }
            public List<uint> SampleSizes { get; set; } = new List<uint>();
            public List<uint> SampleOffsets { get; set; } = new List<uint>();
            public List<uint> SampleDurations { get; set; } = new List<uint>();
            public List<long> CompositionOffsets { get; set; } = new List<long>();
            public List<uint> RandomAccessPoints { get; set; } = new List<uint>();
            public long PresentationStart { get; set; } = long.MaxValue;
            public long PresentationEnd { get; set; } = long.MinValue;

            public TrackContext(ITrack track, int sampleCountHint)
            {
                Track = track;
                SampleSizes.Capacity = sampleCountHint;
                SampleOffsets.Capacity = sampleCountHint;
                SampleDurations.Capacity = sampleCountHint;
                CompositionOffsets.Capacity = sampleCountHint;
            }
        }

        public uint MovieTimescale { get; set; } = 1000;

        private readonly IMp4Output _output;

        private IStorage _storage;
        private bool _ownsStorage;

        private readonly Dictionary<uint, TrackContext> _trackContexts = new Dictionary<uint, TrackContext>();        

        /// <summary>
        /// Ctor.
        /// </summary>
        /// <param name="output">Output stream. Will be progressively written while recording. <see cref="IMp4Output"/>.</param>
        public Mp4Builder(IMp4Output output)
        {
            _output = output;
        }

        public IStorage Storage
        {
            get => _storage;
            set
            {
                if (ReferenceEquals(_storage, value)) return;
                Dispose();
                _storage = value;
            }
        }

        public ITemporaryStorageFactory TemporaryStorageFactory { get; set; } = new TemporaryFileStorageFactory();
        public IMp4Logger Logger { get; set; } = new DefaultMp4Logger();

        /// <summary>
        /// Add a track to the MP4.
        /// </summary>
        /// <param name="track">Track to add: <see cref="TrackBase"/>.</param>
        public void AddTrack(ITrack track)
        {
            AddTrack(track, 0);
        }

        /// <summary>Adds a track with an optional known sample count to avoid table growth.</summary>
        public void AddTrack(ITrack track, int sampleCountHint)
        {
            if (sampleCountHint < 0) throw new ArgumentOutOfRangeException(nameof(sampleCountHint));
            track.Logger ??= this.Logger;

            uint trackID = GetNextTrackId();
            track.TrackID = trackID;
            _trackContexts.Add(trackID, new TrackContext(track, sampleCountHint));
        }
        
        /// <summary>Sets the presented interval after all samples have been written, without removing
        /// decoder priming packets. The MP4 edit list skips samples outside this interval.</summary>
        public void SetPresentationWindow(uint trackId, long start, long duration)
        {
            if (!_trackContexts.TryGetValue(trackId, out var track)) throw new ArgumentOutOfRangeException(nameof(trackId));
            if (start < 0 || duration <= 0 || (ulong)checked(start + duration) > track.EndTime)
                throw new ArgumentOutOfRangeException(nameof(duration));
            track.PresentationStart = start;
            track.PresentationEnd = checked(start + duration);
        }

        private uint GetNextTrackId()
        {
            uint trackID = 1;
            while (_trackContexts.ContainsKey(trackID))
                trackID++;
            return trackID;
        }

        private void WriteSample(uint trackID, byte[] sample, int offset, int count, int sampleDuration, bool isRandomAccessPoint, long compositionTimeOffset)
        {
            if (sample == null) throw new ArgumentNullException(nameof(sample));
            if (offset < 0 || count < 0 || offset > sample.Length - count)
                throw new ArgumentOutOfRangeException(nameof(count));
            var track = _trackContexts[trackID];
            int resolvedSampleDuration = sampleDuration <= 0 ? track.Track.DefaultSampleDuration : sampleDuration;
            if (resolvedSampleDuration <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sampleDuration), "Sample duration must resolve to a positive value.");
            }

            if (_storage == null)
            {
                _storage = this.TemporaryStorageFactory.Create();
                _ownsStorage = true;
            }

            uint currentSampleDuration = (uint)resolvedSampleDuration;
            track.SampleOffsets.Add((uint)_storage.GetPosition());
            _storage.Write(sample, offset, count);
            track.SampleSizes.Add((uint)count);
            track.SampleDurations.Add(currentSampleDuration);
            track.CompositionOffsets.Add(compositionTimeOffset);
            long presentationTime = checked((long)track.EndTime + compositionTimeOffset);
            track.PresentationStart = Math.Min(track.PresentationStart, presentationTime);
            track.PresentationEnd = Math.Max(track.PresentationEnd, checked(presentationTime + currentSampleDuration));
            track.EndTime = checked(track.EndTime + currentSampleDuration);

            if(isRandomAccessPoint)
            {
                track.RandomAccessPoints.Add((uint)track.SampleSizes.Count);
            }
        }

        private static ulong RescaleCeiling(ulong value, uint sourceTimescale, uint destinationTimescale)
        {
            if (sourceTimescale == 0)
                throw new ArgumentOutOfRangeException(nameof(sourceTimescale));
            if (destinationTimescale == 0)
                throw new ArgumentOutOfRangeException(nameof(destinationTimescale));

            ulong quotient = value / sourceTimescale;
            ulong remainder = value % sourceTimescale;
            ulong scaledRemainder = checked(remainder * destinationTimescale);
            ulong roundedRemainder = scaledRemainder == 0 ? 0 : (scaledRemainder - 1) / sourceTimescale + 1;
            return checked(checked(quotient * destinationTimescale) + roundedRemainder);
        }

        private static ulong GetPresentationDuration(TrackContext track)
        {
            if (track.PresentationStart == long.MaxValue)
                return 0;

            long mediaStart = Math.Max(0, track.PresentationStart);
            return checked((ulong)Math.Max(0, track.PresentationEnd - mediaStart));
        }

        private MovieBox BuildMoov()
        {
            var moov = new MovieBox();

            var mvhd = new MovieHeaderBox();
            mvhd.SetParent(moov);
            moov.Children = new List<Box>();
            moov.Children.Add(mvhd);

            ulong movieDuration = 0;
            foreach(var track in _trackContexts.Values)
            {
                ulong trackDuration = RescaleCeiling(GetPresentationDuration(track), track.Track.Timescale, MovieTimescale);
                if (trackDuration > movieDuration)
                {
                    movieDuration = trackDuration;
                }
            }

            mvhd.Version = movieDuration > uint.MaxValue ? (byte)1 : (byte)0;
            mvhd.Duration = movieDuration;
            mvhd.NextTrackID = 0xFFFFFFFF;
            mvhd.Timescale = MovieTimescale; // just for movie time: https://stackoverflow.com/questions/77803940/diffrence-between-mvhd-box-timescale-and-mdhd-box-timescale-in-isobmff-format
            mvhd.Reserved0 = new uint[2]; // TODO simplify API
            mvhd.PreDefined = new uint[6]; // TODO simplify API

            foreach (var track in _trackContexts.Values)
            {
                var trak = new TrackBox();
                trak.SetParent(moov);
                trak.Children = new List<Box>();
                moov.Children.Add(trak);

                var tkhd = new TrackHeaderBox();
                tkhd.SetParent(trak);
                trak.Children.Add(tkhd);
                tkhd.TrackID = track.Track.TrackID;
                tkhd.Reserved1 = new uint[2]; // TODO simplify API
                if (track.Track.HandlerType == HandlerTypes.Video)
                {
                    // 0x1 - track enabled, 0x2 - track is used in the movie, 0x4 - track is used in movie's preview, 0x8 - track is used in movie poster
                    tkhd.Flags = 0x01 | 0x02 | 0x04 | 0x08;
                }
                else
                {
                    tkhd.Flags = 0x01 | 0x02;
                }
                track.Track.FillTkhdBox(tkhd);
                tkhd.Duration = RescaleCeiling(GetPresentationDuration(track), track.Track.Timescale, MovieTimescale);
                tkhd.Version = tkhd.Duration > uint.MaxValue ? (byte)1 : (byte)0;

                long mediaStart = Math.Max(0, track.PresentationStart);
                if (mediaStart > 0)
                {
                    var edts = new EditBox();
                    edts.SetParent(trak);
                    edts.Children = new List<Box>();
                    trak.Children.Add(edts);

                    bool useVersion1 = tkhd.Duration > uint.MaxValue || mediaStart > int.MaxValue;
                    var elst = new EditListBox(useVersion1 ? (byte)1 : (byte)0)
                    {
                        EntryCount = 1,
                        EditDuration = new[] { tkhd.Duration },
                        MediaTime = new[] { mediaStart },
                        MediaRateInteger = new short[] { 1 },
                        MediaRateFraction = new short[] { 0 }
                    };
                    elst.SetParent(edts);
                    edts.Children.Add(elst);
                }

                var mdia = new MediaBox();
                mdia.SetParent(trak);
                trak.Children.Add(mdia);

                MediaHeaderBox mdhd = new MediaHeaderBox();
                mdhd.SetParent(mdia);
                mdia.Children = new List<Box>();
                mdia.Children.Add(mdhd);
                mdhd.Duration = track.EndTime;
                mdhd.Version = mdhd.Duration > uint.MaxValue ? (byte)1 : (byte)0;
                mdhd.Timescale = track.Track.Timescale;
                mdhd.Language = track.Track.Language;

                HandlerBox hdlr = new HandlerBox();
                hdlr.SetParent(mdia);
                mdia.Children.Add(hdlr);
                hdlr.HandlerType = IsoStream.FromFourCC(track.Track.HandlerType);
                hdlr.Name = new BinaryUTF8String(track.Track.HandlerName);
                hdlr.Reserved = new uint[3]; // TODO simplify API

                MediaInformationBox minf = new MediaInformationBox();
                minf.SetParent(mdia);
                mdia.Children.Add(minf);
                minf.Children = new List<Box>();

                switch (track.Track.HandlerType)
                {
                    case HandlerTypes.Video:
                        {
                            var vmhd = new VideoMediaHeaderBox();
                            vmhd.SetParent(mdia);
                            minf.Children.Add(vmhd);
                        }
                        break;

                    case HandlerTypes.Sound:
                        {
                            var smhd = new SoundMediaHeaderBox();
                            smhd.SetParent(mdia);
                            minf.Children.Add(smhd);
                        }
                        break;

                    case HandlerTypes.Hint:
                        {
                            var nmhd = new NullMediaHeaderBox();
                            nmhd.SetParent(mdia);
                            minf.Children.Add(nmhd);
                        }
                        break;

                    default:
                        throw new NotSupportedException(track.Track.HandlerType);
                }

                var dinf = new DataInformationBox();
                dinf.SetParent(minf);
                minf.Children.Add(dinf);
                dinf.Children = new List<Box>();

                var dref = new DataReferenceBox();
                dref.SetParent(dinf);
                dinf.Children.Add(dref);
                dref.Children = new List<Box>();
                dref.EntryCount = 1;

                var url = new DataEntryUrlBox();
                url.Flags = 1;
                url.SetParent(dref);
                dref.Children.Add(url);

                var stbl = new SampleTableBox();
                stbl.SetParent(minf);
                minf.Children.Add(stbl);
                stbl.Children = new List<Box>();

                var stsd = new SampleDescriptionBox();
                stsd.SetParent(stbl);
                stbl.Children.Add(stsd);
                stsd.Children = new List<Box>();

                var sampleEntryBox = track.Track.CreateSampleEntryBox();
                sampleEntryBox.SetParent(stsd);
                stsd.Children.Add(sampleEntryBox);
                stsd.EntryCount = 1;

                var stts = new TimeToSampleBox();
                stts.SetParent(stbl);
                stbl.Children.Add(stts);
                var sttsSampleCounts = new List<uint>();
                var sttsSampleDeltas = new List<uint>();
                foreach (uint duration in track.SampleDurations)
                {
                    if (sttsSampleDeltas.Count > 0 && sttsSampleDeltas[sttsSampleDeltas.Count - 1] == duration)
                    {
                        sttsSampleCounts[sttsSampleCounts.Count - 1] = checked(sttsSampleCounts[sttsSampleCounts.Count - 1] + 1);
                    }
                    else
                    {
                        sttsSampleCounts.Add(1);
                        sttsSampleDeltas.Add(duration);
                    }
                }
                stts.SampleCount = sttsSampleCounts.ToArray();
                stts.SampleDelta = sttsSampleDeltas.ToArray();
                stts.EntryCount = (uint)stts.SampleCount.Length;

                if (track.CompositionOffsets.Any(offset => offset != 0))
                {
                    var cttsSampleCounts = new List<uint>();
                    var cttsSampleOffsets = new List<long>();
                    foreach (long offset in track.CompositionOffsets)
                    {
                        if (cttsSampleOffsets.Count > 0 && cttsSampleOffsets[cttsSampleOffsets.Count - 1] == offset)
                        {
                            cttsSampleCounts[cttsSampleCounts.Count - 1] = checked(cttsSampleCounts[cttsSampleCounts.Count - 1] + 1);
                        }
                        else
                        {
                            cttsSampleCounts.Add(1);
                            cttsSampleOffsets.Add(offset);
                        }
                    }

                    bool hasNegativeOffset = cttsSampleOffsets.Any(offset => offset < 0);
                    var ctts = new CompositionOffsetBox(hasNegativeOffset ? (byte)1 : (byte)0);
                    ctts.SetParent(stbl);
                    stbl.Children.Add(ctts);
                    ctts.SampleCount = cttsSampleCounts.ToArray();
                    ctts.EntryCount = (uint)ctts.SampleCount.Length;
                    if (hasNegativeOffset)
                    {
                        ctts.SampleOffset0 = cttsSampleOffsets.Select(offset => checked((int)offset)).ToArray();
                    }
                    else
                    {
                        ctts.SampleOffset = cttsSampleOffsets.Select(offset => checked((uint)offset)).ToArray();
                    }
                }

                if (track.Track.HandlerType == HandlerTypes.Video)
                {
                    // this box is optional, but it allows for seeking without picture artifacts
                    var stss = new SyncSampleBox();
                    stss.SetParent(stbl);
                    stbl.Children.Add(stss);
                    stss.SampleNumber = track.RandomAccessPoints.ToArray(); 
                    stss.EntryCount = stss.SampleNumber != null ? (uint)stss.SampleNumber.Length : 0;
                }

                var stsc = new SampleToChunkBox();
                stsc.SetParent(stbl);
                stbl.Children.Add(stsc);
                // defaults: 1 sample per chunk
                stsc.FirstChunk = new uint[] { 1 };
                stsc.SamplesPerChunk = new uint[] { 1 };
                stsc.SampleDescriptionIndex = new uint[] { 1 };                
                stsc.EntryCount = (uint)stsc.FirstChunk.Length;

                var stsz = new SampleSizeBox();
                stsz.SetParent(stbl);
                stbl.Children.Add(stsz);
                stsz.EntrySize = track.SampleSizes.ToArray();
                stsz.SampleCount = (uint)track.SampleSizes.Count;

                var stco = new ChunkOffsetBox();
                stco.SetParent(stbl);
                stbl.Children.Add(stco);
                stco.ChunkOffset = track.SampleOffsets.ToArray(); // Temporary offset from the beginning of raw data, we have to add mdat offset later
                stco.EntryCount = (uint)track.SampleOffsets.Count;
            }

            return moov;
        }

        public void ProcessTrackSample(uint trackID, byte[] sample, int sampleDuration)
        {
            _trackContexts[trackID].Track.ProcessSample(sample, out var processedSample, out var isRandomAccessPoint);

            if (processedSample != null)
            {
                ProcessRawSample(trackID, processedSample, sampleDuration, isRandomAccessPoint);
            }
        }

        public void ProcessRawSample(uint trackID, byte[] sample, int sampleDuration, bool isRandomAccessPoint)
        {
            ProcessRawSample(trackID, sample, sampleDuration, isRandomAccessPoint, 0);
        }

        public void ProcessRawSample(uint trackID, byte[] sample, int sampleDuration, bool isRandomAccessPoint, long compositionTimeOffset)
        {
            WriteSample(trackID, sample, 0, sample.Length, sampleDuration, isRandomAccessPoint, compositionTimeOffset);
        }

        /// <summary>Writes only the valid range of a sample buffer; the buffer is not retained.</summary>
        public void ProcessRawSample(uint trackID, byte[] sample, int offset, int count,
            int sampleDuration, bool isRandomAccessPoint, long compositionTimeOffset = 0)
        {
            WriteSample(trackID, sample, offset, count, sampleDuration, isRandomAccessPoint, compositionTimeOffset);
        }

        public void Dispose()
        {
            if (_ownsStorage)
            {
                _storage.Dispose();
                _ownsStorage = false;
            }
        }

        public void FinalizeMedia()
        {
            try { FinalizeMediaCore(); }
            finally { Dispose(); }
        }

        private void FinalizeMediaCore()
        {
            foreach(var track in _trackContexts.Values)
            {
                ProcessTrackSample(track.Track.TrackID, null, -1);
            }

            var mp4 = new Container();

            var ftyp = new FileTypeBox();
            ftyp.SetParent(mp4);
            mp4.Children.Add(ftyp);
            ftyp.MajorBrand = IsoStream.FromFourCC("isom");
            ftyp.MinorVersion = 512;
            var compatibleBrands = new List<string>() { "mp41", "isom" };
            foreach(var track in _trackContexts.Values)
            {
                if (!string.IsNullOrEmpty(track.Track.CompatibleBrand))
                {
                    compatibleBrands.Insert(1, track.Track.CompatibleBrand);
                }
            }
            ftyp.CompatibleBrands = compatibleBrands.Distinct().Select(IsoStream.FromFourCC).ToArray();

            // create moov at the beginning of the file (faststart, allowing to play video early while still streaming)
            var moov = BuildMoov();
            moov.SetParent(mp4);
            mp4.Children.Add(moov);

            var mdat = new MediaDataBox();
            mp4.Children.Add(mdat);
            mdat.Data = new StreamMarker(0, _storage.GetLength(), new IsoStream(_storage));

            // we know the final size of the moov box, we can now adjust the stco offsets
            long mdatOffset = ((int)(ftyp.CalculateSize() + moov.CalculateSize()) >> 3) + 4 + (mdat.HasLargeSize ? 8 : 4);
            moov.ModifyChunkOffsets(mdatOffset);

            var stream = _output.GetStream(0);
            var outputStream = new IsoStream(stream);
            mp4.Write(outputStream);
            _output.Flush(stream, 0);
        }
    }
}
