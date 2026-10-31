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
        private void OnRawAction(RawActionEvent action)
        {
            if (!_enabled || action == null) return;
            if (!TurnFocusPolicy.IsFocusAction(action.Id)) return;
            FocusFor(TurnFocusPolicy.ActionName(action.Id), action.PlayerId);
        }

        private void OnActionStart(ActionStartNotifyS2C model, int errId)
        {
            if (errId != 0 || model == null) return;
            FocusFor("回合开始", model.PlayerId);
        }

        /// <summary>所有触发点共用: 校验上下文/身份/冷却, 再写一条置前请求。</summary>
        private void FocusFor(string reason, long playerId)
        {
            if (!_enabled) return;

            try
            {
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                if (!IsLiveBattle(gm)) return;

                long selfPlayerId = gm.account.GetPlayerID();
                if (!TurnFocusPolicy.IsMyTurn(playerId, selfPlayerId)) return;

                long now = DateTime.UtcNow.Ticks;
                bool available = WindowFocus.IsAvailable;
                if (!TurnFocusPolicy.ShouldRequest(playerId, selfPlayerId, true, false,
                        available, now, _lastRequestTicks, _cooldownSeconds))
                {
                    if (!available) WarnThrottled("窗口置前通道不可用：" + (WindowFocus.UnavailableReason ?? "原因未知"));
                    return;
                }

                _lastRequestTicks = now;
                RequestFocus("轮到我" + reason);
            }
            catch (Exception e)
            {
                WarnThrottled("窗口置前执行失败：" + e.Message);
            }
        }

        /// <summary>
        /// 匹配成功触发点: 游戏刚匹配到对局、准备创建房间时置前。
        /// 这条通知到达时房间还没建好, 所以不检查房间状态, 只排除回放/观战。
        /// </summary>
        private void FocusForMatchSuccess()
        {
            if (!_enabled || !_matchSuccessFocus) return;

            try
            {
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                long now = DateTime.UtcNow.Ticks;
                bool available = WindowFocus.IsAvailable;
                if (!TurnFocusPolicy.ShouldRequestMatchSuccess(_enabled, _matchSuccessFocus,
                        IsReplayOrWatcher(gm), available, now, _lastRequestTicks, _cooldownSeconds))
                {
                    if (!available) WarnThrottled("窗口置前通道不可用：" + (WindowFocus.UnavailableReason ?? "原因未知"));
                    return;
                }

                _lastRequestTicks = now;
                RequestFocus("匹配成功");
            }
            catch (Exception e)
            {
                WarnThrottled("窗口置前执行失败：" + e.Message);
            }
        }

        /// <summary>
        /// 房间邀请触发点: 好友邀请你加入房间时, 游戏在主界面右上角显示邀请按钮。
        /// 按游戏同一套条件判断按钮会不会出现(不在房间里、不在组队里), 避免在
        /// 按钮本就不显示的场合抢焦点; 回放、观战不触发。
        /// </summary>
        private void FocusForRoomInvite()
        {
            if (!_enabled || !_roomInviteFocus) return;

            try
            {
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                long now = DateTime.UtcNow.Ticks;
                bool available = WindowFocus.IsAvailable;
                if (!TurnFocusPolicy.ShouldRequestRoomInvite(_enabled, _roomInviteFocus,
                        CanShowInviteSignal(gm), IsReplayOrWatcher(gm), available, now,
                        _lastRequestTicks, _cooldownSeconds))
                {
                    if (!available) WarnThrottled("窗口置前通道不可用：" + (WindowFocus.UnavailableReason ?? "原因未知"));
                    return;
                }

                _lastRequestTicks = now;
                RequestFocus("收到房间邀请");
            }
            catch (Exception e)
            {
                WarnThrottled("窗口置前执行失败：" + e.Message);
            }
        }

        /// <summary>
        /// 房间快捷消息触发点: 匹配成功开房后, 其他玩家发"准备"之类的消息时置前。
        /// 只处理房间等待阶段, 战斗内的快捷消息不抢焦点。
        /// </summary>
        private void FocusForRoomChat(long senderPlayerId, int index)
        {
            if (!_enabled || !_roomChatFocus || senderPlayerId <= 0) return;

            try
            {
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                if (!IsRoomWaiting(gm)) return;

                long selfPlayerId = gm.account.GetPlayerID();
                long now = DateTime.UtcNow.Ticks;
                bool available = WindowFocus.IsAvailable;
                if (!TurnFocusPolicy.ShouldRequestRoomChat(_enabled, _roomChatFocus, true,
                        TurnFocusPolicy.IsOtherPlayer(senderPlayerId, selfPlayerId),
                        IsReplayOrWatcher(gm), available, now, _lastRequestTicks, _cooldownSeconds))
                {
                    if (!available) WarnThrottled("窗口置前通道不可用：" + (WindowFocus.UnavailableReason ?? "原因未知"));
                    return;
                }

                _lastRequestTicks = now;
                RequestFocus("房间里有人发送快捷消息(Index=" + index + ")");
            }
            catch (Exception e)
            {
                WarnThrottled("窗口置前执行失败：" + e.Message);
            }
        }

    }
}
