using System;
using System.Collections.Generic;
using CesiumLoader.SDK.Gameplay;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.Manifests;
using CesiumLoader.SDK.Mods;
using GameLogic;
using Tools;
using party.model;
using party.protocol;

[assembly: ModManifest("手牌查看", HandViewerMod.ModEntry.ModVersion, "CesiumLoader",
    "进入 PVE 对局后自动用观战码查询官方观战手牌；升星右侧三角展开四人独立手牌条，同牌角标计数",
    Permissions = ModPermission.ReadGameState | ModPermission.UI | ModPermission.Network, SdkVersion = "2.3.2")]

namespace HandViewerMod
{
    public sealed class ModEntry : ModBase
    {
        HandQueryController query;
        HandViewerUi ui;
        bool enabled, autoRefresh;
        long nextPoll;
        string lastRoom, lastCode;
        string lastRoomDiagnostic, lastUiError;
        int lastQueryRevision = -1;
        public static void Main() { SdkManifest.ExportSidecar(); Run(new ModEntry(), tag: "HandViewer"); }
        public override string Name => "手牌查看";
        public const string ModVersion = "1.0.7";
        public const string UserAgent = "AstralParty.Toys Mod/" + ModVersion + " (HandViewerMod)";
        public override string Version => ModVersion;
        public override void OnInitialize()
        {
            enabled = Config?.GetBool("Enabled", true) ?? true;
            autoRefresh = Config?.GetBool("AutoRefresh", true) ?? true;
            string endpoint = Config?.GetString("ServiceUrl", "https://astralpartycards.hiynet.com/") ?? "https://astralpartycards.hiynet.com/";
            int timeout = Math.Max(5, Math.Min(60, Config?.GetInt("TimeoutSeconds", 20) ?? 20));
            bool doubleRow = Config?.GetBool("DoubleRow", true) ?? true;
            Config?.Set("Enabled", enabled); Config?.Set("AutoRefresh", autoRefresh);
            Config?.Set("ServiceUrl", endpoint); Config?.Set("TimeoutSeconds", timeout);
            Config?.Set("DoubleRow", doubleRow); Config?.Save();
            query = new HandQueryController(code => new UnityHandRequest(endpoint, code, timeout));
            ui = new HandViewerUi(query, doubleRow);
            GameEvents.HandChanged += HandChanged;
            GameEvents.HeroAttrUpdated += AttrChanged;
            SdkLog.Info("HandViewer", "已启用：支持的 PVE 对局有观战码时自动查询；观战码不写日志。");
        }
        static long Now => DateTime.UtcNow.Ticks / 10000;
        void HandChanged(long id, IReadOnlyList<CardInfo> cards) { if (enabled && autoRefresh) query?.MarkDirty(Now); }
        void AttrChanged(UpdateHeroAttrS2C model)
        {
            if (!enabled || !autoRefresh || model == null) return;
            foreach (var effect in model.EffectDatas)
                if (effect.Card != null || effect.ConvertCard != null) { query?.MarkDirty(Now); break; }
        }
        public override void OnUpdate()
        {
            if (!enabled || query == null) return;
            long now = Now;
            if (now >= nextPoll)
            {
                nextPoll = now + 250;
                string roomId = null, code = null;
                ui.UnavailableReason = "等待运行中的 PVE 对局";
                try
                {
                    var logic = GameLogicManager.inst.room;
                    var room = logic.curRoomInfo;
                    bool replay = GameLogicManager.inst.replay?.Session?.IsReplay == true;
                    string diagnostic = "roomState=" + logic.roomController.roomStateType
                        + " mapType=" + (room?.info?.MapType ?? 0) + " replay=" + replay
                        + " hasWatchCode=" + !string.IsNullOrEmpty(room?.watchCode);
                    if (diagnostic != lastRoomDiagnostic)
                    {
                        lastRoomDiagnostic = diagnostic;
                        SdkLog.Info("HandViewer", "[room] " + diagnostic);
                    }
                    if (replay) ui.UnavailableReason = "回放不查询实时手牌";
                    else if (logic.roomController.roomStateType == RoomStateType.RUNNING && room?.info != null)
                    {
                        if (room.MapType == 4 || room.MapType == 12)
                        {
                            roomId = room.info.Id.ToString(); code = room.watchCode;
                            ui.UnavailableReason = string.IsNullOrEmpty(code) ? "等待服务器下发观战码" : null;
                        }
                        else ui.UnavailableReason = "当前模式 " + room.MapType + " 暂不支持\n观战服务仅支持 4 / 12";
                    }
                }
                catch (Exception)
                {
                    if (lastRoomDiagnostic != "probe-failed")
                    {
                        lastRoomDiagnostic = "probe-failed";
                        SdkLog.Warn("HandViewer", "[room] 房间读取失败，等待下次重试");
                    }
                }
                if (lastRoom != roomId || lastCode != code) { ui.Dispose(); lastRoom = roomId; lastCode = code; }
                query.SetRoom(roomId, code, now);
                // HUD availability is independent of spectator-code and network availability.
                try { ui.Poll(); lastUiError = null; }
                catch (Exception ex)
                {
                    string error = ex.GetType().Name + ": " + ex.Message;
                    if (error != lastUiError)
                    {
                        lastUiError = error;
                        SdkLog.Warn("HandViewer", "[hud] 挂载失败: " + error);
                    }
                }
            }
            query.Tick(now);
            if (lastQueryRevision != query.Revision)
            {
                lastQueryRevision = query.Revision;
                SdkLog.Info("HandViewer", "[query] " + query.Status);
            }
        }
        public override void OnUnload()
        {
            GameEvents.HandChanged -= HandChanged; GameEvents.HeroAttrUpdated -= AttrChanged;
            ui?.Dispose(); query?.Dispose();
        }
    }
}
