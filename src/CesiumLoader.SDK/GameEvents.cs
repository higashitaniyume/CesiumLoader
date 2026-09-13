using System;
using System.Collections.Generic;
using Core.Net;
using Cysharp.Threading.Tasks;
using GameLogic;
using party.model;
using party.protocol;
using Tools;

namespace CesiumLoader.SDK
{
    /// <summary>
    /// 游戏事件中枢: 封装全部 RPC 回调 hook, 把原始协议转成简单事件。
    ///
    /// 事件驱动: 订阅事件后调用一次 EnsureHooked()(或直接订阅, 首次订阅自动启动
    /// 内部挂钩循环), SDK 内部每 1 秒检查并重新包装回调 —— 游戏每场战斗会重置
    /// 回调, 检测到新回调就重新包装。mod 无需自己每秒轮询。
    ///
    /// 安全: 只在确认战斗中才挂钩(避免启动早期强制创建 NetManager 单例崩溃);
    /// 包装后的回调会转发给游戏原回调, 不干扰游戏逻辑。
    /// </summary>
    public static class GameEvents
    {
        // ---------- 自动挂钩循环 ----------

        private static bool _autoHookStarted;

        /// <summary>
        /// 启动内部挂钩循环(每 1 秒检查战斗状态并重新包装 RPC 回调)。
        /// 幂等: 多次调用只启动一次。mod 不需要自己每秒轮询 —— 订阅事件后调一次即可。
        /// 游戏启动早期(前 30 秒)不挂钩, 避免强制创建 NetManager 单例崩溃。
        /// </summary>
        public static void StartAutoHook()
        {
            if (_autoHookStarted) return;
            _autoHookStarted = true;
            try
            {
                AutoHookLoopAsync().Forget();
            }
            catch { }
        }

        private static async UniTaskVoid AutoHookLoopAsync()
        {
            // 避开游戏启动早期崩溃窗口(与 ModBase 的 30 秒延迟一致)
            try { await UniTask.Delay(30000); } catch { return; }
            while (true)
            {
                try { EnsureHooked(); }
                catch { }
                try { await UniTask.Delay(1000); } catch { return; }
            }
        }

        // ---------- 事件 ----------

        /// <summary>战斗用牌: (playerId, 真实cardId, 剩余手牌数)。CardId 已从 Guid 反查。</summary>
        public static event Action<long, int, int> CardUsed;
        /// <summary>无牌可出跳过: (playerId)。</summary>
        public static event Action<long> NoCard;
        /// <summary>棋盘效果牌: (playerId, cardId, 剩余手牌数)。</summary>
        public static event Action<long, int, int> EffectCardUsed;
        /// <summary>技能释放: (playerId, skillId)。</summary>
        public static event Action<long, int> SkillUsed;
        /// <summary>快速卡/跟牌: (playerId, cardId, 被跟的originalCardId)。</summary>
        public static event Action<long, int, int> QuickCardUsed;
        /// <summary>掷骰子: (playerId, point, maxPoint)。</summary>
        public static event Action<long, int, int> DiceResult;
        /// <summary>移动: (playerId, 步数, 是否到达)。</summary>
        public static event Action<long, int, bool> Move;
        /// <summary>战斗更新(攻防变化/结束时触发, 已去重): (Battle)。</summary>
        public static event Action<Battle> BattleUpdate;
        /// <summary>战斗攻击骰子: (playerId, 点数)。</summary>
        public static event Action<long, int> BattleDice;
        /// <summary>回合结束选奖励卡: (playerId, cardId)。</summary>
        public static event Action<long, int> RewardCardSelected;
        /// <summary>商店待选卡: (playerId, cardId列表)。</summary>
        public static event Action<long, IReadOnlyList<int>> ShopCandidates;
        /// <summary>筹码格候选遗物: (playerId, relicId列表)。</summary>
        public static event Action<long, IReadOnlyList<int>> RelicCandidates;
        /// <summary>筹码选择结果: (playerId, relicId)。</summary>
        public static event Action<long, int> RelicSelected;
        /// <summary>遗物同步(开局/变更): (playerId, relicId列表)。</summary>
        public static event Action<long, IReadOnlyList<int>> RelicsSynced;
        /// <summary>手牌变化: (playerId, 原始CardInfo列表)。队友的 CardId 可能是负数(服务器掩码)。</summary>
        public static event Action<long, IReadOnlyList<CardInfo>> HandChanged;

