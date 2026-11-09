using CesiumLoader.SDK.Mods;
using GameLogic;
using Tools;

namespace TurnClockMod
{
    public sealed partial class TurnClock : ModBase
    {
        // =====================================================================
        // 阶段闸门: 选人结束 → 进入地图
        //
        // 选人确认后房间会先切到 RUNNING(开始预加载资源、切战斗场景), 再过几秒
        // BattleLogic.BattleStart 末尾才把 clientFinishReady 置 true。这一段画面上没有
        // 可操作的倒计时, 本 mod 完全不做收集/补偿/日志, 避免和场景加载抢主线程。
        //
        // 只认这个组合, 不看 "是否在战斗场景": 选人界面和房间等待也发生在非战斗场景,
        // 它们的倒计时仍然需要补偿。
        // =====================================================================

        private bool _suspended;

        /// <summary>是否正处在"选人结束 → 战斗场景/UI 就绪"的加载地图阶段。</summary>
        private static bool InLoadTransition()
        {
            try
            {
                if (!SimpleSingletonProvider<GameLogicManager>.hasInstance) return false;

                GameLogicManager logic = SimpleSingletonProvider<GameLogicManager>.inst;
                if (logic == null) return false;

                RoomLogic room = logic.room;
                if (room == null || room.roomController == null) return false;
                if (room.roomController.roomStateType != RoomStateType.RUNNING) return false;

                BattleLogic battle = logic.battle;
                return battle == null || !battle.clientFinishReady;
            }
            catch
            {
                // 读不到状态就当作不在加载, 宁可少跳过也不要把功能永久停掉
                return false;
            }
        }

        /// <summary>进入加载阶段: 收掉窗口统计与补偿基准, 回来时按新计时器从头开始。</summary>
        private void SuspendForLoading(double now)
        {
            if (_suspended) return;
            _suspended = true;

            if (_windowOpen) CloseWindow(now);
            _compensator.Clear();
            _buffer.Clear();
            _ticked.Clear();
            ResetExtraClocks();
            _autoPicker.Reset();
            _pickerGaveUp = false;
            _nativeSpeedSampler.RequestRefresh();
            _hasTimers = false;
        }

        /// <summary>离开加载阶段。</summary>
        private void ResumeFromLoading()
        {
            _suspended = false;
        }
    }
}
