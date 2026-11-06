using System;
using System.Collections.Generic;
using CesiumLoader.SDK.Engine;
using CesiumLoader.SDK.Events;
using CesiumLoader.SDK.Gameplay;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.Mods;
using CesiumLoader.SDK.Overlay;
using Core.Net;
using Core.Scene;
using GameLogic;
using party.protocol;
using Tools;
using UI;
using UnityEngine;

namespace OverlayFeedMod
{
    public static class ModEntry
    {
        public static void Main()
        {
            ModBase.Run(new OverlayFeedController());
        }
    }

    public sealed class OverlayFeedController : ModBase
    {
        private readonly OverlayFeedConfig _cfg = new OverlayFeedConfig();
        private readonly ShopRelicTracker _tracker = new ShopRelicTracker();
        private int _lastRound = -1;
        private string _lastShopSent;
        private string _lastRelicSent;
        private bool _lastShopVisibleSent;
        private bool _lastRelicVisibleSent;
        private bool _warnedNoOverlay;

        public override string Name => "覆盖层数据馈送";
        public override string Version => "1.0.0";
        public override string Author => "CesiumLoader";
        public override string Description => "把商店与筹码历史推给 WPF 覆盖层进程（与 ShopRelicPeek 同一数据源）";

        public override void OnInitialize()
        {
            ReadConfig(true);
            _tracker.CardNameResolver = ResolveCardName;
            _tracker.RelicInfoResolver = ResolveRelicInfo;
            _tracker.NickResolver = ResolvePlayerNick;
            _tracker.SlotResolver = ResolvePlayerSlot;
            GameEvents.RawAction += OnRawAction;
            GameEvents.RelicSelected += OnRelicSelected;
            GameEvents.StartAutoHook();
            UpdateEvents.LateUpdate += Draw;
            Context?.RegisterCleanup(Stop);
            Log.Info("覆盖层数据馈送已启用（backend=wpf），通道就绪=" + OverlayChannel.IsAvailable);
        }

        private void ReadConfig(bool isInitial = false)
        {
            if (Config == null) return;
            if (isInitial)
            {
                OverlayFeedConfigDocs.Apply(Config);
                Config.SetDefault("enabled", true);
                Config.SetDefault("showShopCards", true);
                Config.SetDefault("showRelicCandidates", true);
                Config.SetDefault("shopDisplayDurationSeconds", 20);
                Config.SetDefault("relicDisplayDurationSeconds", 20);
                Config.SaveIfDirty();
            }
            _cfg.Enabled = Config.GetBool("enabled", true);
            _cfg.ShowShopCards = Config.GetBool("showShopCards", true);
            _cfg.ShowRelicCandidates = Config.GetBool("showRelicCandidates", true);
            _cfg.ShopDisplayDurationSeconds = OverlayFeedConfig.Clamp(Config.GetInt("shopDisplayDurationSeconds", 20), 0, 300, 20);
            _cfg.RelicDisplayDurationSeconds = OverlayFeedConfig.Clamp(Config.GetInt("relicDisplayDurationSeconds", 20), 0, 300, 20);
        }

        public override void OnConfigChanged() => ReadConfig();

