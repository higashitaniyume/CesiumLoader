using System;
using System.IO;
using CesiumLoader.SDK.Gameplay;
using CesiumLoader.SDK.Mods;
using CesiumLoader.SDK.Windowing;
using Core.Net;
using GameLogic;
using party.protocol;
using Tools;

namespace TurnFocusMod
{
    public sealed partial class TurnFocus : ModBase
    {
        /// <summary>
        /// 包装 ActionStartNotifyS2C(1026)。这条 RPC 不在 SDK 的包装清单里,
        /// 按"捕获原始委托 / 只装一次 / 每局限额"策略安装, 并照常转给游戏自己的处理。
        /// </summary>
        private void EnsureActionStartHook()
        {
            try
            {
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                if (!IsLiveBattle(gm)) return;

                if (!ReferenceEquals(_battle, gm.battle))
                {
                    _battle = gm.battle;
                    _installs = 0;
                    _rpc = null;
                    _original = null;
                    _wrapped = null;
                }

                var rpc = MonoSingletonProvider<NetManager>.inst?.RPC?.ActionStartNotifyS2C;
                if (rpc == null) return;

                var current = rpc.OnActionStartNotifyS2CServerCallBackAsync;
                if (current == null || ReferenceEquals(current, _wrapped)) return;
                if (_installs >= MaxHookInstallsPerBattle) return;

                var original = current;
                ActionStartNotifyS2CRPC.OnActionStartNotifyS2CServerDelegate wrapped =
                    (model, errId, isDispatch) =>
                    {
                        try { OnActionStart(model, errId); }
                        catch (Exception e) { WarnThrottled("处理行动开始通知失败：" + e.Message); }
                        return original(model, errId, isDispatch);
                    };

                _installs++;
                _rpc = rpc;
                _original = original;
                _wrapped = wrapped;
                rpc.OnActionStartNotifyS2CServerCallBackAsync = wrapped;
            }
            catch (Exception e)
            {
                WarnThrottled("安装行动开始挂钩失败：" + e.Message);
            }
        }

        /// <summary>
        /// 包装匹配成功、房间邀请与房间快捷消息的 RPC。它们由游戏在登录后常驻注册,
        /// 不依赖战斗, 所以不能用 IsLiveBattle 作为安装条件; 用 hasInstance 避免提前创建 NetManager。
        /// 房间消息有两种通道: 组队匹配成功后走 MatchTeamChatS2C, 否则走 RoomShortChatS2C。
        /// </summary>
        private void EnsureRoomHooks()
        {
            try
            {
                if (!MonoSingletonProvider<NetManager>.hasInstance) return;

                var manager = MonoSingletonProvider<NetManager>.inst;
                if (manager == null) return;

                var net = manager.RPC;
                if (net == null) return;

                // 重新登录或重连会换一个 RPCMsgManager, 挂接额度按实例重置。
                if (!ReferenceEquals(net, _rpcManager))
                {
                    _rpcManager = net;
                    _roomChatInstalls = 0;
                    _teamChatInstalls = 0;
                    _matchSuccessInstalls = 0;
                    _inviteInstalls = 0;
                    _roomChatRpc = null;
                    _roomChatOriginal = null;
                    _roomChatWrapped = null;
                    _teamChatRpc = null;
                    _teamChatOriginal = null;
                    _teamChatWrapped = null;
                    _matchSuccessRpc = null;
                    _matchSuccessOriginal = null;
                    _matchSuccessWrapped = null;
                    _inviteRpc = null;
                    _inviteOriginal = null;
                    _inviteWrapped = null;
                }

                HookMatchSuccess(net);
                HookFriendInviteNotify(net);
                HookRoomShortChat(net);
                HookMatchTeamChat(net);
            }
            catch (Exception e)
            {
                WarnThrottled("安装房间通知挂钩失败：" + e.Message);
            }
        }

