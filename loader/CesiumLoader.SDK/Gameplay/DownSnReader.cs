using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace CesiumLoader.SDK.Gameplay
{
    /// <summary>已确认的协议命令号, 配合 <see cref="GameEvents.DownSn(int)"/> 使用。</summary>
    public static class GameCommands
    {
        /// <summary>1002 动作广播(<c>PredictActionS2C</c>)。</summary>
        public const int PredictAction = 1002;
        /// <summary>1040 属性更新(<c>UpdateHeroAttrS2C</c>, 血量与筹码刷新次数都在这里)。</summary>
        public const int UpdateHeroAttr = 1040;
        /// <summary>1077 击杀消息(<c>KillMessageS2C</c>)。</summary>
        public const int KillMessage = 1077;
        /// <summary>1112 筹码同步(<c>SyncRelicsS2C</c>)。</summary>
        public const int SyncRelics = 1112;
        /// <summary>5030 PVP 商店成交(<c>ShopBuyS2C</c>)。</summary>
        public const int ShopBuy = 5030;
        /// <summary>5212 筹码结果(<c>SelectRelicS2C</c>)。</summary>
        public const int SelectRelic = 5212;
        /// <summary>5216 PVE 商店成交(<c>PVEShopBuyS2C</c>)。</summary>
        public const int PveShopBuy = 5216;
    }

    /// <summary>
    /// 读取游戏待处理下行消息队列, 拿到"当前这条结果消息对应的下行序号"。
    ///
    /// 候选动作带的是 <c>action.Sn</c>, 而结果消息(筹码结果 / 商店成交)本身不带
    /// "这是对哪次候选的回答", 只有队列里的下行序号能把两边对上。
    ///
    /// 约束:
    /// <list type="bullet">
    /// <item>只能在对应 RPC 的回调<b>同步执行期间</b>调用 —— 游戏正是把当前消息放在队首、
    ///       还没标记消费的时候调用回调的; 事件处理器里直接调即可。</item>
    /// <item>队列只对"排队处理"的消息有意义。1001 / 5004 / 5008 / 5012 / 5096 / 5150 /
    ///       5238 / 5246 / 5328 这几条由游戏立即处理、不进队列, 读不到序号(返回 0)。</item>
    /// <item>任何异常、字段缺失、队首命令不匹配、消息已消费, 一律返回 0(保守失败),
    ///       绝不把反射异常抛进网络回调链。</item>
    /// </list>
    ///
    /// 内部字段名在多个游戏版本间不保证一致, 因此按候选名查找并缓存;
    /// 读队列时使用游戏自己的锁对象, 与网络线程互斥。
    /// </summary>
    internal static class DownSnReader
    {
        private sealed class Shape
        {
            public MemberInfo Queue;
            public MemberInfo Gate;
            public MemberInfo Command;
            public MemberInfo DownSn;
            public MemberInfo Frame;
            public MemberInfo IsConsumed;
        }

        private static readonly object _gate = new object();
        private static readonly Dictionary<Type, Shape> _shapes = new Dictionary<Type, Shape>();

        internal static long Read(object rpc, int command)
        {
            if (rpc == null || command <= 0) return 0;
            try
            {
                var rpcShape = ShapeOf(rpc.GetType());
                if (rpcShape.Queue == null || rpcShape.Gate == null) return 0;

                object gate = Value(rpc, rpcShape.Gate);
                if (gate == null) return 0;

                // 网络线程也在写这个 List, 必须用游戏自己的锁。
                lock (gate)
                {
                    var queue = Value(rpc, rpcShape.Queue) as IList;
                    if (queue == null || queue.Count == 0) return 0;

                    object head = queue[0];
                    if (head == null) return 0;

                    var msgShape = ShapeOf(head.GetType());
                    object frame = Value(head, msgShape.Frame);
                    var frameShape = frame != null ? ShapeOf(frame.GetType()) : null;

                    object consumedObj = Value(head, msgShape.IsConsumed);
                    if (consumedObj == null && frameShape != null) consumedObj = Value(frame, frameShape.IsConsumed);
                    if (consumedObj is bool && (bool)consumedObj) return 0;

                    object commandObj = Value(head, msgShape.Command);
                    if (commandObj == null && frameShape != null) commandObj = Value(frame, frameShape.Command);
                    if (ToLong(commandObj) != command) return 0;

                    object downSnObj = Value(head, msgShape.DownSn);
                    if (downSnObj == null && frameShape != null) downSnObj = Value(frame, frameShape.DownSn);
                    return ToLong(downSnObj);
                }
            }
            catch { return 0; }
        }

        private static Shape ShapeOf(Type type)
        {
            lock (_gate)
            {
                Shape shape;
                if (_shapes.TryGetValue(type, out shape)) return shape;
                shape = new Shape
                {
                    Queue = FindMember(type, "_OnPushRPCMsg", "QueuedMessages", "_queuedMessages"),
                    Gate = FindMember(type, "_lockObj", "MessageLock", "_messageLock"),
                    Command = FindMember(type, "CMDID", "Command", "_command", "CmdId"),
                    DownSn = FindMember(type, "DOWNSN", "DownSn", "DownSN", "_downSn"),
                    Frame = FindMember(type, "frame", "Frame", "_frame"),
                    IsConsumed = FindMember(type, "IsConsumed", "_isConsumed"),
                };
                _shapes[type] = shape;
                return shape;
            }
        }

        private static MemberInfo FindMember(Type type, params string[] names)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (int i = 0; i < names.Length; i++)
            {
                try
                {
                    var p = type.GetProperty(names[i], flags);
                    if (p != null && p.CanRead && p.GetIndexParameters().Length == 0) return p;
                }
                catch { }
            }
            for (int i = 0; i < names.Length; i++)
            {
                try
                {
                    var f = type.GetField(names[i], flags);
                    if (f != null) return f;
                }
                catch { }
            }
            return null;
        }

        private static object Value(object target, MemberInfo member)
        {
            if (target == null || member == null) return null;
            try
            {
                var p = member as PropertyInfo;
                if (p != null) return p.GetValue(target, null);
                var f = member as FieldInfo;
                if (f != null) return f.GetValue(target);
            }
            catch { }
            return null;
        }

        private static long ToLong(object value)
        {
            if (value == null) return 0;
            try { return Convert.ToInt64(value); } catch { return 0; }
        }
    }
}
