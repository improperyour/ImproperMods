using BepInEx;
using BepInEx.Configuration;
using System;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace SpeedTracker
{
    [BepInPlugin("com.improperyour.speedtracker", "Speed Tracker", "1.1.0")]
    public class SpeedTrackerPlugin : BaseUnityPlugin
    {
        // UI
        private GameObject _canvasObj;
        private GameObject _speedDisplay;
        private TextMeshProUGUI _speedText;
        private RectTransform _displayRect;

        // Dragging
        private bool _isDragging;
        private Vector2 _dragOffset;

        // Current applied state
        private Vector2 _displayPosition = new Vector2(20, -20);
        private Color _textColor = Color.white;
        private int _fontSize = 24;

        // Config entries
        private ConfigEntry<int> _cfgFontSize;
        private ConfigEntry<string> _cfgTextColorRgba;
        private ConfigEntry<string> _cfgDisplayPosition;

        // Live reload
        private DateTime _lastConfigWriteUtc = DateTime.MinValue;
        private float _nextConfigPollTime;
        private const float ConfigPollIntervalSeconds = 2.0f;

        private void Awake()
        {
            _cfgFontSize = Config.Bind(
                "Display",
                "FontSize",
                24,
                new ConfigDescription(
                    "Font size of the speed display",
                    new AcceptableValueRange<int>(10, 64))
            );

            _cfgTextColorRgba = Config.Bind(
                "Display",
                "TextColorRGBA",
                "255,255,255,255",
                "Text color as RGBA bytes: R,G,B,A (0–255)"
            );

            _cfgDisplayPosition = Config.Bind(
                "Display",
                "Position",
                "20,-20",
                "UI anchored position as \"x,y\""
            );

            ApplyConfig();
            UpdateLastConfigWriteTime();
            _nextConfigPollTime = Time.unscaledTime + ConfigPollIntervalSeconds;
        }

        private void Update()
        {
            PollConfigForChanges();

            // Player.m_localPlayer is NULL on menus and loading screens
            if (Player.m_localPlayer == null)
                return;

            if (_speedDisplay == null)
            {
                CreateUI();
                ApplyConfig();
            }

            UpdateSpeed();
            HandleDragging();
        }

        private void CreateUI()
        {
            _canvasObj = new GameObject("SpeedTrackerCanvas");
            var canvas = _canvasObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            var scaler = _canvasObj.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);

            DontDestroyOnLoad(_canvasObj);

            _speedDisplay = new GameObject("SpeedDisplay");
            _speedDisplay.transform.SetParent(_canvasObj.transform, false);

            _speedText = _speedDisplay.AddComponent<TextMeshProUGUI>();
            _speedText.alignment = TextAlignmentOptions.Left;
            _speedText.textWrappingMode = TextWrappingModes.NoWrap;
            _speedText.raycastTarget = false;
            _speedText.text = "Speed: 0.0 m/s";

            _displayRect = _speedDisplay.GetComponent<RectTransform>();
            _displayRect.anchorMin = new Vector2(0, 1);
            _displayRect.anchorMax = new Vector2(0, 1);
            _displayRect.pivot = new Vector2(0, 1);
            _displayRect.sizeDelta = new Vector2(220, 40);
        }

        private void UpdateSpeed()
        {
            Vector3 vel = Player.m_localPlayer.GetVelocity();
            float speed = new Vector3(vel.x, 0, vel.z).magnitude;
            _speedText.text = $"Speed: {speed:F1} m/s";
        }

        private void HandleDragging()
        {
            if (Input.GetMouseButtonDown(1))
            {
                if (RectTransformUtility.RectangleContainsScreenPoint(
                        _displayRect, Input.mousePosition))
                {
                    _isDragging = true;
                    _dragOffset = _displayRect.anchoredPosition -
                                  (Vector2)Input.mousePosition;
                }
            }

            if (Input.GetMouseButtonUp(1) && _isDragging)
            {
                _isDragging = false;

                _cfgDisplayPosition.Value =
                    $"{_displayPosition.x.ToString(CultureInfo.InvariantCulture)}," +
                    $"{_displayPosition.y.ToString(CultureInfo.InvariantCulture)}";

                Config.Save();
                UpdateLastConfigWriteTime();
            }

            if (_isDragging)
            {
                _displayRect.anchoredPosition =
                    (Vector2)Input.mousePosition + _dragOffset;
                _displayPosition = _displayRect.anchoredPosition;
            }
        }

        private void ApplyConfig()
        {
            _fontSize = Mathf.Clamp(_cfgFontSize.Value, 10, 64);

            if (!TryParseRgba(_cfgTextColorRgba.Value, out _textColor))
                _textColor = Color.white;

            if (TryParseVector2(_cfgDisplayPosition.Value, out var pos))
                _displayPosition = pos;

            if (_speedText != null)
            {
                _speedText.fontSize = _fontSize;
                _speedText.color = _textColor;
            }

            if (_displayRect != null)
                _displayRect.anchoredPosition = _displayPosition;
        }

        private void PollConfigForChanges()
        {
            if (Time.unscaledTime < _nextConfigPollTime)
                return;

            _nextConfigPollTime = Time.unscaledTime + ConfigPollIntervalSeconds;

            try
            {
                if (!File.Exists(Config.ConfigFilePath))
                    return;

                var writeTime = File.GetLastWriteTimeUtc(Config.ConfigFilePath);
                if (writeTime > _lastConfigWriteUtc)
                {
                    _lastConfigWriteUtc = writeTime;
                    Config.Reload();
                    ApplyConfig();
                }
            }
            catch
            {
                // intentionally swallow errors
            }
        }

        private void UpdateLastConfigWriteTime()
        {
            try
            {
                if (File.Exists(Config.ConfigFilePath))
                    _lastConfigWriteUtc =
                        File.GetLastWriteTimeUtc(Config.ConfigFilePath);
            }
            catch { }
        }

        private static bool TryParseRgba(string s, out Color c)
        {
            c = Color.white;
            var parts = s.Split(',');
            if (parts.Length != 4)
                return false;

            if (!byte.TryParse(parts[0], out var r)) return false;
            if (!byte.TryParse(parts[1], out var g)) return false;
            if (!byte.TryParse(parts[2], out var b)) return false;
            if (!byte.TryParse(parts[3], out var a)) return false;

            c = new Color(r / 255f, g / 255f, b / 255f, a / 255f);
            return true;
        }

        private static bool TryParseVector2(string s, out Vector2 v)
        {
            v = default;
            var parts = s.Split(',');
            if (parts.Length != 2)
                return false;

            if (!float.TryParse(parts[0], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var x)) return false;
            if (!float.TryParse(parts[1], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var y)) return false;

            v = new Vector2(x, y);
            return true;
        }
    }
}
