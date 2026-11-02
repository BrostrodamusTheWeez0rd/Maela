using System;
using System.Collections.Generic;

namespace Maela
{
    /// <summary>
    /// How loud each person is right now, for the Dynamic layout's voice lines. Worked out from
    /// audio already being played or sent, so it costs a few additions per frame and sends nothing.
    /// </summary>
    internal sealed partial class VoipEngine
    {
        private readonly Dictionary<uint, float> _voiceLevels = new Dictionary<uint, float>(); // Under _stateLock.

        /// <summary>0 (silent) to 2 for someone talking now: about 1 for normal talking, 2 for shouting. Else 0.</summary>
        public float GetVoiceLevel(uint memberId)
        {
            lock (_stateLock)
            {
                DateTime last;
                float level;
                if (!_lastSpeakingUtc.TryGetValue(memberId, out last) || (DateTime.UtcNow - last).TotalMilliseconds > 250.0)
                    return 0f;
                return _voiceLevels.TryGetValue(memberId, out level) ? level : 0f;
            }
        }

        /// <summary>
        /// The loudness of 16-bit PCM as 0 to 2: quiet speech (about -50 dB) is near 0, normal
        /// talking (about -26 dB) is 1, and shouting (about -6 dB) is 2.
        /// </summary>
        internal static float LevelOf(byte[] pcm16)
        {
            int count = pcm16.Length / 2;
            if (count == 0)
                return 0f;
            double sum = 0;
            for (int i = 0; i < count; i++)
            {
                short v = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));
                sum += (double)v * v;
            }
            double rms = Math.Sqrt(sum / count) / 32768.0;
            if (rms <= 1e-6)
                return 0f;
            double db = 20 * Math.Log10(rms);
            if (db <= -26)
                return (float)Math.Max(0, (db + 50) / 24);
            return (float)Math.Min(2, 1 + (db + 26) / 20);
        }

        /// <summary>Remembers a level. _stateLock held.</summary>
        private void NoteVoiceLevelLocked(uint memberId, float level)
        {
            _voiceLevels[memberId] = level;
        }
    }
}
