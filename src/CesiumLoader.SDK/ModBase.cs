using System;
using Cysharp.Threading.Tasks;

namespace CesiumLoader.SDK
{
    /// <summary>
    /// mod 生命周期基座。
    /// 关键: 启动后先等 delayMs(默认 30 秒)再碰游戏单例(游戏启动早期访问
    /// NetManager/UIManager 等会触发 0x80000003 崩溃)。
    ///
    /// 事件驱动: 事件订阅用 GameEvents(内部自动维持挂钩, 无需轮询);
    /// tick 参数可选 —— 只在确实需要周期轮询(如 UI 状态)时才传, 不传则不空转。
    /// </summary>
    public static class ModBase
    {
        /// <summary>
        /// 启动 mod: 等 delayMs 后执行 init。若传了 tick, 之后每秒执行一次;
        /// 否则 init 完成后结束(事件驱动模式, 无轮询开销)。
        /// </summary>
        /// <param name="init">初始化(订阅事件/读配置)。可为 null。</param>
        /// <param name="tick">可选每秒轮询。null = 纯事件驱动, 无轮询。</param>
        /// <param name="delayMs">启动延迟, 默认 30 秒(避开游戏启动崩溃窗口)。</param>
        /// <param name="tag">日志来源标签。</param>
        public static void Run(Action init, Action tick = null, int delayMs = 30000, string tag = "MOD")
        {
            RunAsync(init, tick, delayMs, tag).Forget();
        }

        private static async UniTaskVoid RunAsync(Action init, Action tick, int delayMs, string tag)
        {
            SdkLog.Write(tag, "=== Main 被调用 ===");
            try
            {
                // 关键: 等待游戏完全启动再碰单例, 避免启动早期崩溃
                await UniTask.Delay(delayMs);
                SdkLog.Write(tag, "启动延迟结束, 开始初始化");
                try { init?.Invoke(); }
                catch (Exception e) { SdkLog.Write(tag, "init 异常: " + e); }

                // 事件驱动: 无 tick = init 完成后结束, 不空转
                if (tick == null) return;

                while (true)
                {
                    try { tick(); }
                    catch (Exception e) { SdkLog.Write(tag, "tick 异常: " + e.Message); }
                    await UniTask.Delay(1000);
                }
            }
            catch (Exception e)
            {
                SdkLog.Write(tag, "运行循环异常: " + e);
            }
        }
    }
}
