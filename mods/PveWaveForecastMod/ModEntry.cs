using System;
using System.Collections.Generic;
using CesiumLoader.SDK.Engine;
using CesiumLoader.SDK.Events;
using CesiumLoader.SDK.Inputs;
using FairyGUI;
using CesiumLoader.SDK.Mods;
using Core.Scene;
using Core.Unit;
using GameLogic;
using Tools;
using UnityEngine;

namespace PveWaveForecastMod
{
    public static class ModEntry
    {
        public static void Main() { ModBase.Run(new PveWaveForecastController()); }
    }

    public sealed class PveWaveForecastController : ModBase
    {
        private readonly WaveOverlay _overlay = new WaveOverlay();
        private List<WaveInfo> _waves = new List<WaveInfo>();
        private bool _enabled;
        private bool _visible;
        private bool _showPanel = true;
        private bool _onlyNextWave = true;
        private bool _showRandom = true;
        private int _fontSize = 22;
        private int _maxWaves = 8;
        private int _labelLift = 36;
        private float _height = 1.6f;
        private KeyCode _toggleKey = KeyCode.O;
        private int _lastMapId = -1;
        private int _lastDifficulty = -1;
        private int _lastProgress = -1;
        private DateTime _nextError;
        private DateTime _nextScan;

        public override string Name { get { return "PVE 波次预告"; } }
        public override string Version { get { return "1.1.0"; } }

        public override void OnInitialize()
        {
            ReadConfig(isInitial: true);
            UpdateEvents.LateUpdate += Draw;
            if (Context != null) Context.RegisterCleanup(Stop);
            Log.Info("PVE 波次预告已加载：默认关闭，按 " + _toggleKey + " 打开/关闭。");
        }

        private void ReadConfig(bool isInitial = false)
        {
            if (Config == null) return;
            if (isInitial)
            {
                // 1.0.0 config -> 1.1.0: raise the label and start hidden by default.
                if (!Config.Has("labelLift"))
                {
                    Config.Set("enabled", false);
                    Config.Set("toggleKey", "O");
                    Config.Set("heightOffset", 1.6f);
                }
                PveWaveForecastConfigDocs.Apply(Config);
                Config.SetDefault("enabled", false);
                Config.SetDefault("toggleKey", "O");
                Config.SetDefault("fontSize", 22);
                Config.SetDefault("heightOffset", 1.6f);
                Config.SetDefault("labelLift", 36);
                Config.SetDefault("showSchedulePanel", true);
                Config.SetDefault("showOnlyNextWave", true);
                Config.SetDefault("showRandomLandHints", true);
                Config.SetDefault("maxWaves", 8);
            }

            _enabled = Config.GetBool("enabled", false);
            _fontSize = Math.Max(14, Math.Min(48, Config.GetInt("fontSize", 22)));
            _height = Clamp(Config.GetFloat("heightOffset", 1.6f), 0f, 10f, 1.6f);
            _labelLift = Math.Max(0, Math.Min(160, Config.GetInt("labelLift", 36)));
            _toggleKey = ParseKey(Config.GetString("toggleKey", "O"), KeyCode.O);
            _showPanel = Config.GetBool("showSchedulePanel", true);
            _onlyNextWave = Config.GetBool("showOnlyNextWave", true);
            _showRandom = Config.GetBool("showRandomLandHints", true);
            _maxWaves = Math.Max(1, Math.Min(20, Config.GetInt("maxWaves", 8)));
            if (isInitial) _visible = _enabled;
            if (isInitial) Config.SaveIfDirty();
        }

        public override void OnConfigChanged()
        {
            ReadConfig();
            _visible = _enabled;
            _waves.Clear();
            _lastMapId = -1;
            _lastDifficulty = -1;
            _lastProgress = -1;
            _overlay.Clear();
            Log.Info("波次预告配置已热重载: enabled=" + _enabled + ", fontSize=" + _fontSize);
        }

        private static float Clamp(float value, float min, float max, float fallback)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return fallback;
            return Math.Max(min, Math.Min(max, value));
        }

        private bool TryRoom(out RoomInfo info, out LandManager lands, out Camera camera)
        {
            info = null;
            lands = null;
            camera = null;
            if (SimpleSingletonProvider<SceneManager>.inst.currentType.Value != SceneType.Battle) return false;

            var room = SimpleSingletonProvider<GameLogicManager>.inst.room;
            if (room == null || room.curRoomInfo == null || !room.curRoomInfo.IsPVE()) return false;
            info = room.curRoomInfo;

            var battle = BattleSceneController.inst;
            if (battle == null) return false;
            camera = battle.mainCamera;
            lands = SimpleSingletonProvider<LandManager>.inst;
            return UnityObject.IsAlive(camera) && lands != null && lands.NodeDict != null && lands.NodeDict.Count > 0;
        }

        private void Draw()
        {
            HandleHotkey();
            if (!_visible)
            {
                _overlay.Clear();
                _waves.Clear();
                return;
            }

            try
            {
                RoomInfo info;
                LandManager lands;
                Camera camera;
                if (!TryRoom(out info, out lands, out camera))
                {
                    _overlay.Clear();
                    _waves.Clear();
                    _lastMapId = -1;
                    _lastDifficulty = -1;
                    _lastProgress = -1;
                    return;
                }

                if (info.MapId != _lastMapId || info.Difficulty != _lastDifficulty || info.GameProgress != _lastProgress || _waves.Count == 0 || DateTime.UtcNow >= _nextScan)
                {
                    _lastMapId = info.MapId;
                    _lastDifficulty = info.Difficulty;
                    _lastProgress = info.GameProgress;
                    _nextScan = DateTime.UtcNow.AddSeconds(2);
                    _waves = WaveSchedule.Build(info, lands, _maxWaves);
                }

                _overlay.Configure(_fontSize, _showPanel, _showRandom, _labelLift);
                _overlay.Render(_waves, info.GameProgress, lands, camera, _height, _onlyNextWave);
            }
            catch (Exception e)
            {
                _overlay.Clear();
                _waves.Clear();
                if (DateTime.UtcNow >= _nextError)
                {
                    _nextError = DateTime.UtcNow.AddSeconds(15);
                    Log.Warn("波次预告暂不可用：" + e.Message);
                }
            }
        }

        private void HandleHotkey()
        {
            if (!InputService.IsKeyPressed(_toggleKey)) return;
            if (InputService.IsKeyboardCaptured) return;
            if (GRoot.inst != null && GRoot.inst.focus is GTextInput) return;
            _visible = !_visible;
            if (!_visible) _overlay.Clear();
            Log.Info("波次预告显示: " + (_visible ? "开" : "关"));
        }

        private static KeyCode ParseKey(string name, KeyCode fallback)
        {
            if (string.IsNullOrEmpty(name)) return fallback;
            try
            {
                KeyCode key;
                if (Enum.TryParse(name.Trim(), true, out key) && key != KeyCode.None) return key;
            }
            catch { }
            return fallback;
        }

        private void Stop()
        {
            UpdateEvents.LateUpdate -= Draw;
            _overlay.Clear();
        }

        public override void OnUnload() { Stop(); }
    }
}
