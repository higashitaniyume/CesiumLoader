using System;
using System.Collections.Generic;
using CesiumLoader.SDK.Gameplay;
using GameLogic;
using party.model;
using party.protocol;
using Tools;

namespace PvePanelMod.Hands
{
    /// <summary>抽屉里一种牌：同一张牌（同 id、同是否临时）合成一格，Count 是张数。</summary>
    internal sealed class CardGroup
    {
        public int CardId;
        public bool IsTemp;
        public int Count;
    }

    /// <summary>一个玩家的手牌：Known=false 表示看不到（真人队友且没开第三方服务，或服务还没返回）。</summary>
    internal sealed class HandView
    {
        public bool Known;
        public readonly List<CardGroup> Groups = new List<CardGroup>();
    }

    /// <summary>
    /// 手牌从哪来（PVE 面板对 HandViewerMod 的主要改动）：
    ///   1. 先读本地：自己的牌、Bot 的牌游戏本地就有（BattlePlayerData.cardContainer 里 CardId &gt; 0），不联网；
    ///   2. 只有真人队友的牌被游戏遮住（CardId = 0），这时才问第三方观战服务 astralpartycards.hiynet.com，
    ///      而且要在配置里手动打开 HandService（默认关：CesiumLoader 内置 mod 约定只读、不联网）；
    ///   3. 只在手牌抽屉打开时查询；全是 Bot 的对局一次请求都不发。
    /// 查询、限流、响应校验沿用 HandViewerMod 的 HandQueryController（见 QueryController.cs 的「改动」注释）。
    /// </summary>
    internal sealed class HandService : IDisposable
    {
        public const string UserAgent = "AstralParty.Toys Mod/0.1.0 (PvePanelMod; based on HandViewerMod)";
        private HandQueryController _query;
        private string _endpoint;
        private bool _serviceEnabled;
        private readonly Dictionary<long, HandView> _views = new Dictionary<long, HandView>();

        public string LastStatus => _query?.Status;
        public int Revision => _query?.Revision ?? 0;

        public HandService()
        {
            GameEvents.HandChanged += OnHandChanged;
            GameEvents.HeroAttrUpdated += OnAttrChanged;
        }

        public void Configure(bool serviceEnabled, string endpoint)
        {
            _serviceEnabled = serviceEnabled;
            if (_query == null || endpoint != _endpoint)
            {
                _query?.Dispose();
                _endpoint = endpoint;
                _query = new HandQueryController(code => new UnityHandRequest(_endpoint, code, 20));
            }
        }

        private static long Now => DateTime.UtcNow.Ticks / 10000;

        private void OnHandChanged(long id, IReadOnlyList<CardInfo> cards) { _query?.MarkDirty(Now); }

        private void OnAttrChanged(UpdateHeroAttrS2C model)
        {
            if (model == null) return;
            foreach (var effect in model.EffectDatas)
                if (effect.Card != null || effect.ConvertCard != null) { _query?.MarkDirty(Now); break; }
        }

        /// <summary>每帧（或每 0.25 秒）调用一次：读本地手牌，必要时推动第三方查询。返回按玩家 id 的手牌。</summary>
        public Dictionary<long, HandView> Tick(IList<PlayerRow> players, bool drawerOpen)
        {
            long now = Now;
            _views.Clear();
            bool anyMasked = false;
            foreach (var r in players)
            {
                var v = ReadLocal(r.Id);
                if (v == null) { anyMasked = true; v = new HandView(); }
                _views[r.Id] = v;
            }

            // 第三方服务：开关打开、抽屉打开、确实有人的牌被遮住，才查询
            bool viewing = _serviceEnabled && drawerOpen && anyMasked;
            string roomId = null, code = null;
            if (viewing) ReadRoom(out roomId, out code);
            if (_query != null)
            {
                _query.SetRoom(roomId, code, now);
                _query.SetViewing(viewing, now);
                _query.Tick(now);
                var snap = _query.Snapshot;
                if (snap != null && snap.roomId == roomId)
                {
                    foreach (var ph in snap.players)
                    {
                        long id;
                        if (!long.TryParse(ph.playerId, out id) || !ph.handKnown) continue;
                        HandView v;
                        if (!_views.TryGetValue(id, out v) || v.Known) continue;   // 本地已知的不用服务的结果
                        v.Known = true;
                        foreach (var c in ph.cards) Add(v, c.cardId, c.isTemp);
                    }
                }
            }
            return _views;
        }

        /// <summary>本地手牌：所有牌都有真实 CardId 才算已知；有被遮住的（CardId = 0）返回 null。</summary>
        private static HandView ReadLocal(long playerId)
        {
            try
            {
                var pd = Players.Get(playerId);
                var cards = pd?.cardContainer?._HandCards;
                if (cards == null) return null;
                var v = new HandView { Known = true };
                foreach (var c in cards)
                {
                    if (c == null) continue;
                    if (c.CardId <= 0) return null;
                    Add(v, c.CardId, c.IsTemp);
                }
                return v;
            }
            catch { return null; }
        }

        private static void Add(HandView v, int cardId, bool isTemp)
        {
            foreach (var g in v.Groups)
                if (g.CardId == cardId && g.IsTemp == isTemp) { g.Count++; return; }
            v.Groups.Add(new CardGroup { CardId = cardId, IsTemp = isTemp, Count = 1 });
        }

        /// <summary>房间号和观战码（照 HandViewerMod：只在运行中的 PVE 对局、模式 4/12、非回放时有）。观战码不写日志。</summary>
        private static void ReadRoom(out string roomId, out string code)
        {
            roomId = null; code = null;
            try
            {
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                var logic = gm?.room;
                var room = logic?.curRoomInfo;
                if (room?.info == null) return;
                if (gm.replay?.Session?.IsReplay == true) return;
                if (logic.roomController.roomStateType != RoomStateType.RUNNING) return;
                if (room.MapType != 4 && room.MapType != 12) return;
                roomId = room.info.Id.ToString();
                code = room.watchCode;
            }
            catch { roomId = null; code = null; }
        }

        public void Dispose()
        {
            GameEvents.HandChanged -= OnHandChanged;
            GameEvents.HeroAttrUpdated -= OnAttrChanged;
            _query?.Dispose();
            _query = null;
        }
    }
}
