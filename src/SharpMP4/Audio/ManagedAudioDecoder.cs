using System.IO;
using System.Threading;

namespace SharpMP4.Audio
{
    /// <summary>Managed, fixed-buffer MP3 and MP4 AAC decoding to mono PCM16.</summary>
    public static class ManagedAudioDecoder
    {
        public static void DecodeMonoPcm16(Stream input, Stream output, int sampleRate = 24000,
            CancellationToken cancellationToken = default)
            => MixedAudioReader.DecodeMonoPcm16(input, output, sampleRate, cancellationToken);
    }
}
