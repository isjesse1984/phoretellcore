using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

namespace Phoretell
{
    public enum AudioPlaybackCategory
    {
        None,
        Effect,
        Dialogue
    }

    /// <summary>
    /// Identifies one pooled effect playback. Handles become invalid when their
    /// sound finishes or the pooled voice is reused.
    /// </summary>
    public readonly struct AudioPlaybackHandle : IEquatable<AudioPlaybackHandle>
    {
        internal readonly int VoiceIndex;
        internal readonly uint Generation;
        public AudioPlaybackCategory Category { get; }

        internal AudioPlaybackHandle(
            AudioPlaybackCategory category,
            int voiceIndex,
            uint generation)
        {
            Category = category;
            VoiceIndex = voiceIndex;
            Generation = generation;
        }

        public bool IsValid =>
            Category != AudioPlaybackCategory.None &&
            VoiceIndex >= 0 &&
            Generation != 0;

        public static AudioPlaybackHandle Invalid =>
            new AudioPlaybackHandle(AudioPlaybackCategory.None, -1, 0);

        public bool Equals(AudioPlaybackHandle other)
        {
            return Category == other.Category &&
                   VoiceIndex == other.VoiceIndex &&
                   Generation == other.Generation;
        }

        public override bool Equals(object obj)
        {
            return obj is AudioPlaybackHandle other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = (int)Category;
                hashCode = (hashCode * 397) ^ VoiceIndex;
                hashCode = (hashCode * 397) ^ (int)Generation;
                return hashCode;
            }
        }

        public static bool operator ==(
            AudioPlaybackHandle left,
            AudioPlaybackHandle right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(
            AudioPlaybackHandle left,
            AudioPlaybackHandle right)
        {
            return !left.Equals(right);
        }
    }

    /// <summary>
    /// Persistent, mixer-ready audio service for 2D and 3D games.
    ///
    /// Music and ambience each use two sources for crossfading. Effects use a
    /// bounded, prewarmed pool with priority-based voice stealing.
    /// </summary>
    [DefaultExecutionOrder(-900)]
    public sealed class AudioHandler : MonoBehaviour
    {
        private const int HighestPriority = 0;
        private const int LowestPriority = 256;

        [Header("Optional Mixer Routing")]
        [SerializeField] private AudioMixerGroup musicOutput;
        [SerializeField] private AudioMixerGroup ambientOutput;
        [SerializeField] private AudioMixerGroup effectsOutput;
        [SerializeField] private AudioMixerGroup dialogueOutput;

        [Header("Music")]
        [SerializeField] private AudioSource musicAudioSource;
        [SerializeField] private AudioSource musicCrossfadeAudioSource;
        [Range(0f, 1f)]
        [SerializeField] private float musicVolume = 1f;
        [Min(0f)]
        [SerializeField] private float defaultMusicCrossfadeSeconds = 1f;
        [SerializeField] private bool musicLoops = true;

        [Header("Ambience")]
        [SerializeField] private AudioSource ambientAudioSource;
        [SerializeField] private AudioSource ambientCrossfadeAudioSource;
        [Range(0f, 1f)]
        [SerializeField] private float ambientVolume = 1f;
        [Min(0f)]
        [SerializeField] private float defaultAmbientCrossfadeSeconds = 1f;
        [SerializeField] private bool ambientLoops = true;

        [Header("Effect Pool")]
        [Tooltip("A ready-to-use effect voice. Additional voices are created automatically.")]
        [SerializeField] private AudioSource effectAudioSource;
        [SerializeField] private Transform effectPoolRoot;
        [Min(1)]
        [SerializeField] private int initialEffectPoolSize = 16;
        [Min(1)]
        [SerializeField] private int maximumEffectPoolSize = 48;
        [SerializeField] private bool allowPoolGrowth = true;
        [SerializeField] private bool allowVoiceStealing = true;
        [Range(0f, 1f)]
        [SerializeField] private float effectsVolume = 1f;

        [Header("Default 3D Effect Settings")]
        [Min(0f)]
        [SerializeField] private float defaultMinDistance = 1f;
        [Min(0.01f)]
        [SerializeField] private float defaultMaxDistance = 30f;
        [Range(0f, 5f)]
        [SerializeField] private float defaultDopplerLevel = 1f;
        [SerializeField] private AudioRolloffMode defaultRolloffMode =
            AudioRolloffMode.Logarithmic;

        [Header("Dialogue Pool")]
        [Tooltip("Used for voice-over and spatial character dialogue.")]
        [SerializeField] private AudioSource dialogueAudioSource;
        [SerializeField] private Transform dialoguePoolRoot;
        [Min(1)]
        [SerializeField] private int initialDialoguePoolSize = 4;
        [Min(1)]
        [SerializeField] private int maximumDialoguePoolSize = 12;
        [SerializeField] private bool allowDialoguePoolGrowth = true;
        [SerializeField] private bool allowDialogueVoiceStealing = true;
        [Range(0f, 1f)]
        [SerializeField] private float dialogueVolume = 1f;
        [Min(0f)]
        [SerializeField] private float dialogueMinDistance = 1f;
        [Min(0.01f)]
        [SerializeField] private float dialogueMaxDistance = 20f;

        [Header("Automatic Dialogue Ducking")]
        [Range(0f, 1f)]
        [SerializeField] private float musicVolumeDuringDialogue = 0.4f;
        [Range(0f, 1f)]
        [SerializeField] private float ambientVolumeDuringDialogue = 0.65f;
        [Min(0f)]
        [SerializeField] private float dialogueDuckSeconds = 0.25f;
        [Min(0f)]
        [SerializeField] private float dialogueRestoreSeconds = 0.5f;

        [Header("Global State")]
        [SerializeField] private bool muted;

        private static AudioHandler instance;
        private static bool hasLoggedMissingInstance;

        private readonly List<EffectVoice> effectVoices =
            new List<EffectVoice>();
        private readonly List<EffectVoice> dialogueVoices =
            new List<EffectVoice>();

        private LayerState musicLayer;
        private LayerState ambientLayer;
        private Coroutine dialogueDuckTransition;
        private AudioPlaybackHandle currentVoiceOver =
            AudioPlaybackHandle.Invalid;
        private int activeDialogueCount;
        private bool initialized;
        private bool globallyPaused;

        public event Action<AudioPlaybackHandle> DialogueStarted;
        public event Action<AudioPlaybackHandle> DialogueEnded;

        public static AudioHandler Instance
        {
            get
            {
                if (instance == null)
                {
                    instance = FindFirstObjectByType<AudioHandler>();
                }

                if (instance == null && !hasLoggedMissingInstance)
                {
                    Debug.LogError(
                        $"{nameof(AudioHandler)} instance was not found in the loaded scenes.");
                    hasLoggedMissingInstance = true;
                }

                return instance;
            }
        }

