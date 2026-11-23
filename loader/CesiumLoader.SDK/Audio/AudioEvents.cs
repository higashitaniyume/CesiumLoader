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
    ///
    /// <para>
    /// 背景音乐（BGM）不在上述回调里：游戏的 BGM 由
    /// <c>Core.Audio.BGMHelper.TryPlayBGM</c> → <c>AudioManager.PlayBGM</c> → <c>BGMModule.PlayBGM</c> →
    /// <c>AkSoundEngine.PostEvent</c> 直接播放，没有任何可以替换的委托字段。因此这里额外维护一个
    /// BGM 监视循环：按固定间隔询问游戏自身「哪首 BGM 在播」，识别到换曲/停止时按同一套替换规则
    /// 决定静音还是用自定义音乐覆盖（见 <see cref="BeforeBgmPlay"/>）。
    /// </para>
    /// </summary>
    public static class AudioEvents
    {
        /// <summary>BGM 状态检查间隔(毫秒)。</summary>
        private const int BgmWatchIntervalMs = 250;

        /// <summary>每隔多少个监视 tick 重新确认一次 Stage 音效挂钩(4 × 250ms = 1s，与原节奏一致)。</summary>
        private const int StageHookEveryTicks = 4;

        /// <summary>连续多少个 tick 没看到任何 BGM 播放，才认为「BGM 已停止」（避免换曲瞬间的抖动）。</summary>
        private const int BgmMissingTicksBeforeRelease = 2;

        private static Action<int> _myWrappedPlaySound;
        private static Action<int> _originalPlaySound;
        private static bool _autoHookStarted;
        private static int _interceptedCount;
        private static int _replacedCount;
        private static int _mutedCount;

        // ---- BGM 监视状态 ----

        private static EventHandler<BgmPlayEventArgs> _beforeBgmPlay;
        private static int _bgmAppliedEventConfigId;
        private static bool _bgmAppliedSuppress;
        private static volatile bool _bgmForceReevaluate;
        private static int _bgmMissingTicks;
        private static int _currentBgmEventConfigId;
        private static string _currentBgmEventName = string.Empty;
        private static int _bgmInterceptedCount;
        private static int _bgmReplacedCount;
        private static int _bgmMutedCount;

        private static EventHandler<SoundPlayEventArgs> _beforeSoundPlay;
        /// <summary>声音播放前触发（允许取消、重定向或指定替换音频）。</summary>
        public static event EventHandler<SoundPlayEventArgs> BeforeSoundPlay
        {
            add { _beforeSoundPlay += value; StartAutoHook(); }
            remove { _beforeSoundPlay -= value; }
        }

        private static Action<int, string> _afterSoundPlay;
        /// <summary>声音播放后触发（已拦截/取消的声音不会触发）。</summary>
        public static event Action<int, string> AfterSoundPlay
        {
            add { _afterSoundPlay += value; StartAutoHook(); }
            remove { _afterSoundPlay -= value; }
        }

        /// <summary>
        /// 背景音乐（BGM）切换前触发（换曲、恢复上一首时）。
        ///
        /// 参数里的 <see cref="BgmPlayEventArgs.EventConfigId"/> 与该 BGM 在游戏 `Audio.Event`
        /// 配表中的播放事件 ID 一致（例如 10011 = BGM_Login），可以用它与音效同样的方式匹配。
        /// 订阅后 SDK 会自动开始监视 BGM 状态；除订阅之外，注册到
        /// <see cref="AudioService.RegisterReplacement(AudioReplacementRule)"/> 的规则也会自动生效。
        /// </summary>
        public static event EventHandler<BgmPlayEventArgs> BeforeBgmPlay
        {
            add { _beforeBgmPlay += value; StartAutoHook(); }
            remove { _beforeBgmPlay -= value; }
        }

        /// <summary>累计拦截的声音次数。</summary>
        public static int InterceptedCount => _interceptedCount;

        /// <summary>累计替换为自定义音频的声音次数。</summary>
        public static int ReplacedCount => _replacedCount;

        /// <summary>累计被静音的声音次数。</summary>
        public static int MutedCount => _mutedCount;

        /// <summary>累计识别到的 BGM 播放次数（含未匹配任何规则的原版曲目）。</summary>
        public static int BgmInterceptedCount => _bgmInterceptedCount;

        /// <summary>累计被自定义音乐替换的 BGM 次数。</summary>
        public static int BgmReplacedCount => _bgmReplacedCount;

        /// <summary>累计被静音的 BGM 次数。</summary>
        public static int BgmMutedCount => _bgmMutedCount;

        /// <summary>当前正在播放的 BGM 的 `Audio.Event` 配置 ID；无法识别时为 0。</summary>
        public static int CurrentBgmEventConfigId => _currentBgmEventConfigId;

        /// <summary>当前正在播放的 BGM 的 Wwise 事件名；无法识别时为空。</summary>
        public static string CurrentBgmEventName => _currentBgmEventName;

        /// <summary>
        /// 该 `Audio.Event` 配置 ID 是否为 BGM 播放事件。
        /// 供 Mod 判断一条规则属于 BGM 还是普通音效（BGM 替换走循环与覆盖逻辑）。
        /// </summary>
        public static bool IsBgmEvent(int eventConfigId)
        {
            return GameAudioBridge.IsBgmPlayEvent(eventConfigId);
        }

        /// <summary>
        /// 该 `Audio.Event` 配置 ID 是否为 BGM 停止事件（如 10012 = BGM_Login_Stop）。
        /// 停止事件本身不播放音乐，为它注册替换规则不会生效，可用于给玩家提示。
        /// </summary>
        public static bool IsBgmStopEvent(int eventConfigId)
        {
            return GameAudioBridge.IsBgmStopEvent(eventConfigId);
        }

        /// <summary>
        /// 启动内部自动挂钩循环（每 250 毫秒检查一次 BGM 状态，每秒确认一次音效挂钩）。
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
            int tick = 0;
            while (true)
            {
                try
                {
                    if (tick % StageHookEveryTicks == 0) { try { EnsureHooked(); } catch { } }
                    try { ProcessBgmWatch(); } catch { }
                    // 覆盖生效期间把原版 BGM 音量持续压回静音（玩家可能刚在设置里改过音量）。
                    try { AudioService.RefreshBgmMute(); } catch { }
                }
                catch { }

                tick++;
                try { await UniTask.Delay(BgmWatchIntervalMs); }
                catch
                {
                    // 循环退出后允许后续再次 StartAutoHook 重启, 否则 BGM 监视会永久失效。
                    _autoHookStarted = false;
                    return;
                }
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

            // 快速路径：若未注册任何规则且无任何事件订阅者，直接放行，避免反射与对象分配
            if (!AudioService.HasRules && _beforeSoundPlay == null && _afterSoundPlay == null && _beforeBgmPlay == null)
            {
                return false;
            }

            // 少数 BGM 播放事件会经 UI 通道派发(AudioManager.SendEvent)。这类事件不能走 PlayOneShot：
            // 既不循环，也不会在换曲/停止时收尾。改由 BGM 覆盖路径处理。
            if (GameAudioBridge.IsBgmPlayEvent(eventConfigId))
            {
                bool bgmSuppressed = false;
                try { bgmSuppressed = HandleBgmEventFromSoundCallback(eventConfigId); }
                catch (Exception ex) { SdkLog.Error("AUDIO", "BGM 事件处理异常: " + ex); }

                if (bgmSuppressed) return true;
                TriggerAfterSoundPlay(eventConfigId);
                return false;
            }

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
                _beforeSoundPlay?.Invoke(null, args);
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

        // =====================================================================
        // 背景音乐（BGM）监视与替换
        // =====================================================================

        /// <summary>
        /// 规则集合发生变化时调用：丢弃「已处理过哪首 BGM」的记忆，让下一次监视重新判定当前曲目。
        /// </summary>
        internal static void NotifyRulesChanged()
        {
            _bgmForceReevaluate = true;
        }

        /// <summary>
        /// BGM 监视 tick。识别当前曲目，并在「换曲 / 规则变化 / 覆盖被清除」时重新判定与应用替换。
        /// </summary>
        internal static void ProcessBgmWatch()
        {
            try
            {
                bool hasRules = AudioService.HasRules;
                bool hasSubscribers = _beforeBgmPlay != null;

                if (!hasRules && !hasSubscribers)
                {
                    // 没有人再关心 BGM：如果之前是本循环接管的，把原版音乐放回来。
                    if (_bgmAppliedSuppress) AudioService.ClearBgmOverride();
                    _bgmAppliedEventConfigId = 0;
                    _bgmAppliedSuppress = false;
                    _bgmMissingTicks = 0;
                    return;
                }

                bool force = _bgmForceReevaluate;
                _bgmForceReevaluate = false;

                if (!GameAudioBridge.TryGetPlayingBgmBank(_bgmAppliedEventConfigId, out var current))
                {
                    _bgmMissingTicks++;
                    if (_bgmMissingTicks < BgmMissingTicksBeforeRelease) return;

                    if (_bgmAppliedSuppress) AudioService.ClearBgmOverride();
                    _bgmAppliedEventConfigId = 0;
                    _bgmAppliedSuppress = false;
                    _currentBgmEventConfigId = 0;
                    _currentBgmEventName = string.Empty;
                    return;
                }

                _bgmMissingTicks = 0;

                bool needEvaluate = force
                    || _bgmAppliedEventConfigId != current.PlayEventConfigId
                    || (_bgmAppliedSuppress && !AudioService.IsBgmOverrideActive);

                if (!needEvaluate) return;

                EvaluateAndApplyBgm(current);
            }
            catch (Exception ex)
            {
                SdkLog.Error("AUDIO", "BGM 监视异常: " + ex.Message);
            }
        }

        /// <summary>
        /// 处理经音效回调派发过来的 BGM 播放事件。返回 true 表示已抑制原版音乐。
        /// </summary>
        private static bool HandleBgmEventFromSoundCallback(int eventConfigId)
        {
            if (!GameAudioBridge.TryGetBgmInfoByEventConfigId(eventConfigId, out var info))
            {
                // 配表里找不到 bank 记录时用事件自身信息兜底，至少让规则与事件订阅者能看到。
                info = new BgmBankInfo
                {
                    PlayEventConfigId = eventConfigId,
                    EventName = ResolveEventName(eventConfigId, out uint wwiseId),
                    WwiseEventId = wwiseId
                };
            }

            return EvaluateAndApplyBgm(info);
        }

        /// <summary>
        /// 按替换规则与该曲目的信息判定并应用 BGM 处理，返回是否抑制了原版音乐。
        /// </summary>
        private static bool EvaluateAndApplyBgm(BgmBankInfo info)
        {
            _bgmInterceptedCount++;

            var args = new BgmPlayEventArgs(
                info.PlayEventConfigId,
                info.EventName,
                info.WwiseEventId,
                info.BgmId,
                info.BankId,
                info.StateConfigId,
                info.StateName);

            // 1. 与音效共用同一套替换规则：命中后按 BGM 语义处理（循环、覆盖、随曲目切换收尾）
            var ruleArgs = new SoundPlayEventArgs(info.PlayEventConfigId, info.EventName, info.WwiseEventId);
            var rule = AudioService.FindMatchingRule(ruleArgs);
            if (rule != null)
            {
                if (rule.RedirectConfigId > 0)
                {
                    // 自定义音频不是 Wwise 事件，无法把 BGM 重定向到游戏内的另一条事件。
                    // 这里保持原版播放，而不是静音，避免玩家以为规则生效了却听不到任何音乐。
                    SdkLog.Warn("AUDIO", "BGM 事件 " + info.EventName + "(" + info.PlayEventConfigId +
                        ") 配置为重定向，但 BGM 不支持重定向，已保持原版播放。");
                }
                else
                {
                    args.Cancel = true;
                    args.CustomClip = rule.ResolveClip(ruleArgs);
                    args.CustomVolume = rule.Volume;
                    args.Loop = rule.Loop;
                }
            }

            // 2. 派发前置事件（可再次改写自定义音频或取消静音）
            try
            {
                _beforeBgmPlay?.Invoke(null, args);
            }
            catch (Exception ex)
            {
                SdkLog.Error("AUDIO", "BeforeBgmPlay 事件订阅者抛出异常: " + ex);
            }

            _currentBgmEventConfigId = args.EventConfigId;
            _currentBgmEventName = args.EventName;

            // 3. 应用
            bool suppressed;
            if (args.Cancel)
            {
                if (args.CustomClip != null)
                {
                    _bgmReplacedCount++;
                    AudioService.SetBgmOverride(args.CustomClip, args.Loop, args.CustomVolume);
                }
                else
                {
                    _bgmMutedCount++;
                    AudioService.MuteGameBgm();
                }
                suppressed = true;
            }
            else
            {
                if (_bgmAppliedSuppress) AudioService.ClearBgmOverride();
                suppressed = false;
            }

            _bgmAppliedEventConfigId = args.EventConfigId;
            _bgmAppliedSuppress = suppressed;
            return suppressed;
        }

        private static void TriggerAfterSoundPlay(int configId, string name = null)
        {
            try
            {
                if (name == null) name = ResolveEventName(configId, out _);
                _afterSoundPlay?.Invoke(configId, name);
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
        /// 单元测试专用入口：在不依赖游戏配表的情况下模拟一次 BGM 事件。
        /// </summary>
        internal static bool DispatchBgmForTesting(int eventConfigId, string eventName, uint wwiseEventId = 0, int bgmId = 0, int bankId = 0)
        {
            var info = new BgmBankInfo
            {
                PlayEventConfigId = eventConfigId,
                EventName = eventName ?? string.Empty,
                WwiseEventId = wwiseEventId,
                BgmId = bgmId,
                BankId = bankId
            };
            return EvaluateAndApplyBgm(info);
        }

        /// <summary>
        /// 单元测试重置计数与 BGM 监视状态。
        /// </summary>
        internal static void ResetStatsForTesting()
        {
            _interceptedCount = 0;
            _replacedCount = 0;
            _mutedCount = 0;
            _bgmInterceptedCount = 0;
            _bgmReplacedCount = 0;
            _bgmMutedCount = 0;
            _bgmAppliedEventConfigId = 0;
            _bgmAppliedSuppress = false;
            _bgmForceReevaluate = false;
            _bgmMissingTicks = 0;
            _currentBgmEventConfigId = 0;
            _currentBgmEventName = string.Empty;
        }
    }
}
