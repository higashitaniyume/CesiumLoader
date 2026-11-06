using System;
using System.Collections.Generic;
using CesiumLoader.SDK.Engine;
using CesiumLoader.SDK.Events;
using CesiumLoader.SDK.Gameplay;
using CesiumLoader.SDK.Inputs;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.Mods;
using CesiumLoader.SDK.Overlay;
using Core.Net;
using Core.Scene;
using FairyGUI;
using GameLogic;
using party.protocol;
using Tools;
using UI;
using UnityEngine;

namespace ShopRelicPeekMod
{
    public static class ModEntry
    {
        public static void Main()
        {
            ModBase.Run(new ShopRelicPeekController());
        }
    }

    public sealed class ShopRelicPeekController : ModBase
    {
        private readonly ShopRelicPeekConfig _cfg = new ShopRelicPeekConfig();
        private readonly ShopRelicTracker _tracker = new ShopRelicTracker();
        private readonly PeekOverlayPanel _shopOverlay = new PeekOverlayPanel("ShopPeekOverlay", 25000);
        private readonly PeekOverlayPanel _relicOverlay = new PeekOverlayPanel("RelicPeekOverlay", 25001);
        private KeyCode _clearKeyCode = KeyCode.T;
        private int _lastRound = -1;
        private string _lastShopSent;
        private string _lastRelicSent;
        private bool _lastShopVisibleSent = false;
        private bool _lastRelicVisibleSent = false;
        private bool _wpfWarnedOnce = false;

        public override string Name => "商店与筹码看板";
        public override string Version => "1.2.0";
        public override string Author => "CesiumLoader";
        public override string Description => "在游戏右上侧独立展示进店卡牌与选筹码候选历史，按一至四楼红绿蓝黄排列，支持按键一键清空";

        public override void OnInitialize()
        {
            ReadConfig(isInitial: true);

            _tracker.CardNameResolver = ResolveCardName;
            _tracker.RelicInfoResolver = ResolveRelicInfo;
            _tracker.NickResolver = ResolvePlayerNick;
            _tracker.SlotResolver = ResolvePlayerSlot;

            GameEvents.RawAction += OnRawAction;
            GameEvents.RelicSelected += OnRelicSelected;
            GameEvents.StartAutoHook();

            UpdateEvents.LateUpdate += Draw;
            Context?.RegisterCleanup(Stop);

            Log.Info("商店与筹码看板已启用：右上侧分别展示进店卡牌与筹码候选历史，快捷键: " + _clearKeyCode);
        }