        /// <summary>已定义的事件数量(静态常量)。</summary>
        public static int EventCount => 15;

        // ---------- hook 状态 ----------

        private static bool _hookedThisSession;
        private static readonly HashSet<long> _reportedShopSn = new HashSet<long>();
        private static readonly HashSet<long> _reportedRelicSn = new HashSet<long>();
        private static string _lastBattleKey;

        private static Core.Net.PredictActionS2CRPC.OnPredictActionS2CServerDelegate _myWrappedPredict;
        private static Core.Net.BattleUseCardS2CRPC.OnBattleUseCardS2CServerDelegate _myWrappedBattleUseCard;
        private static Core.Net.UseEffectCardS2CRPC.OnUseEffectCardS2CServerDelegate _myWrappedEffectCard;
        private static Core.Net.UseQuickCardS2CRPC.OnUseQuickCardS2CServerDelegate _myWrappedQuickCard;
        private static Core.Net.SelectRelicS2CRPC.OnSelectRelicS2CServerDelegate _myWrappedRelic;
        private static Core.Net.RoomHeroCardChangeS2CRPC.OnRoomHeroCardChangeS2CServerDelegate _myWrappedCardChange;
        private static Core.Net.ThrowDiceResultS2CRPC.OnThrowDiceResultS2CServerDelegate _myWrappedDice;
        private static Core.Net.SelectRewardCardS2CRPC.OnSelectRewardCardS2CServerDelegate _myWrappedRewardCard;
        private static Core.Net.SyncRelicsS2CRPC.OnSyncRelicsS2CServerDelegate _myWrappedSyncRelics;
        private static Core.Net.BattleS2CRPC.OnBattleS2CServerDelegate _myWrappedBattle;
        private static Core.Net.BattleThrowDiceS2CRPC.OnBattleThrowDiceS2CServerDelegate _myWrappedBattleDice;
        private static Core.Net.MoveS2CRPC.OnMoveS2CServerDelegate _myWrappedMove;

        /// <summary>
        /// 挂钩全部 RPC(幂等: 已包装的跳过)。游戏每场战斗会重置回调, 所以每秒调用一次。
        /// </summary>
        public static void EnsureHooked()
        {
            try
            {
                // 只在确认战斗中挂钩, 避免启动早期强制创建 NetManager 单例干扰游戏启动
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                if (gm?.battle == null || gm.battle.PlayerDatas == null || gm.battle.PlayerDatas.Count == 0) return;
                var net = MonoSingletonProvider<NetManager>.inst?.RPC;
                if (net == null) return;

                HookPredict(net);
                HookBattleUseCard(net);
                HookEffectCard(net);
                HookQuickCard(net);
                HookRelic(net);
                HookCardChange(net);
                HookDice(net);
                HookRewardCard(net);
                HookSyncRelics(net);
                HookBattle(net);
                HookBattleDice(net);
                HookMove(net);

                if (!_hookedThisSession)
                {
                    _hookedThisSession = true;
                    SdkLog.Write("EVENTS", "全部 RPC 回调已包装");
                }
            }
            catch (Exception e)
            {
                SdkLog.Write("EVENTS", "挂钩失败: " + e.Message);
            }
        }

        // ---------- 各 hook ----------

