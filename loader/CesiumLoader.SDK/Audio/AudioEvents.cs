using System;
using Cysharp.Threading.Tasks;
using CesiumLoader.SDK.Logging;

namespace CesiumLoader.SDK.Audio
{
    /// <summary>
    /// 音频事件中枢与挂钩管理。
    /// 
    /// 负责拦截游戏内所有一次性音效、角色语音与 UI 声音（通过 FairyGUI Stage.inst.playSoundCallback），
    /// 并派发给 SDK 订阅者或执行声明式的音频替换规则。
    /// </summary>
    public static class AudioEvents
    {
        private static Action<int> _myWrappedPlaySound;
        private static Action<int> _originalPlaySound;
        private static bool _autoHookStarted;
        private static int _interceptedCount;
        private static int _replacedCount;
        private static int _mutedCount;

        /// <summary>声音播放前触发（允许取消、重定向或指定替换音频）。</summary>
        public static event EventHandler<SoundPlayEventArgs> BeforeSoundPlay;

        /// <summary>声音播放后触发（已拦截/取消的声音不会触发）。</summary>
        public static event Action<int, string> AfterSoundPlay;

        /// <summary>累计拦截的声音次数。</summary>
        public static int InterceptedCount => _interceptedCount;

        /// <summary>累计替换为自定义音频的声音次数。</summary>
        public static int ReplacedCount => _replacedCount;

        /// <summary>累计被静音的声音次数。</summary>
        public static int MutedCount => _mutedCount;

        /// <summary>
        /// 启动内部自动挂钩循环（每 1 秒检查一次）。
        /// 首次订阅事件或显式调用时均会自动启动。
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
            while (true)
            {
                try { EnsureHooked(); } catch { }
                try { await UniTask.Delay(1000); } catch { return; }
            }
        }

        /// <summary>
        /// 检查并安装 Stage.inst.playSoundCallback 的挂钩包装。
        /// 幂等且安全：若 Stage 尚未初始化则静默跳过，待自动循环下一秒重试。
        /// </summary>
        public static void EnsureHooked()
        {
            try
            {
                object stage = GameAudioBridge.GetStageInstance();
                if (stage == null) return;

                var cur = GameAudioBridge.GetStagePlaySoundCallback(stage);
                if (cur == null || ReferenceEquals(cur, _myWrappedPlaySound)) return;

                _originalPlaySound = cur;
                Action<int> wrapped = (eventConfigId) =>
                {
                    try
                    {
                        if (ProcessSoundEvent(eventConfigId, _originalPlaySound))
                        {
                            return; // 已被取消或替换
                        }
                    }
                    catch (Exception ex)
                    {
                        SdkLog.Error("AUDIO", "音频拦截处理异常: " + ex);
                    }

                    _originalPlaySound?.Invoke(eventConfigId);
                };

                if (GameAudioBridge.SetStagePlaySoundCallback(stage, wrapped))
                {
                    _myWrappedPlaySound = wrapped;
                    SdkLog.Write("AUDIO", "[挂钩] Stage.inst.playSoundCallback");
                }
            }
            catch { }
        }

        /// <summary>
        /// 恢复原生回调（用于测试或清理）。
        /// </summary>
        public static void Unhook()
        {
            try
            {
                object stage = GameAudioBridge.GetStageInstance();
                if (stage != null && ReferenceEquals(GameAudioBridge.GetStagePlaySoundCallback(stage), _myWrappedPlaySound))
                {
                    GameAudioBridge.SetStagePlaySoundCallback(stage, _originalPlaySound);
                }
            }
            catch { }
            _myWrappedPlaySound = null;
        }

        /// <summary>
        /// 处理声音事件逻辑。返回 true 表示已完全拦截（不调用原委托）。
        /// </summary>
        internal static bool ProcessSoundEvent(int eventConfigId, Action<int> originalInvoker)
        {
            _interceptedCount++;

            string eventName = ResolveEventName(eventConfigId, out uint wwiseEventId);
            var args = new SoundPlayEventArgs(eventConfigId, eventName, wwiseEventId);

            // 1. 检查 AudioService 中注册的规则
            var rule = AudioService.FindMatchingRule(args);
            if (rule != null)
            {
                if (rule.RedirectConfigId > 0)
                {
                    args.RedirectConfigId = rule.RedirectConfigId;
                }
                else
                {
                    args.Cancel = true;
                    args.CustomClip = rule.ResolveClip(args);
                    args.CustomVolume = rule.Volume;
                    args.Channel = rule.Channel;
                }
            }

            // 2. 派发前置事件
            try
            {
                BeforeSoundPlay?.Invoke(null, args);
            }
            catch (Exception ex)
            {
                SdkLog.Error("AUDIO", "BeforeSoundPlay 事件订阅者抛出异常: " + ex);
            }

            // 3. 处理取消 / 替换
            if (args.Cancel)
            {
                if (args.CustomClip != null)
                {
                    _replacedCount++;
                    AudioService.PlayOneShot(args.CustomClip, args.CustomVolume, args.Channel);
                }
                else
                {
                    _mutedCount++;
                }
                return true;
            }

            // 4. 处理重定向
            if (args.RedirectConfigId.HasValue && args.RedirectConfigId.Value != eventConfigId)
            {
                originalInvoker?.Invoke(args.RedirectConfigId.Value);
                TriggerAfterSoundPlay(args.RedirectConfigId.Value);
                return true;
            }

            // 5. 允许原版播放
            TriggerAfterSoundPlay(eventConfigId, eventName);
            return false;
        }

        private static void TriggerAfterSoundPlay(int configId, string name = null)
        {
            try
            {
                if (name == null) name = ResolveEventName(configId, out _);
                AfterSoundPlay?.Invoke(configId, name);
            }
            catch { }
        }

        private static string ResolveEventName(int eventConfigId, out uint wwiseId)
        {
            if (GameAudioBridge.TryGetAudioEventInfo(eventConfigId, out string eventName, out wwiseId))
            {
                return eventName;
            }
            wwiseId = 0;
            return string.Empty;
        }

        /// <summary>
        /// 单元测试专用入口：在脱离真实 Stage.inst 环境下模拟声音调度。
        /// </summary>
        internal static bool DispatchSoundForTesting(int eventConfigId, Action<int> originalInvoker)
        {
            if (ProcessSoundEvent(eventConfigId, originalInvoker))
            {
                return true;
            }
            originalInvoker?.Invoke(eventConfigId);
            return false;
        }

        /// <summary>
        /// 单元测试重置计数。
        /// </summary>
        internal static void ResetStatsForTesting()
        {
            _interceptedCount = 0;
            _replacedCount = 0;
            _mutedCount = 0;
        }
    }
}
