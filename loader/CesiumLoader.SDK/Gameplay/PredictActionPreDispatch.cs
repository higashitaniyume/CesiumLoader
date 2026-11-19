using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Core.Net;
using Cysharp.Threading.Tasks;
using GameLogic;
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
        /// 每次读取都会分配一个新数组；需要多次使用时请自行缓存。
        /// </summary>
        public byte[] Data => (byte[])_data.Clone();
    }

    /// <summary>
    /// 【实验性】预测动作的通用前置处理扩展点，并非能够拦截任意 C# 方法的 Hook。
    ///
    /// 订阅者返回 false 时保留原版流程。只有在已经通过原版游戏协议成功发起了该
    /// 动作及 Sn 所需的完整应答后，才能返回 true；原版请求方法通常会自行登记 Sn，
    /// SDK 仅在订阅者返回 true 但尚未登记时做一次查重兜底。随后 SDK 将未修改的完整
    /// S2C 消息转发给原版回调。
    /// SDK 不解析商品价格，不发送 C2S 请求，也不包含任何具体 Mod 的业务判断。
    ///
    /// 仅有效、本地玩家、需要广播的动作可被处理。订阅者在 Unity 主线程执行，
    /// 必须同步快速完成判断 —— 回调在主线程上运行，阻塞会直接卡住游戏。
    /// Mod 卸载时应取消订阅。没有订阅者时直接执行原版回调。
    /// </summary>
    public static class PredictActionPreDispatch
    {
        /// <summary>
        /// 单次主线程派发的等待上限(毫秒)。超时或主线程泵不可用时一律保留原版流程，
        /// 绝不因为前置处理而吞掉 1002 消息。
        /// </summary>
        internal const int DispatchTimeoutMs = 1000;

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

            // 把整批动作先收集好，只做一次主线程往返。
            // 逐动作 await 会让每个动作各占一帧，并且因为 1002 走的是串行 RPC 队列，
            // 整条队列(战斗/移动/结算)都会被一起拖慢。
            var requests = new List<PredictActionRequest>();
            foreach (party.model.Action action in model.Actions)
            {
                if (action == null || action.Sn <= 0 || action.NotBro) continue;
                byte[] data = action.Data == null ? new byte[0] : action.Data.ToByteArray();
                requests.Add(new PredictActionRequest(action.Id, action.PlayerId, action.Sn, data));
            }
            if (requests.Count == 0) return;

            if (!MainThread.IsPumpRunning)
            {
                SdkLog.Warn("PREDICT/PRE", "主线程泵未就绪，保留原版流程，动作数=" + requests.Count);
                return;
            }

            try
            {
                bool ok = await RunOnMainThreadAsync(() => DispatchBatch(requests, handlers));
                if (!ok)
                    SdkLog.Warn("PREDICT/PRE", "主线程前置派发超时/失败，保留原版流程，动作数=" + requests.Count);
            }
            catch (Exception e)
            {
                SdkLog.Warn("PREDICT/PRE", "前置处理失败，继续原版动作: " + e.Message);
            }
        }

        /// <summary>投递到主线程并等待，带超时。返回 false 表示未执行，调用方必须走原版流程。</summary>
        private static async UniTask<bool> RunOnMainThreadAsync(Action action)
        {
            Task task = MainThread.RunAsync(action);
            if (task == null) return false;
            if (task.IsCompleted)
            {
                await task;
                return true;
            }

            // Task.WhenAny 的续体不依赖 Unity 主线程泵，泵停摆时也能超时退出，
            // 保证 original 一定会被调用，1002 消息不会被吞掉。
            Task winner = await Task.WhenAny(task, Task.Delay(DispatchTimeoutMs));
            if (winner != task) return false;
            await task;
            return true;
        }

        private static void DispatchBatch(
            List<PredictActionRequest> requests, Func<PredictActionRequest, bool> handlers)
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

                var finishSn = logic.room.curRoomInfo._finishSn;
                foreach (var request in requests)
                {
                    // NotBro 动作已在收集阶段过滤，这里只需校验 Sn 与本地玩家。
                    if (!IsEligible(request.Sn, false, logic.account.IsSelf(request.PlayerId))) continue;
                    if (finishSn.Contains(request.Sn)) continue;
                    if (!DispatchHandlers(handlers, request)) continue;

                    // 原版请求方法(RequestBuyRelicC2S 等)通常已登记 Sn；这里只对
                    // "返回 true 却没有走原版方法" 的订阅者查重兜底，避免列表里堆重复项。
                    if (!finishSn.Contains(request.Sn))
                        logic.battle.RecordFinishSn(request.Sn);
                    SdkLog.Info("PREDICT/PRE", "动作由模组处理: action=" + request.ActionId +
                        ", sn=" + request.Sn);
                }
            }
            catch (Exception e)
            {
                // 处理失败时不屏蔽原版服务器回调。
                SdkLog.Warn("PREDICT/PRE", "标记完成失败，保留原版流程: " + e.Message);
            }
        }
    }
}
