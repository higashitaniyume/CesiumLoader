using System;
using CesiumLoader.SDK.Engine;
using CesiumLoader.SDK.Events;
using CesiumLoader.SDK.Gameplay;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.Mods;
using FairyGUI;
using GameLogic;
using party.model;
using Tools;
using UI;

namespace MapPeekMod
{
    public static class ModEntry
    {
        public static void Main()
        {
            ModBase.Run(new MapPeekController());
        }
    }

    /// <summary>
    /// 选人阶段地图预览：轮询房间信息，仅在选择角色/皮肤/准备阶段显示本局地图。
    /// 只读，不触碰选人流程与游戏状态。
    /// </summary>
    public sealed class MapPeekController : ModBase
    {
        private readonly MapPeekConfig _cfg = new MapPeekConfig();
        private readonly MapPeekOverlay _overlay = new MapPeekOverlay("MapPeekOverlay", 25002);
        private DateTime _nextError = DateTime.MinValue;

        public override string Name => "选人地图预览";
        public override string Version => "1.0.0";
        public override string Author => "CesiumLoader";
        public override string Description => "选人阶段显示本局地图名、模式、难度与人数（左上角浮窗），便于选角前确认地图";

        public override void OnInitialize()
        {
            ReadConfig(isInitial: true);

            GameEvents.MatchSuccess += OnMatchOrSelection;
            GameEvents.HeroSelectionStarted += OnMatchOrSelection;
            GameEvents.StartAutoHook();

            UpdateEvents.LateUpdate += Draw;
            Context?.RegisterCleanup(Stop);

            Log.Info("选人地图预览已启用：选人阶段左上角显示本局地图");
        }

        private void ReadConfig(bool isInitial = false)
        {
            if (Config != null)
            {
                if (isInitial)
                {
                    MapPeekConfigDocs.Apply(Config);
                    Config.SetDefault("enabled", true);
                    Config.SetDefault("fontSize", 18);
                    Config.SetDefault("panelWidth", 340f);
                    Config.SetDefault("offsetX", 20f);
                    Config.SetDefault("offsetY", 70f);
                    Config.SetDefault("showMapId", true);
                    Config.SetDefault("showMode", true);
                    Config.SetDefault("showDifficulty", true);
                    Config.SetDefault("showPlayerCount", true);
                }

                _cfg.Enabled = Config.GetBool("enabled", true);
                _cfg.FontSize = MapPeekConfig.Clamp(Config.GetInt("fontSize", 18), 12, 36, 18);
                _cfg.PanelWidth = MapPeekConfig.Clamp(Config.GetFloat("panelWidth", 340f), 200f, 600f, 340f);
                _cfg.OffsetX = MapPeekConfig.Clamp(Config.GetFloat("offsetX", 20f), 0f, 1000f, 20f);
                _cfg.OffsetY = MapPeekConfig.Clamp(Config.GetFloat("offsetY", 70f), 0f, 1000f, 70f);
                _cfg.ShowMapId = Config.GetBool("showMapId", true);
                _cfg.ShowMode = Config.GetBool("showMode", true);
                _cfg.ShowDifficulty = Config.GetBool("showDifficulty", true);
                _cfg.ShowPlayerCount = Config.GetBool("showPlayerCount", true);

                if (isInitial) Config.SaveIfDirty();
            }
        }

        public override void OnConfigChanged()
        {
            ReadConfig();
            Log.Info("选人地图预览配置已热重载: enabled=" + _cfg.Enabled);
        }

        private void OnMatchOrSelection(Room room)
        {
            // 事件仅用于打日志；真正显隐靠每帧轮询房间状态，保证状态变化时及时隐藏
            if (room != null) Log.Info("选人地图预览：进入匹配/选人阶段（房间 " + room.Id + "），地图 " + room.MapId);
        }

