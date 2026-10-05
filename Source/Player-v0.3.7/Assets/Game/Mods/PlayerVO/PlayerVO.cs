// Daggerfall Narrator - Player v0.3.7 character-creation flow + packaged-voice filters
// Local, deterministic player-character speech for Daggerfall Unity 1.1.1.
// No LLM and no runtime text generation: PlayerVO chooses from authored bark/response pools
// and voices player-authored keyboard speech through the shared local Kokoro service.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallConnect.Utility;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.UserInterface;
using DaggerfallWorkshop.Game.UserInterfaceWindows;
using DaggerfallWorkshop.Game.Utility.ModSupport;
using DaggerfallWorkshop.Game.Utility.ModSupport.ModSettings;
using DaggerfallWorkshop.Utility;
using Wenzil.Console;

namespace PlayerVO
{
    public class PlayerVOMod : MonoBehaviour
    {
        private static Mod mod;
        private static PlayerVOMod instance;

        private PlayerVOConfig config;
        private BarkLibrary library;
        private readonly Dictionary<string, string[]> barkSets = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, float> categoryLastSpoken = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<string> recentLines = new Queue<string>();
        private readonly Queue<PlayerSpeechRequest> speechQueue = new Queue<PlayerSpeechRequest>();

        private AudioSource audioSource;
        private Coroutine speechRoutine;
        private int speechGeneration;
        private PlayerSpeechRequest activeSpeech;
        private float automaticCooldownUntil;
        private float manualCooldownUntil;
        private float typedCooldownUntil;
        private float vanillaVocalBlockUntil;
        private bool playerDead;

        private string rootDir;
        private string cacheDir;
        private string configPath;
        private string barksPath;
        private float nextCacheCleanupTime;

        private int lastHealth = -1;
        private int lastFatigue = -1;
        private int lastMagicka = -1;
        private int lastBreath = -1;
        private bool lastEnemyAlert;
        private bool lastEncumbered;
        private string lastWorldContext = string.Empty;
        private string lastLocationName = string.Empty;
        private string lastBuildingContext = string.Empty;
        private float affiliationCacheUntil;
        private string cachedPrimaryGuildKey = string.Empty;
        private string cachedPrimaryGuildName = string.Empty;
        private string cachedPrimaryGuildTitle = string.Empty;
        private string cachedLocalReputationBand = "Neutral";
        private float nextConditionPoll;
        private float nextIntegrationPoll;
        private float nextQuestPoll;
        private string recentContextEvent = "General";
        private float recentContextEventTime;
        private bool barkKeyHeld;
        private bool barkKeyWasDown;
        private bool barkHoldOpenedInput;
        private float barkKeyDownTime;
        private KeyCode barkKey = KeyCode.V;
        private string activePlayerSubtitle = string.Empty;
        private float playerSubtitleUntil;
        private GUIStyle playerSubtitleStyle;
        private DaggerfallVoiceEngineClient voiceEngine;
        private string activeVoiceEngineTurnId = string.Empty;
        private float nextVoiceEnginePresence;
        private bool biographyPromptQueued;
        private bool biographyPromptShown;
        private bool biographyResultPromptSeen;
        private bool characterCreationSessionActive;
        private int biographySuggestedBirthsign = 12;
        private string biographySuggestionReason = string.Empty;
        private int pendingCharacterBirthsign = -1;
        private string loadedCharacterBirthsignKey = string.Empty;
        private string loadedCharacterVoiceKey = string.Empty;
        private CreateCharSummary personalitySummaryOwner;
        private CreateCharSummary characterCreationSummaryOwner;
        private bool characterCreationVoicePromptShown;
        private bool pendingCharacterVoiceSave;
        private readonly List<string> packagedCharacterVoices = new List<string>();
        private Coroutine voicePreviewRoutine;
        private string voicePreviewTurnId = string.Empty;
        private int voicePreviewGeneration;
        private object observedPlayerEntityToken;

        private DaggerfallMessageBox observedChoiceBox;

        // Optional NPCVO bridge (soft/reflection-only).
        private bool npcvoReflectionChecked;
        private Type npcvoType;
        private MethodInfo npcvoRegisterPlayerIdentity;
        private MethodInfo npcvoRegisterPlayerIdentityDetailed;
        private MethodInfo npcvoBeginPlayerTurn;
        private MethodInfo npcvoUpdatePlayerText;
        private MethodInfo npcvoEndPlayerTurn;
        private PropertyInfo npcvoEnhancedQuestActive;
        private float nextNpcvoIdentitySync;
        private string lastNpcvoIdentityKey = string.Empty;
        private Texture2D cachedConversationPortrait;
        private string cachedConversationPortraitKey = string.Empty;

        // Optional Climates & Calories bridge (soft/reflection-only).
        private bool ccReflectionChecked;
        private Type ccRootType;
        private Type ccHungerType;
        private Type ccClimatesType;
        private FieldInfo ccThirstField;
        private FieldInfo ccWetField;
        private FieldInfo ccCampingField;
        private FieldInfo ccCookingField;
        private FieldInfo ccHungryField;
        private FieldInfo ccStarvingField;
        private FieldInfo ccTempField;
        private bool ccLastHungry;
        private bool ccLastStarving;
        private bool ccLastWet;
        private bool ccLastCamping;
        private bool ccLastCooking;
        private bool ccLastThirsty;
        private int ccLastTempBand;

        // Generic quest reflection state. This intentionally understands quest structure, not prose.
        private readonly HashSet<string> knownQuestIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool questSnapshotInitialized;

        private const string CacheSchema = "playervo-cache-v3";

        [Invoke(StateManager.StateTypes.Start, 0)]
        public static void Init(InitParams initParams)
        {
            mod = initParams.Mod;
            GameObject go = new GameObject(mod.Title);
            DontDestroyOnLoad(go);
            go.AddComponent<PlayerVOMod>();
            mod.IsReady = true;
            Debug.Log("[PlayerVO] Initialized v0.3.7 character-creation flow + packaged-voice filters.");
        }

        // Other mods can submit a precise event without PlayerVO taking a compile-time dependency.
        // Example future integrations: PlayerVOMod.NotifyContext("FrostKill").
        public static bool NotifyContext(string eventKey)
        {
            if (instance == null || string.IsNullOrWhiteSpace(eventKey))
                return false;
            instance.RecordContext(eventKey.Trim());
            instance.TryAutomaticBark(eventKey.Trim(), true);
            return true;
        }

        // Soft integration seam used by the NPC module for vanilla Ask/Where Is/Tell Me About
        // questions. The exact DFU question is spoken; no race/personality rewrite is applied.
        public static bool SpeakVanillaTalkQuestion(string question)
        {
            if (instance == null || instance.config == null || !instance.config.Enabled ||
                !instance.config.VoiceDialogueChoices || string.IsNullOrWhiteSpace(question))
                return false;
            instance.RecordContext("TalkQuestion");
            instance.EnqueueSpeech(question.Trim(), SpeechPriority.Dialogue, "talk-question", false);
            return true;
        }

        // Dungeon Master can route vanilla look/observation text to the protagonist. PlayerVO
        // keeps the observed subject intact, converts second-person wording to first-person, then
        // adds a restrained reaction from the selected personality. The Agent usually declines
        // routed automatic observations so Dungeon Master can fall back to narration instead.
        public static bool SpeakObservation(string observation)
        {
            if (instance == null || instance.config == null || !instance.config.Enabled || string.IsNullOrWhiteSpace(observation))
                return false;
            if (instance.IsAgentPersonality())
                return false;

            string line = instance.ResolvePersonalityObservation(observation.Trim());
            if (string.IsNullOrWhiteSpace(line))
                return false;

            instance.RecordContext("Observation");
            instance.EnqueueSpeech(line, SpeechPriority.Manual, "observation", false);
            return true;
        }

        private string ResolvePersonalityObservation(string observation)
        {
            string firstPerson = ObservationToFirstPerson(observation);
            if (string.IsNullOrWhiteSpace(firstPerson)) return string.Empty;

            string[] reactions;
            switch (GetPersonalityKey())
            {
                case "Mage":
                    reactions = new[] { "Worth remembering.", "I should take a closer look.", "There may be more to learn here." };
                    break;
                case "Ritual":
                    reactions = new[] { "Interesting.", "There may be something hidden in this.", "I wonder what lies beneath the obvious." };
                    break;
                case "Lady":
                    reactions = new[] { "I should proceed with care.", "Best not to make assumptions.", "A measured approach will serve me." };
                    break;
                case "Lord":
                    reactions = new[] { "Noted.", "It will not alter my course.", "I will decide whether it matters." };
                    break;
                case "Warrior":
                    reactions = new[] { "Stay ready.", "Better to be prepared.", "I will face whatever comes of it." };
                    break;
                case "Thief":
                    reactions = new[] { "Best keep my eyes open.", "There may be an angle here.", "I should watch before I act." };
                    break;
                case "Lover":
                    reactions = new[] { "I will take it as it comes.", "No need to rush to judgment.", "I should see what becomes of this." };
                    break;
                case "Serpent":
                    reactions = new[] { "This could get interesting.", "I have a feeling this may turn entertaining.", "I should see where this leads." };
                    break;
                case "Steed":
                    reactions = new[] { "No reason to stop moving.", "I will keep moving.", "Another detail along the road." };
                    break;
                case "Tower":
                    reactions = new[] { "There may be something to gain here.", "I should see whether this is useful.", "Opportunity often hides in plain sight." };
                    break;
                case "Atronach":
                    reactions = new[] { "I should understand it before I act.", "There is more here than first appears.", "I will study the situation before deciding." };
                    break;
                case "Shadow":
                    reactions = new[] { "I do not trust first impressions.", "I should watch this carefully.", "Something about this deserves suspicion." };
                    break;
                default:
                    return firstPerson;
            }

            int index = StableObservationVariant(observation + "|" + GetPersonalityKey(), reactions.Length);
            return EnsureSentenceEnd(firstPerson) + " " + reactions[index];
        }

        private static string ObservationToFirstPerson(string observation)
        {
            string text = (observation ?? string.Empty).Trim();
            if (text.Length == 0) return string.Empty;

            string converted;
            if (StartsWithIgnoreCase(text, "You see ")) converted = "I see " + text.Substring(8).TrimStart();
            else if (StartsWithIgnoreCase(text, "You notice ")) converted = "I notice " + text.Substring(11).TrimStart();
            else if (StartsWithIgnoreCase(text, "You hear ")) converted = "I hear " + text.Substring(9).TrimStart();
            else if (StartsWithIgnoreCase(text, "You smell ")) converted = "I smell " + text.Substring(10).TrimStart();
            else if (StartsWithIgnoreCase(text, "You feel ")) converted = "I feel " + text.Substring(9).TrimStart();
            else if (StartsWithIgnoreCase(text, "You are ")) converted = "I am " + text.Substring(8).TrimStart();
            else if (StartsWithIgnoreCase(text, "You have ")) converted = "I have " + text.Substring(9).TrimStart();
            else if (StartsWithIgnoreCase(text, "You can ")) converted = "I can " + text.Substring(8).TrimStart();
            else if (StartsWithIgnoreCase(text, "Your ")) converted = "My " + text.Substring(5).TrimStart();
            else converted = text;

            return converted;
        }

        private static bool StartsWithIgnoreCase(string text, string prefix)
        {
            return text != null && prefix != null && text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static string EnsureSentenceEnd(string text)
        {
            string trimmed = (text ?? string.Empty).Trim();
            if (trimmed.Length == 0) return string.Empty;
            char last = trimmed[trimmed.Length - 1];
            return last == '.' || last == '!' || last == '?' ? trimmed : trimmed + ".";
        }

        private static int StableObservationVariant(string observation, int count)
        {
            if (count <= 1) return 0;
            string hash = Sha1(observation ?? string.Empty);
            int value = 0;
            if (hash.Length >= 2)
                int.TryParse(hash.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
            return Math.Abs(value) % count;
        }

        public static bool DialogueSpeechActive
        {
            get
            {
                if (instance == null) return false;
                if (instance.activeSpeech != null && instance.activeSpeech.Priority == SpeechPriority.Dialogue) return true;
                foreach (PlayerSpeechRequest req in instance.speechQueue)
                    if (req != null && req.Priority == SpeechPriority.Dialogue) return true;
                return false;
            }
        }

        private void Awake()
        {
            instance = this;
            audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
            audioSource.loop = false;
            audioSource.spatialBlend = 0f;

            rootDir = Path.Combine(Application.persistentDataPath, "PlayerVO");
            cacheDir = Path.Combine(rootDir, "Cache");
            configPath = Path.Combine(rootDir, "PlayerVO.ini");
            barksPath = Path.Combine(rootDir, "Barks.json");
            Directory.CreateDirectory(rootDir);
            Directory.CreateDirectory(cacheDir);

            LoadAllSettings();
            voiceEngine = new DaggerfallVoiceEngineClient(this, "Player", "0.3.7",
                delegate { return config == null ? 5000 : config.KokoroPort; }, delegate { return config == null || config.AutoStartVoiceEngine; }, 0.15f);
            StartCoroutine(voiceEngine.EnsureRunning());
            if (mod != null && mod.HasSettings)
            {
                // DFU's documented live-settings pattern requires assigning the callback and then
                // calling LoadSettings(). Without that initial call, the in-game settings screen can
                // persist values without reliably pushing live callbacks to the mod.
                mod.LoadSettingsCallback = NativeSettingsChanged;
                try
                {
                    mod.LoadSettings();
                }
                catch (Exception ex) { Debug.LogWarning("[PlayerVO] Live settings registration failed: " + ex.Message); }
            }

            RegisterConsoleCommands();
            SnapshotPlayerVitals();
            nextCacheCleanupTime = Time.realtimeSinceStartup + 5f;
        }

        private void OnDestroy()
        {
            StopCharacterVoicePreview();
            if (voiceEngine != null)
            {
                if (!string.IsNullOrEmpty(activeVoiceEngineTurnId))
                    StartCoroutine(voiceEngine.CancelTurn(activeVoiceEngineTurnId));
                StartCoroutine(voiceEngine.CancelModule());
            }
            UnhookChoiceBox();
            EndNpcvoPlayerTurn();
            if (cachedConversationPortrait != null)
            {
                try { Destroy(cachedConversationPortrait); } catch { }
                cachedConversationPortrait = null;
            }
            if (instance == this)
                instance = null;
        }


        private void HandlePlayerEntityTransition(object newPlayerEntity)
        {
            // Save/load and character switches can replace PlayerEntity while this DontDestroyOnLoad
            // component survives. Drop every transient speech/UI state and cancel all engine turns
            // owned by PlayerVO so a dead turn can never block the suite.
            StopCharacterVoicePreview();
            if (voiceEngine != null)
                StartCoroutine(voiceEngine.CancelModule());
            activeVoiceEngineTurnId = string.Empty;

            speechGeneration++;
            speechQueue.Clear();
            if (speechRoutine != null)
            {
                StopCoroutine(speechRoutine);
                speechRoutine = null;
            }
            activeSpeech = null;
            if (audioSource != null)
            {
                AudioClip clip = audioSource.clip;
                audioSource.Stop();
                audioSource.clip = null;
                if (clip != null) Destroy(clip);
            }

            UnhookChoiceBox();
            EndNpcvoPlayerTurn();
            activePlayerSubtitle = string.Empty;
            playerSubtitleUntil = 0f;
            automaticCooldownUntil = 0f;
            manualCooldownUntil = 0f;
            typedCooldownUntil = 0f;
            vanillaVocalBlockUntil = 0f;
            barkKeyHeld = false;
            barkKeyWasDown = false;
            barkHoldOpenedInput = false;
            playerDead = false;

            lastHealth = -1;
            lastFatigue = -1;
            lastMagicka = -1;
            lastBreath = -1;
            lastEnemyAlert = false;
            lastEncumbered = false;
            lastWorldContext = string.Empty;
            lastLocationName = string.Empty;
            lastBuildingContext = string.Empty;
            affiliationCacheUntil = 0f;
            cachedPrimaryGuildKey = string.Empty;
            cachedPrimaryGuildName = string.Empty;
            cachedPrimaryGuildTitle = string.Empty;
            cachedLocalReputationBand = "Neutral";
            recentContextEvent = "General";
            recentContextEventTime = 0f;
            categoryLastSpoken.Clear();
            recentLines.Clear();
            knownQuestIds.Clear();
            questSnapshotInitialized = false;
            loadedCharacterBirthsignKey = string.Empty;
            loadedCharacterVoiceKey = string.Empty;
            lastNpcvoIdentityKey = string.Empty;
            nextNpcvoIdentitySync = 0f;
            InvalidateConversationPortrait();

            // Do not clear pendingCharacterBirthsign or pendingCharacterVoiceSave here. Character creation
            // selects both before PlayerEntity exists; the first entity transition persists them.
            if (newPlayerEntity != null)
                SnapshotPlayerVitals();

            Debug.Log("[PlayerVO] Player entity changed; transient speech state and Voice Engine ownership reset.");
        }

        private void Update()
        {
            if (config == null || !config.Enabled)
                return;

            object playerEntityToken = GameManager.Instance == null ? null : (object)GameManager.Instance.PlayerEntity;
            if (!ReferenceEquals(playerEntityToken, observedPlayerEntityToken))
            {
                HandlePlayerEntityTransition(playerEntityToken);
                observedPlayerEntityToken = playerEntityToken;
            }

            // Character creation runs before PlayerEntity exists. Keep all UI-only integration ahead
            // of the gameplay guard so the biography/summary flow can always attach its personality UI.
            float now = Time.realtimeSinceStartup;
            if (voiceEngine != null && now >= nextVoiceEnginePresence)
            {
                nextVoiceEnginePresence = now + 20f;
                StartCoroutine(voiceEngine.PulsePresence());
            }
            if (DaggerfallUI.UIManager != null)
                PollBiographyBirthsignIntegration();

            if (GameManager.Instance == null || GameManager.Instance.PlayerEntity == null)
                return;

            ApplyVolume();
            PollBarkKey();
            PollChoiceWindow();
            SyncCharacterBirthsignPersistence();
            if (now >= nextNpcvoIdentitySync)
            {
                nextNpcvoIdentitySync = now + 0.75f;
                if (config.IntegrateNpcvo && config.EnhancedNpcvoConversationUI)
                    SyncNpcvoPlayerIdentity(false);
            }
            if (now >= nextConditionPoll)
            {
                nextConditionPoll = now + 0.15f;
                PollPlayerConditions();
                PollCombatAndWorldContext();
                PollWeaponAndMagicContext();
            }
            if (now >= nextIntegrationPoll)
            {
                nextIntegrationPoll = now + 1.0f;
                PollClimatesCalories();
            }
            if (now >= nextQuestPoll)
            {
                nextQuestPoll = now + 2.0f;
                PollQuestStructure();
            }
            if (now >= nextCacheCleanupTime)
            {
                nextCacheCleanupTime = now + 1800f;
                if (config.AutoManageCache)
                    CleanupCache();
            }

            if (speechRoutine == null && speechQueue.Count > 0)
                speechRoutine = StartCoroutine(ProcessSpeechQueue());
        }

        #region Settings

        private void LoadAllSettings()
        {
            config = PlayerVOConfig.LoadOrCreate(configPath);
            if (mod != null && mod.HasSettings)
            {
                try { ApplyNativeSettings(mod.GetSettings()); }
                catch (Exception ex) { Debug.LogWarning("[PlayerVO] Native settings unavailable; using INI defaults: " + ex.Message); }
            }
            ParseBarkKey();
            LoadOrCreateBarks();
        }

        private void NativeSettingsChanged(ModSettings settings, ModSettingsChange change)
        {
            if (config == null || settings == null)
                return;
            int oldPortraitStyle = config.PlayerPortraitStyle;
            string oldVoice = GetEffectivePlayerVoice();
            string oldStyle = config.AudioStyle;
            int oldDepth = config.VoiceDepth;
            float oldSpeed = config.KokoroSpeed;
            ApplyNativeSettings(settings);
            config.Save(configPath);
            ParseBarkKey();
            ApplyVolume();

            if (oldPortraitStyle != config.PlayerPortraitStyle)
                InvalidateConversationPortrait();

            bool voiceChanged = !string.Equals(oldVoice, GetEffectivePlayerVoice(), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(oldStyle, config.AudioStyle, StringComparison.OrdinalIgnoreCase) ||
                config.VoiceDepth != oldDepth || Math.Abs(config.KokoroSpeed - oldSpeed) > 0.0001f;
            if (voiceChanged)
                StopCurrentSpeech(false);
            SaveActiveCharacterVoiceProfile();
        }

        private void ApplyNativeSettings(ModSettings settings)
        {
            config.Enabled = settings.GetValue<bool>("General", "Enabled");
            config.AutomaticBarks = settings.GetValue<bool>("General", "AutomaticBarks");
            config.VoiceDialogueChoices = settings.GetValue<bool>("General", "VoiceDialogueChoices");
            config.EnhancedNpcvoConversationUI = settings.GetValue<bool>("General", "EnhancedNpcvoConversationUI");
            config.PlayerPortraitStyle = Mathf.Clamp(settings.GetValue<int>("General", "PlayerPortraitStyle"), 0, 2);

            config.BarkKeyMode = Mathf.Clamp(settings.GetValue<int>("Barks", "BarkKeyMode"), 0, 2);
            string key = settings.GetValue<string>("Barks", "BarkKey");
            if (!string.IsNullOrWhiteSpace(key)) config.BarkKeyName = key.Trim();
            config.HoldToTypeMilliseconds = Mathf.Clamp(settings.GetValue<int>("Barks", "HoldToTypeMilliseconds"), 150, 1200);
            config.BarkFrequency = Mathf.Clamp(settings.GetValue<int>("Barks", "Frequency"), 0, 2);
            config.AutomaticCooldownSeconds = Mathf.Clamp(settings.GetValue<int>("Barks", "AutomaticCooldownSeconds"), 5, 180);
            config.ManualCooldownSeconds = Mathf.Clamp(settings.GetValue<int>("Barks", "ManualCooldownSeconds"), 1, 10);
            config.ActionMovieMode = Mathf.Clamp(settings.GetValue<int>("Barks", "ActionMovieMode"), 0, 2);
            config.SubtitleMode = Mathf.Clamp(settings.GetValue<int>("Barks", "SubtitleMode"), 0, 2);

            config.VoiceAccent = Mathf.Clamp(settings.GetValue<int>("Voice", "Accent"), 0, 1);
            config.VoiceGender = Mathf.Clamp(settings.GetValue<int>("Voice", "Gender"), 0, 1);
            config.VoiceDelivery = Mathf.Clamp(settings.GetValue<int>("Voice", "Delivery"), 0, 4);
            config.VoiceDepth = Mathf.Clamp(settings.GetValue<int>("Voice", "Depth"), 0, 100);
            config.KokoroSpeed = Mathf.Clamp(settings.GetValue<int>("Voice", "Speed") / 100f, 0.50f, 1.50f);
            string voiceOverride = settings.GetValue<string>("Voice", "VoiceOverride");
            config.VoiceOverride = string.IsNullOrWhiteSpace(voiceOverride) ? string.Empty : voiceOverride.Trim();
            config.PlayerVoice = ResolveCuratedEnglishVoice(config.VoiceAccent, config.VoiceGender, config.VoiceDelivery);
            config.Volume = Mathf.Clamp(settings.GetValue<int>("Voice", "Volume"), 0, 100);
            config.UseGameSoundVolume = settings.GetValue<bool>("Voice", "UseGameSoundVolume");
            int style = Mathf.Clamp(settings.GetValue<int>("Voice", "AudioStyle"), 0, 2);
            config.AudioStyle = style == 1 ? "cdrom" : (style == 2 ? "dos" : "clean");
            config.CharacterCreationVoice = settings.GetValue<bool>("Voice", "CharacterCreationVoice");

            config.Personality = Mathf.Clamp(settings.GetValue<int>("Personality", "Birthsign"), 0, 12);
            config.RaceFlavor = settings.GetValue<bool>("Personality", "RaceFlavor");
            config.CharacterCreationBirthsign = settings.GetValue<bool>("Personality", "CharacterCreationIntegration");
            config.AutoStartVoiceEngine = settings.GetValue<bool>("Integrations", "AutoStartVoiceEngine");

            config.IntegrateClimatesCalories = settings.GetValue<bool>("Integrations", "ClimatesCalories");
            config.IntegrateNpcvo = settings.GetValue<bool>("Integrations", "NPCVO");
            config.ConditionBarks = settings.GetValue<bool>("Integrations", "HealthAndVitals");
            config.QuestAwareBarks = settings.GetValue<bool>("Integrations", "QuestAwareBarks");

            int cache = settings.GetValue<int>("Cache", "MaxCacheSize");
            config.MaxCacheSizeMB = cache == 0 ? 250 : cache == 1 ? 500 : cache == 3 ? 2048 : cache == 4 ? 5120 : cache == 5 ? 0 : 1024;
            config.AutoManageCache = settings.GetValue<bool>("Cache", "AutoManageCache");
        }

        private static string ResolveCuratedEnglishVoice(int accent, int gender, int delivery)
        {
            // accent: 0 US, 1 UK; gender: 0 male, 1 female; delivery: neutral/warm/refined/energetic/rugged.
            string[,] us = new string[,] {
                { "am_michael", "am_liam", "am_eric", "am_puck", "am_granite" },
                { "af_heart", "af_bella", "af_aoede", "af_nova", "af_kore" }
            };
            string[,] uk = new string[,] {
                { "bm_george", "bm_daniel", "bm_fable", "bm_lewis", "bm_george" },
                { "bf_emma", "bf_lily", "bf_alice", "bf_isabella", "bf_emma" }
            };
            int g = Mathf.Clamp(gender, 0, 1);
            int d = Mathf.Clamp(delivery, 0, 4);
            return accent == 1 ? uk[g, d] : us[g, d];
        }

        private string GetEffectivePlayerVoice()
        {
            if (config == null) return "am_granite";
            return string.IsNullOrWhiteSpace(config.VoiceOverride) ? config.PlayerVoice : config.VoiceOverride.Trim();
        }

        private float GetVoiceDepthSemitones()
        {
            return Mathf.Clamp((50 - config.VoiceDepth) / 20f, -2.5f, 2.5f);
        }

        private void ParseBarkKey()
        {
            KeyCode parsed;
            barkKey = Enum.TryParse<KeyCode>(config.BarkKeyName, true, out parsed) ? parsed : KeyCode.V;
        }

        private void ApplyVolume()
        {
            if (audioSource == null || config == null) return;
            float v = Mathf.Clamp01(config.Volume / 100f);
            if (config.UseGameSoundVolume)
                v *= Mathf.Clamp01(DaggerfallUnity.Settings.SoundVolume);
            audioSource.volume = v;
        }

        #endregion

        #region Character creation birthsign integration

        private void PollBiographyBirthsignIntegration()
        {
            if (config == null || DaggerfallUI.UIManager == null)
                return;

            object top = DaggerfallUI.UIManager.TopWindow;
            bool inCharacterCreation = IsCharacterCreationContext(top);
            if (inCharacterCreation && !characterCreationSessionActive)
            {
                biographyPromptQueued = false;
                biographyPromptShown = false;
                biographyResultPromptSeen = false;
                characterCreationVoicePromptShown = false;
                packagedCharacterVoices.Clear();
                biographySuggestedBirthsign = config.Personality;
                biographySuggestionReason = string.Empty;
                characterCreationSummaryOwner = null;
            }
            characterCreationSessionActive = inCharacterCreation;

            if (!inCharacterCreation)
            {
                characterCreationSummaryOwner = null;
                return;
            }

            // Track the actual DFU summary instance through our popup chain. Restarting character
            // creation pops the old summary while remaining inside CreateChar* windows; without this
            // reset PlayerVO can incorrectly remember that Personality was already completed and jump
            // straight to Voice on the second pass.
            CreateCharSummary currentSummary = FindSummaryInPreviousChain(top);
            if (characterCreationSummaryOwner != null && currentSummary == null)
            {
                biographyPromptQueued = false;
                biographyPromptShown = false;
                biographyResultPromptSeen = false;
                characterCreationVoicePromptShown = false;
                packagedCharacterVoices.Clear();
                biographySuggestedBirthsign = config.Personality;
                biographySuggestionReason = string.Empty;
                characterCreationSummaryOwner = null;
            }
            else if (currentSummary != null && !ReferenceEquals(currentSummary, characterCreationSummaryOwner))
            {
                characterCreationSummaryOwner = currentSummary;
                biographyPromptShown = false;
                characterCreationVoicePromptShown = false;
            }

            RefreshCharacterSummaryPersonalityControl(top);

            // Voice setup is a separate character-creation step. It only becomes eligible once the
            // personality picker has completed (or personality integration is disabled), and its
            // catalog is built strictly from voice files physically present in the packaged engine.
            CreateCharSummary voiceSummary = top as CreateCharSummary;
            bool personalityStepComplete = biographyPromptShown || !config.CharacterCreationBirthsign;
            if (voiceSummary != null && personalityStepComplete && config.CharacterCreationVoice && !characterCreationVoicePromptShown)
            {
                ShowCharacterCreationVoicePicker();
                return;
            }

            if (!config.CharacterCreationBirthsign)
                return;

            CreateCharBiography biography = FindBiographyInPreviousChain(top);
            if (biography != null)
            {
                // Capture the completed biography even while DFU's generated-results message is on top.
                // The picker is deliberately not shown here; CreateCharSummary is the stable hand-off point.
                try
                {
                    if (biography.BackStory != null && biography.BackStory.Count > 0 && !biographyPromptQueued && !biographyPromptShown)
                    {
                        biographySuggestedBirthsign = InferBirthsignFromBiography(biography);
                        biographySuggestionReason = GetBirthsignDescription(biographySuggestedBirthsign);
                        biographyPromptQueued = true;
                        biographyResultPromptSeen = false;
                    }
                }
                catch { }
                if (top is CreateCharBiography)
                    return;
            }

            // DFU can place its generated-results message between Biography and Summary. Remember that
            // transition, but never cover or replace the native message box.
            if (biographyPromptQueued && top is DaggerfallMessageBox)
            {
                biographyResultPromptSeen = true;
                return;
            }

            CreateCharSummary summary = top as CreateCharSummary;
            if (summary == null || biographyPromptShown)
                return;

            // Summary is the authoritative personality-selection anchor. Even if a UI replacement
            // prevented biography inference, present the picker once using the configured personality
            // as a safe default so character creation can never silently skip PlayerVO setup.
            int suggested = biographyPromptQueued ? biographySuggestedBirthsign : config.Personality;
            bool hasBiographySuggestion = biographyPromptQueued;
            biographyPromptQueued = false;
            biographyResultPromptSeen = false;
            biographyPromptShown = true;
            ShowBirthsignPicker(suggested, hasBiographySuggestion);
        }

        private static bool IsCharacterCreationContext(object top)
        {
            object cursor = top;
            for (int i = 0; i < 10 && cursor != null; i++)
            {
                string name = cursor.GetType().Name ?? string.Empty;
                if (name.StartsWith("CreateChar", StringComparison.OrdinalIgnoreCase))
                    return true;
                cursor = ReadMember(cursor, "PreviousWindow");
            }
            return false;
        }

        private static CreateCharBiography FindBiographyInPreviousChain(object top)
        {
            object cursor = top;
            for (int i = 0; i < 8 && cursor != null; i++)
            {
                CreateCharBiography biography = cursor as CreateCharBiography;
                if (biography != null) return biography;
                cursor = ReadMember(cursor, "PreviousWindow");
            }
            return null;
        }

        private static CreateCharSummary FindSummaryInPreviousChain(object top)
        {
            object cursor = top;
            for (int i = 0; i < 8 && cursor != null; i++)
            {
                CreateCharSummary summary = cursor as CreateCharSummary;
                if (summary != null) return summary;
                cursor = ReadMember(cursor, "PreviousWindow");
            }
            return null;
        }

        private void RefreshCharacterSummaryPersonalityControl(object top)
        {
            // The personality picker already appears at the stable CreateCharSummary hand-off.
            // Do not inject extra labels or buttons into DFU's crowded summary sheet: they overlap
            // vanilla skills/reflex controls at several resolutions and UI scales.
            personalitySummaryOwner = top as CreateCharSummary;
        }

        private int InferBirthsignFromBiography(CreateCharBiography biography)
        {
            int[] score = new int[13];
            string corpus = string.Empty;
            try { if (biography.BackStory != null) corpus += " " + string.Join(" ", biography.BackStory.ToArray()); } catch { }
            try { if (biography.PlayerEffects != null) corpus += " " + string.Join(" ", biography.PlayerEffects.ToArray()); } catch { }
            corpus = corpus.ToLowerInvariant();
            AddScoreForWords(score, 0, corpus, new[] { "magic", "spell", "book", "study", "scholar", "mage", "wisdom", "mystic" }, 3);
            AddScoreForWords(score, 1, corpus, new[] { "daedra", "necrom", "forbidden", "dark", "death", "ritual", "power" }, 3);
            AddScoreForWords(score, 2, corpus, new[] { "court", "noble", "etiquette", "diplom", "kind", "mercy", "reputation" }, 2);
            AddScoreForWords(score, 3, corpus, new[] { "command", "rule", "authority", "noble", "lord", "dominat", "power" }, 2);
            AddScoreForWords(score, 4, corpus, new[] { "blade", "weapon", "armor", "battle", "warrior", "honor", "protect" }, 3);
            AddScoreForWords(score, 5, corpus, new[] { "steal", "thief", "pickpocket", "lock", "backstab", "streetwise", "crime" }, 3);
            AddScoreForWords(score, 6, corpus, new[] { "friend", "family", "love", "help", "heal", "mercy", "kind" }, 3);
            AddScoreForWords(score, 7, corpus, new[] { "poison", "murder", "cruel", "kill", "assassin", "betray" }, 3);
            AddScoreForWords(score, 8, corpus, new[] { "travel", "road", "ride", "run", "swim", "climb", "adventure" }, 3);
            AddScoreForWords(score, 9, corpus, new[] { "treasure", "relic", "artifact", "tomb", "ruin", "gold", "loot" }, 3);
            AddScoreForWords(score, 10, corpus, new[] { "endurance", "resist", "survive", "heal", "professional", "monster" }, 2);
            AddScoreForWords(score, 11, corpus, new[] { "stealth", "secret", "spy", "ambush", "hunt", "track", "suspicious" }, 3);
            int best = 12; int bestScore = 0;
            for (int i = 0; i < 12; i++) if (score[i] > bestScore) { best = i; bestScore = score[i]; }
            return bestScore < 2 ? 12 : best;
        }

        private static void AddScoreForWords(int[] score, int index, string corpus, string[] words, int points)
        {
            if (score == null || index < 0 || index >= score.Length || string.IsNullOrEmpty(corpus)) return;
            for (int i = 0; i < words.Length; i++) if (corpus.IndexOf(words[i], StringComparison.OrdinalIgnoreCase) >= 0) score[index] += points;
        }

        private void ShowBirthsignPicker(int suggested, bool fromBiography)
        {
            if (DaggerfallUI.UIManager == null) return;
            DaggerfallBaseWindow previous = DaggerfallUI.UIManager.TopWindow as DaggerfallBaseWindow;
            BirthsignPickerWindow picker = new BirthsignPickerWindow(DaggerfallUI.UIManager, previous, suggested, fromBiography,
                delegate(int index)
                {
                    config.Personality = Mathf.Clamp(index, 0, 12);
                    pendingCharacterBirthsign = config.Personality;
                    try { config.Save(configPath); } catch { }
                    Debug.Log("[PlayerVO] Character archetype selected: " + GetBirthsignName(config.Personality));
                });
            DaggerfallUI.UIManager.PushWindow(picker);
        }

        private void ShowCharacterCreationVoicePicker()
        {
            if (DaggerfallUI.UIManager == null || config == null) return;
            characterCreationVoicePromptShown = true;
            packagedCharacterVoices.Clear();
            packagedCharacterVoices.AddRange(DiscoverPackagedVoiceIds());

            DaggerfallBaseWindow previous = DaggerfallUI.UIManager.TopWindow as DaggerfallBaseWindow;
            int initialGenderFilter = 0; // 0 All, 1 Female, 2 Male. Filter only; never a restriction.
            try
            {
                CreateCharSummary summary = FindSummaryInPreviousChain(DaggerfallUI.UIManager.TopWindow);
                object document = summary == null ? null : (object)summary.CharacterDocument;
                object gender = ReadMember(document, "gender");
                string genderName = gender == null ? string.Empty : gender.ToString();
                if (genderName.IndexOf("female", StringComparison.OrdinalIgnoreCase) >= 0) initialGenderFilter = 1;
                else if (genderName.IndexOf("male", StringComparison.OrdinalIgnoreCase) >= 0) initialGenderFilter = 2;
            }
            catch { }

            CharacterVoicePickerWindow picker = new CharacterVoicePickerWindow(
                DaggerfallUI.UIManager,
                previous,
                packagedCharacterVoices.ToArray(),
                GetEffectivePlayerVoice(),
                config.VoiceDepth,
                config.KokoroSpeed,
                config.AudioStyle,
                0, // Accent starts at All so every packaged voice remains immediately reachable.
                initialGenderFilter,
                delegate(string voice, int depth, float speed, string style)
                {
                    if (string.IsNullOrWhiteSpace(voice)) return;
                    StopCharacterVoicePreview();
                    config.VoiceOverride = voice.Trim();
                    config.VoiceDepth = Mathf.Clamp(depth, 0, 100);
                    config.KokoroSpeed = Mathf.Clamp(speed, 0.50f, 1.50f);
                    config.AudioStyle = NormalizeAudioStyle(style);
                    pendingCharacterVoiceSave = true;
                    try { config.Save(configPath); } catch { }
                    Debug.Log("[PlayerVO] Character voice selected: " + config.VoiceOverride +
                        " depth=" + config.VoiceDepth + " speed=" + config.KokoroSpeed.ToString("0.00", CultureInfo.InvariantCulture) +
                        " style=" + config.AudioStyle);
                },
                delegate(string voice, int depth, float speed, string style)
                {
                    PreviewCharacterVoice(voice, depth, speed, style);
                },
                delegate { StopCharacterVoicePreview(); });
            DaggerfallUI.UIManager.PushWindow(picker);
        }

        private List<string> DiscoverPackagedVoiceIds()
        {
            List<string> voices = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string root = Path.Combine(Application.streamingAssetsPath, "DaggerfallVoiceEngine", "Assets");
                string[] dirs = { Path.Combine(root, "voices"), Path.Combine(root, "voices-custom") };
                for (int d = 0; d < dirs.Length; d++)
                {
                    if (!Directory.Exists(dirs[d])) continue;
                    string[] files = Directory.GetFiles(dirs[d], "*.npy", SearchOption.TopDirectoryOnly);
                    for (int i = 0; i < files.Length; i++)
                    {
                        string id = Path.GetFileNameWithoutExtension(files[i]);
                        if (!string.IsNullOrWhiteSpace(id) && seen.Add(id.Trim())) voices.Add(id.Trim());
                    }
                }
            }
            catch (Exception ex) { Debug.LogWarning("[PlayerVO] Could not enumerate packaged Voice Engine voices: " + ex.Message); }

            voices.Sort(delegate(string a, string b)
            {
                int ga = VoiceSortGroup(a), gb = VoiceSortGroup(b);
                if (ga != gb) return ga.CompareTo(gb);
                return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            });
            return voices;
        }

        private static int VoiceSortGroup(string id)
        {
            if (string.IsNullOrEmpty(id)) return 99;
            string v = id.ToLowerInvariant();
            if (v.StartsWith("am_") || v.StartsWith("af_")) return 0;
            if (v.StartsWith("bm_") || v.StartsWith("bf_")) return 1;
            return 2;
        }

        private static string NormalizeAudioStyle(string style)
        {
            string s = (style ?? string.Empty).Trim().ToLowerInvariant();
            return s == "cdrom" || s == "dos" ? s : "clean";
        }

        private void PreviewCharacterVoice(string voice, int depth, float speed, string style)
        {
            if (string.IsNullOrWhiteSpace(voice)) return;
            StopCharacterVoicePreview();
            int generation = ++voicePreviewGeneration;
            voicePreviewRoutine = StartCoroutine(PreviewCharacterVoiceRoutine(
                voice.Trim(), Mathf.Clamp(depth, 0, 100), Mathf.Clamp(speed, 0.50f, 1.50f), NormalizeAudioStyle(style), generation));
        }

        private void StopCharacterVoicePreview()
        {
            voicePreviewGeneration++;
            if (voicePreviewRoutine != null)
            {
                try { StopCoroutine(voicePreviewRoutine); } catch { }
                voicePreviewRoutine = null;
            }
            if (audioSource != null && audioSource.isPlaying)
            {
                AudioClip clip = audioSource.clip;
                audioSource.Stop();
                audioSource.clip = null;
                if (clip != null) try { Destroy(clip); } catch { }
            }
            if (voiceEngine != null && !string.IsNullOrEmpty(voicePreviewTurnId))
                StartCoroutine(voiceEngine.CancelTurn(voicePreviewTurnId));
            voicePreviewTurnId = string.Empty;
        }

        private IEnumerator PreviewCharacterVoiceRoutine(string voice, int depth, float speed, string style, int generation)
        {
            const string sample = "The road ahead is long, but I have work to do.";
            string wav = Path.Combine(cacheDir, "cc-preview-" + Sha1(sample + "|" + voice + "|" + depth.ToString(CultureInfo.InvariantCulture) +
                "|" + speed.ToString("0.00", CultureInfo.InvariantCulture) + "|" + style) + ".wav");

            if (!File.Exists(wav))
            {
                DaggerfallVoiceEngineTurn turn = new DaggerfallVoiceEngineTurn();
                if (voiceEngine != null)
                    yield return StartCoroutine(voiceEngine.AcquireTurn(130, "cc-preview", Sha1(sample + "|" + voice), 30000,
                        delegate { return generation == voicePreviewGeneration; }, turn));
                if (generation != voicePreviewGeneration) yield break;
                if (!turn.Ready)
                {
                    DaggerfallUI.AddHUDText(turn.EngineOnline ? "Voice preview is busy. Try again." : "Daggerfall Voice Engine is not available.");
                    voicePreviewRoutine = null;
                    yield break;
                }
                voicePreviewTurnId = turn.Id;

                float semitones = Mathf.Clamp((50 - depth) / 20f, -2.5f, 2.5f);
                string json = "{\"text\":\"" + JsonEscape(sample) + "\",\"voice\":\"" + JsonEscape(voice) +
                    "\",\"lang\":\"" + JsonEscape(ResolveLanguage(voice)) + "\",\"speed\":" + speed.ToString(CultureInfo.InvariantCulture) +
                    ",\"pitch_semitones\":" + semitones.ToString(CultureInfo.InvariantCulture) +
                    ",\"audio_style\":\"" + JsonEscape(style) + "\",\"module\":\"Player\",\"turn_id\":\"" + JsonEscape(voicePreviewTurnId) +
                    "\",\"emotion\":\"neutral\",\"emotion_intensity\":0.15}";
                string url = "http://127.0.0.1:" + config.KokoroPort + "/synthesize";
                using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
                {
                    request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.SetRequestHeader("Content-Type", "application/json");
                    request.timeout = 120;
                    UnityWebRequestAsyncOperation op = request.SendWebRequest();
                    while (!op.isDone)
                    {
                        if (generation != voicePreviewGeneration) { request.Abort(); yield break; }
                        yield return null;
                    }
#if UNITY_2020_1_OR_NEWER
                    bool failed = request.result != UnityWebRequest.Result.Success;
#else
                    bool failed = request.isNetworkError || request.isHttpError;
#endif
                    if (!failed && request.downloadHandler != null && request.downloadHandler.data != null && request.downloadHandler.data.Length > 44)
                    {
                        try { File.WriteAllBytes(wav, request.downloadHandler.data); } catch { }
                    }
                    else
                    {
                        Debug.LogWarning("[PlayerVO] Character-creation voice preview synthesis failed: " + request.error);
                        DaggerfallUI.AddHUDText("Voice preview failed for " + voice + ".");
                    }
                }
            }

            if (generation != voicePreviewGeneration) yield break;
            AudioClip previewClip = null;
            if (File.Exists(wav))
                yield return StartCoroutine(LoadWav(wav, delegate(AudioClip c) { previewClip = c; }));
            if (generation != voicePreviewGeneration)
            {
                if (previewClip != null) Destroy(previewClip);
                yield break;
            }
            if (previewClip != null)
            {
                audioSource.Stop();
                audioSource.clip = previewClip;
                ApplyVolume();
                audioSource.Play();
                while (audioSource.isPlaying && generation == voicePreviewGeneration) yield return null;
                if (audioSource.clip == previewClip) audioSource.clip = null;
                Destroy(previewClip);
            }

            if (voiceEngine != null && !string.IsNullOrEmpty(voicePreviewTurnId))
                yield return StartCoroutine(voiceEngine.CompleteTurn(voicePreviewTurnId));
            voicePreviewTurnId = string.Empty;
            voicePreviewRoutine = null;
        }

        private void SyncCharacterBirthsignPersistence()
        {
            if (GameManager.Instance == null || GameManager.Instance.PlayerEntity == null) return;
            string name = GameManager.Instance.PlayerEntity.Name;
            if (string.IsNullOrWhiteSpace(name)) return;
            string key = MakeContextKey(name);
            if (string.IsNullOrEmpty(key)) return;
            string dir = Path.Combine(rootDir, "Characters");
            string path = Path.Combine(dir, key + ".birthsign");
            if (pendingCharacterBirthsign >= 0)
            {
                try { Directory.CreateDirectory(dir); File.WriteAllText(path, pendingCharacterBirthsign.ToString(CultureInfo.InvariantCulture)); } catch { }
                loadedCharacterBirthsignKey = key;
                pendingCharacterBirthsign = -1;
            }
            else if (!string.Equals(loadedCharacterBirthsignKey, key, StringComparison.OrdinalIgnoreCase))
            {
                loadedCharacterBirthsignKey = key;
                try
                {
                    int value;
                    if (File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out value))
                        config.Personality = Mathf.Clamp(value, 0, 12);
                }
                catch { }
            }

            SyncCharacterVoiceProfile(dir, key);
        }

        private void SyncCharacterVoiceProfile(string dir, string key)
        {
            if (config == null || string.IsNullOrEmpty(key)) return;
            if (string.Equals(loadedCharacterVoiceKey, key, StringComparison.OrdinalIgnoreCase)) return;
            loadedCharacterVoiceKey = key;
            string path = Path.Combine(dir, key + ".voice");
            try
            {
                Directory.CreateDirectory(dir);
                if (pendingCharacterVoiceSave)
                {
                    SaveCharacterVoiceProfile(path);
                    pendingCharacterVoiceSave = false;
                    Debug.Log("[PlayerVO] Saved character-creation voice profile for " + key + ": " + GetEffectivePlayerVoice());
                    return;
                }
                if (!File.Exists(path))
                {
                    SaveCharacterVoiceProfile(path);
                    return;
                }
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq < 1) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();
                    int i; float f;
                    if (k == "voiceoverride") config.VoiceOverride = v;
                    else if (k == "accent" && int.TryParse(v, out i)) config.VoiceAccent = Mathf.Clamp(i, 0, 1);
                    else if (k == "gender" && int.TryParse(v, out i)) config.VoiceGender = Mathf.Clamp(i, 0, 1);
                    else if (k == "delivery" && int.TryParse(v, out i)) config.VoiceDelivery = Mathf.Clamp(i, 0, 4);
                    else if (k == "depth" && int.TryParse(v, out i)) config.VoiceDepth = Mathf.Clamp(i, 0, 100);
                    else if (k == "speed" && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) config.KokoroSpeed = Mathf.Clamp(f, 0.5f, 1.5f);
                    else if (k == "audiostyle") config.AudioStyle = string.IsNullOrWhiteSpace(v) ? "clean" : v.ToLowerInvariant();
                }
                config.PlayerVoice = ResolveCuratedEnglishVoice(config.VoiceAccent, config.VoiceGender, config.VoiceDelivery);
                Debug.Log("[PlayerVO] Restored per-character voice profile for " + key + ": " + GetEffectivePlayerVoice());
            }
            catch (Exception ex) { Debug.LogWarning("[PlayerVO] Character voice profile load failed: " + ex.Message); }
        }