        public static bool HasInstance => instance != null;

        public static bool TryGetInstance(out AudioHandler foundInstance)
        {
            if (instance == null)
            {
                instance = FindFirstObjectByType<AudioHandler>();
            }

            foundInstance = instance;
            return foundInstance != null;
        }

        public AudioSource MusicAudioSource =>
            musicLayer?.Active ?? musicAudioSource;
        public AudioSource AmbientAudioSource =>
            ambientLayer?.Active ?? ambientAudioSource;
        public AudioSource EffectAudioSource => effectAudioSource;
        public AudioSource DialogueAudioSource => dialogueAudioSource;

        // Compatibility alias: the old background channel is the music channel.
        public AudioSource BackgroundAudioSource => MusicAudioSource;

        public AudioClip CurrentMusicClip => MusicAudioSource?.clip;
        public AudioClip CurrentAmbientClip => AmbientAudioSource?.clip;
        public AudioClip CurrentBackgroundClip => CurrentMusicClip;

        public float MusicVolume => musicVolume;
        public float AmbientVolume => ambientVolume;
        public float EffectsVolume => effectsVolume;
        public float DialogueVolume => dialogueVolume;
        public float BackgroundVolume => musicVolume;

        public int EffectPoolSize => effectVoices.Count;
        public int DialoguePoolSize => dialogueVoices.Count;
        public int ActiveEffectCount
        {
            get
            {
                int count = 0;
                foreach (EffectVoice voice in effectVoices)
                {
                    if (voice.InUse)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        public int ActiveDialogueCount => activeDialogueCount;

        public bool IsMuted => muted;
        public bool IsMusicPlaying => IsLayerPlaying(musicLayer);
        public bool IsAmbientPlaying => IsLayerPlaying(ambientLayer);
        public bool IsBackgroundPlaying => IsMusicPlaying;

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                Debug.LogWarning(
                    $"Duplicate {nameof(AudioHandler)} found on '{gameObject.name}'. " +
                    "The duplicate GameObject will be destroyed.",
                    this);
                Destroy(gameObject);
                return;
            }

            instance = this;
            hasLoggedMissingInstance = false;
            DontDestroyOnLoad(gameObject);

            Initialize();
        }

        private void LateUpdate()
        {
            if (!initialized)
            {
                return;
            }

            UpdatePooledVoices(effectVoices);
            UpdatePooledVoices(dialogueVoices);
        }

        #region Music and ambience

        public void PlayMusic(AudioClip clip)
        {
            TryPlayMusic(clip, defaultMusicCrossfadeSeconds, false);
        }

        public bool TryPlayMusic(
            AudioClip clip,
            bool restartIfPlaying = false)
        {
            return TryPlayMusic(
                clip,
                defaultMusicCrossfadeSeconds,
                restartIfPlaying);
        }

        public bool TryPlayMusic(
            AudioClip clip,
            float crossfadeSeconds,
            bool restartIfPlaying = false)
        {
            return Initialize() &&
                   TryPlayLayer(
                       musicLayer,
                       clip,
                       crossfadeSeconds,
                       restartIfPlaying);
        }

        public void PlayAmbientAudio(AudioClip clip)
        {
            TryPlayAmbientAudio(
                clip,
                defaultAmbientCrossfadeSeconds,
                false);
        }

        public bool TryPlayAmbientAudio(
            AudioClip clip,
            bool restartIfPlaying = false)
        {
            return TryPlayAmbientAudio(
                clip,
                defaultAmbientCrossfadeSeconds,
                restartIfPlaying);
        }

        public bool TryPlayAmbientAudio(
            AudioClip clip,
            float crossfadeSeconds,
            bool restartIfPlaying = false)
        {
            return Initialize() &&
                   TryPlayLayer(
                       ambientLayer,
                       clip,
                       crossfadeSeconds,
                       restartIfPlaying);
        }

        public void StopMusic(bool clearClip = false)
        {
            StopLayer(musicLayer, 0f, clearClip);
        }

        public void StopMusic(float fadeSeconds, bool clearClip = false)
        {
            StopLayer(musicLayer, fadeSeconds, clearClip);
        }

        public void StopAmbientAudio(bool clearClip = false)
        {
            StopLayer(ambientLayer, 0f, clearClip);
        }

        public void StopAmbientAudio(
            float fadeSeconds,
            bool clearClip = false)
        {
            StopLayer(ambientLayer, fadeSeconds, clearClip);
        }

        public void PauseMusic()
        {
            PauseLayer(musicLayer);
        }

        public void ResumeMusic()
        {
            ResumeLayer(musicLayer);
        }

        public void PauseAmbientAudio()
        {
            PauseLayer(ambientLayer);
        }

        public void ResumeAmbientAudio()
        {
            ResumeLayer(ambientLayer);
        }

        public void SetMusicVolume(float volume)
        {
            musicVolume = Mathf.Clamp01(volume);
            SetLayerVolume(musicLayer, musicVolume);
        }

        public void SetAmbientVolume(float volume)
        {
            ambientVolume = Mathf.Clamp01(volume);
            SetLayerVolume(ambientLayer, ambientVolume);
        }

        public void SetMusicLooping(bool shouldLoop)
        {
            musicLoops = shouldLoop;
            SetLayerLooping(musicLayer, shouldLoop);
        }

        public void SetAmbientLooping(bool shouldLoop)
        {
            ambientLoops = shouldLoop;
            SetLayerLooping(ambientLayer, shouldLoop);
        }

        #endregion

        #region Pooled effects

        /// <summary>
        /// Compatibility method. Plays a pooled, non-positional effect.
        /// </summary>
        public void PlayEffectAudio(AudioClip clip)
        {
            PlayEffect2D(clip);
        }

        public bool TryPlayEffectAudio(AudioClip clip, float volumeScale = 1f)
        {
            return PlayEffect2D(clip, volumeScale).IsValid;
        }

        public AudioPlaybackHandle PlayEffect2D(
            AudioClip clip,
            float volumeScale = 1f,
            float pitch = 1f,
            bool loop = false,
            int priority = 128)
        {
            return PlayEffectInternal(
                clip,
                transform.position,
                null,
                Vector3.zero,
                0f,
                volumeScale,
                pitch,
                loop,
                priority,
                defaultMinDistance,
                defaultMaxDistance,
                defaultDopplerLevel,
                defaultRolloffMode);
        }

        public AudioPlaybackHandle PlayEffectAtPosition(
            AudioClip clip,
            Vector3 worldPosition,
            float volumeScale = 1f,
            float pitch = 1f,
            bool loop = false,
            int priority = 128)
        {
            return PlayEffectInternal(
                clip,
                worldPosition,
                null,
                Vector3.zero,
                1f,
                volumeScale,
                pitch,
                loop,
                priority,
                defaultMinDistance,
                defaultMaxDistance,
                defaultDopplerLevel,
                defaultRolloffMode);
        }

        public AudioPlaybackHandle PlayEffectFollowing(
            AudioClip clip,
            Transform target,
            Vector3 localOffset,
            float volumeScale = 1f,
            float pitch = 1f,
            bool loop = false,
            int priority = 128)
        {
            if (target == null)
            {
                return AudioPlaybackHandle.Invalid;
            }

            return PlayEffectInternal(
                clip,
                target.TransformPoint(localOffset),
                target,
                localOffset,
                1f,
                volumeScale,
                pitch,
                loop,
                priority,
                defaultMinDistance,
                defaultMaxDistance,
                defaultDopplerLevel,
                defaultRolloffMode);
        }

        /// <summary>
        /// Fully configurable pooled effect playback for advanced 2D/3D use.
        /// </summary>
        public AudioPlaybackHandle PlayEffect(
            AudioClip clip,
            Vector3 worldPosition,
            Transform followTarget,
            Vector3 localOffset,
            float spatialBlend,
            float volumeScale,
            float pitch,
            bool loop,
            int priority,
            float minDistance,
            float maxDistance,
            float dopplerLevel,
            AudioRolloffMode rolloffMode)
        {
            return PlayEffectInternal(
                clip,
                worldPosition,
                followTarget,
                localOffset,
                spatialBlend,
                volumeScale,
                pitch,
                loop,
                priority,
                minDistance,
                maxDistance,
                dopplerLevel,
                rolloffMode);
        }

        public bool StopEffect(AudioPlaybackHandle handle)
        {
            EffectVoice voice;
            if (!TryGetVoice(handle, out voice))
            {
                return false;
            }

            ReleaseVoice(voice);
            return true;
        }

        public void StopEffectAudio()
        {
            foreach (EffectVoice voice in effectVoices)
            {
                if (voice.InUse)
                {
                    ReleaseVoice(voice);
                }
            }
        }

        public bool PauseEffect(AudioPlaybackHandle handle)
        {
            EffectVoice voice;
            if (!TryGetVoice(handle, out voice) || voice.Paused)
            {
                return false;
            }

            voice.Source.Pause();
            voice.Paused = true;
            return true;
        }

        public bool ResumeEffect(AudioPlaybackHandle handle)
        {
            EffectVoice voice;
            if (!TryGetVoice(handle, out voice) || !voice.Paused)
            {
                return false;
            }

            voice.Source.UnPause();
            voice.Paused = false;
            return true;
        }

        public bool IsEffectPlaying(AudioPlaybackHandle handle)
        {
            EffectVoice voice;
            return TryGetVoice(handle, out voice) &&
                   (voice.Source.isPlaying || voice.Paused);
        }

        public bool SetEffectVolume(
            AudioPlaybackHandle handle,
            float volumeScale)
        {
            EffectVoice voice;
            if (!TryGetVoice(handle, out voice))
            {
                return false;
            }

            voice.VolumeScale = Mathf.Clamp01(volumeScale);
            voice.Source.volume = effectsVolume * voice.VolumeScale;
            return true;
        }

        public bool SetEffectPitch(
            AudioPlaybackHandle handle,
            float pitch)
        {
            EffectVoice voice;
            if (!TryGetVoice(handle, out voice))
            {
                return false;
            }

            voice.Source.pitch = Mathf.Clamp(pitch, -3f, 3f);
            return true;
        }

        public bool SetEffectPosition(
            AudioPlaybackHandle handle,
            Vector3 worldPosition,
            bool stopFollowing = true)
        {
            EffectVoice voice;
            if (!TryGetVoice(handle, out voice))
            {
                return false;
            }

            if (stopFollowing)
            {
                voice.FollowTarget = null;
                voice.ExpectsFollowTarget = false;
            }

            voice.Source.transform.position = worldPosition;
            return true;
        }

        public void SetEffectsVolume(float volume)
        {
            effectsVolume = Mathf.Clamp01(volume);

            foreach (EffectVoice voice in effectVoices)
            {
                if (voice.InUse)
                {
                    voice.Source.volume =
                        effectsVolume * voice.VolumeScale;
                }
            }
        }

        #endregion

        #region Dialogue and voice-over

        /// <summary>
        /// Plays protected, non-positional narration. By default a new voice-over
        /// interrupts the previous voice-over, but not spatial character dialogue.
        /// </summary>
        public AudioPlaybackHandle PlayVoiceOver(
            AudioClip clip,
            float volumeScale = 1f,
            float pitch = 1f,
            bool interruptCurrent = true,
            int priority = 0)
        {
            if (interruptCurrent)
            {
                StopDialogue(currentVoiceOver);
            }
            else if (IsDialoguePlaying(currentVoiceOver))
            {
                return AudioPlaybackHandle.Invalid;
            }

            currentVoiceOver = PlayDialogueInternal(
                clip,
                transform.position,
                null,
                Vector3.zero,
                0f,
                volumeScale,
                pitch,
                false,
                priority);

            return currentVoiceOver;
        }

        public AudioPlaybackHandle PlayDialogueAtPosition(
            AudioClip clip,
            Vector3 worldPosition,
            float volumeScale = 1f,
            float pitch = 1f,
            int priority = 32)
        {
            return PlayDialogueInternal(
                clip,
                worldPosition,
                null,
                Vector3.zero,
                1f,
                volumeScale,
                pitch,
                false,
                priority);
        }

        public AudioPlaybackHandle PlayDialogueFollowing(
            AudioClip clip,
            Transform speaker,
            Vector3 localOffset,
            float volumeScale = 1f,
            float pitch = 1f,
            int priority = 32)
        {
            if (speaker == null)
            {
                return AudioPlaybackHandle.Invalid;
            }

            return PlayDialogueInternal(
                clip,
                speaker.TransformPoint(localOffset),
                speaker,
                localOffset,
                1f,
                volumeScale,
                pitch,
                false,
                priority);
        }

        public bool StopDialogue(AudioPlaybackHandle handle)
        {
            EffectVoice voice;
            if (!TryGetDialogueVoice(handle, out voice))
            {
                return false;
            }

            ReleaseVoice(voice);
            return true;
        }

        public void StopDialogueAudio()
        {
            foreach (EffectVoice voice in dialogueVoices)
            {
                if (voice.InUse)
                {
                    ReleaseVoice(voice);
                }
            }

            currentVoiceOver = AudioPlaybackHandle.Invalid;
        }

        public bool PauseDialogue(AudioPlaybackHandle handle)
        {
            EffectVoice voice;
            if (!TryGetDialogueVoice(handle, out voice) || voice.Paused)
            {
                return false;
            }

            voice.Source.Pause();
            voice.Paused = true;
            return true;
        }

        public bool ResumeDialogue(AudioPlaybackHandle handle)
        {
            EffectVoice voice;
            if (!TryGetDialogueVoice(handle, out voice) || !voice.Paused)
            {
                return false;
            }

            voice.Source.UnPause();
            voice.Paused = false;
            return true;
        }

        public bool IsDialoguePlaying(AudioPlaybackHandle handle)
        {
            EffectVoice voice;
            return TryGetDialogueVoice(handle, out voice) &&
                   (voice.Source.isPlaying || voice.Paused);
        }

        public void SetDialogueVolume(float volume)
        {
            dialogueVolume = Mathf.Clamp01(volume);

            foreach (EffectVoice voice in dialogueVoices)
            {
                if (voice.InUse)
                {
                    voice.Source.volume =
                        dialogueVolume * voice.VolumeScale;
                }
            }
        }

        /// <summary>
        /// Stops either an effect or dialogue handle.
        /// </summary>
        public bool StopPlayback(AudioPlaybackHandle handle)
        {
            switch (handle.Category)
            {
                case AudioPlaybackCategory.Effect:
                    return StopEffect(handle);
                case AudioPlaybackCategory.Dialogue:
                    return StopDialogue(handle);
                default:
                    return false;
            }
        }

        public bool IsPlaybackActive(AudioPlaybackHandle handle)
        {
            switch (handle.Category)
            {
                case AudioPlaybackCategory.Effect:
                    return IsEffectPlaying(handle);
                case AudioPlaybackCategory.Dialogue:
                    return IsDialoguePlaying(handle);
                default:
                    return false;
            }
        }

        #endregion

        #region Global controls and compatibility

        public void SetMuted(bool shouldMute)
        {
            muted = shouldMute;
            ApplyMuteToAllSources();
        }

        public void StopAllAudio(bool clearClips = false)
        {
            StopEffectAudio();
            StopDialogueAudio();
            StopMusic(clearClips);
            StopAmbientAudio(clearClips);
        }

        public void PauseAllAudio()
        {
            globallyPaused = true;
            AudioListener.pause = true;
        }

        public void ResumeAllAudio()
        {
            AudioListener.pause = false;
            globallyPaused = false;
        }

        public void PlayBackgroundAudio(AudioClip clip)
        {
            PlayMusic(clip);
        }

        public bool TryPlayBackgroundAudio(
            AudioClip clip,
            bool restartIfPlaying = false)
        {
            return TryPlayMusic(clip, restartIfPlaying);
        }

        public void StopBackgroundAudio(bool clearClip = false)
        {
            StopMusic(clearClip);
        }

        public void PauseBackgroundAudio()
        {
            PauseMusic();
        }

        public void ResumeBackgroundAudio()
        {
            ResumeMusic();
        }

        public void SetBackgroundVolume(float volume)
        {
            SetMusicVolume(volume);
        }

        public void SetBackgroundLooping(bool shouldLoop)
        {
            SetMusicLooping(shouldLoop);
        }

        #endregion

        private bool Initialize()
        {
            if (initialized)
            {
                return true;
            }

            ClampSettings();
            EnsureLayerSources();
            EnsureEffectPoolRoot();
            EnsureDialoguePoolRoot();

            musicLayer = new LayerState(
                musicAudioSource,
                musicCrossfadeAudioSource,
                musicVolume,
                musicLoops);

            ambientLayer = new LayerState(
                ambientAudioSource,
                ambientCrossfadeAudioSource,
                ambientVolume,
                ambientLoops);

            ConfigureLayerSource(musicLayer.First, musicOutput, musicLoops);
            ConfigureLayerSource(musicLayer.Second, musicOutput, musicLoops);
            ConfigureLayerSource(ambientLayer.First, ambientOutput, ambientLoops);
            ConfigureLayerSource(ambientLayer.Second, ambientOutput, ambientLoops);

            effectVoices.Clear();
            if (effectAudioSource == null ||
                effectAudioSource.transform == transform ||
                effectAudioSource.transform == effectPoolRoot ||
                !effectAudioSource.transform.IsChildOf(effectPoolRoot))
            {
                effectAudioSource = CreateEffectSource();
            }

            AddEffectVoice(effectAudioSource);

            while (effectVoices.Count < initialEffectPoolSize)
            {
                AddEffectVoice(CreateEffectSource());
            }

            dialogueVoices.Clear();
            if (dialogueAudioSource == null ||
                dialogueAudioSource.transform == transform ||
                dialogueAudioSource.transform == dialoguePoolRoot ||
                !dialogueAudioSource.transform.IsChildOf(dialoguePoolRoot))
            {
                dialogueAudioSource = CreateDialogueSource();
            }

            AddDialogueVoice(dialogueAudioSource);

            while (dialogueVoices.Count < initialDialoguePoolSize)
            {
                AddDialogueVoice(CreateDialogueSource());
            }

            ApplyMuteToAllSources();
            initialized = true;
            return true;
        }

        private void EnsureLayerSources()
        {
            var usedSources = new HashSet<AudioSource>();

            musicAudioSource = EnsureOwnedUniqueSource(
                musicAudioSource,
                "Music A",
                usedSources);
            musicCrossfadeAudioSource = EnsureOwnedUniqueSource(
                musicCrossfadeAudioSource,
                "Music B",
                usedSources);
            ambientAudioSource = EnsureOwnedUniqueSource(
                ambientAudioSource,
                "Ambience A",
                usedSources);
            ambientCrossfadeAudioSource = EnsureOwnedUniqueSource(
                ambientCrossfadeAudioSource,
                "Ambience B",
                usedSources);
        }

        private AudioSource EnsureOwnedUniqueSource(
            AudioSource source,
            string objectName,
            HashSet<AudioSource> usedSources)
        {
            bool isOwned =
                source != null &&
                (source.transform == transform ||
                 source.transform.IsChildOf(transform));

            if (!isOwned || usedSources.Contains(source))
            {
                source = CreateChildAudioSource(objectName, transform);
            }

            usedSources.Add(source);
            return source;
        }

        private void EnsureEffectPoolRoot()
        {
            bool validRoot =
                effectPoolRoot != null &&
                effectPoolRoot != transform &&
                effectPoolRoot.IsChildOf(transform);

            if (validRoot)
            {
                return;
            }

            Transform existingRoot = transform.Find("Effect Pool");
            if (existingRoot != null)
            {
                effectPoolRoot = existingRoot;
                return;
            }

            var rootObject = new GameObject("Effect Pool");
            effectPoolRoot = rootObject.transform;
            effectPoolRoot.SetParent(transform, false);
        }

        private void EnsureDialoguePoolRoot()
        {
            bool validRoot =
                dialoguePoolRoot != null &&
                dialoguePoolRoot != transform &&
                dialoguePoolRoot.IsChildOf(transform);

            if (validRoot)
            {
                return;
            }

            Transform existingRoot = transform.Find("Dialogue Pool");
            if (existingRoot != null)
            {
                dialoguePoolRoot = existingRoot;
                return;
            }

            var rootObject = new GameObject("Dialogue Pool");
            dialoguePoolRoot = rootObject.transform;
            dialoguePoolRoot.SetParent(transform, false);
        }

        private AudioSource CreateEffectSource()
        {
            string sourceName = $"Effect Voice {effectVoices.Count:00}";
            AudioSource source =
                CreateChildAudioSource(sourceName, effectPoolRoot);
            ConfigureEffectSource(source);
            return source;
        }

        private AudioSource CreateDialogueSource()
        {
            string sourceName =
                $"Dialogue Voice {dialogueVoices.Count:00}";
            AudioSource source =
                CreateChildAudioSource(sourceName, dialoguePoolRoot);
            ConfigureDialogueSource(source);
            return source;
        }

        private static AudioSource CreateChildAudioSource(
            string objectName,
            Transform parent)
        {
            var sourceObject = new GameObject(objectName);
            sourceObject.transform.SetParent(parent, false);

            AudioSource source = sourceObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            return source;
        }

        private void AddEffectVoice(AudioSource source)
        {
            ConfigureEffectSource(source);

            var voice = new EffectVoice(
                effectVoices.Count,
                source,
                AudioPlaybackCategory.Effect);
            effectVoices.Add(voice);
        }

        private void AddDialogueVoice(AudioSource source)
        {
            ConfigureDialogueSource(source);

            var voice = new EffectVoice(
                dialogueVoices.Count,
                source,
                AudioPlaybackCategory.Dialogue);
            dialogueVoices.Add(voice);
        }

        private void ConfigureLayerSource(
            AudioSource source,
            AudioMixerGroup output,
            bool shouldLoop)
        {
            source.playOnAwake = false;
            source.spatialBlend = 0f;
            source.loop = shouldLoop;
            source.outputAudioMixerGroup = output;
            source.mute = muted;
        }

        private void ConfigureEffectSource(AudioSource source)
        {
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 1f;
            source.volume = effectsVolume;
            source.pitch = 1f;
            source.priority = 128;
            source.minDistance = defaultMinDistance;
            source.maxDistance = defaultMaxDistance;
            source.dopplerLevel = defaultDopplerLevel;
            source.rolloffMode = defaultRolloffMode;
            source.outputAudioMixerGroup = effectsOutput;
            source.mute = muted;
        }

        private void ConfigureDialogueSource(AudioSource source)
        {
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 1f;
            source.volume = dialogueVolume;
            source.pitch = 1f;
            source.priority = 32;
            source.minDistance = dialogueMinDistance;
            source.maxDistance = dialogueMaxDistance;
            source.dopplerLevel = 0f;
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            source.outputAudioMixerGroup = dialogueOutput;
            source.mute = muted;
        }

        private bool TryPlayLayer(
            LayerState layer,
            AudioClip clip,
            float crossfadeSeconds,
            bool restartIfPlaying)
        {
            if (layer == null)
            {
                return false;
            }

            if (clip == null)
            {
                StopLayer(layer, crossfadeSeconds, true);
                return false;
            }

            if (!restartIfPlaying &&
                layer.Active != null &&
                layer.Active.clip == clip &&
                (layer.Active.isPlaying || layer.IsPaused))
            {
                return true;
            }

            StopLayerTransition(layer);

            AudioSource outgoing = layer.Active;
            AudioSource incoming =
                outgoing == layer.First ? layer.Second : layer.First;

            incoming.Stop();
            incoming.clip = clip;
            incoming.loop = layer.Loop;
            incoming.outputAudioMixerGroup =
                layer == musicLayer ? musicOutput : ambientOutput;
            incoming.mute = muted;

            float duration = Mathf.Max(0f, crossfadeSeconds);
            bool hasOutgoing =
                outgoing != null &&
                (outgoing.isPlaying || layer.IsPaused);

            incoming.volume = duration > 0f ? 0f : layer.EffectiveVolume;
            incoming.Play();
            layer.Active = incoming;
            layer.IsPaused = false;

            if (!hasOutgoing || duration <= 0f)
            {
                StopAndClearInactiveLayerSource(outgoing);
                incoming.volume = layer.EffectiveVolume;
                return true;
            }

            layer.Transition = StartCoroutine(
                CrossfadeLayerRoutine(layer, outgoing, incoming, duration));
            return true;
        }

        private IEnumerator CrossfadeLayerRoutine(
            LayerState layer,
            AudioSource outgoing,
            AudioSource incoming,
            float duration)
        {
            float outgoingStartVolume = outgoing.volume;
            float elapsed = 0f;

            while (elapsed < duration)
            {
                if (globallyPaused || layer.IsPaused)
                {
                    yield return null;
                    continue;
                }

                elapsed += Time.unscaledDeltaTime;
                float progress = Mathf.Clamp01(elapsed / duration);

                if (outgoing != null)
                {
                    outgoing.volume =
                        Mathf.Lerp(outgoingStartVolume, 0f, progress);
                }

                if (incoming != null)
                {
                    incoming.volume =
                        Mathf.Lerp(0f, layer.EffectiveVolume, progress);
                }

                yield return null;
            }

            StopAndClearInactiveLayerSource(outgoing);

            if (incoming != null)
            {
                incoming.volume = layer.EffectiveVolume;
            }

            layer.Transition = null;
        }

        private void StopLayer(
            LayerState layer,
            float fadeSeconds,
            bool clearClips)
        {
            if (layer == null)
            {
                return;
            }

            StopLayerTransition(layer);

            float duration = Mathf.Max(0f, fadeSeconds);
            if (duration <= 0f)
            {
                StopLayerSource(layer.First, clearClips);
                StopLayerSource(layer.Second, clearClips);
                layer.Active = null;
                layer.IsPaused = false;
                return;
            }

            layer.Transition = StartCoroutine(
                FadeOutLayerRoutine(layer, duration, clearClips));
        }

        private IEnumerator FadeOutLayerRoutine(
            LayerState layer,
            float duration,
            bool clearClips)
        {
            float firstStartVolume = layer.First.volume;
            float secondStartVolume = layer.Second.volume;
            float elapsed = 0f;

            while (elapsed < duration)
            {
                if (globallyPaused || layer.IsPaused)
                {
                    yield return null;
                    continue;
                }

                elapsed += Time.unscaledDeltaTime;
                float progress = Mathf.Clamp01(elapsed / duration);
                layer.First.volume =
                    Mathf.Lerp(firstStartVolume, 0f, progress);
                layer.Second.volume =
                    Mathf.Lerp(secondStartVolume, 0f, progress);
                yield return null;
            }

            StopLayerSource(layer.First, clearClips);
            StopLayerSource(layer.Second, clearClips);
            layer.Active = null;
            layer.IsPaused = false;
            layer.Transition = null;
        }

        private void StopLayerTransition(LayerState layer)
        {
            if (layer?.Transition == null)
            {
                return;
            }

            StopCoroutine(layer.Transition);
            layer.Transition = null;
        }

        private static void StopAndClearInactiveLayerSource(AudioSource source)
        {
            StopLayerSource(source, true);
        }

        private static void StopLayerSource(
            AudioSource source,
            bool clearClip)
        {
            if (source == null)
            {
                return;
            }

            source.Stop();

            if (clearClip)
            {
                source.clip = null;
            }
        }

        private static void PauseLayer(LayerState layer)
        {
            if (layer == null || layer.IsPaused)
            {
                return;
            }

            layer.FirstWasPlaying = layer.First.isPlaying;
            layer.SecondWasPlaying = layer.Second.isPlaying;

            if (layer.FirstWasPlaying)
            {
                layer.First.Pause();
            }

            if (layer.SecondWasPlaying)
            {
                layer.Second.Pause();
            }

            layer.IsPaused =
                layer.FirstWasPlaying || layer.SecondWasPlaying;
        }

        private static void ResumeLayer(LayerState layer)
        {
            if (layer == null || !layer.IsPaused)
            {
                return;
            }

            if (layer.FirstWasPlaying)
            {
                layer.First.UnPause();
            }

            if (layer.SecondWasPlaying)
            {
                layer.Second.UnPause();
            }

            layer.FirstWasPlaying = false;
            layer.SecondWasPlaying = false;
            layer.IsPaused = false;
        }

        private static void SetLayerVolume(
            LayerState layer,
            float volume)
        {
            if (layer == null)
            {
                return;
            }

            layer.Volume = Mathf.Clamp01(volume);

            if (layer.Transition == null && layer.Active != null)
            {
                layer.Active.volume = layer.EffectiveVolume;
            }
        }

        private static void SetLayerLooping(
            LayerState layer,
            bool shouldLoop)
        {
            if (layer == null)
            {
                return;
            }

            layer.Loop = shouldLoop;
            layer.First.loop = shouldLoop;
            layer.Second.loop = shouldLoop;
        }

        private static bool IsLayerPlaying(LayerState layer)
        {
            return layer != null &&
                   (layer.First.isPlaying ||
                    layer.Second.isPlaying ||
                    layer.IsPaused);
        }

        private void UpdatePooledVoices(List<EffectVoice> voices)
        {
            foreach (EffectVoice voice in voices)
            {
                if (!voice.InUse)
                {
                    continue;
                }

                if (voice.FollowTarget != null)
                {
                    voice.Source.transform.position =
                        voice.FollowTarget.TransformPoint(voice.LocalOffset);
                }
                else if (voice.ExpectsFollowTarget)
                {
                    ReleaseVoice(voice);
                    continue;
                }

                if (!globallyPaused &&
                    !voice.Paused &&
                    !voice.Source.isPlaying)
                {
                    ReleaseVoice(voice);
                }
            }
        }

        private AudioPlaybackHandle PlayDialogueInternal(
            AudioClip clip,
            Vector3 worldPosition,
            Transform followTarget,
            Vector3 localOffset,
            float spatialBlend,
            float volumeScale,
            float pitch,
            bool loop,
            int priority)
        {
            if (clip == null || !Initialize())
            {
                return AudioPlaybackHandle.Invalid;
            }

            int clampedPriority = Mathf.Clamp(
                priority,
                HighestPriority,
                LowestPriority);

            EffectVoice voice = AcquireDialogueVoice(clampedPriority);
            if (voice == null)
            {
                return AudioPlaybackHandle.Invalid;
            }

            PrepareVoice(
                voice,
                worldPosition,
                followTarget,
                localOffset,
                volumeScale,
                clampedPriority);

            AudioSource source = voice.Source;
            source.clip = clip;
            source.volume = dialogueVolume * voice.VolumeScale;
            source.pitch = Mathf.Clamp(pitch, -3f, 3f);
            source.loop = loop;
            source.spatialBlend = Mathf.Clamp01(spatialBlend);
            source.minDistance = dialogueMinDistance;
            source.maxDistance = dialogueMaxDistance;
            source.dopplerLevel = 0f;
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            source.outputAudioMixerGroup = dialogueOutput;
            source.mute = muted;
            source.Play();

            activeDialogueCount++;
            if (activeDialogueCount == 1)
            {
                SetDialogueDucking(true);
            }

            var handle = new AudioPlaybackHandle(
                AudioPlaybackCategory.Dialogue,
                voice.Index,
                voice.Generation);
            DialogueStarted?.Invoke(handle);
            return handle;
        }

        private EffectVoice AcquireDialogueVoice(int requestedPriority)
        {
            foreach (EffectVoice voice in dialogueVoices)
            {
                if (!voice.InUse)
                {
                    return voice;
                }

                if (!globallyPaused &&
                    !voice.Paused &&
                    !voice.Source.isPlaying)
                {
                    ReleaseVoice(voice);
                    return voice;
                }
            }

            if (allowDialoguePoolGrowth &&
                dialogueVoices.Count < maximumDialoguePoolSize)
            {
                AudioSource source = CreateDialogueSource();
                AddDialogueVoice(source);
                return dialogueVoices[dialogueVoices.Count - 1];
            }

            if (!allowDialogueVoiceStealing)
            {
                return null;
            }

            EffectVoice victim = FindVoiceToSteal(dialogueVoices);
            if (victim == null ||
                victim.Priority < requestedPriority)
            {
                return null;
            }

            ReleaseVoice(victim);
            return victim;
        }

        private bool TryGetDialogueVoice(
            AudioPlaybackHandle handle,
            out EffectVoice voice)
        {
            voice = null;

            if (!handle.IsValid ||
                handle.Category != AudioPlaybackCategory.Dialogue ||
                handle.VoiceIndex < 0 ||
                handle.VoiceIndex >= dialogueVoices.Count)
            {
                return false;
            }

            EffectVoice candidate =
                dialogueVoices[handle.VoiceIndex];
            if (!candidate.InUse ||
                candidate.Generation != handle.Generation)
            {
                return false;
            }

            voice = candidate;
            return true;
        }

        private void SetDialogueDucking(bool dialogueIsActive)
        {
            if (musicLayer == null || ambientLayer == null)
            {
                return;
            }

            if (dialogueDuckTransition != null)
            {
                StopCoroutine(dialogueDuckTransition);
            }

            float targetMusicMultiplier = dialogueIsActive
                ? musicVolumeDuringDialogue
                : 1f;
            float targetAmbientMultiplier = dialogueIsActive
                ? ambientVolumeDuringDialogue
                : 1f;
            float duration = dialogueIsActive
                ? dialogueDuckSeconds
                : dialogueRestoreSeconds;

            if (duration <= 0f)
            {
                SetLayerDuckMultiplier(
                    musicLayer,
                    targetMusicMultiplier);
                SetLayerDuckMultiplier(
                    ambientLayer,
                    targetAmbientMultiplier);
                dialogueDuckTransition = null;
                return;
            }

            dialogueDuckTransition = StartCoroutine(
                DialogueDuckRoutine(
                    targetMusicMultiplier,
                    targetAmbientMultiplier,
                    duration));
        }

        private IEnumerator DialogueDuckRoutine(
            float targetMusicMultiplier,
            float targetAmbientMultiplier,
            float duration)
        {
            float startingMusicMultiplier =
                musicLayer.DuckMultiplier;
            float startingAmbientMultiplier =
                ambientLayer.DuckMultiplier;
            float elapsed = 0f;

            while (elapsed < duration)
            {
                if (globallyPaused)
                {
                    yield return null;
                    continue;
                }

                elapsed += Time.unscaledDeltaTime;
                float progress = Mathf.Clamp01(elapsed / duration);

                SetLayerDuckMultiplier(
                    musicLayer,
                    Mathf.Lerp(
                        startingMusicMultiplier,
                        targetMusicMultiplier,
                        progress));
                SetLayerDuckMultiplier(
                    ambientLayer,
                    Mathf.Lerp(
                        startingAmbientMultiplier,
                        targetAmbientMultiplier,
                        progress));

                yield return null;
            }

            SetLayerDuckMultiplier(
                musicLayer,
                targetMusicMultiplier);
            SetLayerDuckMultiplier(
                ambientLayer,
                targetAmbientMultiplier);
            dialogueDuckTransition = null;
        }

        private static void SetLayerDuckMultiplier(
            LayerState layer,
            float multiplier)
        {
            layer.DuckMultiplier = Mathf.Clamp01(multiplier);

            if (layer.Transition == null &&
                layer.Active != null)
            {
                layer.Active.volume = layer.EffectiveVolume;
            }
        }

        private static void PrepareVoice(
            EffectVoice voice,
            Vector3 worldPosition,
            Transform followTarget,
            Vector3 localOffset,
            float volumeScale,
            int priority)
        {
            voice.Generation++;
            if (voice.Generation == 0)
            {
                voice.Generation = 1;
            }

            voice.InUse = true;
            voice.Paused = false;
            voice.Priority = priority;
            voice.StartedAt = Time.unscaledTimeAsDouble;
            voice.FollowTarget = followTarget;
            voice.ExpectsFollowTarget = followTarget != null;
            voice.LocalOffset = localOffset;
            voice.VolumeScale = Mathf.Clamp01(volumeScale);
            voice.Source.transform.position = worldPosition;
        }

        private AudioPlaybackHandle PlayEffectInternal(
            AudioClip clip,
            Vector3 worldPosition,
            Transform followTarget,
            Vector3 localOffset,
            float spatialBlend,
            float volumeScale,
            float pitch,
            bool loop,
            int priority,
            float minDistance,
            float maxDistance,
            float dopplerLevel,
            AudioRolloffMode rolloffMode)
        {
            if (clip == null || !Initialize())
            {
                return AudioPlaybackHandle.Invalid;
            }

            int clampedPriority = Mathf.Clamp(
                priority,
                HighestPriority,
                LowestPriority);

            EffectVoice voice = AcquireVoice(clampedPriority);
            if (voice == null)
            {
                return AudioPlaybackHandle.Invalid;
            }

            PrepareVoice(
                voice,
                worldPosition,
                followTarget,
                localOffset,
                volumeScale,
                clampedPriority);

            AudioSource source = voice.Source;
            source.clip = clip;
            source.volume = effectsVolume * voice.VolumeScale;
            source.pitch = Mathf.Clamp(pitch, -3f, 3f);
            source.loop = loop;
            source.priority = clampedPriority;
            source.spatialBlend = Mathf.Clamp01(spatialBlend);
            source.minDistance = Mathf.Max(0f, minDistance);
            source.maxDistance = Mathf.Max(
                source.minDistance + 0.01f,
                maxDistance);
            source.dopplerLevel = Mathf.Max(0f, dopplerLevel);
            source.rolloffMode = rolloffMode;
            source.outputAudioMixerGroup = effectsOutput;
            source.mute = muted;
            source.Play();

            return new AudioPlaybackHandle(
                AudioPlaybackCategory.Effect,
                voice.Index,
                voice.Generation);
        }

        private EffectVoice AcquireVoice(int requestedPriority)
        {
            foreach (EffectVoice voice in effectVoices)
            {
                if (!voice.InUse)
                {
                    return voice;
                }

                if (!globallyPaused &&
                    !voice.Paused &&
                    !voice.Source.isPlaying)
                {
                    ReleaseVoice(voice);
                    return voice;
                }
            }

            if (allowPoolGrowth &&
                effectVoices.Count < maximumEffectPoolSize)
            {
                AudioSource source = CreateEffectSource();
                AddEffectVoice(source);
                return effectVoices[effectVoices.Count - 1];
            }

            if (!allowVoiceStealing)
            {
                return null;
            }

            EffectVoice victim = FindVoiceToSteal(effectVoices);
            if (victim == null ||
                victim.Priority < requestedPriority)
            {
                return null;
            }

            ReleaseVoice(victim);
            return victim;
        }

        private static EffectVoice FindVoiceToSteal(
            List<EffectVoice> voices)
        {
            EffectVoice candidate = null;

            foreach (EffectVoice voice in voices)
            {
                if (!voice.InUse)
                {
                    return voice;
                }

                if (candidate == null ||
                    voice.Priority > candidate.Priority ||
                    (voice.Priority == candidate.Priority &&
                     voice.StartedAt < candidate.StartedAt))
                {
                    candidate = voice;
                }
            }

            return candidate;
        }

        private bool TryGetVoice(
            AudioPlaybackHandle handle,
            out EffectVoice voice)
        {
            voice = null;

            if (!handle.IsValid ||
                handle.Category != AudioPlaybackCategory.Effect ||
                handle.VoiceIndex < 0 ||
                handle.VoiceIndex >= effectVoices.Count)
            {
                return false;
            }

            EffectVoice candidate = effectVoices[handle.VoiceIndex];
            if (!candidate.InUse ||
                candidate.Generation != handle.Generation)
            {
                return false;
            }

            voice = candidate;
            return true;
        }

        private void ReleaseVoice(EffectVoice voice)
        {
            bool wasInUse = voice.InUse;
            var releasedHandle = new AudioPlaybackHandle(
                voice.Category,
                voice.Index,
                voice.Generation);

            voice.Source.Stop();
            voice.Source.clip = null;
            voice.Source.loop = false;
            voice.Source.pitch = 1f;
            voice.Source.spatialBlend = 1f;
            voice.Source.transform.localPosition = Vector3.zero;

            voice.InUse = false;
            voice.Paused = false;
            voice.FollowTarget = null;
            voice.ExpectsFollowTarget = false;
            voice.LocalOffset = Vector3.zero;
            voice.VolumeScale = 1f;

            if (wasInUse &&
                voice.Category == AudioPlaybackCategory.Dialogue)
            {
                activeDialogueCount =
                    Mathf.Max(0, activeDialogueCount - 1);

                if (currentVoiceOver == releasedHandle)
                {
                    currentVoiceOver = AudioPlaybackHandle.Invalid;
                }

                DialogueEnded?.Invoke(releasedHandle);

                if (activeDialogueCount == 0)
                {
                    SetDialogueDucking(false);
                }
            }
        }

        private void ApplyMuteToAllSources()
        {
            if (musicLayer != null)
            {
                musicLayer.First.mute = muted;
                musicLayer.Second.mute = muted;
            }

            if (ambientLayer != null)
            {
                ambientLayer.First.mute = muted;
                ambientLayer.Second.mute = muted;
            }

            foreach (EffectVoice voice in effectVoices)
            {
                voice.Source.mute = muted;
            }

            foreach (EffectVoice voice in dialogueVoices)
            {
                voice.Source.mute = muted;
            }
        }

        private void ClampSettings()
        {
            musicVolume = Mathf.Clamp01(musicVolume);
            ambientVolume = Mathf.Clamp01(ambientVolume);
            effectsVolume = Mathf.Clamp01(effectsVolume);
            dialogueVolume = Mathf.Clamp01(dialogueVolume);

            defaultMusicCrossfadeSeconds =
                Mathf.Max(0f, defaultMusicCrossfadeSeconds);
            defaultAmbientCrossfadeSeconds =
                Mathf.Max(0f, defaultAmbientCrossfadeSeconds);

            initialEffectPoolSize =
                Mathf.Max(1, initialEffectPoolSize);
            maximumEffectPoolSize =
                Mathf.Max(initialEffectPoolSize, maximumEffectPoolSize);
            initialDialoguePoolSize =
                Mathf.Max(1, initialDialoguePoolSize);
            maximumDialoguePoolSize =
                Mathf.Max(
                    initialDialoguePoolSize,
                    maximumDialoguePoolSize);

            defaultMinDistance = Mathf.Max(0f, defaultMinDistance);
            defaultMaxDistance = Mathf.Max(
                defaultMinDistance + 0.01f,
                defaultMaxDistance);
            defaultDopplerLevel = Mathf.Max(0f, defaultDopplerLevel);
            dialogueMinDistance = Mathf.Max(0f, dialogueMinDistance);
            dialogueMaxDistance = Mathf.Max(
                dialogueMinDistance + 0.01f,
                dialogueMaxDistance);
            musicVolumeDuringDialogue =
                Mathf.Clamp01(musicVolumeDuringDialogue);
            ambientVolumeDuringDialogue =
                Mathf.Clamp01(ambientVolumeDuringDialogue);
            dialogueDuckSeconds =
                Mathf.Max(0f, dialogueDuckSeconds);
            dialogueRestoreSeconds =
                Mathf.Max(0f, dialogueRestoreSeconds);
        }

        private void OnDestroy()
        {
            if (instance != this)
            {
                return;
            }

            AudioListener.pause = false;
            instance = null;
        }

        private void OnValidate()
        {
            ClampSettings();

            ApplyEditorSourceSettings(
                musicAudioSource,
                musicVolume,
                musicLoops,
                musicOutput);
            ApplyEditorSourceSettings(
                musicCrossfadeAudioSource,
                0f,
                musicLoops,
                musicOutput);
            ApplyEditorSourceSettings(
                ambientAudioSource,
                ambientVolume,
                ambientLoops,
                ambientOutput);
            ApplyEditorSourceSettings(
                ambientCrossfadeAudioSource,
                0f,
                ambientLoops,
                ambientOutput);

            if (effectAudioSource != null)
            {
                effectAudioSource.playOnAwake = false;
                effectAudioSource.volume = effectsVolume;
                effectAudioSource.outputAudioMixerGroup = effectsOutput;
                effectAudioSource.mute = muted;
            }

            if (dialogueAudioSource != null)
            {
                dialogueAudioSource.playOnAwake = false;
                dialogueAudioSource.volume = dialogueVolume;
                dialogueAudioSource.outputAudioMixerGroup =
                    dialogueOutput;
                dialogueAudioSource.mute = muted;
            }
        }

        private void ApplyEditorSourceSettings(
            AudioSource source,
            float volume,
            bool shouldLoop,
            AudioMixerGroup output)
        {
            if (source == null)
            {
                return;
            }

            source.playOnAwake = false;
            source.spatialBlend = 0f;
            source.volume = volume;
            source.loop = shouldLoop;
            source.outputAudioMixerGroup = output;
            source.mute = muted;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            instance = null;
            hasLoggedMissingInstance = false;
            AudioListener.pause = false;
        }

        private sealed class LayerState
        {
            public readonly AudioSource First;
            public readonly AudioSource Second;

            public AudioSource Active;
            public Coroutine Transition;
            public float Volume;
            public float DuckMultiplier = 1f;
            public bool Loop;
            public bool IsPaused;
            public bool FirstWasPlaying;
            public bool SecondWasPlaying;

            public float EffectiveVolume => Volume * DuckMultiplier;

            public LayerState(
                AudioSource first,
                AudioSource second,
                float volume,
                bool loop)
            {
                First = first;
                Second = second;
                Active = first.clip != null ? first : null;
                Volume = volume;
                Loop = loop;
            }
        }

        private sealed class EffectVoice
        {
            public readonly int Index;
            public readonly AudioSource Source;
            public readonly AudioPlaybackCategory Category;

            public uint Generation;
            public bool InUse;
            public bool Paused;
            public int Priority;
            public double StartedAt;
            public Transform FollowTarget;
            public bool ExpectsFollowTarget;
            public Vector3 LocalOffset;
            public float VolumeScale = 1f;

            public EffectVoice(
                int index,
                AudioSource source,
                AudioPlaybackCategory category)
            {
                Index = index;
                Source = source;
                Category = category;
            }
        }
    }
}
