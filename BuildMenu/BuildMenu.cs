using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Jotunn.Managers;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BuildMenu
{
    [BepInPlugin(ModGuid, ModName, ModVersion)]
    public class BuildMenuPlugin : BaseUnityPlugin
    {
        public const string ModGuid = "com.paradoxpoint.valheim.buildmenufilter";
        public const string ModName = "Build Menu";
        public const string ModVersion = "0.1.0";

        internal static BuildMenuPlugin Instance;
        internal static Harmony Harmony;

        private ConfigEntry<bool> _enabled;
        private ConfigEntry<string> _defaultPrimary;
        private ConfigEntry<string> _defaultSecondary;
        private ConfigEntry<bool> _logUnknownPieces;
        private ConfigEntry<bool> _debugLogging;
        private ConfigEntry<KeyboardShortcut> _dumpLibraryShortcut;
        private ConfigEntry<KeyboardShortcut> _primaryUpShortcut;
        private ConfigEntry<KeyboardShortcut> _primaryDownShortcut;
        private ConfigEntry<KeyboardShortcut> _secondaryLeftShortcut;
        private ConfigEntry<KeyboardShortcut> _secondaryRightShortcut;
        private ConfigEntry<KeyboardShortcut> _pagePrevShortcut;
        private ConfigEntry<KeyboardShortcut> _pageNextShortcut;
        private ConfigEntry<float> _uiOffsetLeft;
        private ConfigEntry<float> _uiOffsetTop;
        private bool _validatedConfiguredPrefabs;

        internal readonly PieceClassificationRegistry Registry = new PieceClassificationRegistry();
        internal readonly HashSet<string> ConfiguredPrefabs = new(StringComparer.OrdinalIgnoreCase);
        internal ManualLogSource Log => Logger;

        private void Awake()
        {
            Instance = this;

            _enabled = Config.Bind("General", "Enabled", true, "Enable the build menu filter UI.");
            _defaultPrimary = Config.Bind("General", "DefaultPrimaryCategory", "All", "Default left-side category.");
            _defaultSecondary = Config.Bind("General", "DefaultSecondaryCategory", "All", "Default top category.");
            _logUnknownPieces = Config.Bind("Debug", "LogUnknownPieces", false, "Log pieces that do not have a classification.");
            _debugLogging = Config.Bind("Debug", "VerboseLogging", true, "Emit diagnostic logging for UI and piece filtering.");
            _dumpLibraryShortcut = Config.Bind("Debug", "DumpLibraryShortcut", new KeyboardShortcut(KeyCode.F8), "Dump the full build piece library to JSON.");
            _primaryUpShortcut = Config.Bind("Input", "PrimaryUpShortcut", new KeyboardShortcut(KeyCode.W), "Select previous primary category.");
            _primaryDownShortcut = Config.Bind("Input", "PrimaryDownShortcut", new KeyboardShortcut(KeyCode.S), "Select next primary category.");
            _secondaryLeftShortcut = Config.Bind("Input", "SecondaryLeftShortcut", new KeyboardShortcut(KeyCode.A), "Select previous secondary category.");
            _secondaryRightShortcut = Config.Bind("Input", "SecondaryRightShortcut", new KeyboardShortcut(KeyCode.D), "Select next secondary category.");
            _pagePrevShortcut = Config.Bind("Input", "PagePrevShortcut", new KeyboardShortcut(KeyCode.Q), "Go to previous page.");
            _pageNextShortcut = Config.Bind("Input", "PageNextShortcut", new KeyboardShortcut(KeyCode.E), "Go to next page.");
            _uiOffsetLeft = Config.Bind("Layout", "UiOffsetLeft", 24f, "Left offset in pixels from the top-left of the build HUD.");
            _uiOffsetTop = Config.Bind("Layout", "UiOffsetTop", 124f, "Top offset in pixels from the top-left of the build HUD.");

            LoadClassifications();

            Harmony = new Harmony(ModGuid);
            Harmony.PatchAll();

            Logger.LogInfo($"{ModName} {ModVersion} loaded");
        }

        private void OnDestroy()
        {
            Harmony?.UnpatchSelf();
        }

        internal bool IsEnabled() => _enabled.Value;
        internal string GetDefaultPrimary() => _defaultPrimary.Value;
        internal string GetDefaultSecondary() => _defaultSecondary.Value;
        internal bool ShouldLogUnknownPieces() => _logUnknownPieces.Value;
        internal bool ShouldDebugLog() => _debugLogging.Value;
        internal KeyboardShortcut GetDumpLibraryShortcut() => _dumpLibraryShortcut.Value;
        internal KeyboardShortcut GetPrimaryUpShortcut() => _primaryUpShortcut.Value;
        internal KeyboardShortcut GetPrimaryDownShortcut() => _primaryDownShortcut.Value;
        internal KeyboardShortcut GetSecondaryLeftShortcut() => _secondaryLeftShortcut.Value;
        internal KeyboardShortcut GetSecondaryRightShortcut() => _secondaryRightShortcut.Value;
        internal KeyboardShortcut GetPagePrevShortcut() => _pagePrevShortcut.Value;
        internal KeyboardShortcut GetPageNextShortcut() => _pageNextShortcut.Value;
        internal float GetUiOffsetLeft() => _uiOffsetLeft.Value;
        internal float GetUiOffsetTop() => _uiOffsetTop.Value;
        internal bool HasValidatedConfiguredPrefabs() => _validatedConfiguredPrefabs;
        internal void MarkConfiguredPrefabsValidated() => _validatedConfiguredPrefabs = true;
        internal void DebugLog(string message)
        {
            if (_debugLogging.Value)
            {
                Logger.LogInfo($"[Debug] {message}");
            }
        }

        private void LoadClassifications()
        {
            Registry.Clear();
            ConfiguredPrefabs.Clear();
            var categoryMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            var path = Path.Combine(Paths.ConfigPath, "BuildMenuClassification.json");
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Build menu classification file could not be found: {path}", path);
            }

            BuildMenuClassificationFile root;
            var serializer = new DataContractJsonSerializer(
                typeof(BuildMenuClassificationFile),
                new DataContractJsonSerializerSettings
                {
                    UseSimpleDictionaryFormat = true
                });
            using (var stream = File.OpenRead(path))
            {
                root = serializer.ReadObject(stream) as BuildMenuClassificationFile;
            }

            if (root == null)
            {
                throw new InvalidDataException($"Build menu classification file could not be parsed: {path}");
            }

            var exactCount = 0;
            foreach (var secondaryEntry in root.Exact ?? new Dictionary<string, Dictionary<string, List<ExactClassificationEntry>>>())
            {
                RegisterCategoryMapping(categoryMap, secondaryEntry.Key, secondaryEntry.Value?.Keys);
                foreach (var primaryEntry in secondaryEntry.Value ?? new Dictionary<string, List<ExactClassificationEntry>>())
                {
                    foreach (var entry in primaryEntry.Value ?? new List<ExactClassificationEntry>())
                    {
                        if (!string.IsNullOrWhiteSpace(entry?.Prefab))
                        {
                            Registry.Register(entry.Prefab, secondaryEntry.Key, primaryEntry.Key);
                            ConfiguredPrefabs.Add(entry.Prefab);
                            exactCount++;
                        }
                    }
                }
            }

            var containsCount = 0;
            foreach (var secondaryEntry in root.Contains ?? new Dictionary<string, Dictionary<string, List<ContainsClassificationEntry>>>())
            {
                RegisterCategoryMapping(categoryMap, secondaryEntry.Key, secondaryEntry.Value?.Keys);
                foreach (var primaryEntry in secondaryEntry.Value ?? new Dictionary<string, List<ContainsClassificationEntry>>())
                {
                    foreach (var entry in primaryEntry.Value ?? new List<ContainsClassificationEntry>())
                    {
                        if (!string.IsNullOrWhiteSpace(entry?.Match))
                        {
                            Registry.RegisterContains(entry.Match, secondaryEntry.Key, primaryEntry.Key);
                            containsCount++;
                        }
                    }
                }
            }

            BuildMenuState.ConfigureCategories(categoryMap);
            DebugLog($"Loaded classifications from {path}. exact={exactCount}, contains={containsCount}");
        }

        private static void RegisterCategoryMapping(IDictionary<string, List<string>> categoryMap, string primary, IEnumerable<string> secondaryKeys)
        {
            if (string.IsNullOrWhiteSpace(primary))
            {
                return;
            }

            if (!categoryMap.TryGetValue(primary, out var secondaries))
            {
                secondaries = new List<string>();
                categoryMap[primary] = secondaries;
            }

            foreach (var secondary in secondaryKeys ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(secondary) && !secondaries.Contains(secondary))
                {
                    secondaries.Add(secondary);
                }
            }
        }
    }

    [DataContract]
    internal sealed class BuildMenuClassificationFile
    {
        [DataMember(Name = "exact")]
        public Dictionary<string, Dictionary<string, List<ExactClassificationEntry>>> Exact { get; set; }

        [DataMember(Name = "contains")]
        public Dictionary<string, Dictionary<string, List<ContainsClassificationEntry>>> Contains { get; set; }
    }

    [DataContract]
    internal sealed class ExactClassificationEntry
    {
        [DataMember(Name = "Prefab")]
        public string Prefab { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }
    }

    [DataContract]
    internal sealed class ContainsClassificationEntry
    {
        [DataMember(Name = "match")]
        public string Match { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }
    }

    [DataContract]
    internal sealed class BuildPieceDumpFile
    {
        [DataMember(Name = "pieces")]
        public List<BuildPieceDumpEntry> Pieces { get; set; }
    }

    [DataContract]
    internal sealed class BuildPieceDumpEntry
    {
        [DataMember(Name = "Prefab")]
        public string Prefab { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "token")]
        public string Token { get; set; }

        [DataMember(Name = "pieceTable")]
        public string PieceTable { get; set; }

        [DataMember(Name = "category")]
        public string Category { get; set; }

        [DataMember(Name = "craftingStation")]
        public string CraftingStation { get; set; }

        [DataMember(Name = "systemEffects")]
        public List<string> SystemEffects { get; set; }

        [DataMember(Name = "interactionHooks")]
        public List<string> InteractionHooks { get; set; }

        [DataMember(Name = "required")]
        public List<BuildPieceRequirementDumpEntry> Required { get; set; }
    }

    [DataContract]
    internal sealed class BuildPieceRequirementDumpEntry
    {
        [DataMember(Name = "required")]
        public string Required { get; set; }

        [DataMember(Name = "amount")]
        public int Amount { get; set; }
    }

    internal static class BuildMenuState
    {
        public const string All = "All";
        public const int PageSize = 90;

        public static string SelectedPrimary = All;
        public static string SelectedSecondary = All;
        public static bool Initialized;
        public static int CurrentPage;

        public static readonly List<string> PrimaryCategories = new();
        public static readonly Dictionary<string, List<string>> SecondaryCategories = new(StringComparer.OrdinalIgnoreCase);

        public static void ConfigureCategories(IReadOnlyDictionary<string, List<string>> categoryMap)
        {
            PrimaryCategories.Clear();
            SecondaryCategories.Clear();

            PrimaryCategories.Add(All);

            var allSecondary = new List<string> { All };
            foreach (var entry in categoryMap)
            {
                if (!PrimaryCategories.Contains(entry.Key))
                {
                    PrimaryCategories.Add(entry.Key);
                }

                var secondary = new List<string> { All };
                foreach (var value in entry.Value ?? Enumerable.Empty<string>())
                {
                    if (!secondary.Contains(value))
                    {
                        secondary.Add(value);
                    }

                    if (!allSecondary.Contains(value))
                    {
                        allSecondary.Add(value);
                    }
                }

                SecondaryCategories[entry.Key] = secondary;
            }

            SecondaryCategories[All] = allSecondary;
            Initialized = false;
            EnsureInitialized();
        }

        public static void EnsureInitialized()
        {
            if (Initialized)
            {
                return;
            }

            if (PrimaryCategories.Count == 0)
            {
                PrimaryCategories.Add(All);
            }

            if (!SecondaryCategories.ContainsKey(All))
            {
                SecondaryCategories[All] = new List<string> { All };
            }

            SelectedPrimary = BuildMenuPlugin.Instance.GetDefaultPrimary();
            if (!PrimaryCategories.Contains(SelectedPrimary))
            {
                SelectedPrimary = PrimaryCategories[0];
            }

            SelectedSecondary = BuildMenuPlugin.Instance.GetDefaultSecondary();
            if (!SecondaryCategories.TryGetValue(SelectedPrimary, out var secondary) || !secondary.Contains(SelectedSecondary))
            {
                SelectedSecondary = All;
            }

            CurrentPage = 0;
            Initialized = true;
        }

        public static IReadOnlyList<string> GetSecondaryForSelectedPrimary()
        {
            EnsureInitialized();
            return SecondaryCategories.TryGetValue(SelectedPrimary, out var secondary)
                ? secondary
                : new List<string> { All };
        }

        public static void SetPrimary(string primary)
        {
            EnsureInitialized();
            if (!PrimaryCategories.Contains(primary))
            {
                return;
            }

            SelectedPrimary = primary;
            var allowed = GetSecondaryForSelectedPrimary();
            if (!allowed.Contains(SelectedSecondary))
            {
                SelectedSecondary = All;
            }

            CurrentPage = 0;
        }

        public static void SetSecondary(string secondary)
        {
            EnsureInitialized();
            var allowed = GetSecondaryForSelectedPrimary();
            if (!allowed.Contains(secondary))
            {
                return;
            }

            SelectedSecondary = secondary;
            CurrentPage = 0;
        }

        public static void SetPage(int page)
        {
            CurrentPage = Math.Max(0, page);
        }
    }

    internal sealed class PieceClassificationRegistry
    {
        private readonly Dictionary<string, PieceClassification> _exact = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<ContainsRule> _contains = new();

        public void Register(string pieceName, string primary, string secondary)
        {
            _exact[pieceName] = new PieceClassification(primary, secondary);
        }

        public void RegisterContains(string fragment, string primary, string secondary)
        {
            _contains.Add(new ContainsRule(fragment, new PieceClassification(primary, secondary)));
        }

        public void Clear()
        {
            _exact.Clear();
            _contains.Clear();
        }

        public bool TryGetClassification(GameObject piecePrefab, out PieceClassification classification)
        {
            classification = default;
            if (!piecePrefab)
            {
                return false;
            }

            var stableName = Utils.GetPrefabName(piecePrefab);
            if (!string.IsNullOrEmpty(stableName) && _exact.TryGetValue(stableName, out classification))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(stableName))
            {
                foreach (var rule in _contains)
                {
                    if (stableName.IndexOf(rule.Fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        classification = rule.Classification;
                        return true;
                    }
                }
            }

            return false;
        }

        private struct ContainsRule
        {
            public string Fragment { get; }
            public PieceClassification Classification { get; }

            public ContainsRule(string fragment, PieceClassification classification)
            {
                Fragment = fragment;
                Classification = classification;
            }
        }
    }

    internal struct PieceClassification
    {
        public string Primary { get; }
        public string Secondary { get; }

        public PieceClassification(string primary, string secondary)
        {
            Primary = primary;
            Secondary = secondary;
        }
    }

    internal static class BuildMenuLogic
    {
        public static List<GameObject> Filter(IEnumerable<GameObject> pieces)
        {
            BuildMenuState.EnsureInitialized();

            var output = new List<GameObject>();
            foreach (var piece in pieces)
            {
                if (!piece)
                {
                    continue;
                }

                if (Matches(piece))
                {
                    output.Add(piece);
                }
            }

            return output;
        }

        public static List<Piece> Filter(IEnumerable<Piece> pieces)
        {
            BuildMenuState.EnsureInitialized();

            var output = new List<Piece>();
            foreach (var piece in pieces)
            {
                if (!piece)
                {
                    continue;
                }

                if (Matches(piece.gameObject))
                {
                    output.Add(piece);
                }
            }

            return output;
        }

        public static List<List<Piece>> FilterCategories(IEnumerable<List<Piece>> categories)
        {
            BuildMenuState.EnsureInitialized();

            var output = new List<List<Piece>>();
            foreach (var category in categories)
            {
                output.Add(category == null ? new List<Piece>() : Filter(category));
            }

            return output;
        }

        public static List<List<Piece>> ProjectFilteredToSelectedCategory(IReadOnlyList<List<Piece>> categories, int selectedCategoryIndex)
        {
            BuildMenuState.EnsureInitialized();

            var filtered = FilterCategories(categories);
            var projected = new List<List<Piece>>(filtered.Count);
            var allMatches = new List<Piece>();

            for (var i = 0; i < filtered.Count; ++i)
            {
                var bucket = filtered[i] ?? new List<Piece>();
                projected.Add(new List<Piece>());
                allMatches.AddRange(bucket);
            }

            allMatches = DistinctByPrefab(allMatches);

            var totalPages = Math.Max(1, (int)Math.Ceiling(allMatches.Count / (double)BuildMenuState.PageSize));
            if (BuildMenuState.CurrentPage >= totalPages)
            {
                BuildMenuState.SetPage(totalPages - 1);
            }

            var pageStart = BuildMenuState.CurrentPage * BuildMenuState.PageSize;
            var pagedMatches = allMatches
                .Skip(pageStart)
                .Take(BuildMenuState.PageSize)
                .ToList();

            if (selectedCategoryIndex >= 0 && selectedCategoryIndex < projected.Count)
            {
                projected[selectedCategoryIndex] = pagedMatches;
            }

            return projected;
        }

        public static int GetFilteredPieceCount(IEnumerable<List<Piece>> categories)
        {
            return DistinctByPrefab(FilterCategories(categories).SelectMany(category => category ?? Enumerable.Empty<Piece>())).Count;
        }

        public static int GetFilteredPieceCountForSelection(IEnumerable<List<Piece>> categories, string primary, string secondary)
        {
            BuildMenuState.EnsureInitialized();
            if (categories == null)
            {
                return 0;
            }

            var filtered = new List<Piece>();
            foreach (var category in categories)
            {
                if (category == null)
                {
                    continue;
                }

                foreach (var piece in category)
                {
                    if (piece != null && Matches(piece.gameObject, primary, secondary))
                    {
                        filtered.Add(piece);
                    }
                }
            }

            return DistinctByPrefab(filtered).Count;
        }

        private static List<Piece> DistinctByPrefab(IEnumerable<Piece> pieces)
        {
            var output = new List<Piece>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var piece in pieces)
            {
                if (!piece)
                {
                    continue;
                }

                var prefab = Utils.GetPrefabName(piece.gameObject);
                if (string.IsNullOrWhiteSpace(prefab))
                {
                    continue;
                }

                if (seen.Add(prefab))
                {
                    output.Add(piece);
                }
            }

            return output;
        }

        public static bool Matches(GameObject piece)
        {
            return Matches(piece, BuildMenuState.SelectedPrimary, BuildMenuState.SelectedSecondary);
        }

        private static bool Matches(GameObject piece, string selectedPrimary, string selectedSecondary)
        {
            var registry = BuildMenuPlugin.Instance.Registry;
            if (!registry.TryGetClassification(piece, out var classification))
            {
                if (BuildMenuPlugin.Instance.ShouldLogUnknownPieces())
                {
                    BuildMenuPlugin.Instance.Log.LogInfo($"No classification for piece: {Utils.GetPrefabName(piece)}");
                }

                return string.Equals(selectedPrimary, BuildMenuState.All, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(selectedSecondary, BuildMenuState.All, StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(selectedPrimary, BuildMenuState.All, StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(selectedSecondary, BuildMenuState.All, StringComparison.OrdinalIgnoreCase)
                       || string.Equals(classification.Secondary, selectedSecondary, StringComparison.OrdinalIgnoreCase)
                       || string.Equals(classification.Secondary, BuildMenuState.All, StringComparison.OrdinalIgnoreCase);
            }

            if (!string.Equals(classification.Primary, selectedPrimary, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.Equals(selectedSecondary, BuildMenuState.All, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return string.Equals(classification.Secondary, selectedSecondary, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(classification.Secondary, BuildMenuState.All, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static class BuildMenuUI
    {
        private const string RootName = "BuildMenuRoot";
        private const string PrimaryColumnName = "PrimaryColumn";
        private const string SecondaryRowName = "SecondaryRow";
        private const string PagingRowName = "PagingRow";
        private const float PrimaryButtonHeight = 33f;
        private const float PrimaryPanelWidth = 120f;
        private const float PrimaryLayoutSpacing = 6f;
        private const int PrimaryLayoutPadding = 6;
        private const float SecondaryButtonWidth = 80f;
        private const float SecondaryButtonHeight = 33f;
        private const float SecondaryLayoutSpacing = 6f;
        private const int SecondaryLayoutPadding = 6;
        private const float SecondaryPanelX = 136f;
        private const float SecondaryPanelMinWidth = 280f;

        private static GameObject _root;
        private static GameObject _primaryColumn;
        private static GameObject _secondaryRow;
        private static GameObject _pagingRow;
        private static Hud _hud;
        private static bool? _lastRootActive;
        private static bool? _lastBuildMode;
        private static bool _cursorTemporarilyUnlocked;
        private static CursorLockMode _previousCursorLockMode;
        private static bool _previousCursorVisible;
        private static readonly List<string> _visiblePrimaryCategories = new();
        private static readonly List<string> _visibleSecondaryCategories = new();
        private static readonly Dictionary<string, int> _primaryCounts = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> _secondaryCounts = new(StringComparer.OrdinalIgnoreCase);

        public static void EnsureUI(Hud hud)
        {
            if (!BuildMenuPlugin.Instance.IsEnabled())
            {
                return;
            }

            BuildMenuState.EnsureInitialized();
            _hud = hud;

            if (!_root)
            {
                CreateRoot(hud);
                RebuildPrimaryButtons();
                RebuildSecondaryButtons();
                BuildMenuPlugin.Instance.DebugLog($"UI root created. Primary buttons={BuildMenuState.PrimaryCategories.Count}, secondary buttons={BuildMenuState.GetSecondaryForSelectedPrimary().Count}");
            }

            var inBuildMode = IsInBuildMode(hud);
            if (_lastBuildMode != inBuildMode)
            {
                var player = Player.m_localPlayer;
                var buildHud = Utils.GetHudBuildHud(hud);
                BuildMenuPlugin.Instance.DebugLog($"IsInBuildMode changed to {inBuildMode}. player={(player ? "yes" : "no")}, hud={(hud ? "yes" : "no")}, pieceSelectionVisible={Utils.InvokeHudIsPieceSelectionVisible()}, buildHudActive={(buildHud ? buildHud.activeInHierarchy : false)}, inPlaceMode={(player ? Utils.InvokePlayerInPlaceMode(player) : false)}");
                _lastBuildMode = inBuildMode;
            }

            var wasRootActive = _root.activeSelf;
            _root.SetActive(inBuildMode);
            if (_lastRootActive != _root.activeSelf)
            {
                BuildMenuPlugin.Instance.DebugLog($"UI root active changed to {_root.activeSelf}");
                _lastRootActive = _root.activeSelf;
            }

            if (_root.activeSelf)
            {
                if (!wasRootActive)
                {
                    RefreshVisibleCategories();
                    RebuildPrimaryButtons();
                    RebuildSecondaryButtons();
                }

                ConfigureVanillaTabs();
            }

            RefreshButtonStates();
        }

        public static void DestroyUI()
        {
            if (_root)
            {
                UnityEngine.Object.Destroy(_root);
            }

            _root = null;
            _primaryColumn = null;
            _secondaryRow = null;
            _pagingRow = null;
            _hud = null;
        }

        public static void NotifyPrimaryChanged()
        {
            BuildMenuPlugin.Instance.DebugLog($"Primary changed to {BuildMenuState.SelectedPrimary}");
            RefreshVisibleCategories();
            RebuildPrimaryButtons();
            RebuildSecondaryButtons();
            RefreshButtonStates();
            RefreshPieceSelection();
        }

        public static void NotifySecondaryChanged()
        {
            BuildMenuPlugin.Instance.DebugLog($"Secondary changed to {BuildMenuState.SelectedSecondary}");
            RefreshButtonStates();
            RefreshPieceSelection();
        }

        public static void StepPage(int direction)
        {
            var player = Player.m_localPlayer;
            var buildPieces = player ? Utils.GetBuildPiecesTable(player) : null;
            var categories = buildPieces != null ? BuildMenuPatches.GetSourceAvailablePieces(buildPieces) : null;
            var total = categories != null ? BuildMenuLogic.GetFilteredPieceCount(categories) : 0;
            var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)BuildMenuState.PageSize));
            var nextPage = Math.Max(0, Math.Min(totalPages - 1, BuildMenuState.CurrentPage + direction));
            if (nextPage == BuildMenuState.CurrentPage)
            {
                return;
            }

            BuildMenuState.SetPage(nextPage);
            BuildMenuPlugin.Instance.DebugLog($"Page changed to {BuildMenuState.CurrentPage + 1}/{totalPages}");
            RefreshPagingState();
            RefreshPieceSelection();
        }

        public static void HandleInput()
        {
            if (BuildMenuPlugin.Instance.GetDumpLibraryShortcut().IsDown())
            {
                DumpLibrary();
            }

            if (!_root || !_root.activeSelf)
            {
                RestoreCursor();
                return;
            }

            if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt))
            {
                UnlockCursorTemporarily();
            }
            else
            {
                RestoreCursor();
            }
        }

        public static bool TryHandleNavigationInput()
        {
            if (!_root || !_root.activeSelf)
            {
                return false;
            }

            var primaryUp = BuildMenuPlugin.Instance.GetPrimaryUpShortcut();
            var primaryDown = BuildMenuPlugin.Instance.GetPrimaryDownShortcut();
            var secondaryLeft = BuildMenuPlugin.Instance.GetSecondaryLeftShortcut();
            var secondaryRight = BuildMenuPlugin.Instance.GetSecondaryRightShortcut();
            var pagePrev = BuildMenuPlugin.Instance.GetPagePrevShortcut();
            var pageNext = BuildMenuPlugin.Instance.GetPageNextShortcut();

            var handled = false;
            var action = string.Empty;

            if (primaryUp.IsDown())
            {
                StepPrimary(-1);
                handled = true;
                action = "primary -1";
            }
            else if (primaryDown.IsDown())
            {
                StepPrimary(1);
                handled = true;
                action = "primary +1";
            }

            if (secondaryLeft.IsDown())
            {
                StepSecondary(-1);
                handled = true;
                action = string.IsNullOrEmpty(action) ? "secondary -1" : $"{action}, secondary -1";
            }
            else if (secondaryRight.IsDown())
            {
                StepSecondary(1);
                handled = true;
                action = string.IsNullOrEmpty(action) ? "secondary +1" : $"{action}, secondary +1";
            }

            if (pagePrev.IsDown())
            {
                StepPage(-1);
                handled = true;
                action = string.IsNullOrEmpty(action) ? "page -1" : $"{action}, page -1";
            }
            else if (pageNext.IsDown())
            {
                StepPage(1);
                handled = true;
                action = string.IsNullOrEmpty(action) ? "page +1" : $"{action}, page +1";
            }

            var pressed = IsNavigationInputPressed();
            if (handled || pressed)
            {
                BuildMenuPlugin.Instance.DebugLog($"Navigation input detected. handled={handled}, pressed={pressed}, action={action}");
                Utils.ConsumeNavigationInput();
                return true;
            }

            return false;
        }

        public static bool IsNavigationInputPressed()
        {
            if (!_root || !_root.activeSelf)
            {
                return false;
            }

            var primaryUp = BuildMenuPlugin.Instance.GetPrimaryUpShortcut();
            var primaryDown = BuildMenuPlugin.Instance.GetPrimaryDownShortcut();
            var secondaryLeft = BuildMenuPlugin.Instance.GetSecondaryLeftShortcut();
            var secondaryRight = BuildMenuPlugin.Instance.GetSecondaryRightShortcut();
            var pagePrev = BuildMenuPlugin.Instance.GetPagePrevShortcut();
            var pageNext = BuildMenuPlugin.Instance.GetPageNextShortcut();

            return primaryUp.IsPressed() || primaryDown.IsPressed()
                   || secondaryLeft.IsPressed() || secondaryRight.IsPressed()
                   || pagePrev.IsPressed() || pageNext.IsPressed();
        }

        private static void DumpLibrary()
        {
            var player = Player.m_localPlayer;
            if (!player)
            {
                BuildMenuPlugin.Instance.Log.LogWarning("Cannot dump build library because there is no local player.");
                return;
            }

            var pieces = Utils.GetAllBuildPiecesForPlayer(player);
            if (pieces == null || pieces.Count == 0)
            {
                BuildMenuPlugin.Instance.Log.LogWarning("Cannot dump build library because no build pieces are available.");
                return;
            }

            var toolSources = Utils.GetBuildPieceToolSourcesForPlayer(player);
            var entries = pieces
                .Where(piece => piece)
                .Select(piece =>
                {
                    var prefab = Utils.GetPrefabName(piece);
                    return new BuildPieceDumpEntry
                    {
                        Prefab = prefab,
                        Name = Utils.GetDisplayName(piece),
                        Token = Utils.GetRawDisplayToken(piece),
                        PieceTable = Utils.GetToolSourceLabel(prefab, toolSources),
                        Category = Utils.GetPieceCategory(piece),
                        CraftingStation = Utils.GetCraftingStationName(piece),
                        SystemEffects = Utils.GetSystemEffects(piece),
                        InteractionHooks = Utils.GetInteractionHooks(piece),
                        Required = Utils.GetRequiredItemNames(piece)
                    };
                })
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Prefab))
                .GroupBy(entry => entry.Prefab, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(entry => entry.Prefab, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var path = Path.Combine(Paths.ConfigPath, "BuildMenuPieceDump.json");
            var serializer = new DataContractJsonSerializer(typeof(BuildPieceDumpFile));
            using (var stream = File.Create(path))
            {
                serializer.WriteObject(stream, new BuildPieceDumpFile
                {
                    Pieces = entries
                });
            }

            BuildMenuPlugin.Instance.DebugLog($"Build piece dump written to {path}");
            BuildMenuPlugin.Instance.Log.LogInfo($"Dumped {entries.Count} build pieces to {path}");
        }

        private static void CreateRoot(Hud hud)
        {
            var buildHud = Utils.GetHudBuildHud(hud);
            var anchor = buildHud != null ? buildHud.transform : hud.transform;
            var leftOffset = BuildMenuPlugin.Instance.GetUiOffsetLeft();
            var topOffset = BuildMenuPlugin.Instance.GetUiOffsetTop();

            _root = new GameObject(RootName, typeof(RectTransform));
            _root.transform.SetParent(anchor, false);

            var rootRect = _root.GetComponent<RectTransform>();
            rootRect.anchorMin = new Vector2(0f, 1f);
            rootRect.anchorMax = new Vector2(0f, 1f);
            rootRect.pivot = new Vector2(0f, 1f);
            rootRect.anchoredPosition = new Vector2(leftOffset, -topOffset);
            rootRect.sizeDelta = new Vector2(SecondaryPanelX + GetSecondaryPanelWidth(), 360f);
            BuildMenuPlugin.Instance.DebugLog($"CreateRoot on {anchor.name} at anchoredPosition={rootRect.anchoredPosition} size={rootRect.sizeDelta}");

            _primaryColumn = CreatePanel(
                PrimaryColumnName,
                _root.transform,
                new Vector2(0f, 1f),
                new Vector2(0f, 1f),
                new Vector2(0f, 1f),
                new Vector2(0f, 0f),
                new Vector2(PrimaryPanelWidth, GetPrimaryPanelHeight()));
            var primaryLayout = _primaryColumn.AddComponent<VerticalLayoutGroup>();
            primaryLayout.childControlHeight = true;
            primaryLayout.childControlWidth = true;
            primaryLayout.childForceExpandHeight = false;
            primaryLayout.childForceExpandWidth = true;
            primaryLayout.spacing = PrimaryLayoutSpacing;
            primaryLayout.padding = new RectOffset(PrimaryLayoutPadding, PrimaryLayoutPadding, PrimaryLayoutPadding, PrimaryLayoutPadding);

            _secondaryRow = CreatePanel(SecondaryRowName, _root.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(SecondaryPanelX, 0f), new Vector2(GetSecondaryPanelWidth(), 52f));
            var secondaryLayout = _secondaryRow.AddComponent<HorizontalLayoutGroup>();
            secondaryLayout.childControlHeight = true;
            secondaryLayout.childControlWidth = false;
            secondaryLayout.childForceExpandHeight = true;
            secondaryLayout.childForceExpandWidth = false;
            secondaryLayout.spacing = SecondaryLayoutSpacing;
            secondaryLayout.padding = new RectOffset(SecondaryLayoutPadding, SecondaryLayoutPadding, SecondaryLayoutPadding, SecondaryLayoutPadding);

            _pagingRow = CreatePanel(PagingRowName, _root.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(SecondaryPanelX, -60f), new Vector2(GetSecondaryPanelWidth(), 40f));
            var pagingLayout = _pagingRow.AddComponent<HorizontalLayoutGroup>();
            pagingLayout.childControlHeight = true;
            pagingLayout.childControlWidth = false;
            pagingLayout.childForceExpandHeight = true;
            pagingLayout.childForceExpandWidth = false;
            pagingLayout.spacing = SecondaryLayoutSpacing;
            pagingLayout.padding = new RectOffset(SecondaryLayoutPadding, SecondaryLayoutPadding, SecondaryLayoutPadding, SecondaryLayoutPadding);

            CreateButton(_pagingRow.transform, "Prev", 78f, 28f, () => StepPage(-1)).name = "PagePrev";
            CreateLabel("PageInfo", _pagingRow.transform, "Page 1/1").name = "PageInfo";
            CreateButton(_pagingRow.transform, "Next", 78f, 28f, () => StepPage(1)).name = "PageNext";

            var title = CreateLabel("Title", _root.transform, "Build Filters");
            var titleRect = title.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(0f, 1f);
            titleRect.pivot = new Vector2(0f, 1f);
            titleRect.anchoredPosition = new Vector2(SecondaryPanelX, 24f);
            titleRect.sizeDelta = new Vector2(300f, 24f);
        }

        private static GameObject CreatePanel(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 anchoredPosition, Vector2 size)
        {
            var panel = new GameObject(name, typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(parent, false);

            var rect = panel.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;

            var image = panel.GetComponent<Image>();
            image.color = new Color(0f, 0f, 0f, 0.35f);

            return panel;
        }

        private static GameObject CreateLabel(string name, Transform parent, string text)
        {
            var obj = new GameObject(name, typeof(RectTransform));
            obj.transform.SetParent(parent, false);
            var label = obj.AddComponent<Text>();
            label.text = text;
            label.font = GetUIFont();
            label.fontSize = 20;
            label.color = Color.white;
            label.alignment = TextAnchor.MiddleLeft;
            return obj;
        }

        private static void RebuildPrimaryButtons()
        {
            var rect = _primaryColumn.GetComponent<RectTransform>();
            if (rect != null)
            {
                rect.sizeDelta = new Vector2(PrimaryPanelWidth, GetPrimaryPanelHeight(_visiblePrimaryCategories.Count));
            }

            ClearChildren(_primaryColumn.transform);
            foreach (var category in GetVisiblePrimaryCategories())
            {
                var button = CreateButton(_primaryColumn.transform, category, 0, PrimaryButtonHeight, () =>
                {
                    BuildMenuState.SetPrimary(category);
                    NotifyPrimaryChanged();
                });
                button.name = $"Primary_{category}";
            }
        }

        private static void RebuildSecondaryButtons()
        {
            ClearChildren(_secondaryRow.transform);
            foreach (var category in GetVisibleSecondaryCategories())
            {
                var button = CreateButton(_secondaryRow.transform, category, SecondaryButtonWidth, SecondaryButtonHeight, () =>
                {
                    BuildMenuState.SetSecondary(category);
                    NotifySecondaryChanged();
                });
                button.name = $"Secondary_{category}";
            }
        }

        private static GameObject CreateButton(Transform parent, string text, float preferredWidth, float preferredHeight, Action onClick)
        {
            var buttonObject = new GameObject($"Btn_{text}", typeof(RectTransform), typeof(Image), typeof(Button));
            buttonObject.transform.SetParent(parent, false);
            var buttonRect = buttonObject.GetComponent<RectTransform>();

            var image = buttonObject.GetComponent<Image>();
            image.color = new Color(0.15f, 0.15f, 0.15f, 0.85f);

            var button = buttonObject.GetComponent<Button>();
            button.onClick.AddListener(() => onClick());

            var layout = buttonObject.AddComponent<LayoutElement>();
            if (preferredWidth > 0f)
            {
                layout.preferredWidth = preferredWidth;
                var size = buttonRect.sizeDelta;
                size.x = preferredWidth;
                buttonRect.sizeDelta = size;
            }
            if (preferredHeight > 0f)
            {
                layout.preferredHeight = preferredHeight;
            }
            else
            {
                layout.preferredHeight = 30f;
            }

            var labelObject = new GameObject("Text", typeof(RectTransform));
            labelObject.transform.SetParent(buttonObject.transform, false);
            var labelRect = labelObject.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;

            var label = labelObject.AddComponent<Text>();
            label.text = text;
            label.font = GetUIFont();
            label.fontSize = 16;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = Color.white;

            return buttonObject;
        }

        private static void StepPrimary(int direction)
        {
            var categories = GetVisiblePrimaryCategories();
            if (categories.Count == 0)
            {
                return;
            }

            var index = categories.IndexOf(BuildMenuState.SelectedPrimary);
            if (index < 0)
            {
                index = 0;
            }

            index = (index + direction + categories.Count) % categories.Count;
            BuildMenuState.SetPrimary(categories[index]);
            NotifyPrimaryChanged();
        }

        private static void StepSecondary(int direction)
        {
            var categories = GetVisibleSecondaryCategories();
            if (categories.Count == 0)
            {
                return;
            }

            var index = FindIndex(categories, BuildMenuState.SelectedSecondary);
            if (index < 0)
            {
                index = 0;
            }

            index = (index + direction + categories.Count) % categories.Count;
            BuildMenuState.SetSecondary(categories[index]);
            NotifySecondaryChanged();
        }

        private static int FindIndex(IReadOnlyList<string> items, string value)
        {
            for (var i = 0; i < items.Count; ++i)
            {
                if (string.Equals(items[i], value, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        private static Font GetUIFont()
        {
            return Resources.GetBuiltinResource<Font>("Arial.ttf");
        }

        private static float GetPrimaryPanelHeight(int? buttonCountOverride = null)
        {
            var buttonCount = Math.Max(1, buttonCountOverride ?? BuildMenuState.PrimaryCategories.Count);
            var spacingCount = Math.Max(0, buttonCount - 1);
            return buttonCount * PrimaryButtonHeight
                + spacingCount * PrimaryLayoutSpacing
                + (PrimaryLayoutPadding * 2);
        }

        private static float GetSecondaryPanelWidth()
        {
            var buttonCount = Math.Max(1, BuildMenuState.SecondaryCategories.Values.DefaultIfEmpty().Max(values => values?.Count ?? 0));
            var spacingCount = Math.Max(0, buttonCount - 1);
            return Math.Max(
                SecondaryPanelMinWidth,
                buttonCount * SecondaryButtonWidth
                + spacingCount * SecondaryLayoutSpacing
                + (SecondaryLayoutPadding * 2));
        }

        private static void RefreshButtonStates()
        {
            if (!_root)
            {
                return;
            }

            foreach (Transform child in _primaryColumn.transform)
            {
                var isSelected = child.name == $"Primary_{BuildMenuState.SelectedPrimary}";
                SetButtonVisual(child.gameObject, isSelected);
            }

            foreach (Transform child in _secondaryRow.transform)
            {
                var isSelected = child.name == $"Secondary_{BuildMenuState.SelectedSecondary}";
                SetButtonVisual(child.gameObject, isSelected);
            }

            RefreshPagingState();
        }

        private static void SetButtonVisual(GameObject buttonObject, bool selected)
        {
            var image = buttonObject.GetComponent<Image>();
            if (!image)
            {
                return;
            }

            image.color = selected
                ? new Color(0.40f, 0.24f, 0.08f, 0.95f)
                : new Color(0.15f, 0.15f, 0.15f, 0.85f);
        }

        private static void RefreshPagingState()
        {
            if (_pagingRow == null)
            {
                return;
            }

            var player = Player.m_localPlayer;
            var buildPieces = player ? Utils.GetBuildPiecesTable(player) : null;
            var categories = buildPieces != null ? BuildMenuPatches.GetSourceAvailablePieces(buildPieces) : null;
            var total = categories != null ? BuildMenuLogic.GetFilteredPieceCount(categories) : 0;
            var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)BuildMenuState.PageSize));
            if (BuildMenuState.CurrentPage >= totalPages)
            {
                BuildMenuState.SetPage(totalPages - 1);
            }

            var label = _pagingRow.transform.Find("PageInfo")?.GetComponent<Text>();
            if (label != null)
            {
                label.text = $"Page {BuildMenuState.CurrentPage + 1}/{totalPages} ({total})";
            }

            SetPagingButtonEnabled("PagePrev", BuildMenuState.CurrentPage > 0);
            SetPagingButtonEnabled("PageNext", BuildMenuState.CurrentPage < totalPages - 1);
        }

        private static void SetPagingButtonEnabled(string name, bool enabled)
        {
            var buttonObject = _pagingRow.transform.Find(name);
            if (buttonObject == null)
            {
                return;
            }

            var button = buttonObject.GetComponent<Button>();
            if (button != null)
            {
                button.interactable = enabled;
            }

            var image = buttonObject.GetComponent<Image>();
            if (image != null)
            {
                image.color = enabled
                    ? new Color(0.15f, 0.15f, 0.15f, 0.85f)
                    : new Color(0.08f, 0.08f, 0.08f, 0.45f);
            }
        }

        private static void RefreshPieceSelection()
        {
            if (!_hud)
            {
                return;
            }

            var player = Player.m_localPlayer;
            if (!player)
            {
                return;
            }

            var buildPieces = Utils.GetBuildPiecesTable(player);
            if (buildPieces != null)
            {
                Utils.InvokePlayerSetBuildCategory(player, 0);
                BuildMenuPlugin.Instance.DebugLog($"Refreshing piece selection for category={Utils.InvokePieceTableGetSelectedCategory(buildPieces)}");
                Utils.InvokeUpdateAvailablePiecesList(player);
                BuildMenuPatches.ApplyFilteredBuildPieces(player);
                Utils.InvokePlayerSetSelectedPiece(player, Vector2Int.zero);
                Utils.InvokeHudUpdateBuild(_hud, player, true);
            }
        }

        private static bool IsInBuildMode(Hud hud)
        {
            var player = Player.m_localPlayer;
            var pieceSelectionWindow = Utils.GetHudPieceSelectionWindow(hud);
            return player && Utils.InvokePlayerInPlaceMode(player) && hud && pieceSelectionWindow && pieceSelectionWindow.activeInHierarchy;
        }

        private static void ClearChildren(Transform parent)
        {
            for (var i = parent.childCount - 1; i >= 0; --i)
            {
                UnityEngine.Object.Destroy(parent.GetChild(i).gameObject);
            }
        }

        private static void UnlockCursorTemporarily()
        {
            if (!_cursorTemporarilyUnlocked)
            {
                _previousCursorLockMode = Cursor.lockState;
                _previousCursorVisible = Cursor.visible;
                _cursorTemporarilyUnlocked = true;
            }

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private static void RestoreCursor()
        {
            if (!_cursorTemporarilyUnlocked)
            {
                return;
            }

            Cursor.lockState = _previousCursorLockMode;
            Cursor.visible = _previousCursorVisible;
            _cursorTemporarilyUnlocked = false;
        }

        private static void ConfigureVanillaTabs()
        {
            if (_hud == null)
            {
                return;
            }

            var tabs = Utils.GetHudPieceCategoryTabs(_hud);
            if (tabs == null || tabs.Length == 0)
            {
                return;
            }

            var player = Player.m_localPlayer;
            var buildPieces = player ? Utils.GetBuildPiecesTable(player) : null;
            var sourceCategories = buildPieces != null ? BuildMenuPatches.GetSourceAvailablePieces(buildPieces) : null;
            var filteredCount = sourceCategories != null ? BuildMenuLogic.GetFilteredPieceCount(sourceCategories) : 0;
            string label;
            if (filteredCount <= 0)
            {
                label = "Items (0 of 0)";
            }
            else
            {
                var start = BuildMenuState.CurrentPage * BuildMenuState.PageSize + 1;
                var end = Math.Min(filteredCount, (BuildMenuState.CurrentPage + 1) * BuildMenuState.PageSize);
                label = $"Items ({start}-{end} of {filteredCount})";
            }

            for (var i = 0; i < tabs.Length; ++i)
            {
                var tab = tabs[i];
                if (!tab)
                {
                    continue;
                }

                tab.SetActive(i == 0);
                if (i == 0)
                {
                    var preferredLabelWidth = 0f;

                    foreach (var tmpText in tab.GetComponentsInChildren<TMP_Text>(true))
                    {
                        tmpText.text = label;
                        tmpText.textWrappingMode = TextWrappingModes.NoWrap;
                        tmpText.overflowMode = TextOverflowModes.Overflow;
                        preferredLabelWidth = Math.Max(preferredLabelWidth, tmpText.GetPreferredValues(label).x);
                    }

                    foreach (var text in tab.GetComponentsInChildren<Text>(true))
                    {
                        text.text = label;
                        text.horizontalOverflow = HorizontalWrapMode.Overflow;
                        preferredLabelWidth = Math.Max(preferredLabelWidth, text.preferredWidth);
                    }

                    var tabRect = tab.GetComponent<RectTransform>();
                    if (tabRect != null && preferredLabelWidth > 0f)
                    {
                        var size = tabRect.sizeDelta;
                        size.x = Math.Max(size.x, preferredLabelWidth + 24f);
                        tabRect.sizeDelta = size;
                    }
                }
            }

        }

        private static void RefreshVisibleCategories()
        {
            var player = Player.m_localPlayer;
            var buildPieces = player ? Utils.GetBuildPiecesTable(player) : null;
            var categories = buildPieces != null ? BuildMenuPatches.GetSourceAvailablePieces(buildPieces) : null;

            _primaryCounts.Clear();
            _secondaryCounts.Clear();
            _visiblePrimaryCategories.Clear();
            _visibleSecondaryCategories.Clear();

            foreach (var primary in BuildMenuState.PrimaryCategories)
            {
                var count = BuildMenuLogic.GetFilteredPieceCountForSelection(categories, primary, BuildMenuState.All);
                _primaryCounts[primary] = count;
            }

            foreach (var primary in BuildMenuState.PrimaryCategories)
            {
                if (string.Equals(primary, BuildMenuState.All, StringComparison.OrdinalIgnoreCase) || _primaryCounts.TryGetValue(primary, out var count) && count > 0)
                {
                    _visiblePrimaryCategories.Add(primary);
                }
            }

            if (_visiblePrimaryCategories.Count == 0)
            {
                _visiblePrimaryCategories.Add(BuildMenuState.All);
            }

            if (!_visiblePrimaryCategories.Contains(BuildMenuState.SelectedPrimary))
            {
                BuildMenuState.SetPrimary(_visiblePrimaryCategories[0]);
            }

            var allowedSecondary = BuildMenuState.GetSecondaryForSelectedPrimary();
            foreach (var secondary in allowedSecondary)
            {
                var count = BuildMenuLogic.GetFilteredPieceCountForSelection(categories, BuildMenuState.SelectedPrimary, secondary);
                _secondaryCounts[secondary] = count;
                if (count > 0)
                {
                    _visibleSecondaryCategories.Add(secondary);
                }
            }

            if (_visibleSecondaryCategories.Count > 0 && !_visibleSecondaryCategories.Contains(BuildMenuState.SelectedSecondary))
            {
                BuildMenuState.SetSecondary(_visibleSecondaryCategories[0]);
            }
            else if (_visibleSecondaryCategories.Count == 0)
            {
                BuildMenuState.SetSecondary(BuildMenuState.All);
            }
        }

        private static List<string> GetVisiblePrimaryCategories()
        {
            return _primaryCounts.Count > 0 ? _visiblePrimaryCategories : BuildMenuState.PrimaryCategories;
        }

        private static IReadOnlyList<string> GetVisibleSecondaryCategories()
        {
            return _secondaryCounts.Count > 0 ? _visibleSecondaryCategories : BuildMenuState.GetSecondaryForSelectedPrimary();
        }

    }

    [HarmonyPatch]
    internal static class BuildMenuPatches
    {
        private static string _lastUpdateBuildSignature;
        private static List<List<Piece>> _originalAvailablePieces;
        private static PieceTable _filteredPieceTable;

        private static bool ShouldRun()
        {
            return BuildMenuPlugin.Instance != null && BuildMenuPlugin.Instance.IsEnabled();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Hud), nameof(Hud.Update))]
        // ReSharper disable InconsistentNaming
        private static void Hud_Update_Postfix(Hud __instance)
        {
            if (!ShouldRun())
            {
                return;
            }

            BuildMenuUI.EnsureUI(__instance);
            var player = Player.m_localPlayer;
            if (player != null)
            {
                ValidateConfiguredPrefabs(player);
                if (Utils.InvokeHudIsPieceSelectionVisible())
                {
                    ApplyFilteredBuildPieces(player);
                }
                else
                {
                    RestoreFilteredBuildPieces();
                }
            }
            BuildMenuUI.HandleInput();
        }
        // ReSharper enable InconsistentNaming

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Player), "Update")]
        // ReSharper disable InconsistentNaming
        private static void Player_Update_Prefix(Player __instance)
        {
            if (!ShouldRun() || __instance != Player.m_localPlayer)
            {
                return;
            }
            BuildMenuUI.TryHandleNavigationInput();
        }
        // ReSharper enable InconsistentNaming

        private static void ValidateConfiguredPrefabs(Player player)
        {
            if (BuildMenuPlugin.Instance.HasValidatedConfiguredPrefabs())
            {
                return;
            }

            var pieces = Utils.GetAllBuildPiecesForPlayer(player);
            if (pieces == null || pieces.Count == 0)
            {
                return;
            }

            var knownPrefabs = new HashSet<string>(
                pieces
                    .Where(piece => piece)
                    .Select(Utils.GetPrefabName)
                    .Where(name => !string.IsNullOrWhiteSpace(name)),
                StringComparer.OrdinalIgnoreCase);

            foreach (var configuredPrefab in BuildMenuPlugin.Instance.ConfiguredPrefabs.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                if (!knownPrefabs.Contains(configuredPrefab))
                {
                    BuildMenuPlugin.Instance.Log.LogWarning($"Configured Prefab '{configuredPrefab}' was not found in the known build Prefab library.");
                }
            }

            BuildMenuPlugin.Instance.MarkConfiguredPrefabsValidated();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Hud), nameof(Hud.HidePieceSelection))]
        private static void Hud_HidePieceSelection_Postfix()
        {
            if (!ShouldRun())
            {
                return;
            }

            RestoreFilteredBuildPieces();
            BuildMenuPlugin.Instance.DebugLog("Hud.HidePieceSelection postfix fired");
            BuildMenuUI.DestroyUI();
        }

        internal static void ApplyFilteredBuildPieces(Player player)
        {
            var buildPieces = player != null ? Utils.GetBuildPiecesTable(player) : null;
            var availablePieces = buildPieces != null ? Utils.GetAvailablePieceCategories(buildPieces) : null;
            if (!ShouldRun() || player == null || player != Player.m_localPlayer || buildPieces == null || availablePieces == null)
            {
                return;
            }

            try
            {
                if (!ReferenceEquals(_filteredPieceTable, buildPieces) || _originalAvailablePieces == null)
                {
                    _filteredPieceTable = buildPieces;
                    _originalAvailablePieces = Utils.ClonePieceCategories(availablePieces);
                }

                var sourcePieces = _originalAvailablePieces ?? Utils.ClonePieceCategories(availablePieces);
                var beforePieceCount = sourcePieces.Sum(category => category?.Count ?? 0);
                var selectedCategoryIndex = Utils.GetPieceTableSelectedCategoryIndex(buildPieces);
                Utils.SetAvailablePieceCategories(buildPieces, BuildMenuLogic.ProjectFilteredToSelectedCategory(sourcePieces, selectedCategoryIndex));
                var afterPieceCount = Utils.GetAvailablePieceCategories(buildPieces).Sum(category => category?.Count ?? 0);
                var signature = $"{beforePieceCount}->{afterPieceCount}|{BuildMenuState.SelectedPrimary}|{BuildMenuState.SelectedSecondary}|{selectedCategoryIndex}";
                if (!string.Equals(signature, _lastUpdateBuildSignature, StringComparison.Ordinal))
                {
                    BuildMenuPlugin.Instance.DebugLog($"Hud.UpdateBuild prefix fired. pieces {beforePieceCount}->{afterPieceCount}, primary={BuildMenuState.SelectedPrimary}, secondary={BuildMenuState.SelectedSecondary}, selectedCategoryIndex={selectedCategoryIndex}");
                    _lastUpdateBuildSignature = signature;
                }
            }
            catch (Exception ex)
            {
                BuildMenuPlugin.Instance.Log.LogError($"Failed to apply filtered build pieces: {ex}");
            }
        }

        internal static void RestoreFilteredBuildPieces()
        {
            if (_filteredPieceTable != null && _originalAvailablePieces != null)
            {
                Utils.SetAvailablePieceCategories(_filteredPieceTable, _originalAvailablePieces);
            }

            _filteredPieceTable = null;
            _originalAvailablePieces = null;
        }

        internal static List<List<Piece>> GetSourceAvailablePieces(PieceTable pieceTable)
        {
            if (pieceTable == null)
            {
                return null;
            }

            return ReferenceEquals(_filteredPieceTable, pieceTable) && _originalAvailablePieces != null
                ? _originalAvailablePieces
                : Utils.GetAvailablePieceCategories(pieceTable);
        }

    }

    internal static class Utils
    {
        private static readonly System.Reflection.FieldInfo BuildPiecesField = AccessTools.Field(typeof(Player), "m_buildPieces");
        private static readonly System.Reflection.FieldInfo PieceResourcesField = AccessTools.Field(typeof(Piece), "m_resources");
        private static readonly Type PieceRequirementType = AccessTools.TypeByName("Piece+Requirement");
        private static readonly System.Reflection.FieldInfo RequirementResItemField = PieceRequirementType != null
            ? AccessTools.Field(PieceRequirementType, "m_resItem")
            : null;
        private static readonly System.Reflection.FieldInfo RequirementAmountField = PieceRequirementType != null
            ? AccessTools.Field(PieceRequirementType, "m_amount")
            : null;
        private static readonly System.Reflection.FieldInfo ItemDropItemDataField = AccessTools.Field(typeof(ItemDrop), "m_itemData");
        private static readonly System.Reflection.FieldInfo ItemSharedDataField = AccessTools.Field(typeof(ItemDrop.ItemData), "m_shared");
        private static readonly System.Reflection.FieldInfo SharedNameField = ItemSharedDataField != null
            ? AccessTools.Field(ItemSharedDataField.FieldType, "m_name")
            : null;
        private static readonly System.Reflection.FieldInfo SharedBuildPiecesField = ItemSharedDataField != null
            ? AccessTools.Field(ItemSharedDataField.FieldType, "m_buildPieces")
            : null;
        private static readonly System.Reflection.FieldInfo AvailablePiecesField = AccessTools.Field(typeof(PieceTable), "m_availablePieces");
        private static readonly System.Reflection.FieldInfo PieceTablePiecesField = AccessTools.Field(typeof(PieceTable), "m_pieces");
        private static readonly System.Reflection.FieldInfo HudBuildHudField = AccessTools.Field(typeof(Hud), "m_buildHud");
        private static readonly System.Reflection.FieldInfo HudPieceSelectionWindowField = AccessTools.Field(typeof(Hud), "m_pieceSelectionWindow");
        private static readonly System.Reflection.FieldInfo HudPieceCategoryTabsField = AccessTools.Field(typeof(Hud), "m_pieceCategoryTabs");
        private static readonly System.Reflection.FieldInfo PieceNameField = AccessTools.Field(typeof(Piece), "m_name");
        private static readonly System.Reflection.FieldInfo PieceCategoryField = AccessTools.Field(typeof(Piece), "m_category");
        private static readonly System.Reflection.FieldInfo PieceCraftingStationField = AccessTools.Field(typeof(Piece), "m_craftingStation");
        private static readonly System.Reflection.FieldInfo PieceComfortField = AccessTools.Field(typeof(Piece), "m_comfort");
        private static readonly System.Reflection.FieldInfo PieceTableNameField = AccessTools.Field(typeof(PieceTable), "m_name");
        private static readonly System.Reflection.FieldInfo CraftingStationNameField = AccessTools.Field(typeof(CraftingStation), "m_name");
        private static readonly System.Reflection.MethodInfo PieceGetHoverNameMethod =
            typeof(Piece).GetMethod("GetHoverName",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        private static readonly System.Reflection.MethodInfo UpdateAvailablePiecesListMethod = AccessTools.Method(typeof(Player), "UpdateAvailablePiecesList");
        private static readonly System.Reflection.MethodInfo HudUpdateBuildMethod = AccessTools.Method(typeof(Hud), "UpdateBuild");
        private static readonly System.Reflection.MethodInfo PlayerInPlaceModeMethod = AccessTools.Method(typeof(Player), "InPlaceMode");
        private static readonly System.Reflection.MethodInfo PlayerSetBuildCategoryMethod = AccessTools.Method(typeof(Player), "SetBuildCategory");
        private static readonly System.Reflection.MethodInfo PlayerSetSelectedPieceMethod = AccessTools.Method(typeof(Player), "SetSelectedPiece", new[] { typeof(Vector2Int) });
        private static readonly System.Reflection.MethodInfo HudIsPieceSelectionVisibleMethod = AccessTools.Method(typeof(Hud), "IsPieceSelectionVisible");
        private static readonly System.Reflection.MethodInfo PieceTableGetSelectedCategoryMethod = AccessTools.Method(typeof(PieceTable), "GetSelectedCategory");
        private static readonly System.Reflection.MethodInfo HumanoidGetRightItemMethod = AccessTools.Method(typeof(Humanoid), "GetRightItem");
        private static readonly System.Reflection.MethodInfo HumanoidGetInventoryMethod = AccessTools.Method(typeof(Humanoid), "GetInventory");
        private static readonly System.Reflection.MethodInfo InventoryGetAllItemsMethod = AccessTools.Method(typeof(Inventory), "GetAllItems");
        private static readonly Type ZInputType = AccessTools.TypeByName("ZInput");
        private static readonly System.Reflection.MethodInfo ZInputResetMethod = GetNoArgMethod(ZInputType, "Reset");
        private static readonly System.Reflection.MethodInfo ZInputResetButtonStatusNoArgMethod = GetNoArgMethod(ZInputType, "ResetButtonStatus");
        private static readonly System.Reflection.MethodInfo ZInputResetButtonStatusStringMethod = GetSingleStringArgMethod(ZInputType, "ResetButtonStatus");
        private static readonly System.Reflection.FieldInfo ZInputInstanceField = GetZInputSingletonField(ZInputType);
        private static readonly System.Reflection.PropertyInfo ZInputInstanceProperty = GetZInputSingletonProperty(ZInputType);
        private static readonly string[] ZInputMovementActions =
        {
            "Forward",
            "Backward",
            "Left",
            "Right"
        };
        private static readonly Type InteractableType = AccessTools.TypeByName("Interactable");
        private static readonly Type FireplaceType = AccessTools.TypeByName("Fireplace");
        private static readonly Type BedType = AccessTools.TypeByName("Bed");
        private static readonly Type TeleportWorldType = AccessTools.TypeByName("TeleportWorld");
        private static readonly Type StationExtensionType = AccessTools.TypeByName("StationExtension");
        private static readonly Type PrivateAreaType = AccessTools.TypeByName("PrivateArea");
        private static readonly Type ContainerType = AccessTools.TypeByName("Container");
        private static readonly Type DoorType = AccessTools.TypeByName("Door");
        private static readonly Type SmelterType = AccessTools.TypeByName("Smelter");
        private static readonly Type CookingStationType = AccessTools.TypeByName("CookingStation");
        private static readonly Type FermenterType = AccessTools.TypeByName("Fermenter");
        private static readonly Type SignType = AccessTools.TypeByName("Sign");
        private static readonly Type PickableType = AccessTools.TypeByName("Pickable");
        private static readonly Type ShipType = AccessTools.TypeByName("Ship");

        public static PieceTable GetBuildPiecesTable(Player player)
        {
            if (player == null)
            {
                return null;
            }

            var direct = BuildPiecesField != null
                ? BuildPiecesField.GetValue(player) as PieceTable
                : null;
            if (direct != null)
            {
                return direct;
            }

            var rightItem = HumanoidGetRightItemMethod != null
                ? HumanoidGetRightItemMethod.Invoke(player, null) as ItemDrop.ItemData
                : null;
            if (rightItem == null || ItemSharedDataField == null || SharedBuildPiecesField == null)
            {
                return null;
            }

            var shared = ItemSharedDataField.GetValue(rightItem);
            return shared != null
                ? SharedBuildPiecesField.GetValue(shared) as PieceTable
                : null;
        }

        public static List<List<Piece>> GetAvailablePieceCategories(PieceTable pieceTable)
        {
            return pieceTable != null && AvailablePiecesField != null
                ? AvailablePiecesField.GetValue(pieceTable) as List<List<Piece>>
                : null;
        }

        public static List<GameObject> GetAllBuildPieces(PieceTable pieceTable)
        {
            return pieceTable != null && PieceTablePiecesField != null
                ? PieceTablePiecesField.GetValue(pieceTable) as List<GameObject>
                : null;
        }

        public static List<GameObject> GetAllBuildPiecesForPlayer(Player player)
        {
            var allPieces = new List<GameObject>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AddBuildPiecesFromTable(GetBuildPiecesTable(player), allPieces, seen);

            var inventory = player != null && HumanoidGetInventoryMethod != null
                ? HumanoidGetInventoryMethod.Invoke(player, null) as Inventory
                : null;
            var items = inventory != null && InventoryGetAllItemsMethod != null
                ? InventoryGetAllItemsMethod.Invoke(inventory, null) as List<ItemDrop.ItemData>
                : null;

            if (items != null)
            {
                foreach (var item in items)
                {
                    AddBuildPiecesFromItem(item, allPieces, seen);
                }
            }

            return allPieces;
        }

        public static Dictionary<string, List<string>> GetBuildPieceToolSourcesForPlayer(Player player)
        {
            var sources = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            if (player == null)
            {
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            }

            var rightItem = HumanoidGetRightItemMethod != null
                ? HumanoidGetRightItemMethod.Invoke(player, null) as ItemDrop.ItemData
                : null;
            AddBuildPiecesFromItem(rightItem, null, null, sources);

            var directTable = BuildPiecesField != null
                ? BuildPiecesField.GetValue(player) as PieceTable
                : null;
            AddBuildPiecesFromTable(directTable, null, null, sources, GetPieceTableName(directTable));

            var inventory = HumanoidGetInventoryMethod != null
                ? HumanoidGetInventoryMethod.Invoke(player, null) as Inventory
                : null;
            var items = inventory != null && InventoryGetAllItemsMethod != null
                ? InventoryGetAllItemsMethod.Invoke(inventory, null) as List<ItemDrop.ItemData>
                : null;

            if (items != null)
            {
                foreach (var item in items)
                {
                    AddBuildPiecesFromItem(item, null, null, sources);
                }
            }

            return sources.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);
        }

        public static string GetToolSourceLabel(string prefab, IReadOnlyDictionary<string, List<string>> sources)
        {
            if (string.IsNullOrWhiteSpace(prefab) || sources == null || !sources.TryGetValue(prefab, out var values) || values == null || values.Count == 0)
            {
                return string.Empty;
            }

            return string.Join(", ", values.Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        public static GameObject GetHudBuildHud(Hud hud)
        {
            return hud != null && HudBuildHudField != null
                ? HudBuildHudField.GetValue(hud) as GameObject
                : null;
        }

        public static GameObject GetHudPieceSelectionWindow(Hud hud)
        {
            return hud != null && HudPieceSelectionWindowField != null
                ? HudPieceSelectionWindowField.GetValue(hud) as GameObject
                : null;
        }

        public static GameObject[] GetHudPieceCategoryTabs(Hud hud)
        {
            return hud != null && HudPieceCategoryTabsField != null
                ? HudPieceCategoryTabsField.GetValue(hud) as GameObject[]
                : null;
        }

        public static void SetAvailablePieceCategories(PieceTable pieceTable, List<List<Piece>> value)
        {
            if (pieceTable != null && AvailablePiecesField != null)
            {
                AvailablePiecesField.SetValue(pieceTable, value);
            }
        }

        public static List<List<Piece>> ClonePieceCategories(List<List<Piece>> categories)
        {
            if (categories == null)
            {
                return null;
            }

            return categories
                .Select(category => category != null ? new List<Piece>(category) : new List<Piece>())
                .ToList();
        }

        public static void InvokeUpdateAvailablePiecesList(Player player)
        {
            if (player != null && UpdateAvailablePiecesListMethod != null)
            {
                UpdateAvailablePiecesListMethod.Invoke(player, null);
            }
        }

        public static void InvokeHudUpdateBuild(Hud hud, Player player, bool forceUpdateAllBuildStatuses)
        {
            if (hud != null && player != null && HudUpdateBuildMethod != null)
            {
                HudUpdateBuildMethod.Invoke(hud, new object[] { player, forceUpdateAllBuildStatuses });
            }
        }

        public static bool InvokePlayerInPlaceMode(Player player)
        {
            return player != null && PlayerInPlaceModeMethod != null
                                  && (bool)PlayerInPlaceModeMethod.Invoke(player, null);
        }

        public static void InvokePlayerSetBuildCategory(Player player, int index)
        {
            if (player != null && PlayerSetBuildCategoryMethod != null)
            {
                PlayerSetBuildCategoryMethod.Invoke(player, new object[] { index });
            }
        }

        public static void InvokePlayerSetSelectedPiece(Player player, Vector2Int index)
        {
            if (player != null && PlayerSetSelectedPieceMethod != null)
            {
                PlayerSetSelectedPieceMethod.Invoke(player, new object[] { index });
            }
        }

        public static bool InvokeHudIsPieceSelectionVisible()
        {
            return HudIsPieceSelectionVisibleMethod != null
                   && (bool)HudIsPieceSelectionVisibleMethod.Invoke(null, null);
        }

        public static object InvokePieceTableGetSelectedCategory(PieceTable pieceTable)
        {
            return pieceTable != null && PieceTableGetSelectedCategoryMethod != null
                ? PieceTableGetSelectedCategoryMethod.Invoke(pieceTable, null)
                : null;
        }

        public static int GetPieceTableSelectedCategoryIndex(PieceTable pieceTable)
        {
            var value = InvokePieceTableGetSelectedCategory(pieceTable);
            return value != null ? Convert.ToInt32(value) : -1;
        }

        public static string GetPrefabName(GameObject obj)
        {
            if (!obj)
            {
                return string.Empty;
            }

            var name = obj.name;
            var cloneIndex = name.IndexOf("(Clone)", StringComparison.Ordinal);
            if (cloneIndex >= 0)
            {
                name = name.Substring(0, cloneIndex);
            }

            return name.Trim();
        }

        public static void ConsumeNavigationInput()
        {
            Input.ResetInputAxes();

            try
            {
                var resetInvoked = false;

                if (ZInputResetMethod != null)
                {
                    var target = ZInputResetMethod.IsStatic ? null : ResolveZInputInstance();
                    if (ZInputResetMethod.IsStatic || target != null)
                    {
                        ZInputResetMethod.Invoke(target, null);
                        resetInvoked = true;
                    }
                }

                if (!resetInvoked && ZInputResetButtonStatusNoArgMethod != null)
                {
                    var target = ZInputResetButtonStatusNoArgMethod.IsStatic ? null : ResolveZInputInstance();
                    if (ZInputResetButtonStatusNoArgMethod.IsStatic || target != null)
                    {
                        ZInputResetButtonStatusNoArgMethod.Invoke(target, null);
                        resetInvoked = true;
                    }
                }

                if (!resetInvoked && ZInputResetButtonStatusStringMethod != null)
                {
                    var target = ZInputResetButtonStatusStringMethod.IsStatic ? null : ResolveZInputInstance();
                    if (ZInputResetButtonStatusStringMethod.IsStatic || target != null)
                    {
                        foreach (var action in ZInputMovementActions)
                        {
                            ZInputResetButtonStatusStringMethod.Invoke(target, new object[] { action });
                        }

                        resetInvoked = true;
                    }
                }

                BuildMenuPlugin.Instance.DebugLog(resetInvoked
                    ? "ConsumeNavigationInput: Input.ResetInputAxes + ZInput reset invoked"
                    : "ConsumeNavigationInput: Input.ResetInputAxes invoked, ZInput reset unavailable");
            }
            catch (Exception ex)
            {
                BuildMenuPlugin.Instance.DebugLog($"ConsumeNavigationInput: Input.ResetInputAxes invoked, ZInput.Reset failed: {ex.Message}");
            }
        }

        private static object ResolveZInputInstance()
        {
            if (ZInputInstanceField != null)
            {
                var value = ZInputInstanceField.GetValue(null);
                if (value != null)
                {
                    return value;
                }
            }

            if (ZInputInstanceProperty != null)
            {
                var value = ZInputInstanceProperty.GetValue(null, null);
                if (value != null)
                {
                    return value;
                }
            }

            if (ZInputType != null && typeof(UnityEngine.Object).IsAssignableFrom(ZInputType))
            {
                var existing = UnityEngine.Object.FindFirstObjectByType(ZInputType);
                if (existing != null)
                {
                    return existing;
                }
            }

            return null;
        }

        private static System.Reflection.MethodInfo GetNoArgMethod(Type type, string methodName)
        {
            return type?.GetMethod(
                methodName,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance,
                null,
                Type.EmptyTypes,
                null);
        }

        private static System.Reflection.MethodInfo GetSingleStringArgMethod(Type type, string methodName)
        {
            return type?.GetMethod(
                methodName,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance,
                null,
                new[] { typeof(string) },
                null);
        }

        private static System.Reflection.FieldInfo GetZInputSingletonField(Type type)
        {
            if (type == null)
            {
                return null;
            }

            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var preferred = type.GetField("instance", flags)
                            ?? type.GetField("Instance", flags)
                            ?? type.GetField("m_instance", flags)
                            ?? type.GetField("s_instance", flags);
            if (preferred != null && type.IsAssignableFrom(preferred.FieldType))
            {
                return preferred;
            }

            foreach (var field in type.GetFields(flags))
            {
                if (type.IsAssignableFrom(field.FieldType))
                {
                    return field;
                }
            }

            return null;
        }

        private static System.Reflection.PropertyInfo GetZInputSingletonProperty(Type type)
        {
            if (type == null)
            {
                return null;
            }

            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var preferred = type.GetProperty("instance", flags)
                            ?? type.GetProperty("Instance", flags)
                            ?? type.GetProperty("m_instance", flags)
                            ?? type.GetProperty("s_instance", flags);
            if (preferred != null && preferred.GetMethod != null && type.IsAssignableFrom(preferred.PropertyType))
            {
                return preferred;
            }

            foreach (var property in type.GetProperties(flags))
            {
                if (property.GetMethod != null && type.IsAssignableFrom(property.PropertyType))
                {
                    return property;
                }
            }

            return null;
        }

        public static string GetDisplayName(GameObject obj)
        {
            if (!obj)
            {
                return string.Empty;
            }

            var piece = obj.GetComponent<Piece>();
            if (piece != null && PieceGetHoverNameMethod != null)
            {
                var hoverName = PieceGetHoverNameMethod.Invoke(piece, null) as string;
                if (!string.IsNullOrWhiteSpace(hoverName) && !hoverName.StartsWith("$", StringComparison.Ordinal))
                {
                    return hoverName.Trim();
                }
            }

            var rawName = GetRawDisplayToken(obj);

            if (string.IsNullOrWhiteSpace(rawName))
            {
                return GetPrefabName(obj);
            }

            try
            {
                var localized = LocalizationManager.Instance.TryTranslate(rawName);
                if (!string.IsNullOrWhiteSpace(localized) && !string.Equals(localized, rawName, StringComparison.Ordinal))
                {
                    return localized.Trim();
                }
            }
            catch (Exception ex)
            {
                BuildMenuPlugin.Instance.Log.LogWarning($"Failed to translate display token '{rawName}': {ex.Message}");
            }

            return HumanizeToken(rawName);
        }

        public static List<BuildPieceRequirementDumpEntry> GetRequiredItemNames(GameObject obj)
        {
            if (!obj || PieceResourcesField == null || RequirementResItemField == null || RequirementAmountField == null || ItemDropItemDataField == null || ItemSharedDataField == null || SharedNameField == null)
            {
                return new List<BuildPieceRequirementDumpEntry>();
            }

            var piece = obj.GetComponent<Piece>();
            var resources = piece != null
                ? PieceResourcesField.GetValue(piece) as Array
                : null;
            if (resources == null || resources.Length == 0)
            {
                return new List<BuildPieceRequirementDumpEntry>();
            }

            var required = new List<BuildPieceRequirementDumpEntry>();
            foreach (var resource in resources)
            {
                var itemDrop = resource != null
                    ? RequirementResItemField.GetValue(resource) as ItemDrop
                    : null;
                var amount = resource != null
                    ? Convert.ToInt32(RequirementAmountField.GetValue(resource))
                    : 0;
                var itemData = itemDrop != null
                    ? ItemDropItemDataField.GetValue(itemDrop)
                    : null;
                var shared = itemData != null
                    ? ItemSharedDataField.GetValue(itemData)
                    : null;
                var rawName = shared != null
                    ? SharedNameField.GetValue(shared) as string
                    : null;

                var displayName = GetLocalizedName(rawName);
                if (!string.IsNullOrWhiteSpace(displayName))
                {
                    required.Add(new BuildPieceRequirementDumpEntry
                    {
                        Required = displayName,
                        Amount = amount
                    });
                }
            }

            return required;
        }

        public static string GetRawDisplayToken(GameObject obj)
        {
            if (!obj)
            {
                return string.Empty;
            }

            var piece = obj.GetComponent<Piece>();
            return piece != null && PieceNameField != null
                ? PieceNameField.GetValue(piece) as string ?? string.Empty
                : string.Empty;
        }

        public static string GetPieceCategory(GameObject obj)
        {
            if (!obj || PieceCategoryField == null)
            {
                return string.Empty;
            }

            var piece = obj.GetComponent<Piece>();
            var value = piece != null ? PieceCategoryField.GetValue(piece) : null;
            return value?.ToString() ?? string.Empty;
        }

        public static string GetCraftingStationName(GameObject obj)
        {
            if (!obj || PieceCraftingStationField == null)
            {
                return string.Empty;
            }

            var piece = obj.GetComponent<Piece>();
            if (piece == null)
            {
                return string.Empty;
            }

            var stationValue = PieceCraftingStationField.GetValue(piece);
            if (stationValue == null)
            {
                return string.Empty;
            }

            if (stationValue is CraftingStation station)
            {
                var rawName = CraftingStationNameField?.GetValue(station) as string;
                return !string.IsNullOrWhiteSpace(rawName)
                    ? GetLocalizedName(rawName)
                    : GetPrefabName(station.gameObject);
            }

            if (stationValue is Component component)
            {
                return GetPrefabName(component.gameObject);
            }

            if (stationValue is GameObject gameObject)
            {
                return GetPrefabName(gameObject);
            }

            return stationValue.ToString();
        }

        public static List<string> GetSystemEffects(GameObject obj)
        {
            var effects = new List<string>();
            if (!obj)
            {
                return effects;
            }

            var piece = obj.GetComponent<Piece>();
            if (piece != null && PieceComfortField != null)
            {
                var comfortValue = PieceComfortField.GetValue(piece);
                var comfort = comfortValue != null ? Convert.ToInt32(comfortValue) : 0;
                if (comfort > 0)
                {
                    effects.Add($"Comfort:{comfort}");
                }
            }

            if (HasComponent(obj, typeof(CraftingStation)))
            {
                effects.Add("CraftingStation");
            }

            if (HasComponent(obj, StationExtensionType))
            {
                effects.Add("StationExtension");
            }

            if (HasComponent(obj, FireplaceType))
            {
                effects.Add("HeatSource");
            }

            if (HasComponent(obj, BedType))
            {
                effects.Add("Bed");
            }

            if (HasComponent(obj, TeleportWorldType))
            {
                effects.Add("Portal");
            }

            if (HasComponent(obj, PrivateAreaType))
            {
                effects.Add("Ward");
            }

            return effects
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static List<string> GetInteractionHooks(GameObject obj)
        {
            var hooks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!obj)
            {
                return hooks.ToList();
            }

            AddHookIfPresent(obj, ContainerType, "Container", hooks);
            AddHookIfPresent(obj, DoorType, "Door", hooks);
            AddHookIfPresent(obj, typeof(CraftingStation), "CraftingStation", hooks);
            AddHookIfPresent(obj, StationExtensionType, "StationExtension", hooks);
            AddHookIfPresent(obj, SmelterType, "Smelter", hooks);
            AddHookIfPresent(obj, CookingStationType, "CookingStation", hooks);
            AddHookIfPresent(obj, FermenterType, "Fermenter", hooks);
            AddHookIfPresent(obj, TeleportWorldType, "Portal", hooks);
            AddHookIfPresent(obj, SignType, "Sign", hooks);
            AddHookIfPresent(obj, PickableType, "Pickable", hooks);
            AddHookIfPresent(obj, ShipType, "Ship", hooks);
            AddHookIfPresent(obj, BedType, "Bed", hooks);

            if (InteractableType != null)
            {
                foreach (var component in obj.GetComponents<Component>())
                {
                    if (component != null && InteractableType.IsAssignableFrom(component.GetType()))
                    {
                        hooks.Add(component.GetType().Name);
                    }
                }
            }

            return hooks.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void AddBuildPiecesFromItem(
            ItemDrop.ItemData item,
            ICollection<GameObject> output,
            ISet<string> seen,
            IDictionary<string, HashSet<string>> sources = null)
        {
            if (item == null || ItemSharedDataField == null || SharedBuildPiecesField == null)
            {
                return;
            }

            var shared = ItemSharedDataField.GetValue(item);
            var pieceTable = shared != null
                ? SharedBuildPiecesField.GetValue(shared) as PieceTable
                : null;
            var toolName = GetItemDisplayName(item);

            AddBuildPiecesFromTable(pieceTable, output, seen, sources, toolName);
        }

        private static void AddBuildPiecesFromTable(
            PieceTable pieceTable,
            ICollection<GameObject> output,
            ISet<string> seen,
            IDictionary<string, HashSet<string>> sources = null,
            string sourceName = null)
        {
            var pieces = GetAllBuildPieces(pieceTable);
            if (pieces == null)
            {
                return;
            }

            var resolvedSourceName = !string.IsNullOrWhiteSpace(sourceName) ? sourceName : GetPieceTableName(pieceTable);
            foreach (var piece in pieces)
            {
                if (!piece)
                {
                    continue;
                }

                var prefab = GetPrefabName(piece);
                if (string.IsNullOrWhiteSpace(prefab))
                {
                    continue;
                }

                if (sources != null && !string.IsNullOrWhiteSpace(resolvedSourceName))
                {
                    if (!sources.TryGetValue(prefab, out var names))
                    {
                        names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        sources[prefab] = names;
                    }

                    names.Add(resolvedSourceName);
                }

                if (output == null || seen == null || !seen.Add(prefab))
                {
                    continue;
                }

                output.Add(piece);
            }
        }

        private static string GetItemDisplayName(ItemDrop.ItemData item)
        {
            if (item == null || ItemSharedDataField == null || SharedNameField == null)
            {
                return string.Empty;
            }

            var shared = ItemSharedDataField.GetValue(item);
            var rawName = shared != null
                ? SharedNameField.GetValue(shared) as string
                : null;
            return GetLocalizedName(rawName);
        }

        private static string GetPieceTableName(PieceTable pieceTable)
        {
            if (pieceTable == null)
            {
                return string.Empty;
            }

            if (PieceTableNameField == null)
            {
                return GetPrefabName(pieceTable.gameObject);
            }

            var rawName = PieceTableNameField.GetValue(pieceTable) as string;
            if (!string.IsNullOrWhiteSpace(rawName))
            {
                return GetLocalizedName(rawName);
            }

            return GetPrefabName(pieceTable.gameObject);
        }

        private static bool HasComponent(GameObject obj, Type type)
        {
            return obj && type != null && obj.GetComponent(type) != null;
        }

        private static void AddHookIfPresent(GameObject obj, Type type, string hookName, ISet<string> hooks)
        {
            if (hooks != null && !string.IsNullOrWhiteSpace(hookName) && HasComponent(obj, type))
            {
                hooks.Add(hookName);
            }
        }

        private static string HumanizeToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var text = value.Trim();
            if (text.StartsWith("$", StringComparison.Ordinal))
            {
                text = text.Substring(1);
            }

            if (text.StartsWith("piece_", StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring("piece_".Length);
            }

            text = text
                .Replace("blackmarble", "black marble")
                .Replace("corewood", "core wood")
                .Replace("darkwood", "dark wood");

            var parts = text
                .Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => char.ToUpperInvariant(part[0]) + part.Substring(1))
                .ToArray();

            return parts.Length > 0 ? string.Join(" ", parts) : value.Trim();
        }

        private static string GetLocalizedName(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName))
            {
                return string.Empty;
            }

            try
            {
                var localized = LocalizationManager.Instance.TryTranslate(rawName);
                if (!string.IsNullOrWhiteSpace(localized) && !string.Equals(localized, rawName, StringComparison.Ordinal))
                {
                    return localized.Trim();
                }
            }
            catch (Exception ex)
            {
                BuildMenuPlugin.Instance.Log.LogWarning($"Failed to translate display token '{rawName}': {ex.Message}");
            }

            return HumanizeToken(rawName);
        }
    }
}
