using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace StandingTorchColors
{
    [BepInPlugin(ModGuid, ModName, ModVersion)]
    [BepInDependency(Jotunn.Main.ModGuid)]
    public class StandingTorchColors : BaseUnityPlugin
    {
        public const string ModGuid = "com.impropermods.StandingTorchColors";
        public const string ModName = "Standing Torch Colors";
        public const string ModVersion = "0.1.31";
        
        private const int ColorSlotCount = 10;
        private static ManualLogSource _log;


        // Color variant configs (10 slots)
        private class ColorVariantConfig
        {
            public ConfigEntry<bool> Enabled;
            public ConfigEntry<string> Name;
            public ConfigEntry<string> ColorHex;
            public ConfigEntry<string> Component;
            public ConfigEntry<int> ComponentAmount;
        }

        private class ColorDefaults
        {
            public bool Enabled;
            public string Name;
            public string Hex;
            public string Component;
            public int Amount;
        }

        private List<ColorVariantConfig> _colorConfigs;
        private static readonly Dictionary<string, Sprite> IconCache = new Dictionary<string, Sprite>();

        // Brightness compensation config
        private ConfigEntry<float> _lightBoostMax;
        private ConfigEntry<float> _flareBoost;
        private ConfigEntry<bool> _debugLogging;
        private ConfigEntry<string> _torchNameTemplate;
        private static bool _debugEnabled;

        // Vanilla prefab names
        private const string VanillaTorchPrefab = "piece_groundtorch";
        private const string VanillaTorchBluePrefab = "piece_groundtorch_blue";
        private const string VanillaTorchGreenPrefab = "piece_groundtorch_green";

        // Pick the source torch prefab whose flame color is closest to the target
        // to minimize how much we need to fight the base texture
        private static string SelectTorchSourcePrefab(Color c)
        {
            float h;
            Color.RGBToHSV(c, out h, out _, out _);

            // Hue ranges (0-1):
            // Red:     0.0  - 0.05 and 0.95 - 1.0
            // Yellow:  0.1  - 0.2
            // Green:   0.25 - 0.45
            // Cyan:    0.45 - 0.55
            // Blue:    0.55 - 0.75
            // Magenta: 0.75 - 0.95

            // Green flame covers green/cyan range
            if (h >= 0.25f && h < 0.55f)
                return VanillaTorchGreenPrefab;

            // Blue flame covers blue/violet/indigo/magenta range
            if (h >= 0.55f && h < 0.95f)
                return VanillaTorchBluePrefab;

            // Default orange torch for red/yellow/white/black
            return VanillaTorchPrefab;
        }

        // Default color configurations
        private static readonly ColorDefaults[] DefaultColors =
        {
            new ColorDefaults { Enabled = true, Name = "Teal", Hex = "#30d5c8", Component = "FreezeGland", Amount = 1 },
            new ColorDefaults { Enabled = true, Name = "Red", Hex = "#ff3b30", Component = "Ruby", Amount = 1 },
            new ColorDefaults { Enabled = true, Name = "Green", Hex = "#34c759", Component = "Guck", Amount = 1 },
            new ColorDefaults { Enabled = true, Name = "Yellow", Hex = "#ffcc00", Component = "Amber", Amount = 1 },
            new ColorDefaults { Enabled = true, Name = "Indigo", Hex = "#5856d6", Component = "Crystal", Amount = 1 },
            new ColorDefaults { Enabled = true, Name = "Violet", Hex = "#af52de", Component = "Eitr", Amount = 1 },
            new ColorDefaults
                { Enabled = true, Name = "Magenta", Hex = "#ff2d92", Component = "BlackCore", Amount = 1 },
            new ColorDefaults { Enabled = true, Name = "White", Hex = "#ffffff", Component = "Silver", Amount = 1 },
            new ColorDefaults { Enabled = true, Name = "Black", Hex = "#1c1c1e", Component = "Coal", Amount = 1 },
            new ColorDefaults { Enabled = false, Name = "", Hex = "#808080", Component = "", Amount = 1 }
        };

        private static void LogDebug(string message)
        {
            if (_debugEnabled)
            {
                _log.LogInfo(message);
            }
        }
        
        private void Awake()
        {
            try
            {
                _log = Logger;
                _log.LogInfo("Setting up color configurations...");
                _colorConfigs = new List<ColorVariantConfig>();

                // Setup 10 color configuration slots
                var defaultColors = DefaultColors;

                _debugLogging = Config.Bind(
                    "Debug",
                    "EnableDebugLogs",
                    false,
                    "Enable verbose debug logging.");
                _debugEnabled = _debugLogging.Value;

                for (int i = 0; i < ColorSlotCount; i++)
                {
                    int slot = i + 1;
                    string section = $"Color {slot}";
                    ColorDefaults defaults = defaultColors[i];

                    LogDebug($"Binding config for {section}");

                    ColorVariantConfig config = new ColorVariantConfig();

                    config.Enabled = Config.Bind(
                        section,
                        "Enabled",
                        defaults.Enabled,
                        "Enable this color variant for all fire piece types");

                    config.Name = Config.Bind(
                        section,
                        "Name",
                        defaults.Name,
                        "Display name shown in build menu (e.g., 'Red' creates 'Torch (Red)')");

                    config.ColorHex = Config.Bind(
                        section,
                        "ColorHex",
                        defaults.Hex,
                        "Color in #RRGGBB or #RRGGBBAA format");

                    config.Component = Config.Bind(
                        section,
                        "Component",
                        defaults.Component,
                        "Item prefab name for recipe component (e.g., 'Ruby', 'Guck', 'Crystal')");

                    config.ComponentAmount = Config.Bind(
                        section,
                        "ComponentAmount",
                        defaults.Amount,
                        new ConfigDescription(
                            "Amount of component required for recipe",
                            new AcceptableValueRange<int>(1, 99)));

                    _colorConfigs.Add(config);
                }

                // Global brightness settings
                _lightBoostMax = Config.Bind(
                    "Brightness",
                    "LightBoostMax",
                    3.0f,
                    new ConfigDescription(
                        "Maximum brightness multiplier for lights when using darker colors. Higher values make dark-colored lights brighter.",
                        new AcceptableValueRange<float>(1.0f, 10.0f)));

                _flareBoost = Config.Bind(
                    "Brightness",
                    "FlareBoost",
                    1.5f,
                    new ConfigDescription(
                        "Brightness multiplier for flare particle effects. Higher values make the bright center glow more intense.",
                        new AcceptableValueRange<float>(0.5f, 5.0f)));

                _torchNameTemplate = Config.Bind(
                    "Naming",
                    "TorchNameTemplate",
                    "<color> Iron Torch",
                    "Name template for colored torches. Tokens: <color>, <prefix>, <base>"
                );
                _torchNameTemplate.Value = _torchNameTemplate.Value.Trim();

                PrefabManager.OnVanillaPrefabsAvailable += AddColoredPiecesOnce;
            }
            catch (Exception ex)
            {
                _log.LogError("StandingTorchColors: Error in Awake()");
                _log.LogError(ex);
                throw;
            }
        }

        private static string ApplyBuildPieceName(string template, string colorName, string prefix, string baseName)
        {
            LogDebug($"--- ApplyBuildPieceName({template}, {colorName}, {prefix}, {baseName})");
            if (string.IsNullOrWhiteSpace(template))
                template = "<color> Iron Torch";

            return template
                .Replace("<color>", colorName ?? "")
                .Replace("<prefix>", prefix ?? "")
                .Replace("<base>", baseName ?? "");
        }
        
        private void OnDestroy()
        {
            PrefabManager.OnVanillaPrefabsAvailable -= AddColoredPiecesOnce;
        }

        private void AddColoredPiecesOnce()
        {
            LogDebug("AddColoredPiecesOnce()");
            try
            {
                List<ColorVariant> enabledColors = GetEnabledColorVariants();
                CreateVariants(VanillaTorchPrefab, "Torch", enabledColors);
            }
            catch (Exception ex)
            {
                _log.LogError(ex);
            }
            finally
            {
                PrefabManager.OnVanillaPrefabsAvailable -= AddColoredPiecesOnce;
            }
        }

        private List<ColorVariant> GetEnabledColorVariants()
        {
            LogDebug("- GetEnabledColorVariants()");
            List<ColorVariant> variants = new List<ColorVariant>();

            for (int i = 0; i < _colorConfigs.Count; i++)
            {
                ColorVariantConfig cfg = _colorConfigs[i];

                if (!cfg.Enabled.Value)
                    continue;

                if (string.IsNullOrWhiteSpace(cfg.Name.Value))
                {
                    LogDebug($"-! Color {i + 1}: Enabled but Name is empty, skipping");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(cfg.Component.Value))
                {
                    LogDebug($"-! Color {i + 1}: Enabled but Component is empty, skipping");
                    continue;
                }

                Color color;
                if (!TryParseHexColor(cfg.ColorHex.Value, out color))
                {
                    LogDebug($"-! Color {i + 1}: Invalid ColorHex '{cfg.ColorHex.Value}', skipping");
                    continue;
                }

                variants.Add(new ColorVariant
                {
                    DisplayName = cfg.Name.Value,
                    Color = color,
                    Component = cfg.Component.Value,
                    ComponentAmount = cfg.ComponentAmount.Value
                });
            }

            return variants;
        }

        private static Sprite GetOrCreateSwatchIcon(
            string key,
            Color color,
            float scale, // smaller = less obtrusive (0.65–0.85 good)
            float ringAlpha, // outer ring transparency
            float fillAlpha, // inner fill transparency
            float edgeSoftness, // pixels of soft fade at outer edge
            int size = 64)
        {
            _log.LogInfo(
                $"--- GetOrCreateSwatchIcon({key}, {color}, {size}, {scale}, {ringAlpha}, {fillAlpha}, {edgeSoftness})");
            Sprite cached;
            if (IconCache.TryGetValue(key, out cached) && cached != null)
                return cached;

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;

            Color clear = new Color(0, 0, 0, 0);

            float cx = (size - 1) / 2f;
            float cy = (size - 1) / 2f;

            // Circle radii scaled down
            float rOuter = size * 0.45f * scale;
            float rInner = size * 0.30f * scale;

            // Colors
            Color ring = Color.Lerp(color, Color.black, 0.25f);
            ring.a = ringAlpha;

            Color fill = Color.Lerp(color, Color.white, 0.20f);
            fill.a = fillAlpha;

            Color[] px = new Color[size * size];

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - cx;
                    float dy = y - cy;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);

                    Color outc = clear;

                    if (d <= rOuter + edgeSoftness)
                    {
                        // outer edge soft fade
                        float outerFade = 1f;
                        if (d > rOuter)
                        {
                            float t = (d - rOuter) / Mathf.Max(0.001f, edgeSoftness); // 0..1
                            outerFade = Mathf.Clamp01(1f - t);
                        }

                        if (d >= rInner)
                        {
                            outc = ring;
                            outc.a *= outerFade;
                        }
                        else
                        {
                            outc = fill;
                            outc.a *= outerFade;
                        }
                    }

                    px[y * size + x] = outc;
                }
            }

            tex.SetPixels(px);
            tex.Apply();

            var spr = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
            IconCache[key] = spr;
            return spr;
        }
        
        private static void ApplyInWorldName(GameObject prefab, string displayName)
        {
            // Update Piece name
            LogDebug($"--- ApplyInWorldName: {prefab.name} -> {displayName}");
            var piece = prefab.GetComponent<Piece>();
            if (piece != null)
            {
                piece.m_name = displayName;
            }

            // Update Fireplace name (THIS is what torches use)
            var fireplaces = prefab.GetComponentsInChildren<Fireplace>(true);
            foreach (var fire in fireplaces)
            {
                fire.m_name = displayName;
            }
        }

        private void CreateVariants(string vanillaPrefab, string displayPrefix, List<ColorVariant> variants)
        {
            if (variants.Count == 0)
            {
                LogDebug($"-! CreateVariants: {displayPrefix} -> no enabled color variants; skipping");
                return;
            }

            GameObject vanilla = PrefabManager.Instance.GetPrefab(vanillaPrefab);
            if (vanilla == null)
            {
                LogDebug(
                    $"-! CreateVariants: {displayPrefix}: vanilla prefab '{vanillaPrefab}' not found. " +
                    "Adjust prefab name(s).");
                return;
            }
            
            LogDebug($"- CreateVariants: {displayPrefix} -> {variants.Count} variants");

            // Cache vanilla piece/icon once
            Piece vanillaPiece = vanilla.GetComponent<Piece>();
            string category = vanillaPiece != null ? vanillaPiece.m_category.ToString() : "Misc";
            Sprite vanillaIcon = vanillaPiece != null ? vanillaPiece.m_icon : null;

            foreach (ColorVariant v in variants)
            {
                string safe = Sanitize(v.DisplayName);
                string newPrefabName = $"cl_{vanillaPrefab}_{safe}";

                // For torches, pick the source prefab whose base flame color is closest
                // to our target - avoids fighting the orange texture for blues/greens
                string sourcePrefab = vanillaPrefab;
                if (vanillaPrefab == VanillaTorchPrefab)
                    sourcePrefab = SelectTorchSourcePrefab(v.Color);

                // Build requirements: 2x Iron + component
                List<RequirementConfig> requirements = new List<RequirementConfig>();
                requirements.Add(new RequirementConfig { Item = "Iron", Amount = 2, Recover = true });

                // Add the color-specific component
                if (!string.IsNullOrWhiteSpace(v.Component))
                {
                    requirements.Add(new RequirementConfig
                    {
                        Item = v.Component,
                        Amount = v.ComponentAmount,
                        Recover = true
                    });
                }

                // Config
                PieceConfig cfg = new PieceConfig();
                string baseName = vanillaPiece != null ? vanillaPiece.m_name : displayPrefix;
                cfg.Name = ApplyBuildPieceName(
                    _torchNameTemplate.Value,
                    v.DisplayName,
                    displayPrefix,
                    baseName
                );
                cfg.PieceTable = "Hammer";
                cfg.Category = category;
                cfg.Requirements = requirements.ToArray();

                // Let Jötunn handle the cloning by using the constructor that takes the vanilla prefab name
                CustomPiece custom = new CustomPiece(newPrefabName, sourcePrefab, cfg);

                // Apply recolor + icon on the cloned prefab that Jötunn created BEFORE registration
                GameObject clonedPrefab = custom.PiecePrefab;
                if (clonedPrefab != null)
                {
                    RecolorPiecePrefab(clonedPrefab, v.Color);

                    // Icon tinting (cached)
                    try
                    {
                        Piece clonedPiece = clonedPrefab.GetComponent<Piece>();
                        if (clonedPiece != null && vanillaIcon != null)
                        {
                            // Use a stable cache key based on base icon name + display name
                            string cacheKey = $"{vanillaIcon.name}|{safe}";
                            // string iconKey = $"{cacheKey}|s64|sc0.78|ra0.45|fa0.25";

                            Sprite tinted;
                            if (!IconCache.TryGetValue(cacheKey, out tinted) || tinted == null)
                            {
                                tinted = GetOrCreateSwatchIcon(
                                    cacheKey,
                                    v.Color,
                                    scale: 0.85f,
                                    ringAlpha: 0.25f,
                                    fillAlpha: 0.55f,
                                    edgeSoftness: 2.5f);
                                IconCache[cacheKey] = tinted;
                            }

                            clonedPiece.m_icon = tinted;
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(
                            $"-! CreateVariants: Failed to set tinted icon for {newPrefabName}: {ex.Message}");
                    }
                }
                else
                {
                    _log.LogError($"Failed to get PiecePrefab from CustomPiece {newPrefabName}");
                }

                string displayName = cfg.Name;
                ApplyInWorldName(clonedPrefab, displayName);
                
                // Register
                PieceManager.Instance.AddPiece(custom);
            }
        }

        private void RecolorPiecePrefab(GameObject root, Color c)
        {
            LogDebug($"-- RecolorPiecePrefab: {root.name} -> {c})");
            string goName = root.gameObject.name.ToLowerInvariant();

            c = EffectiveTintColor(c);

            // Lights
            RecolorLightsWithCompensation(root, c, _lightBoostMax.Value);

            foreach (ParticleSystem ps in root.GetComponentsInChildren<ParticleSystem>(true))
            {
                SetStartColorPreserveAlphaAndGradient(ps, c);

                // Modify Color Over Lifetime if enabled
                ParticleSystem.ColorOverLifetimeModule color = ps.colorOverLifetime;
                if (color.enabled)
                {
                    // Check if the source color is white (means startColor drives the color)
                    // In that case, skip replacing the gradient - startColor already set correctly
                    bool sourceColorIsWhite = false;
                    if (color.color.gradient != null)
                    {
                        LogDebug(
                            $"-- RecolorPiecePrefab {goName}: color has gradient, check if source of gradient is white");
                        GradientColorKey[] keys = color.color.gradient.colorKeys;
                        sourceColorIsWhite = keys.Length > 0 &&
                                             keys[0].color.r > 0.95f && keys[0].color.g > 0.95f &&
                                             keys[0].color.b > 0.95f;
                    }

                    if (!sourceColorIsWhite)
                    {
                        LogDebug($"-- RecolorPiecePrefab {goName}: color.gradient is not white, need new gradient");
                        float maxChannel = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                        float scale = maxChannel > 0.001f ? 1.0f / maxChannel : 1.0f;
                        Color normalized = new Color(c.r * scale, c.g * scale, c.b * scale, 1f);

                        Gradient newGradient = new Gradient();
                        Color bright = new Color(normalized.r, normalized.g, normalized.b, 1f);
                        Color mid = new Color(normalized.r * 0.7f, normalized.g * 0.7f, normalized.b * 0.7f, 1f);
                        Color dark = new Color(normalized.r * 0.4f, normalized.g * 0.4f, normalized.b * 0.4f, 1f);

                        GradientColorKey[] colorKeys = new GradientColorKey[3];
                        colorKeys[0] = new GradientColorKey(bright, 0f);
                        colorKeys[1] = new GradientColorKey(mid, 0.141f);
                        colorKeys[2] = new GradientColorKey(dark, 0.324f);

                        GradientAlphaKey[] alphaKeys = new GradientAlphaKey[4];
                        alphaKeys[0] = new GradientAlphaKey(0f, 0f);
                        alphaKeys[1] = new GradientAlphaKey(1f, 0.147f);
                        alphaKeys[2] = new GradientAlphaKey(0.98f, 0.406f);
                        alphaKeys[3] = new GradientAlphaKey(0f, 1f);

                        newGradient.SetKeys(colorKeys, alphaKeys);
                        color.color = new ParticleSystem.MinMaxGradient(newGradient);
                        LogDebug($"-+ RecolorPiecePrefab {goName}: gradient set, new color {color.color}");
                    }
                    else
                    {
                        LogDebug($"-! RecolorPiecePrefab {goName}: color gradient source is white");
                    } // end else (non-white color)
                } // end col.enabled
            }

            // Tint non-particle renderers - only glow/emissive parts, not structural wood/stone
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer) continue;

                // Skip structural meshes - only tint lit/emissive parts
                bool isStructural = r.gameObject.name.IndexOf("log", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    r.gameObject.name.IndexOf("ash", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    r.gameObject.name.IndexOf("unlit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    r.gameObject.name.IndexOf("unfuel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    r.gameObject.name.IndexOf("stone", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    r.gameObject.name.IndexOf("wood", StringComparison.OrdinalIgnoreCase) >= 0;

                if (isStructural)
                {
                    LogDebug($"-! RecolorPiecePrefab {goName}: Structural in nature, will skip");
                    continue;
                }
                LogDebug($"- RecolorPiecePrefab {goName}: actionable item '{r.gameObject.name}' found");

                Material[] tinted = new Material[r.sharedMaterials.Length];
                for (int i = 0; i < r.sharedMaterials.Length; i++)
                {
                    Material sm = r.sharedMaterials[i];
                    if (sm == null)
                    {
                        tinted[i] = sm;
                        continue;
                    }

                    Material m = Instantiate(sm);

                    // Only tint the yellow/bright parts - preserve dark and red parts
                    // Check the existing _Color to decide whether to tint
                    if (m.HasProperty("_Color"))
                    {
                        Color existing = m.GetColor("_Color");
                        LogDebug($"- RecolorPiecePrefab {goName}: Existing material color '{existing}'");
                        float eh, es, ev;
                        Color.RGBToHSV(existing, out eh, out es, out ev);

                        // Tint if: bright (v > 0.5) AND not strongly red (h < 0.05 or h > 0.95)
                        bool isYellowOrBright = ev > 0.5f && !(eh < 0.08f || eh > 0.92f);
                        // Also tint pure white (no saturation, high value)
                        bool isWhite = es < 0.1f && ev > 0.8f;

                        if (isYellowOrBright || isWhite)
                        {
                            LogDebug($"-+ RecolorPiecePrefab {goName}: updating yellow, white, brite color");
                            m.SetColor("_Color", MultiplyColor(existing, c));
                            LogDebug($"-+ RecolorPiecePrefab {goName}: tinting {m.GetColor("_Color")}");
                        }
                        else
                        {
                            LogDebug($"-+ RecolorPiecePrefab {goName}: _Color tinting not required");
                        }
                    }

                    if (m.HasProperty("_EmissionColor"))
                    {
                        Color emission = m.GetColor("_EmissionColor");
                        LogDebug($"-+ RecolorPiecePrefab {goName}: Existing _EmissionColor '{emission}'");
                        if (emission.r + emission.g + emission.b > 0.01f)
                        {
                            m.SetColor("_EmissionColor", MultiplyColor(emission, c));
                            LogDebug($"-+ RecolorPiecePrefab {goName}: Tinted to {m.GetColor("_EmissionColor")}");
                        }
                        else
                        {
                            LogDebug($"-+ RecolorPiecePrefab {goName}: _EmissionColor tinting not required");
                        }
                    }

                    tinted[i] = m;
                }

                r.materials = tinted;
            }

            // Particle materials
            foreach (ParticleSystemRenderer r in root.GetComponentsInChildren<ParticleSystemRenderer>(true))
            {
                ParticleSystem ps = r.GetComponent<ParticleSystem>();
                string psName = ps != null ? ps.name : "";
                bool isFlame = !(psName.IndexOf("smoke", StringComparison.OrdinalIgnoreCase) >= 0) &&
                               !(psName.IndexOf("spark", StringComparison.OrdinalIgnoreCase) >= 0) &&
                               !(psName.IndexOf("ember", StringComparison.OrdinalIgnoreCase) >= 0) &&
                               !(psName.IndexOf("wet", StringComparison.OrdinalIgnoreCase) >= 0);

                Material current = r.sharedMaterial;
                if (current == null) continue;
                LogDebug($"-- RecolorPiecePreFab: psName is {psName} and isFlame is {isFlame}");

                if (current.shader.name.Contains("Gradient"))
                {
                    LogDebug("-- RecolorPiecePreFab: Applying Gradient to shader");
                    Material m = isFlame ? r.material : Instantiate(current);
                    if (m == null) m = Instantiate(current);
                    ApplyMaterialTintPreserveAlpha(m, c, psName, _flareBoost.Value);
                    r.material = m;
                }
                else
                {
                    LogDebug("-- RecolorPiecePreFab: No shaders found");
                    Material m = Instantiate(current);
                    ApplyMaterialTintPreserveAlpha(m, c, psName, _flareBoost.Value);
                    r.material = m;
                }
            }

            // Trails / lines
            foreach (TrailRenderer tr in root.GetComponentsInChildren<TrailRenderer>(true))
            {
                tr.startColor = c;
                tr.endColor = new Color(c.r, c.g, c.b, 0f);
            }

            foreach (LineRenderer lr in root.GetComponentsInChildren<LineRenderer>(true))
            {
                lr.startColor = c;
                lr.endColor = new Color(c.r, c.g, c.b, 0f);
            }
        }

        // Multiply two colors channel by channel, preserving alpha
        // Returns the effective tint color to use for material multiplication.
        // Pure white (#FFFFFF) would leave materials unchanged, so we substitute
        // a bright warm white that still reads as white but lets tinting work.
        private static Color EffectiveTintColor(Color c)
        {
            float s, v;
            Color.RGBToHSV(c, out _, out s, out v);

            // If nearly white (low saturation, high value), use a bright warm-white
            // that will show up as white-ish flame rather than no change
            if (s < 0.15f && v > 0.85f)
                return new Color(1f, 0.97f, 0.88f, c.a); // warm white

            return c;
        }

        private static Color MultiplyColor(Color original, Color tint)
        {
            return new Color(original.r * tint.r, original.g * tint.g, original.b * tint.b, original.a);
        }

        private static string NormalizeHDR(Color hdr)
        {
            float max = Mathf.Max(hdr.r, hdr.g, hdr.b);
            Color norm = max <= 1f ? hdr : new Color(hdr.r / max, hdr.g / max, hdr.b / max, hdr.a);

            int r = Mathf.RoundToInt(norm.r * 255);
            int g = Mathf.RoundToInt(norm.g * 255);
            int b = Mathf.RoundToInt(norm.b * 255);

            return $"RGB({r}, {g}, {b})";
        }


        private static void SetStartColorPreserveAlphaAndGradient(ParticleSystem ps, Color tint)
        {
            ParticleSystem.MainModule main = ps.main;
            ParticleSystem.MinMaxGradient sc = main.startColor;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            LogDebug($"--- SetStartColorPreserveAlphaAndGradient: ps.name {ps.name}, color {tint}");
            LogDebug($"--- SetStartColorPreserveAlphaAndGradient: sc.mode {sc.mode}");
            LogDebug($"--- SetStartColorPreserveAlphaAndGradient: pre-main.startColor {main.startColor.color}");
            switch (sc.mode)
            {
                case ParticleSystemGradientMode.Color:
                {
                    Color oc = sc.color;
                    main.startColor = new ParticleSystem.MinMaxGradient(
                        new Color(tint.r, tint.g, tint.b, oc.a)
                    );
                    break;
                }
                case ParticleSystemGradientMode.TwoColors:
                {
                    // For gradient-mapped shaders, the startColor is MULTIPLIED with the texture
                    // Since the texture is orange/yellow, we need to completely override it
                    Color cMin = sc.colorMin;
                    Color cMax = sc.colorMax;

                    // Apply the tint with aggressive multipliers, preserving alpha
                    main.startColor = new ParticleSystem.MinMaxGradient(
                        new Color(tint.r * 3.0f, tint.g * 3.0f, tint.b * 3.0f, cMin.a),
                        new Color(tint.r * 5.0f, tint.g * 5.0f, tint.b * 5.0f, cMax.a)
                    );
                    break;
                }
                case ParticleSystemGradientMode.Gradient:
                {
                    Gradient g = sc.gradient;
                    main.startColor = new ParticleSystem.MinMaxGradient(TintGradientPreserveAlpha(g, tint));
                    break;
                }
                case ParticleSystemGradientMode.TwoGradients:
                {
                    Gradient gMin = sc.gradientMin;
                    Gradient gMax = sc.gradientMax;
                    main.startColor = new ParticleSystem.MinMaxGradient(
                        TintGradientPreserveAlpha(gMin, tint),
                        TintGradientPreserveAlpha(gMax, tint)
                    );
                    break;
                }
                default:
                {
                    // fallback
                    Color oc = sc.color;
                    main.startColor = new ParticleSystem.MinMaxGradient(
                        new Color(tint.r, tint.g, tint.b, oc.a)
                    );
                    break;
                }
            }

            LogDebug(
                $"--- SetStartColorPreserveAlphaAndGradient: post-main.startColor {NormalizeHDR(main.startColor.color)}");
        }

        private static Gradient TintGradientPreserveAlpha(Gradient src, Color tint)
        {
            // Keep alpha keys EXACTLY; only change the RGB of the color keys.
            Gradient dst = new Gradient();

            GradientAlphaKey[] alphaKeys = src.alphaKeys;

            GradientColorKey[] srcColorKeys = src.colorKeys;
            GradientColorKey[] dstColorKeys = new GradientColorKey[srcColorKeys.Length];

            for (int i = 0; i < srcColorKeys.Length; i++)
            {
                Color orig = srcColorKeys[i].color;

                float vOrig;
                Color.RGBToHSV(orig, out _, out _, out vOrig);

                float hT, sT;
                Color.RGBToHSV(tint, out hT, out sT, out _);

                // Use tint hue/sat, original value ramp
                Color rgb = Color.HSVToRGB(hT, sT, Mathf.Clamp01(vOrig));

                dstColorKeys[i] = new GradientColorKey(rgb, srcColorKeys[i].time);
            }

            dst.SetKeys(dstColorKeys, alphaKeys);
            return dst;
        }

        private static float Luminance(Color c)
        {
            // Rec.709 relative luminance
            return 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
        }

        private static void RecolorLightsWithCompensation(GameObject root, Color newColor, float maxBoost)
        {
            foreach (Light light in root.GetComponentsInChildren<Light>(true))
            {
                // Use the original light color as reference for "expected brightness"
                Color oldColor = light.color;

                float oldLum = Mathf.Max(0.001f, Luminance(oldColor));
                float newLum = Mathf.Max(0.001f, Luminance(newColor));

                // Scale intensity so perceived brightness stays similar
                float boost = Mathf.Clamp(oldLum / newLum, 1.0f, maxBoost);

                light.color = newColor;
                light.intensity *= boost;
            }
        }

        private static void ApplyMaterialTintPreserveAlpha(Material m, Color tint, string fxName, float flareBoost)
        {
            LogDebug($"--- ApplyMaterialTintPreserveAlpha: {fxName} -> {tint} -> {flareBoost}");
            // Optional classification: keep flare/glow bright
            bool isFlare = fxName.Equals("flare", StringComparison.OrdinalIgnoreCase);

            if (m.HasProperty("_Color"))
            {
                Color old = m.GetColor("_Color");
                Color t = new Color(tint.r, tint.g, tint.b, old.a);
                m.SetColor("_Color", t);
            }

            if (m.HasProperty("_TintColor"))
            {
                Color old = m.GetColor("_TintColor");
                Color t = new Color(tint.r, tint.g, tint.b, old.a);
                m.SetColor("_TintColor", t);
            }

            if (m.HasProperty("_EmissionColor"))
            {
                // Embers/flare: keep bright; flame: normal
                float mult = isFlare ? flareBoost : 1.0f;
                m.SetColor("_EmissionColor", new Color(tint.r, tint.g, tint.b, 1f) * mult);
            }
        }

        private struct ColorVariant
        {
            public string DisplayName;
            public Color Color;
            public string Component;
            public int ComponentAmount;
        }

        private static bool TryParseHexColor(string hex, out Color color)
        {
            color = Color.white;
            if (string.IsNullOrWhiteSpace(hex)) return false;
            if (hex.StartsWith("#")) hex = hex.Substring(1);

            if (hex.Length != 6 && hex.Length != 8) return false;

            uint v;
            if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v)) return false;

            float r, g, b, a;
            if (hex.Length == 6)
            {
                r = ((v >> 16) & 0xFF) / 255f;
                g = ((v >> 8) & 0xFF) / 255f;
                b = (v & 0xFF) / 255f;
                a = 1f;
            }
            else
            {
                r = ((v >> 24) & 0xFF) / 255f;
                g = ((v >> 16) & 0xFF) / 255f;
                b = ((v >> 8) & 0xFF) / 255f;
                a = (v & 0xFF) / 255f;
            }

            color = new Color(r, g, b, a);
            return true;
        }

        private static string Sanitize(string s)
        {
            char[] chars = s.ToLowerInvariant()
                .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
                .ToArray();
            return new string(chars).Trim('_');
        }
    }
}