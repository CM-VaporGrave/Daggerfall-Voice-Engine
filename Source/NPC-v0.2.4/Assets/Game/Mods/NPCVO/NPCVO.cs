// Daggerfall Narrator - NPC v0.2.4 stability / clean Q&A
// Runtime NPC voices for Daggerfall Unity 1.1.1.
// Runtime synthesis uses Kokoro. Exact pre-generated WAVs can optionally override Kokoro or run
// as the only speech source. Dynamic Portraits integration is reflection-only.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.UserInterface;
using DaggerfallWorkshop.Game.UserInterfaceWindows;
using DaggerfallWorkshop.Game.Utility.ModSupport;
using DaggerfallWorkshop.Game.Utility.ModSupport.ModSettings;
using Wenzil.Console;

namespace NPCVO
{
    public class NPCVOMod : MonoBehaviour
    {
        private static Mod mod;
        private static NPCVOMod instance;

        private AudioSource audioSource;
        private NPCVOConfig config;
        private VoiceProfileCollection voiceTypes;
        private VoiceProfileCollection uniqueVoices;
        private VoiceProfileCollection specialVoices;
        private VoiceProfileCollection portraitVoices;
        private VoiceAssignmentCollection voiceAssignments;
        private readonly Dictionary<string, VoiceAssignment> voiceAssignmentsByKey = new Dictionary<string, VoiceAssignment>(StringComparer.OrdinalIgnoreCase);

        private object currentConversationWindow;
        private ListBox currentConversationList;
        private readonly Dictionary<ListBox.ListItem, string> observedConversationItems = new Dictionary<ListBox.ListItem, string>();
        private readonly Dictionary<ListBox.ListItem, ProgressiveDialogueState> progressiveDialogue = new Dictionary<ListBox.ListItem, ProgressiveDialogueState>();
        private int lastConversationCount;
        private object currentQuestOfferWindow;
        private object currentQuestOfferDisplayWindow;
        private QuestOfferPresentationState questOfferPresentation;
        private string lastQuestOfferText = string.Empty;
        private string pendingQuestOfferText = string.Empty;
        private int pendingQuestOfferStablePolls;
        private float questDialogueOwnershipUntil;
        private object recentQuestOfferOwner;
        private bool questContinuationClaimAvailable;
        private object currentCourtDisplayWindow;
        private string lastCourtText = string.Empty;
        private string pendingCourtText = string.Empty;
        private int pendingCourtStablePolls;
        // Soft PlayerVO bridge. PlayerVO can register a persistent player identity and reserve a short
        // player turn after a native quest choice. NPCVO keeps DFU quest callbacks untouched and only
        // sequences the presentation layer. The portrait and last spoken player line remain visible for
        // conversational continuity until the quest UI itself closes.
        private bool externalPlayerIdentityRegistered;
        private bool externalPlayerTurnActive;
        private string externalPlayerTurnName = string.Empty;
        private string externalPlayerTurnText = string.Empty;
        private string externalPlayerDisplayedText = string.Empty;
        private Texture2D externalPlayerTurnPortrait;
        private string externalPlayerRace = string.Empty;
        private string externalPlayerPersonality = string.Empty;
        private string externalPlayerLocation = string.Empty;
        private TalkConversationPresentationState talkConversationPresentation;

        // Optional Daggerfall Narrator - Player bridge for vanilla TalkWindow questions. NPCVO sees the
        // question/answer pair first, asks PlayerVO to speak the exact question, then holds the NPC
        // answer until the player's dialogue speech finishes. This keeps both modules off Kokoro at
        // the same time and makes the normal Ask/Where Is/Tell Me About UI conversational.
        private bool playerVoReflectionChecked;
        private Type playerVoType;
        private MethodInfo playerVoSpeakTalkQuestion;
        private PropertyInfo playerVoDialogueSpeechActive;
        private bool waitingForPlayerTalkQuestion;
        private DeferredTalkAnswer deferredTalkAnswer;
        private float deferredTalkDeadline;

        private int speechGeneration;
        private int portraitSkipGeneration = -1;
        private Coroutine activeSpeechRoutine;
        private bool speechBusy;
        private readonly Queue<SpeechRequest> speechQueue = new Queue<SpeechRequest>();
        private DaggerfallVoiceEngineClient voiceEngine;
        private string activeVoiceEngineTurnId = string.Empty;
        private float nextVoiceEnginePresence;
        private string lastSpokenLine = string.Empty;
        private string lastQuestDebugSnapshot = "No quest/message-box diagnostic has been captured yet.";
        private string lastLoggedQuestDebugSnapshot = string.Empty;
        private string lastVoicePackScript = string.Empty;
        private string lastVoicePackHash = string.Empty;
        private NPCIdentity lastIdentity;
        private ResolvedVoice lastResolvedVoice;
        private bool voiceLibraryEnsureStarted;
        private string lastVoiceLibraryStatus = "not checked";
        private string lastAssignmentStatus = "not loaded";

        private string rootDir;
        private string cacheDir;
        private string voicePackDir;
        private string packagedVoicePackDir;
        private string iniPath;
        private string voiceTypesPath;
        private string uniqueVoicesPath;
        private string specialVoicesPath;
        private string portraitVoicesPath;
        private string voiceAssignmentsPath;
        private float nextPollTime;
        private float nextCacheCleanupTime;
        private object observedPlayerEntityToken;

        private const float PollInterval = 0.05f;
        private const string CacheSchema = "npcvo-cache-v2";
        private const string DynamicPortraitsTypeName = "DynamicPortraits.DynamicPortraitsMain";