        private void SaveActiveCharacterVoiceProfile()
        {
            if (config == null || GameManager.Instance == null || GameManager.Instance.PlayerEntity == null) return;
            string key = MakeContextKey(GameManager.Instance.PlayerEntity.Name);
            if (string.IsNullOrEmpty(key)) return;
            string dir = Path.Combine(rootDir, "Characters");
            try { Directory.CreateDirectory(dir); SaveCharacterVoiceProfile(Path.Combine(dir, key + ".voice")); loadedCharacterVoiceKey = key; }
            catch { }
        }

        private void SaveCharacterVoiceProfile(string path)
        {
            File.WriteAllLines(path, new[] {
                "# Daggerfall Narrator - Player per-character voice profile",
                "VoiceOverride=" + (config.VoiceOverride ?? string.Empty),
                "Accent=" + config.VoiceAccent.ToString(CultureInfo.InvariantCulture),
                "Gender=" + config.VoiceGender.ToString(CultureInfo.InvariantCulture),
                "Delivery=" + config.VoiceDelivery.ToString(CultureInfo.InvariantCulture),
                "Depth=" + config.VoiceDepth.ToString(CultureInfo.InvariantCulture),
                "Speed=" + config.KokoroSpeed.ToString(CultureInfo.InvariantCulture),
                "AudioStyle=" + (config.AudioStyle ?? "clean")
            });
        }

        private static string GetBirthsignName(int index)
        {
            string[] names = { "The Mage", "The Ritual", "The Lady", "The Lord", "The Warrior", "The Thief", "The Lover", "The Serpent", "The Steed", "The Tower", "The Atronach", "The Shadow", "The Apprentice" };
            return names[Mathf.Clamp(index, 0, names.Length - 1)];
        }

        private static string GetBirthsignDescription(int index)
        {
            string[] descriptions = {
                "wise scholar; a life shaped by study, curiosity, and measured judgment",
                "forbidden mystic; familiar with rites, old graves, and knowledge best approached carefully",
                "diplomat; practiced in grace, empathy, patience, and reading a room",
                "imperious ruler; accustomed to duty, pride, command, and the weight of expectation",
                "champion; shaped by martial training, direct courage, and decisive action",
                "opportunist; streetwise, evasive, daring, and alert to useful openings",
                "compassionate; loyal, charming, emotionally perceptive, and strongly attached to others",
                "malefactor; an outsider comfortable with risk, uncertainty, and dangerous company",
                "adventurer; restless, road-worn, and more at home moving than settling",
                "possessive seeker; drawn to locks, hidden rooms, old objects, and hard-won secrets",
                "professional hunter; self-reliant, patient, economical, and accustomed to working alone",
                "bounty hunter; watchful, private, suspicious, and practiced at following a trail",
                "agent; new to the land, a quiet learner dedicated to the quest and sparing with words" };
            return descriptions[Mathf.Clamp(index, 0, descriptions.Length - 1)];
        }

        #endregion

        #region Manual Bark / Keyboard Speech

        private void PollBarkKey()
        {
            if (barkKey == KeyCode.None)
                return;

            bool down = false;
            try { down = Input.GetKey(barkKey); } catch { }
            bool pressed = false;
            bool released = false;
            try
            {
                pressed = Input.GetKeyDown(barkKey) || (down && !barkKeyWasDown);
                released = Input.GetKeyUp(barkKey) || (!down && barkKeyWasDown);
            }
            catch
            {
                pressed = down && !barkKeyWasDown;
                released = !down && barkKeyWasDown;
            }
            barkKeyWasDown = down;

            // Do not interpret the key while PlayerVO's text-entry box is active. The raw held-state
            // bookkeeping above is intentionally retained so releasing the key cannot fire a bark later.
            if (IsTextInputOpen())
            {
                barkKeyHeld = false;
                barkHoldOpenedInput = false;
                return;
            }

            if (config.BarkKeyMode == 0)
            {
                if (pressed)
                    TriggerManualContextBark();
                return;
            }
            if (config.BarkKeyMode == 1)
            {
                if (pressed)
                    OpenKeyboardSpeech();
                return;
            }

            // Hybrid: tap for context bark, hold for keyboard speech. Use both Unity edge events and
            // raw key-state transitions so the control remains reliable if another DFU input handler
            // consumes the same frame's key-down/key-up event.
            if (pressed)
            {
                barkKeyHeld = true;
                barkHoldOpenedInput = false;
                barkKeyDownTime = Time.realtimeSinceStartup;
            }

            if (barkKeyHeld && down && !barkHoldOpenedInput)
            {
                float heldMs = (Time.realtimeSinceStartup - barkKeyDownTime) * 1000f;
                if (heldMs >= config.HoldToTypeMilliseconds)
                {
                    barkHoldOpenedInput = true;
                    OpenKeyboardSpeech();
                }
            }

            if (barkKeyHeld && released)
            {
                bool shouldBark = !barkHoldOpenedInput;
                barkKeyHeld = false;
                barkHoldOpenedInput = false;
                if (shouldBark)
                    TriggerManualContextBark();
            }
        }

        private bool IsTextInputOpen()
        {
            object top = DaggerfallUI.UIManager == null ? null : DaggerfallUI.UIManager.TopWindow;
            return top is DaggerfallInputMessageBox;
        }

        private void TriggerManualContextBark()
        {
            if (Time.realtimeSinceStartup < manualCooldownUntil)
                return;
            manualCooldownUntil = Time.realtimeSinceStartup + config.ManualCooldownSeconds;
            string key = Time.realtimeSinceStartup - recentContextEventTime <= 8f ? recentContextEvent : "General";
            string line = ResolveBark(key, true);
            if (!string.IsNullOrWhiteSpace(line))
                EnqueueSpeech(line, SpeechPriority.Manual, "manual:" + key, false);
        }

        private void OpenKeyboardSpeech()
        {
            if (Time.realtimeSinceStartup < typedCooldownUntil || DaggerfallUI.UIManager == null)
                return;
            DaggerfallBaseWindow previous = DaggerfallUI.UIManager.TopWindow as DaggerfallBaseWindow;
            DaggerfallInputMessageBox box = new DaggerfallInputMessageBox(DaggerfallUI.UIManager, previous);

            // Keep the roleplay input compact enough to see the game and, crucially, the text being
            // typed. A 240-character TextBox caused DFU to size the parchment far beyond the virtual
            // screen. Use a fixed visible field and scroll its text offset as the line grows.
            box.TextBox.MaxCharacters = 160;
            box.TextBox.WidthOverride = 176;
            box.TextBox.FixedSize = true;
            box.TextBox.Size = new Vector2(126f, box.TextBox.Font.GlyphHeight + 2f);
            box.InputDistanceX = 4;
            box.SetTextBoxLabel("Speak: ");
            box.TextBox.OnType += delegate()
            {
                try
                {
                    float width = box.TextBox.Font.CalculateTextWidth(box.TextBox.Text, Vector2.one);
                    float visible = Mathf.Max(40f, box.TextBox.Size.x - 8f);
                    box.TextBox.TextOffset = width > visible ? Mathf.RoundToInt(visible - width) : 0;
                }
                catch { }
            };
            box.OnGotUserInput += delegate(DaggerfallInputMessageBox sender, string input)
            {
                if (string.IsNullOrWhiteSpace(input)) return;
                typedCooldownUntil = Time.realtimeSinceStartup + 1.25f;
                // Typed speech is sacred player authorship: never rewrite it through race/personality logic.
                EnqueueSpeech(input.Trim(), SpeechPriority.Typed, "typed", false);
            };
            box.Show();
        }

        #endregion

        #region Player condition / combat / world context

        private void SnapshotPlayerVitals()
        {
            PlayerEntity p = GameManager.Instance == null ? null : GameManager.Instance.PlayerEntity;
            if (p == null) return;
            lastHealth = p.CurrentHealth;
            lastFatigue = p.CurrentFatigue;
            lastMagicka = p.CurrentMagicka;
            lastBreath = p.CurrentBreath;
            lastEnemyAlert = p.EnemyAlertActive;
            playerDead = p.CurrentHealth <= 0;
            try { lastEncumbered = p.MaxEncumbrance > 0 && p.CarriedWeight >= p.MaxEncumbrance * 0.90f; }
            catch { lastEncumbered = false; }
        }

        private void PollPlayerConditions()
        {
            PlayerEntity p = GameManager.Instance.PlayerEntity;
            if (p == null) return;

            int health = p.CurrentHealth;
            int fatigue = p.CurrentFatigue;
            int magicka = p.CurrentMagicka;
            int breath = p.CurrentBreath;

            if (lastHealth >= 0 && health < lastHealth)
            {
                // Vanilla combat voice gets the channel. PlayerVO stops immediately and waits before talking again.
                InterruptForVanillaPlayerVocal(health <= Mathf.Max(1, p.MaxHealth / 6) ? 1.25f : 0.85f);
                RecordContext("HeavyDamage");
            }
            if (health <= 0 && !playerDead)
            {
                playerDead = true;
                InterruptForVanillaPlayerVocal(999f);
            }
            else if (health > 0 && playerDead)
            {
                playerDead = false;
                vanillaVocalBlockUntil = Time.realtimeSinceStartup + 1f;
            }

            if (config.ConditionBarks && health > 0 && p.MaxHealth > 0)
            {
                float oldRatio = lastHealth < 0 ? 1f : (float)lastHealth / p.MaxHealth;
                float ratio = (float)health / p.MaxHealth;
                if (ratio <= 0.15f && oldRatio > 0.15f) TriggerCondition("CriticalHealth", 45f);
                else if (ratio <= 0.35f && oldRatio > 0.35f) TriggerCondition("LowHealth", 35f);
                else if (ratio >= 0.65f && oldRatio < 0.35f) TriggerCondition("Recovered", 40f);
            }

            if (config.ConditionBarks && p.MaxFatigue > 0 && fatigue > 0 && lastFatigue >= 0)
            {
                float ratio = (float)fatigue / p.MaxFatigue;
                float old = (float)lastFatigue / p.MaxFatigue;
                if (ratio <= 0.20f && old > 0.20f) TriggerCondition("LowFatigue", 45f);
                // Rapid exertion receives a tiny vocal lockout to avoid stepping on vanilla exertion sounds.
                if (lastFatigue - fatigue > Mathf.Max(2, p.MaxFatigue / 20))
                    vanillaVocalBlockUntil = Mathf.Max(vanillaVocalBlockUntil, Time.realtimeSinceStartup + 0.35f);
            }

            if (config.ConditionBarks && p.MaxMagicka > 0 && lastMagicka >= 0)
            {
                float ratio = (float)magicka / p.MaxMagicka;
                float old = (float)lastMagicka / p.MaxMagicka;
                if (ratio <= 0.15f && old > 0.15f) TriggerCondition("LowMagicka", 50f);
            }

            if (config.ConditionBarks && p.MaxBreath > 0 && breath > 0 && lastBreath >= 0)
            {
                float ratio = (float)breath / p.MaxBreath;
                float old = (float)lastBreath / p.MaxBreath;
                if (ratio <= 0.25f && old > 0.25f)
                {
                    vanillaVocalBlockUntil = Mathf.Max(vanillaVocalBlockUntil, Time.realtimeSinceStartup + 1.1f);
                    TriggerCondition("LowBreath", 30f);
                }
            }

            if (config.ConditionBarks && p.MaxEncumbrance > 0)
            {
                bool encumbered = p.CarriedWeight >= p.MaxEncumbrance * 0.90f;
                if (encumbered && !lastEncumbered)
                    TriggerCondition("Encumbered", 75f);
                else if (!encumbered && lastEncumbered && p.CarriedWeight < p.MaxEncumbrance * 0.70f)
                    TriggerCondition("Unburdened", 75f);
                lastEncumbered = encumbered;
            }

            lastHealth = health;
            lastFatigue = fatigue;
            lastMagicka = magicka;
            lastBreath = breath;
        }

        private void TriggerCondition(string key, float categoryCooldown)
        {
            RecordContext(key);
            if (CategoryReady(key, categoryCooldown))
                TryAutomaticBark(key, false);
        }

        private void PollCombatAndWorldContext()
        {
            PlayerEntity p = GameManager.Instance.PlayerEntity;
            bool alert = p != null && p.EnemyAlertActive;
            if (alert && !lastEnemyAlert)
            {
                RecordContext("CombatStart");
                TryAutomaticBark("CombatStart", false);
            }
            else if (!alert && lastEnemyAlert)
            {
                RecordContext("CombatEnd");
                TryAutomaticBark("CombatEnd", true);
            }
            lastEnemyAlert = alert;

            string context = GetWorldContext();
            string locationName = GetLocationName();
            bool contextChanged = !string.Equals(context, lastWorldContext, StringComparison.Ordinal);
            bool locationChanged = !string.Equals(locationName, lastLocationName, StringComparison.OrdinalIgnoreCase);
            if (contextChanged || locationChanged)
            {
                // Location changes matter even when the broad context remains "TownEnter". This lets
                // authored Daggerfall/Wayrest/Sentinel/etc. pools outrank generic town observations.
                if (!string.IsNullOrEmpty(lastWorldContext) && !string.IsNullOrEmpty(context))
                {
                    RecordContext(context);
                    if (context == "DungeonEnter" || context == "TownEnter")
                        TryAutomaticBark(context, false);
                }
                lastWorldContext = context;
                lastLocationName = locationName;
            }

            string buildingContext = GetBuildingContextEvent();
            if (!string.Equals(buildingContext, lastBuildingContext, StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(buildingContext))
                {
                    RecordContext(buildingContext);
                    TryAutomaticBark(buildingContext, false);
                }
                lastBuildingContext = buildingContext;
            }
        }