        private static void HookPredict(Core.Net.RPCMsgManager net)
        {
            var rpc = net.PredictActionS2C;
            if (rpc == null) return;
            var cur = rpc.OnPredictActionS2CServerCallBackAsync;
            if (cur == null || ReferenceEquals(cur, _myWrappedPredict)) return;
            var original = cur;
            Core.Net.PredictActionS2CRPC.OnPredictActionS2CServerDelegate wrapped = (model, errId, isDispatch) =>
            {
                try { OnPredictActions(model?.Actions); } catch { }
                if (original != null) return original(model, errId, isDispatch);
                return UniTask.CompletedTask;
            };
            rpc.OnPredictActionS2CServerCallBackAsync = wrapped;
            _myWrappedPredict = wrapped;
            SdkLog.Write("EVENTS", "[挂钩] PredictActionS2C");
        }

        private static void HookBattleUseCard(Core.Net.RPCMsgManager net)
        {
            var rpc = net.BattleUseCardS2C;
            if (rpc == null) return;
            var cur = rpc.OnBattleUseCardS2CServerCallBackAsync;
            if (cur == null || ReferenceEquals(cur, _myWrappedBattleUseCard)) return;
            var original = cur;
            Core.Net.BattleUseCardS2CRPC.OnBattleUseCardS2CServerDelegate wrapped = (model, errId, isDispatch) =>
            {
                try { OnBattleUseCard(model, errId, isDispatch); } catch { }
                if (original != null) return original(model, errId, isDispatch);
                return UniTask.CompletedTask;
            };
            rpc.OnBattleUseCardS2CServerCallBackAsync = wrapped;
            _myWrappedBattleUseCard = wrapped;
            SdkLog.Write("EVENTS", "[挂钩] BattleUseCardS2C");
        }

        private static void HookEffectCard(Core.Net.RPCMsgManager net)
        {
            var rpc = net.UseEffectCardS2C;
            if (rpc == null) return;
            var cur = rpc.OnUseEffectCardS2CServerCallBackAsync;
            if (cur == null || ReferenceEquals(cur, _myWrappedEffectCard)) return;
            var original = cur;
            Core.Net.UseEffectCardS2CRPC.OnUseEffectCardS2CServerDelegate wrapped = (model, errId, isDispatch) =>
            {
                try { OnEffectCardUsed(model, errId, isDispatch); } catch { }
                if (original != null) return original(model, errId, isDispatch);
                return UniTask.CompletedTask;
            };
            rpc.OnUseEffectCardS2CServerCallBackAsync = wrapped;
            _myWrappedEffectCard = wrapped;
            SdkLog.Write("EVENTS", "[挂钩] UseEffectCardS2C");
        }

        private static void HookQuickCard(Core.Net.RPCMsgManager net)
        {
            var rpc = net.UseQuickCardS2C;
            if (rpc == null) return;
            var cur = rpc.OnUseQuickCardS2CServerCallBackAsync;
            if (cur == null || ReferenceEquals(cur, _myWrappedQuickCard)) return;
            var original = cur;
            Core.Net.UseQuickCardS2CRPC.OnUseQuickCardS2CServerDelegate wrapped = (model, errId, isDispatch) =>
            {
                try { OnQuickCardUsed(model, errId, isDispatch); } catch { }
                if (original != null) return original(model, errId, isDispatch);
                return UniTask.CompletedTask;
            };
            rpc.OnUseQuickCardS2CServerCallBackAsync = wrapped;
            _myWrappedQuickCard = wrapped;
            SdkLog.Write("EVENTS", "[挂钩] UseQuickCardS2C");
        }

        private static void HookRelic(Core.Net.RPCMsgManager net)
        {
            var rpc = net.SelectRelicS2C;
            if (rpc == null) return;
            var cur = rpc.OnSelectRelicS2CServerCallBackAsync;
            if (cur == null || ReferenceEquals(cur, _myWrappedRelic)) return;
            var original = cur;
            Core.Net.SelectRelicS2CRPC.OnSelectRelicS2CServerDelegate wrapped = (model, errId, isDispatch) =>
            {
                try { OnRelicSelected(model, errId, isDispatch); } catch { }
                if (original != null) return original(model, errId, isDispatch);
                return UniTask.CompletedTask;
            };
            rpc.OnSelectRelicS2CServerCallBackAsync = wrapped;
            _myWrappedRelic = wrapped;
            SdkLog.Write("EVENTS", "[挂钩] SelectRelicS2C");
        }