        /// <summary>
        /// 包装房间邀请通知(FriendInviteNotifyS2C)。好友邀请你加入房间时游戏会下发
        /// 这条消息并在右上角显示邀请按钮; 这里只关心"收到了邀请"这件事, 消息体本身
        /// 没有字段。游戏自己的处理照常执行。
        /// </summary>
        private void HookFriendInviteNotify(Core.Net.RPCMsgManager net)
        {
            var rpc = net.FriendInviteNotifyS2C;
            if (rpc == null) return;

            var current = rpc.OnFriendInviteNotifyS2CServerCallBackAsync;
            if (current == null || ReferenceEquals(current, _inviteWrapped)) return;
            if (_inviteInstalls >= MaxRoomHookInstallsPerLogin) return;

            var original = current;
            FriendInviteNotifyS2CRPC.OnFriendInviteNotifyS2CServerDelegate wrapped =
                (model, errId, isDispatch) =>
                {
                    try
                    {
                        if (errId == 0 && model != null) FocusForRoomInvite();
                    }
                    catch (Exception e) { WarnThrottled("处理房间邀请失败：" + e.Message); }
                    return original(model, errId, isDispatch);
                };

            _inviteInstalls++;
            _inviteRpc = rpc;
            _inviteOriginal = original;
            _inviteWrapped = wrapped;
            rpc.OnFriendInviteNotifyS2CServerCallBackAsync = wrapped;
            Log.Info("已挂钩房间邀请通知。");
        }

        private void HookMatchSuccess(Core.Net.RPCMsgManager net)
        {
            var rpc = net.MatchSuccessS2C;
            if (rpc == null) return;

            var current = rpc.OnMatchSuccessS2CServerCallBackAsync;
            if (current == null || ReferenceEquals(current, _matchSuccessWrapped)) return;
            if (_matchSuccessInstalls >= MaxRoomHookInstallsPerLogin) return;

            var original = current;
            MatchSuccessS2CRPC.OnMatchSuccessS2CServerDelegate wrapped =
                (model, errId, isDispatch) =>
                {
                    try
                    {
                        if (errId == 0 && model != null) FocusForMatchSuccess();
                    }
                    catch (Exception e) { WarnThrottled("处理匹配成功通知失败：" + e.Message); }
                    return original(model, errId, isDispatch);
                };

            _matchSuccessInstalls++;
            _matchSuccessRpc = rpc;
            _matchSuccessOriginal = original;
            _matchSuccessWrapped = wrapped;
            rpc.OnMatchSuccessS2CServerCallBackAsync = wrapped;
            Log.Info("已挂钩匹配成功通知。");
        }

        private void HookRoomShortChat(Core.Net.RPCMsgManager net)
        {
            var rpc = net.RoomShortChatS2C;
            if (rpc == null) return;

            var current = rpc.OnRoomShortChatS2CServerCallBackAsync;
            if (current == null || ReferenceEquals(current, _roomChatWrapped)) return;
            if (_roomChatInstalls >= MaxRoomHookInstallsPerLogin) return;

            var original = current;
            RoomShortChatS2CRPC.OnRoomShortChatS2CServerDelegate wrapped =
                (model, errId, isDispatch) =>
                {
                    try
                    {
                        if (errId == 0 && model != null) FocusForRoomChat(model.PlayerId, model.Index);
                    }
                    catch (Exception e) { WarnThrottled("处理房间快捷消息失败：" + e.Message); }
                    return original(model, errId, isDispatch);
                };

            _roomChatInstalls++;
            _roomChatRpc = rpc;
            _roomChatOriginal = original;
            _roomChatWrapped = wrapped;
            rpc.OnRoomShortChatS2CServerCallBackAsync = wrapped;
            Log.Info("已挂钩房间快捷消息。");
        }

        private void HookMatchTeamChat(Core.Net.RPCMsgManager net)
        {
            var rpc = net.MatchTeamChatS2C;
            if (rpc == null) return;

            var current = rpc.OnMatchTeamChatS2CServerCallBackAsync;
            if (current == null || ReferenceEquals(current, _teamChatWrapped)) return;
            if (_teamChatInstalls >= MaxRoomHookInstallsPerLogin) return;

            var original = current;
            MatchTeamChatS2CRPC.OnMatchTeamChatS2CServerDelegate wrapped =
                (model, errId, isDispatch) =>
                {
                    try
                    {
                        if (errId == 0 && model != null) FocusForRoomChat(model.PlayerId, model.Index);
                    }
                    catch (Exception e) { WarnThrottled("处理组队快捷消息失败：" + e.Message); }
                    return original(model, errId, isDispatch);
                };

            _teamChatInstalls++;
            _teamChatRpc = rpc;
            _teamChatOriginal = original;
            _teamChatWrapped = wrapped;
            rpc.OnMatchTeamChatS2CServerCallBackAsync = wrapped;
            Log.Info("已挂钩组队快捷消息。");
        }

