using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;

namespace SpeedTracker
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class SpeedTrackerPlugin : BaseUnityPlugin
    {
        private const string PluginGuid = "com.improperyour.speedtracker";
        private const string PluginName = "Speed Tracker";
        private const string PluginVersion = "0.1.0";

        private GameObject _speedDisplay;
        private Text _speedText;
        private RectTransform _displayRect;

        private ManualLogSource _log;

        // Configurable settings
        private Vector2 _displayPosition = new Vector2(20, -20);
        private Color _textColor = Color.white;
        private int _fontSize = 24;
        private bool _isDragging;
        private Vector2 _dragOffset;

        private ConfigEntry<int> _cfgFontSize;
        private ConfigEntry<Color> _cfgFontColor;
        private static ConfigEntry<bool> modEnabled;


        void Awake()
        {
            _log = Logger;

            modEnabled = Config.Bind<bool>(
                "General", 
                "Enabled", 
                true, 
                "Enable this mod");

            _cfgFontSize = Config.Bind(
                "Display",
                "FontSize",
                24,
                new ConfigDescription(
                    "Font size of the speed display",
                    new AcceptableValueRange<int>(10, 64)
                )
            );

            _cfgFontColor = Config.Bind(
                "Display",
                "FontColor",
                Color.white,
                "Font color of the speed display (RGBA)"
            );

            _cfgFontSize.SettingChanged += (_, __) => ApplyConfig();
            _cfgFontColor.SettingChanged += (_, __) => ApplyConfig();

            _log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void ApplyConfig()
        {
            // this is called once when the Game (not server/world) starts up
            // and then anytime a change to the config happens
            
            _log.LogInfo("ApplyConfig");
            _fontSize = _cfgFontSize.Value;
            _textColor = _cfgFontColor.Value;

            if (_speedText != null)
            {
                _speedText.fontSize = _fontSize;
                _speedText.color = _textColor;
            }
        }

        private bool _updateLog;
        private bool _playerLog;
        private bool _hudLog;
        void Update()
        {
            // this is continuously called (in Game and in Server/World)
            // Hud.instance will be null while in Game menu
            // Hud.instance will be set when in Server/World
            if (!modEnabled.Value)
            {
                return;
            }

            if (_speedDisplay == null)
            {
                // so this is only ever called once from what I can tell...
                _log.LogInfo("speedDisplay & ApplyConfig about to be called");
                _log.LogInfo($"Hud.Instance is currently: {Hud.instance}");
                CreateSpeedDisplay();
                ApplyConfig();
            }

            if (Player.m_localPlayer && ! _playerLog)
            {
                _log.LogInfo($"Player object is set!");
                _playerLog = true;
            }
            if (Hud.instance && ! _hudLog)
            {
                _log.LogInfo("Hud.Instance is now set");
                _hudLog = true;
            }
            
            if (Player.m_localPlayer && Hud.instance)
            {
                // Hud.instance can be set, but Player.m_localPlayer is not until inside server/world
                if (!_updateLog)
                {
                    _log.LogInfo("UpdateSpeedDisplay & HandleDragging about to be called");
                    _updateLog = true;
                }

                UpdateSpeedDisplay();
                HandleDragging();
            }
        }

        private void CreateSpeedDisplay()
        {
            // this is called once when Game (not server/world) starts up
            
            // Create canvas
            GameObject canvasObj = new GameObject("SpeedTrackerCanvas");
            Canvas canvas = canvasObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            CanvasScaler scaler = canvasObj.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);

            DontDestroyOnLoad(canvasObj);

            // Create text display
            _speedDisplay = new GameObject("SpeedDisplay");
            _speedDisplay.transform.SetParent(canvasObj.transform, false);

            _speedText = _speedDisplay.AddComponent<Text>();
            _speedText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            _speedText.fontSize = _fontSize;
            _speedText.color = _textColor;
            _speedText.alignment = TextAnchor.MiddleLeft;
            _speedText.text = "Speed: 0.0 m/s";

            // Setup rect transform
            _displayRect = _speedDisplay.GetComponent<RectTransform>();
            _displayRect.anchorMin = new Vector2(0, 1);
            _displayRect.anchorMax = new Vector2(0, 1);
            _displayRect.pivot = new Vector2(0, 1);
            _displayRect.anchoredPosition = _displayPosition;
            _displayRect.sizeDelta = new Vector2(200, 50);

            // Add outline for visibility
            Outline outline = _speedDisplay.AddComponent<Outline>();
            outline.effectColor = Color.black;
            outline.effectDistance = new Vector2(1, -1);
        }

        private void UpdateSpeedDisplay()
        {
            Vector3 velocity = Player.m_localPlayer.GetVelocity();
            float speed = new Vector3(velocity.x, 0, velocity.z).magnitude;

            _speedText.text = $"Speed: {speed:F1} m/s";
        }

        private void HandleDragging()
        {
            // Right-click to drag
            if (Input.GetMouseButtonDown(1))
            {
                Vector2 mousePos = Input.mousePosition;
                if (RectTransformUtility.RectangleContainsScreenPoint(_displayRect, mousePos))
                {
                    _isDragging = true;
                    _dragOffset = _displayRect.anchoredPosition - mousePos;
                }
            }

            if (Input.GetMouseButtonUp(1))
            {
                _isDragging = false;
            }

            if (_isDragging)
            {
                _displayRect.anchoredPosition = (Vector2)Input.mousePosition + _dragOffset;
                _displayPosition = _displayRect.anchoredPosition;
            }
        }

        // Public methods to customize appearance
        public void SetTextColor(Color color)
        {
            _textColor = color;
            if (_speedText != null)
                _speedText.color = color;
        }

        public void SetFontSize(int size)
        {
            _fontSize = size;
            if (_speedText != null)
                _speedText.fontSize = size;
        }

        public void SetPosition(Vector2 position)
        {
            _displayPosition = position;
            if (_displayRect != null)
                _displayRect.anchoredPosition = position;
        }
    }
}