        private static void HookCardChange(Core.Net.RPCMsgManager net)
        {
            var rpc = net.RoomHeroCardChangeS2C;
            if (rpc == null) return;
            var cur = rpc.OnRoomHeroCardChangeS2CServerCallBackAsync;
            if (cur == null || ReferenceEquals(cur, _myWrappedCardChange)) return;
            var original = cur;
            Core.Net.RoomHeroCardChangeS2CRPC.OnRoomHeroCardChangeS2CServerDelegate wrapped = (model, errId, isDispatch) =>
            {
                try { OnHeroCardChange(model, errId, isDispatch); } catch { }
                if (original != null) return original(model, errId, isDispatch);
                return UniTask.CompletedTask;
            };
            rpc.OnRoomHeroCardChangeS2CServerCallBackAsync = wrapped;
            _myWrappedCardChange = wrapped;
            SdkLog.Write("EVENTS", "[挂钩] RoomHeroCardChangeS2C");
        }

        private static void HookDice(Core.Net.RPCMsgManager net)
        {
            var rpc = net.ThrowDiceResultS2C;
            if (rpc == null) return;
            var cur = rpc.OnThrowDiceResultS2CServerCallBackAsync;
            if (cur == null || ReferenceEquals(cur, _myWrappedDice)) return;
            var original = cur;
            Core.Net.ThrowDiceResultS2CRPC.OnThrowDiceResultS2CServerDelegate wrapped = (model, errId, isDispatch) =>
            {
                try { OnDiceResult(model, errId, isDispatch); } catch { }
                if (original != null) return original(model, errId, isDispatch);
                return UniTask.CompletedTask;
            };
            rpc.OnThrowDiceResultS2CServerCallBackAsync = wrapped;
            _myWrappedDice = wrapped;
            SdkLog.Write("EVENTS", "[挂钩] ThrowDiceResultS2C");
        }

        private static void HookRewardCard(Core.Net.RPCMsgManager net)
        {
            var rpc = net.SelectRewardCardS2C;
            if (rpc == null) return;
            var cur = rpc.OnSelectRewardCardS2CServerCallBackAsync;
            if (cur == null || ReferenceEquals(cur, _myWrappedRewardCard)) return;
            var original = cur;
            Core.Net.SelectRewardCardS2CRPC.OnSelectRewardCardS2CServerDelegate wrapped = (model, errId, isDispatch) =>
            {
                try { OnRewardCard(model, errId, isDispatch); } catch { }
                if (original != null) return original(model, errId, isDispatch);
                return UniTask.CompletedTask;
            };
            rpc.OnSelectRewardCardS2CServerCallBackAsync = wrapped;
            _myWrappedRewardCard = wrapped;
            SdkLog.Write("EVENTS", "[挂钩] SelectRewardCardS2C");
        }

        private static void HookSyncRelics(Core.Net.RPCMsgManager net)
        {
            var rpc = net.SyncRelicsS2C;
            if (rpc == null) return;
            var cur = rpc.OnSyncRelicsS2CServerCallBackAsync;
            if (cur == null || ReferenceEquals(cur, _myWrappedSyncRelics)) return;
            var original = cur;
            Core.Net.SyncRelicsS2CRPC.OnSyncRelicsS2CServerDelegate wrapped = (model, errId, isDispatch) =>
            {
                try { OnSyncRelics(model, errId, isDispatch); } catch { }
                if (original != null) return original(model, errId, isDispatch);
                return UniTask.CompletedTask;
            };
            rpc.OnSyncRelicsS2CServerCallBackAsync = wrapped;
            _myWrappedSyncRelics = wrapped;
            SdkLog.Write("EVENTS", "[挂钩] SyncRelicsS2C");
        }