        private void ReadConfig(bool isInitial = false)
        {
            if (Config != null)
            {
                if (isInitial)
                {
                    ShopRelicPeekConfigDocs.Apply(Config);
                    Config.SetDefault("enabled", true);
                    Config.SetDefault("fontSize", 18);
                    Config.SetDefault("clearKey", "T");
                    Config.SetDefault("backend", "internal");
                    Config.SetDefault("showFloorTag", true);

                    Config.SetDefault("showShopCards", true);
                    Config.SetDefault("shopPanelWidth", 340f);
                    Config.SetDefault("shopOffsetX", 20f);
                    Config.SetDefault("shopOffsetY", 70f);
                    Config.SetDefault("shopDisplayDurationSeconds", 20);

                    Config.SetDefault("showRelicCandidates", true);
                    Config.SetDefault("relicPanelWidth", 340f);
                    Config.SetDefault("relicOffsetX", 20f);
                    Config.SetDefault("relicOffsetY", 320f);
                    Config.SetDefault("relicDisplayDurationSeconds", 20);
                    Config.SetDefault("colorRelicByQuality", true);
                }

                _cfg.Enabled = Config.GetBool("enabled", true);
                _cfg.Backend = Config.GetString("backend", "internal") ?? "internal";
                if (_cfg.Backend != "internal" && _cfg.Backend != "wpf") _cfg.Backend = "internal";
                _cfg.FontSize = ShopRelicPeekConfig.Clamp(Config.GetInt("fontSize", 18), 12, 36, 18);
                _cfg.ShowFloorTag = Config.GetBool("showFloorTag", true);

                _cfg.ClearKey = Config.GetString("clearKey", "T");
                if (Enum.TryParse<KeyCode>(_cfg.ClearKey, true, out var parsedKey))
                {
                    _clearKeyCode = parsedKey;
                }
                else
                {
                    _clearKeyCode = KeyCode.T;
                }

                float fallbackPanelWidth = Config.GetFloat("panelWidth", 340f);
                float fallbackOffsetX = Config.GetFloat("offsetX", 20f);
                float fallbackOffsetY = Config.GetFloat("offsetY", 70f);

                _cfg.ShowShopCards = Config.GetBool("showShopCards", true);
                _cfg.ShopPanelWidth = ShopRelicPeekConfig.Clamp(Config.GetFloat("shopPanelWidth", fallbackPanelWidth), 200f, 600f, 340f);
                _cfg.ShopOffsetX = ShopRelicPeekConfig.Clamp(Config.GetFloat("shopOffsetX", fallbackOffsetX), 0f, 1000f, 20f);
                _cfg.ShopOffsetY = ShopRelicPeekConfig.Clamp(Config.GetFloat("shopOffsetY", fallbackOffsetY), 0f, 1000f, 70f);
                _cfg.ShopDisplayDurationSeconds = ShopRelicPeekConfig.Clamp(Config.GetInt("shopDisplayDurationSeconds", 20), 0, 300, 20);

                _cfg.ShowRelicCandidates = Config.GetBool("showRelicCandidates", true);
                _cfg.RelicPanelWidth = ShopRelicPeekConfig.Clamp(Config.GetFloat("relicPanelWidth", fallbackPanelWidth), 200f, 600f, 340f);
                _cfg.RelicOffsetX = ShopRelicPeekConfig.Clamp(Config.GetFloat("relicOffsetX", fallbackOffsetX), 0f, 1000f, 20f);
                _cfg.RelicOffsetY = ShopRelicPeekConfig.Clamp(Config.GetFloat("relicOffsetY", 320f), 0f, 1000f, 320f);
                _cfg.RelicDisplayDurationSeconds = ShopRelicPeekConfig.Clamp(Config.GetInt("relicDisplayDurationSeconds", 20), 0, 300, 20);
                _cfg.ColorRelicByQuality = Config.GetBool("colorRelicByQuality", true);

                if (isInitial) Config.SaveIfDirty();
            }
        }

        public override void OnConfigChanged()
        {
            ReadConfig();
            _lastShopSent = null;
            _lastRelicSent = null;
            _lastShopVisibleSent = false;
            _lastRelicVisibleSent = false;
            _wpfWarnedOnce = false;
            Log.Info("商店与筹码看板配置已重载: enabled=" + _cfg.Enabled + ", backend=" + _cfg.Backend + ", clearKey=" + _clearKeyCode);
        }

