using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Reflection;
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
        public const string ModGuid = "com.improperyour.buildmenufilter";
        public const string ModName = "Build Menu";
        public const string ModVersion = "0.9.1";

        internal static BuildMenuPlugin Instance;
        internal static Harmony Harmony;

        private ConfigEntry<bool> _enabled;
        private ConfigEntry<string> _defaultPrimary;
        private ConfigEntry<string> _defaultSecondary;
        private ConfigEntry<bool> _logUnknownPieces;
        private ConfigEntry<bool> _debugLogging;
        private ConfigEntry<bool> _perfLogging;
        private ConfigEntry<float> _perfLoggingIntervalSeconds;
        private ConfigEntry<float> _perfWarningThresholdMs;
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
        private string _verboseLogPath;
        private readonly object _verboseLogSync = new();
        private readonly HashSet<string> _loggedUnknownPiecePrefabs = new(StringComparer.OrdinalIgnoreCase);

        internal readonly PieceClassificationRegistry Registry = new PieceClassificationRegistry();
        internal readonly HashSet<string> ConfiguredPrefabs = new(StringComparer.OrdinalIgnoreCase);
        internal ManualLogSource Log => Logger;

        private void Awake()
        {
            Instance = this;

            _enabled = Config.Bind("General", "Enabled", true, "Enable the build menu filter UI.");
            _defaultPrimary = Config.Bind("General", "DefaultPrimaryCategory", "All", "Default left-side category.");
            _defaultSecondary = Config.Bind("General", "DefaultSecondaryCategory", "All", "Default top category.");
            _primaryUpShortcut = Config.Bind("Input", "PrimaryUpShortcut", new KeyboardShortcut(KeyCode.W), "Select previous primary category.");
            _primaryDownShortcut = Config.Bind("Input", "PrimaryDownShortcut", new KeyboardShortcut(KeyCode.S), "Select next primary category.");
            _secondaryLeftShortcut = Config.Bind("Input", "SecondaryLeftShortcut", new KeyboardShortcut(KeyCode.A), "Select previous secondary category.");
            _secondaryRightShortcut = Config.Bind("Input", "SecondaryRightShortcut", new KeyboardShortcut(KeyCode.D), "Select next secondary category.");
            _pagePrevShortcut = Config.Bind("Input", "PagePrevShortcut", new KeyboardShortcut(KeyCode.Q), "Go to previous page.");
            _pageNextShortcut = Config.Bind("Input", "PageNextShortcut", new KeyboardShortcut(KeyCode.E), "Go to next page.");
            _uiOffsetLeft = Config.Bind("Layout", "UiOffsetLeft", 24f, "Left offset in pixels from the top-left of the build HUD.");
            _uiOffsetTop = Config.Bind("Layout", "UiOffsetTop", 124f, "Top offset in pixels from the top-left of the build HUD.");
            _logUnknownPieces = Config.Bind("Debug", "LogUnknownPieces", false, "Log pieces that do not have a classification.");
            _debugLogging = Config.Bind("Debug", "VerboseLogging", false, "Emit diagnostic logging for UI and piece filtering.");
            _dumpLibraryShortcut = Config.Bind("Debug", "DumpLibraryShortcut", new KeyboardShortcut(KeyCode.F12), "Dump the full build piece library to JSON.");
            _perfLogging = Config.Bind("Performance", "PerformanceLogging", false, "Emit periodic aggregated timing for BuildMenu hot paths.");
            _perfLoggingIntervalSeconds = Config.Bind("Performance", "PerformanceLoggingIntervalSeconds", 5f, "How often to emit performance timing summaries.");
            _perfWarningThresholdMs = Config.Bind("Performance", "PerformanceWarningThresholdMs", 2f, "Tag sections as WARN when average time exceeds this many milliseconds.");

            InitializeVerboseLogFile();
            LoadClassifications();

            Harmony = new Harmony(ModGuid);
            Harmony.PatchAll();

            LogInfo($"{ModName} {ModVersion} loaded");
        }

        private void OnDestroy()
        {
            BuildMenuPerformance.Flush(force: true);
            Harmony?.UnpatchSelf();
        }

        internal bool IsEnabled() => _enabled.Value;
        internal string GetDefaultPrimary() => _defaultPrimary.Value;
        internal string GetDefaultSecondary() => _defaultSecondary.Value;
        internal bool ShouldLogUnknownPieces() => _logUnknownPieces.Value;
        internal bool ShouldDebugLog() => _debugLogging.Value;
        internal bool ShouldPerfLog() => _perfLogging.Value;
        internal float GetPerfLogIntervalSeconds() => _perfLoggingIntervalSeconds.Value;
        internal float GetPerfWarningThresholdMs() => _perfWarningThresholdMs.Value;
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
        internal void LogInfo(string message)
        {
            Logger.LogInfo(message);
            WriteVerboseLine("INFO", message);
        }

        internal void DebugLog(string message)
        {
            if (_debugLogging.Value)
            {
                WriteVerboseLine("DEBUG", message);
            }
        }

        internal void LogUnknownPieceOnce(string prefabName)
        {
            if (string.IsNullOrWhiteSpace(prefabName))
            {
                return;
            }

            lock (_loggedUnknownPiecePrefabs)
            {
                if (!_loggedUnknownPiecePrefabs.Add(prefabName))
                {
                    return;
                }
            }

            LogInfo($"No classification for piece: {prefabName}");
        }

        private void InitializeVerboseLogFile()
        {
            var directoryPath = Path.Combine(Paths.ConfigPath, "BuildMenuSorter");
            Directory.CreateDirectory(directoryPath);
            _verboseLogPath = Path.Combine(directoryPath, "BuildMenuSorter.log");
            var sessionLine = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [INFO] === Session started {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ==={Environment.NewLine}";
            lock (_verboseLogSync)
            {
                File.WriteAllText(_verboseLogPath, sessionLine);
            }
        }

        private void WriteVerboseLine(string level, string message)
        {
            if (string.IsNullOrWhiteSpace(_verboseLogPath))
            {
                return;
            }

            try
            {
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
                lock (_verboseLogSync)
                {
                    File.AppendAllText(_verboseLogPath, line + Environment.NewLine);
                }
            }
            catch
            {
                // Ignore file write failures to avoid breaking gameplay.
            }
        }

        private void LoadClassifications()
        {
            Registry.Clear();
            ConfiguredPrefabs.Clear();
            lock (_loggedUnknownPiecePrefabs)
            {
                _loggedUnknownPiecePrefabs.Clear();
            }
            var categoryMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var directoryPath = Path.Combine(Paths.ConfigPath, "BuildMenuSorter");
            if (!Directory.Exists(directoryPath))
            {
                throw new DirectoryNotFoundException($"Build menu classification directory could not be found: {directoryPath}");
            }

            var files = Directory
                .GetFiles(directoryPath, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (files.Count == 0)
            {
                throw new FileNotFoundException($"No build menu classification JSON files were found in: {directoryPath}");
            }

            var serializer = new DataContractJsonSerializer(
                typeof(BuildMenuClassificationFile),
                new DataContractJsonSerializerSettings
                {
                    UseSimpleDictionaryFormat = true
                });
            var exactCount = 0;
            var duplicateCount = 0;
            foreach (var filePath in files)
            {
                BuildMenuClassificationFile root;
                using (var stream = File.OpenRead(filePath))
                {
                    root = serializer.ReadObject(stream) as BuildMenuClassificationFile;
                }

                if (root == null)
                {
                    throw new InvalidDataException($"Build menu classification file could not be parsed: {filePath}");
                }

                var fileExactCount = 0;
                var fileDuplicateCount = 0;
                foreach (var secondaryEntry in root.Exact ?? new Dictionary<string, Dictionary<string, List<ExactClassificationEntry>>>())
                {
                    RegisterCategoryMapping(categoryMap, secondaryEntry.Key, secondaryEntry.Value?.Keys);
                    foreach (var primaryEntry in secondaryEntry.Value ?? new Dictionary<string, List<ExactClassificationEntry>>())
                    {
                        foreach (var entry in primaryEntry.Value ?? new List<ExactClassificationEntry>())
                        {
                            var prefab = entry?.GetPrefab();
                            if (string.IsNullOrWhiteSpace(prefab))
                            {
                                continue;
                            }

                            ConfiguredPrefabs.Add(prefab);
                            var source = $"{Path.GetFileName(filePath)} [{secondaryEntry.Key}/{primaryEntry.Key}]";
                            if (Registry.Register(prefab, secondaryEntry.Key, primaryEntry.Key, source, out var previousSource))
                            {
                                exactCount++;
                                fileExactCount++;
                            }
                            else
                            {
                                duplicateCount++;
                                fileDuplicateCount++;
                                BuildMenuPlugin.Instance.Log.LogWarning($"Duplicate prefab '{prefab}' detected in '{source}' and '{previousSource}'. Marked as conflicted and routed to Unsorted.");
                            }
                        }
                    }
                }

                LogInfo($"Loaded classifications from {filePath}. exact={fileExactCount}, conflicts={Registry.GetConflictCount()}, duplicatesSeen={fileDuplicateCount}");
            }

            BuildMenuState.ConfigureCategories(categoryMap);
            LogInfo($"Loaded classification totals. files={files.Count}, exact={exactCount}, conflicts={Registry.GetConflictCount()}, duplicatesSeen={duplicateCount}");
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

    internal static class BuildMenuPerformance
    {
        private sealed class SampleBucket
        {
            public readonly List<double> Samples = new();
            public double TotalMs;
            public double MaxMs;
            public int OverWarningCount;
        }

        private static readonly Dictionary<string, SampleBucket> Buckets = new(StringComparer.Ordinal);
        private static readonly object Sync = new();
        private static DateTime _windowStartUtc = DateTime.UtcNow;

        public static long Start()
        {
            return Stopwatch.GetTimestamp();
        }

        public static void End(string section, long startTicks)
        {
            var plugin = BuildMenuPlugin.Instance;
            if (plugin == null || !plugin.ShouldPerfLog() || string.IsNullOrWhiteSpace(section))
            {
                return;
            }

            var elapsedMs = (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
            var warningThresholdMs = Math.Max(0f, plugin.GetPerfWarningThresholdMs());
            lock (Sync)
            {
                if (!Buckets.TryGetValue(section, out var bucket))
                {
                    bucket = new SampleBucket();
                    Buckets[section] = bucket;
                }

                bucket.Samples.Add(elapsedMs);
                bucket.TotalMs += elapsedMs;
                bucket.MaxMs = Math.Max(bucket.MaxMs, elapsedMs);
                if (elapsedMs >= warningThresholdMs)
                {
                    bucket.OverWarningCount++;
                }
            }

            Flush();
        }

        public static void Flush(bool force = false)
        {
            var plugin = BuildMenuPlugin.Instance;
            if (plugin == null || !plugin.ShouldPerfLog())
            {
                return;
            }

            var intervalSeconds = Math.Max(1f, plugin.GetPerfLogIntervalSeconds());
            var now = DateTime.UtcNow;
            List<KeyValuePair<string, SampleBucket>> snapshot;
            TimeSpan elapsed;
            lock (Sync)
            {
                elapsed = now - _windowStartUtc;
                if (!force && elapsed.TotalSeconds < intervalSeconds)
                {
                    return;
                }

                if (Buckets.Count == 0)
                {
                    _windowStartUtc = now;
                    return;
                }

                snapshot = Buckets
                    .Select(entry => new KeyValuePair<string, SampleBucket>(entry.Key, entry.Value))
                    .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .ToList();

                Buckets.Clear();
                _windowStartUtc = now;
            }

            foreach (var entry in snapshot)
            {
                var section = entry.Key;
                var bucket = entry.Value;
                var count = bucket.Samples.Count;
                if (count == 0)
                {
                    continue;
                }

                bucket.Samples.Sort();
                var averageMs = bucket.TotalMs / count;
                var p95Ms = bucket.Samples[(int)Math.Floor((count - 1) * 0.95)];
                var warningRate = bucket.OverWarningCount * 100.0 / count;
                var level = averageMs >= plugin.GetPerfWarningThresholdMs() ? "WARN" : "INFO";
                plugin.LogInfo(
                    $"PERF[{level}] {section}: n={count}, avg={averageMs:F3}ms, p95={p95Ms:F3}ms, max={bucket.MaxMs:F3}ms, overWarn={warningRate:F1}% (window={elapsed.TotalSeconds:F1}s)");
            }
        }
    }

    [DataContract]
    internal sealed class BuildMenuClassificationFile
    {
        [DataMember(Name = "exact")]
        public Dictionary<string, Dictionary<string, List<ExactClassificationEntry>>> Exact { get; set; }
    }

    [DataContract]
    internal sealed class ExactClassificationEntry
    {
        [DataMember(Name = "Prefab")]
        public string Prefab { get; set; }

        [DataMember(Name = "prefab")]
        public string PrefabLower { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }

        public string GetPrefab()
        {
            return !string.IsNullOrWhiteSpace(Prefab) ? Prefab : PrefabLower;
        }
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
        public const string Unsorted = "Unsorted";
        public const int PageSize = 90;

        public static string SelectedPrimary = All;
        public static string SelectedSecondary = All;
        public static string SearchText = string.Empty;
        public static bool Initialized;
        public static int CurrentPage;

        public static readonly List<string> PrimaryCategories = new();
        public static readonly Dictionary<string, List<string>> SecondaryCategories = new(StringComparer.OrdinalIgnoreCase);

        public static void ConfigureCategories(IReadOnlyDictionary<string, List<string>> categoryMap)
        {
            PrimaryCategories.Clear();
            SecondaryCategories.Clear();

            PrimaryCategories.Add(All);
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
                }

                SecondaryCategories[entry.Key] = secondary;
            }

            if (!PrimaryCategories.Contains(Unsorted))
            {
                PrimaryCategories.Add(Unsorted);
            }

            SecondaryCategories[All] = new List<string> { All };
            SecondaryCategories[Unsorted] = new List<string> { All };
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

        public static void SetSearchText(string value)
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (string.Equals(SearchText, normalized, StringComparison.Ordinal))
            {
                return;
            }

            SearchText = normalized;
            CurrentPage = 0;
        }

        public static bool IsSearchApplicable(string primary, string secondary)
        {
            return string.Equals(secondary, All, StringComparison.OrdinalIgnoreCase);
        }

        public static bool HasActiveSearch(string primary, string secondary)
        {
            return IsSearchApplicable(primary, secondary) && !string.IsNullOrWhiteSpace(SearchText);
        }
    }

    internal sealed class PieceClassificationRegistry
    {
        private readonly Dictionary<string, PieceClassification> _exact = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _sources = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _conflicts = new(StringComparer.OrdinalIgnoreCase);

        public bool Register(string pieceName, string primary, string secondary, string source, out string previousSource)
        {
            previousSource = string.Empty;
            if (string.IsNullOrWhiteSpace(pieceName))
            {
                return false;
            }

            if (_conflicts.Contains(pieceName))
            {
                previousSource = _sources.TryGetValue(pieceName, out var conflictedSource) ? conflictedSource : "<conflicted>";
                return false;
            }

            if (_exact.ContainsKey(pieceName))
            {
                previousSource = _sources.TryGetValue(pieceName, out var existingSource) ? existingSource : "<unknown>";
                _exact.Remove(pieceName);
                _conflicts.Add(pieceName);
                _sources[pieceName] = $"{previousSource}, {source}";
                return false;
            }

            _exact[pieceName] = new PieceClassification(primary, secondary);
            _sources[pieceName] = source ?? string.Empty;
            return true;
        }

        public void Clear()
        {
            _exact.Clear();
            _sources.Clear();
            _conflicts.Clear();
        }

        public int GetConflictCount()
        {
            return _conflicts.Count;
        }

        public bool TryGetClassification(GameObject piecePrefab, out PieceClassification classification)
        {
            classification = default;
            if (!piecePrefab)
            {
                return false;
            }

            var stableName = Utils.GetPrefabName(piecePrefab);
            if (!string.IsNullOrEmpty(stableName) && _conflicts.Contains(stableName))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(stableName) && _exact.TryGetValue(stableName, out classification))
            {
                return true;
            }

            return false;
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
                    if (piece != null && Matches(piece.gameObject, primary, secondary, applySearch: false))
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
            return Matches(piece, BuildMenuState.SelectedPrimary, BuildMenuState.SelectedSecondary, applySearch: true);
        }

        private static bool Matches(GameObject piece, string selectedPrimary, string selectedSecondary, bool applySearch)
        {
            var registry = BuildMenuPlugin.Instance.Registry;
            if (!registry.TryGetClassification(piece, out var classification))
            {
                if (BuildMenuPlugin.Instance.ShouldLogUnknownPieces())
                {
                    BuildMenuPlugin.Instance.LogUnknownPieceOnce(Utils.GetPrefabName(piece));
                }

                var isAllAll = string.Equals(selectedPrimary, BuildMenuState.All, StringComparison.OrdinalIgnoreCase)
                               && string.Equals(selectedSecondary, BuildMenuState.All, StringComparison.OrdinalIgnoreCase);
                var isUnsorted = string.Equals(selectedPrimary, BuildMenuState.Unsorted, StringComparison.OrdinalIgnoreCase)
                                 && string.Equals(selectedSecondary, BuildMenuState.All, StringComparison.OrdinalIgnoreCase);
                return (isAllAll || isUnsorted) && SearchMatches(piece, selectedPrimary, selectedSecondary, applySearch);
            }

            if (string.Equals(selectedPrimary, BuildMenuState.All, StringComparison.OrdinalIgnoreCase))
            {
                var categoryMatch = string.Equals(selectedSecondary, BuildMenuState.All, StringComparison.OrdinalIgnoreCase)
                       || string.Equals(classification.Secondary, selectedSecondary, StringComparison.OrdinalIgnoreCase)
                       || string.Equals(classification.Secondary, BuildMenuState.All, StringComparison.OrdinalIgnoreCase);
                return categoryMatch && SearchMatches(piece, selectedPrimary, selectedSecondary, applySearch);
            }

            if (!string.Equals(classification.Primary, selectedPrimary, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.Equals(selectedSecondary, BuildMenuState.All, StringComparison.OrdinalIgnoreCase))
            {
                return SearchMatches(piece, selectedPrimary, selectedSecondary, applySearch);
            }

            var secondaryMatch = string.Equals(classification.Secondary, selectedSecondary, StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(classification.Secondary, BuildMenuState.All, StringComparison.OrdinalIgnoreCase);
            return secondaryMatch && SearchMatches(piece, selectedPrimary, selectedSecondary, applySearch);
        }

        private static bool SearchMatches(GameObject piece, string selectedPrimary, string selectedSecondary, bool applySearch)
        {
            if (!applySearch || !BuildMenuState.HasActiveSearch(selectedPrimary, selectedSecondary))
            {
                return true;
            }

            var search = BuildMenuState.SearchText;
            if (string.IsNullOrWhiteSpace(search))
            {
                return true;
            }

            var haystack = Utils.GetSearchableText(piece);
            return !string.IsNullOrWhiteSpace(haystack)
                   && haystack.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    internal static class BuildMenuUI
    {
        private const string RootName = "BuildMenuRoot";
        private const string PrimaryColumnName = "PrimaryColumn";
        private const string SecondaryRowName = "SecondaryRow";
        private const string PagingRowName = "PagingRow";
        private const string SearchRowName = "SearchRow";
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
        private const float SearchInputWidth = 200f;

        private static GameObject _root;
        private static GameObject _primaryColumn;
        private static GameObject _secondaryRow;
        private static GameObject _pagingRow;
        private static GameObject _searchRow;
        private static InputField _searchInput;
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
            _searchRow = null;
            _searchInput = null;
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

            if (_searchInput != null && _searchInput.isFocused)
            {
                var pressedWhileTyping = IsNavigationInputPressed();
                if (pressedWhileTyping)
                {
                    Utils.ConsumeNavigationInput();
                    return true;
                }

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

        public static bool IsSearchFocused()
        {
            return _root != null && _root.activeSelf && _searchInput != null && _searchInput.isFocused;
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
            BuildMenuPlugin.Instance.LogInfo($"Dumped {entries.Count} build pieces to {path}");
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
            var secondaryBackground = _secondaryRow.GetComponent<Image>();
            if (secondaryBackground != null)
            {
                secondaryBackground.enabled = false;
            }
            var secondaryLayout = _secondaryRow.AddComponent<HorizontalLayoutGroup>();
            secondaryLayout.childControlHeight = true;
            secondaryLayout.childControlWidth = false;
            secondaryLayout.childForceExpandHeight = true;
            secondaryLayout.childForceExpandWidth = false;
            secondaryLayout.spacing = SecondaryLayoutSpacing;
            secondaryLayout.padding = new RectOffset(SecondaryLayoutPadding, SecondaryLayoutPadding, SecondaryLayoutPadding, SecondaryLayoutPadding);

            _pagingRow = CreatePanel(PagingRowName, _root.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(SecondaryPanelX, -60f), new Vector2(GetSecondaryPanelWidth(), 40f));
            var pagingBackground = _pagingRow.GetComponent<Image>();
            if (pagingBackground != null)
            {
                pagingBackground.enabled = false;
            }
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

            _searchRow = CreatePanel(SearchRowName, _root.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(SecondaryPanelX, -108f), new Vector2(GetSecondaryPanelWidth(), 36f));
            var searchBackground = _searchRow.GetComponent<Image>();
            if (searchBackground != null)
            {
                searchBackground.enabled = false;
            }
            _searchInput = CreateSearchInput(_searchRow.transform, BuildMenuState.SearchText);

            var title = CreateLabel("Title", _root.transform, "Build Filters");
            var titleRect = title.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(0f, 1f);
            titleRect.pivot = new Vector2(0f, 1f);
            titleRect.anchoredPosition = new Vector2(SecondaryPanelX, 24f);
            titleRect.sizeDelta = new Vector2(300f, 24f);
        }

        private static InputField CreateSearchInput(Transform parent, string initialValue)
        {
            var inputObject = new GameObject("SearchInput", typeof(RectTransform), typeof(Image), typeof(InputField));
            inputObject.transform.SetParent(parent, false);
            var inputRect = inputObject.GetComponent<RectTransform>();
            inputRect.anchorMin = new Vector2(0f, 0.5f);
            inputRect.anchorMax = new Vector2(0f, 0.5f);
            inputRect.pivot = new Vector2(0f, 0.5f);
            inputRect.anchoredPosition = new Vector2(8f, 0f);
            inputRect.sizeDelta = new Vector2(SearchInputWidth, 26f);

            var background = inputObject.GetComponent<Image>();
            background.color = new Color(0.10f, 0.10f, 0.10f, 0.92f);

            var input = inputObject.GetComponent<InputField>();
            input.lineType = InputField.LineType.SingleLine;

            var textObject = new GameObject("Text", typeof(RectTransform), typeof(Text));
            textObject.transform.SetParent(inputObject.transform, false);
            var textRect = textObject.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(8f, 4f);
            textRect.offsetMax = new Vector2(-28f, -4f);
            var text = textObject.GetComponent<Text>();
            text.font = GetUIFont();
            text.fontSize = 16;
            text.alignment = TextAnchor.MiddleLeft;
            text.color = Color.white;
            text.supportRichText = false;

            var placeholderObject = new GameObject("Placeholder", typeof(RectTransform), typeof(Text));
            placeholderObject.transform.SetParent(inputObject.transform, false);
            var placeholderRect = placeholderObject.GetComponent<RectTransform>();
            placeholderRect.anchorMin = Vector2.zero;
            placeholderRect.anchorMax = Vector2.one;
            placeholderRect.offsetMin = new Vector2(8f, 4f);
            placeholderRect.offsetMax = new Vector2(-28f, -4f);
            var placeholderText = placeholderObject.GetComponent<Text>();
            placeholderText.font = GetUIFont();
            placeholderText.fontSize = 16;
            placeholderText.alignment = TextAnchor.MiddleLeft;
            placeholderText.color = new Color(0.72f, 0.72f, 0.72f, 0.85f);
            placeholderText.supportRichText = false;
            placeholderText.text = "Search by name...";

            input.textComponent = text;
            input.placeholder = placeholderText;
            input.SetTextWithoutNotify(initialValue ?? string.Empty);
            input.onValueChanged.AddListener(OnSearchValueChanged);

            var clearButtonObject = new GameObject("ClearSearch", typeof(RectTransform), typeof(Image), typeof(Button));
            clearButtonObject.transform.SetParent(inputObject.transform, false);
            var clearRect = clearButtonObject.GetComponent<RectTransform>();
            clearRect.anchorMin = new Vector2(1f, 0.5f);
            clearRect.anchorMax = new Vector2(1f, 0.5f);
            clearRect.pivot = new Vector2(1f, 0.5f);
            clearRect.anchoredPosition = new Vector2(-4f, 0f);
            clearRect.sizeDelta = new Vector2(18f, 18f);

            var clearImage = clearButtonObject.GetComponent<Image>();
            clearImage.color = new Color(0.25f, 0.25f, 0.25f, 0.95f);

            var clearTextObject = new GameObject("Text", typeof(RectTransform), typeof(Text));
            clearTextObject.transform.SetParent(clearButtonObject.transform, false);
            var clearTextRect = clearTextObject.GetComponent<RectTransform>();
            clearTextRect.anchorMin = Vector2.zero;
            clearTextRect.anchorMax = Vector2.one;
            clearTextRect.offsetMin = Vector2.zero;
            clearTextRect.offsetMax = Vector2.zero;
            var clearText = clearTextObject.GetComponent<Text>();
            clearText.font = GetUIFont();
            clearText.fontSize = 14;
            clearText.alignment = TextAnchor.MiddleCenter;
            clearText.color = Color.white;
            clearText.text = "X";

            var clearButton = clearButtonObject.GetComponent<Button>();
            clearButton.onClick.AddListener(() =>
            {
                input.SetTextWithoutNotify(string.Empty);
                OnSearchValueChanged(string.Empty);
            });
            return input;
        }

        private static void OnSearchValueChanged(string value)
        {
            BuildMenuState.SetSearchText(value);
            RefreshPagingState();
            RefreshPieceSelection();
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

            RefreshSearchState();
            RefreshPagingState();
        }

        private static void RefreshSearchState()
        {
            if (_searchRow == null)
            {
                return;
            }

            var shouldShow = _root != null
                             && _root.activeSelf
                             && BuildMenuState.IsSearchApplicable(BuildMenuState.SelectedPrimary, BuildMenuState.SelectedSecondary);
            _searchRow.SetActive(shouldShow);

            if (_searchInput != null)
            {
                var expected = BuildMenuState.SearchText ?? string.Empty;
                if (!string.Equals(_searchInput.text, expected, StringComparison.Ordinal))
                {
                    _searchInput.SetTextWithoutNotify(expected);
                }
            }
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
                var selectedBefore = Utils.GetSelectedBuildPiecePrefab(player);
                var selectedBeforeName = Utils.GetPrefabName(selectedBefore);
                var selectedBeforeIndex = Utils.GetPieceTableSelectedPieceIndex(buildPieces);
                Utils.InvokePlayerSetBuildCategory(player, 0);
                BuildMenuPlugin.Instance.DebugLog($"Refreshing piece selection for category={Utils.InvokePieceTableGetSelectedCategory(buildPieces)}");
                Utils.InvokeUpdateAvailablePiecesList(player);
                BuildMenuPatches.ApplyFilteredBuildPieces(player);
                Utils.InvokePlayerSetSelectedPiece(player, Vector2Int.zero);
                var selectedAfterSetIndex = Utils.GetPieceTableSelectedPieceIndex(buildPieces);
                var selectedAfterSet = Utils.GetSelectedBuildPiecePrefab(player);
                var selectedAfterSetName = Utils.GetPrefabName(selectedAfterSet);
                Utils.InvokeHudUpdateBuild(_hud, player, true);
                var selectedAfterHud = Utils.GetSelectedBuildPiecePrefab(player);
                var selectedAfterHudName = Utils.GetPrefabName(selectedAfterHud);
                BuildMenuPlugin.Instance.DebugLog(
                    $"RefreshPieceSelection trace. before={selectedBeforeName}@{selectedBeforeIndex}, afterSetSelected={selectedAfterSetName}@{selectedAfterSetIndex}, afterHudUpdate={selectedAfterHudName}@{Utils.GetPieceTableSelectedPieceIndex(buildPieces)}");
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
                        var requiredWidth = preferredLabelWidth + 24f;
                        ApplyTabWidth(tab, requiredWidth);
                    }
                }
            }

        }

        private static void ApplyTabWidth(GameObject tab, float requiredWidth)
        {
            if (tab == null || requiredWidth <= 0f)
            {
                return;
            }

            var layout = tab.GetComponent<LayoutElement>() ?? tab.AddComponent<LayoutElement>();
            layout.minWidth = Math.Max(layout.minWidth, requiredWidth);
            layout.preferredWidth = Math.Max(layout.preferredWidth, requiredWidth);

            var tabRect = tab.GetComponent<RectTransform>();
            if (tabRect != null)
            {
                var size = tabRect.sizeDelta;
                size.x = Math.Max(size.x, requiredWidth);
                tabRect.sizeDelta = size;
            }

            foreach (var image in tab.GetComponentsInChildren<Image>(true))
            {
                var rect = image.rectTransform;
                if (rect == null)
                {
                    continue;
                }

                // ReSharper disable once CompareOfFloatsByEqualityOperator
                var widthStretched = rect.anchorMin.x != rect.anchorMax.x;
                if (widthStretched)
                {
                    continue;
                }

                var size = rect.sizeDelta;
                size.x = Math.Max(size.x, requiredWidth);
                rect.sizeDelta = size;
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
        private static string _lastSelectedPieceSignature;
        private static string _lastSelectionMappingSignature;
        private static string _preferredPlacementPrefab;
        private static bool _knownDataDirty;
        private static string _knownDataDirtyReason;
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

            var totalStart = BuildMenuPerformance.Start();
            try
            {
                var ensureUiStart = BuildMenuPerformance.Start();
                BuildMenuUI.EnsureUI(__instance);
                BuildMenuPerformance.End("BuildMenuUI.EnsureUI", ensureUiStart);

                var player = Player.m_localPlayer;
                if (player != null)
                {
                    if (_knownDataDirty)
                    {
                        Utils.InvokeUpdateAvailablePiecesList(player);
                        _filteredPieceTable = null;
                        _originalAvailablePieces = null;
                        BuildMenuPlugin.Instance.DebugLog($"Known data refresh applied. reason={_knownDataDirtyReason ?? "<unknown>"}");
                        _knownDataDirty = false;
                        _knownDataDirtyReason = null;
                    }

                    ValidateConfiguredPrefabs(player);
                    var inPlaceMode = Utils.InvokePlayerInPlaceMode(player);
                    if (inPlaceMode)
                    {
                        if (Utils.InvokeHudIsPieceSelectionVisible())
                        {
                            ApplyFilteredBuildPieces(player);
                            LogSelectedBuildPiece(player);
                        }
                        else
                        {
                            EnsurePreferredPlacementSelection(player);
                        }
                    }
                    else
                    {
                        RestoreFilteredBuildPieces(player);
                        _lastSelectedPieceSignature = null;
                        _preferredPlacementPrefab = null;
                    }
                }

                var handleInputStart = BuildMenuPerformance.Start();
                BuildMenuUI.HandleInput();
                BuildMenuPerformance.End("BuildMenuUI.HandleInput", handleInputStart);
            }
            finally
            {
                BuildMenuPerformance.End("Hud.Update.Postfix.Total", totalStart);
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Player), "Update")]
        private static bool Player_Update_Prefix(Player __instance)
        {
            if (!ShouldRun() || __instance != Player.m_localPlayer)
            {
                return true;
            }

            if (BuildMenuUI.IsSearchFocused())
            {
                Utils.ConsumeNavigationInput();
                return false;
            }

            var navigationStart = BuildMenuPerformance.Start();
            BuildMenuUI.TryHandleNavigationInput();
            BuildMenuPerformance.End("BuildMenuUI.TryHandleNavigationInput", navigationStart);
            return true;
        }

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
                    BuildMenuPlugin.Instance.DebugLog($"Configured Prefab '{configuredPrefab}' was not found in the known build Prefab library.");
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

            var player = Player.m_localPlayer;
            var inPlaceMode = player != null && Utils.InvokePlayerInPlaceMode(player);
            if (!inPlaceMode)
            {
                RestoreFilteredBuildPieces(player);
                _lastSelectedPieceSignature = null;
                _preferredPlacementPrefab = null;
            }
            BuildMenuPlugin.Instance.DebugLog("Hud.HidePieceSelection postfix fired");
            BuildMenuUI.DestroyUI();
        }

        internal static void MarkKnownDataDirty(string reason)
        {
            _knownDataDirty = true;
            _knownDataDirtyReason = reason;
        }

        internal static void ApplyFilteredBuildPieces(Player player)
        {
            var perfStart = BuildMenuPerformance.Start();
            var buildPieces = player != null ? Utils.GetBuildPiecesTable(player) : null;
            var availablePieces = buildPieces != null ? Utils.GetAvailablePieceCategories(buildPieces) : null;
            if (!ShouldRun() || player == null || player != Player.m_localPlayer || buildPieces == null || availablePieces == null)
            {
                BuildMenuPerformance.End("ApplyFilteredBuildPieces", perfStart);
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
                var selectedBeforeIndex = Utils.GetPieceTableSelectedPieceIndex(buildPieces);
                var selectedBeforePrefab = Utils.GetSelectedBuildPiecePrefab(player);
                var selectedBeforePrefabName = Utils.GetPrefabName(selectedBeforePrefab);
                var projected = BuildMenuLogic.ProjectFilteredToSelectedCategory(sourcePieces, selectedCategoryIndex);
                Utils.SetAvailablePieceCategories(buildPieces, projected);
                var availableAfter = Utils.GetAvailablePieceCategories(buildPieces);

                var afterPieceCount = availableAfter.Sum(category => category?.Count ?? 0);
                var selectedAfterIndex = Utils.GetPieceTableSelectedPieceIndex(buildPieces);
                var selectedAfterPrefab = Utils.GetSelectedBuildPiecePrefab(player);
                var selectedAfterPrefabName = Utils.GetPrefabName(selectedAfterPrefab);
                if (!string.IsNullOrWhiteSpace(selectedBeforePrefabName)
                    && !string.Equals(selectedBeforePrefabName, selectedAfterPrefabName, StringComparison.OrdinalIgnoreCase))
                {
                    var reapplied = Utils.TrySelectPieceByPrefab(player, selectedBeforePrefabName);
                    if (reapplied)
                    {
                        selectedAfterPrefab = Utils.GetSelectedBuildPiecePrefab(player);
                        selectedAfterPrefabName = Utils.GetPrefabName(selectedAfterPrefab);
                        if (!Utils.InvokeHudIsPieceSelectionVisible())
                        {
                            Utils.RefreshPlacementGhost(player);
                        }
                    }
                }

                var signature = $"{beforePieceCount}->{afterPieceCount}|{BuildMenuState.SelectedPrimary}|{BuildMenuState.SelectedSecondary}|{selectedCategoryIndex}";
                if (!string.Equals(signature, _lastUpdateBuildSignature, StringComparison.Ordinal))
                {
                    BuildMenuPlugin.Instance.DebugLog($"Hud.UpdateBuild prefix fired. pieces {beforePieceCount}->{afterPieceCount}, primary={BuildMenuState.SelectedPrimary}, secondary={BuildMenuState.SelectedSecondary}, selectedCategoryIndex={selectedCategoryIndex}");
                    _lastUpdateBuildSignature = signature;
                }

                if (BuildMenuPlugin.Instance.ShouldDebugLog())
                {
                    var beforePrefabName = selectedBeforePrefabName;
                    var afterPrefabName = selectedAfterPrefabName;
                    var projectedSlice = Utils.DescribeCategorySlice(projected, selectedCategoryIndex, 20);
                    var availableSlice = Utils.DescribeCategorySlice(availableAfter, selectedCategoryIndex, 20);
                    var mappingSignature = $"{selectedCategoryIndex}|{selectedBeforeIndex}|{selectedAfterIndex}|{beforePrefabName}|{afterPrefabName}|{projectedSlice}|{availableSlice}";
                    var meaningfulChange = selectedBeforeIndex != selectedAfterIndex
                                           || !string.Equals(beforePrefabName, afterPrefabName, StringComparison.OrdinalIgnoreCase);
                    if (meaningfulChange && !string.Equals(mappingSignature, _lastSelectionMappingSignature, StringComparison.Ordinal))
                    {
                        _lastSelectionMappingSignature = mappingSignature;
                        BuildMenuPlugin.Instance.DebugLog(
                            $"Selection mapping trace. catIndex={selectedCategoryIndex}, selectedBeforeIndex={selectedBeforeIndex}, selectedAfterIndex={selectedAfterIndex}, selectedBeforePrefab={beforePrefabName}, selectedAfterPrefab={afterPrefabName}, projectedSlice={projectedSlice}, availableSlice={availableSlice}");
                    }
                }
            }
            catch (Exception ex)
            {
                BuildMenuPlugin.Instance.Log.LogError($"Failed to apply filtered build pieces: {ex}");
            }
            finally
            {
                BuildMenuPerformance.End("ApplyFilteredBuildPieces", perfStart);
            }
        }

        internal static void RestoreFilteredBuildPieces(Player player = null)
        {
            var selectedPrefabName = player != null
                ? Utils.GetPrefabName(Utils.GetSelectedBuildPiecePrefab(player))
                : string.Empty;

            if (_filteredPieceTable != null && _originalAvailablePieces != null)
            {
                Utils.SetAvailablePieceCategories(_filteredPieceTable, _originalAvailablePieces);
            }

            if (player != null && !string.IsNullOrWhiteSpace(selectedPrefabName))
            {
                Utils.TrySelectPieceByPrefab(player, selectedPrefabName);
            }

            _filteredPieceTable = null;
            _originalAvailablePieces = null;
            _lastSelectionMappingSignature = null;
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

        private static void LogSelectedBuildPiece(Player player)
        {
            var selectedPrefab = Utils.GetSelectedBuildPiecePrefab(player);
            if (!selectedPrefab)
            {
                return;
            }

            var prefab = Utils.GetPrefabName(selectedPrefab);
            if (string.IsNullOrWhiteSpace(prefab))
            {
                return;
            }

            var signature = $"{prefab}|{BuildMenuState.SelectedPrimary}|{BuildMenuState.SelectedSecondary}|{BuildMenuState.CurrentPage}";
            if (string.Equals(signature, _lastSelectedPieceSignature, StringComparison.Ordinal))
            {
                return;
            }

            _lastSelectedPieceSignature = signature;
            _preferredPlacementPrefab = prefab;
            var displayName = Utils.GetDisplayName(selectedPrefab);
            var token = Utils.GetRawDisplayToken(selectedPrefab);
            var category = Utils.GetPieceCategory(selectedPrefab);
            var craftingStation = Utils.GetCraftingStationName(selectedPrefab);
            var systemEffects = string.Join(", ", Utils.GetSystemEffects(selectedPrefab));
            var interactionHooks = string.Join(", ", Utils.GetInteractionHooks(selectedPrefab));
            var required = string.Join(", ", Utils.GetRequiredItemNames(selectedPrefab).Select(req => $"{req.Required}:{req.Amount}"));

            BuildMenuPlugin.Instance.DebugLog(
                $"Build picker selection changed. prefab={prefab}, name={displayName}, token={token}, category={category}, craftingStation={craftingStation}, systemEffects=[{systemEffects}], interactionHooks=[{interactionHooks}], required=[{required}]");
        }

        internal static string GetPreferredPlacementPrefab()
        {
            return _preferredPlacementPrefab;
        }

        private static void EnsurePreferredPlacementSelection(Player player)
        {
            var preferred = _preferredPlacementPrefab;
            if (string.IsNullOrWhiteSpace(preferred))
            {
                return;
            }

            var current = Utils.GetPrefabName(Utils.GetSelectedBuildPiecePrefab(player));
            if (string.Equals(current, preferred, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var reapplied = Utils.TrySelectPieceByPrefab(player, preferred);
            if (reapplied)
            {
                BuildMenuPlugin.Instance.DebugLog($"Hidden-mode selection lock applied. preferred={preferred}, previous={current}");
            }
        }

    }

    [HarmonyPatch]
    internal static class BuildMenuKnownDataPatches
    {
        private static List<MethodBase> _targets;

        private static readonly string[] CandidateMethodNames =
        {
            "AddKnownItem",
            "AddKnownMaterial",
            "AddKnownRecipe",
            "AddKnownPiece",
            "AddKnownStation",
            "UpdateKnownRecipes"
        };

        // ReSharper disable InconsistentNaming
        [HarmonyPrepare]
        private static bool Prepare()
        {
            _targets = AccessTools.GetDeclaredMethods(typeof(Player))
                .Where(method => CandidateMethodNames.Contains(method.Name, StringComparer.Ordinal))
                .Cast<MethodBase>()
                .ToList();

            if (_targets.Count == 0)
            {
                BuildMenuPlugin.Instance?.Log.LogWarning("No compatible Player known-data methods found; unlock-triggered build refresh is disabled.");
                return false;
            }

            BuildMenuPlugin.Instance?.DebugLog($"Known-data refresh hooks attached to: {string.Join(", ", _targets.Select(target => target.Name).Distinct().OrderBy(name => name, StringComparer.Ordinal))}");
            return true;
        }

        private static IEnumerable<MethodBase> TargetMethods()
        {
            return _targets ?? Enumerable.Empty<MethodBase>();
        }

        [HarmonyPostfix]
        private static void Postfix(Player __instance, MethodBase __originalMethod)
        {
            if (BuildMenuPlugin.Instance == null || __instance != Player.m_localPlayer || !BuildMenuPlugin.Instance.IsEnabled())
            {
                return;
            }

            BuildMenuPatches.MarkKnownDataDirty(__originalMethod?.Name ?? "KnownDataChanged");
        }
        // ReSharper enable InconsistentNaming
    }

    [HarmonyPatch]
    internal static class BuildPlacementPatches
    {
        private static readonly HashSet<string> CandidateNames = new(StringComparer.Ordinal)
        {
            "PlacePiece",
            "TryPlacePiece"
        };

        private static readonly HashSet<string> LoggedTargets = new(StringComparer.Ordinal);

        // ReSharper disable once UnusedMember.Local
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var methods = AccessTools.GetDeclaredMethods(typeof(Player))
                .Where(method => CandidateNames.Contains(method.Name))
                .Cast<MethodBase>()
                .ToList();

            foreach (var method in methods)
            {
                if (LoggedTargets.Add(method.ToString()))
                {
                    BuildMenuPlugin.Instance?.DebugLog($"Placement tracing attached to: {method.DeclaringType?.Name}.{method}");
                }
            }

            return methods;
        }

        // ReSharper disable once UnusedMember.Local
        private static void Prefix(Player __instance, MethodBase __originalMethod, object[] __args)
        {
            if (BuildMenuPlugin.Instance == null || __instance != Player.m_localPlayer || !BuildMenuPlugin.Instance.IsEnabled())
            {
                return;
            }

            var selected = Utils.GetSelectedBuildPiecePrefab(__instance);
            var selectedPrefab = Utils.GetPrefabName(selected);
            var argPrefab = Utils.GetPlacementArgPrefab(__args);
            var originalArgPrefab = argPrefab;
            if (string.Equals(__originalMethod?.Name, "TryPlacePiece", StringComparison.Ordinal)
                || string.Equals(__originalMethod?.Name, "PlacePiece", StringComparison.Ordinal))
            {
                var preferredPrefab = BuildMenuPatches.GetPreferredPlacementPrefab();
                if (!string.IsNullOrWhiteSpace(preferredPrefab)
                    && !string.Equals(argPrefab, preferredPrefab, StringComparison.OrdinalIgnoreCase))
                {
                    var preferredPiece = Utils.GetPieceByPrefabForPlayer(__instance, preferredPrefab);
                    if (preferredPiece != null && __args != null && __args.Length > 0)
                    {
                        Utils.TrySelectPieceByPrefab(__instance, preferredPrefab);
                        __args[0] = preferredPiece;
                        argPrefab = preferredPrefab;
                        selectedPrefab = preferredPrefab;
                        BuildMenuPlugin.Instance.DebugLog($"Placement override applied. method={__originalMethod.Name}, preferred={preferredPrefab}, incoming={originalArgPrefab}");
                    }
                }
            }
            var argSummary = Utils.DescribePlacementArgs(__args);
            BuildMenuPlugin.Instance.DebugLog(
                $"Placement prefix: method={__originalMethod?.Name}, selected={selectedPrefab}, argPrefab={argPrefab}, args={argSummary}, primary={BuildMenuState.SelectedPrimary}, secondary={BuildMenuState.SelectedSecondary}, page={BuildMenuState.CurrentPage}");
        }

        // ReSharper disable once UnusedMember.Local
        private static void Postfix(Player __instance, MethodBase __originalMethod, object[] __args)
        {
            if (BuildMenuPlugin.Instance == null || __instance != Player.m_localPlayer || !BuildMenuPlugin.Instance.IsEnabled())
            {
                return;
            }

            var selectedAfter = Utils.GetSelectedBuildPiecePrefab(__instance);
            var selectedAfterPrefab = Utils.GetPrefabName(selectedAfter);
            BuildMenuPlugin.Instance.DebugLog(
                $"Placement postfix: method={__originalMethod?.Name}, selectedAfter={selectedAfterPrefab}, args={Utils.DescribePlacementArgs(__args)}");

            // No post-place reselection/ghost manipulation here. In this Valheim version,
            // forcing selection after TryPlacePiece causes a preview desync (coin/sap drift).
        }
        // ReSharper enable InconsistentNaming
    }

    internal static class Utils
    {
        private static readonly FieldInfo BuildPiecesField = AccessTools.Field(typeof(Player), "m_buildPieces");
        private static readonly FieldInfo PieceResourcesField = AccessTools.Field(typeof(Piece), "m_resources");
        private static readonly Type PieceRequirementType = AccessTools.TypeByName("Piece+Requirement");
        private static readonly FieldInfo RequirementResItemField = PieceRequirementType != null
            ? AccessTools.Field(PieceRequirementType, "m_resItem")
            : null;
        private static readonly FieldInfo RequirementAmountField = PieceRequirementType != null
            ? AccessTools.Field(PieceRequirementType, "m_amount")
            : null;
        private static readonly FieldInfo ItemDropItemDataField = AccessTools.Field(typeof(ItemDrop), "m_itemData");
        private static readonly FieldInfo ItemSharedDataField = AccessTools.Field(typeof(ItemDrop.ItemData), "m_shared");
        private static readonly FieldInfo SharedNameField = ItemSharedDataField != null
            ? AccessTools.Field(ItemSharedDataField.FieldType, "m_name")
            : null;
        private static readonly FieldInfo SharedBuildPiecesField = ItemSharedDataField != null
            ? AccessTools.Field(ItemSharedDataField.FieldType, "m_buildPieces")
            : null;
        private static readonly FieldInfo AvailablePiecesField = AccessTools.Field(typeof(PieceTable), "m_availablePieces");
        private static readonly FieldInfo PieceTablePiecesField = AccessTools.Field(typeof(PieceTable), "m_pieces");
        private static readonly FieldInfo HudBuildHudField = AccessTools.Field(typeof(Hud), "m_buildHud");
        private static readonly FieldInfo HudPieceSelectionWindowField = AccessTools.Field(typeof(Hud), "m_pieceSelectionWindow");
        private static readonly FieldInfo HudPieceCategoryTabsField = AccessTools.Field(typeof(Hud), "m_pieceCategoryTabs");
        private static readonly FieldInfo PieceNameField = AccessTools.Field(typeof(Piece), "m_name");
        private static readonly FieldInfo PieceCategoryField = AccessTools.Field(typeof(Piece), "m_category");
        private static readonly FieldInfo PieceCraftingStationField = AccessTools.Field(typeof(Piece), "m_craftingStation");
        private static readonly FieldInfo PieceComfortField = AccessTools.Field(typeof(Piece), "m_comfort");
        private static readonly FieldInfo PieceTableNameField = AccessTools.Field(typeof(PieceTable), "m_name");
        private static readonly FieldInfo CraftingStationNameField = AccessTools.Field(typeof(CraftingStation), "m_name");
        private static readonly MethodInfo PieceGetHoverNameMethod =
            typeof(Piece).GetMethod("GetHoverName",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo UpdateAvailablePiecesListMethod = AccessTools.Method(typeof(Player), "UpdateAvailablePiecesList");
        private static readonly MethodInfo HudUpdateBuildMethod = AccessTools.Method(typeof(Hud), "UpdateBuild");
        private static readonly MethodInfo PlayerInPlaceModeMethod = AccessTools.Method(typeof(Player), "InPlaceMode");
        private static readonly MethodInfo PlayerSetBuildCategoryMethod = AccessTools.Method(typeof(Player), "SetBuildCategory");
        private static readonly MethodInfo PlayerSetSelectedPieceMethod = AccessTools.Method(typeof(Player), "SetSelectedPiece", new[] { typeof(Vector2Int) });
        private static readonly MethodInfo HudIsPieceSelectionVisibleMethod = AccessTools.Method(typeof(Hud), "IsPieceSelectionVisible");
        private static readonly MethodInfo PieceTableGetSelectedCategoryMethod = AccessTools.Method(typeof(PieceTable), "GetSelectedCategory");
        private static readonly MethodInfo PieceTableGetSelectedPrefabMethod =
            typeof(PieceTable).GetMethod("GetSelectedPrefab", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo PieceTableGetSelectedPieceMethod =
            typeof(PieceTable).GetMethod("GetSelectedPiece", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo PieceTableSelectedPieceField = AccessTools.Field(typeof(PieceTable), "m_selectedPiece");
        private static readonly MethodInfo PlayerUpdatePlacementGhostMethod = GetNoArgMethod(typeof(Player), "UpdatePlacementGhost");
        private static readonly MethodInfo PlayerSetupPlacementGhostNoArgMethod =
            GetNoArgMethod(typeof(Player), "SetupPlacementGhost");
        private static readonly MethodInfo PlayerSetupPlacementGhostPieceMethod =
            GetSingleArgMethod(typeof(Player), "SetupPlacementGhost", typeof(Piece));
        private static readonly MethodInfo PlayerSetupPlacementGhostPrefabMethod =
            GetSingleArgMethod(typeof(Player), "SetupPlacementGhost", typeof(GameObject));
        private static readonly MethodInfo HumanoidGetRightItemMethod = AccessTools.Method(typeof(Humanoid), "GetRightItem");
        private static readonly MethodInfo HumanoidGetInventoryMethod = AccessTools.Method(typeof(Humanoid), "GetInventory");
        private static readonly MethodInfo InventoryGetAllItemsMethod = AccessTools.Method(typeof(Inventory), "GetAllItems");
        private static readonly Type ZInputType = AccessTools.TypeByName("ZInput");
        private static readonly MethodInfo ZInputResetMethod = GetNoArgMethod(ZInputType, "Reset");
        private static readonly MethodInfo ZInputResetButtonStatusNoArgMethod = GetNoArgMethod(ZInputType, "ResetButtonStatus");
        private static readonly MethodInfo ZInputResetButtonStatusStringMethod = GetSingleStringArgMethod(ZInputType, "ResetButtonStatus");
        private static readonly FieldInfo ZInputInstanceField = GetZInputSingletonField(ZInputType);
        private static readonly PropertyInfo ZInputInstanceProperty = GetZInputSingletonProperty(ZInputType);
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
        private static readonly Dictionary<string, string> SearchTextCache = new(StringComparer.OrdinalIgnoreCase);

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

        public static Vector2Int GetPieceTableSelectedPieceIndex(PieceTable pieceTable)
        {
            if (pieceTable == null || PieceTableSelectedPieceField == null)
            {
                return new Vector2Int(-1, -1);
            }

            try
            {
                var value = PieceTableSelectedPieceField.GetValue(pieceTable);
                if (value is Vector2Int vector)
                {
                    return vector;
                }
            }
            catch
            {
                // ignored
            }

            return new Vector2Int(-1, -1);
        }

        public static string DescribeCategorySlice(IReadOnlyList<List<Piece>> categories, int categoryIndex, int maxCount)
        {
            if (categories == null)
            {
                return "<null categories>";
            }

            if (categoryIndex < 0 || categoryIndex >= categories.Count)
            {
                return $"<invalid category {categoryIndex}>";
            }

            var category = categories[categoryIndex];
            if (category == null)
            {
                return "<null category>";
            }

            var take = Math.Max(0, maxCount);
            var prefabs = new List<string>();
            for (var i = 0; i < category.Count && i < take; ++i)
            {
                var piece = category[i];
                prefabs.Add(piece ? GetPrefabName(piece.gameObject) : "<null>");
            }

            return $"count={category.Count}, first={string.Join("|", prefabs)}";
        }

        public static bool TrySelectPieceByPrefab(Player player, string prefabName)
        {
            if (player == null || string.IsNullOrWhiteSpace(prefabName))
            {
                return false;
            }

            var pieceTable = GetBuildPiecesTable(player);
            var categories = pieceTable != null ? GetAvailablePieceCategories(pieceTable) : null;
            if (categories == null)
            {
                return false;
            }

            for (var categoryIndex = 0; categoryIndex < categories.Count; ++categoryIndex)
            {
                var category = categories[categoryIndex];
                if (category == null)
                {
                    continue;
                }

                for (var pieceIndex = 0; pieceIndex < category.Count; ++pieceIndex)
                {
                    var piece = category[pieceIndex];
                    if (!piece)
                    {
                        continue;
                    }

                    if (!string.Equals(GetPrefabName(piece.gameObject), prefabName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    InvokePlayerSetBuildCategory(player, categoryIndex);
                    InvokePlayerSetSelectedPiece(player, new Vector2Int(pieceIndex, 0));
                    return true;
                }
            }

            return false;
        }

        public static Piece GetPieceByPrefabForPlayer(Player player, string prefabName)
        {
            if (player == null || string.IsNullOrWhiteSpace(prefabName))
            {
                return null;
            }

            var pieceTable = GetBuildPiecesTable(player);
            var categories = pieceTable != null ? GetAvailablePieceCategories(pieceTable) : null;
            if (categories != null)
            {
                foreach (var category in categories)
                {
                    if (category == null)
                    {
                        continue;
                    }

                    foreach (var piece in category)
                    {
                        if (piece != null && string.Equals(GetPrefabName(piece.gameObject), prefabName, StringComparison.OrdinalIgnoreCase))
                        {
                            return piece;
                        }
                    }
                }
            }

            var allPieces = GetAllBuildPiecesForPlayer(player);
            var match = allPieces?.FirstOrDefault(obj =>
                obj && string.Equals(GetPrefabName(obj), prefabName, StringComparison.OrdinalIgnoreCase));
            return match ? match.GetComponent<Piece>() : null;
        }

        public static string DescribePlacementArgs(object[] args)
        {
            if (args == null || args.Length == 0)
            {
                return "<none>";
            }

            var parts = new List<string>(args.Length);
            for (var i = 0; i < args.Length; ++i)
            {
                var arg = args[i];
                parts.Add($"arg{i}={DescribePlacementArg(arg)}");
            }

            return string.Join(", ", parts);
        }

        public static string GetPlacementArgPrefab(object[] args)
        {
            if (args == null)
            {
                return string.Empty;
            }

            foreach (var arg in args)
            {
                if (arg is Piece piece && piece)
                {
                    return GetPrefabName(piece.gameObject);
                }

                if (arg is GameObject gameObject && gameObject)
                {
                    return GetPrefabName(gameObject);
                }
            }

            return string.Empty;
        }

        public static string DescribeResult(object result)
        {
            return result == null ? "<null>" : $"{result} ({result.GetType().Name})";
        }

        public static bool RefreshPlacementGhost(Player player)
        {
            if (player == null)
            {
                return false;
            }

            try
            {
                if (PlayerUpdatePlacementGhostMethod != null)
                {
                    PlayerUpdatePlacementGhostMethod.Invoke(player, null);
                    return true;
                }

                var selectedPrefab = GetSelectedBuildPiecePrefab(player);
                var selectedPiece = selectedPrefab ? selectedPrefab.GetComponent<Piece>() : null;
                if (PlayerSetupPlacementGhostPrefabMethod != null && selectedPrefab != null)
                {
                    PlayerSetupPlacementGhostPrefabMethod.Invoke(player, new object[] { selectedPrefab });
                    return true;
                }

                if (PlayerSetupPlacementGhostPieceMethod != null && selectedPiece != null)
                {
                    PlayerSetupPlacementGhostPieceMethod.Invoke(player, new object[] { selectedPiece });
                    return true;
                }

                if (PlayerSetupPlacementGhostNoArgMethod != null)
                {
                    PlayerSetupPlacementGhostNoArgMethod.Invoke(player, null);
                    return true;
                }

                foreach (var method in typeof(Player).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (!string.Equals(method.Name, "SetupPlacementGhost", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var parameters = method.GetParameters();
                    if (parameters.Length == 0)
                    {
                        method.Invoke(player, null);
                        return true;
                    }

                    if (parameters.Length == 1)
                    {
                        var parameterType = parameters[0].ParameterType;
                        if (selectedPrefab != null && parameterType.IsInstanceOfType(selectedPrefab))
                        {
                            method.Invoke(player, new object[] { selectedPrefab });
                            return true;
                        }

                        if (selectedPiece != null && parameterType.IsInstanceOfType(selectedPiece))
                        {
                            method.Invoke(player, new object[] { selectedPiece });
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                BuildMenuPlugin.Instance?.DebugLog($"RefreshPlacementGhost failed: {ex.Message}");
            }

            return false;
        }

        private static string DescribePlacementArg(object arg)
        {
            if (arg == null)
            {
                return "<null>";
            }

            if (arg is Piece piece)
            {
                return $"Piece:{GetPrefabName(piece.gameObject)}";
            }

            if (arg is GameObject gameObject)
            {
                return $"GameObject:{GetPrefabName(gameObject)}";
            }

            if (arg is Component component)
            {
                return $"{component.GetType().Name}:{GetPrefabName(component.gameObject)}";
            }

            return $"{arg} ({arg.GetType().Name})";
        }

        public static GameObject GetSelectedBuildPiecePrefab(Player player)
        {
            var pieceTable = GetBuildPiecesTable(player);
            if (pieceTable == null)
            {
                return null;
            }

            if (PieceTableGetSelectedPrefabMethod != null)
            {
                try
                {
                    var selectedPrefab = PieceTableGetSelectedPrefabMethod.Invoke(pieceTable, null) as GameObject;
                    if (selectedPrefab)
                    {
                        return selectedPrefab;
                    }
                }
                catch
                {
                    // Ignore and fallback to other strategies.
                }
            }

            if (PieceTableGetSelectedPieceMethod != null)
            {
                try
                {
                    var selectedPiece = PieceTableGetSelectedPieceMethod.Invoke(pieceTable, null);
                    if (selectedPiece is Piece piece && piece)
                    {
                        return piece.gameObject;
                    }
                }
                catch
                {
                    // Ignore and fallback to list indexing.
                }
            }

            var categories = GetAvailablePieceCategories(pieceTable);
            if (categories == null || categories.Count == 0)
            {
                return null;
            }

            var selectedCategoryIndex = GetPieceTableSelectedCategoryIndex(pieceTable);
            if (selectedCategoryIndex < 0 || selectedCategoryIndex >= categories.Count)
            {
                selectedCategoryIndex = 0;
            }

            var selectedCategory = categories[selectedCategoryIndex];
            if (selectedCategory == null || selectedCategory.Count == 0 || PieceTableSelectedPieceField == null)
            {
                return null;
            }

            try
            {
                var selectedPieceValue = PieceTableSelectedPieceField.GetValue(pieceTable);
                if (selectedPieceValue is Vector2Int selectedPiece)
                {
                    var index = Mathf.Clamp(selectedPiece.x, 0, selectedCategory.Count - 1);
                    var piece = selectedCategory[index];
                    return piece ? piece.gameObject : null;
                }
            }
            catch
            {
                // Ignore and return null.
            }

            return null;
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

        public static string GetSearchableText(GameObject obj)
        {
            if (!obj)
            {
                return string.Empty;
            }

            var prefab = GetPrefabName(obj);
            if (string.IsNullOrWhiteSpace(prefab))
            {
                return string.Empty;
            }

            if (SearchTextCache.TryGetValue(prefab, out var cached))
            {
                return cached;
            }

            var display = GetDisplayName(obj);
            var token = GetRawDisplayToken(obj);
            var combined = $"{display} {HumanizeToken(token)} {prefab}".Trim();
            var normalized = combined.ToLowerInvariant();
            SearchTextCache[prefab] = normalized;
            return normalized;
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

        private static MethodInfo GetNoArgMethod(Type type, string methodName)
        {
            return type?.GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance,
                null,
                Type.EmptyTypes,
                null);
        }

        private static MethodInfo GetSingleStringArgMethod(Type type, string methodName)
        {
            return type?.GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance,
                null,
                new[] { typeof(string) },
                null);
        }

        private static MethodInfo GetSingleArgMethod(Type type, string methodName, Type argType)
        {
            return type?.GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance,
                null,
                new[] { argType },
                null);
        }

        private static FieldInfo GetZInputSingletonField(Type type)
        {
            if (type == null)
            {
                return null;
            }

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
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

        private static PropertyInfo GetZInputSingletonProperty(Type type)
        {
            if (type == null)
            {
                return null;
            }

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
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
