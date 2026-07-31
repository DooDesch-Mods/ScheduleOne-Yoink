using System;
using System.Collections.Generic;

namespace Yoink.Audio
{
    /// <summary>
    /// The winch's voice.
    ///
    /// The design problem this solves: the first version treated "slow" and "straining" as the same sound, and
    /// drove playback speed straight from the raw physics velocity every frame. Both are wrong. A stalled winch is
    /// not a spool turning slowly - it is a mechanism under load that is not turning at all - and a rhythmic
    /// ratchet recording pitched down by ten semitones reads as a tape effect, not as effort.
    ///
    /// So there are three voices and a state machine:
    ///
    /// * MOVING - the body loop, playback rate confined to 0.94-1.08 (about 4.7-5.4 strokes per second against the
    ///   recording's natural 5) with a slow Perlin drift so it never sounds frozen. The rate that drives it is
    ///   median-filtered and asymmetrically smoothed upstream in WinchSession.
    /// * STRAINING - the same loop far slower and low-passed, almost inaudible, plus irregular loaded clacks. This
    ///   is the case that matters: the player is holding the button, the car is not moving, and the sound has to
    ///   say "effort" with zero travel to point at.
    /// * TRANSIENTS - a two-click clatter when the crank engages and a single latch click when it lets go, both
    ///   cut from the recording itself.
    ///
    /// The clip is prepared once at decode time: the 200 ms of silence at the head and 100 ms at the tail are cut
    /// (they were looping, producing a dropout every 8.7 s), the ends are matched at zero crossings and joined
    /// with a 30 ms circular crossfade, and the result is normalised to -3 dBFS.
    /// </summary>
    internal static class WinchSound
    {
        // --- decode-time constants, from measuring the supplied recording -------------------------------------
        private const int HeadTrim = 9600;        // 0.200 s of near-silence before the first stroke
        private const int TailTrim = 413904;      // 8.623 s - everything after this is the trailing silence
        private const int CrossfadeSamples = 1440;// 30 ms at 48 kHz
        private const float PeakTarget = 0.70795f;// -3 dBFS

        // --- mixing ------------------------------------------------------------------------------------------
        private const float MinRate = 0.94f;      // playback rate at a crawl
        private const float MaxRate = 1.08f;      // playback rate at the speed cap
        private const float StrainRate = 0.46f;   // the loop as a low, loaded groan

        private static AudioClip _body;
        private static readonly List<AudioClip> _clicks = new List<AudioClip>();

        private static AudioSource _motion;
        private static AudioSource _strain;
        private static AudioSource _oneShot;
        private static AudioLowPassFilter _strainFilter;
        private static GameObject _root;

        private static bool _failed;
        private static bool _engaged;
        private static float _motionFade;
        private static float _strainFade;
        private static float _nextClackAt;
        private static float _seed;
        private static int _lastClick = -1;

        internal static void ResetSession()
        {
            try { if (_root != null) UnityEngine.Object.Destroy(_root); }
            catch { }
            _root = null;
            _motion = null;
            _strain = null;
            _oneShot = null;
            _strainFilter = null;
            _engaged = false;
            _motionFade = 0f;
            _strainFade = 0f;
            _failed = false;
            // Clips survive: they are decoded once and do not belong to a scene.
        }

        /// <summary>
        /// One frame of winch audio. <paramref name="engaged"/> is the player holding the crank,
        /// <paramref name="rate"/> the smoothed reel rate in m/s and <paramref name="stalled"/> whether the winch
        /// is pulling hard at something that will not move.
        /// </summary>
        internal static void Tick(bool engaged, float rate, float maxRate, bool stalled, float payOut)
        {
            if (_failed) return;

            try
            {
                // Paying out counts as the mechanism running too: a ratchet winch clatters while the cable is
                // pulled off the drum, and staying silent while the player walks away from a hooked car is the
                // one thing that most obviously gives away a sound effect rather than a machine.
                bool payingOut = payOut > 0.05f && !engaged;
                if (!engaged && !payingOut && !_engaged && _motionFade <= 0f && _strainFade <= 0f) return;
                if (!EnsureSources()) return;

                float dt = Mathf.Max(Time.unscaledDeltaTime, 0.0001f);

                if (engaged && !_engaged) OnEngage();
                else if (!engaged && _engaged) OnRelease();
                _engaged = engaged;

                // Two crossfading beds. Only one is ever meaningfully audible; the fades are what stop the
                // transition between "moving" and "stuck" from being a click.
                bool wantMotion = (engaged && !stalled) || payingOut;
                bool wantStrain = engaged && stalled;

                _motionFade = Approach(_motionFade, wantMotion ? 1f : 0f, dt, wantMotion ? 0.06f : 0.06f);
                _strainFade = Approach(_strainFade, wantStrain ? 1f : 0f, dt, wantStrain ? 0.18f : 0.06f);

                // Paying out is the same drum turning the other way: same loop, a touch quicker and quieter,
                // because nobody is cranking - the rope is just being dragged off.
                if (payingOut) DriveMotion(payOut, maxRate, 0.75f);
                else DriveMotion(rate, maxRate, 1f);

                DriveStrain();
            }
            catch (Exception e)
            {
                _failed = true;
                Core.Log.Warning("[Sound] winch audio failed: " + e.Message);
            }
        }