        private void Draw()
        {
            try
            {
                if (!_cfg.Enabled) { _overlay.Hide(); return; }

                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                var room = gm?.room?.curRoomInfo;
                if (room == null || !IsSelectionState(room)) { _overlay.Hide(); return; }

                string text = BuildOverlayText(room);
                _overlay.Render(text, _cfg.PanelWidth, _cfg.OffsetX, _cfg.OffsetY, _cfg.FontSize);
            }
            catch (Exception ex)
            {
                _overlay.Hide();
                if (DateTime.UtcNow >= _nextError)
                {
                    _nextError = DateTime.UtcNow.AddSeconds(15);
                    Log.Warn("选人地图预览暂不可用：" + ex.Message);
                }
            }
        }

        private static bool IsSelectionState(RoomInfo room)
        {
            var s = room.State;
            return s == Room.Types.State.ChoiceHero
                || s == Room.Types.State.ChoiceSkin
                || s == Room.Types.State.Ready1;
        }

        private string BuildOverlayText(RoomInfo room)
        {
            string mapName = ResolveMapName(room.MapId);
            var parts = new System.Text.StringBuilder();
            parts.Append("[color=#FFD24A]【本局地图】[/color] ");
            parts.Append("[color=#FFFFFF][b]").Append(mapName).Append("[/b][/color]");
            if (_cfg.ShowMapId)
            {
                parts.Append(" [color=#9AA7B8](").Append(room.MapId).Append(")[/color]");
            }

            bool firstMeta = true;
            if (_cfg.ShowMode)
            {
                AppendMeta(parts, ref firstMeta, ResolveModeName(room));
            }
            if (_cfg.ShowDifficulty)
            {
                AppendMeta(parts, ref firstMeta, ResolveDifficultyName(room));
            }
            if (_cfg.ShowPlayerCount)
            {
                int count = room.Players != null ? room.Players.Count : 0;
                AppendMeta(parts, ref firstMeta, count + "人对局");
            }

            return parts.ToString();
        }

        private static void AppendMeta(System.Text.StringBuilder sb, ref bool first, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            sb.Append(first ? "\n  [color=#9AA7B8]" : " [color=#9AA7B8]· ");
            sb.Append(value).Append("[/color]");
            first = false;
        }

        private static string ResolveMapName(int mapId)
        {
            if (mapId <= 0) return "未知地图";
            try
            {
                var cfg = mapId.GetMapDataConfigure();
                if (cfg != null)
                {
                    string name = cfg.MapName.GetLocal(UIStringType.Map);
                    if (!string.IsNullOrEmpty(name)) return name;
                }
            }
            catch { }
            return "地图" + mapId;
        }

        private static string ResolveModeName(RoomInfo room)
        {
            try
            {
                int mapType = room.MapType;
                if (StaticConfigure.GameMode != null && StaticConfigure.GameMode.InfoDict != null
                    && StaticConfigure.GameMode.InfoDict.TryGetValue(mapType, out var cfg) && cfg != null)
                {
                    string name = cfg.NameID.GetLocal(UIStringType.GameMode);
                    if (!string.IsNullOrEmpty(name)) return name;
                }
            }
            catch { }
            try { return room.IsPVE() ? "PVE" : "PVP"; } catch { return "对局"; }
        }

        private static string ResolveDifficultyName(RoomInfo room)
        {
            try
            {
                if (room.IsPVE() && StaticConfigure.ChoosingTimeLimit != null
                    && StaticConfigure.ChoosingTimeLimit.DifficultyDict != null
                    && StaticConfigure.ChoosingTimeLimit.DifficultyDict.TryGetValue(room.Difficulty, out var cfg) && cfg != null)
                {
                    string desc = cfg.DescriptionID.GetLocal(UIStringType.ChoosingTimeLimit);
                    if (!string.IsNullOrEmpty(desc)) return desc;
                }
            }
            catch { }
            return "难度" + room.Difficulty;
        }

        private void Stop()
        {
            UpdateEvents.LateUpdate -= Draw;
            GameEvents.MatchSuccess -= OnMatchOrSelection;
            GameEvents.HeroSelectionStarted -= OnMatchOrSelection;
            _overlay.Clear();
        }

        public override void OnUnload()
        {
            Stop();
        }
    }
}