        private void OnRawAction(RawActionEvent evt)
        {
            if (evt == null || evt.Data == null || evt.Data.Length == 0) return;
            try
            {
                switch (evt.Id)
                {
                    case 5211:
                        var offer = ByteBuf.ReadObject<SelectRelicC2S>(evt.Data);
                        if (offer != null && offer.Relics != null && offer.Relics.Count > 0)
                            _tracker.RecordRelicCandidates(evt.PlayerId, evt.Sn, new List<int>(offer.Relics));
                        break;
                    case 5029:
                        var shop = ByteBuf.ReadObject<ShopBuyC2S>(evt.Data);
                        if (shop != null && shop.Cards != null && shop.Cards.Count > 0)
                            _tracker.RecordShopEnter(evt.PlayerId, "PVP商店", new List<int>(shop.Cards), shop.Alreadys != null ? new List<bool>(shop.Alreadys) : null);
                        break;
                    case 5215:
                        var pve = ByteBuf.ReadObject<PVEShopBuyC2S>(evt.Data);
                        if (pve != null)
                        {
                            if (pve.IsClose) _tracker.RecordShopClose(evt.PlayerId, DateTime.UtcNow);
                            else if (pve.Cards != null && pve.Cards.Count > 0)
                                _tracker.RecordShopEnter(evt.PlayerId, "PVE商店", new List<int>(pve.Cards), pve.Alreadys != null ? new List<bool>(pve.Alreadys) : null);
                        }
                        break;
                    case 5323:
                        var vendor = ByteBuf.ReadObject<VendorBuyCardC2S>(evt.Data);
                        if (vendor != null && vendor.CardId > 0)
                            _tracker.RecordShopEnter(evt.PlayerId, "商人", new List<int> { vendor.CardId }, new List<bool> { vendor.IsBuy });
                        break;
                    case 5030:
                    case 5216:
                        try { var s = ByteBuf.ReadObject<PVEShopBuyC2S>(evt.Data); if (s != null && s.Alreadys != null) _tracker.RecordShopAlreadys(evt.PlayerId, new List<bool>(s.Alreadys)); } catch { }
                        break;
                }
            }
            catch (Exception ex) { SdkLog.Warn("OverlayFeed", "解析 Action 异常: " + ex.Message); }
        }

        private void OnRelicSelected(long playerId, int relicId) => _tracker.RecordRelicSelected(playerId, relicId, DateTime.UtcNow);

        private void Draw()
        {
            try
            {
                if (!_cfg.Enabled) { Push(null, null, false, false); return; }
                if (!SimpleSingletonProvider<Core.Scene.SceneManager>.hasInstance || SimpleSingletonProvider<Core.Scene.SceneManager>.inst.currentType.Value != SceneType.Battle) { _tracker.Clear(); Push(null, null, false, false); return; }
                if (!SimpleSingletonProvider<GameLogicManager>.hasInstance) { _tracker.Clear(); Push(null, null, false, false); return; }
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                if (gm == null || gm.battle == null) { _tracker.Clear(); Push(null, null, false, false); return; }
                int curRound = gm.room?.curRoomInfo != null ? gm.room.curRoomInfo.Round : 0;
                if (curRound != _lastRound) { _lastRound = curRound; _tracker.OnRoundChanged(); }
                if (!OverlayChannel.IsAvailable)
                {
                    if (!_warnedNoOverlay) { _warnedNoOverlay = true; SdkLog.Warn("OverlayFeed", "CESIUM_OVERLAY_PORT 未就绪（" + (OverlayChannel.UnavailableReason ?? "?") + "），已跳过推送"); }
                    return;
                }
                _warnedNoOverlay = false;
                var now = DateTime.UtcNow;
                string shopText = null; bool shopVisible = false;
                if (_cfg.ShowShopCards && _tracker.HasActiveShop(now, _cfg.ShopDisplayDurationSeconds))
                {
                    string t = _tracker.BuildShopOverlayText(_cfg, now);
                    if (!string.IsNullOrEmpty(t)) { shopText = t; shopVisible = true; }
                }
                string relicText = null; bool relicVisible = false;
                if (_cfg.ShowRelicCandidates && _tracker.HasActiveRelic(now, _cfg.RelicDisplayDurationSeconds))
                {
                    string t = _tracker.BuildRelicOverlayText(_cfg, now);
                    if (!string.IsNullOrEmpty(t)) { relicText = t; relicVisible = true; }
                }
                Push(shopText, relicText, shopVisible, relicVisible);
            }
            catch (Exception ex) { SdkLog.Warn("OverlayFeed", "推送看板异常: " + ex.Message); }
        }