        private string GetBuildingContextEvent()
        {
            try
            {
                PlayerEnterExit enterExit = GameManager.Instance == null ? null : GameManager.Instance.PlayerEnterExit;
                if (enterExit == null || !enterExit.IsPlayerInsideBuilding) return string.Empty;
                string type = enterExit.BuildingType.ToString();
                if (type.IndexOf("Tavern", StringComparison.OrdinalIgnoreCase) >= 0) return "TavernEnter";
                if (type.IndexOf("Temple", StringComparison.OrdinalIgnoreCase) >= 0) return "TempleEnter";
                if (type.IndexOf("Guild", StringComparison.OrdinalIgnoreCase) >= 0) return "GuildHallEnter";
                if (type.IndexOf("Library", StringComparison.OrdinalIgnoreCase) >= 0) return "LibraryEnter";
                if (type.IndexOf("Palace", StringComparison.OrdinalIgnoreCase) >= 0) return "PalaceEnter";
                if (type.IndexOf("Bank", StringComparison.OrdinalIgnoreCase) >= 0) return "BankEnter";
                if (type.IndexOf("Store", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    type.IndexOf("Shop", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    type.IndexOf("Smith", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    type.IndexOf("Alchemist", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    type.IndexOf("Bookseller", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    type.IndexOf("Pawn", StringComparison.OrdinalIgnoreCase) >= 0) return "ShopEnter";
                return "BuildingEnter";
            }
            catch { return string.Empty; }
        }

        private string GetWorldContext()
        {
            try
            {
                if (GameManager.Instance.PlayerEnterExit != null && GameManager.Instance.PlayerEnterExit.IsPlayerInsideDungeon)
                    return "DungeonEnter";
                if (GameManager.Instance.PlayerGPS != null && GameManager.Instance.PlayerGPS.IsPlayerInTown(true))
                    return "TownEnter";
                return "Wilderness";
            }
            catch { return string.Empty; }
        }

        private bool lastBowAttackDown;
        private bool lastSpellAnimation;
        private void PollWeaponAndMagicContext()
        {
            // These hooks are deliberately reflection-tolerant so PlayerVO does not bind itself to a
            // fragile implementation detail of WeaponManager or PlayerSpellCasting.
            try
            {
                object wm = ReadMember(GameManager.Instance, "WeaponManager");
                object screenWeapon = ReadMember(wm, "ScreenWeapon");
                object weaponType = ReadMember(screenWeapon, "WeaponType");
                bool bow = weaponType != null && string.Equals(weaponType.ToString(), "Bow", StringComparison.OrdinalIgnoreCase);
                bool attack = InputManager.Instance != null && InputManager.Instance.ActionStarted(InputManager.Actions.SwingWeapon);
                if (bow && attack && !lastBowAttackDown)
                {
                    RecordContext("BowDraw");
                    TryAutomaticBark("BowDraw", true);
                }
                lastBowAttackDown = attack;
            }
            catch { }

            try
            {
                object caster = ReadMember(GameManager.Instance, "PlayerSpellCasting");
                bool playing = ReadBoolMember(caster, "IsPlayingAnim", false);
                if (playing && !lastSpellAnimation)
                {
                    RecordContext("MagicCast");
                    TryAutomaticBark("MagicCast", true);
                }
                lastSpellAnimation = playing;
            }
            catch { }
        }

        private void RecordContext(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            recentContextEvent = key;
            recentContextEventTime = Time.realtimeSinceStartup;
        }

        #endregion

        #region Optional Climates & Calories

        private void EnsureClimatesCaloriesReflection()
        {
            if (ccReflectionChecked) return;
            ccReflectionChecked = true;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    ccRootType = ccRootType ?? a.GetType("ClimatesCalories.ClimateCalories", false);
                    ccHungerType = ccHungerType ?? a.GetType("ClimatesCalories.Hunger", false);
                    ccClimatesType = ccClimatesType ?? a.GetType("ClimatesCalories.Climates", false);
                }
                catch { }
            }
            if (ccRootType != null)
            {
                BindingFlags f = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                ccThirstField = ccRootType.GetField("thirst", f);
                ccWetField = ccRootType.GetField("wetCount", f);
                ccCampingField = ccRootType.GetField("camping", f);
                ccCookingField = ccRootType.GetField("cooking", f);
            }
            if (ccHungerType != null)
            {
                BindingFlags f = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                ccHungryField = ccHungerType.GetField("hungry", f);
                ccStarvingField = ccHungerType.GetField("starving", f);
            }
            if (ccClimatesType != null)
            {
                BindingFlags f = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                ccTempField = ccClimatesType.GetField("totalTemp", f) ?? ccClimatesType.GetField("natTemp", f);
            }
            if (ccRootType != null || ccHungerType != null)
                Debug.Log("[PlayerVO] Climates & Calories detected; survival bark context enabled.");
        }

        private void PollClimatesCalories()
        {
            if (!config.IntegrateClimatesCalories) return;
            EnsureClimatesCaloriesReflection();
            if (ccRootType == null && ccHungerType == null) return;

            bool hungry = ReadStaticBool(ccHungryField);
            bool starving = ReadStaticBool(ccStarvingField);
            int thirst = ReadStaticInt(ccThirstField);
            int wet = ReadStaticInt(ccWetField);
            bool camping = ReadStaticBool(ccCampingField);
            bool cooking = ReadStaticBool(ccCookingField);
            int temp = ReadStaticInt(ccTempField);
            bool thirsty = thirst >= 60;
            bool soaked = wet >= 40;
            int tempBand = temp <= -25 ? -1 : temp >= 25 ? 1 : 0;

            if (hungry && !ccLastHungry) TryAutomaticBark("Hungry", false);
            if (starving && !ccLastStarving) TryAutomaticBark("Starving", false);
            if (thirsty && !ccLastThirsty) TryAutomaticBark("Thirsty", false);
            if (soaked && !ccLastWet) TryAutomaticBark("Wet", false);
            if (camping && !ccLastCamping) TryAutomaticBark("Camping", false);
            if (cooking && !ccLastCooking) TryAutomaticBark("Cooking", false);
            if (tempBand != ccLastTempBand && tempBand < 0) TryAutomaticBark("Cold", false);
            if (tempBand != ccLastTempBand && tempBand > 0) TryAutomaticBark("Hot", false);

            ccLastHungry = hungry;
            ccLastStarving = starving;
            ccLastThirsty = thirsty;
            ccLastWet = soaked;
            ccLastCamping = camping;
            ccLastCooking = cooking;
            ccLastTempBand = tempBand;
        }

        private static bool ReadStaticBool(FieldInfo field)
        {
            try { return field != null && Convert.ToBoolean(field.GetValue(null), CultureInfo.InvariantCulture); }
            catch { return false; }
        }
        private static int ReadStaticInt(FieldInfo field)
        {
            try { return field == null ? 0 : Convert.ToInt32(field.GetValue(null), CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        #endregion

        #region Generic quest-aware context / semantic choices

        private void PollQuestStructure()
        {
            if (!config.QuestAwareBarks) return;
            object questMachine = ReadMember(GameManager.Instance, "QuestMachine");
            object active = ReadMember(questMachine, "ActiveQuests");
            IEnumerable enumerable = active as IEnumerable;
            if (enumerable == null) return;

            HashSet<string> now = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object quest in enumerable)
            {
                if (quest == null) continue;
                string id = Convert.ToString(ReadMember(quest, "UID"), CultureInfo.InvariantCulture);
                if (string.IsNullOrEmpty(id)) id = Convert.ToString(ReadMember(quest, "QuestName"), CultureInfo.InvariantCulture);
                if (string.IsNullOrEmpty(id)) id = quest.GetHashCode().ToString(CultureInfo.InvariantCulture);
                now.Add(id);
                if (questSnapshotInitialized && !knownQuestIds.Contains(id))
                {
                    string questName = Convert.ToString(ReadMember(quest, "QuestName"), CultureInfo.InvariantCulture);
                    RecordContext("QuestAccepted");
                    string bespoke = ResolveBark("Quest." + questName + ".Accepted", false);
                    if (!string.IsNullOrEmpty(bespoke)) EnqueueSpeech(bespoke, SpeechPriority.Automatic, "quest", false);
                    else TryAutomaticBark("QuestAccepted", false);
                }
            }

            if (questSnapshotInitialized && knownQuestIds.Count > now.Count)
            {
                foreach (string old in knownQuestIds)
                {
                    if (!now.Contains(old))
                    {
                        RecordContext("QuestResolved");
                        TryAutomaticBark("QuestResolved", false);
                        break;
                    }
                }
            }
            knownQuestIds.Clear();
            foreach (string id in now) knownQuestIds.Add(id);
            questSnapshotInitialized = true;
        }

        private void PollChoiceWindow()
        {
            DaggerfallMessageBox box = DaggerfallUI.UIManager == null ? null : DaggerfallUI.UIManager.TopWindow as DaggerfallMessageBox;
            if (ReferenceEquals(box, observedChoiceBox)) return;
            UnhookChoiceBox();
            if (box == null || !ShouldWatchChoiceBox(box)) return;
            observedChoiceBox = box;
            observedChoiceBox.OnButtonClick += ChoiceBox_OnButtonClick;
        }

        private void UnhookChoiceBox()
        {
            if (observedChoiceBox != null)
            {
                try { observedChoiceBox.OnButtonClick -= ChoiceBox_OnButtonClick; } catch { }
                observedChoiceBox = null;
            }
        }

        private bool ShouldWatchChoiceBox(DaggerfallMessageBox box)
        {
            if (!config.VoiceDialogueChoices || box == null) return false;
            if (IsNpcvoEnhancedQuestActive()) return true;

            object owner = ReadMember(box, "PreviousWindow");
            for (int i = 0; i < 5 && owner != null; i++)
            {
                string name = owner.GetType().Name ?? string.Empty;
                if (name.IndexOf("Quest", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Court", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Talk", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
                owner = ReadMember(owner, "PreviousWindow");
            }
            return false;
        }

        private void ChoiceBox_OnButtonClick(DaggerfallMessageBox sender, DaggerfallMessageBox.MessageBoxButtons button)
        {
            string intent = ChoiceIntent(button);
            if (string.IsNullOrEmpty(intent)) return;
            string response = ResolveChoiceResponse(intent);
            if (string.IsNullOrWhiteSpace(response)) return;

            RecordContext("Choice." + intent);
            bool useNpcvo = config.IntegrateNpcvo && config.EnhancedNpcvoConversationUI;
            if (useNpcvo)
                BeginNpcvoPlayerTurn(response);
            EnqueueSpeech(response, SpeechPriority.Dialogue, "choice:" + intent, useNpcvo);
        }

        private static string ChoiceIntent(DaggerfallMessageBox.MessageBoxButtons button)
        {
            switch (button)
            {
                case DaggerfallMessageBox.MessageBoxButtons.Accept: return "Accept";
                case DaggerfallMessageBox.MessageBoxButtons.Reject: return "Reject";
                case DaggerfallMessageBox.MessageBoxButtons.Yes: return "Yes";
                case DaggerfallMessageBox.MessageBoxButtons.No: return "No";
                case DaggerfallMessageBox.MessageBoxButtons.Guilty: return "Guilty";
                case DaggerfallMessageBox.MessageBoxButtons.NotGuilty: return "NotGuilty";
                case DaggerfallMessageBox.MessageBoxButtons.Debate: return "Debate";
                case DaggerfallMessageBox.MessageBoxButtons.Lie: return "Lie";
                default: return string.Empty;
            }
        }

        private string ResolveChoiceResponse(string intent)
        {
            string race = GetRaceKey();
            string personality = GetPersonalityKey();
            List<string> keys = new List<string>();
            if (config.RaceFlavor) keys.Add("Personality." + personality + ".Race." + race + ".Choice." + intent);
            keys.Add("Personality." + personality + ".Choice." + intent);
            if (config.RaceFlavor) keys.Add("Race." + race + ".Choice." + intent);
            keys.Add("Choice." + intent);
            return PickFromKeys(keys.ToArray());
        }

        #endregion

        #region NPCVO reflection bridge

        private void EnsureNpcvoReflection()
        {
            // If an early probe happened before NPCVO finished loading, keep retrying until its type
            // becomes available. Once resolved, the reflection handles are stable for the session.
            if (npcvoReflectionChecked && npcvoType != null) return;
            npcvoReflectionChecked = true;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = a.GetType("NPCVO.NPCVOMod", false); } catch { }
                if (t == null) continue;
                npcvoType = t;
                BindingFlags sf = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                npcvoRegisterPlayerIdentity = t.GetMethod("RegisterExternalPlayerIdentity", sf);
                npcvoRegisterPlayerIdentityDetailed = t.GetMethod("RegisterExternalPlayerIdentityDetailed", sf);
                npcvoBeginPlayerTurn = t.GetMethod("BeginExternalPlayerTurn", sf);
                npcvoUpdatePlayerText = t.GetMethod("UpdateExternalPlayerTurnText", sf);
                npcvoEndPlayerTurn = t.GetMethod("EndExternalPlayerTurn", sf);
                npcvoEnhancedQuestActive = t.GetProperty("EnhancedQuestConversationActive", sf);
                Debug.Log("[PlayerVO] NPCVO detected; persistent two-sided quest presentation available.");
                break;
            }
        }

        private void SyncNpcvoPlayerIdentity(bool force)
        {
            if (!config.IntegrateNpcvo || !config.EnhancedNpcvoConversationUI)
                return;
            EnsureNpcvoReflection();
            if (npcvoRegisterPlayerIdentity == null)
                return;

            string identityKey = GetPlayerPortraitIdentityKey() + "|" + GetRaceDisplayName() + "|" +
                GetPersonalityKey() + "|" + GetLocationName();
            if (!force && string.Equals(identityKey, lastNpcvoIdentityKey, StringComparison.Ordinal))
                return;

            try
            {
                Texture2D portrait = GetPlayerConversationPortraitTexture();
                if (npcvoRegisterPlayerIdentityDetailed != null)
                {
                    npcvoRegisterPlayerIdentityDetailed.Invoke(null, new object[] { portrait, GetPlayerName(),
                        GetRaceDisplayName(), GetPersonalityKey(), GetLocationName() });
                }
                else
                {
                    npcvoRegisterPlayerIdentity.Invoke(null, new object[] { portrait, GetPlayerName() });
                }
                lastNpcvoIdentityKey = identityKey;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PlayerVO] NPCVO identity bridge failed: " + ex.Message);
            }
        }

        private bool IsNpcvoEnhancedQuestActive()
        {
            if (!config.IntegrateNpcvo) return false;
            EnsureNpcvoReflection();
            try { return npcvoEnhancedQuestActive != null && Convert.ToBoolean(npcvoEnhancedQuestActive.GetValue(null, null), CultureInfo.InvariantCulture); }
            catch { return false; }
        }

        private void BeginNpcvoPlayerTurn(string fullText)
        {
            SyncNpcvoPlayerIdentity(true);
            EnsureNpcvoReflection();
            if (npcvoBeginPlayerTurn == null) return;
            try { npcvoBeginPlayerTurn.Invoke(null, new object[] { GetPlayerConversationPortraitTexture(), GetPlayerName(), fullText ?? string.Empty }); }
            catch (Exception ex) { Debug.LogWarning("[PlayerVO] NPCVO player-turn bridge failed: " + ex.Message); }
        }

        private void UpdateNpcvoPlayerText(string partial)
        {
            if (npcvoUpdatePlayerText == null) return;
            try { npcvoUpdatePlayerText.Invoke(null, new object[] { partial ?? string.Empty }); } catch { }
        }

        private void EndNpcvoPlayerTurn()
        {
            if (npcvoEndPlayerTurn == null) return;
            try { npcvoEndPlayerTurn.Invoke(null, null); } catch { }
        }

        private Texture2D GetPlayerConversationPortraitTexture()
        {
            string key = GetPlayerPortraitIdentityKey();
            if (cachedConversationPortrait != null && string.Equals(key, cachedConversationPortraitKey, StringComparison.Ordinal))
                return cachedConversationPortrait;

            InvalidateConversationPortrait();
            try
            {
                Texture2D face = GetPlayerFaceTexture();
                if (face == null)
                    return null;

                // v0.1.2 deliberately uses the dedicated face/head art for conversation portraits.
                // The full paperdoll looked like a shrunken inventory sprite at 64x64 and became very
                // pixelated. The player still gets the paperdoll's race-appropriate scene as a backdrop
                // in the default mode, but the foreground is always the cleaner head portrait.
                Texture2D background = config.PlayerPortraitStyle == 0 ? GetPlayerPaperDollBackgroundTexture() : null;
                bool transparentBackdrop = config.PlayerPortraitStyle == 2;
                cachedConversationPortrait = ComposePlayerHeadPortrait(background, face, transparentBackdrop);
                cachedConversationPortraitKey = key;
                return cachedConversationPortrait ?? face;
            }
            catch
            {
                return GetPlayerFaceTexture();
            }
        }

        private string GetPlayerPortraitIdentityKey()
        {
            try
            {
                PlayerEntity p = GameManager.Instance == null ? null : GameManager.Instance.PlayerEntity;
                if (p == null) return "none|" + config.PlayerPortraitStyle;
                return config.PlayerPortraitStyle + "|" + p.Race + "|" + p.Gender + "|" + p.FaceIndex + "|" + GetPlayerName();
            }
            catch { return "fallback|" + config.PlayerPortraitStyle + "|" + GetPlayerName(); }
        }

        private void InvalidateConversationPortrait()
        {
            lastNpcvoIdentityKey = string.Empty;
            cachedConversationPortraitKey = string.Empty;
            if (cachedConversationPortrait != null)
            {
                try { Destroy(cachedConversationPortrait); } catch { }
                cachedConversationPortrait = null;
            }
        }

        private Texture2D GetPlayerFaceTexture()
        {
            try
            {
                PlayerEntity p = GameManager.Instance.PlayerEntity;
                if (p == null || p.RaceTemplate == null) return null;
                string filename = p.Gender == Genders.Male ? p.RaceTemplate.PaperDollHeadsMale : p.RaceTemplate.PaperDollHeadsFemale;
                var data = ImageReader.GetImageData(filename, p.FaceIndex, 0, true, true);
                return data.texture;
            }
            catch { return null; }
        }

        private Texture2D GetPlayerPaperDollBackgroundTexture()
        {
            try
            {
                PlayerEntity p = GameManager.Instance.PlayerEntity;
                if (p == null || p.RaceTemplate == null || string.IsNullOrWhiteSpace(p.RaceTemplate.PaperDollBackground))
                    return null;
                Texture2D full = ImageReader.GetTexture(p.RaceTemplate.PaperDollBackground, 0, 0, false);
                if (full == null) return null;
                // Mirrors DFU PaperDoll's own 110x184 crop inside the classic 125x198 background.
                return ImageReader.GetSubTexture(full, new Rect(8f, 7f, 110f, 184f), new DFSize(125, 198));
            }
            catch { return null; }
        }

        private Texture2D ComposePlayerHeadPortrait(Texture2D background, Texture2D face, bool transparentBackdrop)
        {
            if (face == null) return null;
            const int size = 64;
            RenderTexture rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32);
            RenderTexture previous = RenderTexture.active;
            bool matrixPushed = false;
            try
            {
                RenderTexture.active = rt;
                GL.Clear(true, true, transparentBackdrop ? Color.clear : new Color(0.10f, 0.075f, 0.045f, 1f));
                GL.PushMatrix();
                matrixPushed = true;
                GL.LoadPixelMatrix(0f, size, size, 0f);

                if (background != null)
                {
                    // Use the upper square of DFU's paperdoll scene as a portrait backdrop only.
                    Rect upperCrop = new Rect(0f, 74f / 184f, 1f, 110f / 184f);
                    Graphics.DrawTexture(new Rect(0f, 0f, size, size), background, upperCrop, 0, 0, 0, 0);
                }

                // Enlarge the head inside the fixed 64x64 texture. The UI frame stays the same size.
                // Bias left/up slightly to center DFU face art in conversation frames.
                Graphics.DrawTexture(new Rect(-1f, 2f, 64f, 64f), face);

                GL.PopMatrix();
                matrixPushed = false;

                Texture2D result = new Texture2D(size, size, TextureFormat.ARGB32, false);
                result.filterMode = FilterMode.Point;
                result.wrapMode = TextureWrapMode.Clamp;
                result.ReadPixels(new Rect(0f, 0f, size, size), 0, 0, false);
                result.Apply(false, false);
                return result;
            }
            catch
            {
                if (matrixPushed)
                {
                    try { GL.PopMatrix(); } catch { }
                }
                return null;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        #endregion

        #region Bark resolver

        private void LoadOrCreateBarks()
        {
            BarkLibrary defaults = BuildDefaultBarkLibrary();
            bool changed = false;
            if (!File.Exists(barksPath))
            {
                library = defaults;
                changed = true;
            }
            else
            {
                try { library = JsonUtility.FromJson<BarkLibrary>(File.ReadAllText(barksPath)); }
                catch { library = null; }
                if (library == null || library.sets == null || library.sets.Length == 0)
                {
                    library = defaults;
                    changed = true;
                }
                else
                {
                    // Preserve every user-authored pool verbatim, then append only newly shipped keys.
                    // This lets older v1 Bark files gain the expanded v2 writing pass without erasing edits.
                    bool upgrading = library.schemaVersion < defaults.schemaVersion;
                    Dictionary<string, BarkSet> defaultByKey = new Dictionary<string, BarkSet>(StringComparer.OrdinalIgnoreCase);
                    foreach (BarkSet set in defaults.sets)
                        if (set != null && !string.IsNullOrWhiteSpace(set.key)) defaultByKey[set.key] = set;

                    Dictionary<string, BarkSet> existing = new Dictionary<string, BarkSet>(StringComparer.OrdinalIgnoreCase);
                    List<BarkSet> merged = new List<BarkSet>();
                    foreach (BarkSet set in library.sets)
                    {
                        if (set == null || string.IsNullOrWhiteSpace(set.key)) continue;
                        BarkSet shipped;
                        if (upgrading && defaultByKey.TryGetValue(set.key, out shipped))
                        {
                            // Keep user additions, remove only superseded shipped phrasing, and fold in
                            // the richer v2 pool. This upgrades an already-generated Barks.json without
                            // forcing the user to delete it or sacrificing custom lines.
                            set.lines = MergeBarkLinesForUpgrade(set.key, set.lines, shipped.lines);
                            changed = true;
                        }
                        existing[set.key] = set;
                        merged.Add(set);
                    }
                    foreach (BarkSet set in defaults.sets)
                    {
                        if (set == null || existing.ContainsKey(set.key)) continue;
                        merged.Add(set);
                        changed = true;
                    }
                    if (upgrading) changed = true;
                    library.schemaVersion = defaults.schemaVersion;
                    library.sets = merged.ToArray();
                }
            }
            if (changed)
            {
                try { File.WriteAllText(barksPath, JsonUtility.ToJson(library, true)); } catch { }
            }
            barkSets.Clear();
            foreach (BarkSet set in library.sets)
                if (set != null && !string.IsNullOrWhiteSpace(set.key) && set.lines != null)
                    barkSets[set.key] = set.lines;
        }

        private static string[] MergeBarkLinesForUpgrade(string key, string[] existing, string[] shipped)
        {
            List<string> result = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<string> add = delegate(string line)
            {
                if (string.IsNullOrWhiteSpace(line)) return;
                string trimmed = line.Trim();
                // One normal-mode v1 phrase was intentionally retired as too modern. Movie-style
                // language remains valid inside Action.* pools, where anachronism is the feature.
                if (string.Equals(key, "LowFatigue", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(trimmed, "I'm running on fumes.", StringComparison.OrdinalIgnoreCase)) return;
                if (seen.Add(trimmed)) result.Add(trimmed);
            };
            if (existing != null) foreach (string line in existing) add(line);
            if (shipped != null) foreach (string line in shipped) add(line);
            return result.ToArray();
        }

        private void TryAutomaticBark(string eventKey, bool actionEligible)
        {
            // The Apprentice's Agent is new to the land and economical with words. Ambient chatter
            // stays quiet, but mission-critical/quest and immediate danger events can still draw a
            // concise reaction so the archetype is reserved rather than literally mute.
            if (!config.AutomaticBarks || playerDead ||
                Time.realtimeSinceStartup < automaticCooldownUntil || Time.realtimeSinceStartup < vanillaVocalBlockUntil)
                return;
            if (IsAgentPersonality() && !AgentAllowsAutomaticEvent(eventKey))
                return;

            float chance = config.BarkFrequency == 0 ? 0.25f : config.BarkFrequency == 2 ? 0.85f : 0.55f;
            chance = Mathf.Clamp01(chance * GetPersonalityAffinityMultiplier(eventKey));
            if (UnityEngine.Random.value > chance) return;

            string line = ResolveBark(eventKey, false, actionEligible);
            if (string.IsNullOrWhiteSpace(line)) return;
            automaticCooldownUntil = Time.realtimeSinceStartup + config.AutomaticCooldownSeconds;
            EnqueueSpeech(line, SpeechPriority.Automatic, "auto:" + eventKey, false);
        }

        private string ResolveBark(string eventKey, bool manual, bool actionEligible = true)
        {
            string race = GetRaceKey();
            string personality = GetPersonalityKey();
            string locationKey = GetLocationContextKey();
            string regionKey = GetRegionContextKey();
            string guildKey = GetPrimaryGuildKey();
            string reputationBand = GetLocalReputationBand();
            List<string> keys = new List<string>();

            // Action Movie is intentionally outside the personality tree: these are one-liners first.
            bool action = actionEligible && config.ActionMovieMode > 0;
            float actionChance = config.ActionMovieMode == 2 ? 0.85f : 0.35f;
            if (manual && Time.realtimeSinceStartup - recentContextEventTime <= 8f) actionChance = Mathf.Max(actionChance, 0.75f);
            if (action && UnityEngine.Random.value <= actionChance)
            {
                if (config.RaceFlavor) keys.Add("Race." + race + ".Action." + eventKey);
                keys.Add("Action." + eventKey);
            }

            {
                // Personality is the root of the authored tree. Context determines the subject; the
                // birthsign determines why the character cares and what lore they plausibly know.
                bool allowGeneralFlavor = ContextAllowsGeneralFlavor(eventKey, manual);
                if (!string.IsNullOrEmpty(locationKey)) keys.Add("Personality." + personality + ".Location." + locationKey + "." + eventKey);
                if (!string.IsNullOrEmpty(regionKey)) keys.Add("Personality." + personality + ".Region." + regionKey + "." + eventKey);
                if (!string.IsNullOrEmpty(guildKey)) keys.Add("Personality." + personality + ".Guild." + guildKey + "." + eventKey);
                if (!string.IsNullOrEmpty(reputationBand) && !string.Equals(reputationBand, "Neutral", StringComparison.OrdinalIgnoreCase))
                    keys.Add("Personality." + personality + ".Reputation." + reputationBand + "." + eventKey);
                if (config.RaceFlavor) keys.Add("Personality." + personality + ".Race." + race + "." + eventKey);
                keys.Add("Personality." + personality + "." + eventKey);

                // General lore/worldview is deliberately reserved for place-oriented/manual contexts,
                // so a Daggerfall lore bark cannot replace an urgent CombatStart or LowHealth reaction.
                if (allowGeneralFlavor)
                {
                    if (!string.IsNullOrEmpty(locationKey)) keys.Add("Personality." + personality + ".Location." + locationKey + ".General");
                    if (!string.IsNullOrEmpty(regionKey)) keys.Add("Personality." + personality + ".Region." + regionKey + ".General");
                    if (!string.IsNullOrEmpty(guildKey)) keys.Add("Personality." + personality + ".Guild." + guildKey + ".General");
                    if (!string.IsNullOrEmpty(reputationBand) && !string.Equals(reputationBand, "Neutral", StringComparison.OrdinalIgnoreCase))
                        keys.Add("Personality." + personality + ".Reputation." + reputationBand + ".General");
                    if (config.RaceFlavor) keys.Add("Personality." + personality + ".Race." + race + ".General");
                    keys.Add("Personality." + personality + ".General");
                }
            }

            // Structural fallbacks retain all dynamic reactivity even when a personality-specific
            // combination has not been hand-authored yet.
            if (!string.IsNullOrEmpty(locationKey)) keys.Add("Location." + locationKey + "." + eventKey);
            if (!string.IsNullOrEmpty(regionKey)) keys.Add("Region." + regionKey + "." + eventKey);
            if (!string.IsNullOrEmpty(guildKey)) keys.Add("Guild." + guildKey + "." + eventKey);
            if (!string.IsNullOrEmpty(guildKey)) keys.Add("GuildMember." + eventKey);
            if (!string.IsNullOrEmpty(reputationBand) && !string.Equals(reputationBand, "Neutral", StringComparison.OrdinalIgnoreCase))
                keys.Add("Reputation." + reputationBand + "." + eventKey);
            if (config.RaceFlavor) keys.Add("Race." + race + "." + eventKey);
            keys.Add(eventKey);

            if (manual)
            {
                if (!string.IsNullOrEmpty(locationKey)) keys.Add("Location." + locationKey + ".General");
                if (!string.IsNullOrEmpty(regionKey)) keys.Add("Region." + regionKey + ".General");
                if (!string.IsNullOrEmpty(guildKey)) keys.Add("Guild." + guildKey + ".General");
                if (!string.IsNullOrEmpty(guildKey)) keys.Add("GuildMember.General");
                if (!string.IsNullOrEmpty(reputationBand) && !string.Equals(reputationBand, "Neutral", StringComparison.OrdinalIgnoreCase))
                    keys.Add("Reputation." + reputationBand + ".General");
                if (config.RaceFlavor) keys.Add("Race." + race + ".General");
                keys.Add("General");
            }
            return PickFromKeys(keys.ToArray());
        }

        private bool ContextAllowsGeneralFlavor(string eventKey, bool manual)
        {
            if (manual) return true;
            switch (eventKey ?? string.Empty)
            {
                case "General":
                case "TownEnter":
                case "DungeonEnter":
                case "TavernEnter":
                case "TempleEnter":
                case "GuildHallEnter":
                case "LibraryEnter":
                case "PalaceEnter":
                case "BankEnter":
                case "ShopEnter":
                case "BuildingEnter":
                case "TravelEnd":
                case "Wilderness":
                    return true;
                default:
                    return false;
            }
        }

        private float GetPersonalityAffinityMultiplier(string eventKey)
        {
            string p = GetPersonalityKey();
            string e = eventKey ?? string.Empty;
            if (p == "Thief" && e == "TavernEnter") return 1.55f;
            if (p == "Warrior" && (e == "DungeonEnter" || e == "CombatStart")) return 1.30f;
            if (p == "Mage" && (e == "MagicCast" || e == "LibraryEnter" || e == "GuildHallEnter")) return 1.35f;
            if (p == "Ritual" && (e == "DungeonEnter" || e == "TempleEnter" || e == "MagicCast")) return 1.35f;
            if (p == "Lady" && (e == "TownEnter" || e == "PalaceEnter" || e == "GuildHallEnter")) return 1.30f;
            if (p == "Lord" && (e == "PalaceEnter" || e == "TownEnter")) return 1.30f;
            if (p == "Lover" && (e == "TownEnter" || e == "TempleEnter" || e == "QuestAccepted")) return 1.25f;
            if (p == "Serpent" && (e == "CombatStart" || e == "Nightfall")) return 1.35f;
            if (p == "Steed" && (e == "TravelStart" || e == "TravelEnd" || e == "Wilderness")) return 1.45f;
            if (p == "Tower" && (e == "DungeonEnter" || e == "BuildingEnter")) return 1.40f;
            if (p == "Atronach" && (e == "QuestAccepted" || e == "CombatStart" || e == "DungeonEnter")) return 1.25f;
            if (p == "Shadow" && (e == "Nightfall" || e == "DungeonEnter" || e == "TownEnter")) return 1.30f;
            return 1f;
        }

        private string PickFromKeys(string[] keys)
        {
            for (int k = 0; keys != null && k < keys.Length; k++)
            {
                string[] lines;
                if (!barkSets.TryGetValue(keys[k], out lines) || lines == null || lines.Length == 0) continue;
                List<string> candidates = new List<string>();
                foreach (string raw in lines)
                {
                    string line = ExpandLine(raw);
                    if (!string.IsNullOrWhiteSpace(line) && !RecentContains(line)) candidates.Add(line);
                }
                if (candidates.Count == 0)
                    foreach (string raw in lines) if (!string.IsNullOrWhiteSpace(raw)) candidates.Add(ExpandLine(raw));
                if (candidates.Count > 0)
                    return candidates[UnityEngine.Random.Range(0, candidates.Count)];
            }
            return string.Empty;
        }

        private string ExpandLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return string.Empty;
            RefreshAffiliationContext();
            return line.Replace("{name}", GetPlayerName())
                .Replace("{race}", GetRaceDisplayName())
                .Replace("{location}", GetLocationName())
                .Replace("{region}", GetRegionName())
                .Replace("{guild}", string.IsNullOrWhiteSpace(cachedPrimaryGuildName) ? "my guild" : cachedPrimaryGuildName)
                .Replace("{guildtitle}", string.IsNullOrWhiteSpace(cachedPrimaryGuildTitle) ? "member" : cachedPrimaryGuildTitle);
        }

        private bool RecentContains(string line)
        {
            foreach (string recent in recentLines)
                if (string.Equals(recent, line, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private bool CategoryReady(string category, float seconds)
        {
            float last;
            if (categoryLastSpoken.TryGetValue(category, out last) && Time.realtimeSinceStartup - last < seconds) return false;
            categoryLastSpoken[category] = Time.realtimeSinceStartup;
            return true;
        }

        private string GetRaceKey()
        {
            try { return GameManager.Instance.PlayerEntity.Race.ToString().Replace(" ", string.Empty); }
            catch { return "Generic"; }
        }
        private string GetRaceDisplayName()
        {
            try { return GameManager.Instance.PlayerEntity.RaceTemplate.Name; }
            catch { return GetRaceKey(); }
        }
        private string GetPlayerName()
        {
            try { return string.IsNullOrWhiteSpace(GameManager.Instance.PlayerEntity.Name) ? "You" : GameManager.Instance.PlayerEntity.Name; }
            catch { return "You"; }
        }
        private string GetLocationName()
        {
            try
            {
                string name = GameManager.Instance.PlayerGPS.CurrentLocalizedLocationName;
                return string.IsNullOrWhiteSpace(name) ? "this place" : name;
            }
            catch { return "this place"; }
        }
        private string GetRegionName()
        {
            try
            {
                string name = GameManager.Instance.PlayerGPS.CurrentLocalizedRegionName;
                return string.IsNullOrWhiteSpace(name) ? "the Iliac Bay" : name;
            }
            catch { return "the Iliac Bay"; }
        }

        private string GetLocationContextKey()
        {
            string location = GetLocationName();
            if (string.Equals(location, "this place", StringComparison.OrdinalIgnoreCase)) return string.Empty;
            string key = MakeContextKey(location);
            // Earlier shipped data accidentally used DirennisTower. Normalize to the actual name.
            if (string.Equals(key, "DirennisTower", StringComparison.OrdinalIgnoreCase)) key = "DirenniTower";
            return key;
        }

        private string GetRegionContextKey()
        {
            return MakeContextKey(GetRegionName());
        }

        private string GetPrimaryGuildKey()
        {
            RefreshAffiliationContext();
            return cachedPrimaryGuildKey;
        }

        private string GetLocalReputationBand()
        {
            RefreshAffiliationContext();
            return cachedLocalReputationBand;
        }

        private void RefreshAffiliationContext()
        {
            if (Time.realtimeSinceStartup < affiliationCacheUntil)
                return;
            affiliationCacheUntil = Time.realtimeSinceStartup + 5f;
            cachedPrimaryGuildKey = string.Empty;
            cachedPrimaryGuildName = string.Empty;
            cachedPrimaryGuildTitle = string.Empty;
            cachedLocalReputationBand = "Neutral";

            try
            {
                PlayerEntity player = GameManager.Instance == null ? null : GameManager.Instance.PlayerEntity;
                if (player != null && GameManager.Instance.PlayerGPS != null && player.RegionData != null)
                {
                    int region = GameManager.Instance.PlayerGPS.CurrentRegionIndex;
                    if (region >= 0 && region < player.RegionData.Length)
                    {
                        int legal = player.RegionData[region].LegalRep;
                        if (legal <= -20) cachedLocalReputationBand = "Notorious";
                        else if (legal <= -5) cachedLocalReputationBand = "Disreputable";
                        else if (legal >= 20) cachedLocalReputationBand = "Honored";
                        else if (legal >= 8) cachedLocalReputationBand = "Respected";
                    }
                }

                object guildManager = GameManager.Instance == null ? null : GameManager.Instance.GuildManager;
                if (guildManager == null) return;
                MethodInfo membershipsMethod = guildManager.GetType().GetMethod("GetMemberships", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                IEnumerable memberships = membershipsMethod == null ? null : membershipsMethod.Invoke(guildManager, null) as IEnumerable;
                if (memberships == null) return;

                object best = null;
                int bestRank = int.MinValue;
                int bestRep = int.MinValue;
                foreach (object guild in memberships)
                {
                    if (guild == null) continue;
                    int rank = ToIntSafe(ReadMember(guild, "Rank"), -1);
                    int rep = InvokeGuildReputation(guild);
                    if (best == null || rank > bestRank || (rank == bestRank && rep > bestRep))
                    {
                        best = guild;
                        bestRank = rank;
                        bestRep = rep;
                    }
                }
                if (best == null) return;

                cachedPrimaryGuildName = InvokeStringMethod(best, "GetAffiliation");
                cachedPrimaryGuildTitle = InvokeStringMethod(best, "GetTitle");
                cachedPrimaryGuildKey = MakeContextKey(cachedPrimaryGuildName);
            }
            catch { }
        }

        private int InvokeGuildReputation(object guild)
        {
            try
            {
                MethodInfo[] methods = guild.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < methods.Length; i++)
                {
                    if (!string.Equals(methods[i].Name, "GetReputation", StringComparison.Ordinal) || methods[i].GetParameters().Length != 1)
                        continue;
                    object value = methods[i].Invoke(guild, new object[] { GameManager.Instance.PlayerEntity });
                    return ToIntSafe(value, 0);
                }
            }
            catch { }
            return 0;
        }

        private static string InvokeStringMethod(object target, string methodName)
        {
            if (target == null) return string.Empty;
            try
            {
                MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                object value = method == null ? null : method.Invoke(target, null);
                return value == null ? string.Empty : value.ToString().Trim();
            }
            catch { return string.Empty; }
        }

        private static int ToIntSafe(object value, int fallback)
        {
            try { return value == null ? fallback : Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static string MakeContextKey(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            }
            return sb.ToString();
        }

        private string GetPersonalityKey()
        {
            string[] p = { "Mage", "Ritual", "Lady", "Lord", "Warrior", "Thief", "Lover", "Serpent", "Steed", "Tower", "Atronach", "Shadow", "Apprentice" };
            return p[Mathf.Clamp(config.Personality, 0, p.Length - 1)];
        }

        private bool IsAgentPersonality()
        {
            return string.Equals(GetPersonalityKey(), "Apprentice", StringComparison.OrdinalIgnoreCase);
        }

        private static bool AgentAllowsAutomaticEvent(string eventKey)
        {
            string e = eventKey ?? string.Empty;
            return e.Equals("QuestAccepted", StringComparison.OrdinalIgnoreCase) ||
                e.Equals("QuestResolved", StringComparison.OrdinalIgnoreCase) ||
                e.Equals("CombatStart", StringComparison.OrdinalIgnoreCase) ||
                e.Equals("CombatEnd", StringComparison.OrdinalIgnoreCase) ||
                e.Equals("LowHealth", StringComparison.OrdinalIgnoreCase) ||
                e.Equals("CriticalHealth", StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region Speech / vanilla audio arbitration

        private void EnqueueSpeech(string text, SpeechPriority priority, string category, bool useNpcvoUi)
        {
            if (string.IsNullOrWhiteSpace(text) || playerDead) return;
            if (priority == SpeechPriority.Automatic && Time.realtimeSinceStartup < vanillaVocalBlockUntil) return;

            PlayerSpeechRequest req = new PlayerSpeechRequest();
            req.Text = text.Trim();
            req.Priority = priority;
            req.Category = category ?? string.Empty;
            req.UseNpcvoPresentation = useNpcvoUi;
            req.EarliestTime = (priority == SpeechPriority.Automatic) ? Time.realtimeSinceStartup : Mathf.Max(Time.realtimeSinceStartup, vanillaVocalBlockUntil);

            if (activeSpeech != null && priority > activeSpeech.Priority)
                StopCurrentSpeech(false);
            if (speechQueue.Count >= 8 && priority <= SpeechPriority.Automatic) return;
            speechQueue.Enqueue(req);
        }

        private IEnumerator ProcessSpeechQueue()
        {
            while (speechQueue.Count > 0)
            {
                PlayerSpeechRequest req = speechQueue.Dequeue();
                activeSpeech = req;
                int generation = ++speechGeneration;

                // A vanilla grunt/gasp can begin after a manual request was queued. Re-evaluate the
                // lockout at playback time so queued player speech never barges into base-game vocals.
                if (req.Priority != SpeechPriority.Automatic)
                    req.EarliestTime = Mathf.Max(req.EarliestTime, vanillaVocalBlockUntil);

                while (Time.realtimeSinceStartup < req.EarliestTime && generation == speechGeneration && !playerDead)
                    yield return null;
                if (generation != speechGeneration || playerDead) { activeSpeech = null; continue; }

                DaggerfallVoiceEngineTurn engineTurn = new DaggerfallVoiceEngineTurn();
                int dvePriority = GetVoiceEnginePriority(req);
                int dveExpiry = GetVoiceEngineExpiryMs(req);
                if (voiceEngine != null)
                    yield return StartCoroutine(voiceEngine.AcquireTurn(dvePriority, req.Category, Sha1(req.Text + "|" + req.Category), dveExpiry,
                        delegate { return generation == speechGeneration && !playerDead; }, engineTurn));
                activeVoiceEngineTurnId = engineTurn.Ready ? engineTurn.Id : string.Empty;
                if (engineTurn.EngineOnline && !engineTurn.Ready)
                {
                    if (req.UseNpcvoPresentation) { BeginNpcvoPlayerTurn(req.Text); UpdateNpcvoPlayerText(req.Text); EndNpcvoPlayerTurn(); }
                    activeSpeech = null;
                    continue;
                }

                if (req.UseNpcvoPresentation)
                    BeginNpcvoPlayerTurn(req.Text);

                string wav = GetCachePath(req.Text);
                if (!File.Exists(wav))
                    yield return StartCoroutine(GenerateKokoro(req, wav, generation));
                if (generation != speechGeneration || playerDead)
                {
                    if (req.UseNpcvoPresentation) EndNpcvoPlayerTurn();
                    activeSpeech = null;
                    continue;
                }

                AudioClip clip = null;
                if (File.Exists(wav))
                    yield return StartCoroutine(LoadWav(wav, delegate(AudioClip c) { clip = c; }));
                if (clip != null && generation == speechGeneration)
                {
                    audioSource.Stop();
                    audioSource.clip = clip;
                    ApplyVolume();
                    audioSource.Play();
                    if (ShouldShowPlayerSubtitle(req))
                        SetPlayerSubtitle(req.Text, Mathf.Max(clip.length, 0.75f));
                    float start = Time.realtimeSinceStartup;
                    while (audioSource.isPlaying && generation == speechGeneration && !playerDead)
                    {
                        if (req.UseNpcvoPresentation)
                        {
                            float progress = clip.length <= 0f ? 1f : Mathf.Clamp01((Time.realtimeSinceStartup - start) / clip.length);
                            int chars = Mathf.Clamp(Mathf.RoundToInt(req.Text.Length * progress), 0, req.Text.Length);
                            UpdateNpcvoPlayerText(req.Text.Substring(0, chars));
                        }
                        yield return null;
                    }
                    if (req.UseNpcvoPresentation && generation == speechGeneration)
                        UpdateNpcvoPlayerText(req.Text);
                    if (audioSource.clip == clip) audioSource.clip = null;
                    Destroy(clip);
                }
                else if (req.UseNpcvoPresentation && generation == speechGeneration)
                {
                    // TTS failure must never block the quest. Reveal quickly, then release NPCVO.
                    UpdateNpcvoPlayerText(req.Text);
                    if (ShouldShowPlayerSubtitle(req)) SetPlayerSubtitle(req.Text, 2.5f);
                    yield return new WaitForSecondsRealtime(0.35f);
                }

                if (req.UseNpcvoPresentation) EndNpcvoPlayerTurn();
                if (generation == speechGeneration)
                {
                    recentLines.Enqueue(req.Text);
                    while (recentLines.Count > 8) recentLines.Dequeue();
                }
                if (playerSubtitleUntil <= Time.realtimeSinceStartup) activePlayerSubtitle = string.Empty;
                if (voiceEngine != null && !string.IsNullOrEmpty(activeVoiceEngineTurnId))
                    yield return StartCoroutine(voiceEngine.CompleteTurn(activeVoiceEngineTurnId));
                activeVoiceEngineTurnId = string.Empty;
                activeSpeech = null;
            }
            speechRoutine = null;
        }

        private int GetVoiceEnginePriority(PlayerSpeechRequest req)
        {
            if (req == null) return 40;
            if (req.Priority == SpeechPriority.Dialogue) return 120;
            if (req.Priority == SpeechPriority.Typed) return 115;
            if (req.Priority == SpeechPriority.Manual) return 70;
            return 45;
        }

        private int GetVoiceEngineExpiryMs(PlayerSpeechRequest req)
        {
            if (req == null) return 4000;
            if (req.Priority == SpeechPriority.Dialogue) return 30000;
            if (req.Priority == SpeechPriority.Typed) return 15000;
            if (req.Priority == SpeechPriority.Manual) return 6500;
            return 4000;
        }

        private string ResolveSpeechEmotion(PlayerSpeechRequest req)
        {
            string c = req == null ? string.Empty : (req.Category ?? string.Empty).ToLowerInvariant();
            string p = GetPersonalityKey();
            if (c.Contains("critical") || c.Contains("lowhealth") || c.Contains("heavy") || c.Contains("combat")) return p == "Serpent" ? "amused" : "tense";
            if (c.Contains("survival") || c.Contains("hungry") || c.Contains("thirst") || c.Contains("fatigue")) return "weary";
            if (c.Contains("action")) return "amused";
            if (c.Contains("questresolved") || c.Contains("victory")) return "triumphant";
            if (p == "Shadow") return "suspicious";
            if (p == "Lord" || p == "Ritual") return "grim";
            if (p == "Lover" || p == "Lady") return "warm";
            return "neutral";
        }

        private float ResolveSpeechEmotionIntensity(PlayerSpeechRequest req)
        {
            if (req == null) return 0.25f;
            if (req.Priority == SpeechPriority.Dialogue || req.Priority == SpeechPriority.Typed) return 0.20f;
            return req.Priority == SpeechPriority.Automatic ? 0.45f : 0.35f;
        }

        private bool ShouldShowPlayerSubtitle(PlayerSpeechRequest req)
        {
            if (req == null || config == null || config.SubtitleMode <= 0) return false;
            // Conversation questions/quest presentation already have visible host text; do not duplicate it.
            return req.Priority == SpeechPriority.Automatic || req.Priority == SpeechPriority.Manual || req.Priority == SpeechPriority.Typed;
        }

        private void SetPlayerSubtitle(string text, float seconds)
        {
            if (string.IsNullOrWhiteSpace(text) || config == null || config.SubtitleMode <= 0)
                return;

            string subtitle = config.SubtitleMode == 2 ? GetPlayerName() + ": " + text.Trim() : text.Trim();

            // Use DFU's own HUD text renderer so subtitles use the authentic Daggerfall font.
            try
            {
                DaggerfallUI.AddHUDText(subtitle);
                activePlayerSubtitle = string.Empty;
                playerSubtitleUntil = 0f;
            }
            catch
            {
                // Do not fall back to Unity IMGUI's generic font.
                activePlayerSubtitle = string.Empty;
                playerSubtitleUntil = 0f;
            }
        }

        private void OnGUI()
        {
            // Player subtitles are rendered by DaggerfallUI.AddHUDText().
        }

        private void InterruptForVanillaPlayerVocal(float seconds)
        {
            vanillaVocalBlockUntil = seconds > 100f ? float.MaxValue : Mathf.Max(vanillaVocalBlockUntil, Time.realtimeSinceStartup + seconds);
            StopCurrentSpeech(true);
        }

        private void StopCurrentSpeech(bool clearAutomaticQueue)
        {
            speechGeneration++;
            if (audioSource != null) audioSource.Stop();
            activePlayerSubtitle = string.Empty;
            playerSubtitleUntil = 0f;
            EndNpcvoPlayerTurn();
            if (voiceEngine != null && !string.IsNullOrEmpty(activeVoiceEngineTurnId))
                StartCoroutine(voiceEngine.CancelTurn(activeVoiceEngineTurnId));
            activeVoiceEngineTurnId = string.Empty;
            activeSpeech = null;
            if (clearAutomaticQueue && speechQueue.Count > 0)
            {
                Queue<PlayerSpeechRequest> keep = new Queue<PlayerSpeechRequest>();
                while (speechQueue.Count > 0)
                {
                    PlayerSpeechRequest q = speechQueue.Dequeue();
                    if (q.Priority >= SpeechPriority.Manual) keep.Enqueue(q);
                }
                while (keep.Count > 0) speechQueue.Enqueue(keep.Dequeue());
            }
        }

        private IEnumerator GenerateKokoro(PlayerSpeechRequest req, string wavPath, int generation)
        {
            string text = req == null ? string.Empty : req.Text;
            string voice = GetEffectivePlayerVoice();
            string emotion = ResolveSpeechEmotion(req);
            float emotionIntensity = ResolveSpeechEmotionIntensity(req);
            string json = "{\"text\":\"" + JsonEscape(text) + "\",\"voice\":\"" + JsonEscape(voice) +
                "\",\"lang\":\"" + JsonEscape(ResolveLanguage(voice)) + "\",\"speed\":" + config.KokoroSpeed.ToString(CultureInfo.InvariantCulture) +
                ",\"pitch_semitones\":" + GetVoiceDepthSemitones().ToString(CultureInfo.InvariantCulture) +
                ",\"audio_style\":\"" + JsonEscape(config.AudioStyle) + "\",\"module\":\"Player\",\"turn_id\":\"" + JsonEscape(activeVoiceEngineTurnId) +
                "\",\"emotion\":\"" + JsonEscape(emotion) + "\",\"emotion_intensity\":" + emotionIntensity.ToString("0.###", CultureInfo.InvariantCulture) + "}";
            string url = "http://127.0.0.1:" + config.KokoroPort + "/synthesize";
            using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
            {
                byte[] body = Encoding.UTF8.GetBytes(json);
                request.uploadHandler = new UploadHandlerRaw(body);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.timeout = 120;
                UnityWebRequestAsyncOperation op = request.SendWebRequest();
                while (!op.isDone)
                {
                    if (generation != speechGeneration || playerDead) { request.Abort(); yield break; }
                    yield return null;
                }
#if UNITY_2020_1_OR_NEWER
                bool failed = request.result != UnityWebRequest.Result.Success;
#else
                bool failed = request.isNetworkError || request.isHttpError;
#endif
                if (!failed && request.downloadHandler != null && request.downloadHandler.data != null && request.downloadHandler.data.Length > 44)
                {
                    try { File.WriteAllBytes(wavPath, request.downloadHandler.data); } catch { }
                }
                else if (failed)
                    Debug.LogWarning("[PlayerVO] Kokoro synthesis failed: " + request.error);
            }
        }

        private IEnumerator LoadWav(string path, Action<AudioClip> loaded)
        {
            string uri;
            try { uri = new Uri(path).AbsoluteUri; }
            catch { uri = "file:///" + path.Replace("\\", "/"); }
            using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.WAV))
            {
                yield return request.SendWebRequest();
#if UNITY_2020_1_OR_NEWER
                bool failed = request.result != UnityWebRequest.Result.Success;
#else
                bool failed = request.isNetworkError || request.isHttpError;
#endif
                loaded(failed ? null : DownloadHandlerAudioClip.GetContent(request));
            }
        }

        private string GetCachePath(string text)
        {
            string key = text + "|" + GetEffectivePlayerVoice() + "|" + config.KokoroSpeed.ToString(CultureInfo.InvariantCulture) +
                "|depth=" + GetVoiceDepthSemitones().ToString(CultureInfo.InvariantCulture) + "|" + config.AudioStyle + "|" + CacheSchema;
            return Path.Combine(cacheDir, Sha1(key) + ".wav");
        }

        private string ResolveLanguage(string voice)
        {
            string lang = (config.KokoroLanguage ?? "auto").Trim().ToLowerInvariant();
            if (lang != "auto" && lang.Length > 0) return lang;
            string v = (voice ?? string.Empty).Trim().ToLowerInvariant();
            if (v.Length > 0)
            {
                char c = v[0];
                if (c == 'a' || c == 'b' || c == 'e' || c == 'f' || c == 'h' || c == 'i' || c == 'p' || c == 'j' || c == 'z') return c.ToString();
            }
            return "a";
        }

        #endregion

        #region Tests / console

        private void RunSettingsSoundTest(int preset)
        {
            string key = preset == 1 ? "General" : preset == 2 ? "RaceTest" : preset == 3 ? "PersonalityTest" :
                preset == 4 ? "CombatEnd" : preset == 5 ? "Action.CombatEnd" : preset == 6 ? "Hungry" : "General";
            string text;
            if (key == "RaceTest") text = ResolveBark("General", true);
            else if (key == "PersonalityTest") text = IsAgentPersonality() ? "The Apprentice is the Agent: new to the land, dedicated to the quest, and sparing with words." : PickFromKeys(new string[] { "Personality." + GetPersonalityKey() + ".General", "General" });
            else if (key.StartsWith("Action.")) text = PickFromKeys(new string[] { key, "CombatEnd" });
            else text = ResolveBark(key, true, preset == 5);
            if (string.IsNullOrEmpty(text)) text = "Voice test. The road through the Iliac Bay is long.";
            EnqueueSpeech(text, SpeechPriority.Manual, "sound-test", false);
        }

        private void RegisterConsoleCommands()
        {
            try
            {
                ConsoleCommandsDatabase.RegisterCommand("playervo_test", "Tests the configured PlayerVO voice.", "playervo_test", ConsoleTest);
                ConsoleCommandsDatabase.RegisterCommand("playervo_bark", "Forces a context-sensitive player bark.", "playervo_bark", ConsoleBark);
                ConsoleCommandsDatabase.RegisterCommand("playervo_say", "Speaks exact player-authored text.", "playervo_say <text>", ConsoleSay);
                ConsoleCommandsDatabase.RegisterCommand("playervo_event", "Injects a PlayerVO context event for testing/integration.", "playervo_event <eventKey>", ConsoleEvent);
                ConsoleCommandsDatabase.RegisterCommand("playervo_reload", "Reloads PlayerVO.ini and Barks.json.", "playervo_reload", ConsoleReload);
                ConsoleCommandsDatabase.RegisterCommand("playervo_status", "Shows PlayerVO runtime/integration status.", "playervo_status", ConsoleStatus);
                ConsoleCommandsDatabase.RegisterCommand("playervo_clear_cache", "Clears generated PlayerVO WAV cache.", "playervo_clear_cache", ConsoleClearCache);
            }
            catch (Exception ex) { Debug.LogWarning("[PlayerVO] Console commands unavailable: " + ex.Message); }
        }

        private static string ConsoleTest(params string[] args)
        {
            if (instance == null) return "PlayerVO is not initialized.";
            instance.EnqueueSpeech("Player voice test. I am ready for whatever the Iliac Bay has waiting.", SpeechPriority.Manual, "test", false);
            return "PlayerVO test queued.";
        }
        private static string ConsoleBark(params string[] args)
        {
            if (instance == null) return "PlayerVO is not initialized.";
            instance.TriggerManualContextBark(); return "Context bark requested.";
        }
        private static string ConsoleSay(params string[] args)
        {
            if (instance == null) return "PlayerVO is not initialized.";
            if (args == null || args.Length == 0) return "Usage: playervo_say <text>";
            instance.EnqueueSpeech(string.Join(" ", args), SpeechPriority.Typed, "console", false); return "Player speech queued.";
        }
        private static string ConsoleEvent(params string[] args)
        {
            if (instance == null) return "PlayerVO is not initialized.";
            if (args == null || args.Length == 0) return "Usage: playervo_event <eventKey>";
            string key = args[0]; instance.RecordContext(key); instance.TryAutomaticBark(key, true); return "PlayerVO event injected: " + key;
        }
        private static string ConsoleReload(params string[] args)
        {
            if (instance == null) return "PlayerVO is not initialized.";
            instance.LoadAllSettings(); return "PlayerVO settings and bark library reloaded.";
        }
        private static string ConsoleStatus(params string[] args)
        {
            if (instance == null) return "PlayerVO is not initialized.";
            instance.EnsureNpcvoReflection(); instance.EnsureClimatesCaloriesReflection();
            return "Daggerfall Narrator - Player v0.3.7 | Voice=" + instance.GetEffectivePlayerVoice() + " | Race=" + instance.GetRaceKey() +
                " | Personality=" + instance.GetPersonalityKey() + " | BarkKey=" + instance.barkKey + " | BarkMode=" + instance.config.BarkKeyMode +
                " | NPCVO=" + (instance.npcvoType == null ? "not detected" : "detected") +
                " | C&C=" + ((instance.ccRootType == null && instance.ccHungerType == null) ? "not detected" : "detected") +
                " | RecentEvent=" + instance.recentContextEvent;
        }
        private static string ConsoleClearCache(params string[] args)
        {
            if (instance == null) return "PlayerVO is not initialized.";
            int count = 0; try { foreach (string f in Directory.GetFiles(instance.cacheDir, "*.wav")) { File.Delete(f); count++; } } catch { }
            return "Cleared " + count + " PlayerVO cache file(s).";
        }

        #endregion

        #region Utilities/cache/default content

        private void CleanupCache()
        {
            if (config.MaxCacheSizeMB <= 0) return;
            try
            {
                FileInfo[] files = new DirectoryInfo(cacheDir).GetFiles("*.wav");
                long max = (long)config.MaxCacheSizeMB * 1024L * 1024L;
                long total = 0; foreach (FileInfo f in files) total += f.Length;
                if (total <= max) return;
                Array.Sort(files, delegate(FileInfo a, FileInfo b) { return a.LastAccessTimeUtc.CompareTo(b.LastAccessTimeUtc); });
                long target = (long)(max * 0.85);
                foreach (FileInfo f in files) { if (total <= target) break; long len = f.Length; try { f.Delete(); total -= len; } catch { } }
            }
            catch { }
        }

        private static object ReadMember(object target, string name)
        {
            if (target == null || string.IsNullOrEmpty(name)) return null;
            Type t = target.GetType();
            while (t != null)
            {
                try
                {
                    PropertyInfo p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(target, null);
                    FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (f != null) return f.GetValue(target);
                }
                catch { }
                t = t.BaseType;
            }
            return null;
        }
        private static bool ReadBoolMember(object target, string name, bool fallback)
        {
            try { object v = ReadMember(target, name); return v == null ? fallback : Convert.ToBoolean(v, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }
        private static string JsonEscape(string s) { return (s ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n"); }
        private static string Sha1(string s)
        {
            using (SHA1 sha = SHA1.Create())
            {
                byte[] b = sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? string.Empty));
                StringBuilder sb = new StringBuilder(); foreach (byte x in b) sb.Append(x.ToString("x2", CultureInfo.InvariantCulture)); return sb.ToString();
            }
        }

        private static BarkLibrary BuildDefaultBarkLibrary()
        {
            List<BarkSet> sets = new List<BarkSet>();
            Action<string, string[]> add = delegate(string key, string[] lines) { sets.Add(new BarkSet { key = key, lines = lines }); };

            // Core voice: concise, world-aware, and intentionally closer to Morrowind's restrained
            // dialogue discipline than to modern quip-heavy fantasy. These lines assume the Iliac Bay
            // during Daggerfall's own period and avoid later Third Era outcomes.
            add("General", new[] { "Best keep moving.", "The road will not walk itself.", "Keep your eyes open.", "One road at a time.", "There is always another turning." });
            add("CombatStart", new[] { "Steel out.", "Trouble. Ready yourself.", "So be it.", "They have chosen poorly." });
            add("CombatEnd", new[] { "It is done.", "That was the last of them.", "Still standing.", "The road is quiet again." });
            add("BowDraw", new[] { "Easy now.", "One clean shot.", "Hold steady." });
            add("MagicCast", new[] { "Let the spell hold.", "By the old arts.", "Now, magicka answer me." });
            add("HeavyDamage", new[] { "That struck deep.", "I felt that.", "Careful. Another like that will cost me." });
            add("LowHealth", new[] { "I am badly hurt.", "I need to mind my wounds.", "I cannot take much more." });
            add("CriticalHealth", new[] { "A healer. Quickly.", "One more good blow may finish me.", "I am at death's door." });
            add("Recovered", new[] { "Better.", "Strength returns.", "That will keep me on my feet." });
            add("LowFatigue", new[] { "I need a moment to breathe.", "My legs are failing me.", "I cannot keep this pace forever." });
            add("LowMagicka", new[] { "My magicka is nearly spent.", "I have little spellcraft left in me.", "I must husband what magicka remains." });
            add("LowBreath", new[] { "Air. Now.", "I need the surface.", "My breath is nearly gone." });
            add("Encumbered", new[] { "I carry too much.", "This burden will slow me.", "I should leave something behind." });
            add("Unburdened", new[] { "Better. I can move again.", "That is a burden shed.", "Much lighter." });
            add("DungeonEnter", new[] { "Keep a hand near your weapon.", "Old stone, old dangers.", "Few places like this are truly empty.", "Let us see what waits below." });
            add("TownEnter", new[] { "A roof and a market would be welcome.", "Back among walls and watchmen.", "Let us see what news travels here." });
            add("Wilderness", new[] { "Open country again.", "The Bay has long roads.", "Best watch the horizon." });
            add("QuestAccepted", new[] { "I know what is asked of me.", "Another obligation, then.", "Very well. I have a task." });
            add("QuestResolved", new[] { "That matter is behind me.", "One obligation fewer.", "So ends that errand." });
            add("QuestFailed", new[] { "That road is closed to me now.", "I have failed that charge.", "No use pretending otherwise. That task is lost." });
            add("QuestTargetFound", new[] { "There you are.", "At last. The trail ends here.", "Found you." });
            add("QuestTargetKilled", new[] { "The contract is fulfilled.", "That settles the matter.", "The quarry is down." });
            add("Hungry", new[] { "I could use a proper meal.", "It has been too long since I ate.", "My stomach is beginning to complain." });
            add("Starving", new[] { "I need food before I go farther.", "Hunger is weakening me.", "I will not last long without a meal." });
            add("Thirsty", new[] { "I need water.", "A clean drink would be welcome.", "My mouth is dry as old parchment." });
            add("Wet", new[] { "Soaked through.", "These clothes will never dry at this rate.", "I have had enough rain for one journey." });
            add("Cold", new[] { "This cold is becoming dangerous.", "I can scarcely feel my fingers.", "I need warmth soon." });
            add("Hot", new[] { "This heat is punishing.", "Shade and water. Soon.", "Even the road seems to shimmer." });
            add("Camping", new[] { "This ground will serve.", "A fire and a few hours' rest will do.", "We make camp here." });
            add("Cooking", new[] { "Something warm at last.", "A hot meal will put strength back in me.", "Better than another mouthful of trail rations." });
            add("Nightfall", new[] { "Night comes quickly on the road.", "Best mind what moves after dark.", "The light is going." });
            add("Rested", new[] { "That rest did me good.", "I am ready to move.", "Strength enough for another road." });
            add("Poisoned", new[] { "Poison. I need a remedy.", "Something foul is in my blood.", "I must cure this before it worsens." });
            add("Diseased", new[] { "I feel ill. This is no common weariness.", "I should find a healer.", "Something is wrong with me." });
            add("TravelStart", new[] { "Then we are off.", "Another road begins.", "Best make good time." });
            add("TravelEnd", new[] { "At last.", "We have arrived.", "That is enough road for now." });

            // Race flavor changes worldview and idiom rather than merely applying an accent.
            add("Race.HighElf.General", new[] { "The Iliac Bay has ambition, if not always refinement.", "One grows accustomed to provincial habits.", "Competence remains a rare and useful virtue." });
            add("Race.HighElf.CombatStart", new[] { "Do not embarrass yourselves.", "Very well. Let us settle the matter properly.", "You mistake patience for weakness." });
            add("Race.HighElf.CombatEnd", new[] { "Predictable.", "As expected.", "They overestimated themselves." });
            add("Race.HighElf.DungeonEnter", new[] { "Crude masonry. Let us hope its secrets are less disappointing.", "Age does not make every ruin worthy." });
            add("Race.HighElf.MagicCast", new[] { "At least the old disciplines endure.", "A little proper sorcery." });
            add("Race.HighElf.Hungry", new[] { "Even discipline does not excuse an empty stomach.", "I require something fit to eat." });
            add("Race.HighElf.Cold", new[] { "What a miserable climate.", "I was not bred for this dreary cold." });
            add("Race.HighElf.TownEnter", new[] { "Let us see what passes for civility here.", "Perhaps someone here keeps a respectable library." });

            add("Race.Khajiit.General", new[] { "This one keeps to the road.", "Khajiit has walked stranger paths.", "The moons see many roads, and so does this one." });
            add("Race.Khajiit.CombatStart", new[] { "Claws out, then.", "This one warned you.", "Khajiit is not easy prey." });
            add("Race.Khajiit.CombatEnd", new[] { "This one still has all nine lives. More or less.", "The foolish ones are quiet now.", "Khajiit prefers victories that leave the fur intact." });
            add("Race.Khajiit.DungeonEnter", new[] { "This one does not trust the smell of this place.", "Khajiit would rather have moonlight overhead." });
            add("Race.Khajiit.Hungry", new[] { "This one would welcome something to eat.", "Khajiit's stomach has begun making demands." });
            add("Race.Khajiit.Thirsty", new[] { "This one needs water before the road grows longer." });
            add("Race.Khajiit.Cold", new[] { "This one remembers warmer sands.", "Cold gets beneath the fur all the same." });
            add("Race.Khajiit.TownEnter", new[] { "Where there are walls, there are purses and rumors.", "This one wonders what the market holds." });

            add("Race.Argonian.General", new[] { "The path changes. I change with it.", "Still water may hide a deep channel.", "I have crossed stranger ground than this." });
            add("Race.Argonian.CombatStart", new[] { "Then let blood answer blood.", "Come closer, if you choose." });
            add("Race.Argonian.CombatEnd", new[] { "The danger has passed.", "Their struggle is finished." });
            add("Race.Argonian.DungeonEnter", new[] { "Dry stone remembers little of living things.", "The air here has been still too long." });
            add("Race.Argonian.Cold", new[] { "The cold slows the blood.", "I need warmth before my limbs grow stiff." });
            add("Race.Argonian.Wet", new[] { "At least the water is familiar.", "Others curse the rain. I have known worse." });
            add("Race.Argonian.Hot", new[] { "The heat is heavy, even for me." });

            add("Race.Nord.General", new[] { "No sense standing about.", "Forward, then.", "A long road is still only a road." });
            add("Race.Nord.CombatStart", new[] { "Come on, then!", "Steel speaks now.", "Let's have it." });
            add("Race.Nord.CombatEnd", new[] { "A good enough fight.", "They should have brought more steel.", "Done. Who is next?" });
            add("Race.Nord.Cold", new[] { "This? I have known colder mornings.", "A little frost never killed a Nord worth the name." });
            add("Race.Nord.DungeonEnter", new[] { "Stone, dust, and something waiting to die.", "If there is treasure below, we earn it." });
            add("Race.Nord.Hungry", new[] { "I could eat a whole roast by now.", "Food first, heroics after." });

            add("Race.DarkElf.General", new[] { "I have endured worse roads than this.", "Let us be done with it.", "The west is loud, but rarely subtle." });
            add("Race.DarkElf.CombatStart", new[] { "You have chosen your end.", "Very well. Come." });
            add("Race.DarkElf.CombatEnd", new[] { "Fools die everywhere alike.", "Finished.", "Their ancestors may judge them now." });
            add("Race.DarkElf.DungeonEnter", new[] { "The dead are seldom the worst company in a ruin.", "Old places breed old grudges." });
            add("Race.DarkElf.TownEnter", new[] { "Another western town. Let us hope its merchants are less dull than its walls." });
            add("Race.DarkElf.MagicCast", new[] { "Magic has older masters than these western hedge-wizards." });

            add("Race.Redguard.General", new[] { "Keep your footing and your head.", "The road rewards discipline.", "A steady hand carries farther than boasting." });
            add("Race.Redguard.CombatStart", new[] { "Stand ready.", "Draw cleanly.", "Let skill decide it." });
            add("Race.Redguard.CombatEnd", new[] { "Good. The matter is settled.", "Poor form. Worse judgment.", "A blade is honest about such things." });
            add("Race.Redguard.BowDraw", new[] { "Breathe. Loose cleanly.", "Measure the distance." });
            add("Race.Redguard.DungeonEnter", new[] { "Watch the corners. Ruins favor the careless.", "Patience. Stone hides more than treasure." });
            add("Race.Redguard.TownEnter", new[] { "Let us see what trade and rumor have reached this place.", "A town is only as useful as its people." });
            add("Race.Redguard.QuestAccepted", new[] { "A charge accepted should be completed.", "I gave my word. That is enough." });

            add("Race.WoodElf.General", new[] { "The road is kinder beneath an open sky.", "Keep light on your feet.", "There is always a trail, if you know how to look." });
            add("Race.WoodElf.DungeonEnter", new[] { "Too much stone. Not enough sky.", "I dislike places where nothing grows." });
            add("Race.WoodElf.BowDraw", new[] { "Easy breath. Easy hand.", "One arrow is enough." });
            add("Race.WoodElf.CombatEnd", new[] { "Quiet again.", "The trail is clear." });
            add("Race.WoodElf.Hungry", new[] { "I should find something to eat before the trail lengthens." });

            add("Race.Breton.General", new[] { "Another turn in the road.", "The Bay has a way of making every errand political.", "Let us see where this leads." });
            add("Race.Breton.MagicCast", new[] { "Let us put the old arts to work.", "A little sorcery may save a great deal of steel." });
            add("Race.Breton.TownEnter", new[] { "There will be a guildhall, a tavern, and three opinions for every fact.", "Let us hear what the locals are whispering." });
            add("Race.Breton.QuestAccepted", new[] { "Very well. Another thread in the Bay's tangled affairs.", "I will see it through." });
            add("Race.Breton.DungeonEnter", new[] { "Every old keep in High Rock seems to have a cellar full of trouble." });

            // Personality overlays are independent of race. They are intentionally short so race and
            // personality combinations do not turn every bark into a monologue.

            // Action Movie is the deliberate exception to the period voice: recognizable movie-style
            // references are only eligible when this mode is enabled and the triggering context fits.
            add("Action.BowDraw", new[] { "Stick around.", "Let's pin this down.", "Stay right there." });
            add("Action.CombatEnd", new[] { "Anybody else?", "Consider that settled.", "You should have stayed at the tavern." });
            add("Action.MagicCast", new[] { "Now you see the magic trick.", "A little theatrical sorcery." });
            add("Action.FrostKill", new[] { "Ice to meet you.", "Cold enough for you?", "You needed to cool your temper." });
            add("Action.FireKill", new[] { "You're fired.", "That should warm you up.", "Ashes to ashes." });
            add("Action.HeavyDamage", new[] { "I've had worse.", "Is that all you've got?", "You'll have to do better than that." });
            add("Action.QuestResolved", new[] { "Case closed.", "Another happy ending. More or less.", "That's one for the chronicles." });
            add("Action.QuestTargetKilled", new[] { "Contract fulfilled.", "Consider yourself dismissed.", "End of the line." });

            // Semantic responses never alter DFU's actual choice. They only give the selected button a
            // spoken, typewritten presentation when the enhanced quest/court bridge can identify it.
            add("Choice.Accept", new[] { "Very well. I will do it.", "Agreed. I will see it done.", "You have my help." });
            add("Choice.Reject", new[] { "No. Find another hand.", "I must decline.", "That is not a charge I will take." });
            add("Choice.Yes", new[] { "Yes.", "Very well.", "Agreed." });
            add("Choice.No", new[] { "No.", "Not this time.", "I think not." });
            add("Choice.Guilty", new[] { "Guilty.", "I plead guilty." });
            add("Choice.NotGuilty", new[] { "Not guilty.", "I deny the charge." });
            add("Choice.Debate", new[] { "Then hear my argument.", "Allow me to make my case.", "There is more to this than you have heard." });
            add("Choice.Lie", new[] { "That is not how it happened.", "You have been told the wrong tale.", "Your account is mistaken." });

            add("Race.Khajiit.Choice.Accept", new[] { "This one accepts.", "Khajiit will do what is asked.", "This one will take the task." });
            add("Race.Khajiit.Choice.Reject", new[] { "This one must decline.", "Khajiit has another road to walk." });
            add("Race.HighElf.Choice.Accept", new[] { "Very well. I will see it done.", "I suppose this task does require a capable hand." });
            add("Race.HighElf.Choice.Reject", new[] { "Certainly not.", "I have better uses for my time." });
            add("Race.Argonian.Choice.Accept", new[] { "I will see it through.", "The path is clear. I accept." });
            add("Race.Argonian.Choice.Reject", new[] { "No. My path leads elsewhere.", "I will not take this task." });
            add("Race.Nord.Choice.Accept", new[] { "Aye. I will do it.", "Done. Point me where I need to go." });
            add("Race.Nord.Choice.Reject", new[] { "No. Find someone else.", "Not my fight." });
            add("Race.Redguard.Choice.Accept", new[] { "Agreed. I will see it done.", "You have my word." });
            add("Race.Redguard.Choice.Reject", new[] { "No. I will not take this charge.", "My answer is no." });
            add("Race.DarkElf.Choice.Accept", new[] { "Very well. I accept.", "Fine. I will handle it." });
            add("Race.DarkElf.Choice.Reject", new[] { "No. Trouble someone else.", "I have no use for this errand." });
            add("Race.WoodElf.Choice.Accept", new[] { "All right. I will do it.", "Agreed. I will see where the trail leads." });
            add("Race.WoodElf.Choice.Reject", new[] { "No. I will pass.", "Find another traveler." });
            add("Race.Breton.Choice.Accept", new[] { "Very well. You have my help.", "Agreed. I will take the task." });
            add("Race.Breton.Choice.Reject", new[] { "I must decline.", "No. I cannot help you." });

            // Known main-quest hooks are intentionally spoiler-light. They key off quest names already
            // visible to the quest system and never assume knowledge of later events such as the Warp.
            // Location-aware pools. Exact place observations outrank generic town/dungeon lines.
            add("Location.Daggerfall.General", new[] { "Daggerfall. Lysandus' shadow hangs over more than the castle walls.", "In Daggerfall, every tavern seems to have a rumor and every courtier a secret." });
            add("Location.Daggerfall.TownEnter", new[] { "Daggerfall again. Best keep my ears open for talk of Lysandus.", "The city of Daggerfall. Plenty of stone, gold, and unfinished business." });
            add("Location.Wayrest.General", new[] { "Wayrest has enough wealth to make every smile look expensive.", "Wayrest. Polished stone above, knives behind the curtains." });
            add("Location.Wayrest.TownEnter", new[] { "Wayrest. Best remember that courtly manners can be sharper than steel.", "Back in Wayrest. Someone here always seems to be bargaining for something." });
            add("Location.Sentinel.General", new[] { "Sentinel wears its pride openly. I should mind the customs of the court.", "Sentinel. Sun, stone, and a court that remembers every slight." });
            add("Location.Sentinel.TownEnter", new[] { "Sentinel. Best step carefully until I know whose favor matters today.", "The streets of Sentinel again. Honor is cheap to claim and costly to prove." });
            add("Location.PrivateersHold.General", new[] { "Privateer's Hold. The Bay chose a charming hole in the ground to welcome me.", "Privateer's Hold still smells of damp stone and bad luck." });
            add("Location.PrivateersHold.DungeonEnter", new[] { "Privateer's Hold. I remember enough of this place already.", "Back into Privateer's Hold. Let us make this visit shorter than the first." });
            add("Location.DirennisTower.General", new[] { "Direnni Tower. Old Aldmeri stone, and older secrets besides.", "The Direnni left their mark on the Bay. This tower is proof enough." });
            add("Location.ScourgBarrow.General", new[] { "Scourg Barrow. Nothing about that name promises a pleasant visit.", "A barrow this old has had plenty of time to collect trouble." });
            add("Location.Shedungent.General", new[] { "Shedungent. Even the air here feels like it is keeping a secret.", "This place has the look of somewhere sensible people avoid." });
            add("Location.Orsinium.General", new[] { "Orsinium. Whatever the neighboring courts say, this place means to endure.", "Orsinium has heard enough outsiders judge it. I will look with my own eyes." });
            add("Region.Daggerfall.General", new[] { "These lands know the name Lysandus too well.", "Daggerfall's province has a long memory for kings and grudges." });
            add("Region.Wayrest.General", new[] { "Wayrest's influence reaches farther than its walls.", "In Wayrest's lands, coin and family names travel quickly." });
            add("Region.Sentinel.General", new[] { "Sentinel's lands reward respect more readily than presumption.", "Hammerfell has little patience for someone who cannot stand by their word." });

            // Affiliation/reputation-aware pools. The resolver selects the player's highest-ranked
            // current guild membership and local legal reputation without changing game state.
            add("GuildMember.General", new[] { "A {guildtitle} of {guild} ought to remember who may be watching.", "My standing with {guild} may open doors that coin will not." });
            add("GuildMember.TownEnter", new[] { "If {guild} has friends here, this may be easier than it looks.", "I should remember what my name means to {guild} in this place." });
            add("Guild.MagesGuild.General", new[] { "The Mages Guild has taught me that knowledge is rarely free, merely differently priced.", "If there is magic at work here, the Guild may know more than it admits." });
            add("Guild.FightersGuild.General", new[] { "Guild work has taught me to look at exits before contracts.", "The Fighters Guild pays for results, not excuses." });
            add("Guild.ThievesGuild.General", new[] { "Some doors open with a key. Others open because you know whom to ask.", "The Guild teaches discretion before cleverness." });
            add("Guild.DarkBrotherhood.General", new[] { "Some obligations are better left unnamed.", "There are promises one does not discuss in daylight." });
            add("Reputation.Disreputable.General", new[] { "My name has acquired a certain odor here. Best tread carefully.", "People remember trouble longer than favors." });
            add("Reputation.Notorious.General", new[] { "The guards may know my face. I should give them no fresh reason to remember it.", "My reputation here has become difficult to ignore." });
            add("Reputation.Respected.General", new[] { "My name carries a little weight here now.", "A decent reputation is useful currency. I should not waste it." });
            add("Reputation.Honored.General", new[] { "People here know my name for the right reasons. That may prove useful.", "Good standing opens doors more quietly than force ever could." });

            add("Quest._BRISIEN.Accepted", new[] { "Lady Brisienna, then. Best not keep the Emperor's agent waiting." });
            add("Quest.S0000999.Accepted", new[] { "The Emperor's charge still hangs over me. I should remember why I came to the Bay." });

            AddBirthsignWriting(add);
            return new BarkLibrary { schemaVersion = 4, sets = sets.ToArray() };
        }

        private static void AddBirthsignWriting(Action<string, string[]> add)
        {
            // Birthsign personality is the root of the bark tree. The Apprentice maps to the Agent archetype: sparse automatic speech, but deliberate choices remain voiced.
            add("Personality.Mage.General", new[] { "There is always more to understand.", "Knowledge is useful only when one knows what to do with it.", "Observe first. Conclusions can wait." });
            add("Personality.Mage.CombatStart", new[] { "If reason has failed, then let discipline answer.", "Enough. I will not let ignorance become violence without reply." });
            add("Personality.Mage.CombatEnd", new[] { "A poor lesson, but a lesson all the same.", "Done. Better to remember why it came to blows." });
            add("Personality.Mage.DungeonEnter", new[] { "Old places deserve caution. Their builders usually had reasons for closing the door.", "Ruins keep knowledge long after they lose their keepers." });
            add("Personality.Mage.TownEnter", new[] { "A settlement is a library written in habits instead of ink.", "Listen long enough and every town explains itself." });
            add("Personality.Mage.TavernEnter", new[] { "Rumor is unreliable, but patterns survive even bad telling.", "A tavern can teach as much about a town as its archives." });
            add("Personality.Mage.TempleEnter", new[] { "Faith preserves memories scholarship often neglects.", "Ritual can outlive the reason that first created it." });
            add("Personality.Mage.GuildHallEnter", new[] { "Institutions remember what individuals forget.", "A guild hall is useful when its members still value the craft." });
            add("Personality.Mage.Wilderness", new[] { "The land has its own history, even where no one bothered to write it down.", "There are older lessons beneath open sky than in many towers." });
            add("Personality.Mage.QuestAccepted", new[] { "I will see this through, and understand what I can along the way.", "Very well. Better a difficult truth than an easy assumption." });
            add("Personality.Mage.QuestResolved", new[] { "Another question answered. Usually that only reveals the next one.", "The matter is settled. I should remember what it taught me." });
            add("Personality.Mage.HeavyDamage", new[] { "Pain is persuasive, if not particularly subtle.", "That was an argument I would rather not hear twice." });
            add("Personality.Mage.LowHealth", new[] { "Wisdom now means finding a healer.", "I have learned enough about this wound already." });
            add("Personality.Mage.LowFatigue", new[] { "A tired mind makes careless judgments.", "I need rest before exhaustion starts deciding for me." });
            add("Personality.Mage.LowMagicka", new[] { "My reserves are nearly spent. Time for restraint.", "No sense proving a theorem with an empty well of magicka." });
            add("Personality.Mage.Hungry", new[] { "Even scholarship fares poorly on an empty stomach." });
            add("Personality.Mage.Thirsty", new[] { "Water first. Thought becomes very small when the body is neglected." });
            add("Personality.Mage.Cold", new[] { "Cold dulls the hands before it dulls the mind. I should warm both." });
            add("Personality.Mage.Nightfall", new[] { "Night hides detail. That makes observation more important, not less." });
            add("Personality.Mage.MagicCast", new[] { "Form, intent, release.", "Let the old principles hold true once more." });
            add("Personality.Mage.BowDraw", new[] { "Measure distance. Then loose." });
            add("Personality.Mage.TravelStart", new[] { "Every road is an argument about where the world connects." });
            add("Personality.Mage.TravelEnd", new[] { "Arrival is the useful part. Now I can learn why this place matters." });
            add("Personality.Mage.Poisoned", new[] { "Poison is alchemy with malicious intent. I still need an antidote." });
            add("Personality.Mage.Diseased", new[] { "Symptoms first, panic later. I need a healer." });
            add("Personality.Ritual.General", new[] { "The timid call knowledge forbidden when they fear its price.", "Power leaves traces. I have learned to notice them.", "Every prohibition tells me where someone buried a secret." });
            add("Personality.Ritual.CombatStart", new[] { "Blood is such an old currency.", "You have chosen to contribute to the experiment." });
            add("Personality.Ritual.CombatEnd", new[] { "Death is informative when one bothers to study it.", "Another silence. Sometimes silence is useful." });
            add("Personality.Ritual.DungeonEnter", new[] { "Sealed places attract me for excellent reasons and everyone else for terrible ones.", "If something was buried here, I want to know why it frightened them." });
            add("Personality.Ritual.TownEnter", new[] { "Civilized streets conceal the same hungers as tombs. They merely dress them better." });
            add("Personality.Ritual.TavernEnter", new[] { "Wine loosens tongues. Fear loosens them faster." });
            add("Personality.Ritual.TempleEnter", new[] { "Priests guard mysteries with ceremony and call curiosity a sin.", "Sacred places are often old power wrapped in better ceremony." });
            add("Personality.Ritual.GuildHallEnter", new[] { "Guild rules exist to keep lesser minds from touching dangerous things." });
            add("Personality.Ritual.Wilderness", new[] { "There are places beyond law where useful rites leave fewer witnesses." });
            add("Personality.Ritual.QuestAccepted", new[] { "If this leads somewhere interesting, I will indulge it.", "Very well. Let us see what this task is really hiding." });
            add("Personality.Ritual.QuestResolved", new[] { "Useful. The cost was acceptable.", "One mystery closed, and perhaps a more profitable one opened." });
            add("Personality.Ritual.HeavyDamage", new[] { "Pain is temporary. Lessons endure." });
            add("Personality.Ritual.LowHealth", new[] { "Not yet. I have too much left to uncover." });
            add("Personality.Ritual.LowFatigue", new[] { "The body complains. It will obey a little longer." });
            add("Personality.Ritual.LowMagicka", new[] { "Annoying. Power without reserves is only theory." });
            add("Personality.Ritual.Hungry", new[] { "The flesh insists on its petty maintenance." });
            add("Personality.Ritual.Thirsty", new[] { "I require water. Even ambition has a body attached to it." });
            add("Personality.Ritual.Cold", new[] { "Cold preserves corpses better than patience preserves secrets." });
            add("Personality.Ritual.Nightfall", new[] { "Night improves the world. Fewer witnesses, clearer intentions." });
            add("Personality.Ritual.MagicCast", new[] { "Power answers those willing to command it." });
            add("Personality.Ritual.BowDraw", new[] { "Distance makes the result no less final." });
            add("Personality.Ritual.TravelStart", new[] { "If the road ends somewhere forbidden, so much the better." });
            add("Personality.Ritual.TravelEnd", new[] { "At last. Let us see what everyone else was afraid to touch." });
            add("Personality.Ritual.Poisoned", new[] { "A crude poison. I refuse to die from someone else's unimaginative work." });
            add("Personality.Ritual.Diseased", new[] { "Disease is merely another process to master before it masters me." });
            add("Personality.Lady.General", new[] { "Courtesy reveals more than aggression ever does.", "A good name opens doors force only damages.", "People remember how they were treated." });
            add("Personality.Lady.CombatStart", new[] { "There was still time to choose civility. They wasted it." });
            add("Personality.Lady.CombatEnd", new[] { "A needless end to a conversation that never began." });
            add("Personality.Lady.DungeonEnter", new[] { "Places like this punish arrogance. Caution is not cowardice." });
            add("Personality.Lady.TownEnter", new[] { "New streets, new customs. Best learn both before judging either." });
            add("Personality.Lady.TavernEnter", new[] { "If I want the truth of a town, I should listen to the people who work after court closes." });
            add("Personality.Lady.TempleEnter", new[] { "Respect costs nothing, especially in another people's sacred place." });
            add("Personality.Lady.GuildHallEnter", new[] { "Every guild has rules, rivalries, and someone whose opinion matters more than their title suggests." });
            add("Personality.Lady.Wilderness", new[] { "No walls, no court, no audience. A useful change." });
            add("Personality.Lady.QuestAccepted", new[] { "You have my word. I will treat the matter seriously." });
            add("Personality.Lady.QuestResolved", new[] { "Good. Let the people concerned have some peace from it." });
            add("Personality.Lady.HeavyDamage", new[] { "That was an emphatic rejection." });
            add("Personality.Lady.LowHealth", new[] { "Pride will not stop the bleeding. I need help." });
            add("Personality.Lady.LowFatigue", new[] { "I need to recover before exhaustion makes me careless with people or danger." });
            add("Personality.Lady.LowMagicka", new[] { "I have little magicka left. Persuasion may have to do the work." });
            add("Personality.Lady.Hungry", new[] { "A meal and a civilized table would be welcome." });
            add("Personality.Lady.Thirsty", new[] { "I would settle for water and five quiet minutes." });
            add("Personality.Lady.Cold", new[] { "Hospitality suddenly sounds less ceremonial and more essential." });
            add("Personality.Lady.Nightfall", new[] { "At night, intentions become harder to read. I should watch more closely." });
            add("Personality.Lady.MagicCast", new[] { "Power is most useful when applied with restraint." });
            add("Personality.Lady.BowDraw", new[] { "One warning would have been preferable." });
            add("Personality.Lady.TravelStart", new[] { "The road is easier when one leaves a place with friends rather than grudges." });
            add("Personality.Lady.TravelEnd", new[] { "We are here. First impressions matter." });
            add("Personality.Lady.Poisoned", new[] { "Someone has made this personal. I need an antidote before I ask who." });
            add("Personality.Lady.Diseased", new[] { "I should find a healer before I become someone else's problem." });
            add("Personality.Lord.General", new[] { "Authority is wasted on those who mistake inheritance for merit.", "The world belongs to those capable of imposing order upon it.", "Let lesser people debate permission." });
            add("Personality.Lord.CombatStart", new[] { "You presume far beyond your station." });
            add("Personality.Lord.CombatEnd", new[] { "Order restored." });
            add("Personality.Lord.DungeonEnter", new[] { "Someone once commanded this place. Now it waits for a worthier will." });
            add("Personality.Lord.TownEnter", new[] { "Every town has a ruler. The interesting question is whether they deserve the obedience." });
            add("Personality.Lord.TavernEnter", new[] { "Useful places. Men confess their loyalties cheaply over drink." });
            add("Personality.Lord.TempleEnter", new[] { "Even gods require institutions to enforce their importance." });
            add("Personality.Lord.GuildHallEnter", new[] { "A hierarchy is only as strong as the person at its summit." });
            add("Personality.Lord.Wilderness", new[] { "No throne in sight. The land remains subject to whoever can hold it." });
            add("Personality.Lord.QuestAccepted", new[] { "Very well. I will decide how this matter ends." });
            add("Personality.Lord.QuestResolved", new[] { "As expected. The matter yielded to competence." });
            add("Personality.Lord.HeavyDamage", new[] { "Insolent." });
            add("Personality.Lord.LowHealth", new[] { "I will not be reduced to carrion by this place." });
            add("Personality.Lord.LowFatigue", new[] { "The body grows tiresome. The will does not." });
            add("Personality.Lord.LowMagicka", new[] { "My power is diminished, not my authority." });
            add("Personality.Lord.Hungry", new[] { "Find food. There is no dignity in being ruled by an empty stomach." });
            add("Personality.Lord.Thirsty", new[] { "I require water, not a lesson in humility." });
            add("Personality.Lord.Cold", new[] { "A miserable climate for people who expect to be obeyed." });
            add("Personality.Lord.Nightfall", new[] { "Darkness changes nothing. Predators remain predators." });
            add("Personality.Lord.MagicCast", new[] { "Obey." });
            add("Personality.Lord.BowDraw", new[] { "Kneeling would have been simpler." });
            add("Personality.Lord.TravelStart", new[] { "Let the road carry me to someone worth dealing with." });
            add("Personality.Lord.TravelEnd", new[] { "At last. Let us meet whoever imagines they hold power here." });
            add("Personality.Lord.Poisoned", new[] { "Poison. The weapon of someone too frightened to stand before me." });
            add("Personality.Lord.Diseased", new[] { "An indignity. I will have it cured." });
            add("Personality.Warrior.General", new[] { "Do the work in front of you.", "Courage is easier when you remember who depends on it.", "A promise should mean something." });
            add("Personality.Warrior.CombatStart", new[] { "Stand fast.", "You wanted a fight. Face me." });
            add("Personality.Warrior.CombatEnd", new[] { "It is finished. Check the wounded.", "Enough blood for one day." });
            add("Personality.Warrior.DungeonEnter", new[] { "Stay alert. Places like this punish carelessness.", "Whatever waits below, we meet it ready." });
            add("Personality.Warrior.TownEnter", new[] { "Walls are only worthwhile if the people behind them can sleep safely." });
            add("Personality.Warrior.TavernEnter", new[] { "A meal, a drink, and then back to the road." });
            add("Personality.Warrior.TempleEnter", new[] { "No harm in showing respect where others place their faith." });
            add("Personality.Warrior.GuildHallEnter", new[] { "A hall means little without people willing to stand for one another." });
            add("Personality.Warrior.Wilderness", new[] { "Open ground. Keep watch and keep moving." });
            add("Personality.Warrior.QuestAccepted", new[] { "You have my word. I will see it done." });
            add("Personality.Warrior.QuestResolved", new[] { "The charge is fulfilled." });
            add("Personality.Warrior.HeavyDamage", new[] { "Still standing." });
            add("Personality.Warrior.LowHealth", new[] { "I need treatment, not bravado." });
            add("Personality.Warrior.LowFatigue", new[] { "Slow down. Exhaustion gets good people killed." });
            add("Personality.Warrior.LowMagicka", new[] { "Then steel will have to suffice." });
            add("Personality.Warrior.Hungry", new[] { "I fight better fed. Simple truth." });
            add("Personality.Warrior.Thirsty", new[] { "Water first. No heroics against thirst." });
            add("Personality.Warrior.Cold", new[] { "Keep moving. Cold wins when you stop." });
            add("Personality.Warrior.Nightfall", new[] { "Night watch starts now." });
            add("Personality.Warrior.MagicCast", new[] { "Let it strike true." });
            add("Personality.Warrior.BowDraw", new[] { "Make the shot count." });
            add("Personality.Warrior.TravelStart", new[] { "We have a road and a purpose. That is enough." });
            add("Personality.Warrior.TravelEnd", new[] { "We made it. Now find who needs us." });
            add("Personality.Warrior.Poisoned", new[] { "Poison. Find a cure before it takes my strength." });
            add("Personality.Warrior.Diseased", new[] { "I need a healer. Duty is no excuse for spreading sickness." });
            add("Personality.Thief.General", new[] { "Fair fights are for people who failed to find an advantage.", "If it is guarded, someone thinks it is valuable.", "Locks are just opinions made of metal." });
            add("Personality.Thief.CombatStart", new[] { "You should have watched your back." });
            add("Personality.Thief.CombatEnd", new[] { "Expensive mistake on their part." });
            add("Personality.Thief.DungeonEnter", new[] { "Dark, dangerous, probably full of things nobody is using anymore." });
            add("Personality.Thief.TownEnter", new[] { "New streets. New purses. New patrol routes." });
            add("Personality.Thief.TavernEnter", new[] { "Now this is civilization: ale, loose tongues, and terrible judgment." });
            add("Personality.Thief.TempleEnter", new[] { "Donation boxes always have the most trusting locks." });
            add("Personality.Thief.GuildHallEnter", new[] { "Every guild has a back door, even when the members pretend otherwise." });
            add("Personality.Thief.Wilderness", new[] { "No guards for miles. Pity there is not much worth stealing either." });
            add("Personality.Thief.QuestAccepted", new[] { "If the reward is real, so am I." });
            add("Personality.Thief.QuestResolved", new[] { "Done. Now for the part where someone pays me." });
            add("Personality.Thief.HeavyDamage", new[] { "That was my cue to stop fighting fairly." });
            add("Personality.Thief.LowHealth", new[] { "Time to disappear before courage becomes stupidity." });
            add("Personality.Thief.LowFatigue", new[] { "Running out of breath is a terrible way to get caught." });
            add("Personality.Thief.LowMagicka", new[] { "No magicka. Fine. I still have hands and bad intentions." });
            add("Personality.Thief.Hungry", new[] { "Hard to rob anyone when my stomach announces me first." });
            add("Personality.Thief.Thirsty", new[] { "A tavern would solve at least one of my problems." });
            add("Personality.Thief.Cold", new[] { "Frozen fingers are useless on locks." });
            add("Personality.Thief.Nightfall", new[] { "Finally. The respectable people are going home." });
            add("Personality.Thief.MagicCast", new[] { "A spell is just another lockpick if you use it correctly." });
            add("Personality.Thief.BowDraw", new[] { "They cannot catch what they never reach." });
            add("Personality.Thief.TravelStart", new[] { "Let us hope the destination has richer people than the road." });
            add("Personality.Thief.TravelEnd", new[] { "Good. Time to learn where they keep the valuable things." });
            add("Personality.Thief.Poisoned", new[] { "Poison? Rude. Effective, but rude." });
            add("Personality.Thief.Diseased", new[] { "Wonderful. Even my blood is trying to report me." });
            add("Personality.Lover.General", new[] { "People matter more than the stories told about them.", "Kindness is rarely wasted.", "A little decency travels farther than most people think." });
            add("Personality.Lover.CombatStart", new[] { "Leave them alone.", "I asked you not to make this necessary." });
            add("Personality.Lover.CombatEnd", new[] { "I wish it had ended another way." });
            add("Personality.Lover.DungeonEnter", new[] { "I wonder how many people never came home from places like this." });
            add("Personality.Lover.TownEnter", new[] { "Good. People, lights, and somewhere warm." });
            add("Personality.Lover.TavernEnter", new[] { "A warm room full of stories sounds just right." });
            add("Personality.Lover.TempleEnter", new[] { "Whatever god they pray to, someone comes here needing hope." });
            add("Personality.Lover.GuildHallEnter", new[] { "Groups are only worth joining if they remember the people inside them." });
            add("Personality.Lover.Wilderness", new[] { "Beautiful country. Lonely, though." });
            add("Personality.Lover.QuestAccepted", new[] { "If someone needs help, I cannot simply walk away." });
            add("Personality.Lover.QuestResolved", new[] { "Good. I hope that leaves someone better off than before." });
            add("Personality.Lover.HeavyDamage", new[] { "That hurt. I would still rather be hit than watch someone else take it." });
            add("Personality.Lover.LowHealth", new[] { "I need help. No shame in saying it." });
            add("Personality.Lover.LowFatigue", new[] { "I need a rest before I become a burden to anyone." });
            add("Personality.Lover.LowMagicka", new[] { "I have little magicka left. I should save it for when someone truly needs it." });
            add("Personality.Lover.Hungry", new[] { "A simple meal would be enough." });
            add("Personality.Lover.Thirsty", new[] { "Water would be wonderful right now." });
            add("Personality.Lover.Cold", new[] { "I hope anyone else on this road has found shelter." });
            add("Personality.Lover.Nightfall", new[] { "Someone is probably waiting for a traveler who has not come home yet." });
            add("Personality.Lover.MagicCast", new[] { "Let this do some good." });
            add("Personality.Lover.BowDraw", new[] { "I would rather they turned away." });
            add("Personality.Lover.TravelStart", new[] { "One more road. We will get there together." });
            add("Personality.Lover.TravelEnd", new[] { "At last. I hope we are welcome." });
            add("Personality.Lover.Poisoned", new[] { "I need a cure before this gets worse." });
            add("Personality.Lover.Diseased", new[] { "Best find a healer, and keep my distance from others until I do." });
            add("Personality.Serpent.General", new[] { "Everyone has a breaking point. The interesting part is finding it.", "People are so much more honest when they are afraid.", "A little chaos keeps life from becoming dull." });
            add("Personality.Serpent.CombatStart", new[] { "Good. I was getting bored." });
            add("Personality.Serpent.CombatEnd", new[] { "That was almost entertaining." });
            add("Personality.Serpent.DungeonEnter", new[] { "Dark, miserable, probably lethal. Charming." });
            add("Personality.Serpent.TownEnter", new[] { "So many rules in one place. Where to begin?" });
            add("Personality.Serpent.TavernEnter", new[] { "Drink makes people brave, stupid, and wonderfully easy to provoke." });
            add("Personality.Serpent.TempleEnter", new[] { "All that virtue gathered in one room. Tempting." });
            add("Personality.Serpent.GuildHallEnter", new[] { "Rules, ranks, little badges of importance. Adorable." });
            add("Personality.Serpent.Wilderness", new[] { "No witnesses. The countryside improves already." });
            add("Personality.Serpent.QuestAccepted", new[] { "Why not? Perhaps someone interesting will suffer." });
            add("Personality.Serpent.QuestResolved", new[] { "And everyone lived happily ever after. Except the ones who did not." });
            add("Personality.Serpent.HeavyDamage", new[] { "Oh, now you have my attention." });
            add("Personality.Serpent.LowHealth", new[] { "Careful. I am much less pleasant when cornered." });
            add("Personality.Serpent.LowFatigue", new[] { "Even cruelty becomes work eventually." });
            add("Personality.Serpent.LowMagicka", new[] { "No magicka left. We will improvise." });
            add("Personality.Serpent.Hungry", new[] { "I am hungry. Someone nearby should probably be nervous." });
            add("Personality.Serpent.Thirsty", new[] { "I need a drink. Preferably somewhere with fragile furniture." });
            add("Personality.Serpent.Cold", new[] { "Cold enough to make everyone miserable. Lovely." });
            add("Personality.Serpent.Nightfall", new[] { "The dark makes honest people imagine such wonderful things." });
            add("Personality.Serpent.MagicCast", new[] { "Let us make this memorable." });
            add("Personality.Serpent.BowDraw", new[] { "Hold still. Or do not. Either way is funny." });
            add("Personality.Serpent.TravelStart", new[] { "A new road, a new collection of mistakes waiting to happen." });
            add("Personality.Serpent.TravelEnd", new[] { "Here we are. Let us ruin the local peace." });
            add("Personality.Serpent.Poisoned", new[] { "Someone tried poison. I almost respect the effort." });
            add("Personality.Serpent.Diseased", new[] { "How undignified. I prefer suffering with an audience." });
            add("Personality.Steed.General", new[] { "There is always another road worth taking.", "If nobody has a good story about the place, I suppose I will have to make one.", "The map gets interesting where the certainty ends." });
            add("Personality.Steed.CombatStart", new[] { "I was hoping for a quieter tour." });
            add("Personality.Steed.CombatEnd", new[] { "Still worth the trip." });
            add("Personality.Steed.DungeonEnter", new[] { "Now this is why I left the road.", "Old door, bad air, terrible idea. Naturally I am going in." });
            add("Personality.Steed.TownEnter", new[] { "New town. First find the landmarks, then find the trouble." });
            add("Personality.Steed.TavernEnter", new[] { "Every good journey deserves one bad tavern story." });
            add("Personality.Steed.TempleEnter", new[] { "You learn a lot about a place from what its people build for the gods." });
            add("Personality.Steed.GuildHallEnter", new[] { "Local experts. Useful when the map stops helping." });
            add("Personality.Steed.Wilderness", new[] { "This is the part no court map ever gets quite right." });
            add("Personality.Steed.QuestAccepted", new[] { "Sounds like a journey. I am in." });
            add("Personality.Steed.QuestResolved", new[] { "That one will be worth telling later." });
            add("Personality.Steed.HeavyDamage", new[] { "I have had smoother expeditions." });
            add("Personality.Steed.LowHealth", new[] { "Adventure stops being charming when you bleed this much." });
            add("Personality.Steed.LowFatigue", new[] { "I need a rest before the next hill becomes the one that stops me." });
            add("Personality.Steed.LowMagicka", new[] { "Low on magicka. Good thing curiosity weighs nothing." });
            add("Personality.Steed.Hungry", new[] { "I have crossed enough country to earn a meal." });
            add("Personality.Steed.Thirsty", new[] { "Find water. Then find the next road." });
            add("Personality.Steed.Cold", new[] { "I have seen better climates and worse decisions." });
            add("Personality.Steed.Nightfall", new[] { "The road looks different after dark. Usually more interesting." });
            add("Personality.Steed.MagicCast", new[] { "Let us see if this gets us through." });
            add("Personality.Steed.BowDraw", new[] { "Distance, wind, and one chance." });
            add("Personality.Steed.TravelStart", new[] { "Now we are getting somewhere." });
            add("Personality.Steed.TravelEnd", new[] { "Worth the miles. Now what did we come all this way to find?" });
            add("Personality.Steed.Poisoned", new[] { "Of course the interesting route had poison." });
            add("Personality.Steed.Diseased", new[] { "I would rather not bring this home as a souvenir." });
            add("Personality.Tower.General", new[] { "The rarest things usually belong to people who cannot appreciate them.", "A locked door is a list of possessions waiting to change hands.", "History is much easier to value when you can hold it." });
            add("Personality.Tower.CombatStart", new[] { "You are standing between me and something I intend to possess." });
            add("Personality.Tower.CombatEnd", new[] { "Good. Now back to what actually matters." });
            add("Personality.Tower.DungeonEnter", new[] { "Someone went to considerable trouble to keep people out. Promising." });
            add("Personality.Tower.TownEnter", new[] { "Every old town has collectors, smugglers, and someone desperate to sell an heirloom." });
            add("Personality.Tower.TavernEnter", new[] { "Collectors talk when they drink. So do thieves who robbed them." });
            add("Personality.Tower.TempleEnter", new[] { "Temples accumulate relics with extraordinary consistency." });
            add("Personality.Tower.GuildHallEnter", new[] { "Guild archives often contain maps they have no intention of sharing." });
            add("Personality.Tower.Wilderness", new[] { "Civilization ends. Buried things do not." });
            add("Personality.Tower.QuestAccepted", new[] { "If the task crosses something rare, I want first look." });
            add("Personality.Tower.QuestResolved", new[] { "The job is finished. I hope the spoils were not disappointing." });
            add("Personality.Tower.HeavyDamage", new[] { "Careful. I have not collected enough to die yet." });
            add("Personality.Tower.LowHealth", new[] { "No artifact is worth bleeding out before I can take it home." });
            add("Personality.Tower.LowFatigue", new[] { "Treasure is inconveniently heavy after the tenth mile." });
            add("Personality.Tower.LowMagicka", new[] { "No magicka. Then I will use keys, tools, or other people's mistakes." });
            add("Personality.Tower.Hungry", new[] { "I can admire relics after I eat." });
            add("Personality.Tower.Thirsty", new[] { "Water first. Dusty tombs are overrated on an empty throat." });
            add("Personality.Tower.Cold", new[] { "Frozen ruins preserve valuables nicely. Less so fingers." });
            add("Personality.Tower.Nightfall", new[] { "Darkness hides entrances. It also hides collectors." });
            add("Personality.Tower.MagicCast", new[] { "Open." });
            add("Personality.Tower.BowDraw", new[] { "I would rather not damage anything valuable behind you." });
            add("Personality.Tower.TravelStart", new[] { "There had better be something worth bringing back." });
            add("Personality.Tower.TravelEnd", new[] { "At last. Now show me what this place has been hiding." });
            add("Personality.Tower.Poisoned", new[] { "Ancient defenses or modern jealousy? Either way, irritating." });
            add("Personality.Tower.Diseased", new[] { "I refuse to let a ruin keep me by infecting me." });
            add("Personality.Atronach.General", new[] { "Do the job cleanly and move on.", "Preparation turns danger into work.", "Competence is quieter than bravado." });
            add("Personality.Atronach.CombatStart", new[] { "All right. Professional terms are over." });
            add("Personality.Atronach.CombatEnd", new[] { "Job done." });
            add("Personality.Atronach.DungeonEnter", new[] { "Check the exits, check the floor, then worry about the monsters." });
            add("Personality.Atronach.TownEnter", new[] { "Find supplies, information, and whoever is paying." });
            add("Personality.Atronach.TavernEnter", new[] { "Food, information, and perhaps a contract. Efficient." });
            add("Personality.Atronach.TempleEnter", new[] { "If they heal wounds and answer questions, I do not need to share the theology." });
            add("Personality.Atronach.GuildHallEnter", new[] { "Professionals usually keep records. Useful." });
            add("Personality.Atronach.Wilderness", new[] { "Travel light, keep bearings, and do not waste daylight." });
            add("Personality.Atronach.QuestAccepted", new[] { "Understood. I will handle it." });
            add("Personality.Atronach.QuestResolved", new[] { "Contract complete." });
            add("Personality.Atronach.HeavyDamage", new[] { "Noted. Adjust." });
            add("Personality.Atronach.LowHealth", new[] { "Too injured to work properly. Fix that first." });
            add("Personality.Atronach.LowFatigue", new[] { "Fatigue ruins technique. Time to rest." });
            add("Personality.Atronach.LowMagicka", new[] { "Reserve nearly gone. Change methods." });
            add("Personality.Atronach.Hungry", new[] { "Eat before hunger becomes a tactical problem." });
            add("Personality.Atronach.Thirsty", new[] { "Water now. Delays become mistakes." });
            add("Personality.Atronach.Cold", new[] { "Cold compromises grip and judgment. Find shelter." });
            add("Personality.Atronach.Nightfall", new[] { "Visibility down. Risk up." });
            add("Personality.Atronach.MagicCast", new[] { "Controlled release." });
            add("Personality.Atronach.BowDraw", new[] { "One shot. No waste." });
            add("Personality.Atronach.TravelStart", new[] { "Route set. Move." });
            add("Personality.Atronach.TravelEnd", new[] { "We are on site. Start assessing." });
            add("Personality.Atronach.Poisoned", new[] { "Identify it, treat it, continue." });
            add("Personality.Atronach.Diseased", new[] { "Symptoms are work conditions. Find a cure." });
            add("Personality.Shadow.General", new[] { "Someone always knows more than they admit.", "Watch the hands, then the exits.", "If the trail looks easy, someone may want it followed." });
            add("Personality.Shadow.CombatStart", new[] { "There you are." });
            add("Personality.Shadow.CombatEnd", new[] { "One less set of tracks to worry about." });
            add("Personality.Shadow.DungeonEnter", new[] { "Too many blind corners. Somebody built this for ambushes whether they meant to or not." });
            add("Personality.Shadow.TownEnter", new[] { "New town. Find the guards, the gates, and who watches strangers." });
            add("Personality.Shadow.TavernEnter", new[] { "Bounties start in places like this. So do traps." });
            add("Personality.Shadow.TempleEnter", new[] { "Even honest people lie when sanctuary is involved." });
            add("Personality.Shadow.GuildHallEnter", new[] { "A guild keeps lists. Lists mean names. Names mean leads." });
            add("Personality.Shadow.Wilderness", new[] { "Open ground shows tracks. It also shows mine." });
            add("Personality.Shadow.QuestAccepted", new[] { "Give me the facts. I will find the rest." });
            add("Personality.Shadow.QuestResolved", new[] { "Trail ends here. For now." });
            add("Personality.Shadow.HeavyDamage", new[] { "They knew where to hit. Remember that." });
            add("Personality.Shadow.LowHealth", new[] { "Wounded means predictable. I need to stop being predictable." });
            add("Personality.Shadow.LowFatigue", new[] { "Tired hunters miss signs. Rest." });
            add("Personality.Shadow.LowMagicka", new[] { "If they are counting on my magic, let them." });
            add("Personality.Shadow.Hungry", new[] { "Hunger makes noise. Eat before it gives me away." });
            add("Personality.Shadow.Thirsty", new[] { "Dry mouth, slower judgment. Fix it." });
            add("Personality.Shadow.Cold", new[] { "Cold hides tracks and slows hands. Bad combination." });
            add("Personality.Shadow.Nightfall", new[] { "Night is when both hunter and quarry start inventing ghosts." });
            add("Personality.Shadow.MagicCast", new[] { "Let them wonder where that came from." });
            add("Personality.Shadow.BowDraw", new[] { "Do not give them a second warning." });
            add("Personality.Shadow.TravelStart", new[] { "Check the trail behind us as often as the one ahead." });
            add("Personality.Shadow.TravelEnd", new[] { "We arrived. Now find out who noticed." });
            add("Personality.Shadow.Poisoned", new[] { "Poison means planning. Someone wanted distance from the kill." });
            add("Personality.Shadow.Diseased", new[] { "Could be bad luck. I do not rely on that explanation." });
            add("Personality.Mage.Location.Daggerfall.General", new[] { "Daggerfall has scholars enough to record a king's death, but not enough certainty to quiet his ghost." });
            add("Personality.Ritual.Location.Daggerfall.General", new[] { "A murdered king and a city full of frightened stories. Death has fertile ground here." });
            add("Personality.Lady.Location.Daggerfall.General", new[] { "Daggerfall's court is grieving, proud, and watched. Every word will travel." });
            add("Personality.Lord.Location.Daggerfall.General", new[] { "A throne unsettled by one dead king is a throne that was never secure." });
            add("Personality.Warrior.Location.Daggerfall.General", new[] { "Lysandus was a warrior-king. Whatever haunts this city deserves a straight answer." });
            add("Personality.Thief.Location.Daggerfall.General", new[] { "A royal city in mourning means distracted guards and nervous nobles." });
            add("Personality.Lover.Location.Daggerfall.General", new[] { "Too many people here still speak of Lysandus like the grief happened yesterday." });
            add("Personality.Serpent.Location.Daggerfall.General", new[] { "A dead king screaming through the streets. Daggerfall knows how to entertain." });
            add("Personality.Steed.Location.Daggerfall.General", new[] { "Daggerfall at last. Big walls, old harbor, and enough rumors to fill the road back out." });
            add("Personality.Tower.Location.Daggerfall.General", new[] { "Royal cities collect royal relics. Grief makes people careless with inventories." });
            add("Personality.Atronach.Location.Daggerfall.General", new[] { "Daggerfall. Keep the job separate from the court drama until the court drama becomes the job." });
            add("Personality.Shadow.Location.Daggerfall.General", new[] { "A dead king, an uneasy court, and too many people insisting they know nothing. Someone does." });
            add("Personality.Mage.Location.Wayrest.General", new[] { "Wayrest grew wealthy by learning how to make trade serve ambition. That kind of lesson travels." });
            add("Personality.Ritual.Location.Wayrest.General", new[] { "Wayrest polishes everything, even its secrets. Especially its secrets." });
            add("Personality.Lady.Location.Wayrest.General", new[] { "Wayrest is a court where commerce and etiquette share the same table." });
            add("Personality.Lord.Location.Wayrest.General", new[] { "Wayrest understands that wealth is only impressive when it can command obedience." });
            add("Personality.Warrior.Location.Wayrest.General", new[] { "Wayrest looks comfortable. Comfortable courts can still breed dangerous orders." });
            add("Personality.Thief.Location.Wayrest.General", new[] { "Wayrest has money enough to make caution a profession." });
            add("Personality.Lover.Location.Wayrest.General", new[] { "For all the courtly games, ordinary people still have to live beneath them." });
            add("Personality.Serpent.Location.Wayrest.General", new[] { "Everyone here smiles like there is a knife behind the teeth." });
            add("Personality.Steed.Location.Wayrest.General", new[] { "Wayrest sits where roads and ambitions meet. No wonder everyone passes through." });
            add("Personality.Tower.Location.Wayrest.General", new[] { "A rich court accumulates rare things simply to prove it can." });
            add("Personality.Atronach.Location.Wayrest.General", new[] { "Wayrest pays well when it wants something handled quietly." });
            add("Personality.Shadow.Location.Wayrest.General", new[] { "Too much wealth, too many factions. Perfect conditions for someone to disappear on purpose." });
            add("Personality.Mage.Location.Sentinel.General", new[] { "Sentinel preserves old traditions with more discipline than many Breton courts preserve records." });
            add("Personality.Ritual.Location.Sentinel.General", new[] { "Old customs often hide old prohibitions. Those are usually worth studying." });
            add("Personality.Lady.Location.Sentinel.General", new[] { "Sentinel remembers courtesy and insult with equal precision." });
            add("Personality.Lord.Location.Sentinel.General", new[] { "Sentinel respects strength, lineage, and ceremony. At least they understand the language of rule." });
            add("Personality.Warrior.Location.Sentinel.General", new[] { "Sentinel respects a person who stands by their word. Good." });
            add("Personality.Thief.Location.Sentinel.General", new[] { "Honor makes people predictable. Predictability is useful." });
            add("Personality.Lover.Location.Sentinel.General", new[] { "Pride can be harsh, but it also means people care deeply about what they inherit." });
            add("Personality.Serpent.Location.Sentinel.General", new[] { "Everyone here takes honor so seriously. That makes offense wonderfully easy." });
            add("Personality.Steed.Location.Sentinel.General", new[] { "Sentinel feels different from the Breton kingdoms the moment the road turns dry." });
            add("Personality.Tower.Location.Sentinel.General", new[] { "Hammerfell has relics older than half the claims made about them. I would like to see the originals." });
            add("Personality.Atronach.Location.Sentinel.General", new[] { "Sentinel values competence and clear terms. I can work with that." });
            add("Personality.Shadow.Location.Sentinel.General", new[] { "In a place ruled by honor, accusations become weapons. Watch who points first." });
            add("Personality.Mage.Location.PrivateersHold.General", new[] { "Privateer's Hold is hardly ancient scholarship, but even crude places reveal how their occupants expected trouble." });
            add("Personality.Ritual.Location.PrivateersHold.General", new[] { "The first hole I crawled from in the Bay. Every beginning deserves a little blood." });
            add("Personality.Lady.Location.PrivateersHold.General", new[] { "An unpleasant reminder that survival comes before ceremony." });
            add("Personality.Lord.Location.PrivateersHold.General", new[] { "Privateer's Hold. A fitting place for the world to learn it failed to kill me." });
            add("Personality.Warrior.Location.PrivateersHold.General", new[] { "I survived this place once. I know better than to underestimate it now." });
            add("Personality.Thief.Location.PrivateersHold.General", new[] { "Smugglers and privateers rarely build empty rooms without a reason." });
            add("Personality.Lover.Location.PrivateersHold.General", new[] { "I wonder who else woke down here without knowing whether they would see daylight again." });
            add("Personality.Serpent.Location.PrivateersHold.General", new[] { "Ah, the welcoming pit. I almost missed its hospitality." });
            add("Personality.Steed.Location.PrivateersHold.General", new[] { "Privateer's Hold was a miserable first stop. Still counts as a story." });
            add("Personality.Tower.Location.PrivateersHold.General", new[] { "Privateers hide things. The only question is whether anyone found all of them." });
            add("Personality.Atronach.Location.PrivateersHold.General", new[] { "Known ground. Known threats. That is almost relaxing." });
            add("Personality.Shadow.Location.PrivateersHold.General", new[] { "I woke here with too few answers. I still do not like that." });
            add("Personality.Mage.Location.DirenniTower.General", new[] { "Direnni stone carries the memory of an age when High Rock's magical traditions were still being shaped." });
            add("Personality.Ritual.Location.DirenniTower.General", new[] { "Old Aldmeri towers are excellent places to find magic someone later decided was improper." });
            add("Personality.Lady.Location.DirenniTower.General", new[] { "The Direnni ruled through blood, alliance, and culture long after raw conquest would have failed." });
            add("Personality.Lord.Location.DirenniTower.General", new[] { "The Direnni understood that lasting power changes a land even after the rulers are gone." });
            add("Personality.Warrior.Location.DirenniTower.General", new[] { "Old tower, narrow approaches. Whoever held this place understood defense." });
            add("Personality.Thief.Location.DirenniTower.General", new[] { "Ancient towers are generous to anyone willing to notice what later occupants overlooked." });
            add("Personality.Lover.Location.DirenniTower.General", new[] { "So many generations lived under the shadow of places like this." });
            add("Personality.Serpent.Location.DirenniTower.General", new[] { "Old elves, old magic, old grudges. Finally, some atmosphere." });
            add("Personality.Steed.Location.DirenniTower.General", new[] { "You can travel the Bay for weeks and still find Direnni stone waiting on the horizon." });
            add("Personality.Tower.Location.DirenniTower.General", new[] { "Direnni Tower. If anything here survived untouched, I want to know why." });
            add("Personality.Atronach.Location.DirenniTower.General", new[] { "Old magic means old hazards. Treat both as functional until proven otherwise." });
            add("Personality.Shadow.Location.DirenniTower.General", new[] { "Towers are built to see threats coming. I wonder what this one failed to see." });
            add("Personality.Mage.Location.ScourgBarrow.General", new[] { "Barrows preserve more than bodies. They preserve the assumptions people made about death." });
            add("Personality.Ritual.Location.ScourgBarrow.General", new[] { "Scourg Barrow. At last, a place with the courage to admit what it is about." });
            add("Personality.Lady.Location.ScourgBarrow.General", new[] { "Burial places deserve respect even when the dead have become dangerous." });
            add("Personality.Lord.Location.ScourgBarrow.General", new[] { "The dead cling to old halls because they cannot accept that their authority ended." });
            add("Personality.Warrior.Location.ScourgBarrow.General", new[] { "Barrows mean undead often enough. Keep the weapon ready." });
            add("Personality.Thief.Location.ScourgBarrow.General", new[] { "Grave goods are valuable. Grave curses are expensive." });
            add("Personality.Lover.Location.ScourgBarrow.General", new[] { "Whatever is restless here was once someone mourned by the living." });
            add("Personality.Serpent.Location.ScourgBarrow.General", new[] { "A barrow full of the dead. I do appreciate a crowd that cannot complain." });
            add("Personality.Steed.Location.ScourgBarrow.General", new[] { "Scourg Barrow. Not every landmark needs to be cheerful to be worth seeing." });
            add("Personality.Tower.Location.ScourgBarrow.General", new[] { "If they buried someone important here, they buried important things with them." });
            add("Personality.Atronach.Location.ScourgBarrow.General", new[] { "Undead, tight corridors, poor light. Familiar professional misery." });
            add("Personality.Shadow.Location.ScourgBarrow.General", new[] { "Barrows attract treasure hunters. Treasure hunters leave tracks, traps, and bodies." });
            add("Personality.Mage.Location.Shedungent.General", new[] { "Shedungent has the feel of a place where knowledge was preserved for reasons no one wanted public." });
            add("Personality.Ritual.Location.Shedungent.General", new[] { "Shedungent. Even the name sounds like someone whispering over a forbidden page." });
            add("Personality.Lady.Location.Shedungent.General", new[] { "Places with reputations like this often survive because respectable people prefer not to ask questions." });
            add("Personality.Lord.Location.Shedungent.General", new[] { "Fear has kept this place influential without a single proclamation." });
            add("Personality.Warrior.Location.Shedungent.General", new[] { "Bad reputation, worse ground. Stay ready." });
            add("Personality.Thief.Location.Shedungent.General", new[] { "When sensible people avoid a place, fewer people compete for what is inside." });
            add("Personality.Lover.Location.Shedungent.General", new[] { "I doubt everyone who vanished into a place like this deserved what they found." });
            add("Personality.Serpent.Location.Shedungent.General", new[] { "Shedungent. A dreadful name for what I hope is a dreadful place." });
            add("Personality.Steed.Location.Shedungent.General", new[] { "Some places pull travelers in simply because everyone else says not to go." });
            add("Personality.Tower.Location.Shedungent.General", new[] { "Avoided ruins are often the least picked over." });
            add("Personality.Atronach.Location.Shedungent.General", new[] { "Unknown hazards, bad reputation. Approach like a contract with missing pages." });
            add("Personality.Shadow.Location.Shedungent.General", new[] { "If everyone agrees a place is dangerous, ask who benefits from keeping people away." });
            add("Personality.Mage.Location.Orsinium.General", new[] { "Orsinium is proof that culture survives even when neighbors insist on describing it as a problem." });
            add("Personality.Ritual.Location.Orsinium.General", new[] { "Outsiders call Orcish customs crude when what they usually mean is unfamiliar." });
            add("Personality.Lady.Location.Orsinium.General", new[] { "Orsinium has spent generations demanding recognition from courts that prefer condescension." });
            add("Personality.Lord.Location.Orsinium.General", new[] { "A people who build a kingdom despite every neighbor's objection understand will better than most." });
            add("Personality.Warrior.Location.Orsinium.General", new[] { "Orcs respect strength plainly. I prefer that to a court that hides the contest." });
            add("Personality.Thief.Location.Orsinium.General", new[] { "A city used to hostile outsiders will not make easy work for thieves." });
            add("Personality.Lover.Location.Orsinium.General", new[] { "People here have heard enough strangers tell them what they are. Better to listen first." });
            add("Personality.Serpent.Location.Orsinium.General", new[] { "Everyone expects me to be afraid of Orcs. How disappointing for everyone." });
            add("Personality.Steed.Location.Orsinium.General", new[] { "Orsinium is exactly the kind of place maps make seem farther away than it feels once you arrive." });
            add("Personality.Tower.Location.Orsinium.General", new[] { "A kingdom forced to rebuild learns to keep valuable things close." });
            add("Personality.Atronach.Location.Orsinium.General", new[] { "Orsinium respects capability. Good. That shortens negotiations." });
            add("Personality.Shadow.Location.Orsinium.General", new[] { "A city surrounded by enemies learns to watch the watchers before trusting ceremony." });
            add("Personality.Mage.Race.HighElf.General", new[] { "Aldmeri discipline taught my people that knowledge without control is merely danger." });
            add("Personality.Ritual.Race.HighElf.General", new[] { "My people have catalogued enough magic to know exactly which shelves everyone pretends not to visit." });
            add("Personality.Lady.Race.HighElf.General", new[] { "Refinement is useful when it teaches restraint rather than superiority." });
            add("Personality.Lord.Race.HighElf.General", new[] { "There are standards of rule older than these Breton crowns." });
            add("Personality.Warrior.Race.HighElf.General", new[] { "Discipline is not softness. Anyone who doubts that may test it." });
            add("Personality.Thief.Race.HighElf.General", new[] { "People guard Altmeri work carefully. Usually because it is worth taking." });
            add("Personality.Lover.Race.HighElf.General", new[] { "Pride in one's people means little if it becomes contempt for everyone else." });
            add("Personality.Serpent.Race.HighElf.General", new[] { "Provincials are so easy to offend. I barely have to try." });
            add("Personality.Steed.Race.HighElf.General", new[] { "Summerset teaches beauty. The mainland teaches variety." });
            add("Personality.Tower.Race.HighElf.General", new[] { "Aldmeri relics deserve better custodians than whoever happens to own them now." });
            add("Personality.Atronach.Race.HighElf.General", new[] { "Training matters more than boasting about ancestry." });
            add("Personality.Shadow.Race.HighElf.General", new[] { "People expect an Altmer to look down on them. Expectations make convenient camouflage." });
            add("Personality.Mage.Race.Khajiit.General", new[] { "This one has learned that curiosity travels better than certainty." });
            add("Personality.Ritual.Race.Khajiit.General", new[] { "The moons cast light. They also cast shadows worth studying." });
            add("Personality.Lady.Race.Khajiit.General", new[] { "This one finds courtesy cheaper than claws and often more effective." });
            add("Personality.Lord.Race.Khajiit.General", new[] { "Khajiit has bowed often enough to know when the gesture means nothing." });
            add("Personality.Warrior.Race.Khajiit.General", new[] { "This one keeps claws sheathed until courage requires otherwise." });
            add("Personality.Thief.Race.Khajiit.General", new[] { "This one notices unattended things. It would be rude not to." });
            add("Personality.Lover.Race.Khajiit.General", new[] { "Khajiit knows a warm fire is better when someone else is welcome beside it." });
            add("Personality.Serpent.Race.Khajiit.General", new[] { "This one has claws and a sense of humor. Dangerous combination." });
            add("Personality.Steed.Race.Khajiit.General", new[] { "The moons see many roads. This one intends to see a few of them too." });
            add("Personality.Tower.Race.Khajiit.General", new[] { "Khajiit appreciates beautiful things, especially when locked badly." });
            add("Personality.Atronach.Race.Khajiit.General", new[] { "This one gets paid, gets home, and keeps the fur mostly intact." });
            add("Personality.Shadow.Race.Khajiit.General", new[] { "Khajiit hears footsteps others mistake for wind." });
            add("Personality.Mage.Race.Argonian.General", new[] { "The Hist teaches patience differently than any western academy." });
            add("Personality.Ritual.Race.Argonian.General", new[] { "There are old things in Black Marsh that civilized scholars would call impossible until they needed a name for them." });
            add("Personality.Lady.Race.Argonian.General", new[] { "Custom changes from shore to swamp. Respect should travel farther than either." });
            add("Personality.Lord.Race.Argonian.General", new[] { "Those who mistake adaptation for submission misunderstand both." });
            add("Personality.Warrior.Race.Argonian.General", new[] { "I bend when I must. I do not break for convenience." });
            add("Personality.Thief.Race.Argonian.General", new[] { "Locks rust. Habits do not. Habits are easier to exploit." });
            add("Personality.Lover.Race.Argonian.General", new[] { "Every people has ways of belonging that outsiders fail to see." });
            add("Personality.Serpent.Race.Argonian.General", new[] { "Warm blood, cold blood. Everyone screams warmly enough." });
            add("Personality.Steed.Race.Argonian.General", new[] { "Dry roads still feel strange. That makes them worth walking." });
            add("Personality.Tower.Race.Argonian.General", new[] { "Swamps swallow ruins as eagerly as deserts bury them. Both keep treasures." });
            add("Personality.Atronach.Race.Argonian.General", new[] { "Adapt to the ground. Finish the work." });
            add("Personality.Shadow.Race.Argonian.General", new[] { "Still water hides movement. So do quiet people." });
            add("Personality.Mage.Race.Nord.General", new[] { "Nords remember more history in songs than some scholars manage in libraries." });
            add("Personality.Ritual.Race.Nord.General", new[] { "Old Nordic stories know the dead are never as distant as priests prefer." });
            add("Personality.Lady.Race.Nord.General", new[] { "Blunt manners can still hide generous hearts." });
            add("Personality.Lord.Race.Nord.General", new[] { "A strong hall needs one voice when the storm comes." });
            add("Personality.Warrior.Race.Nord.General", new[] { "Stand firm, keep your word, and let steel settle what words cannot." });
            add("Personality.Thief.Race.Nord.General", new[] { "Nords watch the front door. Clever people remember the back." });
            add("Personality.Lover.Race.Nord.General", new[] { "A hall is only a hall until someone shares the fire." });
            add("Personality.Serpent.Race.Nord.General", new[] { "Nords love a good fight. I love when they discover I do too." });
            add("Personality.Steed.Race.Nord.General", new[] { "After Skyrim roads, the Bay feels almost polite." });
            add("Personality.Tower.Race.Nord.General", new[] { "Old barrows taught me that the dead often keep excellent possessions." });
            add("Personality.Atronach.Race.Nord.General", new[] { "Cold, monsters, bad odds. Familiar working conditions." });
            add("Personality.Shadow.Race.Nord.General", new[] { "Snow teaches tracking. Crowds are harder, not impossible." });
            add("Personality.Mage.Race.DarkElf.General", new[] { "Dunmeri tradition has never separated learning from consequence." });
            add("Personality.Ritual.Race.DarkElf.General", new[] { "My people understand that ancestors, spirits, and forbidden things are rarely tidy categories." });
            add("Personality.Lady.Race.DarkElf.General", new[] { "Great Houses taught us long ago that manners can be weapons without becoming lies." });
            add("Personality.Lord.Race.DarkElf.General", new[] { "Power that survives betrayal deserves respect. Power that fails it deserves replacement." });
            add("Personality.Warrior.Race.DarkElf.General", new[] { "Honor is not gentleness. It is knowing which debts must be paid." });
            add("Personality.Thief.Race.DarkElf.General", new[] { "House politics teach a person to notice what is missing from a room." });
            add("Personality.Lover.Race.DarkElf.General", new[] { "Kinship can be difficult and still matter more than outsiders understand." });
            add("Personality.Serpent.Race.DarkElf.General", new[] { "Westerners are very expressive when frightened. Refreshing." });
            add("Personality.Steed.Race.DarkElf.General", new[] { "The west has softer roads and stranger customs. I appreciate both more than expected." });
            add("Personality.Tower.Race.DarkElf.General", new[] { "Old Dunmeri tombs taught me respect for relics. Respect and possession are separate questions." });
            add("Personality.Atronach.Race.DarkElf.General", new[] { "I have seen harsher lands produce harder work." });
            add("Personality.Shadow.Race.DarkElf.General", new[] { "Great Houses teach suspicion early. The Bay merely provides new accents for it." });
            add("Personality.Mage.Race.Redguard.General", new[] { "Yokudan memory survives because people carried more than swords across the sea." });
            add("Personality.Ritual.Race.Redguard.General", new[] { "Every proud tradition has rites it discusses quietly." });
            add("Personality.Lady.Race.Redguard.General", new[] { "Honor is a memory shared by the whole court. Abuse it and everyone remembers." });
            add("Personality.Lord.Race.Redguard.General", new[] { "A ruler who cannot command respect has only furniture and guards." });
            add("Personality.Warrior.Race.Redguard.General", new[] { "Skill deserves discipline. Courage deserves purpose." });
            add("Personality.Thief.Race.Redguard.General", new[] { "A culture that respects skill makes getting caught especially embarrassing." });
            add("Personality.Lover.Race.Redguard.General", new[] { "Hospitality and honor matter because people remember how strangers were treated." });
            add("Personality.Serpent.Race.Redguard.General", new[] { "Everyone speaks of honor until fear makes them inventive." });
            add("Personality.Steed.Race.Redguard.General", new[] { "Hammerfell roads teach you to respect distance before you boast about crossing it." });
            add("Personality.Tower.Race.Redguard.General", new[] { "Yokudan relics crossed oceans. Imagine what never left the old tombs." });
            add("Personality.Atronach.Race.Redguard.General", new[] { "Technique first. Pride after the work is done." });
            add("Personality.Shadow.Race.Redguard.General", new[] { "A duel is honest. A bounty rarely is." });
            add("Personality.Mage.Race.WoodElf.General", new[] { "A forest records history differently, but not less faithfully." });
            add("Personality.Ritual.Race.WoodElf.General", new[] { "Old groves keep bargains city mages would rather call superstition." });
            add("Personality.Lady.Race.WoodElf.General", new[] { "Custom is easiest to respect when one stops assuming one's own is universal." });
            add("Personality.Lord.Race.WoodElf.General", new[] { "Freedom without order becomes someone else's opportunity to rule you." });
            add("Personality.Warrior.Race.WoodElf.General", new[] { "Move lightly, strike only when needed, and leave the path usable behind you." });
            add("Personality.Thief.Race.WoodElf.General", new[] { "Cities have too many doors. Forests taught me to stop needing them." });
            add("Personality.Lover.Race.WoodElf.General", new[] { "Living places deserve the same care as living people." });
            add("Personality.Serpent.Race.WoodElf.General", new[] { "Bosmer smiles make people comfortable. That can be useful." });
            add("Personality.Steed.Race.WoodElf.General", new[] { "Roads are suggestions. The interesting places are usually beside them." });
            add("Personality.Tower.Race.WoodElf.General", new[] { "Ruins swallowed by green are harder to find and therefore more satisfying." });
            add("Personality.Atronach.Race.WoodElf.General", new[] { "Read the terrain, finish the job, leave no unnecessary trail." });
            add("Personality.Shadow.Race.WoodElf.General", new[] { "Tracking in streets is only tracking with more liars underfoot." });
            add("Personality.Mage.Race.Breton.General", new[] { "High Rock makes scholars practical. Magic here has always had to survive politics." });
            add("Personality.Ritual.Race.Breton.General", new[] { "Breton families collect secrets almost as enthusiastically as they collect titles." });
            add("Personality.Lady.Race.Breton.General", new[] { "In High Rock, knowing whose cousin offended whose duke is practically a survival skill." });
            add("Personality.Lord.Race.Breton.General", new[] { "Too many petty crowns teach a person how little a title proves." });
            add("Personality.Warrior.Race.Breton.General", new[] { "Knights, mercenaries, and feuds. High Rock gives courage plenty of work." });
            add("Personality.Thief.Race.Breton.General", new[] { "A province full of castles is a province full of people convinced locks solve problems." });
            add("Personality.Lover.Race.Breton.General", new[] { "High Rock quarrels constantly and still finds reasons to share a table afterward." });
            add("Personality.Serpent.Race.Breton.General", new[] { "Bretons can turn one insult into three generations of entertainment." });
            add("Personality.Steed.Race.Breton.General", new[] { "High Rock packs more borders into a week's ride than some lands manage in a lifetime." });
            add("Personality.Tower.Race.Breton.General", new[] { "Every Breton lord wants an old relic proving the family is older than the neighbor's." });
            add("Personality.Atronach.Race.Breton.General", new[] { "High Rock always has work for someone who can survive other people's feuds." });
            add("Personality.Shadow.Race.Breton.General", new[] { "A Breton court is a bounty board written in genealogy." });
            add("Personality.Mage.Guild.MagesGuild.General", new[] { "The Mages Guild is useful when scholarship survives its ledgers and rules." });
            add("Personality.Ritual.Guild.MagesGuild.General", new[] { "The Guild labels certain arts dangerous. I hear an invitation." });
            add("Personality.Lady.Guild.MagesGuild.General", new[] { "The Guild's influence reaches courts that pretend magic is merely academic." });
            add("Personality.Lord.Guild.MagesGuild.General", new[] { "The Guild has power and wastes too much of it on councils." });
            add("Personality.Warrior.Guild.MagesGuild.General", new[] { "Mages make good allies when they remember someone still has to stand in the doorway." });
            add("Personality.Thief.Guild.MagesGuild.General", new[] { "Magic locks are still locks, only more expensive when they go wrong." });
            add("Personality.Lover.Guild.MagesGuild.General", new[] { "Knowledge should help people, not just impress the people who already have it." });
            add("Personality.Serpent.Guild.MagesGuild.General", new[] { "A hall full of volatile egos and open flame. Wonderful." });
            add("Personality.Steed.Guild.MagesGuild.General", new[] { "Guild mages always know about ruins nobody sensible visits." });
            add("Personality.Tower.Guild.MagesGuild.General", new[] { "The Guild catalogs artifacts. Catalogs are maps to other people's treasures." });
            add("Personality.Atronach.Guild.MagesGuild.General", new[] { "The Guild can identify threats faster than guessing can." });
            add("Personality.Shadow.Guild.MagesGuild.General", new[] { "Mages keep records, rivalries, and grudges. All three leave trails." });
            add("Personality.Mage.Guild.FightersGuild.General", new[] { "The Fighters Guild has accumulated practical knowledge scholars often dismiss until swords are drawn." });
            add("Personality.Ritual.Guild.FightersGuild.General", new[] { "Mercenaries see strange deaths and ask wonderfully few theological questions." });
            add("Personality.Lady.Guild.FightersGuild.General", new[] { "A guild built on contracts lives or dies by reputation." });
            add("Personality.Lord.Guild.FightersGuild.General", new[] { "Paid strength is still strength, provided the hand holding the purse is mine." });
            add("Personality.Warrior.Guild.FightersGuild.General", new[] { "A good company values discipline before boasting." });
            add("Personality.Thief.Guild.FightersGuild.General", new[] { "Fighters watch for frontal threats. Convenient habit." });
            add("Personality.Lover.Guild.FightersGuild.General", new[] { "At its best, the Guild gives dangerous work to people trained to survive it." });
            add("Personality.Serpent.Guild.FightersGuild.General", new[] { "People who solve problems with weapons are refreshingly easy to motivate." });
            add("Personality.Steed.Guild.FightersGuild.General", new[] { "Guild contracts are an excellent excuse to see places nobody recommends." });
            add("Personality.Tower.Guild.FightersGuild.General", new[] { "Fighters clear ruins. Collectors arrive afterward." });
            add("Personality.Atronach.Guild.FightersGuild.General", new[] { "Clear terms, defined work, payment. Sensible." });
            add("Personality.Shadow.Guild.FightersGuild.General", new[] { "Contracts create enemies. Enemies create bounties." });
            add("Personality.Mage.Guild.ThievesGuild.General", new[] { "The Thieves Guild learns rules by breaking them. Crude scholarship, useful results." });
            add("Personality.Ritual.Guild.ThievesGuild.General", new[] { "People who trade secrets eventually encounter the kinds nobody should own." });
            add("Personality.Lady.Guild.ThievesGuild.General", new[] { "A hidden institution is still an institution, with loyalties and etiquette of its own." });
            add("Personality.Lord.Guild.ThievesGuild.General", new[] { "Thieves are useful when they remember who grants them room to operate." });
            add("Personality.Warrior.Guild.ThievesGuild.General", new[] { "I do not admire theft, but I respect anyone who keeps faith with their own." });
            add("Personality.Thief.Guild.ThievesGuild.General", new[] { "The Guild understands the first rule: do not make the difficult job fair." });
            add("Personality.Lover.Guild.ThievesGuild.General", new[] { "Some steal from hunger. Some steal from greed. I should remember the difference." });
            add("Personality.Serpent.Guild.ThievesGuild.General", new[] { "A guild devoted to taking what is not theirs. Finally, honesty." });
            add("Personality.Steed.Guild.ThievesGuild.General", new[] { "Smugglers know roads official maps politely forget." });
            add("Personality.Tower.Guild.ThievesGuild.General", new[] { "Fences know where rare objects go after respectable collectors lose them." });
            add("Personality.Atronach.Guild.ThievesGuild.General", new[] { "Specialists are specialists. Use the right one for the job." });
            add("Personality.Shadow.Guild.ThievesGuild.General", new[] { "The Guild hears who vanished, who paid, and who suddenly stopped asking questions." });
            add("Personality.Mage.Guild.DarkBrotherhood.General", new[] { "An institution devoted to death inevitably becomes a library of motives." });
            add("Personality.Ritual.Guild.DarkBrotherhood.General", new[] { "Professional murder is disappointingly mundane until someone makes it ceremonial." });
            add("Personality.Lady.Guild.DarkBrotherhood.General", new[] { "Assassination is politics stripped of the courtesy that usually disguises it." });
            add("Personality.Lord.Guild.DarkBrotherhood.General", new[] { "A hidden blade is useful, but only when the hand directing it cannot be traced." });
            add("Personality.Warrior.Guild.DarkBrotherhood.General", new[] { "There is little honor in murder from hiding, whatever fee was paid." });
            add("Personality.Thief.Guild.DarkBrotherhood.General", new[] { "The Brotherhood takes the same principle farther: never give the target a fair chance." });
            add("Personality.Lover.Guild.DarkBrotherhood.General", new[] { "Every contract is someone's grief waiting to happen." });
            add("Personality.Serpent.Guild.DarkBrotherhood.General", new[] { "Imagine needing payment as an excuse." });
            add("Personality.Steed.Guild.DarkBrotherhood.General", new[] { "Assassins travel farther than most people realize. Death has routes too." });
            add("Personality.Tower.Guild.DarkBrotherhood.General", new[] { "Dead patrons leave estates. Estates leave objects looking for new owners." });
            add("Personality.Atronach.Guild.DarkBrotherhood.General", new[] { "A professional respects capability even when the trade is ugly." });
            add("Personality.Shadow.Guild.DarkBrotherhood.General", new[] { "Bounties and assassinations share one lesson: the target is rarely the whole story." });
            add("Personality.Mage.Reputation.Honored.General", new[] { "Trust is useful. I should not spend it carelessly." });
            add("Personality.Ritual.Reputation.Honored.General", new[] { "Respectable reputations make excellent camouflage." });
            add("Personality.Lady.Reputation.Honored.General", new[] { "Good standing is a responsibility as much as an advantage." });
            add("Personality.Lord.Reputation.Honored.General", new[] { "At last, recognition approaches accuracy." });
            add("Personality.Warrior.Reputation.Honored.General", new[] { "If my name carries weight here, it should stand for something." });
            add("Personality.Thief.Reputation.Honored.General", new[] { "A clean reputation opens doors nobody thinks to lock behind me." });
            add("Personality.Lover.Reputation.Honored.General", new[] { "It is good to know people remember kindness." });
            add("Personality.Serpent.Reputation.Honored.General", new[] { "They trust me. That is almost irresponsible of them." });
            add("Personality.Steed.Reputation.Honored.General", new[] { "Nice to arrive somewhere and not begin as a stranger." });
            add("Personality.Tower.Reputation.Honored.General", new[] { "Collectors show better pieces to people they respect." });
            add("Personality.Atronach.Reputation.Honored.General", new[] { "A solid reputation lowers the cost of doing business." });
            add("Personality.Shadow.Reputation.Honored.General", new[] { "Being trusted makes questions easier to ask and harder to notice." });
            add("Personality.Mage.Reputation.Respected.General", new[] { "My name has enough credibility to make people listen before dismissing the idea." });
            add("Personality.Ritual.Reputation.Respected.General", new[] { "Respect is a key that leaves no scratches on the lock." });
            add("Personality.Lady.Reputation.Respected.General", new[] { "A little goodwill can prevent a great deal of unnecessary force." });
            add("Personality.Lord.Reputation.Respected.General", new[] { "They are beginning to understand." });
            add("Personality.Warrior.Reputation.Respected.General", new[] { "Good. Deeds should speak before boasts do." });
            add("Personality.Thief.Reputation.Respected.General", new[] { "Respectability is useful cover." });
            add("Personality.Lover.Reputation.Respected.General", new[] { "I am glad the people here remember me kindly." });
            add("Personality.Serpent.Reputation.Respected.General", new[] { "They almost like me. How dangerous." });
            add("Personality.Steed.Reputation.Respected.General", new[] { "Familiar faces make a road feel shorter." });
            add("Personality.Tower.Reputation.Respected.General", new[] { "Respect gets a person invited into rooms with better collections." });
            add("Personality.Atronach.Reputation.Respected.General", new[] { "Known competence saves explanations." });
            add("Personality.Shadow.Reputation.Respected.General", new[] { "If they recognize me, I need to know who recognized me first." });
            add("Personality.Mage.Reputation.Disreputable.General", new[] { "They distrust me. I should decide whether correcting them is worth the effort." });
            add("Personality.Ritual.Reputation.Disreputable.General", new[] { "A suspicious reputation keeps the timid at a useful distance." });
            add("Personality.Lady.Reputation.Disreputable.General", new[] { "A damaged name closes doors before I reach them." });
            add("Personality.Lord.Reputation.Disreputable.General", new[] { "Their disapproval would matter more if I valued their judgment." });
            add("Personality.Warrior.Reputation.Disreputable.General", new[] { "If my name is stained here, I should either mend it or stop pretending it is not." });
            add("Personality.Thief.Reputation.Disreputable.General", new[] { "So they have heard of me. Saves introductions." });
            add("Personality.Lover.Reputation.Disreputable.General", new[] { "Being disliked is easier than knowing I may have earned it." });
            add("Personality.Serpent.Reputation.Disreputable.General", new[] { "Disreputable? I was hoping for memorable." });
            add("Personality.Steed.Reputation.Disreputable.General", new[] { "Some roads leave worse stories behind than others." });
            add("Personality.Tower.Reputation.Disreputable.General", new[] { "Collectors become cautious around bad reputations. Their guards become interesting." });
            add("Personality.Atronach.Reputation.Disreputable.General", new[] { "A bad reputation complicates work. Plan around it." });
            add("Personality.Shadow.Reputation.Disreputable.General", new[] { "If they already expect trouble from me, someone can use that expectation." });
            add("Personality.Mage.Reputation.Notorious.General", new[] { "Infamy distorts every fact that comes after it." });
            add("Personality.Ritual.Reputation.Notorious.General", new[] { "Fear is such an efficient introduction." });
            add("Personality.Lady.Reputation.Notorious.General", new[] { "My name enters the room before I do, and not kindly." });
            add("Personality.Lord.Reputation.Notorious.General", new[] { "Fear will serve where respect failed." });
            add("Personality.Warrior.Reputation.Notorious.General", new[] { "This is not the name I wanted my deeds to make." });
            add("Personality.Thief.Reputation.Notorious.General", new[] { "Being notorious is terrible for stealth and excellent for negotiation." });
            add("Personality.Lover.Reputation.Notorious.General", new[] { "People are afraid of me here. That should trouble me." });
            add("Personality.Serpent.Reputation.Notorious.General", new[] { "Now that is a reputation worth keeping polished." });
            add("Personality.Steed.Reputation.Notorious.General", new[] { "Hard to travel unnoticed when the destination already knows your name." });
            add("Personality.Tower.Reputation.Notorious.General", new[] { "Notoriety makes owners hide valuables. Hidden valuables are still valuables." });
            add("Personality.Atronach.Reputation.Notorious.General", new[] { "Everyone knows the name. Work around it." });
            add("Personality.Shadow.Reputation.Notorious.General", new[] { "Notoriety turns every stranger into a possible watcher." });
            add("Personality.Mage.Choice.Accept", new[] { "Very well. I will see what truth lies behind it." });
            add("Personality.Mage.Choice.Reject", new[] { "No. The premise is poor, and I will not pretend otherwise." });
            add("Personality.Mage.Choice.Yes", new[] { "Yes. That follows." });
            add("Personality.Mage.Choice.No", new[] { "No. I am not convinced." });
            add("Personality.Ritual.Choice.Accept", new[] { "Very well. This may prove interesting." });
            add("Personality.Ritual.Choice.Reject", new[] { "No. There is nothing here worth my time." });
            add("Personality.Ritual.Choice.Yes", new[] { "Yes. Continue." });
            add("Personality.Ritual.Choice.No", new[] { "No. Do not mistake curiosity for consent." });
            add("Personality.Lady.Choice.Accept", new[] { "You have my word. I will help." });
            add("Personality.Lady.Choice.Reject", new[] { "I must decline. I hope you find another way." });
            add("Personality.Lady.Choice.Yes", new[] { "Yes. We are agreed." });
            add("Personality.Lady.Choice.No", new[] { "No. I cannot support that." });
            add("Personality.Lord.Choice.Accept", new[] { "Very well. I will take control of the matter." });
            add("Personality.Lord.Choice.Reject", new[] { "No. Find someone more accustomed to taking orders." });
            add("Personality.Lord.Choice.Yes", new[] { "Yes. Proceed." });
            add("Personality.Lord.Choice.No", new[] { "No. The matter is settled." });
            add("Personality.Warrior.Choice.Accept", new[] { "Aye. I will see it done." });
            add("Personality.Warrior.Choice.Reject", new[] { "No. I will not give my word to that." });
            add("Personality.Warrior.Choice.Yes", new[] { "Aye. I will stand by it.", "Yes. Let us get on with it." });
            add("Personality.Warrior.Choice.No", new[] { "No. I will not give my word to that.", "No. Find another sword." });
            add("Personality.Thief.Choice.Accept", new[] { "If the terms hold, we have a deal." });
            add("Personality.Thief.Choice.Reject", new[] { "No. Too much risk for too little advantage." });
            add("Personality.Thief.Choice.Yes", new[] { "Yes. For now." });
            add("Personality.Thief.Choice.No", new[] { "No. Find another fool." });
            add("Personality.Lover.Choice.Accept", new[] { "If someone needs help, I will do what I can." });
            add("Personality.Lover.Choice.Reject", new[] { "I am sorry, but I cannot take this on." });
            add("Personality.Lover.Choice.Yes", new[] { "Yes. If it helps, I will.", "Of course. Tell me what you need." });
            add("Personality.Lover.Choice.No", new[] { "No. I cannot agree to that." });
            add("Personality.Serpent.Choice.Accept", new[] { "Oh, absolutely. This could be fun." });
            add("Personality.Serpent.Choice.Reject", new[] { "No. Bore someone else." });
            add("Personality.Serpent.Choice.Yes", new[] { "Yes. Why not?" });
            add("Personality.Serpent.Choice.No", new[] { "No. I prefer disappointing you." });
            add("Personality.Steed.Choice.Accept", new[] { "Sounds like a journey. Count me in." });
            add("Personality.Steed.Choice.Reject", new[] { "Not this road. I have another to follow." });
            add("Personality.Steed.Choice.Yes", new[] { "Yes. Let us see where it leads." });
            add("Personality.Steed.Choice.No", new[] { "No. Wrong direction for me." });
            add("Personality.Tower.Choice.Accept", new[] { "Agreed, provided I get first look at anything interesting." });
            add("Personality.Tower.Choice.Reject", new[] { "No. There is nothing here worth acquiring." });
            add("Personality.Tower.Choice.Yes", new[] { "Yes. Show me." });
            add("Personality.Tower.Choice.No", new[] { "No. Keep it." });
            add("Personality.Atronach.Choice.Accept", new[] { "Understood. I will handle it." });
            add("Personality.Atronach.Choice.Reject", new[] { "No. The terms do not work for me." });
            add("Personality.Atronach.Choice.Yes", new[] { "Yes. I understand.", "Agreed. Keep it simple." });
            add("Personality.Atronach.Choice.No", new[] { "No. The terms do not suit me.", "No. I will pass." });
            add("Personality.Shadow.Choice.Accept", new[] { "Give me the trail and the last known facts." });
            add("Personality.Shadow.Choice.Reject", new[] { "No. Too many missing pieces." });
            add("Personality.Shadow.Choice.Yes", new[] { "Yes, but I want to know who else knows." });
            add("Personality.Shadow.Choice.No", new[] { "No. Something about this is wrong." });

            add("Personality.Apprentice.General", new[] { "Still learning this land.", "I should listen before I speak.", "One step at a time." });
            add("Personality.Apprentice.CombatStart", new[] { "So be it.", "Stay focused." });
            add("Personality.Apprentice.CombatEnd", new[] { "Done.", "Still standing." });
            add("Personality.Apprentice.QuestAccepted", new[] { "I have my task.", "I will see it through." });
            add("Personality.Apprentice.QuestResolved", new[] { "The task is done.", "One step closer." });
            add("Personality.Apprentice.LowHealth", new[] { "Need to keep moving.", "Not finished yet." });
            add("Personality.Apprentice.CriticalHealth", new[] { "Still have a mission.", "Not here. Not yet." });
            add("Personality.Apprentice.Choice.Accept", new[] { "I will do it.", "I have my task." });
            add("Personality.Apprentice.Choice.Reject", new[] { "No. I need to stay on course.", "I cannot take that on." });
            add("Personality.Apprentice.Choice.Yes", new[] { "Yes. I understand.", "Go on. I am listening." });
            add("Personality.Apprentice.Choice.No", new[] { "No. I need to stay on course.", "Not this time. My task comes first." });
            add("Personality.Mage.Quest._BRISIEN.Accepted", new[] { "Lady Brisienna. An Imperial agent is rarely summoned for a trivial question." });
            add("Personality.Ritual.Quest._BRISIEN.Accepted", new[] { "Lady Brisienna wants discretion. Secrets already improve the assignment." });
            add("Personality.Lady.Quest._BRISIEN.Accepted", new[] { "An Imperial summons deserves care. Lady Brisienna will expect discretion as much as speed." });
            add("Personality.Lord.Quest._BRISIEN.Accepted", new[] { "The Emperor sends an agent to summon me. At least someone remembers the proper scale of affairs." });
            add("Personality.Warrior.Quest._BRISIEN.Accepted", new[] { "Lady Brisienna speaks for the Emperor. I gave him my service; I will hear her out." });
            add("Personality.Thief.Quest._BRISIEN.Accepted", new[] { "Imperial agent, private meeting. Either very profitable or very dangerous." });
            add("Personality.Lover.Quest._BRISIEN.Accepted", new[] { "If the Emperor sent Lady Brisienna quietly, someone may be in more trouble than the court admits." });
            add("Personality.Serpent.Quest._BRISIEN.Accepted", new[] { "A secret Imperial meeting? Finally, politics with some flavor." });
            add("Personality.Steed.Quest._BRISIEN.Accepted", new[] { "Lady Brisienna gives me a destination and a mystery. Good enough for the road." });
            add("Personality.Tower.Quest._BRISIEN.Accepted", new[] { "Imperial secrets tend to leave valuable objects in their wake." });
            add("Personality.Atronach.Quest._BRISIEN.Accepted", new[] { "Lady Brisienna. Imperial work. Keep it quiet and get the facts." });
            add("Personality.Shadow.Quest._BRISIEN.Accepted", new[] { "Secret meeting, Imperial agent, no public explanation. Assume we are not the only ones watching." });
            add("Personality.Mage.Quest.S0000999.Accepted", new[] { "The Emperor's charge is the central question. Everything else in the Bay is evidence until proven otherwise." });
            add("Personality.Ritual.Quest.S0000999.Accepted", new[] { "An Emperor's private concern has already drawn me into the Bay. Private concerns hide powerful causes." });
            add("Personality.Lady.Quest.S0000999.Accepted", new[] { "The Emperor trusted me with a delicate matter. Carelessness could harm more than my own reputation." });
            add("Personality.Lord.Quest.S0000999.Accepted", new[] { "An Emperor's request is at least an assignment worthy of attention." });
            add("Personality.Warrior.Quest.S0000999.Accepted", new[] { "I came to the Bay on the Emperor's word. I will not forget the duty beneath these distractions." });
            add("Personality.Thief.Quest.S0000999.Accepted", new[] { "Imperial business makes every side job look smaller and every secret look more expensive." });
            add("Personality.Lover.Quest.S0000999.Accepted", new[] { "Whatever court business brought me here, real people will pay the price if I mishandle it." });
            add("Personality.Serpent.Quest.S0000999.Accepted", new[] { "Imperial secrets, dead kings, nervous nobles. The Bay is growing on me." });
            add("Personality.Steed.Quest.S0000999.Accepted", new[] { "The Emperor sent me across the Bay for answers. At least the journey is living up to the invitation." });
            add("Personality.Tower.Quest.S0000999.Accepted", new[] { "Imperial mysteries have a habit of involving objects everyone suddenly wants." });
            add("Personality.Atronach.Quest.S0000999.Accepted", new[] { "The Emperor's charge comes first. Do not let lesser work bury it." });
            add("Personality.Shadow.Quest.S0000999.Accepted", new[] { "The Emperor's charge is why I am here. Every coincidence around it deserves suspicion." });
            add("Personality.Mage.Location.DirenniTower.DungeonEnter", new[] { "Direnni work deserves care. Damage the evidence and the lesson disappears with it." });
            add("Personality.Ritual.Location.ScourgBarrow.DungeonEnter", new[] { "A barrow with a reputation. Let us see which dead thing earned it." });
            add("Personality.Warrior.Location.PrivateersHold.DungeonEnter", new[] { "I know these stones. They do not get a second chance to surprise me." });
            add("Personality.Shadow.Location.PrivateersHold.DungeonEnter", new[] { "I woke here once without answers. This time I watch the entrances." });
        }

        #endregion
    }

    internal enum SpeechPriority { Automatic = 1, Manual = 2, Typed = 3, Dialogue = 4 }

    internal class PlayerSpeechRequest
    {
        public string Text;
        public SpeechPriority Priority;
        public string Category;
        public float EarliestTime;
        public bool UseNpcvoPresentation;
    }

    [Serializable]
    internal class BarkLibrary
    {
        public int schemaVersion = 4;
        public BarkSet[] sets = new BarkSet[0];
    }

    [Serializable]
    internal class BarkSet
    {
        public string key;
        public string[] lines = new string[0];
    }

    internal sealed class CharacterVoicePickerWindow : DaggerfallPopupWindow
    {
        readonly string[] allVoices;
        readonly List<string> filteredVoices = new List<string>();
        readonly Action<string, int, float, string> accepted;
        readonly Action<string, int, float, string> preview;
        readonly Action stopPreview;
        int voiceIndex;
        string selectedVoice = string.Empty;
        int accentFilter; // 0 All, 1 US, 2 UK
        int genderFilter; // 0 All, 1 Female, 2 Male
        int depth;
        float speed;
        int styleIndex;
        TextLabel accentLabel;
        TextLabel genderLabel;
        TextLabel voiceLabel;
        TextLabel voiceIdLabel;
        TextLabel countLabel;
        TextLabel depthLabel;
        TextLabel speedLabel;
        TextLabel qualityLabel;
        TextLabel helpLabel;

        static readonly string[] AccentNames = { "All", "US", "UK" };
        static readonly string[] GenderNames = { "All", "Female", "Male" };
        static readonly string[] StyleIds = { "clean", "cdrom", "dos" };
        static readonly string[] StyleNames = { "Clean", "CD-ROM", "DOS" };

        public CharacterVoicePickerWindow(IUserInterfaceManager uiManager, IUserInterfaceWindow previous,
            string[] voices, string initialVoice, int depth, float speed, string style,
            int initialAccentFilter, int initialGenderFilter,
            Action<string, int, float, string> accepted,
            Action<string, int, float, string> preview,
            Action stopPreview)
            : base(uiManager, previous)
        {
            allVoices = voices ?? new string[0];
            this.accepted = accepted;
            this.preview = preview;
            this.stopPreview = stopPreview;
            this.depth = Mathf.Clamp(depth, 0, 100);
            this.speed = Mathf.Clamp(speed, 0.50f, 1.50f);
            accentFilter = Mathf.Clamp(initialAccentFilter, 0, 2);
            genderFilter = Mathf.Clamp(initialGenderFilter, 0, 2);
            selectedVoice = initialVoice ?? string.Empty;
            string normalized = (style ?? string.Empty).Trim().ToLowerInvariant();
            styleIndex = normalized == "cdrom" ? 1 : (normalized == "dos" ? 2 : 0);
            AllowCancel = false;
            RebuildFilteredVoices(selectedVoice);
        }

        protected override void Setup()
        {
            if (IsSetup) return;
            base.Setup();

            Panel panel = new Panel();
            panel.Size = new Vector2(300, 188);
            panel.Position = new Vector2(10, 6);
            DaggerfallUI.Instance.SetDaggerfallPopupStyle(DaggerfallUI.PopupStyle.Parchment, panel);
            NativePanel.Components.Add(panel);

            TextLabel title = DaggerfallUI.AddTextLabel(DaggerfallUI.DefaultFont, new Vector2(14, 8), "Choose Character Voice", panel);
            title.ShadowColor = Color.black;
            title.ShadowPosition = new Vector2(1, 1);

            TextLabel source = DaggerfallUI.AddTextLabel(DaggerfallUI.DefaultFont, new Vector2(14, 20),
                "Accent and gender are filters only. All / All shows every packaged voice.", panel);
            source.MaxWidth = 272;
            source.TextColor = new Color(0.37f, 0.24f, 0.10f);

            DaggerfallUI.AddTextLabel(DaggerfallUI.DefaultFont, new Vector2(14, 37), "Accent", panel);
            AddArrowButton(panel, new Rect(83, 33, 22, 14), "<", -1, AdjustAccentFilter);
            accentLabel = DaggerfallUI.AddTextLabel(DaggerfallUI.DefaultFont, new Vector2(112, 37), string.Empty, panel);
            accentLabel.MaxWidth = 52;
            AddArrowButton(panel, new Rect(166, 33, 22, 14), ">", 1, AdjustAccentFilter);

            DaggerfallUI.AddTextLabel(DaggerfallUI.DefaultFont, new Vector2(14, 53), "Gender", panel);
            AddArrowButton(panel, new Rect(83, 49, 22, 14), "<", -1, AdjustGenderFilter);
            genderLabel = DaggerfallUI.AddTextLabel(DaggerfallUI.DefaultFont, new Vector2(112, 53), string.Empty, panel);
            genderLabel.MaxWidth = 62;
            AddArrowButton(panel, new Rect(176, 49, 22, 14), ">", 1, AdjustGenderFilter);

            AddArrowButton(panel, new Rect(14, 68, 24, 16), "<", -1, CycleVoice);
            AddArrowButton(panel, new Rect(262, 68, 24, 16), ">", 1, CycleVoice);
            voiceLabel = DaggerfallUI.AddTextLabel(DaggerfallUI.DefaultFont, new Vector2(46, 68), string.Empty, panel);
            voiceLabel.MaxWidth = 208;
            voiceLabel.TextColor = new Color(0.78f, 0.50f, 0.08f);
            voiceIdLabel = DaggerfallUI.AddTextLabel(DaggerfallUI.DefaultFont, new Vector2(46, 79), string.Empty, panel);
            voiceIdLabel.MaxWidth = 208;
            voiceIdLabel.TextColor = new Color(0.40f, 0.28f, 0.14f);
            countLabel = DaggerfallUI.AddTextLabel(DaggerfallUI.DefaultFont, new Vector2(14, 89), string.Empty, panel);
            countLabel.MaxWidth = 272;
            countLabel.TextColor = new Color(0.40f, 0.28f, 0.14f);

            DaggerfallUI.AddTextLabel(DaggerfallUI.DefaultFont, new Vector2(14, 105), "Depth", panel);
            AddArrowButton(panel, new Rect(83, 101, 22, 14), "<", -5, AdjustDepth);
            depthLabel = DaggerfallUI.AddTextLabel(DaggerfallUI.DefaultFont, new Vector2(112, 105), string.Empty, panel);
            depthLabel.MaxWidth = 52;
            AddArrowButton(panel, new Rect(166, 101, 22, 14), ">", 5, AdjustDepth);

            DaggerfallUI.AddTextLabel(DaggerfallUI.DefaultFont, new Vector2(14, 121), "Speed", panel);
            AddArrowButton(panel, new Rect(83, 117, 22, 14), "<", -5, AdjustSpeed);
            speedLabel = DaggerfallUI.AddTextLabel(DaggerfallUI.DefaultFont, new Vector2(112, 121), string.Empty, panel);
            speedLabel.MaxWidth = 52;
            AddArrowButton(panel, new Rect(166, 117, 22, 14), ">", 5, AdjustSpeed);

            DaggerfallUI.AddTextLabel(DaggerfallUI.DefaultFont, new Vector2(14, 137), "Quality", panel);
            AddArrowButton(panel, new Rect(83, 133, 22, 14), "<", -1, AdjustStyle);
            qualityLabel = DaggerfallUI.AddTextLabel(DaggerfallUI.DefaultFont, new Vector2(112, 137), string.Empty, panel);
            qualityLabel.MaxWidth = 70;
            AddArrowButton(panel, new Rect(186, 133, 22, 14), ">", 1, AdjustStyle);

            Button previewButton = DaggerfallUI.AddButton(new Rect(22, 153, 112, 18), panel);
            TextLabel previewText = DaggerfallUI.AddTextLabel(DaggerfallUI.DefaultFont, new Vector2(31, 4), "Preview Voice", previewButton);
            previewText.TextColor = new Color(0.95f, 0.78f, 0.22f);
            previewButton.OnMouseClick += PreviewClick;

            Button doneButton = DaggerfallUI.AddButton(new Rect(166, 153, 112, 18), panel);
            TextLabel doneText = DaggerfallUI.AddTextLabel(DaggerfallUI.DefaultFont, new Vector2(39, 4), "Use Voice", doneButton);
            doneText.TextColor = new Color(0.95f, 0.78f, 0.22f);
            doneButton.OnMouseClick += DoneClick;

            helpLabel = DaggerfallUI.AddTextLabel(DaggerfallUI.DefaultFont, new Vector2(14, 175), string.Empty, panel);
            helpLabel.MaxWidth = 272;
            helpLabel.TextColor = new Color(0.37f, 0.24f, 0.10f);

            RefreshLabels();
            IsSetup = true;
        }

        void AddArrowButton(Panel panel, Rect rect, string text, int tag, BaseScreenComponent.OnMouseClickHandler handler)
        {
            Button b = DaggerfallUI.AddButton(rect, panel);
            b.Tag = tag;
            TextLabel l = DaggerfallUI.AddTextLabel(DaggerfallUI.DefaultFont, new Vector2(7, 3), text, b);
            l.TextColor = new Color(0.95f, 0.78f, 0.22f);
            b.OnMouseClick += handler;
        }

        void AdjustAccentFilter(BaseScreenComponent sender, Vector2 pos)
        {
            int delta = Convert.ToInt32(sender.Tag, CultureInfo.InvariantCulture);
            accentFilter = (accentFilter + delta + AccentNames.Length) % AccentNames.Length;
            if (stopPreview != null) stopPreview();
            RebuildFilteredVoices(GetCurrentVoice());
            RefreshLabels();
        }

        void AdjustGenderFilter(BaseScreenComponent sender, Vector2 pos)
        {
            int delta = Convert.ToInt32(sender.Tag, CultureInfo.InvariantCulture);
            genderFilter = (genderFilter + delta + GenderNames.Length) % GenderNames.Length;
            if (stopPreview != null) stopPreview();
            RebuildFilteredVoices(GetCurrentVoice());
            RefreshLabels();
        }

        void CycleVoice(BaseScreenComponent sender, Vector2 pos)
        {
            if (filteredVoices.Count == 0) return;
            int delta = Convert.ToInt32(sender.Tag, CultureInfo.InvariantCulture);
            voiceIndex = (voiceIndex + delta + filteredVoices.Count) % filteredVoices.Count;
            selectedVoice = filteredVoices[voiceIndex];
            if (stopPreview != null) stopPreview();
            RefreshLabels();
        }

        void AdjustDepth(BaseScreenComponent sender, Vector2 pos)
        {
            depth = Mathf.Clamp(depth + Convert.ToInt32(sender.Tag, CultureInfo.InvariantCulture), 0, 100);
            if (stopPreview != null) stopPreview();
            RefreshLabels();
        }

        void AdjustSpeed(BaseScreenComponent sender, Vector2 pos)
        {
            int delta = Convert.ToInt32(sender.Tag, CultureInfo.InvariantCulture);
            speed = Mathf.Clamp(speed + delta / 100f, 0.50f, 1.50f);
            if (stopPreview != null) stopPreview();
            RefreshLabels();
        }

        void AdjustStyle(BaseScreenComponent sender, Vector2 pos)
        {
            int delta = Convert.ToInt32(sender.Tag, CultureInfo.InvariantCulture);
            styleIndex = (styleIndex + delta + StyleIds.Length) % StyleIds.Length;
            if (stopPreview != null) stopPreview();
            RefreshLabels();
        }

        void PreviewClick(BaseScreenComponent sender, Vector2 pos)
        {
            string voice = GetCurrentVoice();
            if (string.IsNullOrWhiteSpace(voice))
            {
                helpLabel.Text = "No packaged voices match these filters. Change a filter or use All.";
                return;
            }
            if (preview != null) preview(voice, depth, speed, StyleIds[styleIndex]);
            helpLabel.Text = "Previewing " + FriendlyVoiceName(voice) + ".";
        }

        void DoneClick(BaseScreenComponent sender, Vector2 pos)
        {
            string voice = GetCurrentVoice();
            if (string.IsNullOrWhiteSpace(voice))
            {
                if (allVoices.Length == 0)
                {
                    if (stopPreview != null) stopPreview();
                    CloseWindow();
                    return;
                }
                helpLabel.Text = "No packaged voices match these filters. Change a filter or use All.";
                return;
            }
            if (stopPreview != null) stopPreview();
            if (accepted != null) accepted(voice, depth, speed, StyleIds[styleIndex]);
            CloseWindow();
        }

        void RebuildFilteredVoices(string preferredVoice)
        {
            filteredVoices.Clear();
            for (int i = 0; i < allVoices.Length; i++)
            {
                string id = allVoices[i];
                if (VoiceMatchesFilters(id)) filteredVoices.Add(id);
            }

            voiceIndex = 0;
            string preferred = string.IsNullOrWhiteSpace(preferredVoice) ? selectedVoice : preferredVoice;
            for (int i = 0; i < filteredVoices.Count; i++)
            {
                if (string.Equals(filteredVoices[i], preferred, StringComparison.OrdinalIgnoreCase))
                {
                    voiceIndex = i;
                    selectedVoice = filteredVoices[i];
                    return;
                }
            }
            if (filteredVoices.Count > 0)
                selectedVoice = filteredVoices[0];
        }

        bool VoiceMatchesFilters(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            string v = id.Trim().ToLowerInvariant();
            if (accentFilter == 1 && !v.StartsWith("a")) return false;
            if (accentFilter == 2 && !v.StartsWith("b")) return false;
            if (genderFilter == 1 && !(v.Length > 1 && v[1] == 'f')) return false;
            if (genderFilter == 2 && !(v.Length > 1 && v[1] == 'm')) return false;
            return true;
        }

        string GetCurrentVoice()
        {
            if (filteredVoices.Count == 0) return string.Empty;
            voiceIndex = Mathf.Clamp(voiceIndex, 0, filteredVoices.Count - 1);
            return filteredVoices[voiceIndex];
        }

        void RefreshLabels()
        {
            if (accentLabel != null) accentLabel.Text = AccentNames[accentFilter];
            if (genderLabel != null) genderLabel.Text = GenderNames[genderFilter];

            string id = GetCurrentVoice();
            if (string.IsNullOrEmpty(id))
            {
                if (voiceLabel != null) voiceLabel.Text = "No matching packaged voices";
                if (voiceIdLabel != null) voiceIdLabel.Text = string.Empty;
                if (countLabel != null) countLabel.Text = "0 matches / " + allVoices.Length.ToString(CultureInfo.InvariantCulture) + " packaged";
            }
            else
            {
                if (voiceLabel != null) voiceLabel.Text = FriendlyVoiceName(id);
                if (voiceIdLabel != null) voiceIdLabel.Text = id;
                if (countLabel != null) countLabel.Text = (voiceIndex + 1).ToString(CultureInfo.InvariantCulture) + " of " +
                    filteredVoices.Count.ToString(CultureInfo.InvariantCulture) + " matching / " + allVoices.Length.ToString(CultureInfo.InvariantCulture) + " packaged";
            }
            if (depthLabel != null) depthLabel.Text = depth.ToString(CultureInfo.InvariantCulture);
            if (speedLabel != null) speedLabel.Text = Mathf.RoundToInt(speed * 100f).ToString(CultureInfo.InvariantCulture) + "%";
            if (qualityLabel != null) qualityLabel.Text = StyleNames[styleIndex];
            if (helpLabel != null && !string.IsNullOrEmpty(id)) helpLabel.Text = "Filters never remove voices from the package; set All to browse everything.";
        }

        static string FriendlyVoiceName(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return "Unknown Voice";
            string v = id.Trim();
            int underscore = v.IndexOf('_');
            string prefix = underscore > 0 ? v.Substring(0, underscore).ToLowerInvariant() : string.Empty;
            string rawName = underscore >= 0 && underscore + 1 < v.Length ? v.Substring(underscore + 1) : v;
            string[] words = rawName.Replace('-', ' ').Split(new[] { ' ', '_' }, StringSplitOptions.RemoveEmptyEntries);
            StringBuilder name = new StringBuilder();
            for (int i = 0; i < words.Length; i++)
            {
                if (i > 0) name.Append(' ');
                string w = words[i];
                name.Append(char.ToUpperInvariant(w[0]));
                if (w.Length > 1) name.Append(w.Substring(1));
            }

            string family = VoiceFamily(prefix);
            return family.Length == 0 ? name.ToString() : name + " - " + family;
        }

        static string VoiceFamily(string prefix)
        {
            if (prefix.Length < 1) return string.Empty;
            string lang;
            switch (prefix[0])
            {
                case 'a': lang = "US English"; break;
                case 'b': lang = "UK English"; break;
                case 'e': lang = "Spanish"; break;
                case 'f': lang = "French"; break;
                case 'h': lang = "Hindi"; break;
                case 'i': lang = "Italian"; break;
                case 'j': lang = "Japanese"; break;
                case 'p': lang = "Brazilian Portuguese"; break;
                case 'z': lang = "Mandarin"; break;
                default: lang = "Kokoro"; break;
            }
            if (prefix.Length > 1)
            {
                if (prefix[1] == 'm') lang += " Male";
                else if (prefix[1] == 'f') lang += " Female";
            }
            return lang;
        }
    }

    internal sealed class BirthsignPickerWindow : DaggerfallPopupWindow
    {
        readonly int suggested;
        readonly bool fromBiography;
        readonly Action<int> selected;
        readonly string[] names = { "The Mage", "The Ritual", "The Lady", "The Lord", "The Warrior", "The Thief", "The Lover", "The Serpent", "The Steed", "The Tower", "The Atronach", "The Shadow", "The Apprentice" };
        readonly string[] shortDesc = { "Wise Scholar", "Forbidden Mystic", "Diplomat", "Imperious Ruler", "Champion", "Opportunist", "Compassionate", "Malefactor", "Adventurer", "Possessive Seeker", "Professional Hunter", "Bounty Hunter", "Agent" };
        readonly string[] longDesc = {
            "Those drawn to the Mage often come from books, tutors, guildhalls, or long solitary study. This scholar listens closely, asks why, and prefers understanding a mystery before acting on it.",
            "The Ritual suits someone raised around old customs, shrines, funerary rites, or unsettling family traditions. This mystic treats forgotten places and uncomfortable knowledge with wary respect.",
            "The Lady suggests a life touched by courts, households, diplomacy, or careful social expectation. This diplomat reads a room, values mercy, and tries to keep dignity when tempers rise.",
            "The Lord hints at duty, inheritance, command, or a childhood spent beneath demanding expectations. This ruler is proud, resilient, and accustomed to taking responsibility when others hesitate.",
            "The Warrior fits someone shaped by drills, guards, mercenaries, family arms, or hard-earned battlefield lessons. This champion trusts courage, preparation, and decisive action.",
            "The Thief suggests alleys, markets, close escapes, or simply a life where noticing opportunity mattered. This opportunist is quick, evasive, daring, and reluctant to waste an advantage.",
            "The Lover suits someone whose story is defined by people: family, friendship, romance, loyalty, or loss. This compassionate traveler notices feelings quickly and forms strong personal bonds.",
            "The Serpent suits an outsider, drifter, schemer, or survivor whose road has rarely been orderly. This malefactor is sardonic, unpredictable, comfortable with risk, and difficult to intimidate.",
            "The Steed suggests a traveler raised on roads, ships, caravans, borderlands, or a household that never stayed still for long. This adventurer grows restless when life becomes too settled.",
            "The Tower hints at old estates, locked rooms, ruins, collections, or a fascination with what others keep hidden. This seeker is persistent, acquisitive, and rarely convinced that a secret should stay closed.",
            "The Atronach fits a self-reliant upbringing: isolated work, wilderness, long watches, or responsibilities handled without much help. This hunter is patient, economical, and slow to commit before understanding a threat.",
            "The Shadow suggests a private life spent observing more than speaking: scouts, trackers, informants, hunters, or simply someone taught not to trust first impressions. This bounty hunter follows trails carefully.",
            "The Apprentice is the Agent: new to the land, learning its customs while pursuing a larger quest. Quiet by habit and dedicated to the mission, the Agent listens first, speaks briefly, and lets actions carry most of the meaning." };

        Panel artPanel;
        TextLabel signLabel;
        TextLabel archetypeLabel;
        TextLabel bioLabel;
        TextLabel suggestionLabel;
        int previewIndex = -1;
        int chosenIndex;
        static readonly Texture2D[] artCache = new Texture2D[13];
        readonly Button[] signButtons = new Button[13];
        readonly TextLabel[] signText = new TextLabel[13];

        public BirthsignPickerWindow(IUserInterfaceManager uiManager, IUserInterfaceWindow previous, int suggested, bool fromBiography, Action<int> selected)
            : base(uiManager, previous)
        {
            this.suggested = Mathf.Clamp(suggested, 0, 12);
            this.fromBiography = fromBiography;
            this.selected = selected;
            chosenIndex = this.suggested;
            AllowCancel = false;
        }

        protected override void Setup()
        {
            if (IsSetup) return;
            base.Setup();

            Panel panel = new Panel();
            panel.Size = new Vector2(300, 188);
            panel.Position = new Vector2(10, 6);
            DaggerfallUI.Instance.SetDaggerfallPopupStyle(DaggerfallUI.PopupStyle.Parchment, panel);
            NativePanel.Components.Add(panel);

            TextLabel title = DaggerfallUI.AddTextLabel(
                DaggerfallUI.DefaultFont, new Vector2(14, 9), "Choose Character Personality", panel);
            title.ShadowColor = Color.black;
            title.ShadowPosition = new Vector2(1, 1);

            suggestionLabel = DaggerfallUI.AddTextLabel(
                DaggerfallUI.DefaultFont, new Vector2(146, 10),
                fromBiography ? "Suggested by your biography" : "Hover a sign to preview it", panel);
            suggestionLabel.MaxWidth = 138;
            suggestionLabel.TextColor = fromBiography ? new Color(1f, 0.84f, 0.22f) : new Color(0.78f, 0.67f, 0.42f);

            for (int i = 0; i < 13; i++)
            {
                float y = 27f + i * 10.7f;
                Button b = DaggerfallUI.AddButton(new Rect(14, y, 118, 9), panel);
                b.Tag = i;
                b.UseFocus = true;
                if (i == suggested)
                    b.BackgroundColor = new Color(0.35f, 0.23f, 0.08f, 0.35f);
                TextLabel l = DaggerfallUI.AddTextLabel(
                    DaggerfallUI.DefaultFont, new Vector2(2, 1), names[i], b);
                l.MaxWidth = 114;
                l.TextColor = i == suggested ? new Color(1f, 0.9f, 0.36f) : new Color(0.96f, 0.82f, 0.22f);
                b.OnMouseEnter += Hover;
                b.OnMouseClick += Pick;
                l.Tag = i;
                l.OnMouseEnter += Hover;
                l.OnMouseClick += Pick;
                signButtons[i] = b;
                signText[i] = l;
            }

            Button useButton = DaggerfallUI.AddButton(new Rect(14, 169, 118, 14), panel);
            TextLabel useText = DaggerfallUI.AddTextLabel(DaggerfallUI.DefaultFont, new Vector2(17, 3), "Use Personality", useButton);
            useText.TextColor = new Color(0.95f, 0.78f, 0.22f);
            useButton.OnMouseClick += UsePersonality;

            Panel frame = DaggerfallUI.AddPanel(new Rect(147, 27, 135, 79), panel);
            frame.BackgroundColor = new Color(0.10f, 0.07f, 0.03f, 0.94f);
            artPanel = DaggerfallUI.AddPanel(new Rect(2, 2, 131, 75), frame);

            signLabel = DaggerfallUI.AddTextLabel(
                DaggerfallUI.DefaultFont, new Vector2(147, 110), string.Empty, panel);
            signLabel.MaxWidth = 138;
            signLabel.TextColor = new Color(1f, 0.86f, 0.25f);

            archetypeLabel = DaggerfallUI.AddTextLabel(
                DaggerfallUI.DefaultFont, new Vector2(147, 121), string.Empty, panel);
            archetypeLabel.MaxWidth = 138;
            archetypeLabel.TextColor = new Color(0.82f, 0.67f, 0.30f);

            bioLabel = DaggerfallUI.AddTextLabel(
                DaggerfallUI.DefaultFont, new Vector2(147, 133), string.Empty, panel);
            bioLabel.MaxCharacters = -1;
            bioLabel.MaxWidth = 138;
            bioLabel.WrapText = true;
            bioLabel.WrapWords = true;
            bioLabel.TextColor = new Color(0.33f, 0.20f, 0.08f);

            Preview(suggested);
            IsSetup = true;
        }

        void Hover(BaseScreenComponent sender)
        {
            if (sender == null || sender.Tag == null) return;
            Preview(Convert.ToInt32(sender.Tag, CultureInfo.InvariantCulture));
        }

        void Pick(BaseScreenComponent sender, Vector2 pos)
        {
            if (sender == null || sender.Tag == null) return;
            chosenIndex = Mathf.Clamp(Convert.ToInt32(sender.Tag, CultureInfo.InvariantCulture), 0, 12);
            Preview(chosenIndex);
            if (suggestionLabel != null) suggestionLabel.Text = "Selected: " + names[chosenIndex];
        }

        void UsePersonality(BaseScreenComponent sender, Vector2 pos)
        {
            // Hover previews lore/art; clicking a sign chooses it. The explicit commit button avoids
            // a list click accidentally unwinding DFU's character-creation popup stack.
            if (selected != null) selected(chosenIndex);
            CloseWindow();
        }

        void Preview(int index)
        {
            index = Mathf.Clamp(index, 0, 12);
            if (previewIndex == index) return;
            previewIndex = index;
            for (int i = 0; i < signButtons.Length; i++)
            {
                if (signButtons[i] != null) signButtons[i].BackgroundColor = i == index ? new Color(0.35f, 0.23f, 0.08f, 0.35f) : Color.clear;
                if (signText[i] != null) signText[i].TextColor = i == index ? new Color(1f, 0.9f, 0.36f) : new Color(0.96f, 0.82f, 0.22f);
            }
            if (artPanel != null) artPanel.BackgroundTexture = GetArt(index);
            if (signLabel != null) signLabel.Text = names[index];
            if (archetypeLabel != null) archetypeLabel.Text = shortDesc[index];
            if (bioLabel != null) bioLabel.Text = longDesc[index];
            if (suggestionLabel != null && fromBiography)
                suggestionLabel.Text = index == suggested ? "Suggested by your biography" : "Biography suggested " + names[suggested];
        }

        static Texture2D GetArt(int index)
        {
            index = Mathf.Clamp(index, 0, 12);
            if (artCache[index] == null)
                artCache[index] = BirthsignPixelArt.Create(index);
            return artCache[index];
        }
    }

    internal static class BirthsignPixelArt
    {
        const int W = 96;
        const int H = 56;
        static readonly Color32 Ink = new Color32(21, 20, 24, 255);
        static readonly Color32 Night = new Color32(17, 24, 39, 255);
        static readonly Color32 Night2 = new Color32(29, 37, 55, 255);
        static readonly Color32 Gold = new Color32(222, 181, 72, 255);
        static readonly Color32 Pale = new Color32(239, 222, 158, 255);
        static readonly Color32 Dim = new Color32(112, 92, 57, 255);

        // Hand-authored constellations: each entry is a sequence of x,y points. Negative pairs split
        // disconnected strokes. The silhouettes are intentionally suggestive rather than literal icons,
        // closer to an old Tamrielic star chart than a diagram of a sword, tower, or animal.
        static readonly int[][] Points = new int[][] {
            new[]{18,42, 27,30, 38,22, 49,15, 62,19, 73,11, -1,-1, 39,22, 46,34, 56,45},             // Mage
            new[]{22,18, 32,11, 45,9, 58,14, 68,24, 61,36, 48,42, 34,38, 25,29, 22,18, -1,-1, 48,42, 48,49}, // Ritual
            new[]{22,39, 31,27, 42,19, 54,15, 65,22, 72,34, -1,-1, 42,19, 47,31, 55,43},              // Lady
            new[]{20,38, 31,29, 39,17, 49,10, 59,17, 68,29, 77,37, -1,-1, 31,29, 49,32, 68,29},       // Lord
            new[]{19,44, 30,34, 42,25, 55,15, 70,10, -1,-1, 42,25, 54,35, 67,45},                    // Warrior
            new[]{20,25, 31,17, 44,15, 57,21, 68,18, 77,11, -1,-1, 44,15, 39,30, 49,41, 61,45},       // Thief
            new[]{19,22, 30,15, 41,20, 48,31, 55,20, 66,15, 77,22, -1,-1, 48,31, 48,45},             // Lover
            new[]{15,18, 27,12, 40,17, 52,27, 64,22, 77,28, 68,39, 54,45, 40,41, 28,47},              // Serpent
            new[]{18,35, 29,25, 41,18, 55,19, 68,13, 78,20, -1,-1, 41,18, 48,32, 61,39, 74,36, -1,-1, 29,25, 23,44}, // Steed
            new[]{25,43, 25,29, 33,18, 42,12, 51,18, 60,12, 69,18, 77,29, 77,43, -1,-1, 42,12, 42,31, 60,31, 60,12}, // Tower
            new[]{18,33, 28,19, 42,12, 54,14, 68,22, 77,35, -1,-1, 28,19, 33,38, 47,45, 63,39, 68,22}, // Atronach
            new[]{18,18, 31,12, 44,17, 53,28, 62,38, 75,43, -1,-1, 44,17, 58,12, 73,16},              // Shadow
            new[]{20,43, 30,34, 39,27, 48,20, 58,14, 70,10, -1,-1, 39,27, 50,35, 63,43}              // Apprentice / Agent
        };

        public static Texture2D Create(int sign)
        {
            sign = Mathf.Clamp(sign, 0, 12);
            Texture2D tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            Color32[] px = new Color32[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int n = ((x * 19 + y * 37 + sign * 53) ^ (x * y + sign * 11)) & 31;
                    px[y * W + x] = n < 5 ? Night2 : Night;
                    if (n == 7 || n == 19) Set(px, x, y, new Color32(69, 67, 65, 255));
                }

            RectFill(px, 1, 1, W - 2, 1, Ink);
            RectFill(px, 1, H - 2, W - 2, 1, Ink);
            RectFill(px, 1, 1, 1, H - 2, Ink);
            RectFill(px, W - 2, 1, 1, H - 2, Ink);
            DrawConstellation(px, Points[sign]);
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }

        static void DrawConstellation(Color32[] p, int[] pts)
        {
            int lastX = -1, lastY = -1;
            int starIndex = 0;
            for (int i = 0; i + 1 < pts.Length; i += 2)
            {
                int x = pts[i], y = pts[i + 1];
                if (x < 0 || y < 0) { lastX = lastY = -1; continue; }
                if (lastX >= 0) Line(p, lastX, lastY, x, y, Dim);
                Star(p, x, y, (starIndex % 4 == 0) ? 2 : 1, (starIndex % 3 == 0) ? Pale : Gold);
                // a tiny warm halo keeps the highlighted stars organic at low resolution
                if (x > 1) Set(p, x - 1, y + 1, new Color32(121, 92, 43, 255));
                if (x < W - 2) Set(p, x + 1, y - 1, new Color32(121, 92, 43, 255));
                lastX = x; lastY = y; starIndex++;
            }
        }

        static void Set(Color32[] p, int x, int y, Color32 c)
        {
            if (x < 0 || x >= W || y < 0 || y >= H) return;
            p[y * W + x] = c;
        }

        static void RectFill(Color32[] p, int x, int y, int w, int h, Color32 c)
        {
            for (int yy = y; yy < y + h; yy++)
                for (int xx = x; xx < x + w; xx++) Set(p, xx, yy, c);
        }

        static void Line(Color32[] p, int x0, int y0, int x1, int y1, Color32 c)
        {
            int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
            int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;
            while (true)
            {
                Set(p, x0, y0, c);
                if (x0 == x1 && y0 == y1) break;
                int e2 = err * 2;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        static void Star(Color32[] p, int x, int y, int r, Color32 c)
        {
            Set(p, x, y, c);
            for (int i = 1; i <= r; i++)
            {
                Set(p, x + i, y, c); Set(p, x - i, y, c);
                Set(p, x, y + i, c); Set(p, x, y - i, c);
            }
        }
    }

    internal class PlayerVOConfig
    {
        public bool Enabled = true;
        public bool AutomaticBarks = true;
        public bool VoiceDialogueChoices = true;
        public bool EnhancedNpcvoConversationUI = true;
        public int PlayerPortraitStyle = 0; // head+paperdoll background, head+dark backdrop, head only
        public int BarkKeyMode = 2; // context, keyboard, hybrid
        public string BarkKeyName = "V";
        public int HoldToTypeMilliseconds = 450;
        public int BarkFrequency = 1;
        public int AutomaticCooldownSeconds = 45;
        public int ManualCooldownSeconds = 3;
        public int ActionMovieMode = 0;
        public int SubtitleMode = 2;
        public string PlayerVoice = "am_granite"; // resolved from native selectors; exact VoiceOverride takes precedence.
        public string VoiceOverride = string.Empty;
        public int VoiceAccent = 0;
        public int VoiceGender = 0;
        public int VoiceDelivery = 4;
        public int VoiceDepth = 60;
        public string KokoroLanguage = "auto";
        public float KokoroSpeed = 1f;
        public int Personality = 12;
        public bool RaceFlavor = true;
        public bool IntegrateClimatesCalories = true;
        public bool IntegrateNpcvo = true;
        public bool ConditionBarks = true;
        public bool QuestAwareBarks = true;
        public int Volume = 90;
        public bool UseGameSoundVolume = true;
        public string AudioStyle = "clean";
        public int KokoroPort = 5000;
        public bool AutoStartVoiceEngine = true;
        public bool CharacterCreationBirthsign = true;
        public bool CharacterCreationVoice = true;
        public bool AutoManageCache = true;
        public int MaxCacheSizeMB = 1024;

        public static PlayerVOConfig LoadOrCreate(string path)
        {
            PlayerVOConfig c = new PlayerVOConfig();
            if (!File.Exists(path)) { c.Save(path); return c; }
            try
            {
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim(); if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    int eq = line.IndexOf('='); if (eq < 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant(); string v = line.Substring(eq + 1).Trim();
                    int i; float f; bool b;
                    switch (k)
                    {
                        case "enabled": if (bool.TryParse(v, out b)) c.Enabled = b; break;
                        case "playervoice": c.PlayerVoice = v; break; // legacy resolved voice
                        case "voiceoverride": c.VoiceOverride = v; break;
                        case "voiceaccent": if (int.TryParse(v, out i)) c.VoiceAccent = Mathf.Clamp(i, 0, 1); break;
                        case "voicegender": if (int.TryParse(v, out i)) c.VoiceGender = Mathf.Clamp(i, 0, 1); break;
                        case "voicedelivery": if (int.TryParse(v, out i)) c.VoiceDelivery = Mathf.Clamp(i, 0, 4); break;
                        case "voicedepth": if (int.TryParse(v, out i)) c.VoiceDepth = Mathf.Clamp(i, 0, 100); break;
                        case "kokorolanguage": c.KokoroLanguage = v; break;
                        case "kokoroport": if (int.TryParse(v, out i)) c.KokoroPort = i; break;
                        case "autostartvoiceengine": if (bool.TryParse(v, out b)) c.AutoStartVoiceEngine = b; break;
                        case "charactercreationbirthsign": if (bool.TryParse(v, out b)) c.CharacterCreationBirthsign = b; break;
                        case "charactercreationvoice": if (bool.TryParse(v, out b)) c.CharacterCreationVoice = b; break;
                        case "kokorospeed": if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) c.KokoroSpeed = f; break;
                        case "audiostyle": c.AudioStyle = string.IsNullOrWhiteSpace(v) ? "clean" : v.ToLowerInvariant(); break;
                        case "barkkey": c.BarkKeyName = v; break;
                        case "automaticcooldownseconds": if (int.TryParse(v, out i)) c.AutomaticCooldownSeconds = i; break;
                        case "manualcooldownseconds": if (int.TryParse(v, out i)) c.ManualCooldownSeconds = i; break;
                        case "personality": if (int.TryParse(v, out i)) c.Personality = Mathf.Clamp(i, 0, 12); break;
                        case "subtitlemode": if (int.TryParse(v, out i)) c.SubtitleMode = Mathf.Clamp(i, 0, 2); break;
                        case "maxcachesizemb": if (int.TryParse(v, out i)) c.MaxCacheSizeMB = i; break;
                    }
                }
            }
            catch { }
            return c;
        }

        public void Save(string path)
        {
            try
            {
                File.WriteAllLines(path, new[] {
                    "# PlayerVO advanced/runtime settings. Common options are mirrored in DFU Mod Settings.",
                    "Enabled=" + Enabled,
                    "PlayerVoice=" + PlayerVoice,
                    "VoiceOverride=" + (VoiceOverride ?? string.Empty),
                    "VoiceAccent=" + VoiceAccent.ToString(CultureInfo.InvariantCulture),
                    "VoiceGender=" + VoiceGender.ToString(CultureInfo.InvariantCulture),
                    "VoiceDelivery=" + VoiceDelivery.ToString(CultureInfo.InvariantCulture),
                    "VoiceDepth=" + VoiceDepth.ToString(CultureInfo.InvariantCulture),
                    "KokoroLanguage=" + KokoroLanguage,
                    "KokoroPort=" + KokoroPort.ToString(CultureInfo.InvariantCulture),
                    "AutoStartVoiceEngine=" + AutoStartVoiceEngine,
                    "CharacterCreationBirthsign=" + CharacterCreationBirthsign,
                    "CharacterCreationVoice=" + CharacterCreationVoice,
                    "KokoroSpeed=" + KokoroSpeed.ToString(CultureInfo.InvariantCulture),
                    "AudioStyle=" + (AudioStyle ?? "clean"),
                    "BarkKey=" + BarkKeyName,
                    "AutomaticCooldownSeconds=" + AutomaticCooldownSeconds.ToString(CultureInfo.InvariantCulture),
                    "ManualCooldownSeconds=" + ManualCooldownSeconds.ToString(CultureInfo.InvariantCulture),
                    "Personality=" + Personality.ToString(CultureInfo.InvariantCulture),
                    "SubtitleMode=" + SubtitleMode.ToString(CultureInfo.InvariantCulture),
                    "MaxCacheSizeMB=" + MaxCacheSizeMB.ToString(CultureInfo.InvariantCulture)
                });
            }
            catch { }
        }
    }
    // Shared client contract for Daggerfall Voice Engine. Kept source-compatible with Unity 2019/DFU 1.1.1.
    internal sealed class DaggerfallVoiceEngineTurn
    {
        public string Id = string.Empty;
        public bool Ready;
        public bool EngineOnline;
        public bool Expired;
    }

    internal sealed class DaggerfallVoiceEngineClient
    {
        private readonly MonoBehaviour owner;
        private readonly string moduleName;
        private readonly string moduleVersion;
        private readonly Func<int> portProvider;
        private readonly Func<bool> autoStartProvider;
        private readonly float launcherDelaySeconds;
        private System.Diagnostics.Process ownedProcess;
        private bool launchAttempted;
        private float createdAt;
        private bool engineOnline;
        private string lastHealth = string.Empty;

        public bool EngineOnline { get { return engineOnline; } }
        public bool OwnsProcess { get { return ownedProcess != null; } }
        public string LastHealth { get { return lastHealth; } }

        public DaggerfallVoiceEngineClient(MonoBehaviour owner, string moduleName, string moduleVersion,
            Func<int> portProvider, Func<bool> autoStartProvider, float launcherDelaySeconds)
        {
            this.owner = owner;
            this.moduleName = moduleName;
            this.moduleVersion = moduleVersion;
            this.portProvider = portProvider;
            this.autoStartProvider = autoStartProvider;
            this.launcherDelaySeconds = Mathf.Max(0f, launcherDelaySeconds);
            this.createdAt = Time.realtimeSinceStartup;
        }

        public IEnumerator EnsureRunning()
        {
            bool ok = false;
            yield return CheckHealth(delegate(bool v) { ok = v; });
            if (!ok && autoStartProvider != null && autoStartProvider())
            {
                float until = createdAt + launcherDelaySeconds;
                while (Time.realtimeSinceStartup < until)
                {
                    yield return null;
                    if (engineOnline) break;
                }
                if (!engineOnline)
                {
                    yield return CheckHealth(delegate(bool v) { ok = v; });
                    if (!ok) TryLaunch();
                }
                if (!ok)
                {
                    float deadline = Time.realtimeSinceStartup + 12f;
                    while (Time.realtimeSinceStartup < deadline && !ok)
                    {
                        yield return new WaitForSecondsRealtime(0.25f);
                        yield return CheckHealth(delegate(bool v) { ok = v; });
                    }
                }
            }
            if (ok) yield return RegisterPresence();
        }

        public IEnumerator AcquireTurn(int priority, string category, string sourceKey, int expiresMs,
            Func<bool> stillValid, DaggerfallVoiceEngineTurn result)
        {
            if (result == null) yield break;
            result.Id = string.Empty; result.Ready = false; result.Expired = false;

            bool ok = false;
            yield return CheckHealth(delegate(bool v) { ok = v; });
            if (!ok)
            {
                yield return EnsureRunning();
                ok = engineOnline;
            }
            result.EngineOnline = ok;
            if (!ok) yield break;

            string json = "{\"module\":\"" + Escape(moduleName) + "\",\"priority\":" + priority.ToString(CultureInfo.InvariantCulture) +
                ",\"category\":\"" + Escape(category) + "\",\"source_key\":\"" + Escape(sourceKey) + "\",\"expires_ms\":" +
                Mathf.Clamp(expiresMs, 500, 120000).ToString(CultureInfo.InvariantCulture) + "}";
            string response = string.Empty;
            bool posted = false;
            yield return PostJson("/turn/acquire", json, delegate(bool success, string text) { posted = success; response = text; });
            if (!posted) { engineOnline = false; result.EngineOnline = false; yield break; }
            result.Id = JsonField(response, "id");
            if (string.IsNullOrEmpty(result.Id)) yield break;

            float hardDeadline = Time.realtimeSinceStartup + Mathf.Max(2f, expiresMs / 1000f + 2f);
            while (Time.realtimeSinceStartup < hardDeadline)
            {
                if (stillValid != null && !stillValid())
                {
                    yield return CancelTurn(result.Id);
                    yield break;
                }
                string stateBody = string.Empty; bool got = false;
                yield return Get("/turn/" + result.Id, delegate(bool success, string text) { got = success; stateBody = text; });
                if (!got) { result.EngineOnline = false; engineOnline = false; yield break; }
                string state = JsonField(stateBody, "state").ToLowerInvariant();
                if (state == "ready") { result.Ready = true; yield break; }
                if (state == "expired" || state == "cancelled" || state == "completed") { result.Expired = state == "expired"; yield break; }
                yield return new WaitForSecondsRealtime(0.05f);
            }
            result.Expired = true;
            yield return CancelTurn(result.Id);
        }

        public IEnumerator CompleteTurn(string id)
        {
            if (string.IsNullOrEmpty(id)) yield break;
            string json = "{\"id\":\"" + Escape(id) + "\",\"module\":\"" + Escape(moduleName) + "\"}";
            bool ignored = false; string response = string.Empty;
            yield return PostJson("/turn/complete", json, delegate(bool success, string text) { ignored = success; response = text; });
        }

        public IEnumerator CancelTurn(string id)
        {
            if (string.IsNullOrEmpty(id)) yield break;
            string json = "{\"id\":\"" + Escape(id) + "\",\"module\":\"" + Escape(moduleName) + "\"}";
            bool ignored = false; string response = string.Empty;
            yield return PostJson("/turn/cancel", json, delegate(bool success, string text) { ignored = success; response = text; });
        }


        public IEnumerator CancelModule()
        {
            string json = "{\"module\":\"" + Escape(moduleName) + "\"}";
            bool ignored = false; string response = string.Empty;
            yield return PostJson("/turn/cancel-module", json, delegate(bool success, string text) { ignored = success; response = text; });
        }

        public IEnumerator PulsePresence()
        {
            if (!engineOnline) yield return EnsureRunning();
            if (engineOnline) yield return RegisterPresence();
        }

        public void StopOwnedProcess()
        {
            if (ownedProcess == null) return;
            try { if (!ownedProcess.HasExited) ownedProcess.Kill(); } catch { }
            try { ownedProcess.Dispose(); } catch { }
            ownedProcess = null;
        }

        public string StatusSummary()
        {
            return "Daggerfall Voice Engine=" + (engineOnline ? "online" : "offline") +
                (OwnsProcess ? " (launched by " + moduleName + ")" : string.Empty);
        }

        private IEnumerator RegisterPresence()
        {
            string json = "{\"module\":\"" + Escape(moduleName) + "\",\"version\":\"" + Escape(moduleVersion) + "\"}";
            bool ignored = false; string text = string.Empty;
            yield return PostJson("/presence/register", json, delegate(bool success, string body) { ignored = success; text = body; });
        }

        private IEnumerator CheckHealth(Action<bool> done)
        {
            bool ok = false; string body = string.Empty;
            yield return Get("/health", delegate(bool success, string text) { ok = success; body = text; });
            engineOnline = ok && body.IndexOf("Daggerfall Voice Engine", StringComparison.OrdinalIgnoreCase) >= 0;
            lastHealth = body;
            if (done != null) done(engineOnline);
        }

        private void TryLaunch()
        {
            if (launchAttempted) return;
            launchAttempted = true;
            try
            {
                string dir = Path.Combine(Application.streamingAssetsPath, "DaggerfallVoiceEngine");
                string exe = Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor ?
                    "Daggerfall Voice Engine.exe" : "Daggerfall Voice Engine";
                string path = Path.Combine(dir, exe);
                if (!File.Exists(path))
                {
                    Debug.LogWarning("[" + moduleName + "] Daggerfall Voice Engine executable not found at " + path + ". Start it manually or install the bundled engine.");
                    return;
                }
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo();
                psi.FileName = path;
                psi.WorkingDirectory = dir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                try { psi.Arguments = "--parent-pid " + System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture); } catch { }
                ownedProcess = System.Diagnostics.Process.Start(psi);
                Debug.Log("[" + moduleName + "] Launched Daggerfall Voice Engine.");
            }
            catch (Exception ex) { Debug.LogWarning("[" + moduleName + "] Could not launch Daggerfall Voice Engine: " + ex.Message); }
        }

        private IEnumerator Get(string route, Action<bool, string> done)
        {
            string url = "http://127.0.0.1:" + Port() + route;
            using (UnityWebRequest req = UnityWebRequest.Get(url))
            {
                req.timeout = 2;
                yield return req.SendWebRequest();
#if UNITY_2020_1_OR_NEWER
                bool failed = req.result != UnityWebRequest.Result.Success;
#else
                bool failed = req.isNetworkError || req.isHttpError;
#endif
                string text = req.downloadHandler == null ? string.Empty : req.downloadHandler.text;
                if (done != null) done(!failed, text);
            }
        }

        private IEnumerator PostJson(string route, string json, Action<bool, string> done)
        {
            string url = "http://127.0.0.1:" + Port() + route;
            using (UnityWebRequest req = new UnityWebRequest(url, "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json ?? "{}"));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = 4;
                yield return req.SendWebRequest();
#if UNITY_2020_1_OR_NEWER
                bool failed = req.result != UnityWebRequest.Result.Success;
#else
                bool failed = req.isNetworkError || req.isHttpError;
#endif
                string text = req.downloadHandler == null ? string.Empty : req.downloadHandler.text;
                if (done != null) done(!failed, text);
            }
        }

        private int Port()
        {
            try { return Mathf.Clamp(portProvider == null ? 5000 : portProvider(), 1, 65535); }
            catch { return 5000; }
        }

        private static string JsonField(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return string.Empty;
            string token = "\"" + key + "\"";
            int p = json.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (p < 0) return string.Empty;
            p = json.IndexOf(':', p + token.Length); if (p < 0) return string.Empty; p++;
            while (p < json.Length && char.IsWhiteSpace(json[p])) p++;
            if (p >= json.Length) return string.Empty;
            if (json[p] == '"')
            {
                p++; StringBuilder sb = new StringBuilder(); bool esc = false;
                for (; p < json.Length; p++)
                {
                    char c = json[p];
                    if (esc) { sb.Append(c == 'n' ? '\n' : c == 't' ? '\t' : c); esc = false; continue; }
                    if (c == '\\') { esc = true; continue; }
                    if (c == '"') break;
                    sb.Append(c);
                }
                return sb.ToString();
            }
            int e = p; while (e < json.Length && json[e] != ',' && json[e] != '}' && !char.IsWhiteSpace(json[e])) e++;
            return json.Substring(p, e - p).Trim();
        }

        private static string Escape(string s)
        {
            return (s ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }
    }


}
