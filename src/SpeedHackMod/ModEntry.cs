using System;
using System.Globalization;
using CesiumLoader.SDK;
using UnityEngine;

namespace SpeedHackMod
{
    /// <summary>
    /// 入口。原生加载器调用 <c>SpeedHackMod.ModEntry.Main()</c>(boot 线程, 非主线程)。
    /// </summary>
    public static class ModEntry
    {
        /// <summary>
        /// 启动延迟。其它 mod 默认等 30 秒是为了避开"游戏启动早期访问 NetManager/UIManager
        /// 触发 0x80000003 崩溃"的窗口; 本 mod 只碰加载器导出(ap_speed_*)和 Unity 的 Input,
        /// 不碰任何游戏单例, 所以可以早一点接管热键(加载/开场动画阶段就能变速)。
        /// </summary>
        private const int StartupDelayMs = 10000;

        public static void Main()
        {
            // 变速引擎由加载器在进程启动时安装(inline hook 4 个时间 API);
            // 没有它就什么都不做 —— 装了 mod 也不会误报能变速。
            if (!SpeedHack.IsAvailable)
            {
                SdkLog.Warn("SpeedHack", "变速引擎不可用(加载器未安装 hook), 变速热键不启用");
                return;
            }

            ModBase.Run(new SpeedHackController(), StartupDelayMs);
        }
    }

    /// <summary>
    /// 变速热键。把加载器的变速引擎(SDK 的 <see cref="SpeedHack"/>)接到键盘上:
    ///
    /// <code>
    ///   Delete               开关变速(开 = 上次的倍率, 关 = 回到 1.0x)
    ///   Alt + =(小键盘 +)    加速, 每次 speedStep; 按住不放会连续加速
    ///   Alt + -(小键盘 -)    减速, 同上
    /// </code>
    ///
    /// 几个刻意的设计:
    ///  - <b>只读键盘</b>: 不接管鼠标、不用输入独占, 也不拦截游戏自己的按键 —— 不会影响正常游玩;
    ///  - <b>立即生效</b>: 调完马上调 <see cref="SpeedHack.SetSpeed"/>, 不用重启游戏;
    ///  - <b>沿用加载器的基准倍率</b>: 启动时若引擎已是 2.0x(即 doorstop_config.json 的
    ///    <c>speedhackBaseSpeed</c> = 2.0), 那 mod 一开始就是"开着 2.0x"的状态, 按 Delete 才回 1.0x;
    ///    引擎为 1.0x 时, 则用 config.json 里记住的倍率(默认 2.0x), 但<b>不会</b>主动改倍率;
    ///  - <b>关着的时候按 Alt+/- 直接开</b>: 从当前真实倍率(1.0x)起算, 免得"想减速反而变快";
    ///  - 调过的倍率写回 config.json(停手约 1.2 秒后), 下次启动沿用。
    ///
    /// ⚠️ 变速影响游戏感知的所有时间(动画/演出/回合/网络超时), 联机对局有断线/封号风险,
    /// 详见 docs/mod-SpeedHackMod.md。
    /// </summary>
    public sealed class SpeedHackController : ModBase
    {
        // =====================================================================
        // 配置
        // =====================================================================
        private ModConfig _config;
        private double _activeSpeed = 2.0;                        // 开启时使用的倍率
        private double _speedStep = SpeedHack.DefaultSpeedStep;   // 每次调整的倍率
        private double _minSpeed = SpeedHack.MinSpeed;
        private double _maxSpeed = SpeedHack.MaxSpeed;
        private KeyCode _toggleKey = KeyCode.Delete;
        private KeyCode _speedUpKey = KeyCode.Equals;             // 需按住 Alt
        private KeyCode _speedDownKey = KeyCode.Minus;            // 需按住 Alt
        private bool _useNumpadKeys = true;                       // 小键盘 +/- 也认
        private bool _notify = true;                              // 屏幕上弹提示
        private bool _rememberSpeed = true;                       // 写回 config.json
        private float _repeatInterval = 0.15f;                    // 按住连调的间隔(秒); 0 = 只调一次

        private const float SaveDelaySec = 1.2f;                  // 停手多久后落盘

        // =====================================================================
        // 运行态
        // =====================================================================
        private bool _on;                  // 当前是否处于变速状态(引擎倍率 != 1.0)
        private bool _holding;             // 上一帧是否按着 Alt+加/减键(用于"按下立即调一次")
        private float _nextRepeatAt;       // 连续调整的下一次时刻
        private bool _speedDirty;          // 倍率改过, 待写回配置
        private float _saveAt;             // 可以落盘的时刻

        public override string Version { get { return "2.1.3"; } }

        // =====================================================================
        // 生命周期
        // =====================================================================