        private void Push(string shopText, string relicText, bool shopVisible, bool relicVisible)
        {
            if (shopVisible)
            {
                if (shopText != _lastShopSent || !_lastShopVisibleSent)
                {
                    if (OverlayChannel.SendPeek("shop", true, shopText, 14, 340, 20, 70))
                    {
                        _lastShopSent = shopText; _lastShopVisibleSent = true;
                    }
                }
            }
            else if (_lastShopVisibleSent)
            {
                if (OverlayChannel.SendPeek("shop", false, null, 14, 340, 20, 70)) { _lastShopVisibleSent = false; _lastShopSent = null; }
            }
            if (relicVisible)
            {
                if (relicText != _lastRelicSent || !_lastRelicVisibleSent)
                {
                    if (OverlayChannel.SendPeek("relic", true, relicText, 14, 340, 20, 320))
                    {
                        _lastRelicSent = relicText; _lastRelicVisibleSent = true;
                    }
                }
            }
            else if (_lastRelicVisibleSent)
            {
                if (OverlayChannel.SendPeek("relic", false, null, 14, 340, 20, 320)) { _lastRelicVisibleSent = false; _lastRelicSent = null; }
            }
        }

        private static string ResolveCardName(int cardId)
        {
            if (cardId <= 0) return "未知卡";
            try { var cfg = cardId.GetCardConfigure(); if (cfg != null) { string cn = cfg.NameID.GetLocal(UIStringType.Card); if (!string.IsNullOrEmpty(cn)) return cn; } } catch { }
            try { if (StaticConfigure.Skill != null && StaticConfigure.Skill.InfoDict.TryGetValue(cardId, out var skillCfg)) { string sn = skillCfg.NameID.GetLocal(UIStringType.Skill); if (!string.IsNullOrEmpty(sn)) return sn; } } catch { }
            return "卡牌" + cardId;
        }

        private static RelicItemInfo ResolveRelicInfo(int relicId)
        {
            if (relicId <= 0) return new RelicItemInfo("未知筹码", 0);
            try { var cfg = relicId.GetRelicInfoConfigure(); if (cfg != null) { string name = cfg.NameID.GetLocal(UIStringType.Relic); int quality = (int)cfg.RelicQualityType; return new RelicItemInfo(string.IsNullOrEmpty(name) ? ("筹码" + relicId) : name, quality); } } catch { }
            return new RelicItemInfo("筹码" + relicId, 0);
        }

        private static string ResolvePlayerNick(long playerId)
        {
            if (playerId == 0) return "未知玩家";
            try { string nick = Players.Nick(playerId); if (!string.IsNullOrEmpty(nick)) return nick; var gm = SimpleSingletonProvider<GameLogicManager>.inst; var pd = gm?.battle?.GetPlayerDataById(playerId); if (pd?.player != null) { string roomNick = pd.player.GetNick(); if (!string.IsNullOrEmpty(roomNick)) return roomNick; } } catch { }
            return "P" + playerId;
        }

        private static int ResolvePlayerSlot(long playerId)
        {
            if (playerId == 0) return -1;
            try { var gm = SimpleSingletonProvider<GameLogicManager>.inst; var battle = gm?.battle; if (battle != null) { for (int s = 0; s < 4; s++) { var pd = battle.GetPlayerDataBySlot(s); if (pd?.player != null && pd.player.Id == playerId) return s; } var data = battle.GetPlayerDataById(playerId); if (data?.player != null) { if (data.player.Slot >= 0 && data.player.Slot < 4) return data.player.Slot; if (data.player.ChangeSlot >= 0 && data.player.ChangeSlot < 4) return data.player.ChangeSlot; } if (battle.PlayerDatas != null) { for (int i = 0; i < battle.PlayerDatas.Count; i++) if (battle.PlayerDatas[i]?.player?.Id == playerId) return i; } } } catch { }
            return -1;
        }

        private void Stop()
        {
            UpdateEvents.LateUpdate -= Draw;
            GameEvents.RawAction -= OnRawAction;
            GameEvents.RelicSelected -= OnRelicSelected;
            _tracker.Clear();
            try { if (OverlayChannel.IsAvailable) { OverlayChannel.SendPeek("shop", false, null, 14, 340, 20, 70); OverlayChannel.SendPeek("relic", false, null, 14, 340, 20, 320); } } catch { }
        }

        public override void OnUnload() => Stop();
    }
}
