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
        private Vector2 _displayPosition = new Vector2(100, 100);
        private Color _textColor = Color.white;
        private int _fontSize = 24;
        private bool _isDragging;
        private Vector2 _dragOffset;

        private ConfigEntry<int> _cfgFontSize;
        private ConfigEntry<Color> _cfgFontColor;
        private static ConfigEntry<bool> _modEnabled;
        private ConfigEntry<Vector2> _cfgDisplayPosition;
        private ConfigEntry<bool> _cfgDebugLogs;
 
        void Awake()
        {
            _log = Logger;

            _modEnabled = Config.Bind(
                "General", 
                "Enabled", 
                true, 
                "Enable this mod");

            _cfgFontSize = Config.Bind(
                "Display",
                "FontSize",
                12,
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
            
            _cfgDisplayPosition = Config.Bind(
                "Display",
                "Position",
                new Vector2(20f, -20f),
                "Anchored position of the speed display (X,Y) in screen-space canvas units"
            );
            
            _cfgDebugLogs = Config.Bind(
                "Debug",
                "EnableDebugLogs",
                false,
                "Enable debug logging");

            _cfgFontSize.SettingChanged += (_, __) => ApplyConfig();
            _cfgFontColor.SettingChanged += (_, __) => ApplyConfig();
            _cfgDisplayPosition.SettingChanged += (_, __) => ApplyConfig();


            _log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }
        
        private void DebugLog(string message)
        {
            if (_cfgDebugLogs.Value)
            {
                _log.LogInfo(message);
            }
        }

        private void ApplyConfig()
        {
            // this is called once when the Game (not server/world) starts up
            // and then anytime a change to the config happens
            
            _fontSize = _cfgFontSize.Value;
            _textColor = _cfgFontColor.Value;
            _displayPosition = _cfgDisplayPosition.Value;

            if (_speedText != null)
            {
                _speedText.fontSize = _fontSize;
                _speedText.color = _textColor;
            }

            if (_displayRect != null)
            {
                _displayRect.anchoredPosition = _displayPosition;
            }

        }

        private bool _updateLog;
        private bool _playerLog;
        private bool _hudLog;
        void Update()
        {
            if (!_modEnabled.Value)
            {
                if (_speedDisplay != null)
                {
                    _speedDisplay.SetActive(false);
                }
                return;
            }

            if (Player.m_localPlayer && Hud.instance)
            {
                if (_speedDisplay == null)
                {
                    CreateSpeedDisplay();
                    ApplyConfig();
                }

                _speedDisplay.SetActive(true);

                if (!_updateLog)
                {
                    _updateLog = true;
                }

                UpdateSpeedDisplay();
                HandleDragging();
            }
            else if (_speedDisplay != null)
            {
                _speedDisplay.SetActive(false);
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
            if (Input.GetMouseButtonDown(1))
            {
                Vector2 localMousePos;
                RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _displayRect.parent as RectTransform,
                    Input.mousePosition,
                    null,
                    out localMousePos
                );

                if (RectTransformUtility.RectangleContainsScreenPoint(_displayRect, Input.mousePosition))
                {
                    _isDragging = true;
                    _dragOffset = _displayRect.anchoredPosition - localMousePos;
            
                    DebugLog($"START DRAG - Screen: {Input.mousePosition}, Local: {localMousePos}, Anchored: {_displayRect.anchoredPosition}, Offset: {_dragOffset}");
                }
            }

            if (Input.GetMouseButtonUp(1))
            {
                _isDragging = false;
                DebugLog("STOP DRAG");
            }

            if (_isDragging)
            {
                Vector2 localMousePos;
                RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _displayRect.parent as RectTransform,
                    Input.mousePosition,
                    null,
                    out localMousePos
                );
        
                Vector2 newPos = localMousePos + _dragOffset;
        
                // Log every 10th frame to avoid spam
                if (Time.frameCount % 10 == 0)
                {
                    DebugLog($"DRAGGING - Screen: {Input.mousePosition}, Local: {localMousePos}, NewPos: {newPos}, Offset: {_dragOffset}");
                }
        
                _displayRect.anchoredPosition = newPos;
                _displayPosition = _displayRect.anchoredPosition;
                
                if (_cfgDisplayPosition != null) 
                {
                    _cfgDisplayPosition.Value = _displayPosition;
                    Config.Save();
                }
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