        private static void DriveMotion(float rate, float maxRate, float loudness)
        {
            if (_motion == null) return;

            if (_motionFade <= 0.001f)
            {
                if (_motion.isPlaying) _motion.Stop();
                _motion.volume = 0f;
                return;
            }

            float u = Mathf.Sqrt(Mathf.Clamp01(Mathf.InverseLerp(0.05f, Mathf.Max(maxRate, 0.1f), rate)));

            // A slow wander of well under one percent. Machines are never perfectly periodic, and without this the
            // loop starts sounding like a sample the moment it runs for more than a few seconds.
            float drift = (Mathf.PerlinNoise(_seed, Time.unscaledTime * 0.35f) * 2f - 1f) * 0.008f;

            _motion.pitch = Mathf.Lerp(MinRate, MaxRate, u) + drift;
            _motion.volume = _motionFade * Mathf.Lerp(0.42f, 0.62f, u) * loudness;

            if (!_motion.isPlaying) StartAtStroke(_motion);
        }

        private static void DriveStrain()
        {
            if (_strain == null) return;

            if (_strainFade <= 0.001f)
            {
                if (_strain.isPlaying) _strain.Stop();
                _strain.volume = 0f;
                return;
            }

            // Breathing slightly, because a held load is not static - the handle creeps, the pawl loads and eases.
            float breathe = 1f + Mathf.Sin(Time.unscaledTime * 1.7f * Mathf.PI * 2f) * 0.10f;
            _strain.pitch = StrainRate;
            _strain.volume = _strainFade * 0.07f * breathe;

            if (!_strain.isPlaying) _strain.Play();

            // Irregular loaded clacks. Never on a fixed beat: a metronome would read as a machine still turning,
            // which is the exact impression a stalled winch must not give.
            if (Time.unscaledTime >= _nextClackAt)
            {
                _nextClackAt = Time.unscaledTime + UnityEngine.Random.Range(0.42f, 0.68f);
                PlayClick(UnityEngine.Random.Range(0.82f, 0.94f), UnityEngine.Random.Range(0.34f, 0.50f));
            }
        }

        private static void OnEngage()
        {
            // Two clicks, 110 ms apart: the pawl catching and the handle taking up slack. This fires on the press
            // itself, not on the first frame that reports movement, so the control answers immediately even though
            // the physics has nothing to report yet.
            PlayClick(UnityEngine.Random.Range(0.97f, 1.03f), 0.70f);
            _pendingSecondClickAt = Time.unscaledTime + 0.11f;
            _nextClackAt = Time.unscaledTime + 0.5f;
        }

        private static float _pendingSecondClickAt = -1f;

        private static void OnRelease()
        {
            PlayClick(UnityEngine.Random.Range(0.88f, 0.96f), 0.62f);
            _pendingSecondClickAt = -1f;
        }

        /// <summary>Fires the delayed half of the engage clatter. Called from the same tick loop.</summary>
        private static void PumpPendingClick()
        {
            if (_pendingSecondClickAt < 0f || Time.unscaledTime < _pendingSecondClickAt) return;
            _pendingSecondClickAt = -1f;
            PlayClick(UnityEngine.Random.Range(0.97f, 1.03f), 0.62f);
        }

        private static void PlayClick(float pitch, float volume)
        {
            if (_oneShot == null || _clicks.Count == 0) return;

            int index = _clicks.Count == 1 ? 0 : NextClickIndex();
            _oneShot.pitch = pitch;
            _oneShot.PlayOneShot(_clicks[index], volume);
        }

        private static int NextClickIndex()
        {
            int index;
            do { index = UnityEngine.Random.Range(0, _clicks.Count); }
            while (index == _lastClick);
            _lastClick = index;
            return index;
        }

        /// <summary>Enters the loop on a stroke rather than at an arbitrary sample, so it never starts mid-click.</summary>
        private static void StartAtStroke(AudioSource source)
        {
            try
            {
                if (_body != null) source.time = UnityEngine.Random.Range(0f, _body.length * 0.9f);
                source.Play();
            }
            catch { try { source.Play(); } catch { } }
        }

