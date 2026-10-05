// Daggerfall Narrator - Dungeon Master v1.1.4 world-interaction narration channels
// Replaces eligible non-dialogue/flavor DaggerfallMessageBox popups with narrated subtitles,
// narrates books, quest-item notes/letters, character biography questions, and character-creation
// description popups without replacing interactive character-creation interfaces. Includes
// GrimoireUI-tolerant character-creation detection and a bounded rolling runtime TTS cache.
// Kokoro is the default local TTS backend; Piper remains available as a fallback.

using System;
using System.Collections;
using System.Collections.Generic;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Questing;
using DaggerfallWorkshop.Game.UserInterface;
using DaggerfallWorkshop.Game.UserInterfaceWindows;
using DaggerfallWorkshop.Game.Utility.ModSupport;
using DaggerfallWorkshop.Game.Utility.ModSupport.ModSettings;
using Wenzil.Console;

namespace DaggerfallNarrator
{
    public class DaggerfallNarratorMod : MonoBehaviour
    {
        private static Mod mod;
        private static DaggerfallNarratorMod instance;

        private AudioSource audioSource;
        private NarratorConfig config;
        private QuestSourceClassifier questClassifier;
        private PronunciationDictionary pronunciations;
        private Process ttsServerProcess; // Piper legacy only; Daggerfall Voice Engine self-manages Kokoro/ONNX.
        private DaggerfallVoiceEngineClient voiceEngine;
        private string activeVoiceEngineTurnId = string.Empty;
        private float nextVoiceEnginePresence;
        private Type playerVoType;
        private MethodInfo playerVoSpeakObservation;
        private bool playerVoObservationChecked;

        private object lastTopWindow;
        private bool npcvoOwnershipReflectionChecked;
        private PropertyInfo npcvoQuestOwnershipProperty;
        private DaggerfallMessageBox activeModalBox;
        private readonly Queue<NarrationItem> narrationQueue = new Queue<NarrationItem>();
        // Tracks complete narration groups (one source message can be split into several low-latency chunks).
        // This prevents the same DFU popup from being re-captured while later chunks are still queued/playing.
        private readonly Dictionary<string, int> narrationGroupCounts = new Dictionary<string, int>();
        private bool queueRoutineRunning;
        private bool skipRequested;
        private bool temporarilyDisabled;
        private NarrationItem activeItem;

        private object lastBookWindow;
        private bool bookWasTopLastFrame;
        private string lastBookTextHash = string.Empty;
        private object lastBiographyWindow;
        private string lastBiographyQuestionHash = string.Empty;
        private object lastClassQuestionWindow;
        private string lastClassQuestionHash = string.Empty;
        private object lastQuestJournalWindow;
        private string lastQuestJournalTextHash = string.Empty;
        private object lastHistoryWindow;
        private string lastHistoryTextHash = string.Empty;
        private string lastCharacterCreationPopupHash = string.Empty;
        private bool wasPlayingGame;
        private object observedPlayerEntityToken;

        // Manual Read/Stop controls for document-style windows. These are native DFU Buttons
        // added only to the specific window instance and never patch the global Options UI.
        private readonly Dictionary<IUserInterfaceWindow, ReadButtonContext> readButtonContexts =
            new Dictionary<IUserInterfaceWindow, ReadButtonContext>();

        // DFU HUD notifications (DaggerfallUI.AddHUDText) such as "You are rested."
        // We only react to newly-added PopupText rows and keep combat suppression separate
        // from the popup/quest classifier to avoid turning the combat log into speech spam.
        private readonly HashSet<TextLabel> previousHudPopupRows = new HashSet<TextLabel>();
        private readonly Dictionary<string, float> recentHudNarrationTimes = new Dictionary<string, float>();
        private string lastMidScreenText = string.Empty;

        private float nextCacheCleanupRealtime;
        private bool cacheCleanupRunning;

        private string rootDir;
        private string cacheDir;
        private string packagedAudioDir;
        private string configPath;
        private string pronunciationPath;
        private string textFilterPath;
        private TextFilterRules textFilters;

        // Optional Climates & Calories integration is reflection-only so the mod remains optional.
        private bool climatesCaloriesReflectionChecked;
        private FieldInfo climatesCaloriesInfoBoxField;

        private string activeText = string.Empty;
        private List<string> activeSubtitlePages = new List<string>();
        private int activeSubtitlePage;
        private float subtitleAlpha;
        private float subtitleTargetAlpha;
        private float subtitleFadeSpeed = 10f;
        private float subtitleAudioStartRealtime;
        private float subtitleAudioLength;
        private bool subtitleAudioPlaying;

        private string activeSourceKey = string.Empty;
        private string recentlyCompletedSourceKey = string.Empty;
        private float recentlyCompletedSourceTime;
        private readonly Dictionary<string, float> recentlyQueuedKeys = new Dictionary<string, float>();

        private GUIStyle subtitleStyle;
        private GUIStyle subtitleBackgroundStyle;
        private Texture2D subtitleBackgroundTexture;
        private Panel classicSubtitlePanel;
        private MultiFormatTextLabel classicSubtitleLabel;
        private string classicSubtitleRenderedText = string.Empty;

