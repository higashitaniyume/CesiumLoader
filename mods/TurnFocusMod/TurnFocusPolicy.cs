using System;

namespace TurnFocusMod
{
    /// <summary>
    /// "轮到自己时是否请求置前"的纯判定(不碰游戏单例, 可离线测试)。
    /// </summary>
    internal static class TurnFocusPolicy
    {
        internal const int DefaultCooldownSeconds = 1;
        internal const int MaxCooldownSeconds = 60;

        internal const int ActionSelectRelic = 5211;
        internal const int ActionAskBattle = 5047;
        internal const int ActionBattleUseCard = 5035;
        internal const int ActionBattleAttackDice = 5037;
        internal const int ActionBattleDefend = 5039;

        /// <summary>冷却时间配置纠正: 负数/超大值都收进 [0, 60] 秒。</summary>
        internal static int ClampCooldownSeconds(int seconds)
        {
            if (seconds < 0) return 0;
            return seconds > MaxCooldownSeconds ? MaxCooldownSeconds : seconds;
        }

        /// <summary>动作是否需要"轮到我操作"的置前提示。</summary>
        internal static bool IsFocusAction(int actionId)
        {
            switch (actionId)
            {
                case ActionSelectRelic:
                case ActionAskBattle:
                case ActionBattleUseCard:
                case ActionBattleAttackDice:
                case ActionBattleDefend:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>日志里显示的动作名。</summary>
        internal static string ActionName(int actionId)
        {
            switch (actionId)
            {
                case ActionSelectRelic: return "选择筹码";
                case ActionAskBattle: return "战斗询问";
                case ActionBattleUseCard: return "战斗出牌";
                case ActionBattleAttackDice: return "攻击骰";
                case ActionBattleDefend: return "防御选择";
                default: return "操作";
            }
        }

        /// <summary>动作开始通知里的玩家是否就是自己。</summary>
        internal static bool IsMyTurn(long playerId, long selfPlayerId)
        {
            return playerId > 0 && selfPlayerId > 0 && playerId == selfPlayerId;
        }

        /// <summary>发送者是否是别的玩家(自己在房间里发的快捷消息不需要提示)。</summary>
        internal static bool IsOtherPlayer(long senderPlayerId, long selfPlayerId)
        {
            return senderPlayerId > 0 && selfPlayerId > 0 && senderPlayerId != selfPlayerId;
        }

        /// <summary>距离上一次请求是否已经超过冷却时间(第一次请求恒为 true)。</summary>
        internal static bool CooldownElapsed(long nowTicks, long lastRequestTicks, int cooldownSeconds)
        {
            if (lastRequestTicks <= 0) return true;

            long cooldownTicks = ClampCooldownSeconds(cooldownSeconds) * TimeSpan.TicksPerSecond;
            if (cooldownTicks <= 0) return true;

            long elapsed = nowTicks - lastRequestTicks;
            return elapsed >= cooldownTicks;
        }

        /// <summary>
        /// 汇总判定: 开关打开 / 是自己的操作 / 不在回放或观战 / 加载器通道可用 /
        /// 冷却已过。任一条件不满足都不发请求。
        /// </summary>
        internal static bool ShouldRequest(long playerId, long selfPlayerId, bool enabled,
            bool replayOrWatcher, bool channelAvailable, long nowTicks, long lastRequestTicks,
            int cooldownSeconds)
        {
            if (!enabled) return false;
            if (replayOrWatcher) return false;
            if (!channelAvailable) return false;
            if (!IsMyTurn(playerId, selfPlayerId)) return false;
            return CooldownElapsed(nowTicks, lastRequestTicks, cooldownSeconds);
        }

        /// <summary>
        /// 房间等待阶段(匹配成功开房后、游戏开始前)其他玩家发"准备"类快捷消息时的
        /// 置前判定: 房间消息开关打开 / 仍是等待阶段 / 消息来自别人 / 不在回放或观战 /
        /// 加载器通道可用 / 冷却已过。任一条件不满足都不发请求。
        /// </summary>
        internal static bool ShouldRequestRoomChat(bool enabled, bool roomChatFocus, bool inWaitingRoom,
            bool otherPlayer, bool replayOrWatcher, bool channelAvailable, long nowTicks,
            long lastRequestTicks, int cooldownSeconds)
        {
            if (!enabled || !roomChatFocus) return false;
            if (!inWaitingRoom) return false;
            if (!otherPlayer) return false;
            if (replayOrWatcher) return false;
            if (!channelAvailable) return false;
            return CooldownElapsed(nowTicks, lastRequestTicks, cooldownSeconds);
        }

        /// <summary>
        /// 匹配成功(MatchSuccessS2C)时的置前判定: 开关打开 / 不在回放或观战 /
        /// 加载器通道可用 / 冷却已过。匹配成功本身就是要提醒的事件, 不依赖房间状态,
        /// 因为这条通知到达时游戏才刚创建房间。
        /// </summary>
        internal static bool ShouldRequestMatchSuccess(bool enabled, bool matchSuccessFocus,
            bool replayOrWatcher, bool channelAvailable, long nowTicks, long lastRequestTicks,
            int cooldownSeconds)
        {
            if (!enabled || !matchSuccessFocus) return false;
            if (replayOrWatcher) return false;
            if (!channelAvailable) return false;
            return CooldownElapsed(nowTicks, lastRequestTicks, cooldownSeconds);
        }
    }
}