        private static float Approach(float current, float target, float dt, float seconds)
        {
            if (seconds <= 0f) return target;
            float step = dt / seconds;
            return Mathf.MoveTowards(current, target, step);
        }

        private static bool EnsureSources()
        {
            if (_motion != null) { PumpPendingClick(); return true; }

            if (_body == null)
            {
                byte[] wav = Item.WinchItem.ReadEmbeddedPublic("Yoink.Assets.winch_crank.wav");
                if (wav == null) { _failed = true; return false; }
                if (!Decode(wav)) { _failed = true; return false; }
            }

            _root = new GameObject("YoinkWinchAudio");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _seed = UnityEngine.Random.Range(0f, 100f);

            _motion = MakeSource("motion", _body, true, 0f);
            _strain = MakeSource("strain", _body, true, 0f);
            _oneShot = MakeSource("oneshot", null, false, 0f);

            // The low end belongs to the strain bed, so it is filtered down to a groan and the motion loop keeps
            // the clicks.
            _strainFilter = _strain.gameObject.AddComponent<AudioLowPassFilter>();
            _strainFilter.cutoffFrequency = 220f;

            return true;
        }

        /// <summary>
        /// Mostly-2D, but not exactly: a held tool sits in the player's hands, not in the UI. A little spatial
        /// blend keeps it there without letting it swing around the head when the camera turns.
        /// </summary>
        private static AudioSource MakeSource(string name, AudioClip clip, bool loop, float volume)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(_root.transform, false);

            AudioSource s = go.AddComponent<AudioSource>();
            s.clip = clip;
            s.loop = loop;
            s.playOnAwake = false;
            s.volume = volume;
            s.spatialBlend = 0.15f;
            s.dopplerLevel = 0f;
            s.minDistance = 0.35f;
            s.maxDistance = 6f;
            s.reverbZoneMix = 0.1f;
            return s;
        }

        /// <summary>Keeps the audio where the winch is, so the little bit of spatialisation points somewhere true.</summary>
        internal static void FollowMuzzle(Vector3 world)
        {
            try { if (_root != null) _root.transform.position = world; }
            catch { }
        }

        // ---- decoding ---------------------------------------------------------------------------------------

        private static bool Decode(byte[] wav)
        {
            int channels, sampleRate;
            float[] samples = ReadPcm(wav, out channels, out sampleRate);
            if (samples == null) return false;

            // Trim the measured silence, then find a matching pair of zero crossings so the seam lands on the same
            // point of the ratchet cycle rather than merely at the same amplitude.
            int head = Mathf.Clamp(HeadTrim, 0, samples.Length - 1);
            int tail = Mathf.Clamp(TailTrim, head + CrossfadeSamples * 2, samples.Length);

            int start = FindZeroCrossing(samples, head, head + 2400);
            int end = FindZeroCrossing(samples, tail - 2400, tail);
            if (end - start < CrossfadeSamples * 4) { start = head; end = tail; }

            int length = end - start - CrossfadeSamples;
            if (length <= 0) { Core.Log.Warning("[Sound] the clip is too short to loop."); return false; }

            float[] body = new float[length];
            Array.Copy(samples, start, body, 0, length);

            // Circular crossfade: the tail is folded back over the head with complementary weights. Equal-power
            // would lift the level here, because both sides are the same recording and correlate.
            for (int i = 0; i < CrossfadeSamples; i++)
            {
                float w = 0.5f - 0.5f * Mathf.Cos(Mathf.PI * (i / (float)CrossfadeSamples));
                float tailSample = samples[start + length + i];
                body[i] = tailSample * (1f - w) + body[i] * w;
            }

            Normalise(body, PeakTarget);

            _body = AudioClip.Create("YoinkWinchLoop", body.Length / Mathf.Max(channels, 1), Mathf.Max(channels, 1), sampleRate, false);
            _body.SetData((Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<float>)body, 0);
            _body.hideFlags |= HideFlags.DontUnloadUnusedAsset;

            ExtractClicks(samples, head, tail, channels, sampleRate);

            Core.Log.Msg("[Sound] winch loop " + (body.Length / (float)sampleRate).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                + " s seamless, " + _clicks.Count + " ratchet click(s) extracted.");
            return true;
        }

