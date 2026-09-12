using System;
using Cysharp.Threading.Tasks;

namespace CesiumLoader.SDK
{
    /// <summary>
    /// mod 生命周期基座。
    /// 关键: 启动后先等 30 秒再碰游戏单例(游戏启动早期访问 NetManager/UIManager 等
    /// 会触发 0x80000003 崩溃), 之后每秒 tick 一次。
    /// </summary>
    public static class ModBase
    {
        /// <summary>
        /// 启动 mod: 等 delayMs 后执行 init, 然后每秒执行 tick。
        /// </summary>
        /// <param name="init">初始化(订阅事件/读配置)。可为 null。</param>
        /// <param name="tick">每秒轮询。可为 null。</param>
        /// <param name="delayMs">启动延迟, 默认 30 秒(避开游戏启动崩溃窗口)。</param>
        /// <param name="tag">日志来源标签。</param>
        public static void Run(Action init, Action tick, int delayMs = 30000, string tag = "MOD")
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

                while (true)
                {
                    try { tick?.Invoke(); }
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