        private void OnRawAction(RawActionEvent evt)
        {
            if (evt == null || evt.Data == null || evt.Data.Length == 0) return;

            try
            {
                switch (evt.Id)
                {
                    case 5211: // 筹码格候选 (SelectRelicC2S)
                        {
                            var offer = ByteBuf.ReadObject<SelectRelicC2S>(evt.Data);
                            if (offer != null && offer.Relics != null && offer.Relics.Count > 0)
                            {
                                var relicList = new List<int>(offer.Relics);
                                _tracker.RecordRelicCandidates(evt.PlayerId, evt.Sn, relicList);
                            }
                        }
                        break;

                    case 5029: // PVP 卡牌商店候选 (ShopBuyC2S)
                        {
                            var shop = ByteBuf.ReadObject<ShopBuyC2S>(evt.Data);
                            if (shop != null && shop.Cards != null && shop.Cards.Count > 0)
                            {
                                var cardList = new List<int>(shop.Cards);
                                var alreadys = shop.Alreadys != null ? new List<bool>(shop.Alreadys) : null;
                                _tracker.RecordShopEnter(evt.PlayerId, "PVP商店", cardList, alreadys);
                            }
                        }
                        break;

                    case 5215: // PVE 卡牌商店候选 (PVEShopBuyC2S)
                        {
                            var shop = ByteBuf.ReadObject<PVEShopBuyC2S>(evt.Data);
                            if (shop != null)
                            {
                                if (shop.IsClose)
                                {
                                    _tracker.RecordShopClose(evt.PlayerId, DateTime.UtcNow);
                                }
                                else if (shop.Cards != null && shop.Cards.Count > 0)
                                {
                                    var cardList = new List<int>(shop.Cards);
                                    var alreadys = shop.Alreadys != null ? new List<bool>(shop.Alreadys) : null;
                                    _tracker.RecordShopEnter(evt.PlayerId, "PVE商店", cardList, alreadys);
                                }
                            }
                        }
                        break;

                    case 5323: // 商人买卡 (VendorBuyCardC2S)
                        {
                            var vendor = ByteBuf.ReadObject<VendorBuyCardC2S>(evt.Data);
                            if (vendor != null && vendor.CardId > 0)
                            {
                                var cardList = new List<int> { vendor.CardId };
                                var alreadys = vendor.IsBuy ? new List<bool> { true } : new List<bool> { false };
                                _tracker.RecordShopEnter(evt.PlayerId, "商人", cardList, alreadys);
                            }
                        }
                        break;

                    case 5030: // PVP 商店购买回执
                    case 5216: // PVE 商店购买回执
                        {
                            try
                            {
                                var shop = ByteBuf.ReadObject<PVEShopBuyC2S>(evt.Data);
                                if (shop != null && shop.Alreadys != null)
                                {
                                    _tracker.RecordShopAlreadys(evt.PlayerId, new List<bool>(shop.Alreadys));
                                }
                            }
                            catch { }
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                SdkLog.Warn("ShopRelicPeek", "解析 Action 异常: " + ex.Message);
            }
        }

        private void OnRelicSelected(long playerId, int relicId)
        {
            _tracker.RecordRelicSelected(playerId, relicId, DateTime.UtcNow);
        }

        private bool UseWpf()
        {
            bool wants = _cfg.Backend == "wpf";
            if (!wants) return false;
            if (OverlayChannel.IsAvailable) return true;
            if (!_wpfWarnedOnce)
            {
                _wpfWarnedOnce = true;
                SdkLog.Warn("ShopRelicPeek", "backend=wpf 但 OverlayChannel 不可用（" + (OverlayChannel.UnavailableReason ?? "未知") + "），已降级为游戏内渲染。");
            }
            return false;
        }

        private void PushShopToWpf(string text, bool visible)
        {
            string body = visible ? text : null;
            if (visible && body == _lastShopSent && _lastShopVisibleSent) return;
            if (!visible && !_lastShopVisibleSent) return;
            OverlayChannel.SendPeek("shop", visible, body, _cfg.FontSize, _cfg.ShopPanelWidth, _cfg.ShopOffsetX, _cfg.ShopOffsetY);
            _lastShopSent = body;
            _lastShopVisibleSent = visible;
        }

        private void PushRelicToWpf(string text, bool visible)
        {
            string body = visible ? text : null;
            if (visible && body == _lastRelicSent && _lastRelicVisibleSent) return;
            if (!visible && !_lastRelicVisibleSent) return;
            OverlayChannel.SendPeek("relic", visible, body, _cfg.FontSize, _cfg.RelicPanelWidth, _cfg.RelicOffsetX, _cfg.RelicOffsetY);
            _lastRelicSent = body;
            _lastRelicVisibleSent = visible;
        }

        private void HideAll()
        {
            _shopOverlay.Hide();
            _relicOverlay.Hide();
            if (_cfg.Backend == "wpf" && OverlayChannel.IsAvailable)
            {
                PushShopToWpf(null, false);
                PushRelicToWpf(null, false);
            }
        }

        private void Draw()
        {
            try
            {
                if (CheckClearHotkey()) return;

                if (!_cfg.Enabled)
                {
                    HideAll();
                    return;
                }

                bool useWpf = UseWpf();

                if (!SimpleSingletonProvider<Core.Scene.SceneManager>.hasInstance ||
                    SimpleSingletonProvider<Core.Scene.SceneManager>.inst.currentType.Value != SceneType.Battle)
                {
                    _tracker.Clear();
                    HideAll();
                    return;
                }

                if (!SimpleSingletonProvider<GameLogicManager>.hasInstance)
                {
                    _tracker.Clear();
                    HideAll();
                    return;
                }

                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                if (gm == null || gm.battle == null)
                {
                    _tracker.Clear();
                    HideAll();
                    return;
                }

                int curRound = gm.room?.curRoomInfo != null ? gm.room.curRoomInfo.Round : 0;
                if (curRound != _lastRound)
                {
                    _lastRound = curRound;
                    _tracker.OnRoundChanged();
                }

                var now = DateTime.UtcNow;

                // 1. 商店独立看板
                bool shopVisible = false;
                if (_cfg.ShowShopCards && _tracker.HasActiveShop(now, _cfg.ShopDisplayDurationSeconds))
                {
                    string shopText = _tracker.BuildShopOverlayText(_cfg, now);
                    if (!string.IsNullOrEmpty(shopText))
                    {
                        if (useWpf)
                        {
                            _shopOverlay.Hide();
                            PushShopToWpf(shopText, true);
                        }
                        else
                        {
                            _shopOverlay.Render(shopText, _cfg.ShopPanelWidth, _cfg.ShopOffsetX, _cfg.ShopOffsetY, _cfg.FontSize);
                        }
                        shopVisible = true;
                    }
                }

                if (!shopVisible)
                {
                    if (useWpf) PushShopToWpf(null, false);
                    else _shopOverlay.Hide();
                }

                // 2. 筹码独立看板
                bool relicVisible = false;
                if (_cfg.ShowRelicCandidates && _tracker.HasActiveRelic(now, _cfg.RelicDisplayDurationSeconds))
                {
                    string relicText = _tracker.BuildRelicOverlayText(_cfg, now);
                    if (!string.IsNullOrEmpty(relicText))
                    {
                        if (useWpf)
                        {
                            _relicOverlay.Hide();
                            PushRelicToWpf(relicText, true);
                        }
                        else
                        {
                            // 筹码默认位于 RelicOffsetY；若上方商店看板正在显示，则自动避让并排在商店面板下方（距其底部 12px），绝不遮挡商店信息
                            float targetRelicY = _cfg.RelicOffsetY;
                            if (shopVisible && _shopOverlay.IsVisible)
                            {
                                targetRelicY = Math.Max(targetRelicY, _shopOverlay.Bottom + 12f);
                            }
                            _relicOverlay.Render(relicText, _cfg.RelicPanelWidth, _cfg.RelicOffsetX, targetRelicY, _cfg.FontSize);
                        }
                        relicVisible = true;
                    }
                }

                if (!relicVisible)
                {
                    if (useWpf) PushRelicToWpf(null, false);
                    else _relicOverlay.Hide();
                }
            }
            catch (Exception ex)
            {
                _shopOverlay.Hide();
                _relicOverlay.Hide();
                SdkLog.Warn("ShopRelicPeek", "渲染看板异常: " + ex.Message);
            }
        }

        private bool CheckClearHotkey()
        {
            if (_clearKeyCode == KeyCode.None) return false;
            try
            {
                if (InputService.IsKeyPressed(_clearKeyCode))
                {
                    // 避免在打字聊天或按键被独占时误触清空
                    if (InputService.IsKeyboardCaptured) return false;
                    if (GRoot.inst != null && GRoot.inst.focus is GTextInput) return false;

                    _tracker.Clear();
                    _shopOverlay.Clear();
                    _relicOverlay.Clear();
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static string ResolveCardName(int cardId)
        {
            if (cardId <= 0) return "未知卡";
            try
            {
                var cfg = cardId.GetCardConfigure();
                if (cfg != null)
                {
                    string cn = cfg.NameID.GetLocal(UIStringType.Card);
                    if (!string.IsNullOrEmpty(cn)) return cn;
                }
            }
            catch { }

            try
            {
                if (StaticConfigure.Skill != null && StaticConfigure.Skill.InfoDict.TryGetValue(cardId, out var skillCfg))
                {
                    string sn = skillCfg.NameID.GetLocal(UIStringType.Skill);
                    if (!string.IsNullOrEmpty(sn)) return sn;
                }
            }
            catch { }

            return "卡牌" + cardId;
        }

        private static RelicItemInfo ResolveRelicInfo(int relicId)
        {
            if (relicId <= 0) return new RelicItemInfo("未知筹码", 0);
            try
            {
                var cfg = relicId.GetRelicInfoConfigure();
                if (cfg != null)
                {
                    string name = cfg.NameID.GetLocal(UIStringType.Relic);
                    int quality = (int)cfg.RelicQualityType;
                    return new RelicItemInfo(string.IsNullOrEmpty(name) ? ("筹码" + relicId) : name, quality);
                }
            }
            catch { }

            return new RelicItemInfo("筹码" + relicId, 0);
        }

        private static string ResolvePlayerNick(long playerId)
        {
            if (playerId == 0) return "未知玩家";
            try
            {
                string nick = Players.Nick(playerId);
                if (!string.IsNullOrEmpty(nick)) return nick;

                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                var pd = gm?.battle?.GetPlayerDataById(playerId);
                if (pd?.player != null)
                {
                    string roomNick = pd.player.GetNick();
                    if (!string.IsNullOrEmpty(roomNick)) return roomNick;
                }
            }
            catch { }

            return "P" + playerId;
        }

        private static int ResolvePlayerSlot(long playerId)
        {
            if (playerId == 0) return -1;
            try
            {
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                var battle = gm?.battle;
                if (battle != null)
                {
                    for (int s = 0; s < 4; s++)
                    {
                        var pd = battle.GetPlayerDataBySlot(s);
                        if (pd?.player != null && pd.player.Id == playerId)
                            return s;
                    }

                    var data = battle.GetPlayerDataById(playerId);
                    if (data?.player != null)
                    {
                        if (data.player.Slot >= 0 && data.player.Slot < 4)
                            return data.player.Slot;
                        if (data.player.ChangeSlot >= 0 && data.player.ChangeSlot < 4)
                            return data.player.ChangeSlot;
                    }

                    if (battle.PlayerDatas != null)
                    {
                        for (int i = 0; i < battle.PlayerDatas.Count; i++)
                        {
                            if (battle.PlayerDatas[i]?.player?.Id == playerId)
                                return i;
                        }
                    }
                }
            }
            catch { }

            return -1;
        }

        private void Stop()
        {
            UpdateEvents.LateUpdate -= Draw;
            GameEvents.RawAction -= OnRawAction;
            GameEvents.RelicSelected -= OnRelicSelected;
            _tracker.Clear();
            _shopOverlay.Clear();
            _relicOverlay.Clear();
            try
            {
                if (_cfg != null && _cfg.Backend == "wpf" && OverlayChannel.IsAvailable)
                {
                    PushShopToWpf(null, false);
                    PushRelicToWpf(null, false);
                }
            }
            catch { }
        }

        public override void OnUnload()
        {
            Stop();
        }
    }
}