        private static void HookBattle(Core.Net.RPCMsgManager net)
        {
            var rpc = net.BattleS2C;
            if (rpc == null) return;
            var cur = rpc.OnBattleS2CServerCallBackAsync;
            if (cur == null || ReferenceEquals(cur, _myWrappedBattle)) return;
            var original = cur;
            Core.Net.BattleS2CRPC.OnBattleS2CServerDelegate wrapped = (model, errId, isDispatch) =>
            {
                try { OnBattle(model, errId, isDispatch); } catch { }
                if (original != null) return original(model, errId, isDispatch);
                return UniTask.CompletedTask;
            };
            rpc.OnBattleS2CServerCallBackAsync = wrapped;
            _myWrappedBattle = wrapped;
            SdkLog.Write("EVENTS", "[挂钩] BattleS2C");
        }

        private static void HookBattleDice(Core.Net.RPCMsgManager net)
        {
            var rpc = net.BattleThrowDiceS2C;
            if (rpc == null) return;
            var cur = rpc.OnBattleThrowDiceS2CServerCallBackAsync;
            if (cur == null || ReferenceEquals(cur, _myWrappedBattleDice)) return;
            var original = cur;
            Core.Net.BattleThrowDiceS2CRPC.OnBattleThrowDiceS2CServerDelegate wrapped = (model, errId, isDispatch) =>
            {
                try { OnBattleDice(model, errId, isDispatch); } catch { }
                if (original != null) return original(model, errId, isDispatch);
                return UniTask.CompletedTask;
            };
            rpc.OnBattleThrowDiceS2CServerCallBackAsync = wrapped;
            _myWrappedBattleDice = wrapped;
            SdkLog.Write("EVENTS", "[挂钩] BattleThrowDiceS2C");
        }

        private static void HookMove(Core.Net.RPCMsgManager net)
        {
            var rpc = net.MoveS2C;
            if (rpc == null) return;
            var cur = rpc.OnMoveS2CServerCallBackAsync;
            if (cur == null || ReferenceEquals(cur, _myWrappedMove)) return;
            var original = cur;
            Core.Net.MoveS2CRPC.OnMoveS2CServerDelegate wrapped = (model, errId, isDispatch) =>
            {
                try { OnMove(model, errId, isDispatch); } catch { }
                if (original != null) return original(model, errId, isDispatch);
                return UniTask.CompletedTask;
            };
            rpc.OnMoveS2CServerCallBackAsync = wrapped;
            _myWrappedMove = wrapped;
            SdkLog.Write("EVENTS", "[挂钩] MoveS2C");
        }

        // ---------- 事件触发 ----------

        private static void OnBattleUseCard(BattleUseCardS2C model, int errId, bool isDispatch)
        {
            if (model == null || errId != 0) return;
            if (model.CardId != 0)
            {
                int realCardId = Players.ResolveCardGuid(model.PlayerId, model.CardId);
                int remain = Players.HandCount(model.PlayerId);
                CardUsed?.Invoke(model.PlayerId, realCardId, remain);
            }
            else if (model.NoCard)
            {
                NoCard?.Invoke(model.PlayerId);
            }
        }

        private static void OnEffectCardUsed(UseEffectCardS2C model, int errId, bool isDispatch)
        {
            if (model == null || errId != 0) return;
            if (model.UseSkill)
            {
                SkillUsed?.Invoke(model.PlayerId, model.SkillId);
                return;
            }
            if (model.CardId != 0)
            {
                int remain = Players.HandCount(model.PlayerId);
                EffectCardUsed?.Invoke(model.PlayerId, model.CardId, remain);
            }
        }

        private static void OnQuickCardUsed(UseQuickCardS2C model, int errId, bool isDispatch)
        {
            if (model == null || errId != 0 || model.CardId == 0) return;
            QuickCardUsed?.Invoke(model.PlayerId, model.CardId, model.OriginalCardId);
        }