        private static readonly FieldInfo TalkListField = typeof(DaggerfallTalkWindow)
            .GetField("listboxConversation", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo UiWindowStackField = typeof(UserInterfaceManager)
            .GetField("windows", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo TalkNpcDataField = typeof(TalkManager)
            .GetField("npcData", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo MessageBoxButtonsListField = typeof(DaggerfallMessageBox)
            .GetField("buttons", BindingFlags.Instance | BindingFlags.NonPublic);

        private Type dynamicPortraitsType;
        private PropertyInfo dynamicPortraitsInstanceProperty;
        private FieldInfo dynamicPortraitsArchiveField;
        private FieldInfo dynamicPortraitsRecordField;
        private FieldInfo dynamicPortraitsAudioSourceField;
        private FieldInfo dynamicPortraitsReactionStateField;
        private MethodInfo dynamicPortraitsDetermineCifMethod;
        private MethodInfo dynamicPortraitsMoodMethod;
        private bool dynamicPortraitsReflectionChecked;

        [Invoke(StateManager.StateTypes.Start, 0)]
        public static void Init(InitParams initParams)
        {
            mod = initParams.Mod;
            GameObject go = new GameObject(mod.Title);
            DontDestroyOnLoad(go);
            go.AddComponent<NPCVOMod>();
            mod.IsReady = true;
            Debug.Log("[NPCVO] Initialized v0.2.4 stability / clean Q&A.");
        }

        // Reflection-friendly compatibility seam for Dynamic Portraits or another portrait mod.
        // NPCVO does not require that mod, but exposes the active quest portrait target so a future
        // integration can animate the same panel without replacing NPCVO's quest UI.
        public static Panel ActiveQuestPortraitPanel
        {
            get { return instance == null || instance.questOfferPresentation == null ? null : instance.questOfferPresentation.PortraitPanel; }
        }

        public static Texture2D ActiveQuestPortraitTexture
        {
            get { return instance == null || instance.questOfferPresentation == null ? null : instance.questOfferPresentation.PortraitTexture; }
        }

        // Reflection-friendly PlayerVO seam. No compile-time dependency in either direction.
        public static bool EnhancedQuestConversationActive
        {
            get { return instance != null && instance.questOfferPresentation != null; }
        }

        // Reflection-friendly ownership signal for Dungeon Master. It remains true briefly after a
        // quest-giver window closes so immediate quest-script follow-up speech stays with NPCVO.
        public static bool QuestDialogueOwnershipActive
        {
            get { return instance != null && Time.realtimeSinceStartup <= instance.questDialogueOwnershipUntil; }
        }

        public static bool RegisterExternalPlayerIdentity(Texture2D playerPortrait, string playerName)
        {
            if (instance == null || instance.config == null ||
                (!instance.config.QuestGiverPortraitUI && !instance.config.PlayerAskConversationUI))
                return false;

            instance.externalPlayerIdentityRegistered = true;
            if (playerPortrait != null)
                instance.externalPlayerTurnPortrait = playerPortrait;
            instance.externalPlayerTurnName = string.IsNullOrWhiteSpace(playerName) ? "You" : playerName;

            if (instance.questOfferPresentation != null)
                instance.ApplyExternalPlayerTurn(instance.questOfferPresentation, instance.externalPlayerDisplayedText);
            if (instance.talkConversationPresentation != null)
                instance.RefreshTalkConversationPresentation(instance.talkConversationPresentation);
            return true;
        }

        // Extended identity registration used by Daggerfall Narrator - Player v0.1.4+. The original
        // two-argument method remains intact for compatibility with older Player builds.
        public static bool RegisterExternalPlayerIdentityDetailed(Texture2D playerPortrait, string playerName,
            string playerRace, string playerPersonality, string playerLocation)
        {
            if (instance == null || instance.config == null ||
                (!instance.config.QuestGiverPortraitUI && !instance.config.PlayerAskConversationUI))
                return false;

            // Do not route through an overload so older reflection clients never see an ambiguous method name.
            instance.externalPlayerIdentityRegistered = true;
            if (playerPortrait != null)
                instance.externalPlayerTurnPortrait = playerPortrait;
            instance.externalPlayerTurnName = string.IsNullOrWhiteSpace(playerName) ? "You" : playerName;
            instance.externalPlayerRace = playerRace ?? string.Empty;
            instance.externalPlayerPersonality = playerPersonality ?? string.Empty;
            instance.externalPlayerLocation = playerLocation ?? string.Empty;

            if (instance.questOfferPresentation != null)
                instance.ApplyExternalPlayerTurn(instance.questOfferPresentation, instance.externalPlayerDisplayedText);
            if (instance.talkConversationPresentation != null)
                instance.RefreshTalkConversationPresentation(instance.talkConversationPresentation);
            return true;
        }

        public static bool BeginExternalPlayerTurn(Texture2D playerPortrait, string playerName, string fullText)
        {
            if (instance == null || instance.config == null || !instance.config.QuestGiverPortraitUI)
                return false;

            RegisterExternalPlayerIdentity(playerPortrait, playerName);
            instance.externalPlayerTurnActive = true;
            instance.externalPlayerTurnText = fullText ?? string.Empty;
            instance.externalPlayerDisplayedText = string.Empty;
            if (instance.questOfferPresentation != null)
                instance.ApplyExternalPlayerTurn(instance.questOfferPresentation, string.Empty);
            return true;
        }

        public static void UpdateExternalPlayerTurnText(string partialText)
        {
            if (instance == null || !instance.externalPlayerIdentityRegistered)
                return;
            instance.externalPlayerDisplayedText = partialText ?? string.Empty;
            if (instance.questOfferPresentation != null)
                instance.ApplyExternalPlayerTurn(instance.questOfferPresentation, instance.externalPlayerDisplayedText);
        }

        public static void EndExternalPlayerTurn()
        {
            if (instance == null)
                return;
            instance.externalPlayerTurnActive = false;

            // Do not tear down the player half of the conversation when speech ends. Keep the player
            // portrait and final typewritten response visible while releasing NPCVO to present the next
            // NPC line. This prevents the UI from snapping back to one-sided NPC-only mode.
            if (instance.questOfferPresentation != null && instance.externalPlayerIdentityRegistered)
                instance.ApplyExternalPlayerTurn(instance.questOfferPresentation, instance.externalPlayerDisplayedText);
        }

        private void Awake()
        {
            instance = this;
            audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
            audioSource.loop = false;
            audioSource.spatialBlend = 0f;

            rootDir = Path.Combine(Application.persistentDataPath, "NPCVO");
            cacheDir = Path.Combine(rootDir, "Cache");
            voicePackDir = Path.Combine(rootDir, "VoicePack");
            packagedVoicePackDir = Path.Combine(Application.streamingAssetsPath, "Sound", "NPCVO", "VoicePack");
            iniPath = Path.Combine(rootDir, "NPCVO.ini");
            voiceTypesPath = Path.Combine(rootDir, "VoiceTypes.json");
            uniqueVoicesPath = Path.Combine(rootDir, "UniqueVoices.json");
            specialVoicesPath = Path.Combine(rootDir, "SpecialVoices.json");
            portraitVoicesPath = Path.Combine(rootDir, "PortraitVoices.json");
            voiceAssignmentsPath = Path.Combine(rootDir, "VoiceAssignments.json");

            Directory.CreateDirectory(rootDir);
            Directory.CreateDirectory(cacheDir);
            Directory.CreateDirectory(voicePackDir);

            LoadAllConfiguration();
            voiceEngine = new DaggerfallVoiceEngineClient(this, "NPC", "0.2.4",
                delegate { return config == null ? 5000 : config.KokoroPort; }, delegate { return config == null || config.AutoStartVoiceEngine; }, 0.35f);
            StartCoroutine(voiceEngine.EnsureRunning());
            if (mod != null && mod.HasSettings)
            {
                // Follow DFU's documented live-settings pattern: assign the callback, then explicitly
                // invoke LoadSettings() once so in-game settings changes reliably reach NPCVO.
                mod.LoadSettingsCallback = NativeSettingsChanged;
                try
                {
                    mod.LoadSettings();
                }
                catch (Exception ex) { Debug.LogWarning("[NPCVO] Live settings registration failed: " + ex.Message); }
            }

            RegisterConsoleCommands();
            nextCacheCleanupTime = Time.realtimeSinceStartup + 3f;
            TryEnsureEnglishVoiceLibrary();

            // React immediately when DFU pushes a new modal/window. Polling alone can leave the
            // native quest parchment visible for a frame before NPCVO decorates it.
            try
            {
                if (DaggerfallUI.UIManager != null)
                    DaggerfallUI.UIManager.OnWindowChange += OnUiWindowChange;
            }
            catch { }
        }

        private void OnDestroy()
        {
            if (voiceEngine != null)
            {
                if (!string.IsNullOrEmpty(activeVoiceEngineTurnId))
                    StartCoroutine(voiceEngine.CancelTurn(activeVoiceEngineTurnId));
                StartCoroutine(voiceEngine.CancelModule());
            }
            try
            {
                if (DaggerfallUI.UIManager != null)
                    DaggerfallUI.UIManager.OnWindowChange -= OnUiWindowChange;
            }
            catch { }
        }

        private void OnUiWindowChange(object sender, EventArgs e)
        {
            if (config == null || !config.Enabled)
                return;

            // OnWindowChange fires as the modal is pushed, before the next normal NPCVO poll.
            // Attach/suppress quest UI immediately so the vanilla text does not flash first, and
            // inspect legal-court message boxes without waiting for the normal polling interval.
            PollQuestOfferWindow();
            PollCourtWindow();
        }

        private void LateUpdate()
        {
            if (config == null || !config.Enabled || DaggerfallUI.UIManager == null)
                return;

            // Safety net for windows constructed after NPCVO's Update() in the same frame.
            // LateUpdate still runs before GUI rendering and closes the one-frame flash gap.
            object top = DaggerfallUI.UIManager.TopWindow;
            if (top is DaggerfallMessageBox || LooksLikeQuestOfferWindow(top))
            {
                PollQuestOfferWindow();
                PollCourtWindow();
            }
        }


        private void HandlePlayerEntityTransition(object newPlayerEntity)
        {
            // NPCVO persists across save/character changes. A dialogue coroutine or engine turn from
            // the old character must never survive into the new one.
            if (voiceEngine != null)
                StartCoroutine(voiceEngine.CancelModule());
            activeVoiceEngineTurnId = string.Empty;

            StopSpeech(true);
            DestroyTalkConversationPresentation();
            DestroyQuestOfferPresentation();
            currentConversationWindow = null;
            currentConversationList = null;
            observedConversationItems.Clear();
            progressiveDialogue.Clear();
            lastConversationCount = 0;

            currentQuestOfferWindow = null;
            currentQuestOfferDisplayWindow = null;
            lastQuestOfferText = string.Empty;
            pendingQuestOfferText = string.Empty;
            pendingQuestOfferStablePolls = 0;
            questDialogueOwnershipUntil = 0f;
            recentQuestOfferOwner = null;
            questContinuationClaimAvailable = false;

            currentCourtDisplayWindow = null;
            lastCourtText = string.Empty;
            pendingCourtText = string.Empty;
            pendingCourtStablePolls = 0;

            waitingForPlayerTalkQuestion = false;
            deferredTalkAnswer = null;
            deferredTalkDeadline = 0f;

            externalPlayerIdentityRegistered = false;
            externalPlayerTurnActive = false;
            externalPlayerTurnName = string.Empty;
            externalPlayerTurnText = string.Empty;
            externalPlayerDisplayedText = string.Empty;
            externalPlayerTurnPortrait = null;
            externalPlayerRace = string.Empty;
            externalPlayerPersonality = string.Empty;
            externalPlayerLocation = string.Empty;

            lastSpokenLine = string.Empty;
            lastIdentity = null;
            lastResolvedVoice = null;
            lastQuestDebugSnapshot = "No quest/message-box diagnostic has been captured yet.";
            lastLoggedQuestDebugSnapshot = string.Empty;

            Debug.Log("[NPCVO] Player entity changed; conversation state and Voice Engine ownership reset.");
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

            if (voiceEngine != null && Time.realtimeSinceStartup >= nextVoiceEnginePresence)
            {
                nextVoiceEnginePresence = Time.realtimeSinceStartup + 20f;
                StartCoroutine(voiceEngine.PulsePresence());
            }

            if (Time.realtimeSinceStartup >= nextPollTime)
            {
                nextPollTime = Time.realtimeSinceStartup + PollInterval;
                PollConversation();
                PollQuestOfferWindow();
                PollCourtWindow();
            }

            if (Time.realtimeSinceStartup >= nextCacheCleanupTime)
            {
                nextCacheCleanupTime = Time.realtimeSinceStartup + 120f;
                if (audioSource == null || !audioSource.isPlaying)
                    CleanupCacheIfNeeded();
            }
        }

        private void PollConversation()
        {
            object window = FindConversationWindow();
            if (window == null)
            {
                if (currentConversationWindow != null)
                    HandleConversationClosed();
                return;
            }

            ListBox list = FindConversationList(window);
            if (list == null)
                return;

            if (config.PlayerAskConversationUI && externalPlayerIdentityRegistered)
                EnsureTalkConversationPresentation(window);
            else if (talkConversationPresentation != null)
                DestroyTalkConversationPresentation();

            TryFlushDeferredTalkAnswer();

            if (!ReferenceEquals(window, currentConversationWindow) || !ReferenceEquals(list, currentConversationList))
            {
                RestoreAllProgressiveDialogue();
                currentConversationWindow = window;
                currentConversationList = list;
                observedConversationItems.Clear();
                lastConversationCount = 0;
            }

            // Some alternate TalkWindow implementations rebuild or recycle their rows instead of
            // only appending. If the list shrinks, forget the old row identities and rescan what is
            // currently visible. This also makes the detector recover cleanly after a UI refresh.
            if (list.Count < lastConversationCount)
            {
                RestoreAllProgressiveDialogue();
                observedConversationItems.Clear();
            }
            lastConversationCount = list.Count;

            for (int i = 0; i < list.Count; i++)
            {
                ListBox.ListItem item = list.GetItem(i);
                if (item == null || item.textLabel == null)
                    continue;

                string rawText = item.textLabel.Text ?? string.Empty;
                string previousText;
                bool alreadyObserved = observedConversationItems.TryGetValue(item, out previousText);

                // Scan by row identity + content rather than by Count alone. Vanilla DFU appends
                // question/answer pairs, but derived dialogue UIs can replace an existing row.
                // Tracking both catches either behavior and avoids the post-question silence bug.
                if (alreadyObserved && string.Equals(previousText, rawText, StringComparison.Ordinal))
                    continue;

                observedConversationItems[item] = rawText;

                if (IsPlayerQuestion(item))
                {
                    UpdateTalkConversationPlayerQuestion(rawText);
                    if (TrySpeakExternalPlayerTalkQuestion(rawText))
                    {
                        waitingForPlayerTalkQuestion = true;
                        deferredTalkDeadline = Time.realtimeSinceStartup + 30f;
                    }
                    continue;
                }

                string line = NormalizeText(rawText);
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (waitingForPlayerTalkQuestion && IsExternalPlayerDialogueSpeechActive())
                {
                    // Hide the incoming NPC answer immediately while PlayerVO speaks the selected
                    // question. Without this pre-suppression, the full answer flashes into view, then
                    // disappears again only when NPC audio begins.
                    SpeechRequest visualRequest = new SpeechRequest();
                    visualRequest.Line = line;
                    visualRequest.DisplayText = rawText;
                    visualRequest.Item = item;
                    PrepareProgressiveDialogue(visualRequest);
                    deferredTalkAnswer = new DeferredTalkAnswer(line, item, rawText);
                    continue;
                }

                waitingForPlayerTalkQuestion = false;
                SpeakNpcLine(line, item, rawText);
            }
        }

        private void PollQuestOfferWindow()
        {
            if (config == null || DaggerfallUI.UIManager == null)
                return;

            object displayWindow;
            object questOfferWindow = FindQuestOfferWindow(out displayWindow);
            // Keep a short ownership tail after a quest window disappears. This lets Dungeon Master
            // distinguish an immediate quest-script NPC follow-up from free-standing flavor text.
            if (questOfferWindow != null)
            {
                questDialogueOwnershipUntil = Time.realtimeSinceStartup + 4f;
                recentQuestOfferOwner = questOfferWindow;
                questContinuationClaimAvailable = true;
            }
            else if (questContinuationClaimAvailable && recentQuestOfferOwner != null &&
                Time.realtimeSinceStartup <= questDialogueOwnershipUntil)
            {
                // Some quest scripts close the offer parchment and immediately push one plain Message:
                // record containing the quest giver's next sentence. It has no quest-offer callback for
                // us to discover, so give NPCVO one conservative first-refusal continuation slot.
                DaggerfallMessageBox continuation = DaggerfallUI.UIManager.TopWindow as DaggerfallMessageBox;
                if (continuation != null && !ReferenceEquals(continuation, currentQuestOfferDisplayWindow) &&
                    !IsCourtMessageBox(continuation) && !MessageBoxHasChoiceButtons(continuation))
                {
                    string continuationText = ExtractQuestOfferText(continuation);
                    if (!string.IsNullOrWhiteSpace(continuationText))
                    {
                        questOfferWindow = recentQuestOfferOwner;
                        displayWindow = continuation;
                        questContinuationClaimAvailable = false;
                        questDialogueOwnershipUntil = Time.realtimeSinceStartup + 4f;
                        Debug.Log("[NPCVO] Claimed immediate post-quest dialogue continuation: " + continuationText);
                    }
                }
            }
            // Diagnostics run even if quest narration is disabled, so a settings issue is visible.
            RecordQuestDebugSnapshot(questOfferWindow, displayWindow);
            if (!config.NarrateQuestGiverWindows)
                return;

            if (questOfferWindow == null)
            {
                if (currentQuestOfferWindow != null)
                {
                    DestroyQuestOfferPresentation();
                    currentQuestOfferWindow = null;
                    currentQuestOfferDisplayWindow = null;
                    lastQuestOfferText = string.Empty;
                    pendingQuestOfferText = string.Empty;
                    pendingQuestOfferStablePolls = 0;

                    // If a normal TalkWindow has already replaced the quest-offer UI this frame,
                    // let that conversation own playback. Otherwise treat closing the quest UI
                    // the same way as closing a TalkWindow.
                    if (config.StopWhenTalkWindowCloses && currentConversationWindow == null)
                        StopSpeech(false);
                }
                return;
            }

            object effectiveDisplay = displayWindow ?? questOfferWindow;
            if (!ReferenceEquals(questOfferWindow, currentQuestOfferWindow) ||
                !ReferenceEquals(effectiveDisplay, currentQuestOfferDisplayWindow))
            {
                DestroyQuestOfferPresentation();
                currentQuestOfferWindow = questOfferWindow;
                currentQuestOfferDisplayWindow = effectiveDisplay;
                lastQuestOfferText = string.Empty;
                pendingQuestOfferText = string.Empty;
                pendingQuestOfferStablePolls = 0;
            }

            string text = ExtractQuestOfferText(effectiveDisplay);
            if (string.IsNullOrWhiteSpace(text))
                return;

            // Attach the portrait/dialogue presentation immediately on the first valid observation.
            // Speech still waits for a second identical observation below, but the original DFU text
            // is hidden now rather than remaining visible during that stabilization delay.
            NPCIdentity identity = ResolveQuestOfferIdentity(questOfferWindow);
            QuestOfferPresentationState presentation = null;
            if (config.QuestGiverPortraitUI)
                presentation = EnsureQuestOfferPresentation(questOfferWindow, effectiveDisplay, identity, text);

            // A PlayerVO semantic response can occupy the enhanced quest panel between the native
            // choice click and the NPC follow-up. The underlying quest has already advanced normally;
            // we simply defer capturing/voicing the new NPC paragraph until the player line ends.
            if (externalPlayerTurnActive)
            {
                if (presentation != null)
                    ApplyExternalPlayerTurn(presentation, externalPlayerDisplayedText);
                return;
            }

            if (string.Equals(text, lastQuestOfferText, StringComparison.Ordinal))
                return;

            // Quest-offer controls are assembled over more than one UI update on some paths.
            // Require the same extracted text twice before synthesis so we don't voice a half-built sentence.
            if (!string.Equals(text, pendingQuestOfferText, StringComparison.Ordinal))
            {
                pendingQuestOfferText = text;
                pendingQuestOfferStablePolls = 1;
                return;
            }

            if (++pendingQuestOfferStablePolls < 2)
                return;

            lastQuestOfferText = text;
            pendingQuestOfferText = string.Empty;
            pendingQuestOfferStablePolls = 0;

            Debug.Log("[NPCVO] Captured quest-giver line | Window=" + effectiveDisplay.GetType().Name +
                " | NPC=" + identity.Name + " | Portrait=" + identity.PortraitKey + " | Text=\"" + text + "\"");
            SpeakNpcLine(text, null, text, identity, "QuestOffer", presentation);
        }

        private void PollCourtWindow()
        {
            if (config == null || DaggerfallUI.UIManager == null)
                return;

            object top = DaggerfallUI.UIManager.TopWindow;
            DaggerfallMessageBox courtBox = top as DaggerfallMessageBox;
            bool isCourt = courtBox != null && IsCourtMessageBox(courtBox);

            if (!config.NarrateCourtJudge || !isCourt)
            {
                if (currentCourtDisplayWindow != null)
                {
                    currentCourtDisplayWindow = null;
                    lastCourtText = string.Empty;
                    pendingCourtText = string.Empty;
                    pendingCourtStablePolls = 0;

                    // A court modal can replace another UI in the same frame. Only stop a trailing
                    // line when no normal/quest conversation currently owns speech.
                    if (config.StopWhenTalkWindowCloses && currentConversationWindow == null && currentQuestOfferWindow == null)
                        StopSpeech(false);
                }
                return;
            }

            if (!ReferenceEquals(courtBox, currentCourtDisplayWindow))
            {
                currentCourtDisplayWindow = courtBox;
                lastCourtText = string.Empty;
                pendingCourtText = string.Empty;
                pendingCourtStablePolls = 0;
            }

            // Reuse the generic visible-text collector used by quest message boxes. It deliberately
            // ignores Button components, so Guilty/Not Guilty/Debate/Lie remain player choices.
            string text = ExtractQuestOfferText(courtBox);
            if (string.IsNullOrWhiteSpace(text) || string.Equals(text, lastCourtText, StringComparison.Ordinal))
                return;

            // Court windows can rebuild their formatted text over consecutive UI updates. Wait for
            // the same extracted string twice so NPCVO never speaks a partially assembled sentence.
            if (!string.Equals(text, pendingCourtText, StringComparison.Ordinal))
            {
                pendingCourtText = text;
                pendingCourtStablePolls = 1;
                return;
            }

            if (++pendingCourtStablePolls < 2)
                return;

            lastCourtText = text;
            pendingCourtText = string.Empty;
            pendingCourtStablePolls = 0;

            NPCIdentity identity = CreateCourtJudgeIdentity();
            Debug.Log("[NPCVO] Captured legal-court line | Window=" + courtBox.GetType().Name +
                " | VoiceRole=Judge | Text=\"" + text + "\"");
            SpeakNpcLine(text, null, text, identity, "Court");
        }

        private static bool IsCourtMessageBox(DaggerfallMessageBox box)
        {
            if (box == null)
                return false;

            // DaggerfallCourtWindow constructs its narrative/prompt message boxes with itself (or
            // another court-owned box) as PreviousWindow. Following that chain identifies the real
            // legal UI without treating unrelated Yes/No message boxes as a judge.
            object current = box;
            HashSet<object> seen = new HashSet<object>();
            for (int depth = 0; depth < 12 && current != null; depth++)
            {
                if (!seen.Add(current))
                    break;

                string typeName = current.GetType().Name ?? string.Empty;
                if (typeName.IndexOf("CourtWindow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    string.Equals(typeName, "DaggerfallCourtWindow", StringComparison.OrdinalIgnoreCase))
                    return true;

                current = ReadMember(current, "PreviousWindow") ?? ReadMember(current, "previousWindow");
            }
            return false;
        }

        private static NPCIdentity CreateCourtJudgeIdentity()
        {
            NPCIdentity id = new NPCIdentity();
            id.Name = "Court Judge";
            id.NpcType = "CourtJudge";
            id.Race = "Breton";
            id.Gender = "Male";
            id.Role = "Judge";
            id.AssignmentKey = "court-judge";
            id.StableKey = "court-judge";
            return id;
        }

        private static bool MessageBoxHasChoiceButtons(DaggerfallMessageBox box)
        {
            if (box == null || MessageBoxButtonsListField == null) return false;
            try
            {
                IList buttons = MessageBoxButtonsListField.GetValue(box) as IList;
                return buttons != null && buttons.Count > 0;
            }
            catch { return false; }
        }

        private object FindQuestOfferWindow(out object displayWindow)
        {
            displayWindow = null;
            UserInterfaceManager manager = DaggerfallUI.UIManager as UserInterfaceManager;
            if (manager == null)
                return null;

            object top = manager.TopWindow;

            DaggerfallMessageBox questPromptBox = top as DaggerfallMessageBox;

            // Vanilla procedural quest offers can be a plain DaggerfallMessageBox whose only direct
            // link back to DaggerfallQuestOfferWindow is an instance event handler such as
            // QuestPopupMessage_OnClose. Recover the owning quest window from that delegate target.
            // This is the path used by the procedural parchment offers seen in DFU 1.1.1.
            object handlerOwner = FindQuestOfferOwnerFromMessageBoxHandlers(questPromptBox);
            if (handlerOwner != null)
            {
                displayWindow = questPromptBox;
                return handlerOwner;
            }

            // Quest-script `prompt` actions are another plain-message-box path. These have Prompt or
            // PromptMulti callbacks rather than a DaggerfallQuestOfferWindow callback.
            if (IsQuestScriptPromptMessageBox(questPromptBox) && HasCredibleQuestPromptSpeaker())
            {
                displayWindow = questPromptBox;
                return questPromptBox;
            }

            if (LooksLikeQuestOfferWindow(top))
            {
                displayWindow = top;
                return top;
            }

            // Vanilla DFU quest offers commonly present their text in a DaggerfallMessageBox whose
            // PreviousWindow points back to the owning QuestOffer window. That owner is not guaranteed
            // to remain discoverable through UserInterfaceManager's private stack on every presentation
            // path, so follow the public/inherited PreviousWindow chain first.
            object previousOwner = FindQuestOfferInPreviousChain(top);
            if (previousOwner != null)
            {
                displayWindow = top;
                return previousOwner;
            }

            // Do not scan unrelated windows underneath the current TopWindow for a QuestOffer.
            // A valid quest modal must be linked by its delegate target, PreviousWindow chain, or be
            // the QuestOffer window itself. This prevents stale quest state from hijacking other modals.
            return null;
        }

        private static object FindQuestOfferOwnerFromMessageBoxHandlers(DaggerfallMessageBox box)
        {
            if (box == null)
                return null;

            try
            {
                Type type = box.GetType();
                while (type != null)
                {
                    FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    for (int i = 0; i < fields.Length; i++)
                    {
                        FieldInfo field = fields[i];
                        if (!typeof(Delegate).IsAssignableFrom(field.FieldType))
                            continue;

                        Delegate handlers = null;
                        try { handlers = field.GetValue(box) as Delegate; } catch { }
                        if (handlers == null)
                            continue;

                        Delegate[] invocationList = handlers.GetInvocationList();
                        for (int j = 0; j < invocationList.Length; j++)
                        {
                            Delegate handler = invocationList[j];
                            object target = handler.Target;
                            if (target != null && LooksLikeQuestOfferWindow(target))
                                return target;
                        }
                    }
                    type = type.BaseType;
                }
            }
            catch { }

            return null;
        }

        private void RecordQuestDebugSnapshot(object owner, object display)
        {
            object top = DaggerfallUI.UIManager == null ? null : DaggerfallUI.UIManager.TopWindow;
            DaggerfallMessageBox topBox = top as DaggerfallMessageBox;

            // Preserve the last useful modal snapshot after the player closes the box. This makes
            // npcvo_quest_debug usable even though DFU's message box prevents opening the console.
            if (topBox == null && owner == null && display == null)
                return;

            string snapshot = BuildQuestDebugSnapshot(top, owner, display);
            lastQuestDebugSnapshot = snapshot;
            if (!string.Equals(snapshot, lastLoggedQuestDebugSnapshot, StringComparison.Ordinal))
            {
                lastLoggedQuestDebugSnapshot = snapshot;
                Debug.Log("[NPCVO][QuestDebug] " + snapshot);
            }
        }

        private string BuildQuestDebugSnapshot(object top, object owner, object display)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("Top=").Append(top == null ? "<null>" : top.GetType().Name);
            sb.Append(" | NarrateQuest=").Append(config != null && config.NarrateQuestGiverWindows ? "on" : "off");
            sb.Append(" | PortraitUI=").Append(config != null && config.QuestGiverPortraitUI ? "on" : "off");

            DaggerfallMessageBox topBox = top as DaggerfallMessageBox;
            if (topBox != null)
            {
                sb.Append(" | QuestPrompt=").Append(IsQuestScriptPromptMessageBox(topBox) ? "YES" : "no");
                sb.Append(" | YesNo=").Append(HasYesNoButtons(topBox) ? "YES" : "no");
                sb.Append(" | Handlers=").Append(GetMessageBoxHandlerSummary(topBox));
            }
            else
            {
                sb.Append(" | QuestPrompt=n/a | YesNo=n/a | Handlers=n/a");
            }

            try
            {
                sb.Append(" | ActiveQuestor=").Append(
                    GameManager.Instance != null && GameManager.Instance.QuestMachine != null &&
                    GameManager.Instance.QuestMachine.LastNPCClicked != null &&
                    GameManager.Instance.QuestMachine.IsLastNPCClickedAnActiveQuestor() ? "YES" : "no");
            }
            catch { sb.Append(" | ActiveQuestor=?"); }

            sb.Append(" | QuestOwner=").Append(owner == null ? "NOT FOUND" : owner.GetType().Name);
            sb.Append(" | Display=").Append(display == null ? "<null>" : display.GetType().Name);
            object textSource = display ?? top;
            if (textSource != null)
            {
                string offerText = ExtractQuestOfferText(textSource);
                sb.Append(" | TextChars=").Append(offerText == null ? 0 : offerText.Length);
            }

            StaticNPC clicked = GetLastClickedStaticNpc();
            sb.Append(" | LastNPCClicked=").Append(clicked == null ? "<null>" : clicked.DisplayName);
            return sb.ToString();
        }

        private static string GetMessageBoxHandlerSummary(DaggerfallMessageBox box)
        {
            if (box == null)
                return "<none>";

            try
            {
                List<string> names = new List<string>();
                Type type = box.GetType();
                while (type != null)
                {
                    FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    for (int i = 0; i < fields.Length; i++)
                    {
                        FieldInfo field = fields[i];
                        if (!typeof(Delegate).IsAssignableFrom(field.FieldType))
                            continue;
                        Delegate handlers = null;
                        try { handlers = field.GetValue(box) as Delegate; } catch { }
                        if (handlers == null)
                            continue;

                        Delegate[] invocationList = handlers.GetInvocationList();
                        for (int j = 0; j < invocationList.Length; j++)
                        {
                            Delegate handler = invocationList[j];
                            Type targetType = handler.Target == null ? handler.Method.DeclaringType : handler.Target.GetType();
                            string typeName = targetType == null ? "?" : (targetType.FullName ?? targetType.Name);
                            string entry = field.Name + ":" + typeName + "." + handler.Method.Name;
                            if (!names.Contains(entry))
                                names.Add(entry);
                        }
                    }
                    type = type.BaseType;
                }
                return names.Count == 0 ? "<none>" : string.Join(",", names.ToArray());
            }
            catch (Exception ex)
            {
                return "<error:" + ex.GetType().Name + ">";
            }
        }

        private static bool IsQuestScriptPromptMessageBox(DaggerfallMessageBox box)
        {
            if (box == null)
                return false;

            // Strong signal: quest Prompt.Update() subscribes Prompt.MessageBox_OnButtonClick
            // directly to DaggerfallMessageBox.OnButtonClick. Inspect the event backing delegate
            // rather than guessing from parchment appearance or Yes/No captions.
            try
            {
                FieldInfo eventField = typeof(DaggerfallMessageBox).GetField(
                    "OnButtonClick", BindingFlags.Instance | BindingFlags.NonPublic);
                Delegate handlers = eventField == null ? null : eventField.GetValue(box) as Delegate;
                if (handlers != null)
                {
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
            }
            catch { }

            // Do not infer quest ownership from Yes/No buttons or LastNPCClicked state.
            // That state can legitimately persist after a quest interaction and would cause unrelated
            // modal confirmations (notably the game's quit confirmation) to be voiced/decorated as NPC text.
            // Unknown/custom prompt implementations remain untouched unless they expose an actual quest callback.
            return false;
        }

        private static bool HasCredibleQuestPromptSpeaker()
        {
            // Quest scripting is also used by tutorial/system prompts. Only convert a Prompt/PromptMulti
            // box into NPC dialogue when DFU can tie it to an actual NPC speaker.
            StaticNPC clicked = GetLastClickedStaticNpc();
            if (clicked == null)
                return false;

            try
            {
                if (GameManager.Instance != null && GameManager.Instance.QuestMachine != null &&
                    GameManager.Instance.QuestMachine.IsLastNPCClickedAnActiveQuestor())
                    return true;
            }
            catch { }

            try
            {
                if (TalkManager.Instance != null &&
                    !string.IsNullOrWhiteSpace(TalkManager.Instance.NameNPC) &&
                    !string.Equals(TalkManager.Instance.NameNPC.Trim(), "Unknown NPC", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { }

            return false;
        }

        private static bool HasYesNoButtons(DaggerfallMessageBox box)
        {
            try
            {
                IList buttons = ReadMember(box, "buttons") as IList;
                if (buttons == null)
                    return false;

                bool yes = false;
                bool no = false;
                for (int i = 0; i < buttons.Count; i++)
                {
                    Button button = buttons[i] as Button;
                    if (button == null || button.Tag == null)
                        continue;
                    int tag = Convert.ToInt32(button.Tag, CultureInfo.InvariantCulture);
                    if (tag == (int)DaggerfallMessageBox.MessageBoxButtons.Yes) yes = true;
                    if (tag == (int)DaggerfallMessageBox.MessageBoxButtons.No) no = true;
                }
                return yes && no;
            }
            catch
            {
                return false;
            }
        }

        private static object FindQuestOfferInPreviousChain(object window)
        {
            object current = window;
            HashSet<object> seen = new HashSet<object>();
            for (int depth = 0; depth < 12 && current != null; depth++)
            {
                if (!seen.Add(current))
                    break;

                object previous = ReadMember(current, "PreviousWindow");
                if (previous == null)
                    break;
                if (LooksLikeQuestOfferWindow(previous))
                    return previous;
                current = previous;
            }
            return null;
        }

        private static bool LooksLikeQuestOfferWindow(object window)
        {
            if (window == null)
                return false;
            try
            {
                UIWindowType? registeredType = UIWindowFactory.GetWindowType(window.GetType());
                if (registeredType.HasValue && registeredType.Value == UIWindowType.QuestOffer)
                    return true;
            }
            catch { }

            string name = window.GetType().Name ?? string.Empty;
            return name.IndexOf("QuestOffer", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private string ExtractQuestOfferText(object window)
        {
            DaggerfallBaseWindow baseWindow = window as DaggerfallBaseWindow;
            if (baseWindow == null || baseWindow.ParentPanel == null)
                return string.Empty;

            List<string> pieces = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            BaseScreenComponent skip = questOfferPresentation != null && ReferenceEquals(questOfferPresentation.DisplayWindow, window)
                ? questOfferPresentation.ContentPanel : null;
            CollectVisibleQuestText(baseWindow.ParentPanel, pieces, seen, skip);

            if (pieces.Count == 0)
                return string.Empty;

            return NormalizeText(string.Join(" ", pieces.ToArray()));
        }

        private static void CollectVisibleQuestText(BaseScreenComponent component, List<string> pieces, HashSet<string> seen, BaseScreenComponent skip)
        {
            if (component == null || ReferenceEquals(component, skip))
                return;

            string typeName = component.GetType().Name ?? string.Empty;
            // Button/clickable captions are player choices, not NPC speech.
            if (typeName.IndexOf("Button", StringComparison.OrdinalIgnoreCase) < 0)
                CollectComponentText(component, pieces, seen);

            Panel panel = component as Panel;
            if (panel == null || panel.Components == null)
                return;

            for (int i = 0; i < panel.Components.Count; i++)
                CollectVisibleQuestText(panel.Components[i], pieces, seen, skip);
        }

        private static void CollectComponentText(BaseScreenComponent component, List<string> pieces, HashSet<string> seen)
        {
            object raw = ReadMember(component, "Text");
            AddQuestTextCandidate(raw as string, pieces, seen);

            // Daggerfall's formatted quest/message text is often a MultiFormatTextLabel rather
            // than a simple TextLabel. Its rendered rows are internal child labels, so inspect
            // only that component's label/text collections as a fallback.
            string typeName = component.GetType().Name ?? string.Empty;
            if (typeName.IndexOf("MultiFormatTextLabel", StringComparison.OrdinalIgnoreCase) < 0)
                return;

            Type type = component.GetType();
            while (type != null)
            {
                try
                {
                    FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    for (int i = 0; i < fields.Length; i++)
                    {
                        object value = fields[i].GetValue(component);
                        TextLabel label = value as TextLabel;
                        if (label != null)
                        {
                            AddQuestTextCandidate(label.Text, pieces, seen);
                            continue;
                        }

                        IList list = value as IList;
                        if (list == null || list.Count > 256)
                            continue;
                        for (int j = 0; j < list.Count; j++)
                        {
                            object entry = list[j];
                            TextLabel row = entry as TextLabel;
                            if (row != null)
                                AddQuestTextCandidate(row.Text, pieces, seen);
                            else if (entry is string)
                                AddQuestTextCandidate((string)entry, pieces, seen);
                            else
                                AddQuestTextCandidate(ReadMember(entry, "text") as string, pieces, seen);
                        }
                    }
                }
                catch { }
                type = type.BaseType;
            }
        }

        private static void AddQuestTextCandidate(string text, List<string> pieces, HashSet<string> seen)
        {
            text = NormalizeText(text);
            if (!string.IsNullOrWhiteSpace(text) && LooksLikeSpokenQuestText(text) && seen.Add(text))
                pieces.Add(text);
        }

        private static bool LooksLikeSpokenQuestText(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length < 18)
                return false;

            int words = 0;
            bool inWord = false;
            for (int i = 0; i < text.Length; i++)
            {
                bool letterOrDigit = char.IsLetterOrDigit(text[i]);
                if (letterOrDigit && !inWord)
                    words++;
                inWord = letterOrDigit;
            }
            return words >= 4;
        }

        private NPCIdentity ResolveQuestOfferIdentity(object window)
        {
            object npcData = ReadMember(window, "questorNPC") ?? FindQuestOfferNpcData(window);
            StaticNPC clickedNpc = GetLastClickedStaticNpc();

            // QuestMachine `prompt` windows contain no questorNPC field. In that path the most
            // authoritative speaker identity is the StaticNPC that triggered the quest action.
            if (npcData == null && clickedNpc != null)
                npcData = clickedNpc.Data;

            if (npcData == null)
                return ResolveIdentity();

            NPCIdentity id = new NPCIdentity();
            id.NpcType = "Static";

            object name = ReadMember(window, "npcName") ?? ReadMember(window, "nameNPC") ??
                          ReadMember(window, "questorName") ?? ReadMember(window, "displayName");
            id.Name = name == null ? string.Empty : name.ToString().Trim();
            if (string.IsNullOrWhiteSpace(id.Name) && clickedNpc != null &&
                QuestNpcDataMatches(clickedNpc.Data, npcData) && !string.IsNullOrWhiteSpace(clickedNpc.DisplayName))
                id.Name = clickedNpc.DisplayName.Trim();
            if (string.IsNullOrWhiteSpace(id.Name))
                id.Name = ResolveQuestOfferNpcName(npcData);
            if (string.IsNullOrWhiteSpace(id.Name))
                id.Name = "Quest Giver";

            id.Race = CleanEnumName(ReadMember(npcData, "race"), "Unknown");
            id.Gender = CleanEnumName(ReadMember(npcData, "gender"), "Unknown");
            id.Faction = Stringify(ReadMember(npcData, "factionID"));
            id.MapId = Stringify(ReadMember(npcData, "mapID"));
            id.LocationId = Stringify(ReadMember(npcData, "locationID"));
            id.StaticHash = Stringify(ReadMember(npcData, "nameSeed"));
            if (string.IsNullOrEmpty(id.StaticHash))
                id.StaticHash = Stringify(ReadMember(npcData, "hash"));

            object social = ReadMember(window, "socialGroup") ?? ReadMember(window, "npcSocialGroup") ??
                            ReadMember(npcData, "socialGroup");
            string socialRoleHint = CleanEnumName(social, "Commoners");
            id.Role = NormalizeRole(socialRoleHint);
            if (LooksLikeJudgeIdentity(id.Name, id.NpcType, socialRoleHint))
                id.Role = "Judge";

            // Preserve any portrait identity already resolved by the normal TalkWindow/Dynamic Portraits path.
            // QuestOffer does not need to independently parse Arena2 face records.
            NPCIdentity activeIdentity = ResolveIdentity();
            if (activeIdentity != null && !string.IsNullOrEmpty(activeIdentity.PortraitKey))
                id.PortraitKey = activeIdentity.PortraitKey;

            id.AssignmentKey = BuildPersistentIdentityKey(id);
            id.StableKey = string.Join("|", new string[] {
                id.Name, id.NpcType, id.Race, id.Gender, id.Role, id.Faction,
                id.MapId, id.LocationId, id.StaticHash, id.PortraitKey
            });
            return id;
        }

        private static string ResolveQuestOfferNpcName(object npcData)
        {
            // Quest offers are opened before TalkManager.SetTargetNPC(), so TalkManager.NameNPC can
            // still be empty or stale here. QuestMachine retains the StaticNPC the player actually
            // clicked; use its already-resolved DisplayName whenever it matches the offer data.
            try
            {
                StaticNPC clicked = GetLastClickedStaticNpc();
                if (clicked != null && QuestNpcDataMatches(clicked.Data, npcData) && !string.IsNullOrWhiteSpace(clicked.DisplayName))
                    return clicked.DisplayName.Trim();
            }
            catch { }

            try
            {
                if (TalkManager.Instance != null && !string.IsNullOrWhiteSpace(TalkManager.Instance.NameNPC))
                    return TalkManager.Instance.NameNPC.Trim();
            }
            catch { }
            return string.Empty;
        }

        private static StaticNPC GetLastClickedStaticNpc()
        {
            try
            {
                if (GameManager.Instance == null || GameManager.Instance.QuestMachine == null)
                    return null;
                return GameManager.Instance.QuestMachine.LastNPCClicked;
            }
            catch { return null; }
        }

        private static bool QuestNpcDataMatches(StaticNPC.NPCData clickedData, object questData)
        {
            if (questData == null)
                return true;

            try
            {
                int questHash = ToInt(ReadMember(questData, "hash"), int.MinValue);
                int questNameSeed = ToInt(ReadMember(questData, "nameSeed"), int.MinValue);
                int questMap = ToInt(ReadMember(questData, "mapID"), int.MinValue);
                int questLocation = ToInt(ReadMember(questData, "locationID"), int.MinValue);

                if (questHash != int.MinValue && clickedData.hash == questHash)
                    return true;
                if (questNameSeed != int.MinValue && clickedData.nameSeed == questNameSeed &&
                    (questMap == int.MinValue || clickedData.mapID == questMap) &&
                    (questLocation == int.MinValue || clickedData.locationID == questLocation))
                    return true;
            }
            catch { }
            return false;
        }

        private static Texture2D GetCurrentTalkPortraitTexture()
        {
            try
            {
                DaggerfallTalkWindow talkWindow = DaggerfallUI.Instance == null ? null : DaggerfallUI.Instance.TalkWindow;
                if (talkWindow == null)
                    return null;

                // Prefer the actual portrait panel. A portrait mod can replace this texture without NPCVO
                // needing to know how that mod resolved or generated the face.
                object portraitPanelObject = ReadMember(talkWindow, "panelPortrait");
                Panel portraitPanel = portraitPanelObject as Panel;
                if (portraitPanel != null && portraitPanel.BackgroundTexture != null)
                    return portraitPanel.BackgroundTexture;

                return ReadMember(talkWindow, "texturePortrait") as Texture2D;
            }
            catch
            {
                return null;
            }
        }

        private static Texture2D ResolveQuestOfferPortraitTexture(object ownerWindow)
        {
            StaticNPC clicked = GetLastClickedStaticNpc();
            object questData = ownerWindow == null ? null : (ReadMember(ownerWindow, "questorNPC") ?? FindQuestOfferNpcData(ownerWindow));
            if (clicked == null || !QuestNpcDataMatches(clicked.Data, questData))
                return GetCurrentTalkPortraitTexture();

            TalkManager talk = TalkManager.Instance;
            DaggerfallTalkWindow talkWindow = DaggerfallUI.Instance == null ? null : DaggerfallUI.Instance.TalkWindow;
            if (talk == null || talkWindow == null)
                return GetCurrentTalkPortraitTexture();

            // Reuse DFU's own private billboard-to-face resolver so generic, noble, and special NPCs
            // resolve exactly as they do on the Ask screen. Only the resolver's temporary target field
            // is changed and restored; NPCVO does not enter a Talk conversation or alter quest state.
            FieldInfo targetField = typeof(TalkManager).GetField("targetStaticNPC", BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo resolver = typeof(TalkManager).GetMethod("GetPortraitIndexFromStaticNPCBillboard", BindingFlags.Instance | BindingFlags.NonPublic);
            if (targetField == null || resolver == null)
                return GetCurrentTalkPortraitTexture();

            object oldTarget = null;
            try
            {
                oldTarget = targetField.GetValue(talk);
                targetField.SetValue(talk, clicked);

                ParameterInfo[] parameters = resolver.GetParameters();
                if (parameters == null || parameters.Length != 2)
                    return GetCurrentTalkPortraitTexture();

                Type archiveType = parameters[0].ParameterType.IsByRef ? parameters[0].ParameterType.GetElementType() : parameters[0].ParameterType;
                object archiveValue = Activator.CreateInstance(archiveType);
                object[] args = new object[] { archiveValue, 0 };
                resolver.Invoke(talk, args);

                DaggerfallTalkWindow.FacePortraitArchive archive = (DaggerfallTalkWindow.FacePortraitArchive)args[0];
                int record = Convert.ToInt32(args[1], CultureInfo.InvariantCulture);
                talkWindow.SetNPCPortrait(archive, record);
                return GetCurrentTalkPortraitTexture();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[NPCVO] Quest portrait resolver fallback: " + ex.Message);
                return GetCurrentTalkPortraitTexture();
            }
            finally
            {
                try { targetField.SetValue(talk, oldTarget); } catch { }
            }
        }

        private QuestOfferPresentationState EnsureQuestOfferPresentation(object ownerWindow, object displayWindow, NPCIdentity identity, string text)
        {
            DaggerfallBaseWindow baseWindow = displayWindow as DaggerfallBaseWindow;
            if (baseWindow == null || baseWindow.ParentPanel == null)
                return null;

            if (questOfferPresentation != null && ReferenceEquals(questOfferPresentation.DisplayWindow, displayWindow))
            {
                questOfferPresentation.FullText = text ?? string.Empty;
                if (questOfferPresentation.NameLabel != null)
                    questOfferPresentation.NameLabel.Text = identity == null ? "Quest Giver" : identity.Name;
                RefreshQuestOfferPresentationLayout(questOfferPresentation);
                if (questOfferPresentation.DialogueLabel != null && !config.RevealNpcTextWithSpeech)
                    questOfferPresentation.DialogueLabel.Text = questOfferPresentation.FullText;
                if (externalPlayerIdentityRegistered)
                    ApplyExternalPlayerTurn(questOfferPresentation, externalPlayerDisplayedText);
                DaggerfallMessageBox existingMessageBox = displayWindow as DaggerfallMessageBox;
                if (existingMessageBox != null)
                    EnsureQuestChoiceProxyButtons(existingMessageBox, questOfferPresentation);
                return questOfferPresentation;
            }

            DestroyQuestOfferPresentation();

            Panel host = null;
            Panel buttonPanel = null;
            BaseScreenComponent nativeText = null;
            DaggerfallMessageBox messageBox = displayWindow as DaggerfallMessageBox;
            if (messageBox != null)
            {
                host = ReadMember(messageBox, "messagePanel") as Panel;
                buttonPanel = ReadMember(messageBox, "buttonPanel") as Panel;
                nativeText = ReadMember(messageBox, "label") as BaseScreenComponent;
            }
            if (host == null)
                host = baseWindow.ParentPanel;

            QuestOfferPresentationState state = new QuestOfferPresentationState();
            state.OwnerWindow = ownerWindow;
            state.DisplayWindow = displayWindow;
            state.HostPanel = host;
            state.FullText = text ?? string.Empty;
            state.NativeTextComponent = nativeText;
            state.NativeTextWasEnabled = nativeText == null || nativeText.Enabled;
            state.OriginalHostSize = host.Size;
            state.ButtonPanel = buttonPanel;
            if (buttonPanel != null)
                state.OriginalButtonPosition = buttonPanel.Position;

            // A direct/custom QuestOffer window may not use DaggerfallMessageBox.label. In that
            // case hide only components that actually contain paragraph-like quest text, while
            // skipping button subtrees so native Accept/Refuse/Yes/No controls remain untouched.
            if (nativeText == null)
                HideQuestOfferTextComponents(host, state);

            // Message boxes size themselves to their text. Widen/raise only the content shell and
            // move the existing native button strip to the bottom. No quest callbacks are replaced.
            if (messageBox != null)
            {
                host.Size = new Vector2(Mathf.Max(host.Size.x, 300f), Mathf.Clamp(Mathf.Max(host.Size.y, 176f), 176f, 188f));
                if (buttonPanel != null && buttonPanel.Size.y > 0f)
                    buttonPanel.Position = new Vector2(buttonPanel.Position.x, host.Size.y - buttonPanel.Size.y - 12f);
            }

            float buttonReserve = buttonPanel != null && buttonPanel.Size.y > 0f ? buttonPanel.Size.y + 24f : 18f;
            float contentWidth = Mathf.Max(220f, host.Size.x - 20f);
            float contentHeight = Mathf.Max(90f, host.Size.y - buttonReserve - 12f);
            Panel content = DaggerfallUI.AddPanel(new Rect(10f, 7f, contentWidth, contentHeight), host);
            content.BackgroundColor = Color.clear;
            state.ContentPanel = content;

            Panel portraitFrame = DaggerfallUI.AddPanel(new Rect(0f, 6f, 68f, 68f), content);
            portraitFrame.BackgroundColor = new Color(0f, 0f, 0f, 0.85f);
            Panel portraitPanel = DaggerfallUI.AddPanel(new Rect(2f, 2f, 64f, 64f), portraitFrame);
            state.PortraitPanel = portraitPanel;

            Texture2D texture = ResolveQuestOfferPortraitTexture(ownerWindow);
            if (texture != null)
                portraitPanel.BackgroundTexture = texture;
            state.PortraitTexture = texture;

            // Keep speaker identity attached to the portrait instead of floating above the dialogue.
            TextLabel nameLabel = DaggerfallUI.AddTextLabel(
                DaggerfallUI.DefaultFont, new Vector2(0f, 76f), identity == null ? "Quest Giver" : identity.Name, content);
            nameLabel.MaxCharacters = -1;
            nameLabel.MaxWidth = 70;
            nameLabel.WrapText = true;
            nameLabel.WrapWords = true;
            nameLabel.ShadowColor = Color.black;
            nameLabel.ShadowPosition = new Vector2(1f, 1f);
            state.NameLabel = nameLabel;

            string initialDialogueText = config.RevealNpcTextWithSpeech ? string.Empty : state.FullText;
            TextLabel dialogue = DaggerfallUI.AddTextLabel(
                DaggerfallUI.DefaultFont, new Vector2(76f, 3f), initialDialogueText, content);
            dialogue.MaxCharacters = -1;
            dialogue.MaxWidth = Mathf.RoundToInt(Mathf.Max(120f, contentWidth - 80f));
            dialogue.WrapText = true;
            dialogue.WrapWords = true;
            dialogue.ShadowColor = Color.black;
            dialogue.ShadowPosition = new Vector2(1f, 1f);
            state.DialogueLabel = dialogue;

            if (nativeText != null)
                nativeText.Enabled = false;

            if (messageBox != null)
                EnsureQuestChoiceProxyButtons(messageBox, state);
            RefreshQuestOfferPresentationLayout(state);
            questOfferPresentation = state;
            if (externalPlayerIdentityRegistered)
                ApplyExternalPlayerTurn(state, externalPlayerDisplayedText);
            Debug.Log("[NPCVO] Quest portrait UI attached | NPC=" + (identity == null ? "Quest Giver" : identity.Name) +
                " | Portrait=" + (state.PortraitTexture == null ? "unavailable" : "DFU-resolved"));
            return state;
        }

        private static void RefreshQuestOfferPresentationLayout(QuestOfferPresentationState state)
        {
            if (state == null || state.HostPanel == null)
                return;

            if (state.DisplayWindow is DaggerfallMessageBox)
            {
                // DFU's virtual UI is only 200 units tall. Earlier builds could expand this panel to
                // 260 and push it beyond both screen margins. Keep a deliberate safe area instead.
                float safeHeight = Mathf.Clamp(Mathf.Max(state.OriginalHostSize.y, 176f), 176f, 188f);
                state.HostPanel.Size = new Vector2(Mathf.Max(state.OriginalHostSize.x, 300f), safeHeight);
            }

            RefreshQuestChoiceProxyLayout(state);
            if (state.ChoiceProxyPanel == null && state.ButtonPanel != null && state.ButtonPanel.Size.y > 0f && state.DisplayWindow is DaggerfallMessageBox)
                state.ButtonPanel.Position = new Vector2(state.ButtonPanel.Position.x, state.HostPanel.Size.y - state.ButtonPanel.Size.y - 12f);

            float choiceHeight = state.ChoiceProxyPanel != null ? state.ChoiceProxyPanel.Size.y :
                (state.ButtonPanel != null ? state.ButtonPanel.Size.y : 0f);
            float buttonReserve = choiceHeight > 0f ? choiceHeight + 24f : 18f;
            float contentWidth = Mathf.Max(220f, state.HostPanel.Size.x - 20f);
            float contentHeight = Mathf.Max(90f, state.HostPanel.Size.y - buttonReserve - 12f);
            if (state.ContentPanel != null)
            {
                state.ContentPanel.Position = new Vector2(10f, 7f);
                state.ContentPanel.Size = new Vector2(contentWidth, contentHeight);
            }
            float playerPortraitX = ConversationLayout.GetQuestPlayerPortraitX(contentWidth, 68f);
            if (state.DialogueLabel != null)
            {
                state.DialogueLabel.Position = new Vector2(76f, 3f);
                bool playerVisible = state.PlayerPortraitFrame != null && state.PlayerPortraitFrame.Enabled;
                float dialogueWidth = playerVisible ? playerPortraitX - 82f : contentWidth - 80f;
                state.DialogueLabel.MaxWidth = Mathf.RoundToInt(Mathf.Max(100f, dialogueWidth));
            }
            if (state.PlayerPortraitFrame != null)
                state.PlayerPortraitFrame.Position = new Vector2(playerPortraitX, 6f);
            if (state.PlayerNameLabel != null)
            {
                state.PlayerNameLabel.Position = new Vector2(Mathf.Max(0f, playerPortraitX - 2f), 76f);
                state.PlayerNameLabel.MaxWidth = 72;
            }
            if (state.PlayerDialogueLabel != null)
            {
                state.PlayerDialogueLabel.Position = new Vector2(76f, 82f);
                state.PlayerDialogueLabel.MaxWidth = Mathf.RoundToInt(Mathf.Max(100f, playerPortraitX - 82f));
            }
        }

        private void ApplyExternalPlayerTurn(QuestOfferPresentationState state, string partialText)
        {
            if (state == null || state.ContentPanel == null)
                return;

            if (state.PlayerPortraitFrame == null)
            {
                state.PlayerPortraitFrame = DaggerfallUI.AddPanel(new Rect(0f, 6f, 68f, 68f), state.ContentPanel);
                state.PlayerPortraitFrame.BackgroundColor = new Color(0f, 0f, 0f, 0.85f);
                state.PlayerPortraitPanel = DaggerfallUI.AddPanel(new Rect(2f, 2f, 64f, 64f), state.PlayerPortraitFrame);
                state.PlayerNameLabel = DaggerfallUI.AddTextLabel(DaggerfallUI.DefaultFont, new Vector2(0f, 76f), "You", state.ContentPanel);
                state.PlayerNameLabel.MaxCharacters = -1;
                state.PlayerNameLabel.MaxWidth = 72;
                state.PlayerNameLabel.WrapText = true;
                state.PlayerNameLabel.WrapWords = true;
                state.PlayerNameLabel.ShadowColor = Color.black;
                state.PlayerNameLabel.ShadowPosition = new Vector2(1f, 1f);
                state.PlayerDialogueLabel = DaggerfallUI.AddTextLabel(DaggerfallUI.DefaultFont, new Vector2(76f, 82f), string.Empty, state.ContentPanel);
                state.PlayerDialogueLabel.MaxCharacters = -1;
                state.PlayerDialogueLabel.WrapText = true;
                state.PlayerDialogueLabel.WrapWords = true;
                state.PlayerDialogueLabel.ShadowColor = Color.black;
                state.PlayerDialogueLabel.ShadowPosition = new Vector2(1f, 1f);
            }

            state.PlayerPortraitFrame.Enabled = true;
            state.PlayerPortraitPanel.Enabled = true;
            state.PlayerNameLabel.Enabled = true;
            state.PlayerDialogueLabel.Enabled = true;

            // Keep both halves visible. During a player turn, DialogueLabel contains the NPC's previous
            // line; after the turn ends it becomes the typewriter target for the NPC follow-up. Hiding
            // it made the panel visibly collapse between speakers and broke conversational continuity.
            if (state.DialogueLabel != null)
                state.DialogueLabel.Enabled = true;
            if (externalPlayerTurnPortrait != null)
                state.PlayerPortraitPanel.BackgroundTexture = externalPlayerTurnPortrait;
            state.PlayerNameLabel.Text = string.IsNullOrWhiteSpace(externalPlayerTurnName) ? "You" : externalPlayerTurnName;
            externalPlayerDisplayedText = partialText ?? string.Empty;
            state.PlayerDialogueLabel.Text = externalPlayerDisplayedText;
            RefreshQuestOfferPresentationLayout(state);
        }

        private void EnsureQuestChoiceProxyButtons(DaggerfallMessageBox messageBox, QuestOfferPresentationState state)
        {
            if (messageBox == null || state == null || state.HostPanel == null || state.ChoiceProxyPanel != null)
                return;

            IList nativeButtons = null;
            try { if (MessageBoxButtonsListField != null) nativeButtons = MessageBoxButtonsListField.GetValue(messageBox) as IList; }
            catch { }
            if (nativeButtons == null || nativeButtons.Count == 0)
                return;

            state.NativeButtonPanelStateCaptured = state.ButtonPanel != null;
            state.NativeButtonPanelWasEnabled = state.ButtonPanel == null || state.ButtonPanel.Enabled;
            if (state.ButtonPanel != null)
                state.ButtonPanel.Enabled = false;

            Panel proxyPanel = DaggerfallUI.AddPanel(new Rect(0f, 0f, state.HostPanel.Size.x, 18f), state.HostPanel);
            proxyPanel.BackgroundColor = Color.clear;
            state.ChoiceProxyPanel = proxyPanel;

            for (int i = 0; i < nativeButtons.Count; i++)
            {
                Button nativeButton = nativeButtons[i] as Button;
                if (nativeButton == null)
                    continue;

                Vector2 size = nativeButton.Size;
                if (size.x <= 0f || size.y <= 0f)
                    size = new Vector2(44f, 12f);

                Button proxy = DaggerfallUI.AddButton(new Rect(0f, 0f, size.x, size.y), proxyPanel);
                proxy.BackgroundTexture = nativeButton.BackgroundTexture;
                proxy.BackgroundTextureLayout = BackgroundLayout.StretchToFill;
                proxy.Tag = nativeButton.Tag;
                proxy.Hotkey = nativeButton.Hotkey;
                if (proxy.BackgroundTexture == null && nativeButton.Label != null)
                    proxy.Label.Text = nativeButton.Label.Text;

                Button captured = nativeButton;
                proxy.OnMouseClick += delegate(BaseScreenComponent sender, Vector2 position)
                {
                    try { captured.TriggerMouseClick(); }
                    catch (Exception ex) { Debug.LogWarning("[NPCVO] Quest choice proxy click failed: " + ex.Message); }
                };
                state.ChoiceProxyButtons.Add(proxy);
            }

            RefreshQuestChoiceProxyLayout(state);
            Debug.Log("[NPCVO] Quest choice proxy attached | Buttons=" + state.ChoiceProxyButtons.Count);
        }

        private static void RefreshQuestChoiceProxyLayout(QuestOfferPresentationState state)
        {
            if (state == null || state.ChoiceProxyPanel == null || state.HostPanel == null)
                return;

            float spacing = 8f;
            float totalWidth = 0f;
            float maxHeight = 0f;
            for (int i = 0; i < state.ChoiceProxyButtons.Count; i++)
            {
                Button button = state.ChoiceProxyButtons[i];
                if (button == null) continue;
                if (totalWidth > 0f) totalWidth += spacing;
                totalWidth += button.Size.x;
                maxHeight = Mathf.Max(maxHeight, button.Size.y);
            }
            if (maxHeight <= 0f) maxHeight = 12f;

            state.ChoiceProxyPanel.Size = new Vector2(state.HostPanel.Size.x, maxHeight);
            state.ChoiceProxyPanel.Position = new Vector2(0f, Mathf.Max(0f, state.HostPanel.Size.y - maxHeight - 8f));
            float x = Mathf.Max(0f, (state.HostPanel.Size.x - totalWidth) * 0.5f);
            for (int i = 0; i < state.ChoiceProxyButtons.Count; i++)
            {
                Button button = state.ChoiceProxyButtons[i];
                if (button == null) continue;
                button.Position = new Vector2(x, 0f);
                x += button.Size.x + spacing;
            }
        }

        private void HideExternalPlayerTurn(QuestOfferPresentationState state)
        {
            if (state == null)
                return;
            if (state.PlayerPortraitFrame != null) state.PlayerPortraitFrame.Enabled = false;
            if (state.PlayerPortraitPanel != null) state.PlayerPortraitPanel.Enabled = false;
            if (state.PlayerNameLabel != null) state.PlayerNameLabel.Enabled = false;
            if (state.PlayerDialogueLabel != null) state.PlayerDialogueLabel.Enabled = false;
            if (state.DialogueLabel != null) state.DialogueLabel.Enabled = true;
            RefreshQuestOfferPresentationLayout(state);
        }

        private static void HideQuestOfferTextComponents(BaseScreenComponent component, QuestOfferPresentationState state)
        {
            if (component == null || state == null)
                return;

            string typeName = component.GetType().Name ?? string.Empty;
            if (typeName.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0)
                return;

            bool containsSpokenText = false;
            if (component is TextLabel)
            {
                string candidate = NormalizeText(((TextLabel)component).Text);
                containsSpokenText = LooksLikeSpokenQuestText(candidate);
            }
            else if (typeName.IndexOf("MultiFormatTextLabel", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                List<string> pieces = new List<string>();
                HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                CollectComponentText(component, pieces, seen);
                containsSpokenText = pieces.Count > 0;
            }

            if (containsSpokenText)
            {
                QuestHiddenComponentState hidden = new QuestHiddenComponentState();
                hidden.Component = component;
                hidden.WasEnabled = component.Enabled;
                state.HiddenTextComponents.Add(hidden);
                component.Enabled = false;
                return;
            }

            Panel panel = component as Panel;
            if (panel == null || panel.Components == null)
                return;
            for (int i = 0; i < panel.Components.Count; i++)
                HideQuestOfferTextComponents(panel.Components[i], state);
        }

        private void DestroyQuestOfferPresentation()
        {
            QuestOfferPresentationState state = questOfferPresentation;
            questOfferPresentation = null;
            if (state == null)
                return;

            if (state.NativeTextComponent != null)
                state.NativeTextComponent.Enabled = state.NativeTextWasEnabled;
            if (state.HiddenTextComponents != null)
            {
                for (int i = 0; i < state.HiddenTextComponents.Count; i++)
                {
                    QuestHiddenComponentState hidden = state.HiddenTextComponents[i];
                    if (hidden != null && hidden.Component != null)
                        hidden.Component.Enabled = hidden.WasEnabled;
                }
            }
            if (state.HostPanel != null)
                state.HostPanel.Size = state.OriginalHostSize;
            if (state.ButtonPanel != null)
            {
                state.ButtonPanel.Position = state.OriginalButtonPosition;
                if (state.NativeButtonPanelStateCaptured)
                    state.ButtonPanel.Enabled = state.NativeButtonPanelWasEnabled;
            }
            if (state.ChoiceProxyPanel != null && state.HostPanel != null)
            {
                try { state.HostPanel.Components.Remove(state.ChoiceProxyPanel); } catch { }
            }
            if (state.ContentPanel != null && state.HostPanel != null)
            {
                try { state.HostPanel.Components.Remove(state.ContentPanel); } catch { }
            }
        }

        private static object FindQuestOfferNpcData(object window)
        {
            if (window == null)
                return null;

            Type type = window.GetType();
            while (type != null)
            {
                try
                {
                    FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    for (int i = 0; i < fields.Length; i++)
                    {
                        object value = fields[i].GetValue(window);
                        if (IsStaticNpcData(value))
                            return value;
                    }

                    PropertyInfo[] properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    for (int i = 0; i < properties.Length; i++)
                    {
                        PropertyInfo property = properties[i];
                        if (property.GetIndexParameters().Length != 0 || !property.CanRead)
                            continue;
                        object value = null;
                        try { value = property.GetValue(window, null); } catch { }
                        if (IsStaticNpcData(value))
                            return value;
                    }
                }
                catch { }
                type = type.BaseType;
            }
            return null;
        }

        private static bool IsStaticNpcData(object value)
        {
            if (value == null)
                return false;
            Type type = value.GetType();
            string fullName = type.FullName ?? string.Empty;
            return fullName.IndexOf("StaticNPC+NPCData", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (string.Equals(type.Name, "NPCData", StringComparison.OrdinalIgnoreCase) &&
                    fullName.IndexOf("StaticNPC", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private void HandleConversationClosed()
        {
            DestroyTalkConversationPresentation();
            RestoreAllProgressiveDialogue();
            currentConversationWindow = null;
            currentConversationList = null;
            observedConversationItems.Clear();
            lastConversationCount = 0;
            deferredTalkAnswer = null;
            waitingForPlayerTalkQuestion = false;

            if (config.StopWhenTalkWindowCloses)
                StopSpeech(false);
        }

        private object FindConversationWindow()
        {
            UserInterfaceManager manager = DaggerfallUI.UIManager as UserInterfaceManager;
            if (manager == null)
                return null;

            object top = manager.TopWindow;
            // Merchant/shop/trade service menus are intentionally silent. If one is on top of a
            // TalkWindow, do not scan through it to the underlying conversation: entering the menu
            // also stops any trailing NPCVO line so shopping stays unobtrusive.
            if (IsMerchantServiceWindow(top) || IsCourtContextWindow(top))
                return null;
            if (LooksLikeConversationWindow(top))
                return top;

            // Advanced Dialogue and other TalkWindow replacements can sit in the UI stack.
            // Reflection keeps this compatible without a hard dependency on those mods.
            if (UiWindowStackField != null)
            {
                try
                {
                    IList windows = UiWindowStackField.GetValue(manager) as IList;
                    if (windows != null)
                    {
                        for (int i = windows.Count - 1; i >= 0; i--)
                        {
                            object candidate = windows[i];
                            if (LooksLikeConversationWindow(candidate))
                                return candidate;
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        private static bool IsCourtContextWindow(object window)
        {
            if (window == null)
                return false;
            DaggerfallMessageBox box = window as DaggerfallMessageBox;
            if (box != null && IsCourtMessageBox(box))
                return true;
            string typeName = window.GetType().Name ?? string.Empty;
            return typeName.IndexOf("CourtWindow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   string.Equals(typeName, "DaggerfallCourtWindow", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsMerchantServiceWindow(object window)
        {
            if (window == null)
                return false;
            string typeName = window.GetType().Name ?? string.Empty;
            if (typeName.IndexOf("Talk", StringComparison.OrdinalIgnoreCase) >= 0 ||
                typeName.IndexOf("Dialogue", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            return typeName.IndexOf("Trade", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   typeName.IndexOf("Shop", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   typeName.IndexOf("Store", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   typeName.IndexOf("Merchant", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool LooksLikeConversationWindow(object window)
        {
            if (window == null)
                return false;
            if (window is DaggerfallTalkWindow)
                return true;
            return FindConversationList(window) != null;
        }

        private ListBox FindConversationList(object window)
        {
            if (window == null)
                return null;

            try
            {
                DaggerfallTalkWindow talk = window as DaggerfallTalkWindow;
                if (talk != null && TalkListField != null)
                {
                    ListBox direct = TalkListField.GetValue(talk) as ListBox;
                    if (direct != null)
                        return direct;
                }

                Type type = window.GetType();
                while (type != null)
                {
                    FieldInfo named = type.GetField("listboxConversation", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (named != null)
                    {
                        ListBox list = named.GetValue(window) as ListBox;
                        if (list != null)
                            return list;
                    }

                    FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                    foreach (FieldInfo field in fields)
                    {
                        if (!typeof(ListBox).IsAssignableFrom(field.FieldType))
                            continue;
                        ListBox list = field.GetValue(window) as ListBox;
                        if (list != null && string.Equals(list.Name, "list_answers", StringComparison.OrdinalIgnoreCase))
                            return list;
                    }
                    type = type.BaseType;
                }
            }
            catch { }
            return null;
        }

        private static T FindTalkUiField<T>(object window, string fieldName) where T : class
        {
            if (window == null || string.IsNullOrEmpty(fieldName)) return null;
            Type type = window.GetType();
            while (type != null)
            {
                try
                {
                    FieldInfo field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public |
                        BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (field != null)
                    {
                        T value = field.GetValue(window) as T;
                        if (value != null) return value;
                    }
                }
                catch { }
                type = type.BaseType;
            }
            return null;
        }

        private static Panel FindTalkTonePanel(object window)
        {
            if (window == null) return null;
            string[] knownNames = { "panelTone", "tonePanel", "panelTones", "toneBox", "panelToneButtons" };
            for (int i = 0; i < knownNames.Length; i++)
            {
                Panel known = FindTalkUiField<Panel>(window, knownNames[i]);
                if (known != null) return known;
            }

            Type type = window.GetType();
            while (type != null)
            {
                try
                {
                    FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public |
                        BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    for (int i = 0; i < fields.Length; i++)
                    {
                        FieldInfo field = fields[i];
                        if (!typeof(Panel).IsAssignableFrom(field.FieldType) ||
                            field.Name.IndexOf("tone", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        Panel panel = field.GetValue(window) as Panel;
                        if (panel != null) return panel;
                    }
                }
                catch { }
                type = type.BaseType;
            }
            return null;
        }

        // Normal Ask/Tell-Me-About conversations stay visually vanilla. PlayerVO still voices
        // the selected question and NPCVO delays the answer until that speech finishes, but no player
        // portrait/name is injected into the host TalkWindow. This avoids collisions with Tone,
        // Advanced Dialogue, GrimoireUI, and other TalkWindow replacements.
        private void EnsureTalkConversationPresentation(object window)
        {
            if (talkConversationPresentation != null)
                DestroyTalkConversationPresentation();
        }

        private void RefreshTalkConversationPresentation(TalkConversationPresentationState state)
        {
            // Intentionally empty in v0.2.4. Normal Q&A keeps the host UI untouched.
        }

        private void UpdateTalkConversationPlayerQuestion(string rawText)
        {
            // Narration sequencing is handled independently by TrySpeakExternalPlayerTalkQuestion().
        }

        private void DestroyTalkConversationPresentation()
        {
            TalkConversationPresentationState state = talkConversationPresentation;
            talkConversationPresentation = null;
            if (state == null) return;

            if (state.PlayerQuestionLabel != null)
            {
                state.PlayerQuestionLabel.Position = state.OriginalQuestionPosition;
                state.PlayerQuestionLabel.Size = state.OriginalQuestionSize;
                state.PlayerQuestionLabel.MaxWidth = state.OriginalQuestionMaxWidth;
            }
            if (state.MainPanel != null)
            {
                state.MainPanel.Position = state.OriginalMainPanelPosition;
                state.MainPanel.Size = state.OriginalMainPanelSize;
                try { if (state.PlayerPortraitFrame != null) state.MainPanel.Components.Remove(state.PlayerPortraitFrame); } catch { }
                try { if (state.PlayerBioLabel != null) state.MainPanel.Components.Remove(state.PlayerBioLabel); } catch { }
                try { if (state.NpcBioLabel != null) state.MainPanel.Components.Remove(state.NpcBioLabel); } catch { }
            }
        }

        private void EnsurePlayerVoReflection()
        {
            if (playerVoReflectionChecked && playerVoType != null) return;
            playerVoReflectionChecked = true;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = a.GetType("PlayerVO.PlayerVOMod", false); } catch { }
                if (t == null) continue;
                playerVoType = t;
                BindingFlags sf = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                playerVoSpeakTalkQuestion = t.GetMethod("SpeakVanillaTalkQuestion", sf);
                playerVoDialogueSpeechActive = t.GetProperty("DialogueSpeechActive", sf);
                Debug.Log("[NPCVO] Daggerfall Narrator - Player detected; vanilla TalkWindow questions can be voiced before NPC answers.");
                break;
            }
        }

        private bool TrySpeakExternalPlayerTalkQuestion(string question)
        {
            EnsurePlayerVoReflection();
            if (playerVoSpeakTalkQuestion == null || string.IsNullOrWhiteSpace(question)) return false;
            try { return Convert.ToBoolean(playerVoSpeakTalkQuestion.Invoke(null, new object[] { question }), CultureInfo.InvariantCulture); }
            catch (Exception ex) { Debug.LogWarning("[NPCVO] Player question bridge failed: " + ex.Message); return false; }
        }

        private bool IsExternalPlayerDialogueSpeechActive()
        {
            EnsurePlayerVoReflection();
            if (playerVoDialogueSpeechActive == null) return false;
            try { return Convert.ToBoolean(playerVoDialogueSpeechActive.GetValue(null, null), CultureInfo.InvariantCulture); }
            catch { return false; }
        }

        private void TryFlushDeferredTalkAnswer()
        {
            if (deferredTalkAnswer == null) return;
            bool stillSpeaking = IsExternalPlayerDialogueSpeechActive();
            if (stillSpeaking && Time.realtimeSinceStartup < deferredTalkDeadline) return;

            DeferredTalkAnswer pending = deferredTalkAnswer;
            deferredTalkAnswer = null;
            if (pending.Item != null && pending.Item.textLabel != null)
                SpeakNpcLine(pending.Line, pending.Item, pending.DisplayText);
            waitingForPlayerTalkQuestion = false;
        }

        private static bool IsPlayerQuestion(ListBox.ListItem item)
        {
            try
            {
                if (item.textLabel.HorizontalAlignment == HorizontalAlignment.Right)
                    return true;
                if (ApproximatelySameColor(item.textColor, DaggerfallUI.DaggerfallQuestionTextColor))
                    return true;
            }
            catch { }
            return false;
        }

        private static bool ApproximatelySameColor(Color a, Color b)
        {
            const float e = 0.002f;
            return Mathf.Abs(a.r - b.r) < e && Mathf.Abs(a.g - b.g) < e && Mathf.Abs(a.b - b.b) < e && Mathf.Abs(a.a - b.a) < e;
        }

        private void SpeakNpcLine(string line, ListBox.ListItem item, string displayText)
        {
            SpeakNpcLine(line, item, displayText, null, "TalkWindow", null);
        }

        private void SpeakNpcLine(string line, ListBox.ListItem item, string displayText, NPCIdentity forcedIdentity, string sourceKind)
        {
            SpeakNpcLine(line, item, displayText, forcedIdentity, sourceKind, null);
        }

        private void SpeakNpcLine(string line, ListBox.ListItem item, string displayText, NPCIdentity forcedIdentity, string sourceKind, QuestOfferPresentationState questPresentation)
        {
            NPCIdentity identity = forcedIdentity ?? ResolveIdentity();
            string originalLine = line ?? string.Empty;
            string spokenLine = config.GrammarPolish ? PolishNpcText(originalLine) : originalLine;
            string originalDisplay = string.IsNullOrEmpty(displayText) ? originalLine : displayText;
            string polishedDisplay = (config.GrammarPolish && config.DisplayPolishedText) ? PolishNpcText(originalDisplay) : originalDisplay;

            // VoicePack keys always use the original DFU line. This keeps authored/pre-generated packs
            // stable even when optional grammar/delivery polish changes what Kokoro receives or displays.
            // Capture line/hash metadata even when Pre-generated WAV only mode has no matching file;
            // npcvo_lineinfo must remain useful while authors are filling a VoicePack.
            lastSpokenLine = originalLine;
            lastIdentity = identity;
            lastVoicePackScript = NormalizeVoicePackScript(originalLine);
            lastVoicePackHash = Sha1(lastVoicePackScript);

            bool allowPreGenerated = config.SpeechSource != SpeechSourceMode.KokoroOnly;
            bool allowKokoro = config.SpeechSource != SpeechSourceMode.PreGeneratedOnly;
            string preGenerated = allowPreGenerated ? FindVoicePackWav(identity, originalLine) : string.Empty;
            ResolvedVoice voice = string.IsNullOrEmpty(preGenerated) && allowKokoro ? ResolveVoice(identity) : null;
            string emotion = string.IsNullOrEmpty(preGenerated) ? "Neutral" : "PreGenerated";
            if (string.IsNullOrEmpty(preGenerated) && voice != null)
            {
                emotion = DetermineNpcEmotion(identity, originalLine, sourceKind, voice);
                voice = ApplyEmotionToVoice(voice, emotion);
            }

            if (string.IsNullOrEmpty(preGenerated) && (voice == null || string.IsNullOrWhiteSpace(voice.VoiceId)))
            {
                if (config.SpeechSource == SpeechSourceMode.PreGeneratedOnly)
                    Debug.Log("[NPCVO] No authored WAV exists for this line; Pre-generated WAV only mode leaves vanilla text untouched.");
                else
                    Debug.LogWarning("[NPCVO] No pre-generated line or Kokoro fallback voice could be resolved.");
                return;
            }

            lastResolvedVoice = voice;
            Debug.Log("[NPCVO] Captured NPC line | Source=" + sourceKind + " | NPC=" + identity.Name +
                " | Voice=" + (string.IsNullOrEmpty(preGenerated) ? voice.VoiceId : "PRE-GEN") +
                " | Emotion=" + emotion + " | Text=\"" + originalLine + "\"");
            SpeechRequest request = new SpeechRequest();
            request.OriginalLine = originalLine;
            request.Line = spokenLine;
            request.DisplayText = polishedDisplay;
            request.Item = item;
            request.Identity = identity;
            request.Voice = voice;
            request.Emotion = emotion;
            request.PreGeneratedPath = preGenerated;
            request.QuestPresentation = questPresentation;
            if (questPresentation != null && config.GrammarPolish && config.DisplayPolishedText)
            {
                questPresentation.FullText = polishedDisplay;
                if (!config.RevealNpcTextWithSpeech && questPresentation.DialogueLabel != null)
                    questPresentation.DialogueLabel.Text = polishedDisplay;
            }

            if (config.InterruptOnNewResponse)
            {
                bool preserveDeferredVisual = waitingForPlayerTalkQuestion && item != null && progressiveDialogue.ContainsKey(item);
                StopSpeech(!preserveDeferredVisual);
                PrepareProgressiveDialogue(request);
                BeginSpeech(request);
            }
            else
            {
                PrepareProgressiveDialogue(request);
                if (speechBusy || (audioSource != null && audioSource.isPlaying))
                    speechQueue.Enqueue(request);
                else
                    BeginSpeech(request);
            }
        }

        private void BeginSpeech(SpeechRequest request)
        {
            int generation = ++speechGeneration;
            portraitSkipGeneration = -1;
            speechBusy = true;
            activeSpeechRoutine = StartCoroutine(SpeechWrapper(request, generation));
        }

        private IEnumerator SpeechWrapper(SpeechRequest request, int generation)
        {
            yield return SpeakRoutine(request, generation);
            if (generation != speechGeneration)
                yield break;
            if (voiceEngine != null && !string.IsNullOrEmpty(activeVoiceEngineTurnId))
                yield return StartCoroutine(voiceEngine.CompleteTurn(activeVoiceEngineTurnId));
            activeVoiceEngineTurnId = string.Empty;

            RestoreProgressiveDialogue(request);
            speechBusy = false;
            activeSpeechRoutine = null;
            if (!config.InterruptOnNewResponse && speechQueue.Count > 0)
                BeginSpeech(speechQueue.Dequeue());
        }

        private IEnumerator SpeakRoutine(SpeechRequest request, int generation)
        {
            string line = request.Line;
            NPCIdentity identity = request.Identity;
            ResolvedVoice voice = request.Voice;

            DaggerfallVoiceEngineTurn engineTurn = new DaggerfallVoiceEngineTurn();
            int priority = request.QuestPresentation != null ? 112 : 110;
            if (voiceEngine != null)
                yield return StartCoroutine(voiceEngine.AcquireTurn(priority, request.QuestPresentation != null ? "quest-dialogue" : "npc-dialogue",
                    Sha1((request.OriginalLine ?? line) + "|" + (identity == null ? string.Empty : identity.Name)), 30000,
                    delegate { return generation == speechGeneration; }, engineTurn));
            activeVoiceEngineTurnId = engineTurn.Ready ? engineTurn.Id : string.Empty;
            if (engineTurn.EngineOnline && !engineTurn.Ready)
            {
                RestoreProgressiveDialogue(request);
                yield break;
            }

            string preGenerated = request.PreGeneratedPath;
            bool forceKokoro = request.ForceKokoro;
            if (!forceKokoro && config.SpeechSource != SpeechSourceMode.KokoroOnly && string.IsNullOrEmpty(preGenerated))
                preGenerated = FindVoicePackWav(identity, string.IsNullOrEmpty(request.OriginalLine) ? line : request.OriginalLine);
            if (!forceKokoro && !string.IsNullOrEmpty(preGenerated))
            {
                Debug.Log("[NPCVO] Using pre-generated VoicePack WAV: " + preGenerated);
                yield return MaybeWaitForPortraitAudio(generation);
                if (generation != speechGeneration || generation == portraitSkipGeneration)
                {
                    RestoreProgressiveDialogue(request);
                    yield break;
                }
                yield return LoadAndPlayWav(preGenerated, generation, false, request);
                yield break;
            }

            if (!forceKokoro && config.SpeechSource == SpeechSourceMode.PreGeneratedOnly)
            {
                RestoreProgressiveDialogue(request);
                yield break;
            }
            if (voice == null || string.IsNullOrWhiteSpace(voice.VoiceId))
            {
                RestoreProgressiveDialogue(request);
                yield break;
            }

            string cacheKey = Sha1(CacheSchema + "|" + NormalizeForMatch(line) + "|" + voice.VoiceId + "|" +
                voice.Speed.ToString("0.000", CultureInfo.InvariantCulture) + "|" +
                voice.PitchSemitones.ToString("0.000", CultureInfo.InvariantCulture) + "|" +
                voice.Gravel.ToString("0.000", CultureInfo.InvariantCulture) + "|" +
                voice.Saturation.ToString("0.000", CultureInfo.InvariantCulture) + "|" +
                voice.Presence.ToString("0.000", CultureInfo.InvariantCulture) + "|" +
                voice.Compression.ToString("0.000", CultureInfo.InvariantCulture) + "|" +
                voice.DoubleMix.ToString("0.000", CultureInfo.InvariantCulture) + "|" +
                voice.DoublePitchSemitones.ToString("0.000", CultureInfo.InvariantCulture) + "|" +
                voice.DoubleDelayMs.ToString("0.0", CultureInfo.InvariantCulture) + "|" +
                voice.Spectral.ToString("0.000", CultureInfo.InvariantCulture) + "|" +
                voice.Reverb.ToString("0.000", CultureInfo.InvariantCulture) + "|" +
                voice.SubharmonicMix.ToString("0.000", CultureInfo.InvariantCulture) + "|" +
                voice.SubharmonicPitch.ToString("0.000", CultureInfo.InvariantCulture) + "|" +
                voice.Hiss.ToString("0.000", CultureInfo.InvariantCulture) + "|" +
                voice.ThroatResonance.ToString("0.000", CultureInfo.InvariantCulture) + "|" +
                voice.FlutterDepth.ToString("0.000", CultureInfo.InvariantCulture) + "|" +
                voice.FlutterRate.ToString("0.000", CultureInfo.InvariantCulture) + "|" +
                voice.Croak.ToString("0.000", CultureInfo.InvariantCulture) + "|" +
                voice.PurrMix.ToString("0.000", CultureInfo.InvariantCulture) + "|" +
                voice.PurrRate.ToString("0.000", CultureInfo.InvariantCulture) + "|" +
                voice.FelineResonance.ToString("0.000", CultureInfo.InvariantCulture) + "|" +
                voice.Growl.ToString("0.000", CultureInfo.InvariantCulture) + "|" +
                voice.Breath.ToString("0.000", CultureInfo.InvariantCulture) + "|" + config.AudioStyle);
            string cachedPath = Path.Combine(cacheDir, cacheKey + ".wav");

            if (!File.Exists(cachedPath))
            {
                Debug.Log("[NPCVO] Kokoro synth request | Voice=" + voice.VoiceId + " | Cache=" + cachedPath);
                yield return SynthesizeKokoro(line, voice, cachedPath, generation);
                if (generation != speechGeneration)
                    yield break;
            }

            if (!File.Exists(cachedPath))
            {
                RestoreProgressiveDialogue(request);
                yield break;
            }

            Debug.Log("[NPCVO] Ready to play runtime WAV: " + cachedPath);
            yield return MaybeWaitForPortraitAudio(generation);
            if (generation != speechGeneration || generation == portraitSkipGeneration)
            {
                RestoreProgressiveDialogue(request);
                yield break;
            }
            yield return LoadAndPlayWav(cachedPath, generation, true, request);
        }

        private IEnumerator SynthesizeKokoro(string line, ResolvedVoice voice, string outputPath, int generation)
        {
            string url = "http://127.0.0.1:" + config.KokoroPort + "/synthesize";
            string json = "{" +
                "\"text\":\"" + JsonEscape(line) + "\"," +
                "\"voice\":\"" + JsonEscape(voice.VoiceId) + "\"," +
                "\"lang\":\"auto\"," +
                "\"speed\":" + voice.Speed.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                "\"pitch_semitones\":" + voice.PitchSemitones.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                "\"gravel\":" + voice.Gravel.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                "\"saturation\":" + voice.Saturation.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                "\"presence\":" + voice.Presence.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                "\"compression\":" + voice.Compression.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                "\"double_mix\":" + voice.DoubleMix.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                "\"double_pitch_semitones\":" + voice.DoublePitchSemitones.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                "\"double_delay_ms\":" + voice.DoubleDelayMs.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                "\"spectral\":" + voice.Spectral.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                "\"reverb\":" + voice.Reverb.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                "\"subharmonic_mix\":" + voice.SubharmonicMix.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                "\"subharmonic_pitch\":" + voice.SubharmonicPitch.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                "\"hiss\":" + voice.Hiss.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                "\"throat_resonance\":" + voice.ThroatResonance.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                "\"flutter_depth\":" + voice.FlutterDepth.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                "\"flutter_rate\":" + voice.FlutterRate.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                "\"croak\":" + voice.Croak.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                "\"purr_mix\":" + voice.PurrMix.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                "\"purr_rate\":" + voice.PurrRate.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                "\"feline_resonance\":" + voice.FelineResonance.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                "\"growl\":" + voice.Growl.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                "\"breath\":" + voice.Breath.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                "\"audio_style\":\"" + config.AudioStyle + "\"," +
                "\"module\":\"NPC\",\"turn_id\":\"" + JsonEscape(activeVoiceEngineTurnId) + "\"," +
                "\"emotion\":\"" + JsonEscape(MapVoiceEngineEmotion(voice.Emotion)) + "\",\"emotion_intensity\":" + GetVoiceEngineEmotionIntensity().ToString("0.###", CultureInfo.InvariantCulture) + "}";

            byte[] body = Encoding.UTF8.GetBytes(json);
            using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
            {
                request.uploadHandler = new UploadHandlerRaw(body);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.timeout = Mathf.Max(2, config.HttpTimeoutSeconds);
                UnityWebRequestAsyncOperation op = request.SendWebRequest();

                while (!op.isDone)
                {
                    if (generation != speechGeneration)
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
                if (failed || request.downloadHandler == null || request.downloadHandler.data == null || request.downloadHandler.data.Length <= 44)
                {
                    Debug.LogWarning("[NPCVO] Kokoro request failed. Is Daggerfall Voice Engine running? " + request.error);
                    yield break;
                }

                try
                {
                    File.WriteAllBytes(outputPath, request.downloadHandler.data);
                    File.SetLastWriteTimeUtc(outputPath, DateTime.UtcNow);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[NPCVO] Could not cache Kokoro WAV: " + ex.Message);
                }
            }
        }

        private string MapVoiceEngineEmotion(string emotion)
        {
            string e = (emotion ?? string.Empty).ToLowerInvariant();
            if (e == "friendly") return "warm";
            if (e == "formal") return "neutral";
            if (e == "angry" || e == "threatening") return "angry";
            if (e == "afraid") return "afraid";
            if (e == "sad") return "weary";
            if (e == "excited") return "triumphant";
            if (e == "suspicious") return "suspicious";
            return "neutral";
        }

        private float GetVoiceEngineEmotionIntensity()
        {
            if (config == null || !config.EmotionLayer) return 0f;
            return config.EmotionStrength == EmotionProcessingStrength.Subtle ? 0.18f :
                (config.EmotionStrength == EmotionProcessingStrength.Strong ? 0.42f : 0.28f);
        }

        private void TryEnsureEnglishVoiceLibrary()
        {
            // A pre-generated-only installation does not need to contact Kokoro or populate its
            // voice library during normal startup. Settings/console sound tests can still invoke
            // Kokoro explicitly when the user asks for one.
            if (config == null || config.SpeechSource == SpeechSourceMode.PreGeneratedOnly ||
                !config.EnsureEnglishVoiceLibrary || voiceLibraryEnsureStarted)
                return;
            voiceLibraryEnsureStarted = true;
            StartCoroutine(EnsureEnglishVoiceLibraryRoutine(false));
        }

        private IEnumerator EnsureEnglishVoiceLibraryRoutine(bool manual)
        {
            if (!manual)
                yield return new WaitForSeconds(1.5f);

            int attempts = manual ? 1 : 3;
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                string url = "http://127.0.0.1:" + config.KokoroPort + "/voices/install-english";
                bool succeeded = false;
                using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
                {
                    request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes("{}"));
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.SetRequestHeader("Content-Type", "application/json");
                    request.timeout = 300;
                    yield return request.SendWebRequest();
#if UNITY_2020_1_OR_NEWER
                    bool failed = request.result != UnityWebRequest.Result.Success;
#else
                    bool failed = request.isNetworkError || request.isHttpError;
#endif
                    if (failed)
                    {
                        lastVoiceLibraryStatus = "install request failed: " + request.error;
                        Debug.LogWarning("[NPCVO] English Kokoro voice-library install attempt " + attempt + "/" + attempts + " failed. " + request.error);
                    }
                    else
                    {
                        string responseText = request.downloadHandler == null ? string.Empty : request.downloadHandler.text;
                        lastVoiceLibraryStatus = SummarizeVoiceLibraryResponse(responseText, true);
                        Debug.Log("[NPCVO] English Kokoro voice library: " + lastVoiceLibraryStatus +
                            (string.IsNullOrEmpty(responseText) ? string.Empty : " | " + responseText));
                        succeeded = true;
                    }
                }

                if (succeeded || manual || attempt >= attempts)
                    yield break;
                yield return new WaitForSeconds(5f);
            }
        }

        private IEnumerator QueryEnglishVoiceLibraryRoutine()
        {
            string url = "http://127.0.0.1:" + config.KokoroPort + "/voices";
            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                request.timeout = Mathf.Max(2, config.HttpTimeoutSeconds);
                yield return request.SendWebRequest();
#if UNITY_2020_1_OR_NEWER
                bool failed = request.result != UnityWebRequest.Result.Success;
#else
                bool failed = request.isNetworkError || request.isHttpError;
#endif
                if (failed)
                    lastVoiceLibraryStatus = "status request failed: " + request.error;
                else
                {
                    string responseText = request.downloadHandler == null ? string.Empty : request.downloadHandler.text;
                    lastVoiceLibraryStatus = SummarizeVoiceLibraryResponse(responseText, false);
                    Debug.Log("[NPCVO] Kokoro voice-library status: " + lastVoiceLibraryStatus +
                        (string.IsNullOrEmpty(responseText) ? string.Empty : " | " + responseText));
                }
            }
        }

        private static string SummarizeVoiceLibraryResponse(string json, bool installResponse)
        {
            if (string.IsNullOrWhiteSpace(json))
                return installResponse ? "install completed" : "server reachable";
            try
            {
                Match known = Regex.Match(json, "\\\"known_count\\\"\\s*:\\s*(\\d+)");
                Match installed = Regex.Match(json, "\\\"installed_or_verified_count\\\"\\s*:\\s*(\\d+)");
                if (installResponse && known.Success && installed.Success)
                    return installed.Groups[1].Value + "/" + known.Groups[1].Value + " English voices verified";
                if (known.Success)
                    return "server reachable; " + known.Groups[1].Value + " English voices known";
            }
            catch { }
            return installResponse ? "install completed" : "server reachable";
        }

        private IEnumerator LoadAndPlayWav(string path, int generation, bool isCacheFile, SpeechRequest speechRequest)
        {
            string uri;
            try { uri = new Uri(path).AbsoluteUri; }
            catch { uri = "file:///" + path.Replace("\\", "/"); }

            using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.WAV))
            {
                UnityWebRequestAsyncOperation op = request.SendWebRequest();
                while (!op.isDone)
                {
                    if (generation != speechGeneration)
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
                if (failed)
                {
                    Debug.LogWarning("[NPCVO] Could not load WAV: " + request.error);
                    RestoreProgressiveDialogue(speechRequest);
                    yield break;
                }

                AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip == null || generation != speechGeneration)
                {
                    RestoreProgressiveDialogue(speechRequest);
                    yield break;
                }

                if (isCacheFile)
                {
                    try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { }
                }

                audioSource.Stop();
                audioSource.clip = clip;
                ApplyVolume();
                SetProgressiveReveal(speechRequest, 0f);
                audioSource.Play();
                Debug.Log("[NPCVO] Playback started | " + Path.GetFileName(path) +
                    " | Length=" + clip.length.ToString("0.00", CultureInfo.InvariantCulture) + "s");

                while (audioSource != null && audioSource.isPlaying)
                {
                    if (generation != speechGeneration)
                    {
                        audioSource.Stop();
                        break;
                    }

                    if (clip.length > 0.001f)
                        SetProgressiveReveal(speechRequest, Mathf.Clamp01(audioSource.time / clip.length));
                    yield return null;
                }

                if (generation == speechGeneration)
                    SetProgressiveReveal(speechRequest, 1f);

                if (audioSource != null && audioSource.clip == clip)
                    audioSource.clip = null;
                Destroy(clip);
            }
        }

        private IEnumerator MaybeWaitForPortraitAudio(int generation)
        {
            if (!config.DynamicPortraitsIntegration || config.PortraitAudioBehavior == PortraitAudioBehavior.Ignore)
                yield break;

            AudioSource portraitAudio = GetDynamicPortraitsAudioSource();
            if (portraitAudio == null || !portraitAudio.isPlaying)
                yield break;

            if (config.PortraitAudioBehavior == PortraitAudioBehavior.Skip)
            {
                portraitSkipGeneration = generation;
                yield break;
            }

            // Defer until the portrait greeting/reaction sound finishes. A short ceiling avoids a bad
            // external clip preventing NPCVO forever.
            float deadline = Time.realtimeSinceStartup + 8f;
            while (portraitAudio != null && portraitAudio.isPlaying && Time.realtimeSinceStartup < deadline)
            {
                if (generation != speechGeneration)
                    yield break;
                yield return null;
            }
        }

        private void StopSpeech()
        {
            StopSpeech(true);
        }

        private void StopSpeech(bool restoreProgressiveDialogue)
        {
            // Every interruption path must release the engine turn. The old bool overload stopped
            // the coroutine without cancelling its turn, leaving the suite blocked until engine timeout.
            if (voiceEngine != null && !string.IsNullOrEmpty(activeVoiceEngineTurnId))
                StartCoroutine(voiceEngine.CancelTurn(activeVoiceEngineTurnId));
            activeVoiceEngineTurnId = string.Empty;

            speechGeneration++;
            speechBusy = false;
            speechQueue.Clear();
            if (activeSpeechRoutine != null)
            {
                StopCoroutine(activeSpeechRoutine);
                activeSpeechRoutine = null;
            }
            if (audioSource != null)
            {
                AudioClip clip = audioSource.clip;
                audioSource.Stop();
                audioSource.clip = null;
                if (clip != null) Destroy(clip);
            }
            if (restoreProgressiveDialogue)
            {
                RestoreAllProgressiveDialogue();
                if (questOfferPresentation != null && questOfferPresentation.DialogueLabel != null)
                    questOfferPresentation.DialogueLabel.Text = questOfferPresentation.FullText ?? string.Empty;
            }
        }

        private void PrepareProgressiveDialogue(SpeechRequest request)
        {
            if (request == null || !config.RevealNpcTextWithSpeech)
                return;

            if (request.QuestPresentation != null && request.QuestPresentation.DialogueLabel != null)
                request.QuestPresentation.DialogueLabel.Text = string.Empty;

            if (request.Item == null || request.Item.textLabel == null)
                return;

            ProgressiveDialogueState existing;
            if (progressiveDialogue.TryGetValue(request.Item, out existing))
            {
                request.ProgressiveState = existing;
                SetProgressiveReveal(request, 0f);
                return;
            }

            ProgressiveDialogueState state = new ProgressiveDialogueState();
            state.Item = request.Item;
            state.FullText = string.IsNullOrEmpty(request.DisplayText) ? request.Line : request.DisplayText;
            state.TextColor = request.Item.textColor;
            state.DisabledTextColor = request.Item.disabledTextColor;
            state.SelectedTextColor = request.Item.selectedTextColor;
            state.ShadowColor = request.Item.shadowColor;
            state.SelectedShadowColor = request.Item.selectedShadowColor;
            state.HighlightedTextColor = request.Item.highlightedTextColor;
            state.HighlightedDisabledTextColor = request.Item.highlightedDisabledTextColor;
            state.HighlightedSelectedTextColor = request.Item.highlightedSelectedTextColor;

            Panel overlayParent = currentConversationList == null ? null : currentConversationList.Parent as Panel;
            if (overlayParent == null)
            {
                Debug.LogWarning("[NPCVO] Progressive dialogue reveal could not attach an overlay; leaving native NPC text visible.");
                return;
            }

            TextLabel source = request.Item.textLabel;
            state.OriginalLabel = source;
            state.SuppressedLabel = new SuppressedTextLabel(source);

            TextLabel overlay = new TextLabel(source.Font);
            overlay.MaxCharacters = -1;
            overlay.MaxWidth = source.MaxWidth;
            overlay.WrapText = source.WrapText;
            overlay.WrapWords = source.WrapWords;
            overlay.TextScale = source.TextScale;
            // Overlay is parented to the TalkWindow panel rather than the ListBox (ListBox has no
            // child-component collection), so use absolute list-relative positioning.
            overlay.HorizontalAlignment = HorizontalAlignment.None;
            overlay.VerticalAlignment = VerticalAlignment.None;
            overlay.HorizontalTextAlignment = source.HorizontalTextAlignment;
            // Typewriter presentation: use the row's normal palette rather than DFU's selected/highlight
            // palette. The full native row remains transparent underneath while this overlay reveals
            // only the characters reached by audio playback.
            overlay.TextColor = state.TextColor;
            overlay.ShadowColor = state.ShadowColor;
            overlay.ShadowPosition = source.ShadowPosition;
            overlay.Text = string.Empty;
            // The native row is parented to the ListBox, so ParentCoordinates clips to the
            // conversation box. Our overlay is parented one level higher; use the ListBox's
            // absolute rectangle as a screen-space clip instead so scrolling cannot draw text
            // outside the conversation area.
            overlay.RestrictedRenderAreaCoordinateType = BaseScreenComponent.RestrictedRenderArea_CoordinateType.ScreenCoordinates;
            overlay.RectRestrictedRenderArea = currentConversationList.Rectangle;

            state.Overlay = overlay;
            request.ProgressiveState = state;
            progressiveDialogue[request.Item] = state;

            // Keep the full DFU text intact for notebook/history logic, but make only this row's
            // native glyphs transparent. A second label reveals the same text as audio advances.
            request.Item.textColor = Transparent(state.TextColor);
            request.Item.disabledTextColor = Transparent(state.DisabledTextColor);
            request.Item.selectedTextColor = Transparent(state.SelectedTextColor);
            request.Item.shadowColor = Transparent(state.ShadowColor);
            request.Item.selectedShadowColor = Transparent(state.SelectedShadowColor);
            request.Item.highlightedTextColor = Transparent(state.HighlightedTextColor);
            request.Item.highlightedDisabledTextColor = Transparent(state.HighlightedDisabledTextColor);
            request.Item.highlightedSelectedTextColor = Transparent(state.HighlightedSelectedTextColor);

            state.OverlayParent = overlayParent;
            overlayParent.Components.Add(overlay);

            // Hard-suppress the ListBox's own draw call. Keeping alpha at zero was not reliable
            // across DFU font/render paths and could leave the full line visible underneath the
            // progressive overlay, which looked like highlighting. The proxy keeps the complete
            // line and layout metrics but intentionally draws no glyphs.
            request.Item.textLabel = state.SuppressedLabel;
            SetProgressiveReveal(request, 0f);
        }

        private void SetProgressiveReveal(SpeechRequest request, float progress)
        {
            if (request == null)
                return;

            string full = string.IsNullOrEmpty(request.DisplayText) ? request.Line : request.DisplayText;
            string visible = GetProgressiveText(full, progress);

            if (request.QuestPresentation != null && request.QuestPresentation.DialogueLabel != null)
            {
                request.QuestPresentation.FullText = full ?? string.Empty;
                request.QuestPresentation.DialogueLabel.Text = visible;
            }

            if (request.ProgressiveState == null || request.ProgressiveState.Overlay == null)
                return;

            ProgressiveDialogueState state = request.ProgressiveState;
            TextLabel source = state.Item == null ? null : state.Item.textLabel;
            if (source == null)
                return;

            Vector2 listPosition = currentConversationList == null ? Vector2.zero : currentConversationList.Position;
            state.Overlay.Position = listPosition + source.Position;
            state.Overlay.HorizontalTextAlignment = source.HorizontalTextAlignment;
            if (currentConversationList != null)
                state.Overlay.RectRestrictedRenderArea = currentConversationList.Rectangle;
            state.Overlay.Text = visible;
        }

        private static string GetProgressiveText(string full, float progress)
        {
            full = full ?? string.Empty;
            if (progress >= 0.999f)
                return full;
            if (progress <= 0f || full.Length == 0)
                return string.Empty;

            // Kokoro currently returns a WAV without word timestamps. Use audio playback position
            // as a conservative character clock, lagged slightly so text does not race ahead.
            float lagged = Mathf.Clamp01((progress - 0.025f) / 0.975f);
            int chars = Mathf.Clamp(Mathf.FloorToInt(full.Length * lagged), 0, full.Length);
            return full.Substring(0, chars);
        }

        private void RestoreProgressiveDialogue(SpeechRequest request)
        {
            if (request == null)
                return;
            if (request.QuestPresentation != null && request.QuestPresentation.DialogueLabel != null)
                request.QuestPresentation.DialogueLabel.Text = request.QuestPresentation.FullText ?? request.DisplayText ?? request.Line ?? string.Empty;
            if (request.ProgressiveState != null)
            {
                RestoreProgressiveDialogue(request.ProgressiveState);
                request.ProgressiveState = null;
            }
        }

        private void RestoreProgressiveDialogue(ProgressiveDialogueState state)
        {
            if (state == null)
                return;

            if (state.Item != null)
            {
                if (state.OriginalLabel != null &&
                    (state.SuppressedLabel == null || ReferenceEquals(state.Item.textLabel, state.SuppressedLabel)))
                    state.Item.textLabel = state.OriginalLabel;

                state.Item.textColor = state.TextColor;
                state.Item.disabledTextColor = state.DisabledTextColor;
                state.Item.selectedTextColor = state.SelectedTextColor;
                state.Item.shadowColor = state.ShadowColor;
                state.Item.selectedShadowColor = state.SelectedShadowColor;
                state.Item.highlightedTextColor = state.HighlightedTextColor;
                state.Item.highlightedDisabledTextColor = state.HighlightedDisabledTextColor;
                state.Item.highlightedSelectedTextColor = state.HighlightedSelectedTextColor;
                progressiveDialogue.Remove(state.Item);
            }

            if (state.Overlay != null && state.OverlayParent != null)
            {
                try { state.OverlayParent.Components.Remove(state.Overlay); } catch { }
            }
        }

        private void RestoreAllProgressiveDialogue()
        {
            if (progressiveDialogue.Count == 0)
                return;

            List<ProgressiveDialogueState> states = new List<ProgressiveDialogueState>(progressiveDialogue.Values);
            progressiveDialogue.Clear();
            for (int i = 0; i < states.Count; i++)
            {
                ProgressiveDialogueState state = states[i];
                if (state == null)
                    continue;
                if (state.Item != null)
                {
                    if (state.OriginalLabel != null &&
                        (state.SuppressedLabel == null || ReferenceEquals(state.Item.textLabel, state.SuppressedLabel)))
                        state.Item.textLabel = state.OriginalLabel;

                    state.Item.textColor = state.TextColor;
                    state.Item.disabledTextColor = state.DisabledTextColor;
                    state.Item.selectedTextColor = state.SelectedTextColor;
                    state.Item.shadowColor = state.ShadowColor;
                    state.Item.selectedShadowColor = state.SelectedShadowColor;
                    state.Item.highlightedTextColor = state.HighlightedTextColor;
                    state.Item.highlightedDisabledTextColor = state.HighlightedDisabledTextColor;
                    state.Item.highlightedSelectedTextColor = state.HighlightedSelectedTextColor;
                }
                if (state.Overlay != null && state.OverlayParent != null)
                {
                    try { state.OverlayParent.Components.Remove(state.Overlay); } catch { }
                }
            }
        }

        private static Color Transparent(Color color)
        {
            color.a = 0f;
            return color;
        }

        private void ApplyVolume()
        {
            if (audioSource == null || config == null)
                return;
            float v = Mathf.Clamp01(config.Volume / 100f);
            if (config.UseGameSoundVolume)
                v *= Mathf.Clamp01(DaggerfallUnity.Settings.SoundVolume);
            audioSource.volume = v;
        }

        private NPCIdentity ResolveIdentity()
        {
            NPCIdentity id = new NPCIdentity();
            TalkManager talk = TalkManager.Instance;
            if (talk == null)
            {
                id.Name = "Unknown NPC";
                id.Race = "Unknown";
                id.Gender = "Unknown";
                id.Role = "Commoner";
                id.StableKey = "unknown";
                return id;
            }

            id.Name = string.IsNullOrWhiteSpace(talk.NameNPC) ? "Unknown NPC" : talk.NameNPC.Trim();
            id.NpcType = talk.CurrentNPCType.ToString();

            object target = null;
            if (talk.CurrentNPCType == TalkManager.NPCType.Mobile)
                target = talk.MobileNPC;
            else if (talk.CurrentNPCType == TalkManager.NPCType.Static)
                target = talk.StaticNPC;

            if (target != null)
            {
                object data = ReadMember(target, "Data");
                object race = ReadMember(target, "Race") ?? ReadMember(data, "race");
                object gender = ReadMember(target, "Gender") ?? ReadMember(data, "gender");
                id.Race = CleanEnumName(race, "Unknown");
                id.Gender = CleanEnumName(gender, "Unknown");

                id.Faction = Stringify(ReadMember(data, "factionID"));
                id.MapId = Stringify(ReadMember(data, "mapID"));
                id.LocationId = Stringify(ReadMember(data, "locationID"));
                id.StaticHash = Stringify(ReadMember(data, "hash"));
                if (string.IsNullOrEmpty(id.StaticHash) || id.StaticHash == "0")
                    id.StaticHash = Stringify(ReadMember(data, "nameSeed"));
                if (string.IsNullOrEmpty(id.StaticHash) || id.StaticHash == "0")
                    id.StaticHash = Stringify(ReadMember(data, "seed"));
                if (string.IsNullOrEmpty(id.StaticHash) || id.StaticHash == "0")
                    id.StaticHash = Stringify(ReadMember(target, "PersonFaceRecordId"));
                object isChild = ReadMember(target, "IsChildNPC");
                if (isChild is bool && (bool)isChild)
                    id.IsChild = true;
            }

            object npcData = null;
            try { if (TalkNpcDataField != null) npcData = TalkNpcDataField.GetValue(talk); } catch { }
            if (npcData != null)
            {
                if (id.Race == "Unknown")
                    id.Race = CleanEnumName(ReadMember(npcData, "race"), "Unknown");
                string socialRoleHint = CleanEnumName(ReadMember(npcData, "socialGroup"), "Commoners");
                id.Role = NormalizeRole(socialRoleHint);
                if (LooksLikeJudgeIdentity(id.Name, id.NpcType, socialRoleHint))
                    id.Role = "Judge";
                object factionData = ReadMember(npcData, "factionData");
                if (string.IsNullOrEmpty(id.Faction))
                    id.Faction = Stringify(ReadMember(factionData, "id"));
                id.GuildGroup = CleanEnumName(ReadMember(npcData, "guildGroup"), string.Empty);
            }
            else
            {
                id.Role = "Commoner";
            }

            if (string.IsNullOrWhiteSpace(id.Race)) id.Race = "Unknown";
            if (string.IsNullOrWhiteSpace(id.Gender)) id.Gender = "Unknown";
            if (string.IsNullOrWhiteSpace(id.Role)) id.Role = "Commoner";

            id.PortraitKey = GetDynamicPortraitKey();
            if (string.IsNullOrEmpty(id.PortraitKey))
                id.PortraitKey = GetFallbackPortraitKey(talk);
            if (id.IsChild || (!string.IsNullOrEmpty(id.PortraitKey) && id.PortraitKey.StartsWith("CHLD00I0.RCI_", StringComparison.OrdinalIgnoreCase)))
            {
                id.IsChild = true;
                id.Role = "Child";
            }

            id.AssignmentKey = BuildPersistentIdentityKey(id);
            id.StableKey = string.Join("|", new string[] {
                id.Name, id.NpcType, id.Race, id.Gender, id.Role, id.Faction,
                id.MapId, id.LocationId, id.StaticHash, id.PortraitKey
            });
            return id;
        }

        private static string BuildPersistentIdentityKey(NPCIdentity id)
        {
            if (id == null)
                return "unknown";

            // Keep only identity-bearing DFU data here. Role and portrait are deliberately excluded:
            // both can be re-evaluated by UI/portrait mods and must not cause a voice identity swap.
            string name = NormalizeForMatch(id.Name);
            string npcType = NormalizeForMatch(id.NpcType);
            string race = NormalizeForMatch(id.Race);
            string gender = NormalizeForMatch(id.Gender);
            string faction = NormalizeForMatch(id.Faction);
            string map = NormalizeForMatch(id.MapId);
            string location = NormalizeForMatch(id.LocationId);
            string staticHash = NormalizeForMatch(id.StaticHash);

            bool meaningfulName = !string.IsNullOrEmpty(name) &&
                name != "unknown npc" && name != "quest giver" && name != "unknown";
            bool meaningfulSeed = !string.IsNullOrEmpty(staticHash) && staticHash != "0";
            bool meaningfulPlace = !string.IsNullOrEmpty(map) || !string.IsNullOrEmpty(location);
            // Refuse to persist identities that are effectively placeholders. A transient deterministic
            // fallback is much safer than permanently merging unrelated anonymous NPCs.
            if (!meaningfulSeed && !meaningfulName)
                return "unknown";
            if (!meaningfulSeed && !meaningfulPlace && (string.IsNullOrEmpty(npcType) || npcType == "unknown"))
                return "unknown";

            return string.Join("|", new string[] {
                name, npcType, race, gender, faction, map, location, staticHash
            });
        }

        private static bool ShouldPersistVoiceAssignment(NPCIdentity id, string source, VoiceProfile profile)
        {
            if (id == null || profile == null || profile.lockVariation || string.IsNullOrEmpty(source))
                return false;
            if (string.Equals(id.NpcType, "ConsoleVoiceTest", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(id.NpcType, "SettingsVoiceTest", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(id.NpcType, "Test", StringComparison.OrdinalIgnoreCase))
                return false;
            return source.StartsWith("Race/", StringComparison.OrdinalIgnoreCase) ||
                   source.StartsWith("Gender", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(source, "Generic", StringComparison.OrdinalIgnoreCase);
        }

        private VoiceAssignment GetOrCreateVoiceAssignment(NPCIdentity id, VoiceProfile profile, List<string> validVoices)
        {
            if (id == null || profile == null || validVoices == null || validVoices.Count == 0)
                return null;

            string key = string.IsNullOrWhiteSpace(id.AssignmentKey) ? BuildPersistentIdentityKey(id) : id.AssignmentKey;
            // Never persist the catch-all identity. If DFU gives us too little identity data,
            // deterministic runtime fallback is safer than making unrelated NPCs share one slot.
            if (string.IsNullOrWhiteSpace(key) || string.Equals(key, "unknown", StringComparison.OrdinalIgnoreCase))
                return null;

            VoiceAssignment assignment;
            if (!voiceAssignmentsByKey.TryGetValue(key, out assignment) || assignment == null)
            {
                assignment = new VoiceAssignment();
                assignment.identityKey = key;
                assignment.profileKey = profile.key ?? string.Empty;
                uint hv = Fnv1a32(key + "|voice");
                uint hp = Fnv1a32(key + "|pitch");
                uint hs = Fnv1a32(key + "|speed");
                assignment.voiceId = validVoices[(int)(hv % (uint)validVoices.Count)];
                assignment.pitchUnit = ((hp % 10001u) / 5000f) - 1f;
                assignment.speedUnit = ((hs % 10001u) / 5000f) - 1f;
                assignment.displayName = id.Name ?? string.Empty;
                voiceAssignmentsByKey[key] = assignment;
                SaveVoiceAssignments();
                lastAssignmentStatus = "created persistent assignment for " + assignment.displayName;
                return assignment;
            }

            // Persistence is intentionally stronger than the current social-role pool. Once a
            // procedural NPC is cast, keep that exact base voice even if role inference or a profile
            // refresh changes which voices would be offered to a newly encountered NPC. Users can
            // intentionally recast everyone with npcvo_clear_assignments.
            bool changed = false;
            if (string.IsNullOrWhiteSpace(assignment.voiceId))
            {
                uint hv = Fnv1a32(key + "|voice|" + (profile.key ?? string.Empty));
                assignment.voiceId = validVoices[(int)(hv % (uint)validVoices.Count)];
                changed = true;
            }
            if (!string.Equals(assignment.profileKey, profile.key, StringComparison.Ordinal))
            {
                assignment.profileKey = profile.key ?? string.Empty;
                changed = true;
            }
            if (!string.Equals(assignment.displayName, id.Name, StringComparison.Ordinal))
            {
                assignment.displayName = id.Name ?? string.Empty;
                changed = true;
            }
            if (changed)
                SaveVoiceAssignments();
            return assignment;
        }

        private ResolvedVoice ResolveVoice(NPCIdentity id)
        {
            VoiceProfile profile = FindProfile(uniqueVoices, id.Name);
            string source = "Unique NPC";

            if (profile == null)
            {
                profile = FindProfile(specialVoices, id.Name);
                source = "Curated Special NPC";
            }

            if (profile == null && config.DynamicPortraitsIntegration && !string.IsNullOrEmpty(id.PortraitKey))
            {
                profile = FindProfile(portraitVoices, id.PortraitKey);
                source = "Dynamic Portraits";
            }

            if (profile == null && config.UseRoleVoiceTypes)
            {
                profile = FindProfile(voiceTypes, id.Race + id.Gender + id.Role);
                source = "Race/Gender/Social";
            }
            if (profile == null)
            {
                profile = FindProfile(voiceTypes, id.Race + id.Gender);
                source = "Race/Gender";
            }
            if (profile == null && config.UseRoleVoiceTypes)
            {
                profile = FindProfile(voiceTypes, id.Gender + id.Role);
                source = "Gender/Social";
            }
            if (profile == null)
            {
                profile = FindProfile(voiceTypes, id.Gender);
                source = "Gender";
            }
            if (profile == null)
            {
                profile = FindProfile(voiceTypes, "Generic");
                source = "Generic";
            }
            if (profile == null)
                return null;

            string[] voices = profile.voices == null ? new string[0] : profile.voices;
            List<string> valid = new List<string>();
            foreach (string v in voices)
                if (!string.IsNullOrWhiteSpace(v)) valid.Add(v.Trim());
            if (valid.Count == 0)
                return null;

            // Procedural NPC voice identity is persisted independently of transient UI state.
            // Portrait swaps, social-role refreshes, or save/load cycles therefore cannot casually
            // turn the same person into a different speaker. Exact/curated profiles remain governed
            // directly by their authored profile and do not need persistence.
            VoiceAssignment assignment = null;
            if (ShouldPersistVoiceAssignment(id, source, profile))
                assignment = GetOrCreateVoiceAssignment(id, profile, valid);

            uint hash = Fnv1a32((string.IsNullOrEmpty(id.AssignmentKey) ? id.StableKey : id.AssignmentKey) + "|" + profile.key);
            string voiceId = assignment == null || string.IsNullOrWhiteSpace(assignment.voiceId)
                ? valid[(int)(hash % (uint)valid.Count)]
                : assignment.voiceId;
            if (!string.IsNullOrWhiteSpace(config.VoiceOverride))
                voiceId = config.VoiceOverride.Trim();
            float pitch = profile.pitch;
            float speed = profile.speed <= 0f ? 1f : profile.speed;

            if (!profile.lockVariation)
            {
                float pitchRange = 0f;
                float speedRange = 0f;
                if (config.Variation == VariationMode.Subtle) { pitchRange = 0.65f; speedRange = 0.04f; }
                if (config.Variation == VariationMode.Moderate) { pitchRange = 1.25f; speedRange = 0.06f; }

                float raceVariation = IsBeastRace(id.Race) ? 1.15f : 0.75f;
                if (id.IsChild) raceVariation *= 0.70f;
                if (string.Equals(id.Role, "Noble", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(id.Role, "Judge", StringComparison.OrdinalIgnoreCase)) raceVariation *= 0.70f;
                pitchRange *= raceVariation;
                speedRange *= raceVariation;

                float pitchUnit;
                float speedUnit;
                if (assignment != null)
                {
                    pitchUnit = Mathf.Clamp(assignment.pitchUnit, -1f, 1f);
                    speedUnit = Mathf.Clamp(assignment.speedUnit, -1f, 1f);
                }
                else
                {
                    string variationKey = string.IsNullOrEmpty(id.AssignmentKey) ? id.StableKey : id.AssignmentKey;
                    uint h2 = Fnv1a32(variationKey + "|pitch");
                    uint h3 = Fnv1a32(variationKey + "|speed");
                    pitchUnit = ((h2 % 10001u) / 5000f) - 1f;
                    speedUnit = ((h3 % 10001u) / 5000f) - 1f;
                }
                pitch += pitchUnit * pitchRange;
                speed *= 1f + speedUnit * speedRange;
            }

            float dspScale = config.FantasyProcessing == FantasyProcessingStrength.Subtle ? 0.55f :
                (config.FantasyProcessing == FantasyProcessingStrength.Strong ? 1.35f : 1f);

            ResolvedVoice result = new ResolvedVoice();
            result.VoiceId = voiceId;
            result.PitchSemitones = Mathf.Clamp(pitch, -6f, 4f);
            result.Speed = Mathf.Clamp(speed, 0.70f, 1.35f);
            result.Gravel = Mathf.Clamp01(profile.gravel * dspScale);
            result.Saturation = Mathf.Clamp01(profile.saturation * dspScale);
            result.Presence = Mathf.Clamp01(profile.presence * dspScale);
            result.Compression = Mathf.Clamp01(profile.compression * dspScale);
            result.DoubleMix = Mathf.Clamp01(profile.doubleMix * dspScale);
            result.DoublePitchSemitones = Mathf.Clamp(profile.doublePitch, -4f, 4f);
            result.DoubleDelayMs = Mathf.Clamp(profile.doubleDelayMs, 0f, 80f);
            result.Spectral = Mathf.Clamp01(profile.spectral * dspScale);
            result.Reverb = Mathf.Clamp01(profile.reverb * dspScale);
            result.SubharmonicMix = Mathf.Clamp01(profile.subharmonicMix * dspScale);
            result.SubharmonicPitch = Mathf.Clamp(profile.subharmonicPitch == 0f ? -5.5f : profile.subharmonicPitch, -12f, -1f);
            result.Hiss = Mathf.Clamp01(profile.hiss * dspScale);
            result.ThroatResonance = Mathf.Clamp01(profile.throatResonance * dspScale);
            result.FlutterDepth = Mathf.Clamp(profile.flutterDepth * dspScale, 0f, 0.5f);
            result.FlutterRate = Mathf.Clamp(profile.flutterRate, 0f, 12f);
            result.Croak = Mathf.Clamp01(profile.croak * dspScale);
            result.PurrMix = Mathf.Clamp01(profile.purrMix * dspScale);
            result.PurrRate = Mathf.Clamp(profile.purrRate, 0f, 70f);
            result.FelineResonance = Mathf.Clamp01(profile.felineResonance * dspScale);
            result.Growl = Mathf.Clamp01(profile.growl * dspScale);
            result.Breath = Mathf.Clamp01(profile.breath * dspScale);
            result.DefaultEmotion = string.IsNullOrWhiteSpace(profile.defaultEmotion) ? "Neutral" : profile.defaultEmotion;
            result.ProfileKey = profile.key;
            result.Source = source;
            return result;
        }

        private string DetermineNpcEmotion(NPCIdentity identity, string line, string sourceKind, ResolvedVoice voice)
        {
            if (config == null || !config.EmotionLayer)
                return "Neutral";

            // Dynamic Portraits already evaluates reaction/mood from the same conversation state.
            // Prefer that signal when available so the face and voice react together.
            if (config.DynamicPortraitsIntegration && config.DynamicPortraitEmotionSync &&
                string.Equals(sourceKind, "TalkWindow", StringComparison.OrdinalIgnoreCase))
            {
                string portraitEmotion = GetDynamicPortraitEmotion();
                if (!string.IsNullOrEmpty(portraitEmotion))
                    return portraitEmotion;
            }

            string text = (line ?? string.Empty).ToLowerInvariant();
            if (ContainsAny(text, "kill you", "you will die", "shall die", "suffer", "eternal torment", "destroy you", "weakling", "insolence", "traitor", "revenge", "your doom"))
                return "Threatening";
            if (ContainsAny(text, "desperate", "afraid", "terrified", "fear", "save me", "help me", "please help", "will kill", "taken my child", "ransom"))
                return "Afraid";
            if (ContainsAny(text, "grief", "mourning", "mourn", "alas", "sorrow", "my beloved", "my love", "has died", "is dead", "lost my", "death of"))
                return "Sad";
            if (ContainsAny(text, "thank you", "many thanks", "welcome", "well met", "greetings", "glad to", "pleased to", "my friend"))
                return "Friendly";
            if (ContainsAny(text, "excellent", "wonderful", "splendid", "marvelous", "brilliant") || CountCharacter(text, '!') >= 2)
                return "Excited";
            if (ContainsAny(text, "fool", "idiot", "damn", "curse you", "how dare", "disgrace", "hate"))
                return "Angry";

            if (voice != null && !string.IsNullOrWhiteSpace(voice.DefaultEmotion) &&
                !string.Equals(voice.DefaultEmotion, "Neutral", StringComparison.OrdinalIgnoreCase))
                return voice.DefaultEmotion;

            if (identity != null && (string.Equals(identity.Role, "Noble", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(identity.Role, "Scholar", StringComparison.OrdinalIgnoreCase)))
                return "Formal";

            return "Neutral";
        }

        private string GetDynamicPortraitEmotion()
        {
            object dp = GetDynamicPortraitsInstance();
            if (dp == null)
                return string.Empty;

            try
            {
                if (dynamicPortraitsReactionStateField != null)
                {
                    object state = dynamicPortraitsReactionStateField.GetValue(dp);
                    string reaction = state == null ? string.Empty : state.ToString();
                    if (string.Equals(reaction, "Win", StringComparison.OrdinalIgnoreCase)) return "Friendly";
                    if (string.Equals(reaction, "Los", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(reaction, "Loss", StringComparison.OrdinalIgnoreCase)) return "Angry";
                }

                if (dynamicPortraitsMoodMethod != null)
                {
                    object raw = dynamicPortraitsMoodMethod.Invoke(dp, null);
                    string mood = raw == null ? string.Empty : raw.ToString();
                    if (mood.IndexOf("_Hap", StringComparison.OrdinalIgnoreCase) >= 0) return "Friendly";
                    if (mood.IndexOf("_Ang", StringComparison.OrdinalIgnoreCase) >= 0) return "Angry";
                }
            }
            catch { }
            return string.Empty;
        }

        private ResolvedVoice ApplyEmotionToVoice(ResolvedVoice source, string emotion)
        {
            if (source == null)
                return null;

            ResolvedVoice result = CloneResolvedVoice(source);
            result.Emotion = string.IsNullOrWhiteSpace(emotion) ? "Neutral" : emotion;
            if (config == null || !config.EmotionLayer || string.Equals(result.Emotion, "Neutral", StringComparison.OrdinalIgnoreCase))
                return result;

            float strength = config.EmotionStrength == EmotionProcessingStrength.Subtle ? 0.55f :
                (config.EmotionStrength == EmotionProcessingStrength.Strong ? 1.30f : 1f);
            string e = result.Emotion.ToLowerInvariant();

            if (e == "friendly")
            {
                result.Speed *= 1f + 0.025f * strength;
                result.PitchSemitones += 0.15f * strength;
                result.Presence += 0.035f * strength;
            }
            else if (e == "formal")
            {
                result.Speed *= 1f - 0.025f * strength;
                result.Compression += 0.035f * strength;
            }
            else if (e == "angry")
            {
                result.Speed *= 1f + 0.055f * strength;
                result.PitchSemitones -= 0.25f * strength;
                result.Gravel += 0.10f * strength;
                result.Saturation += 0.07f * strength;
                result.Presence += 0.10f * strength;
                result.Compression += 0.13f * strength;
            }
            else if (e == "afraid")
            {
                result.Speed *= 1f + 0.085f * strength;
                result.PitchSemitones += 0.45f * strength;
                result.Presence += 0.08f * strength;
                result.Gravel *= Mathf.Max(0.4f, 1f - 0.25f * strength);
            }
            else if (e == "sad")
            {
                result.Speed *= 1f - 0.09f * strength;
                result.PitchSemitones -= 0.35f * strength;
                result.Presence *= Mathf.Max(0.5f, 1f - 0.18f * strength);
            }
            else if (e == "excited")
            {
                result.Speed *= 1f + 0.075f * strength;
                result.PitchSemitones += 0.35f * strength;
                result.Presence += 0.10f * strength;
                result.Compression += 0.05f * strength;
            }
            else if (e == "threatening")
            {
                result.Speed *= 1f - 0.055f * strength;
                result.PitchSemitones -= 0.55f * strength;
                result.Gravel += 0.11f * strength;
                result.Saturation += 0.08f * strength;
                result.Compression += 0.12f * strength;
            }
            else if (e == "injured")
            {
                result.Speed *= 1f - 0.08f * strength;
                result.PitchSemitones -= 0.15f * strength;
                result.Gravel += 0.08f * strength;
                result.Compression += 0.05f * strength;
            }

            // Creature physiology follows emotion as a second layer rather than replacing the base voice.
            if (result.PurrMix > 0.001f || result.FelineResonance > 0.001f)
            {
                if (e == "friendly") result.PurrMix += 0.07f * strength;
                else if (e == "angry" || e == "threatening") { result.PurrMix *= 0.55f; result.Growl += 0.13f * strength; }
                else if (e == "afraid") { result.PurrMix *= 0.20f; result.Breath += 0.12f * strength; }
                else if (e == "sad") result.PurrMix += 0.025f * strength;
            }
            if (result.SubharmonicMix > 0.001f || result.ThroatResonance > 0.001f)
            {
                if (e == "angry" || e == "threatening") { result.Hiss += 0.08f * strength; result.Croak += 0.08f * strength; }
                else if (e == "afraid") { result.Hiss += 0.06f * strength; result.FlutterDepth += 0.025f * strength; }
            }

            result.Speed = Mathf.Clamp(result.Speed, 0.65f, 1.45f);
            result.PitchSemitones = Mathf.Clamp(result.PitchSemitones, -6f, 4f);
            result.Gravel = Mathf.Clamp01(result.Gravel);
            result.Saturation = Mathf.Clamp01(result.Saturation);
            result.Presence = Mathf.Clamp01(result.Presence);
            result.Compression = Mathf.Clamp01(result.Compression);
            result.DoubleMix = Mathf.Clamp01(result.DoubleMix);
            result.Spectral = Mathf.Clamp01(result.Spectral);
            result.Reverb = Mathf.Clamp01(result.Reverb);
            result.SubharmonicMix = Mathf.Clamp01(result.SubharmonicMix);
            result.SubharmonicPitch = Mathf.Clamp(result.SubharmonicPitch, -12f, -1f);
            result.Hiss = Mathf.Clamp01(result.Hiss);
            result.ThroatResonance = Mathf.Clamp01(result.ThroatResonance);
            result.FlutterDepth = Mathf.Clamp(result.FlutterDepth, 0f, 0.5f);
            result.FlutterRate = Mathf.Clamp(result.FlutterRate, 0f, 12f);
            result.Croak = Mathf.Clamp01(result.Croak);
            result.PurrMix = Mathf.Clamp01(result.PurrMix);
            result.PurrRate = Mathf.Clamp(result.PurrRate, 0f, 70f);
            result.FelineResonance = Mathf.Clamp01(result.FelineResonance);
            result.Growl = Mathf.Clamp01(result.Growl);
            result.Breath = Mathf.Clamp01(result.Breath);
            return result;
        }

        private static ResolvedVoice CloneResolvedVoice(ResolvedVoice source)
        {
            if (source == null) return null;
            ResolvedVoice clone = new ResolvedVoice();
            clone.VoiceId = source.VoiceId;
            clone.Speed = source.Speed;
            clone.PitchSemitones = source.PitchSemitones;
            clone.Gravel = source.Gravel;
            clone.Saturation = source.Saturation;
            clone.Presence = source.Presence;
            clone.Compression = source.Compression;
            clone.DoubleMix = source.DoubleMix;
            clone.DoublePitchSemitones = source.DoublePitchSemitones;
            clone.DoubleDelayMs = source.DoubleDelayMs;
            clone.Spectral = source.Spectral;
            clone.Reverb = source.Reverb;
            clone.SubharmonicMix = source.SubharmonicMix;
            clone.SubharmonicPitch = source.SubharmonicPitch;
            clone.Hiss = source.Hiss;
            clone.ThroatResonance = source.ThroatResonance;
            clone.FlutterDepth = source.FlutterDepth;
            clone.FlutterRate = source.FlutterRate;
            clone.Croak = source.Croak;
            clone.PurrMix = source.PurrMix;
            clone.PurrRate = source.PurrRate;
            clone.FelineResonance = source.FelineResonance;
            clone.Growl = source.Growl;
            clone.Breath = source.Breath;
            clone.DefaultEmotion = source.DefaultEmotion;
            clone.Emotion = source.Emotion;
            clone.ProfileKey = source.ProfileKey;
            clone.Source = source.Source;
            return clone;
        }

        private static bool ContainsAny(string text, params string[] terms)
        {
            if (string.IsNullOrEmpty(text) || terms == null) return false;
            for (int i = 0; i < terms.Length; i++)
                if (!string.IsNullOrEmpty(terms[i]) && text.IndexOf(terms[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        private static int CountCharacter(string text, char c)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int count = 0;
            for (int i = 0; i < text.Length; i++) if (text[i] == c) count++;
            return count;
        }

        private static bool IsBeastRace(string race)
        {
            if (string.IsNullOrEmpty(race)) return false;
            return string.Equals(race, "Orc", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(race, "Khajiit", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(race, "Argonian", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(race, "Dragon", StringComparison.OrdinalIgnoreCase);
        }

        private static VoiceProfile FindProfile(VoiceProfileCollection collection, string key)
        {
            if (collection == null || collection.profiles == null || string.IsNullOrWhiteSpace(key))
                return null;
            foreach (VoiceProfile profile in collection.profiles)
            {
                if (profile != null && profile.enabled && string.Equals(profile.key, key, StringComparison.OrdinalIgnoreCase))
                    return profile;
            }
            return null;
        }

        private string FindVoicePackWav(NPCIdentity identity, string line)
        {
            string script = NormalizeVoicePackScript(line);
            string hash = Sha1(script);

            string specialSlug = GetSpecialCharacterSlug(identity == null ? string.Empty : identity.Name);
            if (!string.IsNullOrEmpty(specialSlug))
            {
                string persistentSpecial = Path.Combine(voicePackDir, "Characters", specialSlug, "Lines", hash + ".wav");
                if (File.Exists(persistentSpecial)) return persistentSpecial;
                string packagedSpecial = Path.Combine(packagedVoicePackDir, "Characters", specialSlug, "Lines", hash + ".wav");
                if (File.Exists(packagedSpecial)) return packagedSpecial;
            }

            string[] folderKeys = new string[] {
                SanitizePathComponent(identity.Name),
                string.IsNullOrEmpty(identity.PortraitKey) ? string.Empty : SanitizePathComponent(identity.PortraitKey)
            };

            foreach (string folder in folderKeys)
            {
                if (string.IsNullOrEmpty(folder))
                    continue;
                string persistent = Path.Combine(voicePackDir, folder, hash + ".wav");
                if (File.Exists(persistent))
                    return persistent;
                string packaged = Path.Combine(packagedVoicePackDir, folder, hash + ".wav");
                if (File.Exists(packaged))
                    return packaged;
            }
            return null;
        }

        private string NormalizeVoicePackScript(string line)
        {
            string result = NormalizeText(line);
            if (config != null && config.IgnorePlayerNameInVoicePackKeys)
            {
                string playerName = GetPlayerName();
                if (!string.IsNullOrWhiteSpace(playerName))
                {
                    result = Regex.Replace(result, @"(?i)(^|[\s,])" + Regex.Escape(playerName) + @"(?=\s|[,!.?;:]|$)", "$1");
                    result = Regex.Replace(result, @"\s+,", ",");
                    result = Regex.Replace(result, @",\s*([.!?])", "$1");
                    result = Regex.Replace(result, @"\s{2,}", " ").Trim(' ', ',');
                }
            }
            return NormalizeForMatch(result);
        }

        private static string GetPlayerName()
        {
            try
            {
                object player = GameManager.Instance == null ? null : (object)GameManager.Instance.PlayerEntity;
                object name = ReadMember(player, "Name");
                return name == null ? string.Empty : name.ToString();
            }
            catch { return string.Empty; }
        }

        private string GetDynamicPortraitKey()
        {
            if (config == null || !config.DynamicPortraitsIntegration)
                return string.Empty;
            EnsureDynamicPortraitsReflection();
            object dp = GetDynamicPortraitsInstance();
            if (dp == null)
                return string.Empty;
            try
            {
                int recordId = Convert.ToInt32(dynamicPortraitsRecordField.GetValue(dp), CultureInfo.InvariantCulture);
                string cif = null;
                if (dynamicPortraitsDetermineCifMethod != null)
                    cif = dynamicPortraitsDetermineCifMethod.Invoke(dp, new object[] { recordId }) as string;

                if (string.IsNullOrEmpty(cif) && dynamicPortraitsArchiveField != null)
                {
                    object archive = dynamicPortraitsArchiveField.GetValue(dp);
                    string archiveName = archive == null ? string.Empty : archive.ToString();
                    cif = archiveName.IndexOf("Common", StringComparison.OrdinalIgnoreCase) >= 0 ? "TFAC00I0.RCI" : "FACES.CIF";
                }
                if (!string.IsNullOrEmpty(cif))
                    return cif + "_" + recordId.ToString(CultureInfo.InvariantCulture);
            }
            catch { }
            return string.Empty;
        }

        private string GetFallbackPortraitKey(TalkManager talk)
        {
            try
            {
                if (talk != null && talk.CurrentNPCType == TalkManager.NPCType.Mobile && talk.MobileNPC != null)
                {
                    object id = ReadMember(talk.MobileNPC, "PersonFaceRecordId");
                    if (id != null)
                        return "TFAC00I0.RCI_" + Convert.ToInt32(id).ToString(CultureInfo.InvariantCulture);
                }
            }
            catch { }
            return string.Empty;
        }

        private void EnsureDynamicPortraitsReflection()
        {
            if (dynamicPortraitsReflectionChecked)
                return;
            dynamicPortraitsReflectionChecked = true;

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = null;
                try { type = assembly.GetType(DynamicPortraitsTypeName, false); } catch { }
                if (type == null)
                    continue;

                dynamicPortraitsType = type;
                dynamicPortraitsInstanceProperty = type.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                dynamicPortraitsArchiveField = type.GetField("currentArchive", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                dynamicPortraitsRecordField = type.GetField("currentRecordId", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                dynamicPortraitsAudioSourceField = type.GetField("audioSource", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                dynamicPortraitsReactionStateField = type.GetField("currentReactionState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                dynamicPortraitsDetermineCifMethod = type.GetMethod("DetermineCif", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                dynamicPortraitsMoodMethod = type.GetMethod("GetReputationMoodTag", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                Debug.Log("[NPCVO] Dynamic Portraits detected; portrait-key and emotion pairing enabled.");
                break;
            }
        }

        private object GetDynamicPortraitsInstance()
        {
            EnsureDynamicPortraitsReflection();
            if (dynamicPortraitsType == null || dynamicPortraitsInstanceProperty == null)
                return null;
            try { return dynamicPortraitsInstanceProperty.GetValue(null, null); }
            catch { return null; }
        }

        private AudioSource GetDynamicPortraitsAudioSource()
        {
            object dp = GetDynamicPortraitsInstance();
            if (dp == null || dynamicPortraitsAudioSourceField == null)
                return null;
            try { return dynamicPortraitsAudioSourceField.GetValue(dp) as AudioSource; }
            catch { return null; }
        }

        private void LoadVoiceAssignments()
        {
            voiceAssignmentsByKey.Clear();
            voiceAssignments = new VoiceAssignmentCollection();
            try
            {
                if (File.Exists(voiceAssignmentsPath))
                {
                    string json = File.ReadAllText(voiceAssignmentsPath);
                    VoiceAssignmentCollection parsed = JsonUtility.FromJson<VoiceAssignmentCollection>(json);
                    if (parsed != null)
                        voiceAssignments = parsed;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[NPCVO] Could not read VoiceAssignments.json; starting with a fresh assignment map. " + ex.Message);
                try
                {
                    if (File.Exists(voiceAssignmentsPath))
                    {
                        string backup = voiceAssignmentsPath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".bak";
                        File.Copy(voiceAssignmentsPath, backup, true);
                    }
                }
                catch { }
                voiceAssignments = new VoiceAssignmentCollection();
            }

            if (voiceAssignments.assignments == null)
                voiceAssignments.assignments = new VoiceAssignment[0];

            for (int i = 0; i < voiceAssignments.assignments.Length; i++)
            {
                VoiceAssignment a = voiceAssignments.assignments[i];
                if (a == null || string.IsNullOrWhiteSpace(a.identityKey))
                    continue;
                voiceAssignmentsByKey[a.identityKey] = a;
            }
            lastAssignmentStatus = voiceAssignmentsByKey.Count + " persistent NPC voice assignment(s) loaded";
        }

        private void SaveVoiceAssignments()
        {
            if (string.IsNullOrWhiteSpace(voiceAssignmentsPath))
                return;
            try
            {
                List<VoiceAssignment> ordered = new List<VoiceAssignment>(voiceAssignmentsByKey.Values);
                ordered.Sort(delegate(VoiceAssignment a, VoiceAssignment b)
                {
                    return string.Compare(a == null ? string.Empty : a.identityKey,
                        b == null ? string.Empty : b.identityKey, StringComparison.OrdinalIgnoreCase);
                });
                voiceAssignments = new VoiceAssignmentCollection();
                voiceAssignments.schemaVersion = 1;
                voiceAssignments.assignments = ordered.ToArray();
                File.WriteAllText(voiceAssignmentsPath, JsonUtility.ToJson(voiceAssignments, true));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[NPCVO] Could not save persistent voice assignments: " + ex.Message);
            }
        }

        private void LoadAllConfiguration()
        {
            config = NPCVOConfig.LoadOrCreate(iniPath);
            voiceTypes = LoadOrCreateProfiles(voiceTypesPath, CreateDefaultVoiceTypes());
            uniqueVoices = LoadOrCreateProfiles(uniqueVoicesPath, new VoiceProfileCollection());
            specialVoices = LoadOrCreateProfiles(specialVoicesPath, CreateDefaultSpecialVoices());
            portraitVoices = LoadOrCreateProfiles(portraitVoicesPath, new VoiceProfileCollection());
            LoadVoiceAssignments();

            if (mod != null && mod.HasSettings)
            {
                try { ApplyNativeSettings(mod.GetSettings()); }
                catch (Exception ex) { Debug.LogWarning("[NPCVO] Could not load native settings: " + ex.Message); }
            }
            ApplyVolume();
        }

        private void NativeSettingsChanged(ModSettings settings, ModSettingsChange change)
        {
            if (settings == null || config == null)
                return;
            bool oldQuestPortraitUI = config.QuestGiverPortraitUI;
            bool oldAskUi = config.PlayerAskConversationUI;
            ApplyNativeSettings(settings);
            ApplyVolume();
            TryEnsureEnglishVoiceLibrary();

            if (oldAskUi != config.PlayerAskConversationUI && !config.PlayerAskConversationUI)
                DestroyTalkConversationPresentation();

            if (oldQuestPortraitUI != config.QuestGiverPortraitUI)
            {
                if (!config.QuestGiverPortraitUI)
                    DestroyQuestOfferPresentation();
                lastQuestOfferText = string.Empty;
                pendingQuestOfferText = string.Empty;
                pendingQuestOfferStablePolls = 0;
            }
        }

        private void ApplyNativeSettings(ModSettings settings)
        {
            config.Enabled = settings.GetValue<bool>("General", "Enabled");
            config.InterruptOnNewResponse = settings.GetValue<bool>("General", "InterruptOnNewResponse");
            config.StopWhenTalkWindowCloses = settings.GetValue<bool>("General", "StopWhenTalkWindowCloses");
            config.RevealNpcTextWithSpeech = settings.GetValue<bool>("General", "RevealNpcTextWithSpeech");
            config.NarrateQuestGiverWindows = settings.GetValue<bool>("General", "NarrateQuestGiverWindows");
            config.NarrateCourtJudge = settings.GetValue<bool>("General", "NarrateCourtJudge");
            config.QuestGiverPortraitUI = settings.GetValue<bool>("General", "QuestGiverPortraitUI");
            config.PlayerAskConversationUI = settings.GetValue<bool>("Portraits & Integration", "PlayerAskConversationUI");
            config.Variation = (VariationMode)Mathf.Clamp(settings.GetValue<int>("NPC Voices", "Variation"), 0, 2);
            config.SpeechSource = (SpeechSourceMode)Mathf.Clamp(settings.GetValue<int>("NPC Voices", "SpeechSource"), 0, 2);
            config.UseRoleVoiceTypes = settings.GetValue<bool>("NPC Voices", "UseRoleVoiceTypes");
            config.FantasyProcessing = (FantasyProcessingStrength)Mathf.Clamp(settings.GetValue<int>("NPC Voices", "FantasyProcessing"), 0, 2);
            config.IgnorePlayerNameInVoicePackKeys = settings.GetValue<bool>("NPC Voices", "IgnorePlayerNameInVoicePackKeys");
            string voiceOverride = settings.GetValue<string>("NPC Voices", "VoiceOverride");
            config.VoiceOverride = string.IsNullOrWhiteSpace(voiceOverride) ? string.Empty : voiceOverride.Trim();
            config.EmotionLayer = settings.GetValue<bool>("Speech Style", "EmotionLayer");
            config.EmotionStrength = (EmotionProcessingStrength)Mathf.Clamp(settings.GetValue<int>("Speech Style", "EmotionStrength"), 0, 2);
            config.GrammarPolish = settings.GetValue<bool>("Speech Style", "GrammarPolish");
            config.DisplayPolishedText = settings.GetValue<bool>("Speech Style", "DisplayPolishedText");
            config.EnsureEnglishVoiceLibrary = settings.GetValue<bool>("Advanced", "EnsureEnglishVoiceLibrary");
            config.AutoStartVoiceEngine = settings.GetValue<bool>("Advanced", "AutoStartVoiceEngine");
            config.DynamicPortraitsIntegration = settings.GetValue<bool>("Portraits & Integration", "Integration");
            config.DynamicPortraitEmotionSync = settings.GetValue<bool>("Portraits & Integration", "EmotionSync");
            config.PortraitAudioBehavior = (PortraitAudioBehavior)Mathf.Clamp(settings.GetValue<int>("Portraits & Integration", "PortraitAudioBehavior"), 0, 2);
            config.Volume = Mathf.Clamp(settings.GetValue<int>("Audio", "Volume"), 0, 100);
            config.UseGameSoundVolume = settings.GetValue<bool>("Audio", "UseGameSoundVolume");
            int style = Mathf.Clamp(settings.GetValue<int>("Audio", "AudioStyle"), 0, 2);
            config.AudioStyle = style == 1 ? "cdrom" : (style == 2 ? "dos" : "clean");
            int cache = settings.GetValue<int>("Advanced", "MaxCacheSize");
            switch (cache)
            {
                case 0: config.MaxCacheSizeMB = 250; break;
                case 1: config.MaxCacheSizeMB = 500; break;
                case 3: config.MaxCacheSizeMB = 2048; break;
                case 4: config.MaxCacheSizeMB = 5120; break;
                case 5: config.MaxCacheSizeMB = 0; break;
                default: config.MaxCacheSizeMB = 1024; break;
            }
        }

        private VoiceProfileCollection LoadOrCreateProfiles(string path, VoiceProfileCollection defaults)
        {
            if (defaults == null)
                defaults = new VoiceProfileCollection();
            if (defaults.profiles == null)
                defaults.profiles = new VoiceProfile[0];

            bool hasRequiredDefaults = defaults.profiles.Length > 0;
            try
            {
                if (!File.Exists(path))
                {
                    WriteProfileCollection(path, defaults);
                    Debug.Log("[NPCVO] Created profile file " + path + " with " + defaults.profiles.Length + " profile(s).");
                    return CloneProfileCollection(defaults);
                }

                string json = File.ReadAllText(path);
                string trimmed = string.IsNullOrWhiteSpace(json) ? string.Empty : json.Trim();

                // Older/broken NPCVO builds could leave VoiceTypes.json as [] or an otherwise
                // empty JSON object. Treat that as corrupt when this file has built-in defaults.
                if (hasRequiredDefaults && (trimmed.Length == 0 || trimmed == "[]" || trimmed == "{}"))
                {
                    VoiceProfileCollection repaired = CloneProfileCollection(defaults);
                    WriteProfileCollection(path, repaired);
                    Debug.LogWarning("[NPCVO] Repaired empty profile file " + path + " with " + repaired.profiles.Length + " built-in profile(s).");
                    return repaired;
                }

                VoiceProfileCollection parsed = null;
                try { parsed = JsonUtility.FromJson<VoiceProfileCollection>(json); }
                catch { parsed = null; }

                if (parsed == null)
                    parsed = new VoiceProfileCollection();
                if (parsed.profiles == null)
                    parsed.profiles = new VoiceProfile[0];

                if (hasRequiredDefaults)
                {
                    bool schemaUpgrade = defaults.schemaVersion > 0 && parsed.schemaVersion < defaults.schemaVersion;
                    bool changed;
                    if (schemaUpgrade)
                    {
                        try
                        {
                            string backup = path + ".pre-v" + defaults.schemaVersion + ".bak";
                            if (!File.Exists(backup)) File.Copy(path, backup);
                        }
                        catch { }
                        changed = UpgradeProfilesToSchema(parsed, defaults);
                    }
                    else
                    {
                        changed = MergeMissingProfiles(parsed, defaults);
                    }
                    if (parsed.profiles.Length == 0)
                    {
                        parsed = CloneProfileCollection(defaults);
                        changed = true;
                    }
                    if (changed)
                    {
                        WriteProfileCollection(path, parsed);
                        Debug.LogWarning("[NPCVO] Repaired/merged profile file " + path + "; now " + parsed.profiles.Length + " profile(s)." + (schemaUpgrade ? " Schema upgraded to v" + defaults.schemaVersion + "." : string.Empty));
                    }
                }
                else if (trimmed == "[]" || trimmed.Length == 0)
                {
                    // Normalize optional empty files to the wrapper shape JsonUtility expects.
                    WriteProfileCollection(path, parsed);
                }

                return parsed;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[NPCVO] Could not load " + path + ": " + ex.Message);
                VoiceProfileCollection fallback = CloneProfileCollection(defaults);
                try { WriteProfileCollection(path, fallback); } catch { }
                return fallback;
            }
        }

        private static bool UpgradeProfilesToSchema(VoiceProfileCollection target, VoiceProfileCollection defaults)
        {
            if (target == null || defaults == null) return false;
            Dictionary<string, VoiceProfile> builtIns = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
            if (defaults.profiles != null)
            {
                foreach (VoiceProfile p in defaults.profiles)
                    if (p != null && !string.IsNullOrWhiteSpace(p.key)) builtIns[p.key.Trim()] = p;
            }

            List<VoiceProfile> merged = new List<VoiceProfile>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (target.profiles != null)
            {
                foreach (VoiceProfile p in target.profiles)
                {
                    if (p == null || string.IsNullOrWhiteSpace(p.key)) continue;
                    string key = p.key.Trim();
                    VoiceProfile builtIn;
                    if (builtIns.TryGetValue(key, out builtIn)) merged.Add(CloneProfile(builtIn));
                    else merged.Add(p);
                    seen.Add(key);
                }
            }
            foreach (KeyValuePair<string, VoiceProfile> kv in builtIns)
                if (!seen.Contains(kv.Key)) merged.Add(CloneProfile(kv.Value));

            target.schemaVersion = defaults.schemaVersion;
            target.profiles = merged.ToArray();
            return true;
        }

        private static bool MergeMissingProfiles(VoiceProfileCollection target, VoiceProfileCollection defaults)
        {
            List<VoiceProfile> merged = new List<VoiceProfile>();
            HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (target != null && target.profiles != null)
            {
                foreach (VoiceProfile p in target.profiles)
                {
                    if (p == null || string.IsNullOrWhiteSpace(p.key))
                        continue;
                    merged.Add(p);
                    keys.Add(p.key.Trim());
                }
            }

            bool changed = target == null || target.profiles == null || merged.Count != target.profiles.Length;
            if (defaults != null && defaults.profiles != null)
            {
                foreach (VoiceProfile p in defaults.profiles)
                {
                    if (p == null || string.IsNullOrWhiteSpace(p.key) || keys.Contains(p.key.Trim()))
                        continue;
                    merged.Add(CloneProfile(p));
                    keys.Add(p.key.Trim());
                    changed = true;
                }
            }

            target.profiles = merged.ToArray();
            return changed;
        }

        private static VoiceProfileCollection CloneProfileCollection(VoiceProfileCollection source)
        {
            VoiceProfileCollection clone = new VoiceProfileCollection();
            clone.schemaVersion = source == null ? 0 : source.schemaVersion;
            if (source == null || source.profiles == null)
            {
                clone.profiles = new VoiceProfile[0];
                return clone;
            }
            clone.profiles = new VoiceProfile[source.profiles.Length];
            for (int i = 0; i < source.profiles.Length; i++)
                clone.profiles[i] = CloneProfile(source.profiles[i]);
            return clone;
        }

        private static VoiceProfile CloneProfile(VoiceProfile source)
        {
            if (source == null)
                return null;
            VoiceProfile clone = new VoiceProfile();
            clone.key = source.key ?? string.Empty;
            clone.voices = source.voices == null ? new string[0] : (string[])source.voices.Clone();
            clone.speed = source.speed;
            clone.pitch = source.pitch;
            clone.gravel = source.gravel;
            clone.saturation = source.saturation;
            clone.presence = source.presence;
            clone.compression = source.compression;
            clone.doubleMix = source.doubleMix;
            clone.doublePitch = source.doublePitch;
            clone.doubleDelayMs = source.doubleDelayMs;
            clone.spectral = source.spectral;
            clone.reverb = source.reverb;
            clone.subharmonicMix = source.subharmonicMix;
            clone.subharmonicPitch = source.subharmonicPitch;
            clone.hiss = source.hiss;
            clone.throatResonance = source.throatResonance;
            clone.flutterDepth = source.flutterDepth;
            clone.flutterRate = source.flutterRate;
            clone.croak = source.croak;
            clone.purrMix = source.purrMix;
            clone.purrRate = source.purrRate;
            clone.felineResonance = source.felineResonance;
            clone.growl = source.growl;
            clone.breath = source.breath;
            clone.defaultEmotion = source.defaultEmotion;
            clone.lockVariation = source.lockVariation;
            clone.enabled = source.enabled;
            return clone;
        }

        private static void WriteProfileCollection(string path, VoiceProfileCollection collection)
        {
            // Do not rely on Unity JsonUtility for initial serialization here. A previous build
            // produced a top-level [] on some setups, which permanently disabled voice resolution.
            // Write the tiny schema deterministically, then continue to use JsonUtility for reads.
            if (collection == null)
                collection = new VoiceProfileCollection();
            if (collection.profiles == null)
                collection.profiles = new VoiceProfile[0];

            StringBuilder sb = new StringBuilder();
            sb.Append("{\n  \"schemaVersion\": ").Append(collection.schemaVersion).Append(",\n  \"profiles\": [");
            for (int i = 0; i < collection.profiles.Length; i++)
            {
                VoiceProfile p = collection.profiles[i];
                if (p == null)
                    continue;
                sb.Append(i == 0 ? "\n" : ",\n");
                sb.Append("    {\n");
                sb.Append("      \"key\": ").Append(JsonString(p.key)).Append(",\n");
                sb.Append("      \"voices\": [");
                string[] voices = p.voices ?? new string[0];
                for (int v = 0; v < voices.Length; v++)
                {
                    if (v > 0) sb.Append(", ");
                    sb.Append(JsonString(voices[v]));
                }
                sb.Append("],\n");
                sb.Append("      \"speed\": ").Append(p.speed.ToString("0.###", CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("      \"pitch\": ").Append(p.pitch.ToString("0.###", CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("      \"gravel\": ").Append(p.gravel.ToString("0.###", CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("      \"saturation\": ").Append(p.saturation.ToString("0.###", CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("      \"presence\": ").Append(p.presence.ToString("0.###", CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("      \"compression\": ").Append(p.compression.ToString("0.###", CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("      \"doubleMix\": ").Append(p.doubleMix.ToString("0.###", CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("      \"doublePitch\": ").Append(p.doublePitch.ToString("0.###", CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("      \"doubleDelayMs\": ").Append(p.doubleDelayMs.ToString("0.###", CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("      \"spectral\": ").Append(p.spectral.ToString("0.###", CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("      \"reverb\": ").Append(p.reverb.ToString("0.###", CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("      \"subharmonicMix\": ").Append(p.subharmonicMix.ToString("0.###", CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("      \"subharmonicPitch\": ").Append(p.subharmonicPitch.ToString("0.###", CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("      \"hiss\": ").Append(p.hiss.ToString("0.###", CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("      \"throatResonance\": ").Append(p.throatResonance.ToString("0.###", CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("      \"flutterDepth\": ").Append(p.flutterDepth.ToString("0.###", CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("      \"flutterRate\": ").Append(p.flutterRate.ToString("0.###", CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("      \"croak\": ").Append(p.croak.ToString("0.###", CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("      \"purrMix\": ").Append(p.purrMix.ToString("0.###", CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("      \"purrRate\": ").Append(p.purrRate.ToString("0.###", CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("      \"felineResonance\": ").Append(p.felineResonance.ToString("0.###", CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("      \"growl\": ").Append(p.growl.ToString("0.###", CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("      \"breath\": ").Append(p.breath.ToString("0.###", CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("      \"defaultEmotion\": ").Append(JsonString(p.defaultEmotion)).Append(",\n");
                sb.Append("      \"lockVariation\": ").Append(p.lockVariation ? "true" : "false").Append(",\n");
                sb.Append("      \"enabled\": ").Append(p.enabled ? "true" : "false").Append("\n");
                sb.Append("    }");
            }
            if (collection.profiles.Length > 0) sb.Append("\n  ");
            sb.Append("]\n}\n");
            File.WriteAllText(path, sb.ToString());
        }

        private static string JsonString(string value)
        {
            if (value == null) value = string.Empty;
            StringBuilder sb = new StringBuilder(value.Length + 2);
            sb.Append('\"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '\"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('\"');
            return sb.ToString();
        }

        private static readonly string[] UsFemaleVoices = new string[] {
            "af_alloy", "af_aoede", "af_bella", "af_heart", "af_jessica", "af_kore",
            "af_nicole", "af_nova", "af_river", "af_sarah", "af_sky"
        };
        private static readonly string[] UsMaleVoices = new string[] {
            "am_adam", "am_echo", "am_eric", "am_fenrir", "am_liam", "am_michael",
            "am_onyx", "am_puck", "am_santa"
        };
        private static readonly string[] UkFemaleVoices = new string[] {
            "bf_alice", "bf_emma", "bf_isabella", "bf_lily"
        };
        private static readonly string[] UkMaleVoices = new string[] {
            "bm_daniel", "bm_fable", "bm_george", "bm_lewis"
        };
        // Curated low-register pools keep the archetype in the source timbre instead of relying
        // entirely on post-pitching a brighter voice. Social accent weighting is retained inside
        // these smaller pools.
        private static readonly string[] OrcUsMaleVoices = new string[] { "am_onyx", "am_michael", "am_fenrir" };
        private static readonly string[] OrcUkMaleVoices = new string[] { "bm_george", "bm_daniel" };
        // Nord men use the CC0 community am_granite voice as the heroic anchor. Kokoro permits
        // comma-separated voice blending; repeating Granite weights the average toward it while
        // a British component adds just enough old-world color without changing English G2P.
        private static readonly string[] NordHeroicUsMaleVoices = new string[] {
            "am_granite,am_granite,bm_george",
            "am_granite,am_granite,bm_daniel",
            "am_granite,am_granite,bm_fable"
        };
        private static readonly string[] NordHeroicUkMaleVoices = new string[] {
            "am_granite,bm_george,bm_george",
            "am_granite,bm_daniel,bm_george",
            "am_granite,bm_fable,bm_george"
        };
        // Granite is useful well beyond Nords. These small archetype pools let it occasionally
        // enter the wider male ecosystem without overwhelming race identity. Lower/cleaner blends
        // read epic, UK-heavy blends read arcane/old-world, and raw/lighter blends make excellent
        // oily schemers when paired with a slightly brighter profile.
        private static readonly string[] GraniteEpicMaleVoices = new string[] {
            "am_granite",
            "am_granite,am_granite,am_fenrir",
            "am_granite,am_granite,am_onyx"
        };
        private static readonly string[] GraniteWizardMaleVoices = new string[] {
            "am_granite,bm_george,bm_george",
            "am_granite,bm_daniel,bm_george",
            "am_granite,bm_fable,bm_george"
        };
        private static readonly string[] GraniteSchemerMaleVoices = new string[] {
            "am_granite",
            "am_granite,bm_fable",
            "am_granite,am_puck"
        };
        private static readonly string[] DunmerUsMaleVoices = new string[] { "am_onyx", "am_fenrir", "am_michael" };
        private static readonly string[] DunmerUkMaleVoices = new string[] { "bm_george", "bm_daniel" };
        private static readonly string[] DunmerUsFemaleVoices = new string[] { "af_nicole", "af_bella", "af_heart" };
        private static readonly string[] DunmerUkFemaleVoices = new string[] { "bf_emma", "bf_isabella" };
        private static readonly string[] ProceduralRoles = new string[] {
            "Commoner", "Merchant", "Scholar", "Noble", "Judge", "Underworld", "Supernatural", "Guild", "Child"
        };
        private static readonly string[] ProceduralRaces = new string[] {
            "Breton", "Redguard", "Nord", "DarkElf", "HighElf", "WoodElf",
            "Khajiit", "Argonian", "Orc", "Dragon"
        };

        private static readonly Dictionary<string, string> SpecialCharacterSlugs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Lady Brisienna", "lady-brisienna" }, { "Brisienna", "lady-brisienna" },
            { "King Lysandus", "king-lysandus" }, { "Lysandus", "king-lysandus" },
            { "Queen Mynisera", "queen-mynisera" }, { "Mynisera", "queen-mynisera" },
            { "King Gothryd", "king-gothryd" }, { "Gothryd", "king-gothryd" },
            { "Queen Aubk-i", "queen-aubk-i" }, { "Aubk-i", "queen-aubk-i" },
            { "Medora Direnni", "medora-direnni" }, { "Medora", "medora-direnni" },
            { "Nulfaga", "nulfaga" },
            { "Gortwog", "gortwog" }, { "Gortwog gro-Nagorm", "gortwog" },
            { "Queen Akorithi", "queen-akorithi" }, { "Akorithi", "queen-akorithi" },
            { "Prince Lhotun", "prince-lhotun" }, { "Lhotun", "prince-lhotun" },
            { "King Eadwyre", "king-eadwyre" }, { "Eadwyre", "king-eadwyre" },
            { "Queen Barenziah", "queen-barenziah" }, { "Barenziah", "queen-barenziah" },
            { "Prince Helseth", "prince-helseth" }, { "Helseth", "prince-helseth" },
            { "Princess Morgiah", "princess-morgiah" }, { "Morgiah", "princess-morgiah" },
            { "Princess Elysana", "princess-elysana" }, { "Elysana", "princess-elysana" },
            { "Lord Woodborne", "lord-woodborne" }, { "Woodborne", "lord-woodborne" },
            { "The Underking", "the-underking" }, { "Underking", "the-underking" },
            { "The King of Worms", "king-of-worms" }, { "King of Worms", "king-of-worms" }, { "Mannimarco", "king-of-worms" },
            { "Emperor Uriel Septim VII", "uriel-septim-vii" }, { "Uriel Septim VII", "uriel-septim-vii" },
            { "Prince Greklith", "prince-greklith" }, { "Greklith", "prince-greklith" },
            { "Azura", "daedra/azura" }, { "Boethiah", "daedra/boethiah" }, { "Clavicus Vile", "daedra/clavicus-vile" },
            { "Hermaeus Mora", "daedra/hermaeus-mora" }, { "Hircine", "daedra/hircine" }, { "Malacath", "daedra/malacath" },
            { "Mehrunes Dagon", "daedra/mehrunes-dagon" }, { "Mephala", "daedra/mephala" }, { "Meridia", "daedra/meridia" },
            { "Molag Bal", "daedra/molag-bal" }, { "Namira", "daedra/namira" }, { "Nocturnal", "daedra/nocturnal" },
            { "Peryite", "daedra/peryite" }, { "Sanguine", "daedra/sanguine" }, { "Sheogorath", "daedra/sheogorath" },
            { "Vaernima", "daedra/vaernima" }, { "Vaermina", "daedra/vaernima" }
        };

        private static string GetSpecialCharacterSlug(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            string slug;
            return SpecialCharacterSlugs.TryGetValue(name.Trim(), out slug) ? slug : string.Empty;
        }

        private static VoiceProfileCollection CreateDefaultSpecialVoices()
        {
            List<VoiceProfile> list = new List<VoiceProfile>();

            AddSpecial(list, new string[] { "Lady Brisienna", "Brisienna" }, "bf_alice", 0.97f, 0.00f, 0.00f, 0.00f, 0.06f, 0.05f, 0.00f, 0f, 0f, 0.00f, 0.00f, "Formal");
            AddSpecial(list, new string[] { "King Lysandus", "Lysandus" }, "am_granite,bm_george", 0.92f, -0.70f, 0.03f, 0.03f, 0.05f, 0.08f, 0.08f, -0.25f, 18f, 0.55f, 0.24f, "Sad");
            AddSpecial(list, new string[] { "Queen Mynisera", "Mynisera" }, "bf_isabella", 0.94f, -0.05f, 0f, 0f, 0.04f, 0.07f, 0f, 0f, 0f, 0f, 0f, "Formal");
            AddSpecial(list, new string[] { "King Gothryd", "Gothryd" }, "bm_daniel", 0.94f, -0.25f, 0.04f, 0.03f, 0.06f, 0.10f, 0f, 0f, 0f, 0f, 0f, "Formal");
            AddSpecial(list, new string[] { "Queen Aubk-i", "Aubk-i" }, "bf_lily", 0.98f, 0.05f, 0f, 0f, 0.05f, 0.04f, 0f, 0f, 0f, 0f, 0f, "Friendly");
            AddSpecial(list, new string[] { "Medora Direnni", "Medora" }, "bf_alice", 0.95f, -0.05f, 0.02f, 0.02f, 0.08f, 0.06f, 0f, 0f, 0f, 0.04f, 0.03f, "Formal");
            AddSpecial(list, new string[] { "Nulfaga" }, "bf_isabella", 0.88f, -0.25f, 0.06f, 0.04f, 0.04f, 0.08f, 0.06f, 0.30f, 20f, 0.12f, 0.10f, "Sad");
            AddSpecial(list, new string[] { "Gortwog", "Gortwog gro-Nagorm" }, "am_granite,am_granite,bm_daniel", 0.89f, -1.65f, 0.24f, 0.17f, 0.07f, 0.30f, 0.05f, -0.35f, 14f, 0.03f, 0.02f, "Formal");
            AddSpecial(list, new string[] { "Queen Akorithi", "Akorithi" }, "bf_isabella", 0.93f, -0.10f, 0f, 0.02f, 0.09f, 0.10f, 0f, 0f, 0f, 0f, 0f, "Formal");
            AddSpecial(list, new string[] { "Prince Lhotun", "Lhotun" }, "bm_fable", 0.92f, 0.15f, 0f, 0f, 0.02f, 0.03f, 0f, 0f, 0f, 0f, 0f, "Sad");
            AddSpecial(list, new string[] { "King Eadwyre", "Eadwyre" }, "am_granite,bm_george,bm_george", 0.90f, -0.40f, 0.02f, 0.02f, 0.04f, 0.08f, 0f, 0f, 0f, 0f, 0f, "Formal");
            AddSpecial(list, new string[] { "Queen Barenziah", "Barenziah" }, "bf_emma", 0.94f, -0.75f, 0.10f, 0.07f, 0.06f, 0.15f, 0f, 0f, 0f, 0f, 0f, "Friendly");
            AddSpecial(list, new string[] { "Prince Helseth", "Helseth" }, "bm_daniel", 0.93f, -1.55f, 0.68f, 0.20f, 0.17f, 0.27f, 0.04f, -0.30f, 7f, 0f, 0f, "Formal");
            AddSpecial(list, new string[] { "Princess Morgiah", "Morgiah" }, "bf_isabella", 0.95f, -0.80f, 0.10f, 0.07f, 0.06f, 0.15f, 0f, 0f, 0f, 0f, 0f, "Formal");
            AddSpecial(list, new string[] { "Princess Elysana", "Elysana" }, "bf_isabella", 0.98f, 0.08f, 0f, 0.02f, 0.07f, 0.06f, 0f, 0f, 0f, 0f, 0f, "Friendly");
            AddSpecial(list, new string[] { "Lord Woodborne", "Woodborne" }, "am_granite,bm_fable", 0.97f, 0.18f, 0.05f, 0.05f, 0.07f, 0.10f, 0f, 0f, 0f, 0.02f, 0.02f, "Threatening");
            AddSpecial(list, new string[] { "The Underking", "Underking" }, "am_granite,bm_george,bm_george", 0.82f, -1.45f, 0.12f, 0.12f, 0.05f, 0.22f, 0.18f, -0.75f, 22f, 0.90f, 0.38f, "Formal");
            AddSpecial(list, new string[] { "The King of Worms", "King of Worms", "Mannimarco" }, "am_granite,bm_fable,bm_george", 0.87f, -0.65f, 0.08f, 0.08f, 0.08f, 0.15f, 0.12f, -0.40f, 20f, 0.72f, 0.30f, "Threatening");
            AddSpecial(list, new string[] { "Emperor Uriel Septim VII", "Uriel Septim VII" }, "am_granite,bm_george", 0.91f, -0.45f, 0f, 0f, 0.07f, 0.09f, 0f, 0f, 0f, 0f, 0f, "Formal");
            AddSpecial(list, new string[] { "Prince Greklith", "Greklith" }, "am_onyx", 0.96f, -0.35f, 0.05f, 0.04f, 0.07f, 0.08f, 0f, 0f, 0f, 0f, 0f, "Formal");

            // Daedric Princes: distinct Kokoro identities plus spectral post-processing. The clean
            // signal stays dominant so these remain intelligible rather than becoming effect demos.
            AddSpecial(list, new string[] { "Azura" }, "bf_emma", 0.91f, -0.10f, 0f, 0.02f, 0.09f, 0.08f, 0.10f, 0.20f, 18f, 0.62f, 0.24f, "Formal");
            AddSpecial(list, new string[] { "Boethiah" }, "bm_fable", 0.94f, -0.20f, 0.07f, 0.06f, 0.10f, 0.11f, 0.10f, -0.20f, 17f, 0.62f, 0.20f, "Threatening");
            AddSpecial(list, new string[] { "Clavicus Vile" }, "am_granite,bm_fable", 1.02f, 0.22f, 0.02f, 0.03f, 0.09f, 0.06f, 0.07f, 0.25f, 14f, 0.48f, 0.16f, "Friendly");
            AddSpecial(list, new string[] { "Hermaeus Mora" }, "am_granite,bm_george,bm_george", 0.80f, -1.45f, 0.08f, 0.12f, 0.04f, 0.20f, 0.20f, -0.90f, 25f, 0.92f, 0.42f, "Formal");
            AddSpecial(list, new string[] { "Hircine" }, "am_onyx", 0.90f, -0.90f, 0.15f, 0.10f, 0.07f, 0.18f, 0.10f, -0.35f, 18f, 0.56f, 0.18f, "Threatening");
            AddSpecial(list, new string[] { "Malacath" }, "am_onyx", 0.86f, -1.75f, 0.35f, 0.22f, 0.08f, 0.26f, 0.12f, -0.55f, 19f, 0.58f, 0.18f, "Angry");
            AddSpecial(list, new string[] { "Mehrunes Dagon" }, "am_fenrir", 0.88f, -1.55f, 0.27f, 0.22f, 0.12f, 0.28f, 0.15f, -0.65f, 20f, 0.72f, 0.24f, "Threatening");
            AddSpecial(list, new string[] { "Mephala" }, "bf_alice", 0.94f, -0.12f, 0.03f, 0.04f, 0.11f, 0.08f, 0.08f, 0.30f, 17f, 0.58f, 0.20f, "Threatening");
            AddSpecial(list, new string[] { "Meridia" }, "bf_isabella", 0.93f, 0.12f, 0f, 0.03f, 0.18f, 0.14f, 0.10f, 0.35f, 14f, 0.68f, 0.26f, "Formal");
            AddSpecial(list, new string[] { "Molag Bal" }, "am_onyx", 0.82f, -2.00f, 0.25f, 0.28f, 0.08f, 0.34f, 0.20f, -0.85f, 23f, 0.84f, 0.34f, "Threatening");
            AddSpecial(list, new string[] { "Namira" }, "af_kore", 0.89f, -0.65f, 0.12f, 0.10f, 0.04f, 0.16f, 0.13f, -0.45f, 24f, 0.80f, 0.34f, "Threatening");
            AddSpecial(list, new string[] { "Nocturnal" }, "bf_emma", 0.89f, -0.30f, 0.02f, 0.05f, 0.06f, 0.12f, 0.14f, -0.35f, 22f, 0.78f, 0.32f, "Formal");
            AddSpecial(list, new string[] { "Peryite" }, "am_granite,bm_fable", 0.92f, -0.20f, 0.05f, 0.06f, 0.09f, 0.14f, 0.10f, -0.30f, 19f, 0.64f, 0.22f, "Formal");
            AddSpecial(list, new string[] { "Sanguine" }, "bm_fable", 1.02f, 0.10f, 0.03f, 0.05f, 0.11f, 0.08f, 0.07f, 0.25f, 15f, 0.50f, 0.16f, "Friendly");
            AddSpecial(list, new string[] { "Sheogorath" }, "bm_fable", 1.05f, 0.15f, 0.05f, 0.07f, 0.13f, 0.10f, 0.13f, 0.50f, 16f, 0.66f, 0.22f, "Excited");
            AddSpecial(list, new string[] { "Vaernima", "Vaermina" }, "af_nicole", 0.87f, -0.40f, 0.07f, 0.07f, 0.05f, 0.14f, 0.16f, -0.45f, 26f, 0.88f, 0.38f, "Threatening");

            VoiceProfileCollection result = new VoiceProfileCollection();
            result.schemaVersion = 3;
            result.profiles = list.ToArray();
            return result;
        }

        private static void AddSpecial(List<VoiceProfile> list, string[] aliases, string voice, float speed, float pitch,
            float gravel, float saturation, float presence, float compression, float doubleMix, float doublePitch,
            float doubleDelayMs, float spectral, float reverb, string defaultEmotion)
        {
            if (aliases == null) return;
            for (int i = 0; i < aliases.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(aliases[i])) continue;
                VoiceProfile p = new VoiceProfile();
                p.key = aliases[i];
                p.voices = new string[] { voice };
                p.speed = speed;
                p.pitch = pitch;
                p.gravel = gravel;
                p.saturation = saturation;
                p.presence = presence;
                p.compression = compression;
                p.doubleMix = doubleMix;
                p.doublePitch = doublePitch;
                p.doubleDelayMs = doubleDelayMs;
                p.spectral = spectral;
                p.reverb = reverb;
                p.defaultEmotion = defaultEmotion;
                p.lockVariation = true;
                p.enabled = true;
                list.Add(p);
            }
        }

        private static VoiceProfileCollection CreateDefaultVoiceTypes()
        {
            List<VoiceProfile> list = new List<VoiceProfile>();
            foreach (string race in ProceduralRaces)
            {
                AddRaceGenderFallback(list, race, "Male");
                AddRaceGenderFallback(list, race, "Female");
                foreach (string role in ProceduralRoles)
                {
                    list.Add(CreateProceduralProfile(race, "Male", role));
                    list.Add(CreateProceduralProfile(race, "Female", role));
                }
            }

            AddProfile(list, "MaleChild", BuildAccentWeightedPool("Male", 0.35f, "MaleChild"), 1.06f, 1.0f);
            AddProfile(list, "FemaleChild", BuildAccentWeightedPool("Female", 0.35f, "FemaleChild"), 1.06f, 1.0f);
            AddProfile(list, "Male", ConcatVoices(UsMaleVoices, UkMaleVoices, new string[] { "am_granite" }));
            AddProfile(list, "Female", ConcatVoices(UsFemaleVoices, UkFemaleVoices));
            AddProfile(list, "Generic", ConcatVoices(UsMaleVoices, UsFemaleVoices, UkMaleVoices, UkFemaleVoices));

            VoiceProfileCollection result = new VoiceProfileCollection();
            result.schemaVersion = 6;
            result.profiles = list.ToArray();
            return result;
        }

        private static void AddRaceGenderFallback(List<VoiceProfile> list, string race, string gender)
        {
            VoiceProfile p = CreateProceduralProfile(race, gender, "Guild");
            p.key = race + gender;
            // CreateProceduralProfile may substitute a curated race/gender pool (e.g. Dunmer/Orc).
            // Keep that pool rather than rebuilding it generically here.
            list.Add(p);
        }

        private static VoiceProfile CreateProceduralProfile(string race, string gender, string role)
        {
            VoiceProfile p = new VoiceProfile();
            p.key = race + gender + role;
            p.voices = BuildAccentWeightedPool(gender, GetUkProbability(race, role), p.key);
            p.speed = 1f;
            p.pitch = 0f;
            p.enabled = true;
            p.lockVariation = false;

            // Social class changes delivery while race supplies the broader vocal character.
            if (role == "Noble") p.speed *= 0.97f;
            else if (role == "Scholar") p.speed *= 0.98f;
            else if (role == "Judge")
            {
                // Court dialogue should feel ceremonial and weighty rather than like an ordinary
                // service NPC. Keep it intelligible: authority comes from cadence/resonance, not monster DSP.
                p.speed *= 0.90f;
                p.pitch -= 0.30f;
                p.presence += 0.10f;
                p.compression += 0.15f;
                p.throatResonance += 0.05f;
                p.reverb += 0.045f;
                p.defaultEmotion = "Formal";
            }
            else if (role == "Merchant") p.speed *= 1.01f;
            else if (role == "Underworld") p.speed *= 0.98f;
            else if (role == "Supernatural") { p.speed *= 0.96f; p.saturation += 0.04f; }
            else if (role == "Guild") p.speed *= 0.99f;

            bool male = string.Equals(gender, "Male", StringComparison.OrdinalIgnoreCase);
            if (race == "Nord")
            {
                if (male)
                {
                    // Heroic Nord: mature, broad, deliberate and resonant. The custom Granite voice
                    // does the heavy lifting; British blends add a restrained saga-like color.
                    p.voices = BuildAccentWeightedPoolFrom(NordHeroicUsMaleVoices, NordHeroicUkMaleVoices,
                        GetUkProbability(race, role), p.key + "|NordHeroicGranite");
                    p.speed *= 0.95f;
                    p.pitch -= 0.85f;
                    p.gravel += 0.08f;
                    p.saturation += 0.07f;
                    p.presence += 0.10f;
                    p.compression += 0.20f;
                    p.doubleMix = Mathf.Max(p.doubleMix, 0.045f);
                    p.doublePitch = -0.18f;
                    p.doubleDelayMs = 6f;
                }
                else
                {
                    p.speed *= 0.99f;
                    p.pitch -= 0.05f;
                }
            }
            else if (race == "DarkElf")
            {
                // Dunmer are intentionally exempted from the brighter generic Mer treatment. Male
                // voices aim for the deep, dry, gravel-heavy Morrowind character; female voices are
                // lower, slower and smoother in the Skyrim direction rather than high/fey.
                p.speed *= male ? 0.94f : 0.95f;
                p.pitch += male ? -1.75f : -0.85f;
                p.voices = BuildAccentWeightedPoolFrom(
                    male ? DunmerUsMaleVoices : DunmerUsFemaleVoices,
                    male ? DunmerUkMaleVoices : DunmerUkFemaleVoices,
                    GetUkProbability(race, role), p.key + "|DunmerCurated");
                if (male)
                {
                    p.gravel = 0.78f; p.saturation = 0.22f; p.presence = 0.18f; p.compression = 0.28f;
                    p.doubleMix = 0.05f; p.doublePitch = -0.35f; p.doubleDelayMs = 7f;
                }
                else
                {
                    p.gravel = 0.12f; p.saturation = 0.08f; p.presence = 0.06f; p.compression = 0.16f;
                }
            }
            else if (race == "Redguard" && male)
            {
                // Redguard men borrow the disciplined Orc baritone *shape* while remaining fully human.
                // Deliberately keep the normal human US/UK voice pool instead of switching to OrcUsMaleVoices.
                // This gives them more chest weight and authority without turning every Redguard into an Orc.
                p.speed *= 0.94f;
                p.pitch -= 1.45f;
                p.gravel += 0.16f;
                p.saturation += 0.10f;
                p.compression += 0.20f;
                p.presence += 0.05f;
                p.throatResonance += 0.035f;
            }
            else if (race == "HighElf") { p.speed *= 0.98f; p.pitch += male ? 0.10f : 0.15f; }
            else if (race == "WoodElf") { p.speed *= 1.01f; p.pitch += male ? 0.10f : 0.15f; }
            else if (race == "Khajiit")
            {
                p.speed *= male ? 0.97f : 0.99f;
                p.pitch += male ? -0.45f : -0.15f;
                p.gravel += male ? 0.06f : 0.04f; p.presence += 0.10f; p.saturation += 0.04f;
                // Feline physiology: a subtle chest purr, warm throat resonance, gentle growl and air.
                p.purrMix = male ? 0.14f : 0.12f; p.purrRate = male ? 33f : 36f;
                p.felineResonance = 0.18f; p.growl = male ? 0.17f : 0.11f; p.breath = 0.10f;
            }
            else if (race == "Argonian")
            {
                p.speed *= male ? 0.95f : 0.97f;
                p.pitch += male ? -1.00f : -0.65f;
                p.saturation += 0.14f; p.presence += 0.08f; p.compression += 0.22f;
                // Reptilian throat stack: subharmonic body, sibilance, short cavity resonance,
                // micro-flutter and a croaking nonlinear low layer.
                p.subharmonicMix = male ? 0.23f : 0.17f; p.subharmonicPitch = -5.5f;
                p.hiss = male ? 0.14f : 0.12f; p.throatResonance = 0.19f;
                p.flutterDepth = 0.11f; p.flutterRate = 4.1f; p.croak = male ? 0.34f : 0.27f;
                p.doubleMix = 0.05f; p.doublePitch = -0.45f; p.doubleDelayMs = 9f;
            }
            else if (race == "Orc")
            {
                p.speed *= male ? 0.90f : 0.95f;
                p.pitch += male ? -2.65f : -1.10f;
                if (male)
                    p.voices = BuildAccentWeightedPoolFrom(OrcUsMaleVoices, OrcUkMaleVoices, GetUkProbability(race, role), p.key + "|OrcBaritone");
                // v0.1.10 nudges male Orcs lower than before so Redguards can occupy the adjacent
                // human baritone space without the two archetypes collapsing into one sound.
                p.gravel += male ? 0.28f : 0.18f; p.saturation += male ? 0.18f : 0.20f;
                p.compression += male ? 0.33f : 0.25f; p.presence += male ? 0.08f : 0.08f;
                if (male) p.throatResonance += 0.04f;
            }
            else if (race == "Dragon")
            {
                p.speed *= 0.86f; p.pitch -= 3.50f;
                p.gravel += 0.22f; p.saturation += 0.32f; p.presence += 0.15f; p.compression += 0.35f;
                p.doubleMix = 0.24f; p.doublePitch = -1.20f; p.doubleDelayMs = 18f;
            }

            ApplyGraniteEcosystem(p, race, gender, role);

            if (role == "Child")
            {
                // Youthful rather than helium-like. Race processing remains but is reduced.
                p.speed *= 1.06f;
                p.pitch += 1.00f;
                p.gravel *= 0.45f; p.saturation *= 0.55f; p.presence *= 0.75f; p.compression *= 0.60f;
                p.doubleMix *= 0.35f; p.subharmonicMix *= 0.40f; p.hiss *= 0.70f; p.throatResonance *= 0.60f;
                p.flutterDepth *= 0.60f; p.croak *= 0.45f; p.purrMix *= 0.70f; p.growl *= 0.45f; p.breath *= 0.80f;
            }
            return p;
        }

        private static bool IsGraniteEcosystemRace(string race)
        {
            return string.Equals(race, "Breton", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(race, "Redguard", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(race, "HighElf", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(race, "WoodElf", StringComparison.OrdinalIgnoreCase);
        }

        private static void ApplyGraniteEcosystem(VoiceProfile p, string race, string gender, string role)
        {
            if (p == null || !string.Equals(gender, "Male", StringComparison.OrdinalIgnoreCase) ||
                !IsGraniteEcosystemRace(race) || string.Equals(role, "Child", StringComparison.OrdinalIgnoreCase))
                return;

            string[] additions = null;
            if (role == "Guild")
                additions = GraniteEpicMaleVoices;
            else if (role == "Judge")
                additions = ConcatVoices(GraniteEpicMaleVoices, GraniteWizardMaleVoices);
            else if (role == "Noble")
                additions = ConcatVoices(GraniteEpicMaleVoices, GraniteWizardMaleVoices);
            else if (role == "Scholar")
                additions = GraniteWizardMaleVoices;
            else if (role == "Supernatural")
                additions = ConcatVoices(GraniteWizardMaleVoices, new string[] { "am_granite,am_granite,bm_george" });
            else if (role == "Underworld")
            {
                additions = GraniteSchemerMaleVoices;
                // A very small lift in cadence/pitch helps the Granite schemer branch read oily rather than heroic.
                p.speed *= 1.01f;
                p.pitch += 0.08f;
                p.presence += 0.03f;
            }
            else if (role == "Merchant")
                additions = new string[] { "am_granite", "am_granite,bm_fable" };
            else if (role == "Commoner")
                additions = new string[] { "am_granite" };

            if (additions != null && additions.Length > 0)
                p.voices = ConcatVoices(p.voices, additions);
        }

        private static float GetUkProbability(string race, string role)
        {
            float uk;
            if (role == "Noble") uk = 0.85f;
            else if (role == "Judge") uk = 0.78f;
            else if (role == "Scholar") uk = 0.68f;
            else if (role == "Guild") uk = 0.55f;
            else if (role == "Merchant") uk = 0.40f;
            else if (role == "Supernatural") uk = 0.50f;
            else if (role == "Underworld") uk = 0.12f;
            else if (role == "Child") uk = 0.35f;
            else uk = 0.18f;

            // Mer favor UK voices even at lower social ranks; humans stay subtler.
            if (race == "HighElf") uk += 0.25f;
            else if (race == "DarkElf") uk += 0.20f;
            else if (race == "WoodElf") uk += 0.15f;
            else if (race == "Breton") uk += 0.05f;
            else if (race == "Nord") uk -= 0.05f;
            else if (race == "Redguard") uk -= 0.08f;
            else if (race == "Orc") uk -= 0.05f;
            return Mathf.Clamp(uk, 0.05f, 0.95f);
        }

        private static string[] BuildAccentWeightedPoolFrom(string[] us, string[] uk, float ukProbability, string seed)
        {
            if (us == null || us.Length == 0) us = UsMaleVoices;
            if (uk == null || uk.Length == 0) uk = UkMaleVoices;
            const int slots = 24;
            int ukSlots = Mathf.Clamp(Mathf.RoundToInt(ukProbability * slots), 1, slots - 1);
            int usSlots = slots - ukSlots;
            List<string> result = new List<string>(slots);
            AddWeightedVoiceSlots(result, us, usSlots, Fnv1a32(seed + "|US"));
            AddWeightedVoiceSlots(result, uk, ukSlots, Fnv1a32(seed + "|UK"));
            return result.ToArray();
        }

        private static string[] BuildAccentWeightedPool(string gender, float ukProbability, string seed)
        {
            string[] us = string.Equals(gender, "Female", StringComparison.OrdinalIgnoreCase) ? UsFemaleVoices : UsMaleVoices;
            string[] uk = string.Equals(gender, "Female", StringComparison.OrdinalIgnoreCase) ? UkFemaleVoices : UkMaleVoices;
            const int slots = 24;
            int ukSlots = Mathf.Clamp(Mathf.RoundToInt(ukProbability * slots), 1, slots - 1);
            int usSlots = slots - ukSlots;
            List<string> result = new List<string>(slots);
            AddWeightedVoiceSlots(result, us, usSlots, Fnv1a32(seed + "|US"));
            AddWeightedVoiceSlots(result, uk, ukSlots, Fnv1a32(seed + "|UK"));
            return result.ToArray();
        }

        private static void AddWeightedVoiceSlots(List<string> output, string[] voices, int count, uint seed)
        {
            if (output == null || voices == null || voices.Length == 0 || count <= 0) return;
            int start = (int)(seed % (uint)voices.Length);
            for (int i = 0; i < count; i++)
                output.Add(voices[(start + i) % voices.Length]);
        }

        private static string[] ConcatVoices(params string[][] groups)
        {
            List<string> result = new List<string>();
            if (groups != null)
                foreach (string[] group in groups)
                    if (group != null)
                        foreach (string voice in group)
                            if (!string.IsNullOrWhiteSpace(voice)) result.Add(voice);
            return result.ToArray();
        }

        private static void AddProfile(List<VoiceProfile> list, string key, string[] voices)
        {
            AddProfile(list, key, voices, 1f, 0f);
        }

        private static void AddProfile(List<VoiceProfile> list, string key, string[] voices, float speed, float pitch)
        {
            VoiceProfile p = new VoiceProfile();
            p.key = key;
            p.voices = voices;
            p.speed = speed;
            p.pitch = pitch;
            p.enabled = true;
            p.lockVariation = false;
            list.Add(p);
        }

        private void CleanupCacheIfNeeded()
        {
            if (config == null || config.MaxCacheSizeMB <= 0 || !Directory.Exists(cacheDir))
                return;
            try
            {
                FileInfo[] files = new DirectoryInfo(cacheDir).GetFiles("*.wav");
                long total = 0;
                foreach (FileInfo f in files) total += f.Length;
                long max = (long)config.MaxCacheSizeMB * 1024L * 1024L;
                if (total <= max)
                    return;

                Array.Sort(files, delegate(FileInfo a, FileInfo b) { return a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc); });
                long target = (long)(max * 0.85);
                foreach (FileInfo file in files)
                {
                    if (total <= target) break;
                    long size = file.Length;
                    try { file.Delete(); total -= size; } catch { }
                }
            }
            catch { }
        }

        private void RegisterConsoleCommands()
        {
            try
            {
                ConsoleCommandsDatabase.RegisterCommand("npcvo_status", "Shows NPCVO backend and integration status.", "npcvo_status", ConsoleStatus);
                ConsoleCommandsDatabase.RegisterCommand("npcvo_who", "Shows the current NPC identity and resolved VoiceType.", "npcvo_who", ConsoleWho);
                ConsoleCommandsDatabase.RegisterCommand("npcvo_test", "Tests Daggerfall Voice Engine.", "npcvo_test", ConsoleTest);
                ConsoleCommandsDatabase.RegisterCommand("npcvo_test_voice", "Tests a procedural VoiceType: npcvo_test_voice <race> <gender> [role].", "npcvo_test_voice Breton Male Commoner", ConsoleVoicePreset);
                ConsoleCommandsDatabase.RegisterCommand("npcvo_test_breton_m", "Tests a Breton male Commoner VoiceType.", "npcvo_test_breton_m", ConsoleTestBretonM);
                ConsoleCommandsDatabase.RegisterCommand("npcvo_test_breton_f", "Tests a Breton female Commoner VoiceType.", "npcvo_test_breton_f", ConsoleTestBretonF);
                ConsoleCommandsDatabase.RegisterCommand("npcvo_test_redguard_m", "Tests a Redguard male Commoner VoiceType.", "npcvo_test_redguard_m", ConsoleTestRedguardM);
                ConsoleCommandsDatabase.RegisterCommand("npcvo_test_redguard_f", "Tests a Redguard female Commoner VoiceType.", "npcvo_test_redguard_f", ConsoleTestRedguardF);
                ConsoleCommandsDatabase.RegisterCommand("npcvo_test_nord_m", "Tests a Nord male Commoner VoiceType.", "npcvo_test_nord_m", ConsoleTestNordM);
                ConsoleCommandsDatabase.RegisterCommand("npcvo_test_nord_f", "Tests a Nord female Commoner VoiceType.", "npcvo_test_nord_f", ConsoleTestNordF);
                ConsoleCommandsDatabase.RegisterCommand("npcvo_test_darkelf_m", "Tests a Dark Elf male Commoner VoiceType, including Dunmer gravel processing.", "npcvo_test_darkelf_m", ConsoleTestDarkElfM);
                ConsoleCommandsDatabase.RegisterCommand("npcvo_test_darkelf_f", "Tests a Dark Elf female Commoner VoiceType.", "npcvo_test_darkelf_f", ConsoleTestDarkElfF);
                ConsoleCommandsDatabase.RegisterCommand("npcvo_test_highelf_m", "Tests a High Elf male Commoner VoiceType.", "npcvo_test_highelf_m", ConsoleTestHighElfM);
                ConsoleCommandsDatabase.RegisterCommand("npcvo_test_highelf_f", "Tests a High Elf female Commoner VoiceType.", "npcvo_test_highelf_f", ConsoleTestHighElfF);
                ConsoleCommandsDatabase.RegisterCommand("npcvo_test_woodelf_m", "Tests a Wood Elf male Commoner VoiceType.", "npcvo_test_woodelf_m", ConsoleTestWoodElfM);
                ConsoleCommandsDatabase.RegisterCommand("npcvo_test_woodelf_f", "Tests a Wood Elf female Commoner VoiceType.", "npcvo_test_woodelf_f", ConsoleTestWoodElfF);
                ConsoleCommandsDatabase.RegisterCommand("npcvo_test_khajiit_m", "Tests a Khajiit male Commoner VoiceType.", "npcvo_test_khajiit_m", ConsoleTestKhajiitM);
                ConsoleCommandsDatabase.RegisterCommand("npcvo_test_khajiit_f", "Tests a Khajiit female Commoner VoiceType.", "npcvo_test_khajiit_f", ConsoleTestKhajiitF);
                ConsoleCommandsDatabase.RegisterCommand("npcvo_test_argonian_m", "Tests an Argonian male Commoner VoiceType.", "npcvo_test_argonian_m", ConsoleTestArgonianM);
                ConsoleCommandsDatabase.RegisterCommand("npcvo_test_argonian_f", "Tests an Argonian female Commoner VoiceType.", "npcvo_test_argonian_f", ConsoleTestArgonianF);
                ConsoleCommandsDatabase.RegisterCommand("npcvo_test_orc_m", "Tests an Orc male Commoner VoiceType.", "npcvo_test_orc_m", ConsoleTestOrcM);
                ConsoleCommandsDatabase.RegisterCommand("npcvo_test_orc_f", "Tests an Orc female Commoner VoiceType.", "npcvo_test_orc_f", ConsoleTestOrcF);
                ConsoleCommandsDatabase.RegisterCommand("npcvo_test_dragon_m", "Tests a Dragon male Supernatural VoiceType.", "npcvo_test_dragon_m", ConsoleTestDragonM);
                ConsoleCommandsDatabase.RegisterCommand("npcvo_test_dragon_f", "Tests a Dragon female Supernatural VoiceType.", "npcvo_test_dragon_f", ConsoleTestDragonF);
                ConsoleCommandsDatabase.RegisterCommand("npcvo_test_daedra", "Tests the generic spectral Daedra fallback processing.", "npcvo_test_daedra", ConsoleTestDaedra);
                ConsoleCommandsDatabase.RegisterCommand("npcvo_stop", "Stops current NPC speech.", "npcvo_stop", ConsoleStop);
                ConsoleCommandsDatabase.RegisterCommand("npcvo_reload", "Reloads NPCVO.ini and voice profile JSON files.", "npcvo_reload", ConsoleReload);
                ConsoleCommandsDatabase.RegisterCommand("npcvo_lineinfo", "Shows the current line's pre-generated VoicePack key/path.", "npcvo_lineinfo", ConsoleLineInfo);
                ConsoleCommandsDatabase.RegisterCommand("npcvo_clear_cache", "Clears runtime Kokoro WAVs only; VoicePack WAVs are untouched.", "npcvo_clear_cache", ConsoleClearCache);
                ConsoleCommandsDatabase.RegisterCommand("npcvo_clear_assignments", "Clears persistent procedural NPC voice assignments so they can be recast on next encounter.", "npcvo_clear_assignments", ConsoleClearAssignments);
                ConsoleCommandsDatabase.RegisterCommand("npcvo_quest_debug", "Shows QuestOffer UI detection state.", "npcvo_quest_debug", ConsoleQuestDebug);
                ConsoleCommandsDatabase.RegisterCommand("npcvo_voice_install", "Downloads/verifies all supported English Kokoro voices through the shared server.", "npcvo_voice_install", ConsoleVoiceInstall);
                ConsoleCommandsDatabase.RegisterCommand("npcvo_voice_status", "Queries the Daggerfall Voice Engine voice library.", "npcvo_voice_status", ConsoleVoiceStatus);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[NPCVO] Console commands could not be registered: " + ex.Message);
            }
        }

        private static string ConsoleStatus(params string[] args)
        {
            if (instance == null) return "NPCVO is not initialized.";
            instance.EnsureDynamicPortraitsReflection();
            bool dp = instance.GetDynamicPortraitsInstance() != null;
            return "Daggerfall Narrator - NPC v0.2.4 | Enabled=" + instance.config.Enabled +
                " | SpeechSource=" + instance.config.SpeechSource +
                " | Kokoro=http://127.0.0.1:" + instance.config.KokoroPort +
                " | Dynamic Portraits=" + (dp ? "detected" : "not detected") +
                " | Progressive text=" + (instance.config.RevealNpcTextWithSpeech ? "on" : "off") +
                " | Quest portrait UI=" + (instance.config.QuestGiverPortraitUI ? "on" : "off") +
                " | VoiceTypes=" + ProfileCount(instance.voiceTypes) +
                " | UniqueVoices=" + ProfileCount(instance.uniqueVoices) +
                " | SpecialVoices=" + ProfileCount(instance.specialVoices) +
                " | PortraitVoices=" + ProfileCount(instance.portraitVoices) +
                " | PersistentAssignments=" + (instance.voiceAssignmentsByKey == null ? 0 : instance.voiceAssignmentsByKey.Count) +
                " | FantasyDSP=" + instance.config.FantasyProcessing +
                " | VoiceLibrary=" + instance.lastVoiceLibraryStatus +
                " | Cache=" + instance.GetCacheSizeText();
        }

        private static string ConsoleWho(params string[] args)
        {
            if (instance == null) return "NPCVO is not initialized.";
            NPCIdentity id = instance.ResolveIdentity();
            ResolvedVoice voice = instance.ResolveVoice(id);
            if (voice == null) return id.ToDisplayString() + " | Voice=UNRESOLVED";
            return id.ToDisplayString() + " | Profile=" + voice.ProfileKey + " (" + voice.Source + ")" +
                " | Voice=" + voice.VoiceId + " | Pitch=" + voice.PitchSemitones.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) +
                " st | Speed=" + voice.Speed.ToString("0.000", CultureInfo.InvariantCulture) +
                " | DSP[g=" + voice.Gravel.ToString("0.00", CultureInfo.InvariantCulture) +
                ",sat=" + voice.Saturation.ToString("0.00", CultureInfo.InvariantCulture) +
                ",p=" + voice.Presence.ToString("0.00", CultureInfo.InvariantCulture) +
                ",c=" + voice.Compression.ToString("0.00", CultureInfo.InvariantCulture) +
                ",dbl=" + voice.DoubleMix.ToString("0.00", CultureInfo.InvariantCulture) + "]";
        }

        private static string ConsoleTest(params string[] args)
        {
            if (instance == null) return "NPCVO is not initialized.";
            NPCIdentity fake = new NPCIdentity();
            fake.Name = "NPCVO Test"; fake.Race = "Breton"; fake.Gender = "Male"; fake.Role = "Commoner"; fake.NpcType = "Test"; fake.StableKey = "npcvo-test";
            ResolvedVoice voice = instance.ResolveVoice(fake);
            if (voice == null)
            {
                // Self-test must still be able to exercise Kokoro when the assignment table is bad.
                voice = new ResolvedVoice();
                voice.VoiceId = "bm_george";
                voice.Speed = 1f;
                voice.PitchSemitones = 0f;
                voice.ProfileKey = "SelfTestFallback";
                voice.Source = "hard fallback";
                Debug.LogWarning("[NPCVO] npcvo_test used hard fallback bm_george because no profile resolved.");
            }
            instance.StopSpeech();
            SpeechRequest request = new SpeechRequest();
            request.Line = "Greetings, traveler. NPC V O is working.";
            request.Identity = fake;
            request.Voice = voice;
            request.ForceKokoro = true;
            instance.BeginSpeech(request);
            return "NPCVO test requested.";
        }

        private static string ConsoleVoicePreset(params string[] args)
        {
            if (instance == null) return "NPCVO is not initialized.";
            if (args == null || args.Length < 2)
                return "Usage: npcvo_test_voice <race> <gender> [role]. Example: npcvo_test_voice DarkElf Male Noble";

            string race = NormalizeTestRace(args[0]);
            string gender = NormalizeTestGender(args[1]);
            string role = args.Length >= 3 ? NormalizeTestRole(args[2]) : "Commoner";
            if (string.IsNullOrEmpty(race))
                return "Unknown race. Use Breton, Redguard, Nord, DarkElf, HighElf, WoodElf, Khajiit, Argonian, Orc, or Dragon.";
            if (string.IsNullOrEmpty(gender))
                return "Unknown gender. Use Male/Female or M/F.";
            if (string.IsNullOrEmpty(role))
                return "Unknown role. Use Commoner, Merchant, Scholar, Noble, Judge, Underworld, Supernatural, Guild, or Child.";
            return RunVoiceTypeTest(race, gender, role, false);
        }

        private static string ConsoleTestBretonM(params string[] args) { return RunVoiceTypeTest("Breton", "Male", "Commoner", false); }
        private static string ConsoleTestBretonF(params string[] args) { return RunVoiceTypeTest("Breton", "Female", "Commoner", false); }
        private static string ConsoleTestRedguardM(params string[] args) { return RunVoiceTypeTest("Redguard", "Male", "Commoner", false); }
        private static string ConsoleTestRedguardF(params string[] args) { return RunVoiceTypeTest("Redguard", "Female", "Commoner", false); }
        private static string ConsoleTestNordM(params string[] args) { return RunVoiceTypeTest("Nord", "Male", "Commoner", false); }
        private static string ConsoleTestNordF(params string[] args) { return RunVoiceTypeTest("Nord", "Female", "Commoner", false); }
        private static string ConsoleTestDarkElfM(params string[] args) { return RunVoiceTypeTest("DarkElf", "Male", "Commoner", false); }
        private static string ConsoleTestDarkElfF(params string[] args) { return RunVoiceTypeTest("DarkElf", "Female", "Commoner", false); }
        private static string ConsoleTestHighElfM(params string[] args) { return RunVoiceTypeTest("HighElf", "Male", "Commoner", false); }
        private static string ConsoleTestHighElfF(params string[] args) { return RunVoiceTypeTest("HighElf", "Female", "Commoner", false); }
        private static string ConsoleTestWoodElfM(params string[] args) { return RunVoiceTypeTest("WoodElf", "Male", "Commoner", false); }
        private static string ConsoleTestWoodElfF(params string[] args) { return RunVoiceTypeTest("WoodElf", "Female", "Commoner", false); }
        private static string ConsoleTestKhajiitM(params string[] args) { return RunVoiceTypeTest("Khajiit", "Male", "Commoner", false); }
        private static string ConsoleTestKhajiitF(params string[] args) { return RunVoiceTypeTest("Khajiit", "Female", "Commoner", false); }
        private static string ConsoleTestArgonianM(params string[] args) { return RunVoiceTypeTest("Argonian", "Male", "Commoner", false); }
        private static string ConsoleTestArgonianF(params string[] args) { return RunVoiceTypeTest("Argonian", "Female", "Commoner", false); }
        private static string ConsoleTestOrcM(params string[] args) { return RunVoiceTypeTest("Orc", "Male", "Commoner", false); }
        private static string ConsoleTestOrcF(params string[] args) { return RunVoiceTypeTest("Orc", "Female", "Commoner", false); }
        private static string ConsoleTestDragonM(params string[] args) { return RunVoiceTypeTest("Dragon", "Male", "Supernatural", false); }
        private static string ConsoleTestDragonF(params string[] args) { return RunVoiceTypeTest("Dragon", "Female", "Supernatural", false); }
        private static string ConsoleTestDaedra(params string[] args) { return RunVoiceTypeTest("Daedra", "Male", "Supernatural", true); }

        private static string RunSettingsSoundTest(int preset, int roleIndex)
        {
            if (preset <= 0)
                return "Sound test idle.";

            string[] roles = new string[] { "Commoner", "Merchant", "Scholar", "Noble", "Judge", "Underworld", "Supernatural", "Guild", "Child" };
            string role = roles[Mathf.Clamp(roleIndex, 0, roles.Length - 1)];

            if (preset == 21)
                return RunVoiceTypeTest("Daedra", "Male", "Supernatural", true);
            if (preset == 22)
                return RunVoiceTypeTest("Breton", "Male", "Judge", false);
            if (preset == 23)
                return RunVoiceTypeTest("Breton", "Female", "Judge", false);

            string[] races = new string[] { "Breton", "Redguard", "Nord", "DarkElf", "HighElf", "WoodElf", "Khajiit", "Argonian", "Orc", "Dragon" };
            int zero = preset - 1;
            if (zero < 0 || zero >= races.Length * 2)
                return "Unknown settings sound-test preset.";
            string race = races[zero / 2];
            string gender = (zero % 2) == 0 ? "Male" : "Female";
            if (string.Equals(race, "Dragon", StringComparison.OrdinalIgnoreCase) && role == "Commoner")
                role = "Supernatural";
            return RunVoiceTypeTest(race, gender, role, false);
        }

        private static string RunVoiceTypeTest(string race, string gender, string role, bool genericDaedra)
        {
            if (instance == null) return "NPCVO is not initialized.";

            NPCIdentity fake = new NPCIdentity();
            fake.Name = genericDaedra ? "Generic Daedra Test" : (race + " " + gender + " Test");
            fake.Race = race;
            fake.Gender = gender;
            fake.Role = role;
            fake.NpcType = "ConsoleVoiceTest";
            fake.StableKey = "npcvo-voice-test|" + race + "|" + gender + "|" + role;

            ResolvedVoice voice = genericDaedra ? instance.CreateGenericDaedraTestVoice(fake) : instance.ResolveVoice(fake);
            if (voice == null || string.IsNullOrWhiteSpace(voice.VoiceId))
                return "NPCVO could not resolve the " + race + " " + gender + " " + role + " test voice.";

            string line;
            if (genericDaedra)
                line = "Mortal, the veil between your world and Oblivion is thinner than you imagine.";
            else if (string.Equals(race, "DarkElf", StringComparison.OrdinalIgnoreCase) && string.Equals(gender, "Male", StringComparison.OrdinalIgnoreCase))
                line = "You have come a long way, outlander. Speak your business.";
            else if (string.Equals(race, "Dragon", StringComparison.OrdinalIgnoreCase))
                line = "You stand before an ancient voice. Choose your next words carefully.";
            else if (string.Equals(role, "Judge", StringComparison.OrdinalIgnoreCase))
                line = "The court will hear your plea. Speak clearly, for judgment shall be rendered.";
            else
                line = "Greetings, traveler. This is the " + race + " " + gender.ToLowerInvariant() + " voice test.";

            string emotion = genericDaedra ? "Threatening" : (string.Equals(role, "Judge", StringComparison.OrdinalIgnoreCase) ? "Formal" : "Neutral");
            voice = instance.ApplyEmotionToVoice(voice, emotion);
            instance.StopSpeech();

            SpeechRequest request = new SpeechRequest();
            request.OriginalLine = line;
            request.Line = instance.config != null && instance.config.GrammarPolish ? PolishNpcText(line) : line;
            request.DisplayText = request.Line;
            request.Identity = fake;
            request.Voice = voice;
            request.Emotion = emotion;
            request.ForceKokoro = true;
            instance.BeginSpeech(request);

            return "NPCVO voice test requested | " + race + "/" + gender + "/" + role +
                " | Profile=" + voice.ProfileKey + " (" + voice.Source + ")" +
                " | Voice=" + voice.VoiceId +
                " | Emotion=" + emotion +
                " | Pitch=" + voice.PitchSemitones.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) +
                " st | Speed=" + voice.Speed.ToString("0.000", CultureInfo.InvariantCulture) +
                " | DSP[g=" + voice.Gravel.ToString("0.00", CultureInfo.InvariantCulture) +
                ",sat=" + voice.Saturation.ToString("0.00", CultureInfo.InvariantCulture) +
                ",p=" + voice.Presence.ToString("0.00", CultureInfo.InvariantCulture) +
                ",c=" + voice.Compression.ToString("0.00", CultureInfo.InvariantCulture) +
                ",dbl=" + voice.DoubleMix.ToString("0.00", CultureInfo.InvariantCulture) +
                ",sp=" + voice.Spectral.ToString("0.00", CultureInfo.InvariantCulture) +
                ",rv=" + voice.Reverb.ToString("0.00", CultureInfo.InvariantCulture) +
                ",sub=" + voice.SubharmonicMix.ToString("0.00", CultureInfo.InvariantCulture) +
                ",hiss=" + voice.Hiss.ToString("0.00", CultureInfo.InvariantCulture) +
                ",throat=" + voice.ThroatResonance.ToString("0.00", CultureInfo.InvariantCulture) +
                ",croak=" + voice.Croak.ToString("0.00", CultureInfo.InvariantCulture) +
                ",purr=" + voice.PurrMix.ToString("0.00", CultureInfo.InvariantCulture) +
                ",growl=" + voice.Growl.ToString("0.00", CultureInfo.InvariantCulture) + "]";
        }

        private ResolvedVoice CreateGenericDaedraTestVoice(NPCIdentity identity)
        {
            // Deliberately generic rather than impersonating one of the curated Princes. This auditions
            // the common spectral post-processing layer that can be used by non-special Daedric voices.
            NPCIdentity baseIdentity = new NPCIdentity();
            baseIdentity.Name = "Generic Daedra Base";
            baseIdentity.Race = "HighElf";
            baseIdentity.Gender = identity == null || string.IsNullOrWhiteSpace(identity.Gender) ? "Male" : identity.Gender;
            baseIdentity.Role = "Supernatural";
            baseIdentity.NpcType = "ConsoleVoiceTest";
            baseIdentity.StableKey = identity == null ? "npcvo-generic-daedra" : identity.StableKey + "|base";

            ResolvedVoice result = ResolveVoice(baseIdentity);
            if (result == null)
            {
                result = new ResolvedVoice();
                result.VoiceId = "bm_fable";
                result.Speed = 1f;
                result.PitchSemitones = 0f;
            }
            else
            {
                result = CloneResolvedVoice(result);
            }

            float dspScale = config != null && config.FantasyProcessing == FantasyProcessingStrength.Subtle ? 0.55f :
                (config != null && config.FantasyProcessing == FantasyProcessingStrength.Strong ? 1.35f : 1f);
            result.ProfileKey = "GenericDaedraSpectral";
            result.Source = "Generic Daedra console test";
            result.DefaultEmotion = "Threatening";
            result.Speed = Mathf.Clamp(result.Speed * 0.94f, 0.70f, 1.35f);
            result.PitchSemitones = Mathf.Clamp(result.PitchSemitones - 1.05f, -6f, 4f);
            result.Saturation = Mathf.Clamp01(result.Saturation + 0.10f * dspScale);
            result.Presence = Mathf.Clamp01(result.Presence + 0.08f * dspScale);
            result.Compression = Mathf.Clamp01(result.Compression + 0.16f * dspScale);
            result.DoubleMix = Mathf.Max(result.DoubleMix, Mathf.Clamp01(0.22f * dspScale));
            result.DoublePitchSemitones = -0.55f;
            result.DoubleDelayMs = Mathf.Max(result.DoubleDelayMs, 24f);
            result.Spectral = Mathf.Max(result.Spectral, Mathf.Clamp01(0.88f * dspScale));
            result.Reverb = Mathf.Max(result.Reverb, Mathf.Clamp01(0.58f * dspScale));
            return result;
        }

        private static string NormalizeTestRace(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string v = value.Trim().Replace("-", string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
            if (v == "breton") return "Breton";
            if (v == "redguard") return "Redguard";
            if (v == "nord") return "Nord";
            if (v == "darkelf" || v == "dunmer") return "DarkElf";
            if (v == "highelf" || v == "altmer") return "HighElf";
            if (v == "woodelf" || v == "bosmer") return "WoodElf";
            if (v == "khajiit") return "Khajiit";
            if (v == "argonian") return "Argonian";
            if (v == "orc" || v == "orsimer") return "Orc";
            if (v == "dragon") return "Dragon";
            return string.Empty;
        }

        private static string NormalizeTestGender(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string v = value.Trim().ToLowerInvariant();
            if (v == "m" || v == "male") return "Male";
            if (v == "f" || v == "female") return "Female";
            return string.Empty;
        }

        private static string NormalizeTestRole(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string v = value.Trim().Replace("-", string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
            if (v == "commoner") return "Commoner";
            if (v == "merchant") return "Merchant";
            if (v == "scholar") return "Scholar";
            if (v == "noble") return "Noble";
            if (v == "judge" || v == "magistrate" || v == "magistrates") return "Judge";
            if (v == "underworld" || v == "criminal") return "Underworld";
            if (v == "supernatural") return "Supernatural";
            if (v == "guild") return "Guild";
            if (v == "child" || v == "kid") return "Child";
            return string.Empty;
        }

        private static string ConsoleVoiceInstall(params string[] args)
        {
            if (instance == null) return "NPCVO is not initialized.";
            instance.StartCoroutine(instance.EnsureEnglishVoiceLibraryRoutine(true));
            return "NPCVO requested installation/verification of all supported English Kokoro voices. See Player.log or npcvo_voice_status.";
        }

        private static string ConsoleVoiceStatus(params string[] args)
        {
            if (instance == null) return "NPCVO is not initialized.";
            instance.StartCoroutine(instance.QueryEnglishVoiceLibraryRoutine());
            return "NPCVO requested Kokoro voice-library status. Current cached status: " + instance.lastVoiceLibraryStatus;
        }

        private static string ConsoleQuestDebug(params string[] args)
        {
            if (instance == null) return "NPCVO is not initialized.";

            // The quest parchment is modal and blocks opening DFU's console. Return the last snapshot
            // captured automatically while a DaggerfallMessageBox was actually on screen.
            return "Last captured quest/message-box state: " + instance.lastQuestDebugSnapshot;
        }

        private static string ConsoleStop(params string[] args)
        {
            if (instance == null) return "NPCVO is not initialized.";
            instance.StopSpeech();
            return "NPCVO speech stopped.";
        }

        private static string ConsoleReload(params string[] args)
        {
            if (instance == null) return "NPCVO is not initialized.";
            instance.LoadAllConfiguration();
            return "NPCVO configuration reloaded.";
        }

        private static string ConsoleLineInfo(params string[] args)
        {
            if (instance == null) return "NPCVO is not initialized.";
            if (string.IsNullOrEmpty(instance.lastVoicePackHash)) return "No NPC line has been captured yet.";
            string folder = instance.lastIdentity == null ? "Unknown_NPC" : SanitizePathComponent(instance.lastIdentity.Name);
            return "NPC=" + (instance.lastIdentity == null ? "Unknown" : instance.lastIdentity.Name) +
                " | Script=\"" + instance.lastVoicePackScript + "\" | Hash=" + instance.lastVoicePackHash +
                " | WAV=Sound/NPCVO/VoicePack/" + folder + "/" + instance.lastVoicePackHash + ".wav";
        }

        private static string ConsoleClearCache(params string[] args)
        {
            if (instance == null) return "NPCVO is not initialized.";
            instance.StopSpeech();
            int count = 0; long bytes = 0;
            try
            {
                foreach (string path in Directory.GetFiles(instance.cacheDir, "*.wav"))
                {
                    FileInfo f = new FileInfo(path); bytes += f.Length; f.Delete(); count++;
                }
            }
            catch { }
            return "Deleted " + count + " runtime WAV(s), " + FormatBytes(bytes) + ". VoicePack files were not touched.";
        }

        private static string ConsoleClearAssignments(params string[] args)
        {
            if (instance == null) return "NPCVO is not initialized.";
            int count = instance.voiceAssignmentsByKey == null ? 0 : instance.voiceAssignmentsByKey.Count;
            if (instance.voiceAssignmentsByKey != null)
                instance.voiceAssignmentsByKey.Clear();
            instance.SaveVoiceAssignments();
            instance.lastAssignmentStatus = "persistent assignments cleared";
            return "Cleared " + count + " persistent NPC voice assignment(s). Procedural NPCs will be recast on next resolution.";
        }

        private static int ProfileCount(VoiceProfileCollection collection)
        {
            return collection == null || collection.profiles == null ? 0 : collection.profiles.Length;
        }

        private string GetCacheSizeText()
        {
            long bytes = 0; int count = 0;
            try
            {
                foreach (string path in Directory.GetFiles(cacheDir, "*.wav")) { bytes += new FileInfo(path).Length; count++; }
            }
            catch { }
            string limit = config.MaxCacheSizeMB <= 0 ? "Unlimited" : config.MaxCacheSizeMB + " MB";
            return FormatBytes(bytes) + " / " + limit + " (" + count + " files)";
        }

        private static object ReadMember(object obj, string name)
        {
            if (obj == null || string.IsNullOrEmpty(name)) return null;
            Type t = obj.GetType();
            try
            {
                PropertyInfo p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                if (p != null) return p.GetValue(obj, null);
                FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                if (f != null) return f.GetValue(obj);
            }
            catch { }
            return null;
        }

        private static string CleanEnumName(object value, string fallback)
        {
            if (value == null) return fallback;
            string s = value.ToString();
            return string.IsNullOrWhiteSpace(s) ? fallback : Regex.Replace(s, @"[^A-Za-z0-9]", string.Empty);
        }

        private static int ToInt(object value, int fallback)
        {
            if (value == null)
                return fallback;
            try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static string Stringify(object value)
        {
            return value == null ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static bool LooksLikeJudgeIdentity(string name, string npcType, string roleHint)
        {
            string combined = ((name ?? string.Empty) + " " + (npcType ?? string.Empty) + " " + (roleHint ?? string.Empty)).ToLowerInvariant();
            return combined.Contains("judge") || combined.Contains("magistrate") || combined.Contains("magistracy");
        }

        private static string GetNpcDisplayTitle(NPCIdentity id)
        {
            if (id == null) return string.Empty;
            if (string.Equals(id.Role, "Judge", StringComparison.OrdinalIgnoreCase)) return "Judge";

            string name = id.Name ?? string.Empty;
            string[] significant = { "Emperor", "Empress", "King", "Queen", "Prince", "Princess", "Duke", "Duchess", "Count", "Countess", "Baron", "Baroness", "Lord", "Lady" };
            for (int i = 0; i < significant.Length; i++)
            {
                // Important vanilla NPCs normally carry the formal title in DisplayName itself
                // (for example "King Gothryd"). The name is already shown beneath the portrait,
                // so do not repeat the same title on a second line.
                if (name.IndexOf(significant[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return string.Empty;
            }

            if (string.Equals(id.Role, "Merchant", StringComparison.OrdinalIgnoreCase))
            {
                string building = GetCurrentBuildingTypeName();
                string b = building.ToLowerInvariant();
                if (b.Contains("tavern")) return "Innkeeper";
                if (b.Contains("alchemist")) return "Alchemist";
                if (b.Contains("book")) return "Bookseller";
                if (b.Contains("armor")) return "Armorer";
                if (b.Contains("weapon")) return "Weaponsmith";
                if (b.Contains("pawn")) return "Pawnbroker";
                if (b.Contains("bank")) return "Banker";
                if (b.Contains("general")) return "Shopkeeper";
                return "Merchant";
            }

            // Broad inferred social classes are useful for VoiceTypes but too generic for UI bios.
            // Show no title rather than inventing social status that the game did not explicitly expose.
            return string.Empty;
        }

        private static string GetCurrentBuildingTypeName()
        {
            try
            {
                object enterExit = GameManager.Instance == null ? null : GameManager.Instance.PlayerEnterExit;
                object data = ReadMember(enterExit, "BuildingDiscoveryData");
                object type = ReadMember(data, "buildingType") ?? ReadMember(data, "BuildingType");
                return type == null ? string.Empty : type.ToString();
            }
            catch { return string.Empty; }
        }

        private static string NormalizeRole(string socialGroup)
        {
            string s = (socialGroup ?? string.Empty).ToLowerInvariant();
            if (s.Contains("merchant") || s.Contains("trader")) return "Merchant";
            if (s.Contains("judge") || s.Contains("magistr")) return "Judge";
            if (s.Contains("scholar") || s.Contains("academic")) return "Scholar";
            if (s.Contains("nobil") || s.Contains("arist") || s.Contains("court")) return "Noble";
            if (s.Contains("underworld") || s.Contains("criminal") || s.Contains("thief")) return "Underworld";
            if (s.Contains("supernatural")) return "Supernatural";
            if (s.Contains("guild") || s.Contains("military") || s.Contains("knight") || s.Contains("temple")) return "Guild";
            return "Commoner";
        }

        private static string NormalizeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            text = text.Replace("\r", " ").Replace("\n", " ").Replace("<--->", " ");
            text = Regex.Replace(text, @"\s+", " ");
            return text.Trim();
        }

        private static string PolishNpcText(string text)
        {
            string result = NormalizeText(text);
            if (string.IsNullOrEmpty(result)) return result;

            // Conservative grammar/delivery pass. This intentionally avoids paraphrasing lore text.
            // It corrects spacing/punctuation and a very small set of known vanilla typos while the
            // original line remains untouched for quest logic and VoicePack hashing.
            result = Regex.Replace(result, @"\s+([,.;:!?])", "$1");
            result = Regex.Replace(result, @"([,;:!?])(?=[\p{L}\p{N}])", "$1 ");
            result = Regex.Replace(result, @"(?<!\.)\.(?=[\p{L}\p{N}])", ". ");
            result = Regex.Replace(result, @"\.{2,}", "...");
            result = Regex.Replace(result, @"(?i)\byou much search\b", "you must search");
            result = Regex.Replace(result, @"(?i)\bpreformed well\b", "performed well");
            result = Regex.Replace(result, @"(?i)\bin in the\b", "in the");
            result = Regex.Replace(result, @"\s{2,}", " ").Trim();

            string[] discourse = new string[] { "Well", "Yes", "No", "Ah", "Hmm", "Hmmm", "Indeed" };
            for (int i = 0; i < discourse.Length; i++)
            {
                string word = discourse[i];
                if (result.StartsWith(word + " ", StringComparison.OrdinalIgnoreCase))
                {
                    result = result.Substring(0, word.Length) + "," + result.Substring(word.Length);
                    break;
                }
            }

            result = CapitalizeSentenceStarts(result);
            char last = result.Length == 0 ? '\0' : result[result.Length - 1];
            if (result.Length > 2 && last != '.' && last != '!' && last != '?' && last != ':' && last != ';' && last != '"' && last != '\'')
                result += ".";
            return result;
        }

        private static string CapitalizeSentenceStarts(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            StringBuilder sb = new StringBuilder(text.Length);
            bool capitalize = true;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (capitalize && char.IsLetter(c))
                {
                    c = char.ToUpperInvariant(c);
                    capitalize = false;
                }
                sb.Append(c);
                if (c == '.' || c == '!' || c == '?') capitalize = true;
                else if (!char.IsWhiteSpace(c) && c != '"' && c != '\'') capitalize = false;
            }
            return sb.ToString();
        }

        private static string NormalizeForMatch(string text)
        {
            text = NormalizeText(text).ToLowerInvariant();
            text = Regex.Replace(text, @"[^\p{L}\p{N}']+", " ");
            return Regex.Replace(text, @"\s+", " ").Trim();
        }

        private static string SanitizePathComponent(string value)
        {
            string s = string.IsNullOrWhiteSpace(value) ? "Unknown_NPC" : value.Trim();
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            s = Regex.Replace(s, @"\s+", "_");
            return s;
        }

        private static string JsonEscape(string value)
        {
            if (value == null) return string.Empty;
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }

        private static string Sha1(string input)
        {
            using (SHA1 sha1 = SHA1.Create())
            {
                byte[] data = sha1.ComputeHash(Encoding.UTF8.GetBytes(input ?? string.Empty));
                return BitConverter.ToString(data).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static uint Fnv1a32(string text)
        {
            unchecked
            {
                uint hash = 2166136261u;
                byte[] data = Encoding.UTF8.GetBytes(text ?? string.Empty);
                for (int i = 0; i < data.Length; i++) { hash ^= data[i]; hash *= 16777619u; }
                return hash;
            }
        }

        private static string FormatBytes(long value)
        {
            if (value >= 1024L * 1024L * 1024L) return (value / (1024d * 1024d * 1024d)).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
            if (value >= 1024L * 1024L) return (value / (1024d * 1024d)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            if (value >= 1024L) return (value / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            return value + " B";
        }
    }

    internal enum VariationMode { Off, Subtle, Moderate }
    internal enum SpeechSourceMode { Hybrid, KokoroOnly, PreGeneratedOnly }
    internal enum FantasyProcessingStrength { Subtle, Default, Strong }
    internal enum EmotionProcessingStrength { Subtle, Default, Strong }
    internal enum PortraitAudioBehavior { Ignore, Defer, Skip }

    [Serializable]
    internal class VoiceProfileCollection
    {
        public int schemaVersion = 0;
        public VoiceProfile[] profiles = new VoiceProfile[0];
    }

    [Serializable]
    internal class VoiceProfile
    {
        public string key = "";
        public string[] voices = new string[0];
        public float speed = 1f;
        public float pitch = 0f;
        public float gravel = 0f;
        public float saturation = 0f;
        public float presence = 0f;
        public float compression = 0f;
        public float doubleMix = 0f;
        public float doublePitch = 0f;
        public float doubleDelayMs = 0f;
        public float spectral = 0f;
        public float reverb = 0f;
        public float subharmonicMix = 0f;
        public float subharmonicPitch = -5.5f;
        public float hiss = 0f;
        public float throatResonance = 0f;
        public float flutterDepth = 0f;
        public float flutterRate = 0f;
        public float croak = 0f;
        public float purrMix = 0f;
        public float purrRate = 0f;
        public float felineResonance = 0f;
        public float growl = 0f;
        public float breath = 0f;
        public string defaultEmotion = "Neutral";
        public bool lockVariation = false;
        public bool enabled = true;
    }

    [Serializable]
    internal class VoiceAssignmentCollection
    {
        public int schemaVersion = 1;
        public VoiceAssignment[] assignments = new VoiceAssignment[0];
    }

    [Serializable]
    internal class VoiceAssignment
    {
        public string identityKey = "";
        public string displayName = "";
        public string profileKey = "";
        public string voiceId = "";
        public float pitchUnit = 0f;
        public float speedUnit = 0f;
    }

    internal class DeferredTalkAnswer
    {
        public string Line;
        public ListBox.ListItem Item;
        public string DisplayText;
        public DeferredTalkAnswer(string line, ListBox.ListItem item, string displayText)
        {
            Line = line ?? string.Empty;
            Item = item;
            DisplayText = displayText ?? string.Empty;
        }
    }

    internal class NPCIdentity
    {
        public string Name = "Unknown NPC";
        public string NpcType = "Unknown";
        public string Race = "Unknown";
        public string Gender = "Unknown";
        public string Role = "Commoner";
        public string GuildGroup = "";
        public string Faction = "";
        public string MapId = "";
        public string LocationId = "";
        public string StaticHash = "";
        public string PortraitKey = "";
        public bool IsChild = false;
        public string AssignmentKey = "unknown";
        public string StableKey = "unknown";

        public string ToDisplayString()
        {
            return "NPC=" + Name + " | Race=" + Race + " | Gender=" + Gender + " | Role=" + Role +
                (string.IsNullOrEmpty(PortraitKey) ? "" : " | Portrait=" + PortraitKey) +
                (string.IsNullOrEmpty(AssignmentKey) ? "" : " | Assignment=" + AssignmentKey);
        }
    }

    internal class ResolvedVoice
    {
        public string VoiceId;
        public float Speed;
        public float PitchSemitones;
        public float Gravel;
        public float Saturation;
        public float Presence;
        public float Compression;
        public float DoubleMix;
        public float DoublePitchSemitones;
        public float DoubleDelayMs;
        public float Spectral;
        public float Reverb;
        public float SubharmonicMix;
        public float SubharmonicPitch;
        public float Hiss;
        public float ThroatResonance;
        public float FlutterDepth;
        public float FlutterRate;
        public float Croak;
        public float PurrMix;
        public float PurrRate;
        public float FelineResonance;
        public float Growl;
        public float Breath;
        public string DefaultEmotion;
        public string Emotion;
        public string ProfileKey;
        public string Source;
    }

    internal class SpeechRequest
    {
        public string OriginalLine;
        public string Line;
        public string DisplayText;
        public ListBox.ListItem Item;
        public NPCIdentity Identity;
        public ResolvedVoice Voice;
        public string Emotion;
        public string PreGeneratedPath;
        public bool ForceKokoro;
        public ProgressiveDialogueState ProgressiveState;
        public QuestOfferPresentationState QuestPresentation;
    }

    internal class QuestHiddenComponentState
    {
        public BaseScreenComponent Component;
        public bool WasEnabled;
    }

    internal class TalkConversationPresentationState
    {
        public object Window;
        public Panel MainPanel;
        public Panel PlayerPortraitFrame;
        public Panel PlayerPortraitPanel;
        public TextLabel PlayerBioLabel;
        public Panel TonePanel;
        public float PlayerPortraitSize;
        public TextLabel NpcBioLabel;
        public TextLabel PlayerQuestionLabel;
        public Vector2 OriginalQuestionPosition;
        public Vector2 OriginalQuestionSize;
        public int OriginalQuestionMaxWidth;
        public Vector2 OriginalMainPanelPosition;
        public Vector2 OriginalMainPanelSize;
        public string LastPlayerQuestion = string.Empty;
    }

    internal static class ConversationLayout
    {
        // Quest parchment uses a wider decorative right edge than its content panel implies. A small
        // explicit inset visually mirrors the NPC portrait's left-side breathing room.
        public const float QuestPlayerRightInset = 18f;
        public const float TalkEdgeInset = 8f;

        public static float GetQuestPlayerPortraitX(float contentWidth, float frameWidth)
        {
            return Mathf.Max(0f, contentWidth - frameWidth - QuestPlayerRightInset);
        }

        public static Vector2 GetTalkPlayerPortraitPosition(Panel mainPanel, TextLabel playerQuestion, Panel tonePanel, float playerSize)
        {
            if (mainPanel == null) return Vector2.zero;
            float frameSize = playerSize + 2f;
            float x = Mathf.Max(TalkEdgeInset, mainPanel.Size.x - frameSize - TalkEdgeInset);
            float y = playerQuestion == null ? 54f : Mathf.Max(TalkEdgeInset, playerQuestion.Position.y - playerSize - 4f);

            if (tonePanel != null && tonePanel.Size.x > 0f && tonePanel.Size.y > 0f)
            {
                x = tonePanel.Position.x + tonePanel.Size.x - frameSize - 4f;
                y = tonePanel.Position.y + tonePanel.Size.y + 4f;
            }

            x = Mathf.Clamp(x, TalkEdgeInset, Mathf.Max(TalkEdgeInset, mainPanel.Size.x - frameSize - TalkEdgeInset));
            y = Mathf.Clamp(y, TalkEdgeInset, Mathf.Max(TalkEdgeInset, mainPanel.Size.y - frameSize - 18f));
            return new Vector2(x, y);
        }

        public static int GetQuestionMaxWidth(int originalMaxWidth, float questionX, float portraitX)
        {
            int available = Mathf.RoundToInt(Mathf.Max(80f, portraitX - questionX - 8f));
            if (originalMaxWidth <= 0) return available;
            return Mathf.Min(originalMaxWidth, available);
        }
    }

    internal class QuestOfferPresentationState
    {
        public object OwnerWindow;
        public object DisplayWindow;
        public Panel HostPanel;
        public Panel ContentPanel;
        public Panel PortraitPanel;
        public TextLabel NameLabel;
        public TextLabel DialogueLabel;
        public Panel PlayerPortraitFrame;
        public Panel PlayerPortraitPanel;
        public TextLabel PlayerNameLabel;
        public TextLabel PlayerDialogueLabel;
        public BaseScreenComponent NativeTextComponent;
        public bool NativeTextWasEnabled;
        public readonly List<QuestHiddenComponentState> HiddenTextComponents = new List<QuestHiddenComponentState>();
        public Panel ButtonPanel;
        public Panel ChoiceProxyPanel;
        public readonly List<Button> ChoiceProxyButtons = new List<Button>();
        public bool NativeButtonPanelStateCaptured;
        public bool NativeButtonPanelWasEnabled;
        public Vector2 OriginalHostSize;
        public Vector2 OriginalButtonPosition;
        public string FullText;
        public Texture2D PortraitTexture;
    }

    internal sealed class SuppressedTextLabel : TextLabel
    {
        public SuppressedTextLabel(TextLabel source) : base(source == null ? null : source.Font)
        {
            if (source == null)
                return;

            MaxCharacters = source.MaxCharacters;
            MaxWidth = source.MaxWidth;
            WrapText = source.WrapText;
            WrapWords = source.WrapWords;
            TextScale = source.TextScale;
            HorizontalAlignment = source.HorizontalAlignment;
            VerticalAlignment = source.VerticalAlignment;
            HorizontalTextAlignment = source.HorizontalTextAlignment;
            ShadowPosition = source.ShadowPosition;
            Position = source.Position;
            Text = source.Text ?? string.Empty;
        }

        public override void Draw()
        {
            // Keep the ListBox row's text/layout data intact while drawing nothing.
            // NPCVO's separate overlay is the only visible line during speech.
        }
    }

    internal class ProgressiveDialogueState
    {
        public ListBox.ListItem Item;
        public string FullText;
        public TextLabel OriginalLabel;
        public TextLabel SuppressedLabel;
        public TextLabel Overlay;
        public Panel OverlayParent;
        public Color TextColor;
        public Color DisabledTextColor;
        public Color SelectedTextColor;
        public Color ShadowColor;
        public Color SelectedShadowColor;
        public Color HighlightedTextColor;
        public Color HighlightedDisabledTextColor;
        public Color HighlightedSelectedTextColor;
    }

    internal class NPCVOConfig
    {
        public bool Enabled = true;
        public bool InterruptOnNewResponse = true;
        public bool StopWhenTalkWindowCloses = true;
        public bool RevealNpcTextWithSpeech = true;
        public bool NarrateQuestGiverWindows = true;
        public bool NarrateCourtJudge = true;
        public bool QuestGiverPortraitUI = true;
        public bool PlayerAskConversationUI = true;
        public VariationMode Variation = VariationMode.Subtle;
        public SpeechSourceMode SpeechSource = SpeechSourceMode.Hybrid;
        public bool UseRoleVoiceTypes = true;
        public FantasyProcessingStrength FantasyProcessing = FantasyProcessingStrength.Default;
        public bool IgnorePlayerNameInVoicePackKeys = true;
        public bool EmotionLayer = true;
        public EmotionProcessingStrength EmotionStrength = EmotionProcessingStrength.Default;
        public bool GrammarPolish = true;
        public bool DisplayPolishedText = true;
        public bool EnsureEnglishVoiceLibrary = true;
        public bool DynamicPortraitsIntegration = true;
        public bool DynamicPortraitEmotionSync = true;
        public PortraitAudioBehavior PortraitAudioBehavior = PortraitAudioBehavior.Defer;
        public int Volume = 90;
        public bool UseGameSoundVolume = true;
        public string AudioStyle = "clean";
        public string VoiceOverride = string.Empty;
        public int MaxCacheSizeMB = 1024;
        public int KokoroPort = 5000;
        public bool AutoStartVoiceEngine = true;
        public int HttpTimeoutSeconds = 30;

        public static NPCVOConfig LoadOrCreate(string path)
        {
            NPCVOConfig cfg = new NPCVOConfig();
            if (!File.Exists(path))
            {
                cfg.Save(path);
                return cfg;
            }

            try
            {
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";") || line.StartsWith("[")) continue;
                    int eq = line.IndexOf('=');
                    if (eq < 1) continue;
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string value = line.Substring(eq + 1).Trim();
                    int i;
                    bool b;
                    if (key == "kokoroport" && int.TryParse(value, out i)) cfg.KokoroPort = Mathf.Clamp(i, 1, 65535);
                    else if (key == "voiceoverride") cfg.VoiceOverride = value;
                    else if (key == "autostartvoiceengine" && bool.TryParse(value, out b)) cfg.AutoStartVoiceEngine = b;
                    else if (key == "httptimeoutseconds" && int.TryParse(value, out i)) cfg.HttpTimeoutSeconds = Mathf.Clamp(i, 2, 120);
                }
            }
            catch { }
            return cfg;
        }

        public void Save(string path)
        {
            File.WriteAllText(path,
                "# NPCVO advanced settings. Common settings are under Mods > NPCVO > Settings.\r\n" +
                "# NPCVO intentionally shares the same Kokoro service as Daggerfall Narrator.\r\n" +
                "KokoroPort=" + KokoroPort + "\r\n" +
                "VoiceOverride=" + (VoiceOverride ?? string.Empty) + "\r\n" +
                "AutoStartVoiceEngine=" + AutoStartVoiceEngine + "\r\n" +
                "HttpTimeoutSeconds=" + HttpTimeoutSeconds + "\r\n");
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