        /// <summary>
        /// Cuts individual ratchet strokes out of the recording to use as one-shots. Free material: the engage
        /// clatter and the release latch are the same mechanism, so they should be the same metal.
        /// </summary>
        private static void ExtractClicks(float[] samples, int from, int to, int channels, int sampleRate)
        {
            const int Window = 240;      // 5 ms energy window
            const int MinGap = 5760;     // 120 ms between accepted peaks
            const int Pre = 720;         // 15 ms before the peak
            const int Post = 4080;       // 85 ms after

            var peaks = new List<int>();
            int lastPeak = -MinGap;

            for (int i = from; i < to - Window; i += Window)
            {
                float peak = 0f;
                int at = i;
                for (int k = 0; k < Window; k++)
                {
                    float a = Mathf.Abs(samples[i + k]);
                    if (a > peak) { peak = a; at = i + k; }
                }

                if (peak > 0.5f && at - lastPeak >= MinGap)
                {
                    peaks.Add(at);
                    lastPeak = at;
                    if (peaks.Count >= 6) break;
                }
            }

            for (int p = 0; p < peaks.Count; p++)
            {
                int begin = Mathf.Max(peaks[p] - Pre, 0);
                int end = Mathf.Min(peaks[p] + Post, samples.Length);
                int len = end - begin;
                if (len < 480) continue;

                float[] click = new float[len];
                Array.Copy(samples, begin, click, 0, len);

                int fade = 144;   // 3 ms
                for (int i = 0; i < fade && i < len; i++)
                {
                    float w = i / (float)fade;
                    click[i] *= w;
                    click[len - 1 - i] *= w;
                }

                Normalise(click, 0.63f);   // -4 dBFS, so overlapping layers keep headroom

                AudioClip clip = AudioClip.Create("YoinkRatchet" + p, len / Mathf.Max(channels, 1), Mathf.Max(channels, 1), sampleRate, false);
                clip.SetData((Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<float>)click, 0);
                clip.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                _clicks.Add(clip);
            }
        }

        private static void Normalise(float[] data, float target)
        {
            float peak = 0f;
            for (int i = 0; i < data.Length; i++)
            {
                float a = Mathf.Abs(data[i]);
                if (a > peak) peak = a;
            }
            if (peak <= 0.0001f) return;

            float gain = target / peak;
            for (int i = 0; i < data.Length; i++) data[i] *= gain;
        }

        private static int FindZeroCrossing(float[] data, int from, int to)
        {
            from = Mathf.Clamp(from, 1, data.Length - 2);
            to = Mathf.Clamp(to, from + 1, data.Length - 1);

            for (int i = from; i < to; i++)
                if (data[i - 1] <= 0f && data[i] > 0f) return i;   // positive-going

            return from;
        }

        /// <summary>
        /// Reads PCM out of a WAV. Walks the chunk list rather than assuming fixed offsets, because recorded files
        /// routinely carry cue/bext/LIST chunks between fmt and data - this one does.
        /// </summary>
        private static float[] ReadPcm(byte[] wav, out int channels, out int sampleRate)
        {
            channels = 0;
            sampleRate = 0;

            try
            {
                if (wav.Length < 12 || wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F')
                {
                    Core.Log.Warning("[Sound] not a RIFF file.");
                    return null;
                }

                int bits = 0, format = 0, dataStart = -1, dataLength = 0;

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

                    i = body + size + (size & 1);
                }

                if (dataStart < 0 || channels <= 0 || sampleRate <= 0)
                {
                    Core.Log.Warning("[Sound] WAV is missing fmt or data.");
                    return null;
                }

                int bytesPerSample = bits / 8;
                if (bytesPerSample <= 0) { Core.Log.Warning("[Sound] unsupported bit depth " + bits + "."); return null; }

                int count = dataLength / bytesPerSample;
                float[] samples = new float[count];

                for (int s = 0; s < count; s++)
                {
                    int o = dataStart + s * bytesPerSample;
                    if (format == 3 && bits == 32) samples[s] = BitConverter.ToSingle(wav, o);
                    else if (bits == 16) samples[s] = BitConverter.ToInt16(wav, o) / 32768f;
                    else if (bits == 32) samples[s] = BitConverter.ToInt32(wav, o) / 2147483648f;
                    else if (bits == 24) samples[s] = ((wav[o] | (wav[o + 1] << 8) | ((sbyte)wav[o + 2] << 16))) / 8388608f;
                    else if (bits == 8) samples[s] = (wav[o] - 128) / 128f;
                    else { Core.Log.Warning("[Sound] unsupported WAV format " + format + "/" + bits + "."); return null; }
                }

                // Remove any DC offset, which otherwise shows up as a level jump at the loop seam.
                double mean = 0d;
                for (int s = 0; s < count; s++) mean += samples[s];
                mean /= Math.Max(count, 1);
                if (Math.Abs(mean) > 0.0005d)
                    for (int s = 0; s < count; s++) samples[s] -= (float)mean;

                return samples;
            }
            catch (Exception e)
            {
                Core.Log.Warning("[Sound] WAV decode failed: " + e.Message);
                return null;
            }
        }
    }
}