        public override void OnInitialize()
        {
            _config = Config;
            LoadConfig();

            double current = SpeedHack.Speed;
            if (current > 0.0 && Math.Abs(current - 1.0) > 0.0001)
            {
                // 加载器已经按 doorstop 的 speedhackBaseSpeed 设过倍率: 以它为准, 别覆盖用户的设置
                _on = true;
                _activeSpeed = SpeedHack.ClampSpeed(current, _minSpeed, _maxSpeed);
            }
            else
            {
                // 引擎是 1.0x: 记着配置里的倍率, 但不主动变速(按 Delete 或 Alt+/- 才生效)
                _on = false;
                _activeSpeed = SpeedHack.ClampSpeed(_activeSpeed, _minSpeed, _maxSpeed);
            }

            Log.Info("变速热键已就绪: " + Describe());
            Log.Info("操作: " + _toggleKey + " 开关; 按住 Alt + " + _speedUpKey + " 加速, Alt + " + _speedDownKey +
                     " 减速 (每次 " + F(_speedStep) + "x, 范围 " + F(_minSpeed) + "~" + F(_maxSpeed) + "x" +
                     (_rememberSpeed ? ", 停手 " + F(SaveDelaySec) + "s 后写回配置" : "") + ")");
            Log.Info("配置键: speed(开启时倍率)/speedStep/minSpeed/maxSpeed/toggleKey/speedUpKey/speedDownKey/" +
                     "useNumpadKeys/rememberSpeed/repeatInterval/notify");
            Log.Warn("注意: 变速会改变游戏感知的所有时间(动画/演出/回合/网络超时); 联机对局使用有断线/封号风险");
        }

        public override void OnUpdate()
        {
            try
            {
                HandleHotkeys();
                FlushSaveIfDue();
            }
            catch (Exception e) { Log.ReportCrash("OnUpdate", e); }
        }

        public override void OnUnload()
        {
            // 退出/卸载: 把还没落盘的倍率补写一次(不改倍率 —— 进程都要结束了, 别再动引擎)
            FlushSaveNow();
            Log.Info("变速已卸载");
        }

        // =====================================================================
        // 热键
        // =====================================================================

        private void HandleHotkeys()
        {
            if (InputService.IsKeyPressed(_toggleKey)) Toggle();
            if (!InputService.IsAvailable) return;

            // Alt 是"调整倍率"的修饰键: 和 FreeCameraMod 的 Ctrl+=/Ctrl+- 一样只读键盘
            bool alt = InputService.IsKeyHeld(KeyCode.LeftAlt) || InputService.IsKeyHeld(KeyCode.RightAlt);

            int dir = 0;
            if (alt)
            {
                bool up = InputService.IsKeyHeld(_speedUpKey) ||
                          (_useNumpadKeys && InputService.IsKeyHeld(KeyCode.KeypadPlus));
                bool down = InputService.IsKeyHeld(_speedDownKey) ||
                            (_useNumpadKeys && InputService.IsKeyHeld(KeyCode.KeypadMinus));

                if (up != down) dir = up ? 1 : -1;   // 同时按住 = 忽略, 免得来回横跳
            }

            if (dir == 0)
            {
                _holding = false;
                return;
            }

            float now = UnityTime.RealtimeSinceStartup;
            if (_holding && _repeatInterval > 0f && now < _nextRepeatAt) return;   // 连调节流

            _holding = true;
            _nextRepeatAt = now + (_repeatInterval > 0f ? _repeatInterval : 0.5f);
            Adjust(dir);
        }

        /// <summary>按一次热键调整倍率, 下一帧即生效。</summary>
        private void Adjust(int dir)
        {
            // 关着的时候按 Alt+/-: 从"当前真实倍率"(1.0)起算, 否则"减速"会变成加速
            double baseSpeed = _on ? _activeSpeed : 1.0;
            double next = SpeedHack.StepSpeed(baseSpeed, dir, _speedStep, _minSpeed, _maxSpeed);

            if (Math.Abs(next - baseSpeed) < 0.0005)
            {
                // 已经顶到上下限: 只提示, 不改状态也不写盘
                Notify("变速: 已是极限 " + F(baseSpeed) + "x", UiNotificationLevel.Info);
                return;
            }

            _activeSpeed = next;
            MarkSpeedDirty();

            if (_on)
            {
                if (!Apply(next)) return;
                Log.Info("倍率 " + F(baseSpeed) + "x -> " + F(next) + "x");
                Notify("变速: " + F(next) + "x", UiNotificationLevel.Info);
                return;
            }

            // 原本关着: 视为"想变速", 直接开启(等价于按了一次 Delete)
            if (!Apply(next)) return;
            _on = true;
            Log.Info("变速已开启 (Alt 调整): " + F(next) + "x");
            Notify("变速: 开启 " + F(next) + "x", UiNotificationLevel.Success);
        }