        private static readonly FieldInfo MessageLabelField = typeof(DaggerfallMessageBox)
            .GetField("label", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo MessageButtonsField = typeof(DaggerfallMessageBox)
            .GetField("buttons", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo MessagePanelField = typeof(DaggerfallMessageBox)
            .GetField("messagePanel", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo BookLabelsField = typeof(DaggerfallBookReaderWindow)
            .GetField("bookLabels", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo ClassQuestionLabelField = typeof(CreateCharClassQuestions)
            .GetField("questionLabel", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo QuestLogLabelField = typeof(DaggerfallQuestJournalWindow)
            .GetField("questLogLabel", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo QuestLogMainPanelField = typeof(DaggerfallQuestJournalWindow)
            .GetField("mainPanel", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo HistoryPageLabelsField = typeof(DaggerfallPlayerHistoryWindow)
            .GetField("pageLabels", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo BiographyQuestionLabelsField = typeof(CreateCharBiography)
            .GetField("questionLabels", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo BiographyFileField = typeof(CreateCharBiography)
            .GetField("biogFile", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo BiographyQuestionIndexField = typeof(CreateCharBiography)
            .GetField("questionIndex", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo UiWindowStackField = typeof(UserInterfaceManager)
            .GetField("windows", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo PopupTextRowsField = typeof(PopupText)
            .GetField("textRows", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo MidScreenTextLabelField = typeof(DaggerfallHUD)
            .GetField("midScreenTextLabel", BindingFlags.Instance | BindingFlags.NonPublic);

        [Invoke(StateManager.StateTypes.Start, 0)]
        public static void Init(InitParams initParams)
        {
            mod = initParams.Mod;
            GameObject go = new GameObject(mod.Title);
            DontDestroyOnLoad(go);
            go.AddComponent<DaggerfallNarratorMod>();
            mod.IsReady = true;
            UnityEngine.Debug.Log("[Daggerfall Narrator] Initialized Dungeon Master v1.1.4.");
        }

        private void Awake()
        {
            instance = this;

            audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
            audioSource.spatialBlend = 0f;
            audioSource.loop = false;

            rootDir = Path.Combine(Application.persistentDataPath, "DaggerfallNarrator");
            cacheDir = Path.Combine(rootDir, "Cache");
            packagedAudioDir = Path.Combine(rootDir, "VoicePack");
            configPath = Path.Combine(rootDir, "DaggerfallNarrator.ini");
            pronunciationPath = Path.Combine(rootDir, "Pronunciations.txt");
            textFilterPath = Path.Combine(rootDir, "TextFilters.txt");

            Directory.CreateDirectory(rootDir);
            Directory.CreateDirectory(cacheDir);
            Directory.CreateDirectory(packagedAudioDir);

            LoadSettings();
            ScheduleNextCacheCleanup(config.CacheCleanupOnStartup ? 3f : Mathf.Max(60f, config.CacheCleanupIntervalMinutes * 60f));

            // Use DFU's official mod-settings UI. Follow DFU's documented live-settings pattern:
            // assign the callback, then call LoadSettings() once so future in-game changes are pushed
            // back into the running mod. v1.0.2 assigned the callback but skipped this registration step.
            if (mod != null && mod.HasSettings)
            {
                mod.LoadSettingsCallback = NativeSettingsChanged;
                try
                {
                    mod.LoadSettings();
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning("[Daggerfall Narrator] Live settings registration failed: " + ex.Message);
                }
            }

            RegisterConsoleCommands();

            voiceEngine = new DaggerfallVoiceEngineClient(this, "DungeonMaster", "1.1.4",
                delegate { return config == null ? 5000 : config.KokoroPort; }, delegate { return config == null || config.AutoStartVoiceEngine; }, 0f);
            if (config.Backend == TtsBackend.Kokoro) StartCoroutine(voiceEngine.EnsureRunning());
            else if (config.AutoStartTtsServer) TryStartTtsServer();
        }

        private void OnDestroy()
        {
            if (voiceEngine != null)
            {
                if (!string.IsNullOrEmpty(activeVoiceEngineTurnId))
                    StartCoroutine(voiceEngine.CancelTurn(activeVoiceEngineTurnId));
                StartCoroutine(voiceEngine.CancelModule());
            }
            if (instance == this)
                instance = null;

            if (config != null && config.StopAutoStartedTtsOnExit && ttsServerProcess != null)
            {
                try
                {
                    if (!ttsServerProcess.HasExited)
                        ttsServerProcess.Kill();
                }
                catch { }
            }

            if (subtitleBackgroundTexture != null)
                Destroy(subtitleBackgroundTexture);
        }

        private void LoadSettings()
        {
            config = NarratorConfig.LoadOrCreate(configPath);
            pronunciations = PronunciationDictionary.LoadOrCreate(pronunciationPath);
            textFilters = TextFilterRules.LoadOrCreate(textFilterPath);

            // Common user-facing options live in DFU's native Mod Settings screen.
            // Those values intentionally override matching INI keys. The INI remains
            // available for advanced/debug/backend-path settings.
            if (mod != null && mod.HasSettings)
            {
                try
                {
                    ApplyNativeSettings(mod.GetSettings(), false);
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning("[Daggerfall Narrator] Could not load native mod settings; using INI values: " + ex.Message);
                }
            }

            ApplyRuntimeSettings();

            UnityEngine.Debug.Log("[Daggerfall Narrator] Config: " + configPath);
            UnityEngine.Debug.Log("[Daggerfall Narrator] Pronunciations: " + pronunciationPath);
            UnityEngine.Debug.Log("[Daggerfall Narrator] Text filters: " + textFilterPath);
        }

        private void NativeSettingsChanged(ModSettings settings, ModSettingsChange change)
        {
            if (config == null || settings == null)
                return;

            ApplyNativeSettings(settings, true);
            ApplyRuntimeSettings();
            UnityEngine.Debug.Log("[Daggerfall Narrator] Native mod settings applied.");
        }

        private void ApplyNativeSettings(ModSettings settings, bool persistIni)
        {
            bool wasEnabled = config.Enabled;

            config.Enabled = settings.GetValue<bool>("General", "Enabled");
            config.Mode = settings.GetValue<int>("General", "Mode") == 1 ? NarratorMode.Pause : NarratorMode.Immersive;
            config.ReplaceEligiblePopups = settings.GetValue<bool>("General", "ReplaceEligiblePopups");
            int timing = settings.GetValue<int>("General", "MessageBoxTiming");
            config.MessageTiming = timing == 1 ? MessageTimingMode.HoldUntilSpeechStarts :
                (timing == 2 ? MessageTimingMode.HoldUntilSpeechEnds : MessageTimingMode.FollowWindow);

            bool flavor = settings.GetValue<bool>("Narration", "NarrateFlavorText");
            config.NarrateGenericQuestMessages = flavor;
            config.NarrateNotifyMessages = flavor;
            config.NarrateUnclassifiedFlavorPopups = flavor;
            int bookMode = settings.GetValue<int>("Readables", "Books");
            config.BookReadingMode = (ReadableMode)Mathf.Clamp(bookMode, 0, 2);
            int noteMode = settings.GetValue<int>("Readables", "QuestNotes");
            config.QuestNoteReadingMode = (ReadableMode)Mathf.Clamp(noteMode, 0, 2);
            int questLogMode = settings.GetValue<int>("Readables", "QuestLog");
            config.QuestLogReadingMode = (ReadableMode)Mathf.Clamp(questLogMode, 0, 2);
            int historyMode = settings.GetValue<int>("Readables", "History");
            config.HistoryReadingMode = (ReadableMode)Mathf.Clamp(historyMode, 0, 2);
            config.NarrateBooks = config.BookReadingMode != ReadableMode.Off;
            config.NarrateQuestItemNotes = config.QuestNoteReadingMode != ReadableMode.Off;
            config.NarrateCharacterCreationQuestions = settings.GetValue<bool>("Narration", "NarrateCharacterCreationQuestions");
            config.NarrateCharacterCreationDescriptions = settings.GetValue<bool>("Narration", "NarrateCharacterCreationDescriptions");
            config.NarrateTutorialMessages = settings.GetValue<bool>("Narration", "NarrateTutorialMessages");
            config.NarrateOpeningNarration = settings.GetValue<bool>("Narration", "NarrateOpeningNarration");
            config.NarratePlayerStatusPopups = settings.GetValue<bool>("Narration", "NarratePlayerStatusPopups");
            config.NarrateLevelingInspiration = settings.GetValue<bool>("Narration", "NarrateLevelingInspiration");
            config.NarrateHudStatusMessages = settings.GetValue<bool>("Narration", "NarrateHUDStatusMessages");
            config.NarrateClimatesCalories = settings.GetValue<bool>("Narration", "NarrateClimatesCalories");
            config.ObservationVoice = Mathf.Clamp(settings.GetValue<int>("Narration", "ObservationVoice"), 0, 2);
            // Combat-log narration is intentionally hard-disabled and is not a user option.
            config.SuppressHudStatusDuringCombat = true;

            config.ShowSubtitles = settings.GetValue<bool>("Subtitles", "ShowSubtitles");
            config.ShowNarratorLabel = settings.GetValue<bool>("Subtitles", "ShowNarratorLabel");
            int subtitlePosition = settings.GetValue<int>("Subtitles", "SubtitlePosition");
            config.SubtitlePosition = subtitlePosition == 1 ? SubtitlePosition.TopCenter :
                (subtitlePosition == 2 ? SubtitlePosition.Center : SubtitlePosition.BottomCenter);
            config.SubtitleFontSize = Mathf.Clamp(settings.GetValue<int>("Subtitles", "SubtitleFontSize"), 10, 48);
            int subtitleStyleMode = settings.GetValue<int>("Subtitles", "SubtitleStyle");
            config.SubtitleStyle = subtitleStyleMode == 1 ? SubtitleStyleMode.ClassicShadowed :
                (subtitleStyleMode == 2 ? SubtitleStyleMode.ClassicBackdrop : SubtitleStyleMode.Modern);

            // Native UI is Kokoro-first. Piper remains an INI-only legacy fallback, but normal
            // framework configuration always uses the Daggerfall Voice Engine service.
            config.Backend = TtsBackend.Kokoro;
            config.VoiceAccent = Mathf.Clamp(settings.GetValue<int>("Voice", "Accent"), 0, 1);
            config.VoiceGender = Mathf.Clamp(settings.GetValue<int>("Voice", "Gender"), 0, 1);
            config.VoiceDelivery = Mathf.Clamp(settings.GetValue<int>("Voice", "Delivery"), 0, 4);
            config.VoiceDepth = Mathf.Clamp(settings.GetValue<int>("Voice", "Depth"), 0, 100);
            string voiceOverride = settings.GetValue<string>("Voice", "VoiceOverride");
            config.KokoroVoiceOverride = string.IsNullOrWhiteSpace(voiceOverride) ? string.Empty : voiceOverride.Trim();
            config.KokoroVoice = ResolveCuratedEnglishVoice(config.VoiceAccent, config.VoiceGender, config.VoiceDelivery);
            config.KokoroSpeed = Mathf.Clamp(settings.GetValue<int>("Voice", "Speed") / 100f, 0.5f, 1.5f);
            config.AutoStartVoiceEngine = settings.GetValue<bool>("Voice", "AutoStartVoiceEngine");
            int audioStyle = settings.GetValue<int>("Voice", "AudioStyle");
            config.KokoroAudioStylePreset = audioStyle == 1 ? KokoroAudioStyle.Cdrom :
                (audioStyle == 2 ? KokoroAudioStyle.Dos : KokoroAudioStyle.Clean);
            config.Volume = Mathf.Clamp01(settings.GetValue<int>("Voice", "Volume") / 100f);
            config.UseGameSoundVolume = settings.GetValue<bool>("Voice", "UseGameSoundVolume");

            config.AutoManageCache = settings.GetValue<bool>("Cache", "AutoManageCache");
            config.CacheCleanupOnStartup = settings.GetValue<bool>("Cache", "CleanupOnStartup");
            int cacheSizeChoice = settings.GetValue<int>("Cache", "MaxCacheSize");
            switch (cacheSizeChoice)
            {
                case 0: config.MaxCacheSizeMB = 250; break;
                case 1: config.MaxCacheSizeMB = 500; break;
                case 3: config.MaxCacheSizeMB = 2048; break;
                case 4: config.MaxCacheSizeMB = 5120; break;
                case 5: config.MaxCacheSizeMB = 0; break; // Unlimited
                default: config.MaxCacheSizeMB = 1024; break;
            }

            if (persistIni)
                config.Save(configPath);

            if (wasEnabled && !config.Enabled)
            {
                ClearQueuedNarration();
                skipRequested = true;
                if (audioSource != null)
                    audioSource.Stop();
                activeText = string.Empty;
                activeSubtitlePages.Clear();
                subtitleAlpha = 0f;
                subtitleTargetAlpha = 0f;
            }
        }

        private static string ResolveCuratedEnglishVoice(int accent, int gender, int delivery)
        {
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

        private string GetEffectiveKokoroVoice()
        {
            if (config == null) return "bm_fable";
            return string.IsNullOrWhiteSpace(config.KokoroVoiceOverride) ? config.KokoroVoice : config.KokoroVoiceOverride.Trim();
        }

        private float GetVoiceDepthSemitones()
        {
            return Mathf.Clamp((50 - config.VoiceDepth) / 20f, -2.5f, 2.5f);
        }

        private void ApplyRuntimeSettings()
        {
            questClassifier = new QuestSourceClassifier(config);
            if (audioSource != null)
                audioSource.volume = EffectiveVolume();
            ScheduleNextCacheCleanup(config.CacheCleanupOnStartup ? 3f : Mathf.Max(60f, config.CacheCleanupIntervalMinutes * 60f));
            subtitleStyle = null;
            subtitleBackgroundStyle = null;
            classicSubtitleRenderedText = string.Empty;
            if (classicSubtitlePanel != null)
                classicSubtitlePanel.Enabled = false;
            if (subtitleBackgroundTexture != null)
            {
                Destroy(subtitleBackgroundTexture);
                subtitleBackgroundTexture = null;
            }
        }

        private float EffectiveVolume()
        {
            float volume = Mathf.Clamp01(config.Volume);
            if (config.UseGameSoundVolume)
                volume *= Mathf.Clamp01(DaggerfallUnity.Settings.SoundVolume);
            return volume;
        }

        private void Update()
        {
            if (voiceEngine != null && config != null && config.Backend == TtsBackend.Kokoro && Time.realtimeSinceStartup >= nextVoiceEnginePresence)
            {
                nextVoiceEnginePresence = Time.realtimeSinceStartup + 20f;
                StartCoroutine(voiceEngine.PulsePresence());
            }
            if (config == null)
                return;

            object playerEntityToken = GameManager.Instance == null ? null : (object)GameManager.Instance.PlayerEntity;
            if (!ReferenceEquals(playerEntityToken, observedPlayerEntityToken))
            {
                HandlePlayerEntityTransition(playerEntityToken);
                observedPlayerEntityToken = playerEntityToken;
            }

            if (config.ToggleKey != KeyCode.None && Input.GetKeyDown(config.ToggleKey))
            {
                temporarilyDisabled = !temporarilyDisabled;
                UnityEngine.Debug.Log("[Daggerfall Narrator] Runtime toggle: " + (!temporarilyDisabled));
                if (temporarilyDisabled)
                {
                    ClearQueuedNarration();
                    skipRequested = true;
                }
            }

            if (config.SkipKey != KeyCode.None && Input.GetKeyDown(config.SkipKey))
                skipRequested = true;

            UpdateSubtitleFade();
            UpdateSubtitlePage();
            UpdateClassicSubtitleOverlay();
            MaybeRunScheduledCacheCleanup();
            CleanupReadButtons();
            UpdateReadButtonLabels();

            bool playingNow = GameManager.Instance != null && GameManager.Instance.IsPlayingGame();
            if (wasPlayingGame && !playingNow)
                StopAllNarrationForContextChange();
            wasPlayingGame = playingNow;

            if (!config.Enabled || temporarilyDisabled || DaggerfallUI.UIManager == null)
                return;

            // HUD status messages are not DaggerfallMessageBox windows. DFU routes them through
            // DaggerfallHUD.PopupText, so inspect that stream separately. This is intentionally
            // limited to PopupText and does not scrape arbitrary UI text/combat-log panels.
            HandleHudStatusNotifications();
            HandleMidScreenWorldFlavor();

            object top = DaggerfallUI.UIManager.TopWindow;
            if (top == null)
            {
                if (bookWasTopLastFrame && config.StopBookNarrationOnClose)
                    CancelCategory(NarrationCategory.Book);
                lastTopWindow = null;
                bookWasTopLastFrame = false;
                lastBookWindow = null;
                lastBiographyWindow = null;
                lastClassQuestionWindow = null;
                lastQuestJournalWindow = null;
                lastHistoryWindow = null;
                lastCharacterCreationPopupHash = string.Empty;
                return;
            }

            // Books and the biography questionnaire stay on-screen. We only listen to their
            // rendered text and never replace, close, disable, or otherwise modify those UIs.
            HandleReadableWindows(top);

            // Message-box narration only needs to inspect a newly presented top window.
            // The hidden pause-mode modal is the one exception because it remains on top.
            if (ReferenceEquals(top, lastTopWindow) && !ReferenceEquals(top, activeModalBox))
                return;

            lastTopWindow = top;

            DaggerfallMessageBox box = top as DaggerfallMessageBox;
            if (box == null)
            {
                lastCharacterCreationPopupHash = string.Empty;
                return;
            }

            // Don't recapture the popup currently being used as the hidden pause-mode modal.
            if (ReferenceEquals(box, activeModalBox))
                return;

            string renderedText = NormalizeForSpeech(ExtractText(box));
            if (string.IsNullOrWhiteSpace(renderedText))
                return;

            // Hard safety filters apply to every message-box path, including explicitly supported
            // integrations. Combat-resolution text and mode-toggle notices are never narrator
            // content, and user BLOCK rules are honored before any special-case detector.
            if (IsLikelyCombatHudText(renderedText) || IsModeToggleHudText(renderedText))
                return;
            if (textFilters != null && textFilters.IsBlocked(renderedText))
                return;

            // The classic new-game Privateer's Hold opening narrative is a standalone startup
            // message rather than a quest tutorial page. Treat it as flavor explicitly so it is
            // narrated even when conservative fallback rules would otherwise reject it.
            if (config.NarrateOpeningNarration && IsOpeningNarrationPopup(renderedText))
            {
                ClassificationResult opening = new ClassificationResult();
                opening.Decision = NarrationDecision.NarrateQuestFlavor;
                opening.NarrationText = renderedText;
                opening.SourceKey = "opening:" + Sha1(NormalizeForMatch(renderedText));
                opening.PreserveOriginalPopup = !config.ReplaceEligiblePopups;
                opening.Category = NarrationCategory.Flavor;
                opening.ShowSubtitle = true;
                HandleEligiblePopup(box, opening);
                return;
            }

            // Building/door interactions can present a real DaggerfallMessageBox even though no
            // conversational NPC owns it (for example, "Someone calls out, come in!"). Preserve the
            // native textbox and narrate a deliberately narrow set of world-interaction messages.
            if (config.NarrateUnclassifiedFlavorPopups && IsNarratableWorldFlavorPopup(renderedText) && !IsNpcvoQuestDialogueOwnershipActive())
            {
                HandlePreservedPopup(box, renderedText, "worldpopup:", NarrationCategory.Flavor, true, false);
                return;
            }

            // DFU 1.1's tutorial and some quest/system instructions use Questing Actions
            // Prompt/PromptMulti message boxes without an NPC speaker. NPCVO deliberately leaves these
            // alone; Dungeon Master preserves the native choices and reads the prompt as system narration.
            if (IsQuestScriptPromptMessageBox(box) && !IsNpcvoQuestDialogueOwnershipActive())
            {
                HandlePreservedPopup(box, renderedText, "questprompt:", NarrationCategory.Flavor, true);
                return;
            }

            // Medical/status readouts such as "You are healthy" are short message boxes and can
            // fall below the normal flavor minimum-character threshold. Keep their UI intact and
            // narrate them through an explicit status path.
            if (config.NarratePlayerStatusPopups && IsPlayerStatusPopup(renderedText))
            {
                HandlePreservedPopup(box, renderedText, "playerstatus:", NarrationCategory.HudStatus, false);
                return;
            }

            // Climates & Calories adds a third status popup to the normal I-key readout. Detect
            // its own message box by reflection so the compatibility remains optional.
            if (config.NarrateClimatesCalories && IsClimatesCaloriesPopup(box))
            {
                HandlePreservedPopup(box, renderedText, "calories:", NarrationCategory.HudStatus, false);
                return;
            }

            // Character-creation descriptions (notably home-province/race descriptions and
            // class descriptions) use interactive Yes/No message boxes. Narrate them while
            // leaving the original UI completely intact. This path intentionally runs before
            // the generic button/text filters that protect normal gameplay prompts.
            if (config.NarrateCharacterCreationDescriptions && IsCharacterCreationInfoPopup(box))
            {
                HandleCharacterCreationInfoPopup(box, renderedText);
                return;
            }

            if (!PassesTextFilter(renderedText))
                return;

            ClassificationResult result = questClassifier.Classify(box, renderedText);

            // NPCVO owns the short tail immediately after a quest-giver exchange. Generic quest
            // Message: records can otherwise look like narrator flavor even though they are the NPC's
            // next spoken sentence. Honor that ownership without guessing from first-person prose.
            if (IsNpcvoQuestDialogueOwnershipActive() &&
                (result.Decision == NarrationDecision.NarrateQuestFlavor || result.Decision == NarrationDecision.Unclassified))
            {
                result.Decision = NarrationDecision.BlockedNpcQuestOwnership;
                DebugDecision(renderedText, result.Decision, result.SourceKey);
                return;
            }

            bool shouldNarrate = false;

            if (result.Decision == NarrationDecision.NarrateQuestFlavor ||
                result.Decision == NarrationDecision.NarrateQuestReadableItem)
            {
                shouldNarrate = true;
            }
            else if (result.Decision == NarrationDecision.Unclassified)
            {
                shouldNarrate = config.NarrateUnclassifiedFlavorPopups && PassesUiFlavorHeuristic(box);
                if (shouldNarrate)
                {
                    result.NarrationText = renderedText;
                    result.SourceKey = "popup:" + Sha1(NormalizeForMatch(renderedText));
                }
            }

            DebugDecision(renderedText, result.Decision, result.SourceKey);

            if (!shouldNarrate)
                return;

            if (result.Decision == NarrationDecision.NarrateQuestReadableItem)
            {
                if (config.QuestNoteReadingMode == ReadableMode.Off)
                    return;
                if (config.QuestNoteReadingMode == ReadableMode.Manual)
                {
                    EnsureReadButton(box, box.NativePanel, ReadableKind.QuestNote, NarrationCategory.QuestNote, new Rect(132, 187, 48, 10));
                    return;
                }
            }

            HandleEligiblePopup(box, result);
        }

        private void HandleHudStatusNotifications()
        {
            if (!config.NarrateHudStatusMessages || DaggerfallUI.Instance == null || DaggerfallUI.Instance.DaggerfallHUD == null || PopupTextRowsField == null)
                return;

            PopupText popup = DaggerfallUI.Instance.DaggerfallHUD.PopupText;
            if (popup == null)
                return;

            IEnumerable rows;
            try { rows = PopupTextRowsField.GetValue(popup) as IEnumerable; }
            catch { return; }
            if (rows == null)
                return;

            HashSet<TextLabel> currentRows = new HashSet<TextLabel>();
            List<TextLabel> newRows = new List<TextLabel>();
            foreach (object raw in rows)
            {
                TextLabel label = raw as TextLabel;
                if (label == null)
                    continue;
                currentRows.Add(label);
                if (!previousHudPopupRows.Contains(label))
                    newRows.Add(label);
            }

            previousHudPopupRows.Clear();
            foreach (TextLabel label in currentRows)
                previousHudPopupRows.Add(label);

            if (newRows.Count == 0)
                return;

            bool enemyAlert = GameManager.Instance != null && GameManager.Instance.PlayerEntity != null && GameManager.Instance.PlayerEntity.EnemyAlertActive;
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < newRows.Count; i++)
            {
                string text = NormalizeForSpeech(newRows[i].Text);
                if (string.IsNullOrWhiteSpace(text) || text.Length < config.HudStatusMinimumCharacters)
                    continue;

                // Hard safety boundary: combat-resolution text is never narratable, regardless of
                // INI rules or any compatibility module. There is deliberately no option to disable this.
                if (IsLikelyCombatHudText(text))
                {
                    if (config.DebugClassification)
                        UnityEngine.Debug.Log("[Daggerfall Narrator] Hard-blocked combat HUD text: " + text);
                    continue;
                }

                if (IsModeToggleHudText(text))
                    continue;

                bool observation = IsObservationHudText(text);
                if (enemyAlert && !observation)
                    continue;

                if (textFilters != null && textFilters.IsBlocked(text))
                    continue;

                bool allowed = IsNarratableHudStatus(text) || observation || IsNarratableWorldFlavorHudText(text) ||
                    (textFilters != null && textFilters.IsAllowed(text));
                if (!allowed)
                    continue;

                string normalized = NormalizeForMatch(text);
                string sourceKey = "hud:" + Sha1(normalized);
                float previousTime;
                if (recentHudNarrationTimes.TryGetValue(sourceKey, out previousTime) && now - previousTime < config.HudStatusRepeatCooldownSeconds)
                    continue;

                recentHudNarrationTimes[sourceKey] = now;
                if (observation && config.ObservationVoice == 2)
                    continue;
                if (observation && config.ObservationVoice == 1 && TryRouteObservationToPlayer(text))
                    continue;
                NarrationItem item = new NarrationItem();
                item.Text = text;
                item.SourceKey = sourceKey;
                item.Category = NarrationCategory.HudStatus;
                item.ShowSubtitle = config.ShowSubtitlesForHudStatus;
                Enqueue(item);
            }

            if (recentHudNarrationTimes.Count > 256)
            {
                List<string> stale = new List<string>();
                foreach (KeyValuePair<string, float> pair in recentHudNarrationTimes)
                    if (now - pair.Value > Mathf.Max(60f, config.HudStatusRepeatCooldownSeconds * 4f))
                        stale.Add(pair.Key);
                for (int i = 0; i < stale.Count; i++)
                    recentHudNarrationTimes.Remove(stale[i]);
            }
        }


        private void HandleMidScreenWorldFlavor()
        {
            if (!config.NarrateHudStatusMessages || DaggerfallUI.Instance == null || DaggerfallUI.Instance.DaggerfallHUD == null || MidScreenTextLabelField == null)
                return;

            TextLabel label;
            try { label = MidScreenTextLabelField.GetValue(DaggerfallUI.Instance.DaggerfallHUD) as TextLabel; }
            catch { return; }
            if (label == null) return;

            string text = NormalizeForSpeech(label.Text);
            if (string.IsNullOrWhiteSpace(text))
            {
                lastMidScreenText = string.Empty;
                return;
            }

            // SetMidScreenText can stay visible for multiple frames. Only inspect a new rendered string;
            // the normal HUD cooldown below also protects against repeated clicks on the same lock.
            if (string.Equals(text, lastMidScreenText, StringComparison.Ordinal))
                return;
            lastMidScreenText = text;

            if (!IsNarratableWorldFlavorHudText(text) || IsLikelyCombatHudText(text) || IsModeToggleHudText(text))
                return;
            if (textFilters != null && textFilters.IsBlocked(text))
                return;

            string sourceKey = "midscreen:" + Sha1(NormalizeForMatch(text));
            float now = Time.realtimeSinceStartup;
            float previousTime;
            if (recentHudNarrationTimes.TryGetValue(sourceKey, out previousTime) && now - previousTime < config.HudStatusRepeatCooldownSeconds)
                return;
            recentHudNarrationTimes[sourceKey] = now;

            NarrationItem item = new NarrationItem();
            item.Text = text;
            item.SourceKey = sourceKey;
            item.Category = NarrationCategory.HudStatus;
            // The original mid-screen subtitle remains visible, so do not draw a second Narrator subtitle.
            item.ShowSubtitle = false;
            Enqueue(item);
        }


        private bool IsNpcvoQuestDialogueOwnershipActive()
        {
            if (!npcvoOwnershipReflectionChecked || npcvoQuestOwnershipProperty == null)
            {
                npcvoOwnershipReflectionChecked = true;
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        Type type = assembly.GetType("NPCVO.NPCVOMod", false);
                        if (type == null) continue;
                        npcvoQuestOwnershipProperty = type.GetProperty("QuestDialogueOwnershipActive",
                            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        if (npcvoQuestOwnershipProperty != null) break;
                    }
                    catch { }
                }
            }

            if (npcvoQuestOwnershipProperty == null)
                return false;
            try
            {
                object value = npcvoQuestOwnershipProperty.GetValue(null, null);
                return value is bool && (bool)value;
            }
            catch { return false; }
        }

        private void HandleReadableWindows(object top)
        {
            DaggerfallBookReaderWindow book = top as DaggerfallBookReaderWindow;
            if (book != null)
            {
                bool enteredBook = !bookWasTopLastFrame || !ReferenceEquals(lastBookWindow, book);
                bookWasTopLastFrame = true;
                lastBookWindow = book;
                string bookText = NormalizeForSpeech(ExtractBookText(book));

                if (config.BookReadingMode == ReadableMode.Manual)
                    EnsureReadButton(book, book.NativePanel, ReadableKind.Book, NarrationCategory.Book, new Rect(132, 187, 48, 10));
                else if (config.BookReadingMode == ReadableMode.Auto && !string.IsNullOrWhiteSpace(bookText))
                {
                    string hash = Sha1(NormalizeForMatch(bookText));
                    if (enteredBook || hash != lastBookTextHash)
                    {
                        lastBookTextHash = hash;
                        ReplaceCategoryWithStreamingChunks(bookText, "book:" + hash, NarrationCategory.Book,
                            config.ShowSubtitlesForBooks, config.ReadableChunkCharacters, book);
                    }
                }
            }
            else
            {
                if (bookWasTopLastFrame && config.StopBookNarrationOnClose)
                    CancelCategory(NarrationCategory.Book);
                bookWasTopLastFrame = false;
                lastBookWindow = null;
            }

            // Native Warrior/Mage/Rogue constellation questionnaire. Do not require it to be
            // TopWindow: UI overhauls/overlays can sit above the vanilla questionnaire while the
            // original CreateCharClassQuestions window remains on DFU's window stack.
            object classQuestions = FindClassQuestionWindow(top);
            if (classQuestions != null && config.NarrateCharacterCreationQuestions)
            {
                string question = NormalizeForSpeech(ExtractClassQuestion(classQuestions));
                if (!string.IsNullOrWhiteSpace(question))
                {
                    string hash = Sha1(NormalizeForMatch(question));
                    if (!ReferenceEquals(lastClassQuestionWindow, classQuestions) || hash != lastClassQuestionHash)
                    {
                        lastClassQuestionWindow = classQuestions;
                        lastClassQuestionHash = hash;
                        NarrationItem item = new NarrationItem();
                        item.Text = question;
                        item.SourceKey = "classquestion:" + hash;
                        item.Category = NarrationCategory.CharacterQuestion;
                        item.ShowSubtitle = config.ShowSubtitlesForCharacterQuestions;
                        item.BoundWindow = classQuestions as IUserInterfaceWindow;
                        item.CancelIfWindowClosed = item.BoundWindow != null;
                        EnqueueReplacingCategoryStreaming(item, config.FirstChunkCharacters);
                        if (config.DebugClassification)
                            UnityEngine.Debug.Log("[Daggerfall Narrator] Class questionnaire detected: " + question);
                    }
                }
            }
            else
            {
                lastClassQuestionWindow = null;
                lastClassQuestionHash = string.Empty;
            }

            object biography = FindBiographyWindow(top);
            if (biography != null && classQuestions == null)
            {
                if (config.NarrateCharacterCreationQuestions)
                {
                    string question = NormalizeForSpeech(ExtractBiographyQuestion(biography));
                    if (!string.IsNullOrWhiteSpace(question))
                    {
                        string hash = Sha1(NormalizeForMatch(question));
                        if (!ReferenceEquals(lastBiographyWindow, biography) || hash != lastBiographyQuestionHash)
                        {
                            lastBiographyWindow = biography;
                            lastBiographyQuestionHash = hash;
                            NarrationItem item = new NarrationItem();
                            item.Text = question;
                            item.SourceKey = "biography:" + hash;
                            item.Category = NarrationCategory.CharacterQuestion;
                            item.ShowSubtitle = config.ShowSubtitlesForCharacterQuestions;
                            item.BoundWindow = biography as IUserInterfaceWindow;
                            item.CancelIfWindowClosed = item.BoundWindow != null;
                            EnqueueReplacingCategory(item);
                        }
                    }
                }
            }
            else if (classQuestions == null)
            {
                lastBiographyWindow = null;
                lastBiographyQuestionHash = string.Empty;
            }

            DaggerfallQuestJournalWindow journal = top as DaggerfallQuestJournalWindow;
            if (journal != null)
            {
                string text = NormalizeForSpeech(ExtractQuestJournalText(journal));
                string hash = string.IsNullOrWhiteSpace(text) ? string.Empty : Sha1(NormalizeForMatch(text));
                if (config.QuestLogReadingMode == ReadableMode.Manual)
                {
                    Panel mainPanel = QuestLogMainPanelField != null ? QuestLogMainPanelField.GetValue(journal) as Panel : journal.NativePanel;
                    EnsureReadButton(journal, mainPanel ?? journal.NativePanel, ReadableKind.QuestLog, NarrationCategory.QuestLog, new Rect(128, 187, 48, 10));
                    if (ReferenceEquals(lastQuestJournalWindow, journal) && !string.IsNullOrEmpty(lastQuestJournalTextHash) && hash != lastQuestJournalTextHash)
                        CancelCategory(NarrationCategory.QuestLog);
                }
                else if (config.QuestLogReadingMode == ReadableMode.Auto && !string.IsNullOrWhiteSpace(text) &&
                         (!ReferenceEquals(lastQuestJournalWindow, journal) || hash != lastQuestJournalTextHash))
                {
                    ReplaceCategoryWithStreamingChunks(text, "questlog:" + hash, NarrationCategory.QuestLog, false,
                        config.ReadableChunkCharacters, journal);
                }
                lastQuestJournalWindow = journal;
                lastQuestJournalTextHash = hash;
            }
            else
            {
                if (lastQuestJournalWindow != null)
                    CancelCategory(NarrationCategory.QuestLog);
                lastQuestJournalWindow = null;
                lastQuestJournalTextHash = string.Empty;
            }

            DaggerfallPlayerHistoryWindow history = top as DaggerfallPlayerHistoryWindow;
            if (history != null)
            {
                string text = NormalizeForSpeech(ExtractHistoryText(history));
                string hash = string.IsNullOrWhiteSpace(text) ? string.Empty : Sha1(NormalizeForMatch(text));
                if (config.HistoryReadingMode == ReadableMode.Manual)
                {
                    EnsureReadButton(history, history.NativePanel, ReadableKind.History, NarrationCategory.History, new Rect(128, 187, 48, 10));
                    if (ReferenceEquals(lastHistoryWindow, history) && !string.IsNullOrEmpty(lastHistoryTextHash) && hash != lastHistoryTextHash)
                        CancelCategory(NarrationCategory.History);
                }
                else if (config.HistoryReadingMode == ReadableMode.Auto && !string.IsNullOrWhiteSpace(text) &&
                         (!ReferenceEquals(lastHistoryWindow, history) || hash != lastHistoryTextHash))
                {
                    ReplaceCategoryWithStreamingChunks(text, "history:" + hash, NarrationCategory.History, false,
                        config.ReadableChunkCharacters, history);
                }
                lastHistoryWindow = history;
                lastHistoryTextHash = hash;
            }
            else
            {
                if (lastHistoryWindow != null)
                    CancelCategory(NarrationCategory.History);
                lastHistoryWindow = null;
                lastHistoryTextHash = string.Empty;
            }
        }


        private object FindClassQuestionWindow(object top)
        {
            if (top == null)
                return null;

            if (top is CreateCharClassQuestions)
                return top;

            try
            {
                UserInterfaceManager manager = DaggerfallUI.UIManager as UserInterfaceManager;
                if (manager != null && UiWindowStackField != null)
                {
                    IEnumerable stack = UiWindowStackField.GetValue(manager) as IEnumerable;
                    if (stack != null)
                    {
                        foreach (object window in stack)
                        {
                            if (window is CreateCharClassQuestions)
                                return window;
                            if (LooksLikeClassQuestionWindow(window))
                                return window;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (config != null && config.DebugClassification)
                    UnityEngine.Debug.LogWarning("[Daggerfall Narrator] Class-question stack scan failed: " + ex.Message);
            }

            return LooksLikeClassQuestionWindow(top) ? top : null;
        }

        private static bool LooksLikeClassQuestionWindow(object window)
        {
            if (window == null)
                return false;
            Type type = window.GetType();
            string name = (type.FullName ?? type.Name).ToLowerInvariant();
            if (!(name.Contains("classquestion") || (name.Contains("class") && name.Contains("question"))))
                return false;
            return FindFieldInHierarchy(type, "questionLabel") != null;
        }

        private string ExtractClassQuestion(object window)
        {
            try
            {
                if (window == null)
                    return string.Empty;
                FieldInfo field = window is CreateCharClassQuestions
                    ? ClassQuestionLabelField
                    : FindFieldInHierarchy(window.GetType(), "questionLabel");
                MultiFormatTextLabel label = field != null ? field.GetValue(window) as MultiFormatTextLabel : null;
                return ExtractMultiFormatText(label);
            }
            catch (Exception ex)
            {
                if (config != null && config.DebugClassification)
                    UnityEngine.Debug.LogWarning("[Daggerfall Narrator] Class-question extraction failed: " + ex.Message);
                return string.Empty;
            }
        }

        private string ExtractQuestJournalText(DaggerfallQuestJournalWindow window)
        {
            try
            {
                MultiFormatTextLabel label = QuestLogLabelField != null ? QuestLogLabelField.GetValue(window) as MultiFormatTextLabel : null;
                return ExtractMultiFormatText(label);
            }
            catch { return string.Empty; }
        }

        private string ExtractHistoryText(DaggerfallPlayerHistoryWindow window)
        {
            try
            {
                IEnumerable labels = HistoryPageLabelsField != null ? HistoryPageLabelsField.GetValue(window) as IEnumerable : null;
                return ExtractTextLabels(labels);
            }
            catch { return string.Empty; }
        }

        private static string ExtractMultiFormatText(MultiFormatTextLabel label)
        {
            if (label == null || label.TextLabels == null)
                return string.Empty;
            return ExtractTextLabels(label.TextLabels);
        }

        private static string ExtractTextLabels(IEnumerable labels)
        {
            if (labels == null)
                return string.Empty;
            StringBuilder sb = new StringBuilder();
            foreach (object raw in labels)
            {
                TextLabel label = raw as TextLabel;
                if (label == null || string.IsNullOrWhiteSpace(label.Text))
                    continue;
                if (sb.Length > 0)
                    sb.Append(' ');
                sb.Append(label.Text.Trim());
            }
            return sb.ToString();
        }

        private void EnsureReadButton(IUserInterfaceWindow window, Panel parent, ReadableKind kind, NarrationCategory category, Rect rect)
        {
            if (window == null || parent == null || readButtonContexts.ContainsKey(window))
                return;
            try
            {
                Button button = DaggerfallUI.AddButton(rect, parent);
                button.Name = "DaggerfallNarratorReadButton";
                button.Label.Text = "READ";
                button.Label.Font = DaggerfallUI.DefaultFont;
                button.OnMouseClick += ReadButton_OnMouseClick;
                ReadButtonContext context = new ReadButtonContext();
                context.Window = window;
                context.Button = button;
                context.Kind = kind;
                context.Category = category;
                readButtonContexts[window] = context;
            }
            catch (Exception ex)
            {
                if (config.DebugClassification)
                    UnityEngine.Debug.LogWarning("[Daggerfall Narrator] Could not add READ button: " + ex.Message);
            }
        }

        private void ReadButton_OnMouseClick(BaseScreenComponent sender, Vector2 position)
        {
            Button clicked = sender as Button;
            if (clicked == null)
                return;
            ReadButtonContext context = null;
            foreach (KeyValuePair<IUserInterfaceWindow, ReadButtonContext> pair in readButtonContexts)
            {
                if (ReferenceEquals(pair.Value.Button, clicked))
                {
                    context = pair.Value;
                    break;
                }
            }
            if (context == null)
                return;

            if (IsCategoryActiveForWindow(context.Category, context.Window))
            {
                CancelCategory(context.Category);
                return;
            }

            string text = NormalizeForSpeech(ExtractReadableText(context));
            if (string.IsNullOrWhiteSpace(text))
                return;
            string key = "read:" + context.Kind.ToString().ToLowerInvariant() + ":" + Sha1(NormalizeForMatch(text));
            ReplaceCategoryWithStreamingChunks(text, key, context.Category, false, config.ReadableChunkCharacters, context.Window);
        }

        private string ExtractReadableText(ReadButtonContext context)
        {
            if (context == null || context.Window == null)
                return string.Empty;
            switch (context.Kind)
            {
                case ReadableKind.Book: return ExtractBookText(context.Window as DaggerfallBookReaderWindow);
                case ReadableKind.QuestNote: return ExtractText(context.Window as DaggerfallMessageBox);
                case ReadableKind.QuestLog: return ExtractQuestJournalText(context.Window as DaggerfallQuestJournalWindow);
                case ReadableKind.History: return ExtractHistoryText(context.Window as DaggerfallPlayerHistoryWindow);
                default: return string.Empty;
            }
        }

        private bool IsCategoryActiveForWindow(NarrationCategory category, IUserInterfaceWindow window)
        {
            if (activeItem != null && activeItem.Category == category && ReferenceEquals(activeItem.BoundWindow, window))
                return true;
            foreach (NarrationItem item in narrationQueue)
                if (item != null && item.Category == category && ReferenceEquals(item.BoundWindow, window))
                    return true;
            return false;
        }

        private void UpdateReadButtonLabels()
        {
            foreach (KeyValuePair<IUserInterfaceWindow, ReadButtonContext> pair in readButtonContexts)
            {
                if (pair.Value != null && pair.Value.Button != null)
                    pair.Value.Button.Label.Text = IsCategoryActiveForWindow(pair.Value.Category, pair.Key) ? "STOP" : "READ";
            }
        }

        private void CleanupReadButtons()
        {
            if (DaggerfallUI.UIManager == null || readButtonContexts.Count == 0)
                return;
            List<IUserInterfaceWindow> stale = new List<IUserInterfaceWindow>();
            foreach (KeyValuePair<IUserInterfaceWindow, ReadButtonContext> pair in readButtonContexts)
            {
                try
                {
                    if (!DaggerfallUI.UIManager.ContainsWindow(pair.Key))
                        stale.Add(pair.Key);
                }
                catch { stale.Add(pair.Key); }
            }
            for (int i = 0; i < stale.Count; i++)
                readButtonContexts.Remove(stale[i]);
        }

        private string ExtractBookText(DaggerfallBookReaderWindow book)
        {
            try
            {
                if (BookLabelsField == null)
                    return string.Empty;
                object value = BookLabelsField.GetValue(book);
                IEnumerable labels = value as IEnumerable;
                if (labels == null)
                    return string.Empty;

                StringBuilder sb = new StringBuilder();
                foreach (object raw in labels)
                {
                    TextLabel label = raw as TextLabel;
                    if (label == null || string.IsNullOrWhiteSpace(label.Text))
                        continue;
                    string part = label.Text.Trim();
                    if (part.Length == 0)
                        continue;
                    if (sb.Length > 0)
                        sb.Append(' ');
                    sb.Append(part);
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[Daggerfall Narrator] Book text extraction failed: " + ex.Message);
                return string.Empty;
            }
        }

        private object FindBiographyWindow(object top)
        {
            if (top == null)
                return null;

            // Vanilla UI, or a UI mod subclassing the vanilla biography window.
            if (top is CreateCharBiography)
                return top;

            // Some UI replacers keep CreateCharBiography on the DFU window stack but put
            // their own overlay/replacement window above it. Search the stack instead of
            // requiring the biography screen to be TopWindow.
            try
            {
                UserInterfaceManager manager = DaggerfallUI.UIManager as UserInterfaceManager;
                if (manager != null && UiWindowStackField != null)
                {
                    IEnumerable stack = UiWindowStackField.GetValue(manager) as IEnumerable;
                    if (stack != null)
                    {
                        foreach (object window in stack)
                        {
                            if (window is CreateCharBiography)
                                return window;

                            // Compatibility fallback for UI mods that replace rather than
                            // subclass CreateCharBiography but retain a questionLabels field.
                            if (LooksLikeBiographyWindow(window))
                                return window;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (config != null && config.DebugClassification)
                    UnityEngine.Debug.LogWarning("[Daggerfall Narrator] Biography stack scan failed: " + ex.Message);
            }

            // Last chance: the top window itself might be a replacement with the expected field.
            return LooksLikeBiographyWindow(top) ? top : null;
        }

        private static bool LooksLikeBiographyWindow(object window)
        {
            if (window == null)
                return false;

            Type type = window.GetType();
            string name = type.FullName ?? type.Name;
            bool biographyName = name.IndexOf("biograph", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 name.IndexOf("biog", StringComparison.OrdinalIgnoreCase) >= 0;
            return biographyName && FindFieldInHierarchy(type, "questionLabels") != null;
        }

        private static FieldInfo FindFieldInHierarchy(Type type, string fieldName)
        {
            while (type != null)
            {
                FieldInfo field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null)
                    return field;
                type = type.BaseType;
            }
            return null;
        }

        private string ExtractBiographyQuestion(object biography)
        {
            try
            {
                if (biography == null)
                    return string.Empty;

                // Prefer the underlying BiogFile model for vanilla CreateCharBiography. This is
                // independent of menu textures/fonts and therefore also works with texture-only
                // UI overhauls such as GrimoireUI.
                if (biography is CreateCharBiography && BiographyFileField != null && BiographyQuestionIndexField != null)
                {
                    object biogFile = BiographyFileField.GetValue(biography);
                    int index = (int)BiographyQuestionIndexField.GetValue(biography);
                    object questions = GetMemberValue(biogFile, "Questions");
                    object question = GetIndexedValue(questions, index);
                    object textValue = GetMemberValue(question, "Text");
                    string modelText = JoinStringEnumerable(textValue as IEnumerable);
                    if (!string.IsNullOrWhiteSpace(modelText))
                        return modelText;
                }

                FieldInfo field = biography is CreateCharBiography
                    ? BiographyQuestionLabelsField
                    : FindFieldInHierarchy(biography.GetType(), "questionLabels");
                if (field == null)
                    return string.Empty;

                IEnumerable labels = field.GetValue(biography) as IEnumerable;
                if (labels == null)
                    return string.Empty;

                StringBuilder sb = new StringBuilder();
                foreach (object raw in labels)
                {
                    TextLabel label = raw as TextLabel;
                    if (label == null || string.IsNullOrWhiteSpace(label.Text))
                        continue;
                    if (sb.Length > 0)
                        sb.Append(' ');
                    sb.Append(label.Text.Trim());
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[Daggerfall Narrator] Character question extraction failed: " + ex.Message);
                return string.Empty;
            }
        }

        private static object GetMemberValue(object target, string memberName)
        {
            if (target == null)
                return null;
            Type type = target.GetType();
            while (type != null)
            {
                PropertyInfo prop = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (prop != null)
                    return prop.GetValue(target, null);
                FieldInfo field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null)
                    return field.GetValue(target);
                type = type.BaseType;
            }
            return null;
        }

        private static object GetIndexedValue(object collection, int index)
        {
            if (collection == null || index < 0)
                return null;
            Array array = collection as Array;
            if (array != null)
                return index < array.Length ? array.GetValue(index) : null;
            IList list = collection as IList;
            if (list != null)
                return index < list.Count ? list[index] : null;
            return null;
        }

        private static string JoinStringEnumerable(IEnumerable values)
        {
            if (values == null)
                return string.Empty;
            StringBuilder sb = new StringBuilder();
            foreach (object raw in values)
            {
                string part = raw as string;
                if (string.IsNullOrWhiteSpace(part))
                    continue;
                if (sb.Length > 0)
                    sb.Append(' ');
                sb.Append(part.Trim());
            }
            return sb.ToString();
        }

        private bool IsCharacterCreationInfoPopup(DaggerfallMessageBox box)
        {
            if (box == null || IsConversationWindow(box.PreviousWindow))
                return false;

            // Vanilla race/home-province and class descriptions are Yes/No message boxes
            // whose PreviousWindow is a CreateChar* window. GrimoireUI can wrap or replace
            // those windows, so also inspect the UI stack by type name.
            if (LooksLikeCharacterCreationWindow(box.PreviousWindow))
                return true;

            try
            {
                UserInterfaceManager manager = DaggerfallUI.UIManager as UserInterfaceManager;
                if (manager != null && UiWindowStackField != null)
                {
                    IEnumerable stack = UiWindowStackField.GetValue(manager) as IEnumerable;
                    if (stack != null)
                    {
                        foreach (object window in stack)
                        {
                            if (ReferenceEquals(window, box))
                                continue;
                            if (LooksLikeCharacterCreationWindow(window))
                                return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (config != null && config.DebugClassification)
                    UnityEngine.Debug.LogWarning("[Daggerfall Narrator] Character-creation popup stack scan failed: " + ex.Message);
            }

            return false;
        }

        private static bool LooksLikeCharacterCreationWindow(object window)
        {
            if (window == null)
                return false;

            Type type = window.GetType();
            string name = (type.FullName ?? type.Name).ToLowerInvariant();

            if (name.Contains("createchar") || name.Contains("charactercreation") || name.Contains("charcreation") || name.Contains("chargen"))
                return true;

            // Compatibility fallback for UI overhauls such as GrimoireUI whose replacement
            // classes may not inherit vanilla CreateChar* windows but retain descriptive names.
            bool creationSubject = name.Contains("race") || name.Contains("class") || name.Contains("biograph") || name.Contains("birth") || name.Contains("province");
            return name.Contains("grimoire") && creationSubject;
        }

        private void HandleCharacterCreationInfoPopup(DaggerfallMessageBox box, string renderedText)
        {
            string text = NormalizeForSpeech(renderedText);
            if (string.IsNullOrWhiteSpace(text))
                return;

            string hash = Sha1(NormalizeForMatch(text));
            string sourceKey = "ccdesc:" + hash;

            if (hash == lastCharacterCreationPopupHash || IsNarrationGroupActive(sourceKey) || sourceKey == activeSourceKey || WasRecentlyQueued(sourceKey))
                return;

            lastCharacterCreationPopupHash = hash;

            if (config.DebugClassification)
            {
                string previousName = box.PreviousWindow != null ? box.PreviousWindow.GetType().FullName : "<none>";
                UnityEngine.Debug.Log("[Daggerfall Narrator] Character-creation description detected (UI preserved), previous=" + previousName + ": " + text);
            }

            NarrationItem item = new NarrationItem();
            item.Text = text;
            item.SourceKey = sourceKey;
            item.ModalBox = null;
            item.BoundWindow = box;
            item.CancelIfWindowClosed = true;
            item.Category = NarrationCategory.CharacterDescription;
            item.ShowSubtitle = config.ShowSubtitlesForCharacterDescriptions;

            // Replacing this category means clicking a different province/class immediately
            // moves narration to the newly displayed description instead of building a queue.
            EnqueueReplacingCategoryStreaming(item, config.ReadableChunkCharacters);
        }

        private void HandleEligiblePopup(DaggerfallMessageBox box, ClassificationResult result)
        {
            string narrationText = NormalizeForSpeech(result.NarrationText);
            if (string.IsNullOrWhiteSpace(narrationText))
                narrationText = NormalizeForSpeech(ExtractText(box));

            string sourceKey = string.IsNullOrEmpty(result.SourceKey)
                ? "text:" + Sha1(NormalizeForMatch(narrationText))
                : result.SourceKey;

            if (IsNarrationGroupActive(sourceKey) || sourceKey == activeSourceKey ||
                (sourceKey == recentlyCompletedSourceKey && Time.realtimeSinceStartup - recentlyCompletedSourceTime < config.ChunkDedupSeconds) ||
                WasRecentlyQueued(sourceKey))
            {
                if (!result.PreserveOriginalPopup)
                    SafeClose(box);
                return;
            }

            NarrationItem item = new NarrationItem();
            item.Text = narrationText;
            item.SourceKey = sourceKey;
            item.Category = result.Category;
            item.ShowSubtitle = result.ShowSubtitle;

            bool popupWillRemain = result.PreserveOriginalPopup || !config.ReplaceEligiblePopups;

            if (!result.PreserveOriginalPopup && config.ReplaceEligiblePopups)
            {
                if (config.Mode == NarratorMode.Pause)
                {
                    HideMessageBoxVisuals(box);
                    item.ModalBox = box;
                    // Keep Pause mode as one clip so the hidden modal is not recaptured between chunks.
                    Enqueue(item);
                    return;
                }
                else
                {
                    SafeClose(box);
                    popupWillRemain = false;
                }
            }

            if (popupWillRemain)
            {
                item.BoundWindow = box;
                item.CancelIfWindowClosed = true;
            }

            EnqueueStreaming(item, config.ReadableChunkCharacters, popupWillRemain ? box : null);
        }

        private void HandlePreservedPopup(DaggerfallMessageBox box, string text, string keyPrefix, NarrationCategory category, bool showSubtitle, bool cancelIfWindowClosed = true)
        {
            text = NormalizeForSpeech(text);
            if (box == null || string.IsNullOrWhiteSpace(text))
                return;
            string sourceKey = keyPrefix + Sha1(NormalizeForMatch(text));
            if (IsNarrationGroupActive(sourceKey) || sourceKey == activeSourceKey || WasRecentlyQueued(sourceKey))
                return;

            NarrationItem item = new NarrationItem();
            item.Text = text;
            item.SourceKey = sourceKey;
            item.Category = category;
            item.ShowSubtitle = showSubtitle;
            item.BoundWindow = cancelIfWindowClosed ? box : null;
            item.CancelIfWindowClosed = cancelIfWindowClosed;
            EnqueueStreaming(item, config.ReadableChunkCharacters, cancelIfWindowClosed ? box : null);
        }

        private static bool IsOpeningNarrationPopup(string text)
        {
            string normalized = NormalizeForMatch(text);
            return normalized.StartsWith("you wake and look around the room") ||
                   (normalized.Contains("storm of supernatural strength") && normalized.Contains("emperor s quest")) ||
                   (normalized.Contains("privateer s hold") && (normalized.Contains("emperor") || normalized.Contains("tutorial"))) ||
                   (normalized.Contains("shipwreck") && normalized.Contains("iliac bay"));
        }

        private static bool IsQuestScriptPromptMessageBox(DaggerfallMessageBox box)
        {
            if (box == null)
                return false;
            try
            {
                FieldInfo eventField = typeof(DaggerfallMessageBox).GetField(
                    "OnButtonClick", BindingFlags.Instance | BindingFlags.NonPublic);
                Delegate handlers = eventField == null ? null : eventField.GetValue(box) as Delegate;
                if (handlers == null)
                    return false;

                Delegate[] invocationList = handlers.GetInvocationList();
                for (int i = 0; i < invocationList.Length; i++)
                {
                    Delegate handler = invocationList[i];
                    Type targetType = handler.Target == null ? handler.Method.DeclaringType : handler.Target.GetType();
                    string fullName = targetType == null ? string.Empty : (targetType.FullName ?? string.Empty);
                    if (fullName.StartsWith("DaggerfallWorkshop.Game.Questing.Actions.", StringComparison.Ordinal) &&
                        (string.Equals(targetType.Name, "Prompt", StringComparison.Ordinal) ||
                         string.Equals(targetType.Name, "PromptMulti", StringComparison.Ordinal)))
                        return true;
                }
            }
            catch { }
            return false;
        }

        private static bool IsNarratableWorldFlavorPopup(string text)
        {
            string n = NormalizeForMatch(text);
            if (string.IsNullOrWhiteSpace(n)) return false;
            return n.StartsWith("someone calls out") ||
                   n.StartsWith("someone calls from") ||
                   n.StartsWith("a voice calls out") ||
                   n.StartsWith("you hear someone call") ||
                   (n.Contains("calls out") && n.Contains("come in"));
        }

        private static bool IsPlayerStatusPopup(string text)
        {
            string normalized = NormalizeForMatch(text);
            return normalized.StartsWith("you are healthy") ||
                   normalized.StartsWith("you are rested") ||
                   normalized.StartsWith("you are well rested") ||
                   normalized.StartsWith("you are tired") ||
                   normalized.StartsWith("you are fatigued") ||
                   normalized.StartsWith("you are exhausted") ||
                   normalized.StartsWith("you feel rested") ||
                   normalized.StartsWith("you feel refreshed") ||
                   normalized.StartsWith("you are poisoned") ||
                   normalized.StartsWith("you have been poisoned") ||
                   normalized.StartsWith("you have contracted") ||
                   normalized.StartsWith("you are suffering from") ||
                   normalized.StartsWith("you feel somewhat bad") ||
                   normalized.StartsWith("you are in ");
        }

        private static bool IsLikelyCombatHudText(string text)
        {
            string n = NormalizeForMatch(text);
            if (string.IsNullOrWhiteSpace(n))
                return false;
            string[] starts = new string[]
            {
                "you hit ", "you miss ", "you strike ", "you attack ", "you damage ",
                "you parry ", "you block ", "you dodge ", "you killed ", "you kill ", "you slay ",
                "save against ", "saving throw ", "roll against "
            };
            for (int i = 0; i < starts.Length; i++)
                if (n.StartsWith(starts[i]))
                    return true;

            string[] contains = new string[]
            {
                " hits you", " hit you", " misses you", " missed you", " attacks you",
                " strikes you", " damages you", " critical hit", " critical strike",
                "save against", "saving throw", "resisted your", "you resist", "resists your",
                " points of damage", " damage to ", "to hit", "attack roll", "armor class",
                "spell resistance", "magic resistance", "is immune to", "are immune to"
            };
            for (int i = 0; i < contains.Length; i++)
                if (n.Contains(contains[i]))
                    return true;
            return false;
        }

        private static bool IsModeToggleHudText(string text)
        {
            string n = NormalizeForMatch(text);
            return n.Contains("climbing mode") || n.Contains("stealth mode") || n.Contains("run mode") ||
                   n.Contains("running mode") || n.Contains("walk mode") || n.Contains("crouch mode") ||
                   n.Contains("levitation mode") || n.EndsWith(" mode on") || n.EndsWith(" mode off");
        }

        private static bool IsObservationHudText(string text)
        {
            string n = NormalizeForMatch(text);
            return n.StartsWith("you see a ") || n.StartsWith("you see an ") || n.StartsWith("you see the ");
        }

        private static bool IsNarratableWorldFlavorHudText(string text)
        {
            // A small whitelist for classic world-interaction quips that are presented only through
            // HUD PopupText. These are flavor/system narration rather than combat log entries. Keep
            // this deliberately narrow so Dungeon Master does not begin scraping every HUD message.
            string n = NormalizeForMatch(text);
            return (n.StartsWith("this lock ") && n.Contains("you")) ||
                   n.StartsWith("the lock has ") ||
                   n.StartsWith("the door is magically held") ||
                   n.StartsWith("you hear a faint ") ||
                   n.StartsWith("you hear the sound of ");
        }

        private bool IsNarratableHudStatus(string text)
        {
            string n = NormalizeForMatch(text);
            if (n.StartsWith("you are healthy") || n.StartsWith("you are rested") || n.StartsWith("you are well rested") ||
                n.StartsWith("you are in ") ||
                n.StartsWith("you are tired") || n.StartsWith("you are fatigued") || n.StartsWith("you are exhausted") ||
                n.StartsWith("you are poisoned") || n.StartsWith("you have been poisoned") || n.StartsWith("you have contracted") ||
                n.StartsWith("you are suffering from") || n.StartsWith("you feel rested") || n.StartsWith("you feel refreshed") ||
                n.StartsWith("you are encumbered") || n.StartsWith("you are overburdened"))
                return true;

            if (config.NarrateClimatesCalories)
            {
                string[] calories = new string[]
                {
                    "you are hungry", "you are starving", "you are thirsty", "you are dehydrated",
                    "you are cold", "you are freezing", "you are hot", "you are overheating", "you are wet",
                    "you feel hungry", "you feel thirsty", "you feel cold", "you feel chilly", "you feel warm",
                    "you feel hot", "you feel wet", "you are getting cold", "you are getting hot"
                };
                for (int i = 0; i < calories.Length; i++)
                    if (n.StartsWith(calories[i]))
                        return true;
            }
            return false;
        }

        private bool IsClimatesCaloriesPopup(DaggerfallMessageBox box)
        {
            if (box == null)
                return false;
            if (!climatesCaloriesReflectionChecked)
            {
                climatesCaloriesReflectionChecked = true;
                try
                {
                    Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                    for (int i = 0; i < assemblies.Length && climatesCaloriesInfoBoxField == null; i++)
                    {
                        Type type = assemblies[i].GetType("ClimatesCalories.ClimateCalories", false);
                        if (type != null)
                            climatesCaloriesInfoBoxField = type.GetField("tempInfoBox", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    }
                }
                catch { }
            }
            if (climatesCaloriesInfoBoxField == null)
                return false;
            try { return ReferenceEquals(climatesCaloriesInfoBoxField.GetValue(null), box); }
            catch { return false; }
        }

        private bool WasRecentlyQueued(string key)
        {
            float timestamp;
            if (!recentlyQueuedKeys.TryGetValue(key, out timestamp))
                return false;

            if (Time.realtimeSinceStartup - timestamp <= config.ChunkDedupSeconds)
                return true;

            recentlyQueuedKeys.Remove(key);
            return false;
        }

        private void Enqueue(NarrationItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Text))
                return;

            if (string.IsNullOrEmpty(item.GroupKey))
                item.GroupKey = item.SourceKey;

            if (narrationQueue.Count >= config.MaxQueueItems)
            {
                NarrationItem dropped = narrationQueue.Dequeue();
                if (dropped != null)
                {
                    dropped.Cancelled = true;
                    RestoreMessageTiming(dropped);
                    ReleaseNarrationGroup(dropped);
                    if (dropped.ModalBox != null)
                        SafeClose(dropped.ModalBox);
                }
            }

            narrationQueue.Enqueue(item);
            RetainNarrationGroup(item);
            recentlyQueuedKeys[item.SourceKey] = Time.realtimeSinceStartup;

            if (!queueRoutineRunning)
                StartCoroutine(ProcessNarrationQueue());
        }

        private bool IsNarrationGroupActive(string groupKey)
        {
            if (string.IsNullOrEmpty(groupKey))
                return false;
            int count;
            return narrationGroupCounts.TryGetValue(groupKey, out count) && count > 0;
        }

        private void RetainNarrationGroup(NarrationItem item)
        {
            if (item == null || item.GroupRetained || string.IsNullOrEmpty(item.GroupKey))
                return;
            int count;
            narrationGroupCounts.TryGetValue(item.GroupKey, out count);
            narrationGroupCounts[item.GroupKey] = count + 1;
            item.GroupRetained = true;
        }

        private void ReleaseNarrationGroup(NarrationItem item)
        {
            if (item == null || !item.GroupRetained || item.GroupReleased || string.IsNullOrEmpty(item.GroupKey))
                return;
            int count;
            if (narrationGroupCounts.TryGetValue(item.GroupKey, out count))
            {
                count--;
                if (count <= 0)
                    narrationGroupCounts.Remove(item.GroupKey);
                else
                    narrationGroupCounts[item.GroupKey] = count;
            }
            item.GroupReleased = true;
        }

        private void EnqueueReplacingCategory(NarrationItem item)
        {
            if (item == null)
                return;

            Queue<NarrationItem> kept = new Queue<NarrationItem>();
            while (narrationQueue.Count > 0)
            {
                NarrationItem queued = narrationQueue.Dequeue();
                if (queued.Category == item.Category)
                {
                    queued.Cancelled = true;
                    recentlyQueuedKeys.Remove(queued.SourceKey);
                    RestoreMessageTiming(queued);
                    ReleaseNarrationGroup(queued);
                    if (queued.ModalBox != null)
                        SafeClose(queued.ModalBox);
                }
                else
                {
                    kept.Enqueue(queued);
                }
            }
            while (kept.Count > 0)
                narrationQueue.Enqueue(kept.Dequeue());

            if (activeItem != null && activeItem.Category == item.Category)
                skipRequested = true;

            Enqueue(item);
        }

        private void EnqueueReplacingCategoryStreaming(NarrationItem item, int targetCharacters)
        {
            if (item == null)
                return;
            CancelCategory(item.Category);
            EnqueueStreaming(item, targetCharacters, item.BoundWindow as DaggerfallMessageBox);
        }

        private void EnqueueStreaming(NarrationItem template, int targetCharacters, DaggerfallMessageBox timingBox)
        {
            if (template == null || string.IsNullOrWhiteSpace(template.Text))
                return;

            List<string> chunks = config.StreamLongNarration
                ? SplitStreamingChunks(template.Text, config.FirstChunkCharacters, targetCharacters)
                : new List<string>() { template.Text };

            if (chunks.Count == 0)
                return;

            List<NarrationItem> items = new List<NarrationItem>();
            for (int i = 0; i < chunks.Count; i++)
            {
                NarrationItem item = CloneNarrationItem(template);
                item.Text = chunks[i];
                item.GroupKey = string.IsNullOrEmpty(template.GroupKey) ? template.SourceKey : template.GroupKey;
                item.SourceKey = chunks.Count == 1 ? template.SourceKey : template.SourceKey + ":chunk:" + i.ToString(CultureInfo.InvariantCulture);
                item.IsFirstChunk = i == 0;
                item.IsFinalChunk = i == chunks.Count - 1;
                items.Add(item);
            }

            if (timingBox != null)
                ApplyMessageTiming(timingBox, items[0], items[items.Count - 1]);

            for (int i = 0; i < items.Count; i++)
                Enqueue(items[i]);
        }

        private static NarrationItem CloneNarrationItem(NarrationItem source)
        {
            NarrationItem item = new NarrationItem();
            item.Text = source.Text;
            item.SourceKey = source.SourceKey;
            item.GroupKey = source.GroupKey;
            item.ModalBox = source.ModalBox;
            item.BoundWindow = source.BoundWindow;
            item.CancelIfWindowClosed = source.CancelIfWindowClosed;
            item.Category = source.Category;
            item.ShowSubtitle = source.ShowSubtitle;
            return item;
        }

        private void ApplyMessageTiming(DaggerfallMessageBox box, NarrationItem first, NarrationItem last)
        {
            if (box == null || first == null || last == null || config.MessageTiming == MessageTimingMode.FollowWindow || HasButtons(box))
                return;

            try
            {
                bool originalClick = box.ClickAnywhereToClose;
                bool originalCancel = box.AllowCancel;
                box.ClickAnywhereToClose = false;
                box.AllowCancel = false;

                if (config.MessageTiming == MessageTimingMode.HoldUntilSpeechStarts)
                {
                    first.TimingBox = box;
                    first.OriginalClickAnywhereToClose = originalClick;
                    first.OriginalAllowCancel = originalCancel;
                    first.UnlockWindowOnSpeechStart = true;
                }
                else
                {
                    last.TimingBox = box;
                    last.OriginalClickAnywhereToClose = originalClick;
                    last.OriginalAllowCancel = originalCancel;
                    last.UnlockWindowOnFinish = true;
                }
            }
            catch (Exception ex)
            {
                if (config.DebugClassification)
                    UnityEngine.Debug.LogWarning("[Daggerfall Narrator] Could not apply message timing lock: " + ex.Message);
            }
        }

        private void RestoreMessageTiming(NarrationItem item)
        {
            if (item == null || item.TimingBox == null)
                return;
            try
            {
                item.TimingBox.ClickAnywhereToClose = item.OriginalClickAnywhereToClose;
                item.TimingBox.AllowCancel = item.OriginalAllowCancel;
            }
            catch { }
            item.TimingBox = null;
        }

        private bool IsNarrationItemValid(NarrationItem item)
        {
            if (item == null || item.Cancelled)
                return false;
            if (!item.CancelIfWindowClosed || item.BoundWindow == null)
                return true;
            try
            {
                IUserInterfaceWindow window = item.BoundWindow as IUserInterfaceWindow;
                return window != null && DaggerfallUI.UIManager != null && DaggerfallUI.UIManager.ContainsWindow(window);
            }
            catch
            {
                return false;
            }
        }

        private static List<string> SplitStreamingChunks(string text, int firstChunkCharacters, int targetCharacters)
        {
            List<string> result = new List<string>();
            text = NormalizeForSpeech(text);
            if (string.IsNullOrWhiteSpace(text))
                return result;

            int firstTarget = Math.Max(80, firstChunkCharacters);
            int split = FindSentenceBoundary(text, firstTarget);
            if (split <= 0 || split >= text.Length - 1)
            {
                if (text.Length <= Math.Max(firstTarget, targetCharacters))
                {
                    result.Add(text);
                    return result;
                }
                split = FindWordBoundary(text, firstTarget);
            }

            if (split > 0 && split < text.Length)
            {
                string first = text.Substring(0, split).Trim();
                if (!string.IsNullOrWhiteSpace(first))
                    result.Add(first);
                string remaining = text.Substring(split).Trim();
                if (!string.IsNullOrWhiteSpace(remaining))
                    result.AddRange(SplitNarrationChunks(remaining, Math.Max(firstTarget, targetCharacters)));
            }
            else
            {
                result.AddRange(SplitNarrationChunks(text, Math.Max(firstTarget, targetCharacters)));
            }
            return result;
        }

        private static int FindSentenceBoundary(string text, int target)
        {
            if (string.IsNullOrEmpty(text))
                return -1;
            int max = Math.Min(text.Length - 1, Math.Max(target + 180, target));
            int min = Math.Min(text.Length - 1, 40);
            for (int i = min; i <= max; i++)
            {
                char c = text[i];
                if ((c == '.' || c == '!' || c == '?') && (i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1])))
                    return i + 1;
            }
            return -1;
        }

        private static int FindWordBoundary(string text, int target)
        {
            if (text.Length <= target)
                return text.Length;
            int start = Math.Min(target, text.Length - 1);
            for (int i = start; i > Math.Max(40, start - 80); i--)
                if (char.IsWhiteSpace(text[i]))
                    return i;
            return start;
        }

        private void ReplaceCategoryWithStreamingChunks(string text, string sourcePrefix, NarrationCategory category, bool showSubtitle, int targetCharacters, IUserInterfaceWindow boundWindow)
        {
            CancelCategory(category);
            NarrationItem template = new NarrationItem();
            template.Text = text;
            template.SourceKey = sourcePrefix;
            template.Category = category;
            template.ShowSubtitle = showSubtitle;
            template.BoundWindow = boundWindow;
            template.CancelIfWindowClosed = boundWindow != null;
            EnqueueStreaming(template, targetCharacters, null);
        }

        private void ReplaceCategoryWithChunks(string text, string sourcePrefix, NarrationCategory category, bool showSubtitle, int targetCharacters)
        {
            CancelCategory(category);
            List<string> chunks = SplitNarrationChunks(text, targetCharacters);
            for (int i = 0; i < chunks.Count; i++)
            {
                NarrationItem item = new NarrationItem();
                item.Text = chunks[i];
                item.SourceKey = sourcePrefix + ":" + i.ToString(CultureInfo.InvariantCulture);
                item.Category = category;
                item.ShowSubtitle = showSubtitle;
                Enqueue(item);
            }
        }

        private void CancelCategory(NarrationCategory category)
        {
            Queue<NarrationItem> kept = new Queue<NarrationItem>();
            while (narrationQueue.Count > 0)
            {
                NarrationItem queued = narrationQueue.Dequeue();
                if (queued.Category == category)
                {
                    queued.Cancelled = true;
                    recentlyQueuedKeys.Remove(queued.SourceKey);
                    RestoreMessageTiming(queued);
                    ReleaseNarrationGroup(queued);
                    if (queued.ModalBox != null)
                        SafeClose(queued.ModalBox);
                }
                else
                {
                    kept.Enqueue(queued);
                }
            }
            while (kept.Count > 0)
                narrationQueue.Enqueue(kept.Dequeue());

            if (activeItem != null && activeItem.Category == category)
                skipRequested = true;
        }

        private static List<string> SplitNarrationChunks(string text, int targetCharacters)
        {
            List<string> chunks = new List<string>();
            text = NormalizeForSpeech(text);
            if (string.IsNullOrWhiteSpace(text))
                return chunks;
            if (targetCharacters <= 0 || text.Length <= targetCharacters)
            {
                chunks.Add(text);
                return chunks;
            }

            StringBuilder current = new StringBuilder();
            string[] words = text.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < words.Length; i++)
            {
                string word = words[i];
                bool sentenceEnd = word.EndsWith(".") || word.EndsWith("!") || word.EndsWith("?") || word.EndsWith(";");
                if (current.Length > 0)
                    current.Append(' ');
                current.Append(word);

                if (current.Length >= targetCharacters && sentenceEnd)
                {
                    chunks.Add(current.ToString());
                    current.Length = 0;
                }
                else if (current.Length >= targetCharacters + Math.Max(120, targetCharacters / 3))
                {
                    chunks.Add(current.ToString());
                    current.Length = 0;
                }
            }
            if (current.Length > 0)
                chunks.Add(current.ToString());
            return chunks;
        }

        private void ClearQueuedNarration()
        {
            while (narrationQueue.Count > 0)
            {
                NarrationItem queued = narrationQueue.Dequeue();
                if (queued != null)
                {
                    queued.Cancelled = true;
                    RestoreMessageTiming(queued);
                    ReleaseNarrationGroup(queued);
                    if (queued.ModalBox != null)
                        SafeClose(queued.ModalBox);
                }
            }
            recentlyQueuedKeys.Clear();
        }

        private void StopAllNarrationForContextChange()
        {
            // This component survives save/character loads. Cancel both the known active turn and any
            // queued/ready module turns at the engine so stale narration cannot block NPC or Player.
            if (voiceEngine != null)
            {
                if (!string.IsNullOrEmpty(activeVoiceEngineTurnId))
                    StartCoroutine(voiceEngine.CancelTurn(activeVoiceEngineTurnId));
                StartCoroutine(voiceEngine.CancelModule());
            }
            activeVoiceEngineTurnId = string.Empty;

            ClearQueuedNarration();
            skipRequested = true;
            if (audioSource != null)
            {
                AudioClip clip = audioSource.clip;
                audioSource.Stop();
                audioSource.clip = null;
                if (clip != null) Destroy(clip);
            }
            activeItem = null;
            activeModalBox = null;
            activeSourceKey = string.Empty;
            activeText = string.Empty;
            activeSubtitlePages.Clear();
            activeSubtitlePage = 0;
            subtitleAudioPlaying = false;
            subtitleAudioLength = 0f;
            subtitleAlpha = 0f;
            subtitleTargetAlpha = 0f;
            recentlyQueuedKeys.Clear();
            narrationGroupCounts.Clear();
            if (classicSubtitlePanel != null)
                classicSubtitlePanel.Enabled = false;
        }

        private void HandlePlayerEntityTransition(object newPlayerEntity)
        {
            StopAllNarrationForContextChange();

            lastTopWindow = null;
            lastBookWindow = null;
            bookWasTopLastFrame = false;
            lastBookTextHash = string.Empty;
            lastBiographyWindow = null;
            lastBiographyQuestionHash = string.Empty;
            lastClassQuestionWindow = null;
            lastClassQuestionHash = string.Empty;
            lastQuestJournalWindow = null;
            lastQuestJournalTextHash = string.Empty;
            lastHistoryWindow = null;
            lastHistoryTextHash = string.Empty;
            lastCharacterCreationPopupHash = string.Empty;
            previousHudPopupRows.Clear();
            recentHudNarrationTimes.Clear();
            recentlyCompletedSourceKey = string.Empty;
            recentlyCompletedSourceTime = 0f;

            Debug.Log("[Daggerfall Narrator] Player entity changed; narration state and Voice Engine ownership reset.");
        }

        // Small public adapter for other DFU mods. A mod can reference Daggerfall Narrator and
        // explicitly submit narrator-worthy text instead of relying on UI scraping.
        public static bool Speak(string text)
        {
            return Speak(text, null, true);
        }

        public static bool Speak(string text, string sourceKey, bool showSubtitle)
        {
            if (instance == null || instance.config == null || !instance.config.Enabled || string.IsNullOrWhiteSpace(text))
                return false;
            NarrationItem item = new NarrationItem();
            item.Text = NormalizeForSpeech(text);
            item.SourceKey = string.IsNullOrWhiteSpace(sourceKey) ? "external:" + Sha1(NormalizeForMatch(text)) : "external:" + sourceKey;
            item.Category = NarrationCategory.External;
            item.ShowSubtitle = showSubtitle;
            instance.Enqueue(item);
            return true;
        }

        public void EnqueueManual(string text)
        {
            EnqueueManualWithVoice(text, null);
        }

        public void EnqueueManualWithVoice(string text, string voiceOverride)
        {
            NarrationItem item = new NarrationItem();
            item.Text = NormalizeForSpeech(text);
            item.SourceKey = "manual:" + Guid.NewGuid().ToString("N");
            item.ModalBox = null;
            item.Category = NarrationCategory.Manual;
            item.ShowSubtitle = config.ShowSubtitlesForManual;
            item.VoiceOverride = string.IsNullOrWhiteSpace(voiceOverride) ? null : voiceOverride.Trim();
            Enqueue(item);
        }

        private IEnumerator ProcessNarrationQueue()
        {
            queueRoutineRunning = true;

            while (narrationQueue.Count > 0)
            {
                NarrationItem item = narrationQueue.Dequeue();
                activeItem = item;
                activeSourceKey = item.SourceKey;
                activeModalBox = item.ModalBox;
                skipRequested = false;

                if (!IsNarrationItemValid(item))
                {
                    FinishSkippedItem(item);
                    continue;
                }

                if (config.Backend == TtsBackend.Kokoro && voiceEngine != null)
                {
                    DaggerfallVoiceEngineTurn turn = new DaggerfallVoiceEngineTurn();
                    int priority = GetVoiceEnginePriority(item);
                    int expiry = item.Category == NarrationCategory.HudStatus ? 6500 : 30000;
                    yield return StartCoroutine(voiceEngine.AcquireTurn(priority, "narrator-" + item.Category.ToString().ToLowerInvariant(), item.SourceKey, expiry,
                        delegate { return IsNarrationItemValid(item) && !skipRequested; }, turn));
                    activeVoiceEngineTurnId = turn.Ready ? turn.Id : string.Empty;
                    if (turn.EngineOnline && !turn.Ready)
                    {
                        FinishSkippedItem(item);
                        continue;
                    }
                }

                if (item.ShowSubtitle && config.ShowSubtitles)
                    SetSubtitle(item.Text, 1f);
                else
                {
                    activeText = string.Empty;
                    activeSubtitlePages.Clear();
                    activeSubtitlePage = 0;
                    subtitleAlpha = 0f;
                    subtitleTargetAlpha = 0f;
                }

                string wavPath = string.Empty;
                if (item.PrefetchStarted)
                {
                    while (!item.PrefetchComplete && !item.Cancelled && IsNarrationItemValid(item))
                        yield return null;
                    if (item.PrefetchComplete && !item.PrefetchFailed)
                        wavPath = item.PreparedWavPath;
                }

                if (string.IsNullOrEmpty(wavPath))
                {
                    wavPath = FindExistingAudio(item.Text, item);
                    if (string.IsNullOrEmpty(wavPath))
                    {
                        wavPath = GetCachePath(item.Text, item);
                        string speechText = pronunciations.Apply(item.Text);
                        yield return StartCoroutine(GenerateSpeech(speechText, wavPath, item));
                    }
                }

                if (!IsNarrationItemValid(item) || skipRequested)
                {
                    FinishSkippedItem(item);
                    continue;
                }

                AudioClip clip = null;
                if (!string.IsNullOrEmpty(wavPath) && File.Exists(wavPath))
                    yield return StartCoroutine(LoadWav(wavPath, delegate(AudioClip loaded) { clip = loaded; }));

                if (!IsNarrationItemValid(item) || skipRequested)
                {
                    if (clip != null)
                        Destroy(clip);
                    FinishSkippedItem(item);
                    continue;
                }

                if (clip != null)
                {
                    audioSource.Stop();
                    audioSource.clip = clip;
                    audioSource.volume = EffectiveVolume();
                    audioSource.Play();

                    if (item.UnlockWindowOnSpeechStart)
                        RestoreMessageTiming(item);

                    subtitleAudioPlaying = true;
                    subtitleAudioStartRealtime = Time.realtimeSinceStartup;
                    subtitleAudioLength = Mathf.Max(0.01f, clip.length);

                    StartPrefetchNextItem();

                    while (audioSource.isPlaying && !skipRequested && IsNarrationItemValid(item))
                        yield return null;

                    if ((skipRequested || !IsNarrationItemValid(item)) && audioSource.isPlaying)
                        audioSource.Stop();

                    subtitleAudioPlaying = false;
                    subtitleAudioLength = 0f;

                    if (audioSource.clip == clip)
                        audioSource.clip = null;
                    Destroy(clip);
                }
                else
                {
                    RestoreMessageTiming(item);
                    subtitleAudioPlaying = false;
                    subtitleAudioLength = 0f;
                    float until = Time.realtimeSinceStartup + config.TtsFailureSubtitleSeconds;
                    while (Time.realtimeSinceStartup < until && !skipRequested && IsNarrationItemValid(item))
                        yield return null;
                }

                if (voiceEngine != null && !string.IsNullOrEmpty(activeVoiceEngineTurnId))
                    yield return StartCoroutine(voiceEngine.CompleteTurn(activeVoiceEngineTurnId));
                activeVoiceEngineTurnId = string.Empty;

                if (item.ShowSubtitle && config.ShowSubtitles && !skipRequested && IsNarrationItemValid(item) && config.SubtitleTailSeconds > 0f)
                {
                    float tailUntil = Time.realtimeSinceStartup + config.SubtitleTailSeconds;
                    while (Time.realtimeSinceStartup < tailUntil && !skipRequested && IsNarrationItemValid(item))
                        yield return null;
                }

                subtitleTargetAlpha = 0f;
                if (item.ShowSubtitle && config.ShowSubtitles && config.SubtitleFadeOutSeconds > 0f)
                {
                    float fadeUntil = Time.realtimeSinceStartup + config.SubtitleFadeOutSeconds;
                    while (Time.realtimeSinceStartup < fadeUntil)
                        yield return null;
                }
                else
                {
                    subtitleAlpha = 0f;
                }

                if (item.UnlockWindowOnFinish)
                    RestoreMessageTiming(item);

                if (activeModalBox != null)
                    SafeClose(activeModalBox);

                recentlyCompletedSourceKey = string.IsNullOrEmpty(item.GroupKey) ? item.SourceKey : item.GroupKey;
                recentlyCompletedSourceTime = Time.realtimeSinceStartup;
                recentlyQueuedKeys.Remove(item.SourceKey);
                ReleaseNarrationGroup(item);
                activeModalBox = null;
                activeSourceKey = string.Empty;
                activeText = string.Empty;
                activeSubtitlePages.Clear();
                activeSubtitlePage = 0;
                activeItem = null;
            }

            activeItem = null;
            queueRoutineRunning = false;
        }

        private void FinishSkippedItem(NarrationItem item)
        {
            if (voiceEngine != null && !string.IsNullOrEmpty(activeVoiceEngineTurnId)) StartCoroutine(voiceEngine.CancelTurn(activeVoiceEngineTurnId));
            activeVoiceEngineTurnId = string.Empty;
            if (item != null)
            {
                RestoreMessageTiming(item);
                recentlyQueuedKeys.Remove(item.SourceKey);
                ReleaseNarrationGroup(item);
                if (item.ModalBox != null && config.Mode == NarratorMode.Pause)
                    SafeClose(item.ModalBox);
            }
            if (audioSource != null && audioSource.isPlaying)
                audioSource.Stop();
            subtitleAudioPlaying = false;
            subtitleAudioLength = 0f;
            subtitleTargetAlpha = 0f;
            activeModalBox = null;
            activeSourceKey = string.Empty;
            activeText = string.Empty;
            activeSubtitlePages.Clear();
            activeSubtitlePage = 0;
            activeItem = null;
        }

        private void StartPrefetchNextItem()
        {
            // Daggerfall Voice Engine uses reactive caching only. Never synthesize a line until it is actually needed.
            return;
#pragma warning disable 162
            if (!config.PrefetchNextChunk || narrationQueue.Count == 0)
                return;
            NarrationItem next = narrationQueue.Peek();
            if (next == null || next.PrefetchStarted || next.Cancelled || !IsNarrationItemValid(next))
                return;
            if (!string.IsNullOrEmpty(FindExistingAudio(next.Text, next)))
                return;
            next.PrefetchStarted = true;
            StartCoroutine(PrefetchNarrationItem(next));
#pragma warning restore 162
        }

        private IEnumerator PrefetchNarrationItem(NarrationItem item)
        {
            string path = GetCachePath(item.Text, item);
            string speechText = pronunciations.Apply(item.Text);
            yield return StartCoroutine(GenerateSpeech(speechText, path, item));
            item.PreparedWavPath = path;
            item.PrefetchFailed = !File.Exists(path);
            item.PrefetchComplete = true;
        }

        private string FindExistingAudio(string originalText, NarrationItem item = null)
        {
            string fileName = GetAudioFileName(originalText, item);
            string packaged = Path.Combine(packagedAudioDir, fileName);
            if (File.Exists(packaged))
                return packaged;

            string cached = Path.Combine(cacheDir, fileName);
            if (File.Exists(cached))
            {
                TouchCacheFile(cached);
                return cached;
            }

            return string.Empty;
        }

        private string GetCachePath(string originalText, NarrationItem item = null)
        {
            return Path.Combine(cacheDir, GetAudioFileName(originalText, item));
        }

        private string GetAudioFileName(string originalText, NarrationItem item = null)
        {
            string voice;
            string speed;
            string style = config.KokoroAudioStylePreset.ToString();
            if (config.Backend == TtsBackend.Piper)
            {
                voice = config.PiperVoiceName;
                speed = config.PiperLengthScale.ToString(CultureInfo.InvariantCulture);
                style = "Clean";
            }
            else
            {
                voice = item != null && !string.IsNullOrWhiteSpace(item.VoiceOverride) ? item.VoiceOverride : GetEffectiveKokoroVoice();
                speed = config.KokoroSpeed.ToString(CultureInfo.InvariantCulture);
            }

            string key = originalText + "|" + config.Backend + "|" + voice + "|" + speed + "|" + style +
                "|depth=" + GetVoiceDepthSemitones().ToString(CultureInfo.InvariantCulture) + "|v7";
            return Sha1(key) + ".wav";
        }

        private IEnumerator LoadWav(string wavPath, Action<AudioClip> onLoaded)
        {
            string uri;
            try
            {
                uri = new Uri(wavPath).AbsoluteUri;
            }
            catch
            {
                uri = "file:///" + wavPath.Replace("\\", "/");
            }

            using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.WAV))
            {
                yield return request.SendWebRequest();
#if UNITY_2020_1_OR_NEWER
                bool failed = request.result != UnityWebRequest.Result.Success;
#else
                bool failed = request.isNetworkError || request.isHttpError;
#endif
                if (failed)
                {
                    UnityEngine.Debug.LogWarning("[Daggerfall Narrator] WAV load failed: " + request.error);
                    onLoaded(null);
                    yield break;
                }

                onLoaded(DownloadHandlerAudioClip.GetContent(request));
            }
        }

        private IEnumerator GenerateSpeech(string text, string wavPath, NarrationItem item)
        {
            if (config.Backend == TtsBackend.Piper)
                yield return StartCoroutine(GenerateWithPiperHttp(text, wavPath, item));
            else
                yield return StartCoroutine(GenerateWithKokoroHttp(text, wavPath, item));
        }

        private IEnumerator GenerateWithKokoroHttp(string text, string wavPath, NarrationItem item)
        {
            string voice = item != null && !string.IsNullOrWhiteSpace(item.VoiceOverride) ? item.VoiceOverride : GetEffectiveKokoroVoice();
            string json = "{\"text\":\"" + JsonEscape(text) + "\",\"voice\":\"" +
                JsonEscape(voice) + "\",\"lang\":\"" + JsonEscape(ResolveKokoroLanguage(voice)) + "\",\"speed\":" +
                config.KokoroSpeed.ToString(CultureInfo.InvariantCulture) + ",\"pitch_semitones\":" +
                GetVoiceDepthSemitones().ToString(CultureInfo.InvariantCulture) + ",\"audio_style\":\"" +
                config.KokoroAudioStylePreset.ToString().ToLowerInvariant() + "\",\"module\":\"DungeonMaster\",\"turn_id\":\"" +
                JsonEscape(activeVoiceEngineTurnId) + "\",\"emotion\":\"" + JsonEscape(GetNarratorEmotion(item)) + "\",\"emotion_intensity\":" +
                GetNarratorEmotionIntensity(item).ToString("0.###", CultureInfo.InvariantCulture) + "}";
            string url = "http://127.0.0.1:" + config.KokoroPort + "/synthesize";
            yield return StartCoroutine(PostTtsRequest(url, json, wavPath, "Daggerfall Voice Engine", item));
        }

        private int GetVoiceEnginePriority(NarrationItem item)
        {
            if (item == null) return 80;
            if (item.Category == NarrationCategory.Manual) return 95;
            if (item.Category == NarrationCategory.HudStatus) return 35;
            if (item.Category == NarrationCategory.Book || item.Category == NarrationCategory.History) return 75;
            if (item.Category == NarrationCategory.CharacterQuestion || item.Category == NarrationCategory.CharacterDescription) return 85;
            return 90;
        }

        private string GetNarratorEmotion(NarrationItem item)
        {
            if (item == null) return "neutral";
            if (item.Category == NarrationCategory.HudStatus) return "tense";
            if (item.Category == NarrationCategory.Book || item.Category == NarrationCategory.History) return "warm";
            if (item.Category == NarrationCategory.QuestLog || item.Category == NarrationCategory.QuestNote) return "grim";
            return "neutral";
        }

        private float GetNarratorEmotionIntensity(NarrationItem item)
        {
            return item != null && item.Category == NarrationCategory.HudStatus ? 0.32f : 0.20f;
        }

        private bool TryRouteObservationToPlayer(string text)
        {
            try
            {
                if (!playerVoObservationChecked)
                {
                    playerVoObservationChecked = true;
                    foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        Type t = a.GetType("PlayerVO.PlayerVOMod", false);
                        if (t == null) continue;
                        playerVoType = t;
                        playerVoSpeakObservation = t.GetMethod("SpeakObservation", BindingFlags.Public | BindingFlags.Static);
                        break;
                    }
                }
                if (playerVoSpeakObservation == null) return false;
                object result = playerVoSpeakObservation.Invoke(null, new object[] { text });
                return result is bool && (bool)result;
            }
            catch { return false; }
        }

        private IEnumerator GenerateWithPiperHttp(string text, string wavPath, NarrationItem item)
        {
            string json = "{\"text\":\"" + JsonEscape(text) + "\",\"length_scale\":" +
                config.PiperLengthScale.ToString(CultureInfo.InvariantCulture) + "}";
            string url = "http://127.0.0.1:" + config.PiperPort + "/synthesize";
            yield return StartCoroutine(PostTtsRequest(url, json, wavPath, "Piper", item));
        }

        private IEnumerator PostTtsRequest(string url, string json, string wavPath, string backendName, NarrationItem item)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            int attempts = Math.Max(1, config.TtsHttpAttempts);
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
                {
                    request.uploadHandler = new UploadHandlerRaw(body);
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.SetRequestHeader("Content-Type", "application/json");
                    request.timeout = Math.Max(1, config.TtsHttpTimeoutSeconds);
                    UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                    while (!operation.isDone)
                    {
                        bool activeSkip = item != null && ReferenceEquals(item, activeItem) && skipRequested;
                        if ((item != null && !IsNarrationItemValid(item)) || activeSkip)
                        {
                            request.Abort();
                            yield break;
                        }
                        yield return null;
                    }
#if UNITY_2020_1_OR_NEWER
                    bool failed = request.result != UnityWebRequest.Result.Success;
#else
                    bool failed = request.isNetworkError || request.isHttpError;
#endif
                    if (!failed && request.downloadHandler != null && request.downloadHandler.data != null && request.downloadHandler.data.Length > 44)
                    {
                        try
                        {
                            File.WriteAllBytes(wavPath, request.downloadHandler.data);
                            TouchCacheFile(wavPath);
                            RequestCacheCleanupSoon();
                            yield break;
                        }
                        catch (Exception ex)
                        {
                            UnityEngine.Debug.LogWarning("[Daggerfall Narrator] Could not cache " + backendName + " WAV: " + ex.Message);
                            yield break;
                        }
                    }

                    if (attempt == attempts - 1)
                        UnityEngine.Debug.LogWarning("[Daggerfall Narrator] " + backendName + " HTTP synthesis failed: " + request.error);
                }

                if (config.TtsRetryDelaySeconds > 0f)
                    yield return new WaitForSecondsRealtime(config.TtsRetryDelaySeconds);
            }
        }

        private void ScheduleNextCacheCleanup(float delaySeconds)
        {
            nextCacheCleanupRealtime = Time.realtimeSinceStartup + Mathf.Max(1f, delaySeconds);
        }

        private void RequestCacheCleanupSoon()
        {
            if (config == null || !config.AutoManageCache || config.MaxCacheSizeMB <= 0)
                return;

            float soon = Time.realtimeSinceStartup + 2f;
            if (nextCacheCleanupRealtime <= 0f || nextCacheCleanupRealtime > soon)
                nextCacheCleanupRealtime = soon;
        }

        private void MaybeRunScheduledCacheCleanup()
        {
            if (config == null || !config.AutoManageCache || config.MaxCacheSizeMB <= 0 || cacheCleanupRunning)
                return;
            if (Time.realtimeSinceStartup < nextCacheCleanupRealtime)
                return;

            // Never scan/delete while audio is being generated/played or narration is queued.
            if (queueRoutineRunning || narrationQueue.Count > 0 || (audioSource != null && audioSource.isPlaying))
            {
                ScheduleNextCacheCleanup(15f);
                return;
            }

            CleanupCacheToLimit();
            ScheduleNextCacheCleanup(Mathf.Max(60f, config.CacheCleanupIntervalMinutes * 60f));
        }

        private void TouchCacheFile(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path) && Path.GetDirectoryName(path) == cacheDir)
                    File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            }
            catch { }
        }

        private void CleanupCacheToLimit()
        {
            if (cacheCleanupRunning || config == null || !config.AutoManageCache || config.MaxCacheSizeMB <= 0)
                return;

            cacheCleanupRunning = true;
            try
            {
                Directory.CreateDirectory(cacheDir);
                string[] paths = Directory.GetFiles(cacheDir, "*.wav", SearchOption.TopDirectoryOnly);
                List<FileInfo> files = new List<FileInfo>();
                long totalBytes = 0;
                for (int i = 0; i < paths.Length; i++)
                {
                    try
                    {
                        FileInfo info = new FileInfo(paths[i]);
                        totalBytes += info.Length;
                        files.Add(info);
                    }
                    catch { }
                }

                long maxBytes = (long)config.MaxCacheSizeMB * 1024L * 1024L;
                if (totalBytes <= maxBytes)
                    return;

                long targetBytes = (long)(maxBytes * Mathf.Clamp(config.CacheCleanupTargetPercent, 0.25f, 0.99f));
                bool hardOverLimit = totalBytes > (long)(maxBytes * Mathf.Max(1f, config.CacheHardLimitMultiplier));
                DateTime minimumAgeCutoff = DateTime.UtcNow.AddHours(-Mathf.Max(0f, config.CacheMinimumAgeHours));

                files.Sort(delegate(FileInfo a, FileInfo b)
                {
                    DateTime aTime = a.LastAccessTimeUtc > a.LastWriteTimeUtc ? a.LastAccessTimeUtc : a.LastWriteTimeUtc;
                    DateTime bTime = b.LastAccessTimeUtc > b.LastWriteTimeUtc ? b.LastAccessTimeUtc : b.LastWriteTimeUtc;
                    return DateTime.Compare(aTime, bTime);
                });

                int deletedFiles = 0;
                long deletedBytes = 0;
                for (int i = 0; i < files.Count && totalBytes > targetBytes; i++)
                {
                    FileInfo info = files[i];
                    DateTime newestUse = info.LastAccessTimeUtc > info.LastWriteTimeUtc ? info.LastAccessTimeUtc : info.LastWriteTimeUtc;
                    if (!hardOverLimit && newestUse > minimumAgeCutoff)
                        continue;

                    try
                    {
                        long length = info.Length;
                        info.Delete();
                        totalBytes -= length;
                        deletedBytes += length;
                        deletedFiles++;
                    }
                    catch (Exception ex)
                    {
                        if (config.DebugClassification)
                            UnityEngine.Debug.LogWarning("[Daggerfall Narrator] Cache cleanup could not delete " + info.FullName + ": " + ex.Message);
                    }
                }

                if (deletedFiles > 0)
                    UnityEngine.Debug.Log("[Daggerfall Narrator] Cache cleanup removed " + deletedFiles + " file(s), " + FormatBytes(deletedBytes) + ". Remaining cache: " + FormatBytes(totalBytes) + ".");
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[Daggerfall Narrator] Cache cleanup failed: " + ex.Message);
            }
            finally
            {
                cacheCleanupRunning = false;
            }
        }

        private void ClearRuntimeCache(out int deletedFiles, out long deletedBytes)
        {
            deletedFiles = 0;
            deletedBytes = 0;
            try
            {
                if (!Directory.Exists(cacheDir))
                    return;
                string[] paths = Directory.GetFiles(cacheDir, "*.wav", SearchOption.TopDirectoryOnly);
                for (int i = 0; i < paths.Length; i++)
                {
                    try
                    {
                        FileInfo info = new FileInfo(paths[i]);
                        long length = info.Length;
                        info.Delete();
                        deletedBytes += length;
                        deletedFiles++;
                    }
                    catch { }
                }
            }
            catch { }
            ScheduleNextCacheCleanup(Mathf.Max(60f, config.CacheCleanupIntervalMinutes * 60f));
        }

        private string GetCacheStatusText()
        {
            long totalBytes = 0;
            int files = 0;
            try
            {
                if (Directory.Exists(cacheDir))
                {
                    string[] paths = Directory.GetFiles(cacheDir, "*.wav", SearchOption.TopDirectoryOnly);
                    files = paths.Length;
                    for (int i = 0; i < paths.Length; i++)
                    {
                        try { totalBytes += new FileInfo(paths[i]).Length; }
                        catch { }
                    }
                }
            }
            catch { }

            string limit = config.MaxCacheSizeMB <= 0 ? "Unlimited" : config.MaxCacheSizeMB + " MB";
            return "Runtime cache=" + FormatBytes(totalBytes) + " in " + files + " file(s), limit=" + limit +
                ", auto-cleanup=" + config.AutoManageCache + ", path=" + cacheDir;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024L)
                return bytes + " B";
            if (bytes < 1024L * 1024L)
                return (bytes / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            if (bytes < 1024L * 1024L * 1024L)
                return (bytes / (1024d * 1024d)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            return (bytes / (1024d * 1024d * 1024d)).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
        }

        private void TryStartTtsServer()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = config.PythonExecutablePath;
                psi.Arguments = config.Backend == TtsBackend.Piper ? BuildPiperServerArguments() : BuildKokoroServerArguments();
                psi.UseShellExecute = false;
                psi.CreateNoWindow = config.HideAutoStartedTtsConsole;
                psi.RedirectStandardError = false;
                psi.RedirectStandardOutput = false;

                ttsServerProcess = new Process();
                ttsServerProcess.StartInfo = psi;
                ttsServerProcess.Start();
                UnityEngine.Debug.Log("[Daggerfall Narrator] Started local " + config.Backend + " TTS server.");
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[Daggerfall Narrator] Could not auto-start " + config.Backend + ": " + ex.Message);
            }
        }

        private string ResolveKokoroLanguage()
        {
            return ResolveKokoroLanguage(GetEffectiveKokoroVoice());
        }

        private string ResolveKokoroLanguage(string voiceId)
        {
            string lang = (config.KokoroLanguage ?? string.Empty).Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(lang) && lang != "auto")
                return lang;
            string voice = (voiceId ?? string.Empty).Trim().ToLowerInvariant();
            if (voice.Length > 0)
            {
                char c = voice[0];
                if (c == 'a' || c == 'b' || c == 'e' || c == 'f' || c == 'h' || c == 'i' || c == 'p' || c == 'j' || c == 'z')
                    return c.ToString();
            }
            return "b";
        }

        private string BuildKokoroServerArguments()
        {
            if (string.IsNullOrWhiteSpace(config.KokoroServerScriptPath))
                throw new InvalidOperationException("KokoroServerScriptPath is empty.");

            StringBuilder args = new StringBuilder();
            args.Append("-u \"");
            args.Append(config.KokoroServerScriptPath);
            args.Append("\" --host 127.0.0.1 --port ");
            args.Append(config.KokoroPort);
            args.Append(" --lang ");
            args.Append(ResolveKokoroLanguage());
            args.Append(" --device ");
            args.Append(config.KokoroDeviceMode.ToString().ToLowerInvariant());
            args.Append(" --voice \"");
            args.Append(GetEffectiveKokoroVoice());
            args.Append("\" --speed ");
            args.Append(config.KokoroSpeed.ToString(CultureInfo.InvariantCulture));
            args.Append(" --audio-style ");
            args.Append(config.KokoroAudioStylePreset.ToString().ToLowerInvariant());
            return args.ToString();
        }

        private string BuildPiperServerArguments()
        {
            StringBuilder args = new StringBuilder();
            args.Append("-m piper.http_server -m \"");
            args.Append(config.PiperVoiceName);
            args.Append("\" --host 127.0.0.1 --port ");
            args.Append(config.PiperPort);

            if (!string.IsNullOrWhiteSpace(config.PiperDataDir))
            {
                args.Append(" --data-dir \"");
                args.Append(config.PiperDataDir);
                args.Append("\"");
            }

            return args.ToString();
        }

        private bool PassesUiFlavorHeuristic(DaggerfallMessageBox box)
        {
            if (config.RequireClickAnywhereToClose && !box.ClickAnywhereToClose)
                return false;

            if (config.SkipBoxesWithButtons && HasButtons(box))
                return false;

            if (IsConversationWindow(box.PreviousWindow))
                return false;

            if (config.FallbackRequiresNoPreviousWindow && box.PreviousWindow != null)
                return false;

            return true;
        }

        private bool HasButtons(DaggerfallMessageBox box)
        {
            try
            {
                if (MessageButtonsField == null)
                    return false;
                object value = MessageButtonsField.GetValue(box);
                ICollection collection = value as ICollection;
                return collection != null && collection.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        private bool IsConversationWindow(object window)
        {
            if (window == null)
                return false;
            string name = window.GetType().Name.ToLowerInvariant();
            return name.Contains("talk") || name.Contains("conversation") ||
                   name.Contains("dialogue") || name.Contains("dialog");
        }

        private string ExtractText(DaggerfallMessageBox box)
        {
            try
            {
                if (MessageLabelField == null)
                    return string.Empty;
                MultiFormatTextLabel label = MessageLabelField.GetValue(box) as MultiFormatTextLabel;
                if (label == null || label.TextLabels == null)
                    return string.Empty;

                StringBuilder sb = new StringBuilder();
                foreach (TextLabel textLabel in label.TextLabels)
                {
                    if (textLabel == null || string.IsNullOrWhiteSpace(textLabel.Text))
                        continue;
                    if (sb.Length > 0)
                        sb.Append(' ');
                    sb.Append(textLabel.Text.Trim());
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[Daggerfall Narrator] Text extraction failed: " + ex.Message);
                return string.Empty;
            }
        }

        private void HideMessageBoxVisuals(DaggerfallMessageBox box)
        {
            try
            {
                box.ClickAnywhereToClose = false;
                box.AllowCancel = false;
                box.ParentPanel.BackgroundColor = Color.clear;
                if (MessagePanelField != null)
                {
                    Panel messagePanel = MessagePanelField.GetValue(box) as Panel;
                    if (messagePanel != null)
                        messagePanel.Enabled = false;
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[Daggerfall Narrator] Could not hide popup visuals: " + ex.Message);
            }
        }

        private void SafeClose(DaggerfallMessageBox box)
        {
            if (box == null)
                return;
            try
            {
                box.CloseWindow();
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[Daggerfall Narrator] Popup close failed: " + ex.Message);
            }
        }

        private bool PassesTextFilter(string text)
        {
            if (textFilters != null && textFilters.IsBlocked(text))
                return false;
            if (text.Length < config.MinimumCharacters && !(textFilters != null && textFilters.IsAllowed(text)))
                return false;

            string lower = text.ToLowerInvariant();
            string[] blockedStarts = new string[]
            {
                "are you sure", "do you want to", "save game", "load game", "delete save",
                "overwrite", "cannot save", "press ", "click ", "choose ", "select "
            };
            int i;
            for (i = 0; i < blockedStarts.Length; i++)
            {
                if (lower.StartsWith(blockedStarts[i]))
                    return false;
            }
            return true;
        }

        private void DebugDecision(string text, NarrationDecision decision, string sourceKey)
        {
            if (!config.DebugClassification)
                return;
            string preview = text.Length > 100 ? text.Substring(0, 100) + "..." : text;
            UnityEngine.Debug.Log("[Daggerfall Narrator] " + decision + " [" + sourceKey + "]: " + preview);
        }

        private void SetSubtitle(string text, float targetAlpha)
        {
            activeText = text;
            string displayText = config != null && config.ShowNarratorLabel ? "Narrator: " + text : text;
            activeSubtitlePages = SplitSubtitlePages(displayText, config.SubtitlePageCharacters);
            activeSubtitlePage = 0;
            subtitleTargetAlpha = targetAlpha;
            if (config.SubtitleFadeInSeconds <= 0f)
                subtitleAlpha = targetAlpha;
            subtitleFadeSpeed = config.SubtitleFadeInSeconds > 0f ? 1f / config.SubtitleFadeInSeconds : 1000f;
        }

        private void UpdateSubtitleFade()
        {
            if (Mathf.Approximately(subtitleAlpha, subtitleTargetAlpha))
                return;

            float duration = subtitleTargetAlpha > subtitleAlpha ? config.SubtitleFadeInSeconds : config.SubtitleFadeOutSeconds;
            if (duration <= 0f)
            {
                subtitleAlpha = subtitleTargetAlpha;
                return;
            }

            subtitleAlpha = Mathf.MoveTowards(subtitleAlpha, subtitleTargetAlpha, Time.unscaledDeltaTime / duration);
        }

        private void UpdateSubtitlePage()
        {
            if (!subtitleAudioPlaying || activeSubtitlePages == null || activeSubtitlePages.Count <= 1 || subtitleAudioLength <= 0f)
                return;

            float progress = Mathf.Clamp01((Time.realtimeSinceStartup - subtitleAudioStartRealtime) / subtitleAudioLength);
            int page = Mathf.FloorToInt(progress * activeSubtitlePages.Count);
            if (page >= activeSubtitlePages.Count)
                page = activeSubtitlePages.Count - 1;
            activeSubtitlePage = page;
        }

        private List<string> SplitSubtitlePages(string text, int targetCharacters)
        {
            List<string> pages = new List<string>();
            if (string.IsNullOrWhiteSpace(text))
                return pages;
            if (targetCharacters <= 0 || text.Length <= targetCharacters)
            {
                pages.Add(text);
                return pages;
            }

            string[] words = text.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            StringBuilder page = new StringBuilder();
            int i;
            for (i = 0; i < words.Length; i++)
            {
                string word = words[i];
                if (page.Length > 0 && page.Length + 1 + word.Length > targetCharacters)
                {
                    pages.Add(page.ToString());
                    page.Length = 0;
                }
                if (page.Length > 0)
                    page.Append(' ');
                page.Append(word);
            }
            if (page.Length > 0)
                pages.Add(page.ToString());
            return pages;
        }

        private void EnsureClassicSubtitlePanel()
        {
            if (classicSubtitlePanel != null || DaggerfallUI.Instance == null || DaggerfallUI.Instance.DaggerfallHUD == null)
                return;

            // Character creation runs before the gameplay HUD is fully available. v1.0 attempted
            // to attach the classic subtitle panel on every Update(), which could throw here and
            // abort all later character-creation detection. Wait until the HUD NativePanel exists.
            Panel hudPanel = null;
            try { hudPanel = DaggerfallUI.Instance.DaggerfallHUD.NativePanel; }
            catch { return; }
            if (hudPanel == null)
                return;

            try
            {
                classicSubtitlePanel = new Panel();
                classicSubtitlePanel.Size = new Vector2(280, 44);
                classicSubtitlePanel.Position = new Vector2(20, 145);
                classicSubtitlePanel.BackgroundColor = Color.clear;
                classicSubtitlePanel.Enabled = false;

                classicSubtitleLabel = new MultiFormatTextLabel();
                classicSubtitleLabel.Position = Vector2.zero;
                classicSubtitleLabel.Size = new Vector2(280, 44);
                classicSubtitleLabel.MaxTextWidth = 280;
                classicSubtitleLabel.WrapText = true;
                classicSubtitleLabel.WrapWords = true;
                classicSubtitleLabel.TextAlignment = HorizontalAlignment.Center;
                classicSubtitleLabel.ShadowPosition = DaggerfallUI.DaggerfallDefaultShadowPos;
                classicSubtitleLabel.Font = DaggerfallUI.DefaultFont;
                classicSubtitlePanel.Components.Add(classicSubtitleLabel);
                hudPanel.Components.Add(classicSubtitlePanel);
            }
            catch (Exception ex)
            {
                classicSubtitlePanel = null;
                classicSubtitleLabel = null;
                if (config != null && config.DebugClassification)
                    UnityEngine.Debug.LogWarning("[Daggerfall Narrator] Classic subtitle HUD attachment deferred: " + ex.Message);
            }
        }

        private void UpdateClassicSubtitleOverlay()
        {
            try
            {
            if (config == null || config.SubtitleStyle == SubtitleStyleMode.Modern)
            {
                if (classicSubtitlePanel != null)
                    classicSubtitlePanel.Enabled = false;
                return;
            }
            EnsureClassicSubtitlePanel();
            if (classicSubtitlePanel == null || classicSubtitleLabel == null)
                return;

            bool visible = config.ShowSubtitles && subtitleAlpha > 0.001f && activeSubtitlePages != null && activeSubtitlePages.Count > 0;
            classicSubtitlePanel.Enabled = visible;
            if (!visible)
            {
                classicSubtitleRenderedText = string.Empty;
                return;
            }

            int page = Mathf.Clamp(activeSubtitlePage, 0, activeSubtitlePages.Count - 1);
            string text = activeSubtitlePages[page];
            if (text != classicSubtitleRenderedText)
            {
                classicSubtitleRenderedText = text;
                classicSubtitleLabel.Clear();
                classicSubtitleLabel.TextScale = Mathf.Clamp(config.SubtitleFontSize / 22f, 0.65f, 2.0f);
                classicSubtitleLabel.AddTextLabel(text, DaggerfallUI.DefaultFont, DaggerfallUI.DaggerfallDefaultTextColor);
            }

            float alpha = Mathf.Clamp01(subtitleAlpha);
            for (int i = 0; i < classicSubtitleLabel.TextLabels.Count; i++)
            {
                TextLabel label = classicSubtitleLabel.TextLabels[i];
                Color tc = DaggerfallUI.DaggerfallDefaultTextColor; tc.a *= alpha; label.TextColor = tc;
                Color sc = DaggerfallUI.DaggerfallDefaultShadowColor; sc.a *= alpha; label.ShadowColor = sc;
            }

            float y = 145f;
            if (config.SubtitlePosition == SubtitlePosition.TopCenter) y = 18f;
            else if (config.SubtitlePosition == SubtitlePosition.Center) y = 82f;
            classicSubtitlePanel.Position = new Vector2(20, y);
            classicSubtitlePanel.BackgroundColor = config.SubtitleStyle == SubtitleStyleMode.ClassicBackdrop
                ? new Color(0f, 0f, 0f, Mathf.Clamp01(config.SubtitleBackgroundOpacity) * alpha)
                : Color.clear;
                    }
            catch (Exception ex)
            {
                // Subtitle rendering must never prevent gameplay/text detection (especially during chargen).
                if (config != null && config.DebugClassification)
                    UnityEngine.Debug.LogWarning("[Daggerfall Narrator] Classic subtitle update skipped: " + ex.Message);
            }
        }

        private void BuildSubtitleStyles()
        {
            subtitleStyle = new GUIStyle(GUI.skin.label);
            subtitleStyle.alignment = TextAnchor.MiddleCenter;
            subtitleStyle.wordWrap = true;
            subtitleStyle.richText = false;
            subtitleStyle.fontSize = Math.Max(10, config.SubtitleFontSize);
            subtitleStyle.normal.textColor = Color.white;
            subtitleStyle.padding = new RectOffset(14, 14, 9, 9);

            if (subtitleBackgroundTexture != null)
                Destroy(subtitleBackgroundTexture);
            subtitleBackgroundTexture = new Texture2D(1, 1);
            subtitleBackgroundTexture.SetPixel(0, 0, Color.white);
            subtitleBackgroundTexture.Apply();

            subtitleBackgroundStyle = new GUIStyle(GUI.skin.box);
            subtitleBackgroundStyle.normal.background = subtitleBackgroundTexture;
        }

        private void OnGUI()
        {
            if (subtitleStyle == null || subtitleBackgroundStyle == null || subtitleBackgroundTexture == null)
                BuildSubtitleStyles();

            if (config == null || config.SubtitleStyle != SubtitleStyleMode.Modern || !config.ShowSubtitles || subtitleAlpha <= 0.001f || activeSubtitlePages == null || activeSubtitlePages.Count == 0)
                return;

            int page = Mathf.Clamp(activeSubtitlePage, 0, activeSubtitlePages.Count - 1);
            string text = activeSubtitlePages[page];

            float width = Screen.width * Mathf.Clamp(config.SubtitleWidthPercent, 20f, 100f) / 100f;
            float x = (Screen.width - width) * 0.5f;
            float height = subtitleStyle.CalcHeight(new GUIContent(text), width - 28f) + 18f;
            float y;

            if (config.SubtitlePosition == SubtitlePosition.TopCenter)
                y = config.SubtitleMarginPixels;
            else if (config.SubtitlePosition == SubtitlePosition.Center)
                y = (Screen.height - height) * 0.5f;
            else
                y = Screen.height - height - config.SubtitleMarginPixels;

            Rect rect = new Rect(x, y, width, height);

            if (config.SubtitleBackgroundOpacity > 0f)
            {
                Color old = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, Mathf.Clamp01(config.SubtitleBackgroundOpacity) * subtitleAlpha);
                GUI.Box(rect, GUIContent.none, subtitleBackgroundStyle);
                GUI.color = old;
            }

            Color previous = subtitleStyle.normal.textColor;
            subtitleStyle.normal.textColor = new Color(1f, 1f, 1f, subtitleAlpha);
            GUI.Label(rect, text, subtitleStyle);
            subtitleStyle.normal.textColor = previous;
        }

        private void RegisterConsoleCommands()
        {
            try
            {
                ConsoleCommandsDatabase.RegisterCommand("narrator_test", "Tests Daggerfall Narrator TTS and subtitle output.", "narrator_test", ConsoleNarratorTest);
                ConsoleCommandsDatabase.RegisterCommand("narrator_say", "Speaks arbitrary text through the narrator.", "narrator_say <text>", ConsoleNarratorSay);
                ConsoleCommandsDatabase.RegisterCommand("narrator_reload", "Reloads DaggerfallNarrator.ini and Pronunciations.txt.", "narrator_reload", ConsoleNarratorReload);
                ConsoleCommandsDatabase.RegisterCommand("narrator_status", "Shows narrator paths and runtime status.", "narrator_status", ConsoleNarratorStatus);
                ConsoleCommandsDatabase.RegisterCommand("narrator_cache_status", "Shows runtime narration cache size and limit.", "narrator_cache_status", ConsoleNarratorCacheStatus);
                ConsoleCommandsDatabase.RegisterCommand("narrator_clear_cache", "Clears runtime-generated narration WAVs. VoicePack files are never touched.", "narrator_clear_cache", ConsoleNarratorClearCache);
                ConsoleCommandsDatabase.RegisterCommand("narrator_voices", "Shows the Kokoro voice-list endpoint and current voice ID.", "narrator_voices", ConsoleNarratorVoices);
                ConsoleCommandsDatabase.RegisterCommand("narrator_test_voice", "Auditions any Kokoro voice without changing Narrator settings.", "narrator_test_voice <voiceId>", ConsoleNarratorTestVoice);
                ConsoleCommandsDatabase.RegisterCommand("narrator_test_preset", "Auditions a Narrator content-style test line.", "narrator_test_preset <atmosphere|book|status|tutorial|opening>", ConsoleNarratorTestPreset);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[Daggerfall Narrator] Console commands could not be registered: " + ex.Message);
            }
        }

        private static string ConsoleNarratorTest(params string[] args)
        {
            if (instance == null)
                return "Daggerfall Narrator is not initialized.";
            instance.EnqueueManual("The Daggerfall narrator is working. You hear a cold wind blowing through the halls of the Iliac Bay.");
            return "Narrator test queued.";
        }

        private static string ConsoleNarratorTestVoice(params string[] args)
        {
            if (instance == null)
                return "Daggerfall Narrator is not initialized.";
            if (args == null || args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
                return "Usage: narrator_test_voice <voiceId>";
            string voice = args[0].Trim();
            instance.EnqueueManualWithVoice("Voice test. The wind carries an old story across the Iliac Bay.", voice);
            return "Narrator voice test queued for " + voice + ".";
        }

        private static string ConsoleNarratorTestPreset(params string[] args)
        {
            if (instance == null)
                return "Daggerfall Narrator is not initialized.";
            if (args == null || args.Length == 0)
                return "Usage: narrator_test_preset <atmosphere|book|status|tutorial|opening>";
            string key = args[0].Trim().ToLowerInvariant();
            int preset = key == "atmosphere" ? 1 : key == "book" ? 2 : key == "status" ? 3 : key == "tutorial" ? 4 : key == "opening" ? 5 : 0;
            if (preset == 0)
                return "Unknown preset. Use atmosphere, book, status, tutorial, or opening.";
            instance.RunSettingsSoundTest(preset);
            return "Narrator preset test queued.";
        }

        private void RunSettingsSoundTest(int preset)
        {
            string text = preset == 1 ? "A cold draft moves through the passage, carrying the smell of old stone." :
                preset == 2 ? "The old volume opens with a dry whisper of parchment and dust." :
                preset == 3 ? "You feel rested, alert, and ready to continue your journey." :
                preset == 4 ? "Use caution. Some doors hide more than an empty room." :
                preset == 5 ? "You wake and look around the room. Some time has passed since the shipwreck." : string.Empty;
            if (!string.IsNullOrEmpty(text))
                EnqueueManual(text);
        }

        private static string ConsoleNarratorSay(params string[] args)
        {
            if (instance == null)
                return "Daggerfall Narrator is not initialized.";
            if (args == null || args.Length == 0)
                return "Usage: narrator_say <text>";
            instance.EnqueueManual(string.Join(" ", args));
            return "Narration queued.";
        }

        private static string ConsoleNarratorReload(params string[] args)
        {
            if (instance == null)
                return "Daggerfall Narrator is not initialized.";
            instance.LoadSettings();
            return "Daggerfall Narrator settings reloaded.";
        }

        private static string ConsoleNarratorStatus(params string[] args)
        {
            if (instance == null)
                return "Daggerfall Narrator is not initialized.";
            return "Enabled=" + instance.config.Enabled +
                ", Mode=" + instance.config.Mode +
                ", Queue=" + instance.narrationQueue.Count +
                ", Backend=" + instance.config.Backend +
                ", Endpoint=http://127.0.0.1:" + (instance.config.Backend == TtsBackend.Piper ? instance.config.PiperPort : instance.config.KokoroPort) +
                ", Config=" + instance.configPath;
        }

        private static string ConsoleNarratorCacheStatus(params string[] args)
        {
            if (instance == null)
                return "Daggerfall Narrator is not initialized.";
            return instance.GetCacheStatusText();
        }

        private static string ConsoleNarratorVoices(params string[] args)
        {
            if (instance == null)
                return "Daggerfall Narrator is not initialized.";
            return "Current Kokoro voice=" + instance.GetEffectiveKokoroVoice() + ". Voice list: http://127.0.0.1:" +
                instance.config.KokoroPort + "/voices . Normal configuration uses Accent / Gender / Delivery under the Voice section; narrator_test_voice remains available for advanced auditioning.";
        }

        private static string ConsoleNarratorClearCache(params string[] args)
        {
            if (instance == null)
                return "Daggerfall Narrator is not initialized.";
            if (instance.queueRoutineRunning || (instance.audioSource != null && instance.audioSource.isPlaying))
                return "Narrator is currently speaking. Try narrator_clear_cache after the current line finishes.";

            long deletedBytes;
            int deletedFiles;
            instance.ClearRuntimeCache(out deletedFiles, out deletedBytes);
            return "Cleared " + deletedFiles + " runtime cache file(s), " + FormatBytes(deletedBytes) + ". VoicePack files were not touched.";
        }

        internal static string NormalizeForSpeech(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            text = text.Replace("\r", " ").Replace("\n", " ");
            text = text.Replace("<--->", " ");
            text = Regex.Replace(text, @"\s+", " ");
            return text.Trim();
        }

        internal static string NormalizeForMatch(string text)
        {
            text = NormalizeForSpeech(text).ToLowerInvariant();
            text = Regex.Replace(text, @"[^\p{L}\p{N}']+", " ");
            text = Regex.Replace(text, @"\s+", " ");
            return text.Trim();
        }

        private static string JsonEscape(string value)
        {
            if (value == null)
                return string.Empty;
            return value.Replace("\\", "\\\\")
                        .Replace("\"", "\\\"")
                        .Replace("\r", "\\r")
                        .Replace("\n", "\\n")
                        .Replace("\t", "\\t");
        }

        private static string Sha1(string input)
        {
            using (SHA1 sha1 = SHA1.Create())
            {
                byte[] data = sha1.ComputeHash(Encoding.UTF8.GetBytes(input));
                return BitConverter.ToString(data).Replace("-", string.Empty).ToLowerInvariant();
            }
        }
    }

    internal class NarrationItem
    {
        public string Text;
        public string SourceKey;
        public string GroupKey;
        public bool GroupRetained;
        public bool GroupReleased;
        public DaggerfallMessageBox ModalBox;
        public IUserInterfaceWindow BoundWindow;
        public bool CancelIfWindowClosed;
        public bool Cancelled;
        public NarrationCategory Category = NarrationCategory.Flavor;
        public bool ShowSubtitle = true;
        public bool IsFirstChunk;
        public bool IsFinalChunk;
        public bool PrefetchStarted;
        public bool PrefetchComplete;
        public bool PrefetchFailed;
        public string PreparedWavPath;
        public string VoiceOverride;
        public DaggerfallMessageBox TimingBox;
        public bool OriginalClickAnywhereToClose;
        public bool OriginalAllowCancel;
        public bool UnlockWindowOnSpeechStart;
        public bool UnlockWindowOnFinish;
    }

    internal enum NarrationCategory
    {
        Flavor,
        QuestNote,
        Book,
        CharacterQuestion,
        CharacterDescription,
        HudStatus,
        QuestLog,
        History,
        External,
        Manual
    }

    internal enum ReadableMode
    {
        Off = 0,
        Manual = 1,
        Auto = 2
    }

    internal enum ReadableKind
    {
        Book,
        QuestNote,
        QuestLog,
        History
    }

    internal enum SubtitleStyleMode
    {
        Modern = 0,
        ClassicShadowed = 1,
        ClassicBackdrop = 2
    }

    internal class ReadButtonContext
    {
        public IUserInterfaceWindow Window;
        public Button Button;
        public ReadableKind Kind;
        public NarrationCategory Category;
    }

    internal enum NarrationDecision
    {
        Unclassified,
        NarrateQuestFlavor,
        NarrateQuestReadableItem,
        BlockedQuestDialogue,
        BlockedQuestAmbiguousSay,
        BlockedQuestPrompt,
        BlockedQuestRumor,
        BlockedQuestLog,
        BlockedNpcQuestOwnership
    }

    internal enum QuestMessageKind
    {
        Unknown,
        GenericMessage,
        Dialogue,
        Rumor,
        LogEntry
    }

    internal class ClassificationResult
    {
        public NarrationDecision Decision = NarrationDecision.Unclassified;
        public string NarrationText = string.Empty;
        public string SourceKey = string.Empty;
        public bool PreserveOriginalPopup = false;
        public NarrationCategory Category = NarrationCategory.Flavor;
        public bool ShowSubtitle = true;
    }

    internal class QuestSourceClassifier
    {
        private readonly NarratorConfig config;
        private readonly Dictionary<string, Dictionary<int, QuestMessageInfo>> sourceCache =
            new Dictionary<string, Dictionary<int, QuestMessageInfo>>(StringComparer.OrdinalIgnoreCase);

        private static readonly FieldInfo ActiveQuestsField = typeof(QuestMachine)
            .GetField("quests", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo QuestResourcesField = typeof(Quest)
            .GetField("resources", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly Regex MessageHeaderRegex = new Regex(
            @"^\s*(?<label>[A-Za-z][A-Za-z0-9_]*)\s*:\s*\[?\s*(?<id>\d+)\s*\]?",
            RegexOptions.Compiled);

        public QuestSourceClassifier(NarratorConfig config)
        {
            this.config = config;
        }

        public ClassificationResult Classify(DaggerfallMessageBox box, string renderedText)
        {
            ClassificationResult result = new ClassificationResult();
            if (!config.UseQuestSourceClassification)
                return result;

            List<QuestMatch> matches = FindQuestMatches(renderedText);
            if (matches.Count == 0)
                return result;

            int i;

            // Cliffworms/RebornZA Leveling Inspiration uses the LVLUP01 quest (and historically
            // loose LVLUP quest-pack files) to present Morrowind/Oblivion-style level-up prose.
            // These messages are intentional narrator/flavor text even when the quest uses `say`,
            // so handle the LVLUP quest family before the conservative generic `say` blocker.
            if (config.NarrateLevelingInspiration)
            {
                for (i = 0; i < matches.Count; i++)
                {
                    QuestMatch inspirationMatch = matches[i];
                    if (inspirationMatch.QuestName.StartsWith("LVLUP", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Decision = NarrationDecision.NarrateQuestFlavor;
                        result.NarrationText = inspirationMatch.FullText;
                        result.SourceKey = "levelinginspiration:" + inspirationMatch.QuestName + ":" + inspirationMatch.MessageId + ":" + inspirationMatch.Variant;
                        result.PreserveOriginalPopup = !config.ReplaceEligiblePopups;
                        result.Category = NarrationCategory.Flavor;
                        result.ShowSubtitle = true;
                        return result;
                    }
                }
            }

            // DFU 1.1's built-in tutorial (_TUTOR__) intentionally uses interactive
            // `prompt` messages (including the opening tutorial question) and `say` pages.
            // Those would normally be blocked by the conservative dialogue/prompt rules.
            // Treat only this known tutorial quest as preserved instructional narration.
            if (config.NarrateTutorialMessages)
            {
                for (i = 0; i < matches.Count; i++)
                {
                    QuestMatch tutorialMatch = matches[i];
                    if (tutorialMatch.QuestName.Equals("_TUTOR__", StringComparison.OrdinalIgnoreCase) &&
                        (tutorialMatch.UsedByPrompt || tutorialMatch.UsedBySay))
                    {
                        result.Decision = NarrationDecision.NarrateQuestFlavor;
                        result.NarrationText = tutorialMatch.FullText;
                        result.SourceKey = "tutorial:" + tutorialMatch.MessageId + ":" + tutorialMatch.Variant;
                        result.PreserveOriginalPopup = true;
                        result.Category = NarrationCategory.Flavor;
                        result.ShowSubtitle = false;
                        return result;
                    }
                }
            }

            for (i = 0; i < matches.Count; i++)
            {
                QuestMatch match = matches[i];
                if (match.Kind == QuestMessageKind.Dialogue)
                {
                    result.Decision = NarrationDecision.BlockedQuestDialogue;
                    return result;
                }
                if (match.Kind == QuestMessageKind.Rumor)
                {
                    result.Decision = NarrationDecision.BlockedQuestRumor;
                    return result;
                }
                if (match.Kind == QuestMessageKind.LogEntry || match.UsedByLog)
                {
                    result.Decision = NarrationDecision.BlockedQuestLog;
                    return result;
                }
                if (match.UsedByPrompt)
                {
                    result.Decision = NarrationDecision.BlockedQuestPrompt;
                    return result;
                }
                // Vanilla quests also use generic Message: records for direct NPC speech.
                // A numeric message referenced by a QBN `say` action is therefore ambiguous.
                // Default is conservative: do not narrate it unless explicitly enabled in the INI.
                if (match.UsedBySay && !config.NarrateAmbiguousSayMessages)
                {
                    result.Decision = NarrationDecision.BlockedQuestAmbiguousSay;
                    return result;
                }
            }

            for (i = 0; i < matches.Count; i++)
            {
                QuestMatch match = matches[i];
                if (match.Kind != QuestMessageKind.GenericMessage)
                    continue;

                if (match.UsedByReadableItem && config.NarrateQuestItemNotes && !match.UsedBySay)
                {
                    result.Decision = NarrationDecision.NarrateQuestReadableItem;
                    result.NarrationText = match.FullText;
                    result.SourceKey = "questitem:" + match.QuestName + ":" + match.MessageId + ":" + match.Variant;
                    result.PreserveOriginalPopup = config.KeepQuestItemNotesPopup;
                    result.Category = NarrationCategory.QuestNote;
                    result.ShowSubtitle = config.ShowSubtitlesForQuestNotes;
                    return result;
                }

                bool explicitlyNarratable = match.UsedByNotify && config.NarrateNotifyMessages;
                bool genericNarratable = config.NarrateGenericQuestMessages && !match.UsedByPrompt && !match.UsedByLog;
                if (explicitlyNarratable || genericNarratable)
                {
                    result.Decision = NarrationDecision.NarrateQuestFlavor;
                    result.NarrationText = match.FullText;
                    result.SourceKey = "quest:" + match.QuestName + ":" + match.MessageId + ":" + match.Variant;
                    result.Category = NarrationCategory.Flavor;
                    result.ShowSubtitle = true;
                    return result;
                }
            }

            return result;
        }

        private List<QuestMatch> FindQuestMatches(string renderedText)
        {
            List<QuestMatch> matches = new List<QuestMatch>();
            string shown = DaggerfallNarratorMod.NormalizeForMatch(renderedText);
            if (shown.Length == 0)
                return matches;

            foreach (Quest quest in GetRunningQuests())
            {
                Dictionary<int, QuestMessageInfo> sourceMap = GetSourceMap(quest);
                foreach (KeyValuePair<int, QuestMessageInfo> kvp in sourceMap)
                {
                    Message message = quest.GetMessage(kvp.Key);
                    if (message == null)
                        continue;

                    int variant;
                    for (variant = 0; variant < message.VariantCount; variant++)
                    {
                        string candidate = ExpandedVariantToString(message, variant);
                        string normalizedCandidate = DaggerfallNarratorMod.NormalizeForMatch(candidate);
                        if (normalizedCandidate.Length == 0)
                            continue;

                        if (normalizedCandidate == shown ||
                            normalizedCandidate.Contains(shown) ||
                            (shown.Length > 45 && shown.Contains(normalizedCandidate)))
                        {
                            matches.Add(new QuestMatch(quest.QuestName, kvp.Key, kvp.Value, variant, candidate));
                            break;
                        }
                    }
                }
            }
            return matches;
        }

        private IEnumerable<Quest> GetRunningQuests()
        {
            if (QuestMachine.Instance == null || ActiveQuestsField == null)
                yield break;

            object raw;
            try
            {
                raw = ActiveQuestsField.GetValue(QuestMachine.Instance);
            }
            catch
            {
                yield break;
            }

            IDictionary dictionary = raw as IDictionary;
            if (dictionary == null)
                yield break;

            foreach (DictionaryEntry entry in dictionary)
            {
                Quest quest = entry.Value as Quest;
                if (quest != null)
                    yield return quest;
            }
        }

        private Dictionary<int, QuestMessageInfo> GetSourceMap(Quest quest)
        {
            Dictionary<int, QuestMessageInfo> map;
            if (sourceCache.TryGetValue(quest.QuestName, out map))
                return map;
            map = ParseQuestSource(quest);
            MarkReadableQuestItemMessages(quest, map);
            sourceCache[quest.QuestName] = map;
            return map;
        }

        private Dictionary<int, QuestMessageInfo> ParseQuestSource(Quest quest)
        {
            Dictionary<int, QuestMessageInfo> map = new Dictionary<int, QuestMessageInfo>();
            string[] source;
            try
            {
                source = QuestMachine.Instance.GetQuestSourceText(quest.QuestName);
            }
            catch
            {
                return map;
            }

            bool inQrc = false;
            bool inQbn = false;
            foreach (string rawLine in source)
            {
                string line = rawLine == null ? string.Empty : rawLine.Trim();
                if (line.Equals("QRC:", StringComparison.OrdinalIgnoreCase))
                {
                    inQrc = true;
                    inQbn = false;
                    continue;
                }
                if (line.Equals("QBN:", StringComparison.OrdinalIgnoreCase))
                {
                    inQrc = false;
                    inQbn = true;
                    continue;
                }
                if (line.Length == 0 || line.StartsWith("--"))
                    continue;

                if (inQrc)
                {
                    Match match = MessageHeaderRegex.Match(line);
                    if (!match.Success)
                        continue;

                    int id;
                    if (!int.TryParse(match.Groups["id"].Value, out id))
                        continue;

                    string label = match.Groups["label"].Value;
                    QuestMessageInfo info = new QuestMessageInfo();
                    info.Kind = ClassifyHeader(label, id);
                    map[id] = info;
                    continue;
                }

                if (inQbn)
                    MarkQbnMessageUses(line, map);
            }
            return map;
        }


        private void MarkReadableQuestItemMessages(Quest quest, Dictionary<int, QuestMessageInfo> map)
        {
            if (QuestResourcesField == null || quest == null)
                return;

            try
            {
                IDictionary resources = QuestResourcesField.GetValue(quest) as IDictionary;
                if (resources == null)
                    return;

                foreach (DictionaryEntry entry in resources)
                {
                    object resource = entry.Value;
                    if (resource == null || !resource.GetType().Name.Equals("Item", StringComparison.OrdinalIgnoreCase))
                        continue;

                    int usedMessageId = 0;
                    PropertyInfo property = resource.GetType().GetProperty("UsedMessageID", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (property != null)
                    {
                        object value = property.GetValue(resource, null);
                        if (value is int)
                            usedMessageId = (int)value;
                    }
                    else
                    {
                        FieldInfo field = resource.GetType().GetField("UsedMessageID", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (field != null)
                        {
                            object value = field.GetValue(resource);
                            if (value is int)
                                usedMessageId = (int)value;
                        }
                    }

                    QuestMessageInfo info;
                    if (usedMessageId > 0 && map.TryGetValue(usedMessageId, out info))
                        info.UsedByReadableItem = true;
                }
            }
            catch (Exception ex)
            {
                if (config.DebugClassification)
                    UnityEngine.Debug.LogWarning("[Daggerfall Narrator] Quest-item message scan failed: " + ex.Message);
            }
        }

        private void MarkQbnMessageUses(string line, Dictionary<int, QuestMessageInfo> map)
        {
            string lower = line.ToLowerInvariant();
            foreach (KeyValuePair<int, QuestMessageInfo> kvp in map)
            {
                string idText = kvp.Key.ToString(CultureInfo.InvariantCulture);
                if (!Regex.IsMatch(lower, @"\b" + Regex.Escape(idText) + @"\b"))
                    continue;

                QuestMessageInfo info = kvp.Value;
                if (Regex.IsMatch(lower, @"\bsay\s+" + Regex.Escape(idText) + @"\b"))
                    info.UsedBySay = true;
                if (Regex.IsMatch(lower, @"\bprompt\s+" + Regex.Escape(idText) + @"\b"))
                    info.UsedByPrompt = true;
                if (Regex.IsMatch(lower, @"\bnotify\s+" + Regex.Escape(idText) + @"\b"))
                    info.UsedByNotify = true;
                if (Regex.IsMatch(lower, @"\blog\s+" + Regex.Escape(idText) + @"\b"))
                    info.UsedByLog = true;
            }
        }

        private QuestMessageKind ClassifyHeader(string label, int id)
        {
            string lower = label.ToLowerInvariant();
            if (lower == "message")
                return QuestMessageKind.GenericMessage;
            if (lower.Contains("log"))
                return QuestMessageKind.LogEntry;
            if (lower.StartsWith("rumor"))
                return QuestMessageKind.Rumor;
            if (lower.StartsWith("questor") || lower == "acceptquest" || lower == "refusequest" ||
                lower == "questcomplete" || lower == "questfail" || (id >= 1000 && id <= 1009))
                return QuestMessageKind.Dialogue;
            return QuestMessageKind.Unknown;
        }

        private string ExpandedVariantToString(Message message, int variant)
        {
            try
            {
                TextFile.Token[] tokens = message.GetTextTokensByVariant(variant, true);
                if (tokens == null)
                    return string.Empty;

                StringBuilder sb = new StringBuilder();
                foreach (TextFile.Token token in tokens)
                {
                    if (token.formatting != TextFile.Formatting.Text || string.IsNullOrWhiteSpace(token.text))
                        continue;
                    if (sb.Length > 0)
                        sb.Append(' ');
                    sb.Append(token.text.Trim());
                }
                return DaggerfallNarratorMod.NormalizeForSpeech(sb.ToString());
            }
            catch
            {
                return string.Empty;
            }
        }

        private class QuestMessageInfo
        {
            public QuestMessageKind Kind;
            public bool UsedBySay;
            public bool UsedByPrompt;
            public bool UsedByNotify;
            public bool UsedByLog;
            public bool UsedByReadableItem;
        }

        private struct QuestMatch
        {
            public string QuestName;
            public int MessageId;
            public QuestMessageKind Kind;
            public int Variant;
            public string FullText;
            public bool UsedBySay;
            public bool UsedByPrompt;
            public bool UsedByNotify;
            public bool UsedByLog;
            public bool UsedByReadableItem;

            public QuestMatch(string questName, int messageId, QuestMessageInfo info, int variant, string fullText)
            {
                QuestName = questName;
                MessageId = messageId;
                Kind = info.Kind;
                Variant = variant;
                FullText = fullText;
                UsedBySay = info.UsedBySay;
                UsedByPrompt = info.UsedByPrompt;
                UsedByNotify = info.UsedByNotify;
                UsedByLog = info.UsedByLog;
                UsedByReadableItem = info.UsedByReadableItem;
            }
        }
    }

    internal enum NarratorMode
    {
        Immersive,
        Pause
    }

    internal enum MessageTimingMode
    {
        FollowWindow,
        HoldUntilSpeechStarts,
        HoldUntilSpeechEnds
    }

    internal enum SubtitlePosition
    {
        BottomCenter,
        Center,
        TopCenter
    }

    internal enum TtsBackend
    {
        Kokoro,
        Piper
    }

    internal enum KokoroDevice
    {
        Cpu,
        Cuda,
        Auto
    }

    internal enum KokoroAudioStyle
    {
        Clean,
        Cdrom,
        Dos
    }

    internal class NarratorConfig
    {
        public bool Enabled = true;
        public NarratorMode Mode = NarratorMode.Immersive;
        public bool ReplaceEligiblePopups = true;
        public MessageTimingMode MessageTiming = MessageTimingMode.FollowWindow;

        public bool UseQuestSourceClassification = true;
        public bool NarrateGenericQuestMessages = true;
        public bool NarrateNotifyMessages = true;
        public bool NarrateAmbiguousSayMessages = false;
        public bool NarrateQuestItemNotes = true;
        public bool KeepQuestItemNotesPopup = true;
        public bool NarrateBooks = true;
        public ReadableMode BookReadingMode = ReadableMode.Manual;
        public ReadableMode QuestNoteReadingMode = ReadableMode.Manual;
        public ReadableMode QuestLogReadingMode = ReadableMode.Manual;
        public ReadableMode HistoryReadingMode = ReadableMode.Manual;
        public bool NarrateCharacterCreationQuestions = true;
        public bool NarrateCharacterCreationDescriptions = true;
        public bool NarrateTutorialMessages = true;
        public bool NarrateOpeningNarration = true;
        public bool NarratePlayerStatusPopups = true;
        // Explicit compatibility for Cliffworms/RebornZA Leveling Inspiration (LVLUP quest family).
        public bool NarrateLevelingInspiration = true;
        // Short non-modal HUD notifications (DaggerfallUI.AddHUDText), e.g. rest/status/environment lines.
        public bool NarrateHudStatusMessages = true;
        // DFU raises EnemyAlertActive for hostile encounters; suppress HUD speech while active to avoid combat-log spam.
        public bool SuppressHudStatusDuringCombat = true; // Legacy INI key; combat narration is hard-blocked regardless.
        public bool NarrateClimatesCalories = true;
        public int HudStatusMinimumCharacters = 4;
        public float HudStatusRepeatCooldownSeconds = 8f;
        public bool StopBookNarrationOnClose = true;
        public int ReadableChunkCharacters = 700;
        public bool StreamLongNarration = true;
        public int FirstChunkCharacters = 220;
        public bool PrefetchNextChunk = false;
        public bool NarrateUnclassifiedFlavorPopups = true;
        public bool FallbackRequiresNoPreviousWindow = true;
        public bool RequireClickAnywhereToClose = true;
        public bool SkipBoxesWithButtons = true;
        public bool DebugClassification = false;
        public int MinimumCharacters = 20;
        public float ChunkDedupSeconds = 3f;
        public int MaxQueueItems = 12;

        public bool ShowSubtitles = true;
        public bool ShowNarratorLabel = false;
        public bool ShowSubtitlesForBooks = false;
        public bool ShowSubtitlesForQuestNotes = false;
        public bool ShowSubtitlesForCharacterQuestions = false;
        public bool ShowSubtitlesForCharacterDescriptions = false;
        // HUD status text is already visible in DFU's HUD, so avoid duplicating it in narrator subtitles by default.
        public bool ShowSubtitlesForHudStatus = false;
        public bool ShowSubtitlesForManual = true;
        public SubtitlePosition SubtitlePosition = SubtitlePosition.BottomCenter;
        public float SubtitleWidthPercent = 72f;
        public int SubtitleMarginPixels = 90;
        public int SubtitleFontSize = 22;
        public SubtitleStyleMode SubtitleStyle = SubtitleStyleMode.ClassicShadowed;
        public float SubtitleBackgroundOpacity = 0.55f;
        public float SubtitleFadeInSeconds = 0.10f;
        public float SubtitleFadeOutSeconds = 0.25f;
        public float SubtitleTailSeconds = 0.65f;
        public int SubtitlePageCharacters = 260;
        public float TtsFailureSubtitleSeconds = 4f;

        // Runtime-generated speech cache. VoicePack is separate and is never auto-deleted.
        public bool AutoManageCache = true;
        public int MaxCacheSizeMB = 1024; // 0 = unlimited
        public bool CacheCleanupOnStartup = true;
        public float CacheCleanupIntervalMinutes = 30f;
        public float CacheCleanupTargetPercent = 0.85f;
        public float CacheMinimumAgeHours = 24f;
        public float CacheHardLimitMultiplier = 1.25f;

        public float Volume = 0.90f;
        public bool UseGameSoundVolume = true;
        public KeyCode SkipKey = KeyCode.F9;
        public KeyCode ToggleKey = KeyCode.F10;

        public TtsBackend Backend = TtsBackend.Kokoro;
        public bool AutoStartTtsServer = false;
        public bool StopAutoStartedTtsOnExit = true;
        public bool HideAutoStartedTtsConsole = true;
        public string PythonExecutablePath = "py";
        public int TtsHttpAttempts = 3;
        public float TtsRetryDelaySeconds = 0.75f;
        public int TtsHttpTimeoutSeconds = 120;

        public string KokoroVoice = "bm_fable"; // resolved from accent/gender/delivery selectors.
        public string KokoroVoiceOverride = string.Empty;
        public int VoiceAccent = 1;
        public int VoiceGender = 0;
        public int VoiceDelivery = 2;
        public int VoiceDepth = 55;
        public string KokoroLanguage = "auto";
        public KokoroDevice KokoroDeviceMode = KokoroDevice.Cpu;
        public int KokoroPort = 5000;
        public bool AutoStartVoiceEngine = true;
        public int ObservationVoice = 0; // 0 Dungeon Master, 1 Player, 2 Off
        public float KokoroSpeed = 0.95f;
        public KokoroAudioStyle KokoroAudioStylePreset = KokoroAudioStyle.Clean;
        public string KokoroServerScriptPath = @"C:\DaggerfallNarrator\Kokoro\KokoroServer.py";

        public string PiperVoiceName = "en_US-lessac-medium";
        public string PiperDataDir = @"C:\PiperVoices";
        public int PiperPort = 5000;
        public float PiperLengthScale = 1.0f;

        public static NarratorConfig LoadOrCreate(string path)
        {
            NarratorConfig cfg = new NarratorConfig();
            if (!File.Exists(path))
            {
                cfg.Save(path);
                return cfg;
            }

            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";"))
                    continue;
                int eq = line.IndexOf('=');
                if (eq < 0)
                    continue;
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string value = line.Substring(eq + 1).Trim();

                bool b;
                int i;
                float f;
                NarratorMode mode;
                SubtitlePosition position;
                KeyCode keyCode;
                TtsBackend backend;
                KokoroDevice device;
                KokoroAudioStyle audioStyle;
                ReadableMode readableMode;
                SubtitleStyleMode subtitleStyleMode;

                switch (key)
                {
                    case "enabled": if (bool.TryParse(value, out b)) cfg.Enabled = b; break;
                    case "mode": if (Enum.TryParse<NarratorMode>(value, true, out mode)) cfg.Mode = mode; break;
                    case "replaceeligiblepopups": if (bool.TryParse(value, out b)) cfg.ReplaceEligiblePopups = b; break;
                    case "messageboxtiming": { MessageTimingMode mt; if (Enum.TryParse<MessageTimingMode>(value, true, out mt)) cfg.MessageTiming = mt; } break;

                    case "usequestsourceclassification": if (bool.TryParse(value, out b)) cfg.UseQuestSourceClassification = b; break;
                    case "narrategenericquestmessages": if (bool.TryParse(value, out b)) cfg.NarrateGenericQuestMessages = b; break;
                    case "narratenotifymessages": if (bool.TryParse(value, out b)) cfg.NarrateNotifyMessages = b; break;
                    case "narrateambiguoussaymessages": if (bool.TryParse(value, out b)) cfg.NarrateAmbiguousSayMessages = b; break;
                    case "narratequestitemnotes": if (bool.TryParse(value, out b)) cfg.NarrateQuestItemNotes = b; break;
                    case "keepquestitemnotespopup": if (bool.TryParse(value, out b)) cfg.KeepQuestItemNotesPopup = b; break;
                    case "narratebooks": if (bool.TryParse(value, out b)) cfg.NarrateBooks = b; break;
                    case "bookreadingmode": if (Enum.TryParse<ReadableMode>(value, true, out readableMode)) cfg.BookReadingMode = readableMode; break;
                    case "questnotereadingmode": if (Enum.TryParse<ReadableMode>(value, true, out readableMode)) cfg.QuestNoteReadingMode = readableMode; break;
                    case "questlogreadingmode": if (Enum.TryParse<ReadableMode>(value, true, out readableMode)) cfg.QuestLogReadingMode = readableMode; break;
                    case "historyreadingmode": if (Enum.TryParse<ReadableMode>(value, true, out readableMode)) cfg.HistoryReadingMode = readableMode; break;
                    case "narratecharactercreationquestions": if (bool.TryParse(value, out b)) cfg.NarrateCharacterCreationQuestions = b; break;
                    case "narratecharactercreationdescriptions": if (bool.TryParse(value, out b)) cfg.NarrateCharacterCreationDescriptions = b; break;
                    case "narratetutorialmessages": if (bool.TryParse(value, out b)) cfg.NarrateTutorialMessages = b; break;
                    case "narrateopeningnarration": if (bool.TryParse(value, out b)) cfg.NarrateOpeningNarration = b; break;
                    case "narrateplayerstatuspopups": if (bool.TryParse(value, out b)) cfg.NarratePlayerStatusPopups = b; break;
                    case "narratelevelinginspiration": if (bool.TryParse(value, out b)) cfg.NarrateLevelingInspiration = b; break;
                    case "narratehudstatusmessages": if (bool.TryParse(value, out b)) cfg.NarrateHudStatusMessages = b; break;
                    case "suppresshudstatusduringcombat": cfg.SuppressHudStatusDuringCombat = true; break;
                    case "narrateclimatescalories": if (bool.TryParse(value, out b)) cfg.NarrateClimatesCalories = b; break;
                    case "hudstatusminimumcharacters": if (int.TryParse(value, out i)) cfg.HudStatusMinimumCharacters = Math.Max(1, i); break;
                    case "hudstatusrepeatcooldownseconds": if (TryFloat(value, out f)) cfg.HudStatusRepeatCooldownSeconds = Mathf.Max(0f, f); break;
                    case "stopbooknarrationonclose": if (bool.TryParse(value, out b)) cfg.StopBookNarrationOnClose = b; break;
                    case "readablechunkcharacters": if (int.TryParse(value, out i)) cfg.ReadableChunkCharacters = Math.Max(200, i); break;
                    case "streamlongnarration": if (bool.TryParse(value, out b)) cfg.StreamLongNarration = b; break;
                    case "firstchunkcharacters": if (int.TryParse(value, out i)) cfg.FirstChunkCharacters = Math.Max(80, i); break;
                    case "prefetchnextchunk": if (bool.TryParse(value, out b)) cfg.PrefetchNextChunk = b; break;
                    case "narrateunclassifiedflavorpopups": if (bool.TryParse(value, out b)) cfg.NarrateUnclassifiedFlavorPopups = b; break;
                    case "fallbackrequiresnopreviouswindow": if (bool.TryParse(value, out b)) cfg.FallbackRequiresNoPreviousWindow = b; break;
                    case "requireclickanywheretoclose": if (bool.TryParse(value, out b)) cfg.RequireClickAnywhereToClose = b; break;
                    case "skipboxeswithbuttons": if (bool.TryParse(value, out b)) cfg.SkipBoxesWithButtons = b; break;
                    case "debugclassification": if (bool.TryParse(value, out b)) cfg.DebugClassification = b; break;
                    case "minimumcharacters": if (int.TryParse(value, out i)) cfg.MinimumCharacters = Math.Max(0, i); break;
                    case "chunkdedupseconds": if (TryFloat(value, out f)) cfg.ChunkDedupSeconds = Mathf.Max(0.1f, f); break;
                    case "maxqueueitems": if (int.TryParse(value, out i)) cfg.MaxQueueItems = Math.Max(1, i); break;

                    case "showsubtitles": if (bool.TryParse(value, out b)) cfg.ShowSubtitles = b; break;
                    case "shownarratorlabel": if (bool.TryParse(value, out b)) cfg.ShowNarratorLabel = b; break;
                    case "showsubtitlesforbooks": if (bool.TryParse(value, out b)) cfg.ShowSubtitlesForBooks = b; break;
                    case "showsubtitlesforquestnotes": if (bool.TryParse(value, out b)) cfg.ShowSubtitlesForQuestNotes = b; break;
                    case "showsubtitlesforcharacterquestions": if (bool.TryParse(value, out b)) cfg.ShowSubtitlesForCharacterQuestions = b; break;
                    case "showsubtitlesforcharacterdescriptions": if (bool.TryParse(value, out b)) cfg.ShowSubtitlesForCharacterDescriptions = b; break;
                    case "showsubtitlesforhudstatus": if (bool.TryParse(value, out b)) cfg.ShowSubtitlesForHudStatus = b; break;
                    case "showsubtitlesformanual": if (bool.TryParse(value, out b)) cfg.ShowSubtitlesForManual = b; break;
                    case "subtitleposition": if (Enum.TryParse<SubtitlePosition>(value, true, out position)) cfg.SubtitlePosition = position; break;
                    case "subtitlewidthpercent": if (TryFloat(value, out f)) cfg.SubtitleWidthPercent = Mathf.Clamp(f, 20f, 100f); break;
                    case "subtitlemarginpixels": if (int.TryParse(value, out i)) cfg.SubtitleMarginPixels = Math.Max(0, i); break;
                    case "subtitlefontsize": if (int.TryParse(value, out i)) cfg.SubtitleFontSize = Math.Max(10, i); break;
                    case "subtitlestyle": if (Enum.TryParse<SubtitleStyleMode>(value, true, out subtitleStyleMode)) cfg.SubtitleStyle = subtitleStyleMode; break;
                    case "subtitlebackgroundopacity": if (TryFloat(value, out f)) cfg.SubtitleBackgroundOpacity = Mathf.Clamp01(f); break;
                    case "subtitlefadeinseconds": if (TryFloat(value, out f)) cfg.SubtitleFadeInSeconds = Mathf.Max(0f, f); break;
                    case "subtitlefadeoutseconds": if (TryFloat(value, out f)) cfg.SubtitleFadeOutSeconds = Mathf.Max(0f, f); break;
                    case "subtitletailseconds": if (TryFloat(value, out f)) cfg.SubtitleTailSeconds = Mathf.Max(0f, f); break;
                    case "subtitlepagecharacters": if (int.TryParse(value, out i)) cfg.SubtitlePageCharacters = Math.Max(0, i); break;
                    case "ttsfailuresubtitleseconds": if (TryFloat(value, out f)) cfg.TtsFailureSubtitleSeconds = Mathf.Max(0f, f); break;

                    case "automanagecache": if (bool.TryParse(value, out b)) cfg.AutoManageCache = b; break;
                    case "maxcachesizemb": if (int.TryParse(value, out i)) cfg.MaxCacheSizeMB = Math.Max(0, i); break;
                    case "cachecleanuponstartup": if (bool.TryParse(value, out b)) cfg.CacheCleanupOnStartup = b; break;
                    case "cachecleanupintervalminutes": if (TryFloat(value, out f)) cfg.CacheCleanupIntervalMinutes = Mathf.Max(1f, f); break;
                    case "cachecleanuptargetpercent": if (TryFloat(value, out f)) cfg.CacheCleanupTargetPercent = Mathf.Clamp(f, 0.25f, 0.99f); break;
                    case "cacheminimumagehours": if (TryFloat(value, out f)) cfg.CacheMinimumAgeHours = Mathf.Max(0f, f); break;
                    case "cachehardlimitmultiplier": if (TryFloat(value, out f)) cfg.CacheHardLimitMultiplier = Mathf.Max(1f, f); break;

                    case "volume": if (TryFloat(value, out f)) cfg.Volume = Mathf.Clamp01(f); break;
                    case "usegamesoundvolume": if (bool.TryParse(value, out b)) cfg.UseGameSoundVolume = b; break;
                    case "skipkey": if (Enum.TryParse<KeyCode>(value, true, out keyCode)) cfg.SkipKey = keyCode; break;
                    case "togglekey": if (Enum.TryParse<KeyCode>(value, true, out keyCode)) cfg.ToggleKey = keyCode; break;

                    case "backend": if (Enum.TryParse<TtsBackend>(value, true, out backend)) cfg.Backend = backend; break;
                    case "autostartttsserver": if (bool.TryParse(value, out b)) cfg.AutoStartTtsServer = b; break;
                    case "stopautostartedttsonexit": if (bool.TryParse(value, out b)) cfg.StopAutoStartedTtsOnExit = b; break;
                    case "hideautostartedttsconsole": if (bool.TryParse(value, out b)) cfg.HideAutoStartedTtsConsole = b; break;
                    case "pythonexecutablepath": cfg.PythonExecutablePath = value; break;
                    case "ttshttpattempts": if (int.TryParse(value, out i)) cfg.TtsHttpAttempts = Math.Max(1, i); break;
                    case "ttsretrydelayseconds": if (TryFloat(value, out f)) cfg.TtsRetryDelaySeconds = Mathf.Max(0f, f); break;
                    case "ttshttptimeoutseconds": if (int.TryParse(value, out i)) cfg.TtsHttpTimeoutSeconds = Math.Max(1, i); break;

                    case "kokorovoice": cfg.KokoroVoice = value; break; // legacy resolved voice
                    case "kokorovoiceoverride": cfg.KokoroVoiceOverride = value; break;
                    case "voiceaccent": if (int.TryParse(value, out i)) cfg.VoiceAccent = Mathf.Clamp(i, 0, 1); break;
                    case "voicegender": if (int.TryParse(value, out i)) cfg.VoiceGender = Mathf.Clamp(i, 0, 1); break;
                    case "voicedelivery": if (int.TryParse(value, out i)) cfg.VoiceDelivery = Mathf.Clamp(i, 0, 4); break;
                    case "voicedepth": if (int.TryParse(value, out i)) cfg.VoiceDepth = Mathf.Clamp(i, 0, 100); break;
                    case "kokorolanguage": cfg.KokoroLanguage = value; break;
                    case "kokorodevice": if (Enum.TryParse<KokoroDevice>(value, true, out device)) cfg.KokoroDeviceMode = device; break;
                    case "kokoroport": if (int.TryParse(value, out i)) cfg.KokoroPort = Math.Max(1, i); break;
                    case "autostartvoiceengine": if (bool.TryParse(value, out b)) cfg.AutoStartVoiceEngine = b; break;
                    case "observationvoice": if (int.TryParse(value, out i)) cfg.ObservationVoice = Mathf.Clamp(i,0,2); break;
                    case "kokorospeed": if (TryFloat(value, out f)) cfg.KokoroSpeed = Mathf.Clamp(f, 0.5f, 2.0f); break;
                    case "kokoroaudiostyle": if (Enum.TryParse<KokoroAudioStyle>(value, true, out audioStyle)) cfg.KokoroAudioStylePreset = audioStyle; break;
                    case "kokoroserverscriptpath": cfg.KokoroServerScriptPath = value; break;

                    case "pipervoicename": cfg.PiperVoiceName = value; break;
                    case "piperdatadir": cfg.PiperDataDir = value; break;
                    case "piperport": if (int.TryParse(value, out i)) cfg.PiperPort = Math.Max(1, i); break;
                    case "piperlengthscale": if (TryFloat(value, out f)) cfg.PiperLengthScale = Mathf.Max(0.25f, f); break;

                    // v0.3 compatibility aliases.
                    case "autostartpiperserver": if (bool.TryParse(value, out b)) cfg.AutoStartTtsServer = b; break;
                    case "stopautostartedpiperonexit": if (bool.TryParse(value, out b)) cfg.StopAutoStartedTtsOnExit = b; break;
                    case "piperhttpattempts": if (int.TryParse(value, out i)) cfg.TtsHttpAttempts = Math.Max(1, i); break;
                    case "piperretrydelayseconds": if (TryFloat(value, out f)) cfg.TtsRetryDelaySeconds = Mathf.Max(0f, f); break;
                }
            }
            // Legacy booleans map to the new readable modes only when no explicit mode was changed.
            if (!cfg.NarrateBooks && cfg.BookReadingMode == ReadableMode.Manual) cfg.BookReadingMode = ReadableMode.Off;
            if (!cfg.NarrateQuestItemNotes && cfg.QuestNoteReadingMode == ReadableMode.Manual) cfg.QuestNoteReadingMode = ReadableMode.Off;
            cfg.NarrateBooks = cfg.BookReadingMode != ReadableMode.Off;
            cfg.NarrateQuestItemNotes = cfg.QuestNoteReadingMode != ReadableMode.Off;
            cfg.SuppressHudStatusDuringCombat = true;
            return cfg;
        }

        private static bool TryFloat(string value, out float result)
        {
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        }

        public void Save(string path)
        {
            string[] lines = new string[]
            {
                "# Daggerfall Narrator - Dungeon Master v1.1.4",
                "# Immersive replaces eligible flavor popups with voice + subtitle; Pause keeps an invisible modal until narration ends.",
                "Enabled=" + Enabled,
                "Mode=" + Mode,
                "ReplaceEligiblePopups=" + ReplaceEligiblePopups,
                "MessageBoxTiming=" + MessageTiming,
                "",
                "# Content and classification",
                "UseQuestSourceClassification=" + UseQuestSourceClassification,
                "NarrateGenericQuestMessages=" + NarrateGenericQuestMessages,
                "NarrateNotifyMessages=" + NarrateNotifyMessages,
                "NarrateAmbiguousSayMessages=" + NarrateAmbiguousSayMessages,
                "NarrateQuestItemNotes=" + NarrateQuestItemNotes,
                "KeepQuestItemNotesPopup=" + KeepQuestItemNotesPopup,
                "NarrateBooks=" + NarrateBooks,
                "BookReadingMode=" + BookReadingMode,
                "QuestNoteReadingMode=" + QuestNoteReadingMode,
                "QuestLogReadingMode=" + QuestLogReadingMode,
                "HistoryReadingMode=" + HistoryReadingMode,
                "NarrateCharacterCreationQuestions=" + NarrateCharacterCreationQuestions,
                "NarrateCharacterCreationDescriptions=" + NarrateCharacterCreationDescriptions,
                "NarrateTutorialMessages=" + NarrateTutorialMessages,
                "NarrateOpeningNarration=" + NarrateOpeningNarration,
                "NarratePlayerStatusPopups=" + NarratePlayerStatusPopups,
                "NarrateLevelingInspiration=" + NarrateLevelingInspiration,
                "NarrateHudStatusMessages=" + NarrateHudStatusMessages,
                "SuppressHudStatusDuringCombat=True",
                "NarrateClimatesCalories=" + NarrateClimatesCalories,
                "HudStatusMinimumCharacters=" + HudStatusMinimumCharacters,
                "HudStatusRepeatCooldownSeconds=" + HudStatusRepeatCooldownSeconds.ToString(CultureInfo.InvariantCulture),
                "StopBookNarrationOnClose=" + StopBookNarrationOnClose,
                "ReadableChunkCharacters=" + ReadableChunkCharacters,
                "StreamLongNarration=" + StreamLongNarration,
                "FirstChunkCharacters=" + FirstChunkCharacters,
                "PrefetchNextChunk=" + PrefetchNextChunk,
                "NarrateUnclassifiedFlavorPopups=" + NarrateUnclassifiedFlavorPopups,
                "FallbackRequiresNoPreviousWindow=" + FallbackRequiresNoPreviousWindow,
                "RequireClickAnywhereToClose=" + RequireClickAnywhereToClose,
                "SkipBoxesWithButtons=" + SkipBoxesWithButtons,
                "DebugClassification=" + DebugClassification,
                "MinimumCharacters=" + MinimumCharacters,
                "ChunkDedupSeconds=" + ChunkDedupSeconds.ToString(CultureInfo.InvariantCulture),
                "MaxQueueItems=" + MaxQueueItems,
                "",
                "# Subtitles. Books, quest notes, and character questions keep their original interfaces, so their duplicate subtitle is off by default.",
                "ShowSubtitles=" + ShowSubtitles,
                "ShowNarratorLabel=" + ShowNarratorLabel,
                "ShowSubtitlesForBooks=" + ShowSubtitlesForBooks,
                "ShowSubtitlesForQuestNotes=" + ShowSubtitlesForQuestNotes,
                "ShowSubtitlesForCharacterQuestions=" + ShowSubtitlesForCharacterQuestions,
                "ShowSubtitlesForCharacterDescriptions=" + ShowSubtitlesForCharacterDescriptions,
                "ShowSubtitlesForHudStatus=" + ShowSubtitlesForHudStatus,
                "ShowSubtitlesForManual=" + ShowSubtitlesForManual,
                "SubtitlePosition=" + SubtitlePosition,
                "SubtitleWidthPercent=" + SubtitleWidthPercent.ToString(CultureInfo.InvariantCulture),
                "SubtitleMarginPixels=" + SubtitleMarginPixels,
                "SubtitleFontSize=" + SubtitleFontSize,
                "SubtitleStyle=" + SubtitleStyle,
                "SubtitleBackgroundOpacity=" + SubtitleBackgroundOpacity.ToString(CultureInfo.InvariantCulture),
                "SubtitleFadeInSeconds=" + SubtitleFadeInSeconds.ToString(CultureInfo.InvariantCulture),
                "SubtitleFadeOutSeconds=" + SubtitleFadeOutSeconds.ToString(CultureInfo.InvariantCulture),
                "SubtitleTailSeconds=" + SubtitleTailSeconds.ToString(CultureInfo.InvariantCulture),
                "SubtitlePageCharacters=" + SubtitlePageCharacters,
                "TtsFailureSubtitleSeconds=" + TtsFailureSubtitleSeconds.ToString(CultureInfo.InvariantCulture),
                "",
                "# Runtime-generated TTS cache. VoicePack files are separate and are never auto-deleted.",
                "AutoManageCache=" + AutoManageCache,
                "MaxCacheSizeMB=" + MaxCacheSizeMB,
                "CacheCleanupOnStartup=" + CacheCleanupOnStartup,
                "CacheCleanupIntervalMinutes=" + CacheCleanupIntervalMinutes.ToString(CultureInfo.InvariantCulture),
                "CacheCleanupTargetPercent=" + CacheCleanupTargetPercent.ToString(CultureInfo.InvariantCulture),
                "CacheMinimumAgeHours=" + CacheMinimumAgeHours.ToString(CultureInfo.InvariantCulture),
                "CacheHardLimitMultiplier=" + CacheHardLimitMultiplier.ToString(CultureInfo.InvariantCulture),
                "",
                "# Audio and controls",
                "Volume=" + Volume.ToString(CultureInfo.InvariantCulture),
                "UseGameSoundVolume=" + UseGameSoundVolume,
                "SkipKey=" + SkipKey,
                "ToggleKey=" + ToggleKey,
                "",
                "# TTS backend: Kokoro (default) or Piper",
                "Backend=" + Backend,
                "AutoStartTtsServer=" + AutoStartTtsServer,
                "StopAutoStartedTtsOnExit=" + StopAutoStartedTtsOnExit,
                "HideAutoStartedTtsConsole=" + HideAutoStartedTtsConsole,
                "PythonExecutablePath=" + PythonExecutablePath,
                "TtsHttpAttempts=" + TtsHttpAttempts,
                "TtsRetryDelaySeconds=" + TtsRetryDelaySeconds.ToString(CultureInfo.InvariantCulture),
                "TtsHttpTimeoutSeconds=" + TtsHttpTimeoutSeconds,
                "",
                "# Kokoro. Voice prefix should normally match language: a=American English, b=British English.",
                "KokoroVoice=" + KokoroVoice,
                "KokoroVoiceOverride=" + (KokoroVoiceOverride ?? string.Empty),
                "VoiceAccent=" + VoiceAccent,
                "VoiceGender=" + VoiceGender,
                "VoiceDelivery=" + VoiceDelivery,
                "VoiceDepth=" + VoiceDepth,
                "KokoroLanguage=" + KokoroLanguage,
                "KokoroDevice=" + KokoroDeviceMode,
                "KokoroPort=" + KokoroPort,
                "AutoStartVoiceEngine=" + AutoStartVoiceEngine,
                "ObservationVoice=" + ObservationVoice,
                "KokoroSpeed=" + KokoroSpeed.ToString(CultureInfo.InvariantCulture),
                "# Clean, Cdrom (22.05 kHz/16-bit), Dos (11.025 kHz/8-bit)",
                "KokoroAudioStyle=" + KokoroAudioStylePreset,
                "KokoroServerScriptPath=" + KokoroServerScriptPath,
                "",
                "# Piper fallback",
                "PiperVoiceName=" + PiperVoiceName,
                "PiperDataDir=" + PiperDataDir,
                "PiperPort=" + PiperPort,
                "PiperLengthScale=" + PiperLengthScale.ToString(CultureInfo.InvariantCulture)
            };
            File.WriteAllLines(path, lines);
        }
    }

    internal class TextFilterRules
    {
        private readonly List<string> allowContains = new List<string>();
        private readonly List<string> blockContains = new List<string>();

        public static TextFilterRules LoadOrCreate(string path)
        {
            if (!File.Exists(path))
            {
                File.WriteAllLines(path, new string[]
                {
                    "# Optional user text rules. Built-in combat blocks always win.",
                    "# ALLOW:<text> lets a non-combat HUD notification be narrated.",
                    "# BLOCK:<text> prevents matching popup/HUD text from narration.",
                    "# Examples:",
                    "# ALLOW:You see a",
                    "# BLOCK:Some third-party mechanical notification"
                });
            }
            TextFilterRules rules = new TextFilterRules();
            try
            {
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";"))
                        continue;
                    if (line.StartsWith("ALLOW:", StringComparison.OrdinalIgnoreCase))
                        rules.allowContains.Add(NormalizeRule(line.Substring(6)));
                    else if (line.StartsWith("BLOCK:", StringComparison.OrdinalIgnoreCase))
                        rules.blockContains.Add(NormalizeRule(line.Substring(6)));
                }
            }
            catch { }
            return rules;
        }

        public bool IsAllowed(string text) { return Matches(allowContains, text); }
        public bool IsBlocked(string text) { return Matches(blockContains, text); }

        private static bool Matches(List<string> rules, string text)
        {
            string n = DaggerfallNarratorMod.NormalizeForMatch(text);
            for (int i = 0; i < rules.Count; i++)
                if (!string.IsNullOrEmpty(rules[i]) && n.Contains(rules[i]))
                    return true;
            return false;
        }

        private static string NormalizeRule(string value)
        {
            return DaggerfallNarratorMod.NormalizeForMatch(value ?? string.Empty);
        }
    }

    internal class PronunciationDictionary
    {
        private readonly List<PronunciationEntry> entries = new List<PronunciationEntry>();

        public static PronunciationDictionary LoadOrCreate(string path)
        {
            if (!File.Exists(path))
            {
                string[] defaults = new string[]
                {
                    "# Daggerfall Narrator pronunciation overrides",
                    "# Format: displayed spelling=spoken spelling",
                    "# These substitutions affect TTS only; subtitles and original UIs keep the original text.",
                    "Daggerfall=Dagger-fall",
                    "Wayrest=Way-rest",
                    "Nulfaga=Null-fah-gah",
                    "Direnni=Dee-ren-ee",
                    "Daedra=Day-druh"
                };
                File.WriteAllLines(path, defaults);
            }

            PronunciationDictionary dictionary = new PronunciationDictionary();
            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";"))
                    continue;
                int eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;
                string from = line.Substring(0, eq).Trim();
                string to = line.Substring(eq + 1).Trim();
                if (from.Length == 0 || to.Length == 0)
                    continue;
                dictionary.entries.Add(new PronunciationEntry(from, to));
            }
            return dictionary;
        }

        public string Apply(string text)
        {
            string result = text;
            int i;
            for (i = 0; i < entries.Count; i++)
            {
                PronunciationEntry entry = entries[i];
                string pattern = @"(?<![\p{L}\p{N}])" + Regex.Escape(entry.From) + @"(?![\p{L}\p{N}])";
                result = Regex.Replace(result, pattern, entry.To, RegexOptions.IgnoreCase);
            }
            return result;
        }

        private struct PronunciationEntry
        {
            public string From;
            public string To;
            public PronunciationEntry(string from, string to)
            {
                From = from;
                To = to;
            }
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
