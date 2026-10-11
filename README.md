# SharpMP4
Simple lightweight mp4/fmp4/mov/m4v reader/writer. Supports H264/H265/H266/AV1 for video and AAC/Opus for audio. No platform dependencies, easily portable cross-platform. It was designed to be a stream-in and stream-out solution for recording streams from IP cameras into MP4 and fragmented MP4.

[![NuGet version](https://img.shields.io/nuget/v/SharpMP4.svg?style=flat-square)](https://www.nuget.org/packages/SharpMP4)

## Supported boxes
The list of all supported boxes, entries and descriptors is [here](Boxes.md).

## Read MP4
To parse an existing mp4/mov/m4v file, first you have to get the stream:
```cs
using (Stream inputFileStream = new BufferedStream(new FileStream("frag_bunny.mp4", FileMode.Open, FileAccess.Read, FileShare.Read)))
{
    ...
}
```
Create a new `Container` and call `Read` to get the in-memory representation of all the boxes.
```cs 
var mp4 = new Container();
mp4.Read(new IsoStream(inputFileStream));    ...

```
To process this in-memory representation and read the audio/video samples, create a new `VideoReader`:
```cs
VideoReader videoReader = new VideoReader();
videoReader.Parse(mp4);
```
Now it is possible to get all the tracks from the video:
```cs
IEnumerable<ITrack> tracks = videoReader.GetTracks();
```
To read the first track samples, call:
```cs
uint trackID = tracks.First().TrackID;
MediaSample sample = videoReader.ReadSample(trackID);
```
Where `trackID` is the ID of the track you want to read.

For sequential remuxing, reuse a caller-owned sample buffer:
```cs
byte[] buffer = null;
int bytesRead;
while ((sample = videoReader.ReadSample(trackID, ref buffer, out bytesRead)) != null)
{
    // Consume only buffer[0..bytesRead] before reading the next sample.
    builder.ProcessRawSample(outputTrackID, buffer, 0, bytesRead,
        sample.Duration, sample.IsRandomAccessPoint, sample.PTS - sample.DTS);
}
```
The original `ReadSample(trackID)` still returns an independent, exact-length array.
`ReadSample(trackID, false)` reads timing/flags with empty data and skips media I/O.
H.264/H.265/H.266 tracks also provide `ParseSampleSlices(sample)`, returning
`ReadOnlyMemory<byte>` NALUs backed by the sample without payload copies; keep the
sample unchanged while using these slices. Invalid NAL lengths throw `InvalidDataException`.

Concrete builders implement `IDisposable`. Use `using` to release their temporary
storage if processing fails or is cancelled. `FinalizeMedia()` also releases
factory-created storage; explicitly supplied storage remains owned by the caller.
`Mp4Builder.AddTrack(track, sampleCountHint)` reserves sample table capacity when
the sample count is known. `TemporaryMemoryStorageFactory(initialCapacity)` can
reserve bytes when memory storage is explicitly desired.

## Build MP4
To write `MP4` into a file, you first have to create the file:
```cs
using (Stream output = new BufferedStream(new FileStream("bunny_out.mp4", FileMode.Create, FileAccess.Write, FileShare.Read)))
{
    ...
}
```
Next, create the builder depending upon the output format. Currently, there are two builders - `FragmentedMp4Builder` and `Mp4Builder`.
```cs
IMp4Builder outputBuilder = new Mp4Builder(new SingleStreamOutput(output));
```
For fragmented MP4 output, use `FragmentedMp4Builder` with the fragment duration in miliseconds:
```cs
// use fragment duration 2 seconds
IMp4Builder outputBuilder = new FragmentedMp4Builder(new SingleStreamOutput(output), 2000);
```

Add the H264 video track to the builder instance:
```cs
var videoTrack = new H264Track();
outputBuilder.AddTrack(videoTrack);
```
Add the AAC audio track to the builder instance:
```cs
var audioTrack = new AACTrack(2, 44100, 16);
outputBuilder.AddTrack(audioTrack);
```
Pass the track samples to the builder as follows:
```cs
byte[] nalu = ...;
outputBuilder.ProcessTrackSample(videoTrack.TrackID, nalu);
...
byte[] aac = ...;
outputBuilder.ProcessTrackSample(audioTrack.TrackID, aac);
```
When done, call `FinalizeMedia` to create the video file:
```cs
outputBuilder.FinalizeMedia();
```

To repeat an existing MP4 video three times with an MP4 audio track or a raw MP3 file, without re-encoding:
```cs
using var video = new BufferedStream(File.OpenRead("short-video.mp4"));
using var audio = new BufferedStream(File.OpenRead("music.mp3")); // .m4a is also supported
using var output = File.Create("looped-with-audio.mp4");
LoopingVideoAudioMuxer.Mux(video, audio, output, loopCount: 3);
```
Inputs must be seekable, and the video must start with a keyframe. Pass `null` for audio to repeat only the video, or `loopCount: 1` to play it once. Long audio is trimmed at the video end; short audio ends naturally while the video continues. This is entirely managed remuxing, without ffmpeg, native codecs, or PCM conversion. It does not animate still images.

Use `LoopingVideoAudioMuxer.MuxPreservingEmbeddedAudio(video, audio, output, loopCount: 3)` to preserve and repeat the video's existing audio alongside the picture. In this mode the supplied background audio is used only when the video has no embedded audio track. Encoded samples are copied without re-encoding.

`MP3Reader` supports MPEG-1/2/2.5 Layer III, CBR/VBR, CRC-protected frames, ID3v2 tags (including v2.4 footers), ID3v1/APEv2 trailers, and Xing/Info/VBRI metadata frames. It reads sequentially and reuses the caller's frame buffer and the returned `MediaSample` instance; consume each sample before reading the next. Free-format MP3, malformed/truncated data, and changes of sample rate or channel count are rejected. Gapless encoder delay/padding metadata is not transferred to MP4 edit lists.

An MP3-only MP4 can also be written with the normal builder:
```cs
var reader = new MP3Reader(audioStream);
using var builder = new Mp4Builder(new SingleStreamOutput(outputStream));
builder.AddTrack(reader.Track);
byte[] buffer = null;
MediaSample sample;
while ((sample = reader.ReadSample(ref buffer, out var bytes)) != null)
    builder.ProcessRawSample(reader.Track.TrackID, buffer, 0, bytes, sample.Duration, true, 0);
builder.FinalizeMedia();
```
`MP3Track` uses `mp4a` with MPEG audio ES descriptor object types `0x6b` (MPEG-1) and `0x69` (MPEG-2/2.5), matching the [FFmpeg ISO media codec registry](https://github.com/FFmpeg/FFmpeg/blob/master/libavformat/isom.c). `VideoReader` recognizes these tracks on round-trip reads. The output keeps the MP3 codec and original compressed frames; it does not convert audio to AAC.

## Extensibility
### Logging
There is an `IMp4Logger` interface exposed on the `Mp4Builder`, `FragmentedMp4Builder`, `VideoReader` and `ImageReader` instances where you can supply your own logger implementation, or use an existing one:
```cs
mp4Builder.Logger = new ConsoleMp4Logger();
```
You can also enable/disable different trace levels like:
```cs
mp4Builder.Logger.IsWarnEnabled = false;
```

## Credits
Huge inspiration for this project was the `mp4parser` https://github.com/sannies/mp4parser, thank you very much!

The library targets .NET 8 and .NET 10; .NET Framework 4.8.1 is no longer supported.

Dispose the owning `IsoStream` after using its parsed data. It also closes any
temporary storage created for non-seekable input. Returned `StreamMarker` data
is valid only while its owning stream remains open.

### Managed original-audio and music mixing

```csharp
// AAC embedded in the video + raw MP3 or AAC-in-MP4 background music:
LoopingVideoAudioMuxer.MuxMixingEmbeddedAudio(video, music, output, loopCount: 3);
```

This mode decodes each source incrementally, resamples mono/stereo PCM to 48 kHz stereo with a 32-tap polyphase sinc filter, mixes the two sources at equal half gain using SIMD, and encodes one AAC-LC track. Video packets are copied unchanged. Original audio plays once and is then padded with silence. Background music plays through once; the picture repeats for at least `loopCount` passes and continues until the music finishes when it is longer. Shorter music is padded with silence. Encoder priming is kept for decoding and hidden using an MP4 edit list. Simple input AAC edit lists are honored. If no embedded audio exists, the original remux path keeps the supplied background codec unchanged; if no background exists, embedded audio is preserved without encoding.

`PcmMixer.Mix` and `ManagedAacEncoder.Encode` are also available separately. The encoder accepts interleaved float PCM, mono/stereo at 44.1/48 kHz, and emits raw 1024-sample AAC-LC access units. It uses long sine windows, spectral Huffman codebook selection and a bounded rate search, without a full psychoacoustic model. Mixing and encoding reuse fixed buffers and do not allocate per frame; the managed SharpJaad.AAC and NLayer decoders have their own reusable buffers. AAC mixing supports MP4 AAC mono/stereo; encrypted tracks and complex edit lists are rejected. No external process or native codec is used in this path. Container metadata and encoded samples use the existing temporary storage factory, preserving the bounded-memory mux behavior.

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for codec dependencies and the FFT MDCT reference license.