        private static void OnRelicSelected(SelectRelicS2C model, int errId, bool isDispatch)
        {
            if (model == null || errId != 0 || model.IsReroll || model.RelicId == 0) return;
            RelicSelected?.Invoke(model.PlayerId, model.RelicId);
        }

        private static void OnHeroCardChange(RoomHeroCardChangeS2C model, int errId, bool isDispatch)
        {
            if (model == null || errId != 0 || model.Cards == null || model.Cards.Count == 0) return;
            HandChanged?.Invoke(model.PlayerId, model.Cards);
        }

        private static void OnDiceResult(ThrowDiceResultS2C model, int errId, bool isDispatch)
        {
            if (model == null || errId != 0) return;
            DiceResult?.Invoke(model.PlayerId, model.Point, model.MaxPoint);
        }

        private static void OnRewardCard(SelectRewardCardS2C model, int errId, bool isDispatch)
        {
            if (model == null || errId != 0 || model.CardId == 0) return;
            RewardCardSelected?.Invoke(model.PlayerId, model.CardId);
        }

        private static void OnSyncRelics(SyncRelicsS2C model, int errId, bool isDispatch)
        {
            if (model == null || errId != 0 || model.SelectRelics == null) return;
            var ids = new List<int>();
            foreach (var kv in model.SelectRelics)
                if (kv.Value && kv.Key > 0) ids.Add(kv.Key);
            if (ids.Count > 0) RelicsSynced?.Invoke(model.PlayerId, ids);
        }

        private static void OnBattle(BattleS2C model, int errId, bool isDispatch)
        {
            if (model == null || errId != 0 || model.Battle == null) return;
            var b = model.Battle;
            if (b.Attacker == null || b.Defender == null) return;
            // 去重: 攻防数值或结束状态变化才触发
            string key = $"{b.Attacker.PlayerId}|{b.Defender.PlayerId}|{b.Attacker.Point}|{b.Defender.Point}|{b.Attacker.Atk}|{b.Defender.Def}|{b.IsEnd}";
            if (key == _lastBattleKey) return;
            _lastBattleKey = key;
            BattleUpdate?.Invoke(b);
        }

        private static void OnBattleDice(BattleThrowDiceS2C model, int errId, bool isDispatch)
        {
            if (model == null || errId != 0) return;
            BattleDice?.Invoke(model.PlayerId, model.Val);
        }

        private static void OnMove(MoveS2C model, int errId, bool isDispatch)
        {
            if (model == null || errId != 0 || model.NodeIds == null || model.NodeIds.Count == 0) return;
            Move?.Invoke(model.PlayerId, model.NodeIds.Count, model.End);
        }

        private static void OnPredictActions(Google.Protobuf.Collections.RepeatedField<party.model.Action> actions)
        {
            if (actions == null) return;
            foreach (var a in actions)
            {
                if (a == null || a.Data == null || a.Data.Length == 0) continue;
                try
                {
                    switch (a.Id)
                    {
                        case 5029: // PVP 商店
                        case 5215: // PVE 商店
                            {
                                if (_reportedShopSn.Contains(a.Sn)) break;
                                var d = ByteBuf.ReadObject<ShopBuyC2S>(a.Data.ToByteArray());
                                if (d != null && d.Cards != null && d.Cards.Count > 0)
                                {
                                    _reportedShopSn.Add(a.Sn);
                                    var ids = new List<int>(d.Cards);
                                    ShopCandidates?.Invoke(a.PlayerId, ids);
                                }
                            }
                            break;
                        case 5211: // 筹码格(遗物)
                            {
                                if (_reportedRelicSn.Contains(a.Sn)) break;
                                var d = ByteBuf.ReadObject<SelectRelicC2S>(a.Data.ToByteArray());
                                if (d != null && d.Relics != null && d.Relics.Count > 0)
                                {
                                    _reportedRelicSn.Add(a.Sn);
                                    var ids = new List<int>(d.Relics);
                                    RelicCandidates?.Invoke(a.PlayerId, ids);
                                }
                            }
                            break;
                    }
                }
                catch { }
            }
        }
    }
}