        private static bool IsLiveBattle(GameLogicManager gm)
        {
            if (gm?.room?.curRoomInfo == null || gm.account == null || gm.battle == null) return false;
            if (!gm.room.IsInRoom || gm.room.roomController == null) return false;
            if (gm.room.roomController.roomStateType != RoomStateType.RUNNING) return false;
            if (!gm.battle.clientFinishReady) return false;
            if (gm.battle.PlayerDatas == null || gm.battle.PlayerDatas.Count == 0) return false;

            // 回放与观战不是"轮到玩家操作", 不抢焦点。
            if (IsReplayOrWatcher(gm)) return false;

            return true;
        }

        /// <summary>匹配成功开房后、游戏开始前的房间等待阶段(选人/准备)。</summary>
        private static bool IsRoomWaiting(GameLogicManager gm)
        {
            if (gm?.room?.curRoomInfo == null || gm.account == null) return false;
            if (!gm.room.IsInRoom || gm.room.roomController == null) return false;

            var state = gm.room.roomController.roomStateType;
            return state == RoomStateType.WAIT || state == RoomStateType.CHOICE || state == RoomStateType.READY;
        }

        /// <summary>
        /// 房间邀请的右上角按钮会不会出现: 游戏只在不在房间、也不在组队时显示,
        /// 这里按同样的条件判断, 免得按钮本就不显示时还把窗口拉到前台。
        /// </summary>
        private static bool CanShowInviteSignal(GameLogicManager gm)
        {
            if (gm == null) return false;

            try { if (gm.room != null && gm.room.IsInRoom) return false; }
            catch { }

            try
            {
                if (gm.match != null && gm.match.matchData != null && gm.match.matchData.InTeam) return false;
            }
            catch { }

            return true;
        }

        private static bool IsReplayOrWatcher(GameLogicManager gm)
        {
            try { if (gm.replay != null && gm.replay.Session != null && gm.replay.Session.IsReplay) return true; }
            catch { }
            try { if (gm.watch != null && gm.watch.PlayerIsWatcher()) return true; }
            catch { }

            return false;
        }

        private void WarnThrottled(string message)
        {
            long now = DateTime.UtcNow.Ticks;
            if (now < _nextWarnTicks) return;
            _nextWarnTicks = now + WarnIntervalTicks;
            Log.Warn(message);
        }

        private void Unhook()
        {
            if (_subscribed)
            {
                _subscribed = false;
                try { GameEvents.RawAction -= OnRawAction; } catch { }
            }

            try
            {
                if (_rpc != null && ReferenceEquals(_rpc.OnActionStartNotifyS2CServerCallBackAsync, _wrapped))
                    _rpc.OnActionStartNotifyS2CServerCallBackAsync = _original;
            }
            catch { }

            try
            {
                if (_roomChatRpc != null && ReferenceEquals(_roomChatRpc.OnRoomShortChatS2CServerCallBackAsync, _roomChatWrapped))
                    _roomChatRpc.OnRoomShortChatS2CServerCallBackAsync = _roomChatOriginal;
            }
            catch { }

            try
            {
                if (_teamChatRpc != null && ReferenceEquals(_teamChatRpc.OnMatchTeamChatS2CServerCallBackAsync, _teamChatWrapped))
                    _teamChatRpc.OnMatchTeamChatS2CServerCallBackAsync = _teamChatOriginal;
            }
            catch { }

            try
            {
                if (_matchSuccessRpc != null && ReferenceEquals(_matchSuccessRpc.OnMatchSuccessS2CServerCallBackAsync, _matchSuccessWrapped))
                    _matchSuccessRpc.OnMatchSuccessS2CServerCallBackAsync = _matchSuccessOriginal;
            }
            catch { }

            try
            {
                if (_inviteRpc != null && ReferenceEquals(_inviteRpc.OnFriendInviteNotifyS2CServerCallBackAsync, _inviteWrapped))
                    _inviteRpc.OnFriendInviteNotifyS2CServerCallBackAsync = _inviteOriginal;
            }
            catch { }

            _rpc = null;
            _original = null;
            _wrapped = null;
            _roomChatRpc = null;
            _roomChatOriginal = null;
            _roomChatWrapped = null;
            _teamChatRpc = null;
            _teamChatOriginal = null;
            _teamChatWrapped = null;
            _matchSuccessRpc = null;
            _matchSuccessOriginal = null;
            _matchSuccessWrapped = null;
            _inviteRpc = null;
            _inviteOriginal = null;
            _inviteWrapped = null;
            _rpcManager = null;
        }
    }
}
