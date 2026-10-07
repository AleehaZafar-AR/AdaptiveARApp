// File: SpeechLibrary.cs
// Spoken instructions and feedback, looked up by the exact text they speak.
//
// The clips live in Assets/Resources/AdaptiveAR/Speech and are named by a key derived
// from the sentence (see KeyFor). The runtime asks for a clip with the SAME string it
// shows on the card, so the visual and spoken instruction cannot drift apart: if the
// wording changes, the clip for the old wording is simply not found and a warning
// names the missing key. The clips were synthesised offline with a fixed voice, so
// every participant hears identical audio.
//
// Playback rules live in InstructionSpeech: an instruction is spoken once when its
// action is entered, never per frame and never again on a support-level re-render.

using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace AdaptiveAR.Audio
{
    public static class SpeechLibrary
    {
        public const string ResourcesFolder = "AdaptiveAR/Speech/";

        private static readonly Dictionary<string, AudioClip> _cache = new Dictionary<string, AudioClip>();
        private static readonly HashSet<string> _warned = new HashSet<string>();

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

        /// <summary>The clip for a sentence, or null (warned once per key).</summary>
        public static AudioClip ClipFor(string text)
        {
            string key = KeyFor(text);
            if (key.Length == 0) return null;

            if (_cache.TryGetValue(key, out AudioClip cached)) return cached;

            AudioClip clip = Resources.Load<AudioClip>(ResourcesFolder + key);
            _cache[key] = clip;

            if (clip == null && _warned.Add(key))
                Debug.LogWarning($"[Speech] No clip for \"{text}\" (expected Resources/{ResourcesFolder}{key}.wav).");

            return clip;
        }

        public static bool Has(string text)
        {
            return ClipFor(text) != null;
        }
    }

    /// <summary>
    /// Plays speech through one AudioSource. Instructions replace whatever is speaking;
    /// short feedback is layered so it never cuts an instruction.
    /// </summary>
    public class InstructionSpeech : MonoBehaviour
    {
        [Tooltip("Scene AudioSource used for all speech. Found or created when empty.")]
        [SerializeField] private AudioSource source;

        [Tooltip("Master switch, e.g. for a silent condition.")]
        [SerializeField] private bool enabledSpeech = true;

        [Tooltip("Seconds within which the same sentence is not spoken again.")]
        [SerializeField] private float repeatGuardSeconds = 1.0f;

        private string _lastText;
        private float _lastAt = -999f;

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
            if (source == null) source = GetComponent<AudioSource>();
            if (source == null) source = FindAnyObjectByType<AudioSource>(FindObjectsInactive.Include);
            if (source == null)
            {
                source = gameObject.AddComponent<AudioSource>();
                source.spatialBlend = 0f;
                source.playOnAwake = false;
            }
        }

        /// <summary>Speaks an instruction once, replacing any instruction still playing.</summary>
        public bool SpeakInstruction(string text)
        {
            if (!enabledSpeech || source == null || string.IsNullOrEmpty(text)) return false;

            if (text == _lastText && Time.time - _lastAt < repeatGuardSeconds) return false;

            AudioClip clip = SpeechLibrary.ClipFor(text);
            if (clip == null) return false;

            source.Stop();
            source.clip = clip;
            source.Play();

            _lastText = text;
            _lastAt = Time.time;
            return true;
        }

        /// <summary>Speaks a short feedback phrase on top of whatever is playing.</summary>
        public bool SpeakFeedback(string text)
        {
            if (!enabledSpeech || source == null || string.IsNullOrEmpty(text)) return false;

            AudioClip clip = SpeechLibrary.ClipFor(text);
            if (clip == null) return false;

            source.PlayOneShot(clip);
            return true;
        }

        /// <summary>Plays an authored clip (a stage's recorded instruction) as an instruction.</summary>
        public void PlayAuthored(AudioClip clip)
        {
            if (!enabledSpeech || source == null || clip == null) return;
            source.Stop();
            source.clip = clip;
            source.Play();
            _lastText = null;
        }
    }
}
