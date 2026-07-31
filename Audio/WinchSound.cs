using System;

namespace Yoink.Audio
{
    /// <summary>
    /// The winch's crank, played only while the winch is actually reeling and pitched by how fast the load is
    /// coming in. A manual winch that sounds the same whether the rope is running free or a car is refusing to
    /// move would say nothing; tying the pitch to the measured reel rate makes the sound report the load - it
    /// speeds up as the load breaks free and drags down to a grind when it does not.
    ///
    /// The clip is a plain PCM WAV embedded in the DLL and decoded here rather than shipped in an AssetBundle -
    /// the mod already parses its own model, and one more small parser is cheaper than a Unity project.
    /// </summary>
    internal static class WinchSound
    {
        private const float MinPitch = 0.55f;   // straining against a stuck load
        private const float MaxPitch = 1.35f;   // rope running in freely

        private static AudioClip _clip;
        private static AudioSource _source;
        private static bool _failed;
        private static bool _started;

        internal static void ResetSession()
        {
            try { if (_source != null) UnityEngine.Object.Destroy(_source.gameObject); }
            catch { }
            _source = null;
            _failed = false;
            _started = false;
            // The clip survives: it is decoded once and does not belong to the scene.
        }

        /// <summary>
        /// Drives the sound for this frame. <paramref name="reeling"/> is whether the winch is engaged and
        /// <paramref name="rate"/> is the reel-in speed along the rope in m/s.
        /// </summary>
        internal static void Tick(bool reeling, float rate, float maxRate)
        {
            if (_failed) return;

            try
            {
                if (!reeling)
                {
                    // Pause, never Stop. Stop rewinds to zero, so every re-engage began on the same attack - a crank
                    // that restarts identically each time reads as a sound effect being triggered rather than as a
                    // mechanism that was already turning.
                    if (_source != null && _source.isPlaying) _source.Pause();
                    return;
                }

                if (!EnsureSource()) return;

                float t = maxRate > 0.01f ? Mathf.Clamp01(rate / maxRate) : 0f;
                _source.pitch = Mathf.Lerp(MinPitch, MaxPitch, t);

                if (!_source.isPlaying)
                {
                    if (_started)
                    {
                        _source.UnPause();
                    }
                    else
                    {
                        // First engage of the session starts somewhere random in the loop. The clip is a few seconds
                        // of repeating crank, so beginning at the same point every session makes two sessions sound
                        // like the same recording; entering it anywhere makes it sound like a machine that was there
                        // before you arrived.
                        _source.time = _clip != null ? UnityEngine.Random.Range(0f, _clip.length * 0.98f) : 0f;
                        _source.Play();
                        _started = true;
                    }
                }
            }
            catch (Exception e)
            {
                _failed = true;
                Core.Log.Warning("[Sound] winch audio failed: " + e.Message);
            }
        }

        private static bool EnsureSource()
        {
            if (_source != null) return true;

            if (_clip == null)
            {
                byte[] wav = Item.WinchItem.ReadEmbeddedPublic("Yoink.Assets.winch_crank.wav");
                if (wav == null) { _failed = true; return false; }

                _clip = DecodeWav(wav, "YoinkWinchCrank");
                if (_clip == null) { _failed = true; return false; }
            }

            // 2D and parented to nothing: the winch is in the player's hands, so the crank has no place in the
            // world to come from and should not fall off with distance or pan around when the camera turns.
            GameObject go = new GameObject("YoinkWinchAudio");
            UnityEngine.Object.DontDestroyOnLoad(go);

            _source = go.AddComponent<AudioSource>();
            _source.clip = _clip;
            _source.loop = true;
            _source.spatialBlend = 0f;
            _source.volume = 0.6f;
            _source.playOnAwake = false;
            return true;
        }

        /// <summary>
        /// Turns a PCM WAV into an AudioClip. Deliberately narrow: 8/16/24/32-bit integer or 32-bit float PCM,
        /// any channel count and sample rate. It walks the chunk list rather than assuming fmt and data sit at
        /// fixed offsets, because recorded files routinely carry cue/bext/LIST chunks in between - this one does.
        /// </summary>
        private static AudioClip DecodeWav(byte[] wav, string name)
        {
            try
            {
                if (wav.Length < 12 || wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F')
                {
                    Core.Log.Warning("[Sound] not a RIFF file.");
                    return null;
                }

                int channels = 0, sampleRate = 0, bits = 0, format = 0;
                int dataStart = -1, dataLength = 0;

                int i = 12;
                while (i + 8 <= wav.Length)
                {
                    string id = string.Empty + (char)wav[i] + (char)wav[i + 1] + (char)wav[i + 2] + (char)wav[i + 3];
                    int size = BitConverter.ToInt32(wav, i + 4);
                    int body = i + 8;
                    if (size < 0 || body + size > wav.Length) break;

                    if (id == "fmt ")
                    {
                        format = BitConverter.ToUInt16(wav, body);
                        channels = BitConverter.ToUInt16(wav, body + 2);
                        sampleRate = BitConverter.ToInt32(wav, body + 4);
                        bits = BitConverter.ToUInt16(wav, body + 14);
                    }
                    else if (id == "data")
                    {
                        dataStart = body;
                        dataLength = size;
                    }

                    i = body + size + (size & 1);   // chunks are word-aligned
                }

                if (dataStart < 0 || channels <= 0 || sampleRate <= 0)
                {
                    Core.Log.Warning("[Sound] WAV is missing fmt or data.");
                    return null;
                }

                int bytesPerSample = bits / 8;
                if (bytesPerSample <= 0) { Core.Log.Warning("[Sound] unsupported bit depth " + bits + "."); return null; }

                int sampleCount = dataLength / bytesPerSample;
                float[] samples = new float[sampleCount];

                for (int s = 0; s < sampleCount; s++)
                {
                    int o = dataStart + s * bytesPerSample;
                    if (format == 3 && bits == 32) samples[s] = BitConverter.ToSingle(wav, o);
                    else if (bits == 16) samples[s] = BitConverter.ToInt16(wav, o) / 32768f;
                    else if (bits == 32) samples[s] = BitConverter.ToInt32(wav, o) / 2147483648f;
                    else if (bits == 24) samples[s] = ((wav[o] | (wav[o + 1] << 8) | ((sbyte)wav[o + 2] << 16))) / 8388608f;
                    else if (bits == 8) samples[s] = (wav[o] - 128) / 128f;
                    else { Core.Log.Warning("[Sound] unsupported WAV format " + format + "/" + bits + "."); return null; }
                }

                AudioClip clip = AudioClip.Create(name, sampleCount / channels, channels, sampleRate, false);
                clip.SetData((Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<float>)samples, 0);
                clip.hideFlags |= HideFlags.DontUnloadUnusedAsset;

                Core.Log.Msg("[Sound] winch crank decoded (" + sampleRate + " Hz, " + channels + " ch, "
                    + (sampleCount / channels / (float)sampleRate).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " s).");
                return clip;
            }
            catch (Exception e)
            {
                Core.Log.Warning("[Sound] WAV decode failed: " + e.Message);
                return null;
            }
        }
    }
}