        /// <summary>Delete: 在"上次的倍率"和 1.0x 之间切换。</summary>
        private void Toggle()
        {
            if (_on)
            {
                if (!Apply(1.0)) return;
                _on = false;
                Log.Info("变速已关闭 (1.0x)");
                Notify("变速: 关闭 (1.0x)", UiNotificationLevel.Info);
                return;
            }

            double target = SpeedHack.ClampSpeed(_activeSpeed, _minSpeed, _maxSpeed);
            if (!Apply(target)) return;
            _activeSpeed = target;
            _on = true;
            MarkSpeedDirty();
            Log.Info("变速已开启: " + F(target) + "x");
            Notify("变速: 开启 " + F(target) + "x", UiNotificationLevel.Success);
        }

        /// <summary>真正写引擎倍率(失败只提示, 不抛)。</summary>
        private bool Apply(double speed)
        {
            if (SpeedHack.SetSpeed(speed)) return true;

            Log.Warn("设置倍率 " + F(speed) + "x 失败(引擎不可用 / 倍率非法 / 无 SpeedHack 权限)");
            Notify("变速: 设置失败(" + F(speed) + "x)", UiNotificationLevel.Warning);
            return false;
        }

        // =====================================================================
        // 配置读写
        // =====================================================================

        private void LoadConfig()
        {
            if (_config == null) return;

            _activeSpeed = _config.GetDouble("speed", _activeSpeed);
            _speedStep = _config.GetDouble("speedStep", _speedStep);
            _minSpeed = _config.GetDouble("minSpeed", _minSpeed);
            _maxSpeed = _config.GetDouble("maxSpeed", _maxSpeed);
            _toggleKey = ParseKey(_config.GetString("toggleKey", null), _toggleKey);
            _speedUpKey = ParseKey(_config.GetString("speedUpKey", null), _speedUpKey);
            _speedDownKey = ParseKey(_config.GetString("speedDownKey", null), _speedDownKey);
            _useNumpadKeys = _config.GetBool("useNumpadKeys", _useNumpadKeys);
            _notify = _config.GetBool("notify", _notify);
            _rememberSpeed = _config.GetBool("rememberSpeed", _rememberSpeed);
            _repeatInterval = _config.GetFloat("repeatInterval", _repeatInterval);

            // 首次运行: 把默认值整份写出来, 省得用户去翻文档猜键名
            if (!_config.Has("speed") || !_config.Has("toggleKey"))
            {
                SaveConfig();
                Log.Info("已生成默认配置: " + (_config.Path ?? "(未知路径)"));
            }
        }

        private void SaveConfig()
        {
            if (_config == null) return;
            try
            {
                _config.Set("speed", _activeSpeed);
                _config.Set("speedStep", _speedStep);
                _config.Set("minSpeed", _minSpeed);
                _config.Set("maxSpeed", _maxSpeed);
                _config.Set("toggleKey", _toggleKey.ToString());
                _config.Set("speedUpKey", _speedUpKey.ToString());
                _config.Set("speedDownKey", _speedDownKey.ToString());
                _config.Set("useNumpadKeys", _useNumpadKeys);
                _config.Set("notify", _notify);
                _config.Set("rememberSpeed", _rememberSpeed);
                _config.Set("repeatInterval", _repeatInterval);
                _config.SaveIfDirty();
            }
            catch (Exception e) { Log.ReportCrash("SaveConfig", e); }
        }

        private void MarkSpeedDirty()
        {
            if (!_rememberSpeed) return;
            _speedDirty = true;
            _saveAt = UnityTime.RealtimeSinceStartup + SaveDelaySec;
        }

        private void FlushSaveIfDue()
        {
            if (!_speedDirty) return;
            if (UnityTime.RealtimeSinceStartup < _saveAt) return;
            FlushSaveNow();
        }

        /// <summary>立刻把待写的倍率落盘(按住连调时只会在这里写一次)。</summary>
        private void FlushSaveNow()
        {
            if (!_speedDirty) return;
            _speedDirty = false;
            SaveConfig();
            Log.Info("倍率 " + F(_activeSpeed) + "x 已写回配置" +
                     (_config != null && !string.IsNullOrEmpty(_config.Path) ? ": " + _config.Path : ""));
        }

        private static KeyCode ParseKey(string name, KeyCode fallback)
        {
            if (string.IsNullOrEmpty(name)) return fallback;
            try
            {
                if (Enum.TryParse(name.Trim(), true, out KeyCode key) && key != KeyCode.None) return key;
            }
            catch { }
            SdkLog.Warn("SpeedHack", "配置里的按键名无效: " + name + " (用 " + fallback + " 代替)");
            return fallback;
        }

        // =====================================================================
        // 工具
        // =====================================================================

        private string Describe()
        {
            return (_on ? "已开启 " : "未开启 ") + F(_on ? _activeSpeed : 1.0) + "x" +
                   " (开启时倍率 " + F(_activeSpeed) + "x, 引擎当前 " + F(SpeedHack.Speed) + "x)";
        }

        private void Notify(string text, UiNotificationLevel level)
        {
            if (!_notify) return;
            try { UiService.Notify(text, level, 2f, Context); }
            catch (Exception e) { Log.ReportCrash("Notify", e); }
        }

        private static string F(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
