// File: SpeechLibrary.cs
// Spoken instructions and feedback, looked up by the exact text they speak.
//
// The clips live in Assets/Resources/AdaptiveAR/Speech and are named by a key derived
// from the sentence (see KeyFor). The runtime asks for a clip with the SAME string it
// shows on the card, so the visual and spoken instruction cannot drift apart.
//
// Why the first version was silent on the headset
// ------------------------------------------------
// The clips are imported with "Preload Audio Data" off (the importer default this
// project inherited from its one authored mp3). A clip loaded that way has no audio
// data until AudioClip.LoadAudioData() is called; AudioSource.PlayOneShot on such a
// clip does nothing, and Play() may or may not fetch the data in time. On top of
// that, every instruction called AudioSource.Stop() on the ONE scene source before
// playing, which also stops any one-shot ("Correct.") still playing on it - and the
// next instruction always followed a success within the same frame.
//
// Now: every clip is loaded once at startup and its load state is checked before
// each play; instructions and feedback use two separate 2-D sources, so neither can
// stop the other; an instruction that follows feedback waits for it to finish; and
// every request is logged (text -> key -> found -> load state -> source -> play).

using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace AdaptiveAR.Audio
{
    public static class SpeechLibrary
    {
        public const string ResourcesFolder = "AdaptiveAR/Speech";

        private static readonly Dictionary<string, AudioClip> _cache = new Dictionary<string, AudioClip>();
        private static readonly HashSet<string> _warned = new HashSet<string>();
        private static bool _preloaded;

        /// <summary>
        /// Key for a sentence: lower case, runs of non-alphanumerics become one underscore,
        /// trimmed, at most 60 characters. Mirrors the generator script exactly.
        /// </summary>
        public static string KeyFor(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            var sb = new StringBuilder(text.Length);
            bool lastUnderscore = false;
            foreach (char ch in text.ToLowerInvariant())
            {
                bool alnum = (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9');
                if (alnum) { sb.Append(ch); lastUnderscore = false; }
                else if (!lastUnderscore) { sb.Append('_'); lastUnderscore = true; }
            }

            string key = sb.ToString().Trim('_');
            if (key.Length > 60) key = key.Substring(0, 60).TrimEnd('_');
            return key;
        }

        /// <summary>
        /// Loads every clip in the Resources folder once and starts loading their audio
        /// data. Logs how many were found: zero on the device means the folder is not in
        /// the build, which is the first thing to rule out.
        /// </summary>
        public static int Preload()
        {
            if (_preloaded) return _cache.Count;
            _preloaded = true;

            AudioClip[] all = Resources.LoadAll<AudioClip>(ResourcesFolder);
            foreach (AudioClip c in all)
            {
                if (c == null) continue;
                _cache[c.name] = c;
                if (c.loadState == AudioDataLoadState.Unloaded) c.LoadAudioData();
            }

            Debug.Log($"[Speech] Preload: {all.Length} clip(s) found in Resources/{ResourcesFolder}" +
                      (all.Length == 0 ? "  <-- NONE: the clips are not in this build" : ""));
            return all.Length;
        }

        /// <summary>The clip for a sentence, or null (warned once per key).</summary>
        public static AudioClip ClipFor(string text)
        {
            string key = KeyFor(text);
            if (key.Length == 0) return null;

            Preload();

            if (!_cache.TryGetValue(key, out AudioClip clip) || clip == null)
            {
                clip = Resources.Load<AudioClip>(ResourcesFolder + "/" + key);
                _cache[key] = clip;
            }

            if (clip == null && _warned.Add(key))
                Debug.LogWarning($"[Speech] No clip for \"{text}\" (expected Resources/{ResourcesFolder}/{key}.wav).");

            return clip;
        }

        public static bool Has(string text)
        {
            return ClipFor(text) != null;
        }
    }

    /// <summary>
    /// Plays speech through two dedicated 2-D sources: one for instructions (replaced
    /// when a new instruction arrives) and one for short feedback (never cut by an
    /// instruction). An instruction requested while feedback is playing waits for it.
    /// </summary>
    public class InstructionSpeech : MonoBehaviour
    {
        [Tooltip("Scene AudioSource the authored clips used. Kept for reference; speech now " +
                 "plays on two dedicated 2-D sources created on this object.")]
        [SerializeField] private AudioSource source;

        [Tooltip("Master switch, e.g. for a silent condition.")]
        [SerializeField] private bool enabledSpeech = true;

        [Tooltip("Seconds within which the same sentence is not spoken again.")]
        [SerializeField] private float repeatGuardSeconds = 1.0f;

        [Tooltip("Longest an instruction waits for playing feedback before starting anyway.")]
        [SerializeField] private float maxWaitForFeedbackSeconds = 2.0f;

        [Range(0f, 1f)]
        [SerializeField] private float volume = 1f;

        private AudioSource _instructionSource;
        private AudioSource _feedbackSource;
        private string _lastText;
        private float _lastAt = -999f;
        private Coroutine _pending;
        private int _requestId;

        public bool SpeechEnabled { get { return enabledSpeech; } set { enabledSpeech = value; } }

        public static InstructionSpeech Ensure(AudioSource preferred)
        {
            var existing = FindAnyObjectByType<InstructionSpeech>(FindObjectsInactive.Include);
            if (existing != null)
            {
                if (existing.source == null && preferred != null) existing.source = preferred;
                return existing;
            }

            GameObject host = preferred != null ? preferred.gameObject : new GameObject("InstructionSpeech");
            var created = host.AddComponent<InstructionSpeech>();
            created.source = preferred;
            return created;
        }

        private void Awake()
        {
            _instructionSource = MakeSource("instruction");
            _feedbackSource = MakeSource("feedback");
            SpeechLibrary.Preload();
        }

        private AudioSource MakeSource(string label)
        {
            var s = gameObject.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.loop = false;
            s.spatialBlend = 0f;        // 2-D: audible wherever the participant is
            s.volume = volume;
            s.priority = 0;             // never culled by other sounds
            if (source != null && source.outputAudioMixerGroup != null)
                s.outputAudioMixerGroup = source.outputAudioMixerGroup;
            return s;
        }

        /// <summary>Speaks an instruction once, replacing any instruction still playing.</summary>
        public bool SpeakInstruction(string text)
        {
            if (!enabledSpeech || string.IsNullOrEmpty(text)) return false;

            if (text == _lastText && Time.time - _lastAt < repeatGuardSeconds)
            {
                Debug.Log($"[Speech] instruction \"{text}\" skipped: repeat guard.");
                return false;
            }

            AudioClip clip = SpeechLibrary.ClipFor(text);
            Debug.Log($"[Speech] instruction request \"{text}\" -> key '{SpeechLibrary.KeyFor(text)}' -> " +
                      (clip != null ? $"found, loadState={clip.loadState}, {clip.length:F1}s" : "NOT FOUND"));
            if (clip == null) return false;

            _lastText = text;
            _lastAt = Time.time;
            StartInstruction(clip, text);
            return true;
        }

        /// <summary>Plays an authored clip (a stage's recorded instruction) as an instruction.</summary>
        public void PlayAuthored(AudioClip clip)
        {
            if (!enabledSpeech || clip == null) return;
            Debug.Log($"[Speech] authored clip '{clip.name}' requested, loadState={clip.loadState}");
            _lastText = null;
            StartInstruction(clip, "<authored:" + clip.name + ">");
        }

        /// <summary>Speaks a short feedback phrase on its own source; never cut by an instruction.</summary>
        public bool SpeakFeedback(string text)
        {
            if (!enabledSpeech || string.IsNullOrEmpty(text)) return false;

            AudioClip clip = SpeechLibrary.ClipFor(text);
            Debug.Log($"[Speech] feedback request \"{text}\" -> key '{SpeechLibrary.KeyFor(text)}' -> " +
                      (clip != null ? $"found, loadState={clip.loadState}" : "NOT FOUND"));
            if (clip == null) return false;

            StartCoroutine(PlayWhenLoaded(_feedbackSource, clip, text, oneShot: true, waitForFeedback: false, id: ++_requestId));
            return true;
        }

        private void StartInstruction(AudioClip clip, string label)
        {
            if (_pending != null) StopCoroutine(_pending);
            _pending = StartCoroutine(PlayWhenLoaded(_instructionSource, clip, label, oneShot: false, waitForFeedback: true, id: ++_requestId));
        }

        private IEnumerator PlayWhenLoaded(AudioSource target, AudioClip clip, string label, bool oneShot, bool waitForFeedback, int id)
        {
            if (target == null) yield break;

            // Audio data must be loaded before a clip can play; with "Preload Audio Data"
            // off that only happens on request.
            if (clip.loadState == AudioDataLoadState.Unloaded) clip.LoadAudioData();
            float t0 = Time.time;
            while (clip.loadState == AudioDataLoadState.Loading && Time.time - t0 < 3f)
                yield return null;

            if (clip.loadState != AudioDataLoadState.Loaded)
            {
                Debug.LogWarning($"[Speech] \"{label}\": clip '{clip.name}' not loaded (state {clip.loadState}); not played.");
                yield break;
            }

            // Let "Correct." finish before the next instruction starts over it.
            if (waitForFeedback && _feedbackSource != null)
            {
                float w0 = Time.time;
                while (_feedbackSource.isPlaying && Time.time - w0 < maxWaitForFeedbackSeconds)
                    yield return null;
            }

            target.volume = volume;
            if (oneShot)
            {
                target.PlayOneShot(clip);
                Debug.Log($"[Speech] PlayOneShot '{clip.name}' on {target.gameObject.name}/feedback ({label}).");
            }
            else
            {
                target.Stop();
                target.clip = clip;
                target.Play();
                Debug.Log($"[Speech] Play '{clip.name}' on {target.gameObject.name}/instruction ({label}), isPlaying={target.isPlaying}.");
            }
        }
    }
}
