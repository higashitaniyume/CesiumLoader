using System;
using Core.Net;
using Cysharp.Threading.Tasks;
using GameLogic;
using party.protocol;
using Tools;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.Runtime;

namespace CesiumLoader.SDK.Gameplay
{
    /// <summary>
    /// 本地玩家预测动作的只读快照。
    /// 数据会在创建时复制，订阅者无法通过此对象修改服务端原始消息。
    /// </summary>
    public sealed class PredictActionRequest
    {
        private readonly byte[] _data;

        internal PredictActionRequest(int actionId, long playerId, long sn, byte[] data)
        {
            ActionId = actionId;
            PlayerId = playerId;
            Sn = sn;
            _data = data == null ? new byte[0] : (byte[])data.Clone();
        }

        /// <summary>游戏预测动作编号。</summary>
        public int ActionId { get; }
        /// <summary>触发动作的玩家 ID。</summary>
        public long PlayerId { get; }
        /// <summary>服务端操作序号，用于避免重复处理。</summary>
        public long Sn { get; }

        /// <summary>
        /// 返回动作的 Protobuf 数据副本。开发者应根据 ActionId 使用对应协议类型进行解码。
        /// </summary>
        public byte[] Data => (byte[])_data.Clone();
    }

    /// <summary>
    /// 【实验性】预测动作的通用前置处理扩展点，并非能够拦截任意 C# 方法的 Hook。
    ///
    /// 订阅者返回 false 时保留原版流程。只有在已经通过原版游戏协议成功发起了该
    /// 动作及 Sn 所需的完整应答后，才能返回 true；SDK 随后标记该 Sn 已完成，
    /// 并继续将未修改的完整 S2C 消息转发给原版回调。
    /// SDK 不解析商品价格，不发送 C2S 请求，也不包含任何具体 Mod 的业务判断。
    ///
    /// 仅有效、本地玩家、需要广播的动作可被处理。订阅者在 Unity 主线程执行，
    /// 必须同步完成判断；Mod 卸载时应取消订阅。没有订阅者时直接执行原版回调。
    /// </summary>
    public static class PredictActionPreDispatch
    {
        /// <summary>
        /// 原版预测动作处理前触发。返回 true 表示该 Sn 已由订阅者完成协议应答；
        /// 未完成应答、仅安排异步操作或判断不明确时必须返回 false。
        /// </summary>
        public static event Func<PredictActionRequest, bool> BeforeDispatch;

        internal static bool HasSubscribers => BeforeDispatch != null;

        internal static bool IsEligible(long sn, bool notBroadcast, bool isLocalPlayer)
        {
            return sn > 0 && !notBroadcast && isLocalPlayer;
        }

        internal static bool DispatchHandlers(
            Func<PredictActionRequest, bool> handlers, PredictActionRequest request)
        {
            if (handlers == null || request == null) return false;
            foreach (Delegate item in handlers.GetInvocationList())
            {
                try
                {
                    // 第一个明确完成该动作的订阅者生效，避免重复处理。
                    if (((Func<PredictActionRequest, bool>)item)(request)) return true;
                }
                catch (Exception e)
                {
                    SdkLog.Warn("PREDICT/PRE", "订阅者异常，继续处理其它订阅者: " + e.Message);
                }
            }
            return false;
        }

        internal static async UniTask ProcessAsync(PredictActionS2C model)
        {
            var handlers = BeforeDispatch;
            if (handlers == null || model?.Actions == null) return;

            foreach (party.model.Action action in model.Actions)
            {
                if (action == null || action.Sn <= 0 || action.NotBro) continue;
                if (!MainThread.IsPumpRunning)
                {
                    SdkLog.Warn("PREDICT/PRE", "主线程泵未就绪，保留原版流程 sn=" + action.Sn);
                    continue;
                }

                try
                {
                    // 在原版 RPC 回调将动作加入队列之前等待前置处理完成。
                    // 使用主线程异步调度，不同步阻塞 Unity 主线程。
                    await MainThread.RunAsync(() => TryDispatchOnMainThread(action, handlers));
                }
                catch (Exception e)
                {
                    SdkLog.Warn("PREDICT/PRE", "调度异常，保留原版流程: " + e.Message);
                }
            }
        }

        private static void TryDispatchOnMainThread(
            party.model.Action action, Func<PredictActionRequest, bool> handlers)
        {
            try
            {
                if (!SimpleSingletonProvider<GameLogicManager>.hasInstance ||
                    !MonoSingletonProvider<NetManager>.hasInstance) return;
                var logic = SimpleSingletonProvider<GameLogicManager>.inst;
                var net = MonoSingletonProvider<NetManager>.inst;
                if (logic?.battle == null || logic.room?.curRoomInfo == null || logic.account == null ||
                    net?.RPC == null || !net.IsConnected || NetManager.IsReturningToLogin)
                    return;

                bool self = logic.account.IsSelf(action.PlayerId);
                if (!IsEligible(action.Sn, action.NotBro, self)) return;
                if (logic.room.curRoomInfo._finishSn.Contains(action.Sn)) return;

                byte[] data = action.Data == null ? new byte[0] : action.Data.ToByteArray();
                var context = new PredictActionRequest(action.Id, action.PlayerId, action.Sn, data);
                if (!DispatchHandlers(handlers, context)) return;

                // 订阅者已承诺发出对应协议应答；SDK 不伪造回复，也不修改其他动作。
                logic.battle.RecordFinishSn(action.Sn);
                SdkLog.Info("PREDICT/PRE", "动作由模组处理: action=" + action.Id +
                    ", sn=" + action.Sn);
            }
            catch (Exception e)
            {
                // 处理失败时不屏蔽原版服务器回调。
                SdkLog.Warn("PREDICT/PRE", "标记完成失败，保留原版流程: " + e.Message);
            }
        }
    }
}
