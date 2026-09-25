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
            // 变速引擎由加载器在进程启动时安装(inline hook 4 个时间 API), 并与 mod 之间
            // 通过"控制文件通道"通信(request.txt / state.txt)。本游戏的热更程序集无法
            // P/Invoke, 所以走文件(详见 CesiumLoader.SDK 的 SpeedHack 类注释)。
            //
            // 引擎此刻不可用也不放弃: 控制器照常跑, 每秒重试一次, 一旦读到加载器状态文件
            // 就自动就绪。这样"加载器/ mod 初始化先后顺序"再怎么变, 热键都能用上。
            if (!SpeedHack.IsAvailable)
                SdkLog.Warn("SpeedHack", "引擎暂不可用(会每秒重试): " + SpeedHack.UnavailableReason +
                                         " [需要加载器 ≥ 2.1.5 且 speedControlEnabled 不为 false]");

            ModBase.Run(new SpeedHackController(), StartupDelayMs);
        }
    }

    /// <summary>
    /// 变速热键。把加载器的变速引擎(SDK 的 <see cref="SpeedHack"/>)接到键盘上:
    ///
    /// <code>
    ///   Delete               在 1.0x 与"刚才的倍率"之间来回切(引擎保持开启, 不卸载 hook)
    ///   Alt + =(小键盘 +)    加速, 每次 speedStep; 按住不放会连续加速
    ///   Alt + -(小键盘 -)    减速, 同上
    /// </code>
    ///
    /// 几个刻意的设计:
    ///  - <b>只读键盘</b>: 不接管鼠标、不用输入独占, 也不拦截游戏自己的按键 —— 不会影响正常游玩;
    ///  - <b>立即生效</b>: 调完马上调 <see cref="SpeedHack.SetSpeed"/>, 不用重启游戏;
    ///  - <b>Delete 是"1.0x ↔ 刚才的倍率"的开关, 不是"关掉变速"</b>: 两次写入走的代码路径与
    ///    Alt 调整**完全一致**(都是纯粹的一次倍率写入)。加载器的变速引擎一旦装上 hook 就常驻,
    ///    1.0x 时 hook 原样返回真实时间 —— 所以不存在"卸载 hook / 切换引擎状态"这种会引发卡顿的
    ///    额外动作。第一次按: 记下当前真实倍率再设为 1.0x; 再按一次: 切回记下的倍率。
    ///    这个记忆只在本次运行内有效, 且每次**离开非 1.0x** 都会用当时的真实倍率覆盖它 ——
    ///    所以 Alt 调到多少就能切回多少(`toggleRestoresSpeed=false` 可退回老行为: 永远只设为 1.0x);
    ///  - <b>Alt 永远从"引擎当前真实倍率"起算</b>: 所以 Delete 调到 1.0x 后按 Alt+= 得到 1.5x,
    ///    而不是跳到"上次记忆的倍率";
    ///  - 调过的倍率写回 config.json(停手约 1.2 秒后), 下次启动沿用; Delete 的两次切换都不写回
    ///    (1.0x 是"临时恢复正常", 切回的倍率又恰好等于记忆倍率, 都不该覆盖你习惯的倍率)。
    ///
    /// ⚠️ 变速影响游戏感知的所有时间(动画/演出/回合/网络超时), 倍率别调太高(建议 ≤3x),
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
        private bool _toggleRestoresSpeed = true;                 // toggleKey 再按一次 = 切回刚才的倍率
        private float _repeatInterval = 0.15f;                    // 按住连调的间隔(秒); 0 = 只调一次

        private const float SaveDelaySec = 1.2f;                  // 停手多久后落盘

        // =====================================================================
        // 运行态
        // =====================================================================
        private bool _ready;               // 引擎是否已确认可用(不可用时每秒重试)
        private float _nextProbeAt;        // 下次探测引擎的时刻
        private bool _holding;             // 上一帧是否按着 Alt+加/减键(用于"按下立即调一次")
        private float _nextRepeatAt;       // 连续调整的下一次时刻
        private bool _speedDirty;          // 倍率改过, 待写回配置
        private float _saveAt;             // 可以落盘的时刻
        private double _restoreSpeed;      // 被 toggleKey 压到 1.0x 之前的倍率(0 = 本次运行还没记过)

        public override string Version { get { return "2.2.1"; } }

        // =====================================================================
        // 生命周期
        // =====================================================================

        public override void OnInitialize()
        {
            _config = Config;
            LoadConfig();

            if (SpeedHack.IsAvailable) MarkReady();
            else
            {
                // 引擎还没就绪(加载器刚起来 / state.txt 还没写出来): 每秒重试, 不做变速
                _nextProbeAt = UnityTime.RealtimeSinceStartup + 1.0f;
                Log.Warn("变速引擎暂不可用, 每秒重试; 期间热键不起作用");
                Log.Warn("引擎诊断: " + SpeedHack.UnavailableReason);
            }

            // 输入不可用时热键必然没反应 —— 提前写清楚, 省得排查"按了没反应"
            Log.Info("输入后端: " + InputService.BackendName + " (可用=" + InputService.IsAvailable + ")");
            Log.Info("操作: " + _toggleKey + (_toggleRestoresSpeed
                         ? " 在 1.0x 与刚才的倍率之间切换(引擎保持开启)"
                         : " 设为 1.0x(引擎保持开启; toggleRestoresSpeed=false 已关掉回切)") +
                     "; 按住 Alt + " + _speedUpKey + " 加速, Alt + " + _speedDownKey +
                     " 减速 (每次 " + F(_speedStep) + "x, 范围 " + F(_minSpeed) + "~" + F(_maxSpeed) + "x" +
                     (_rememberSpeed ? ", 停手 " + F(SaveDelaySec) + "s 后写回配置" : "") + ")");
            Log.Info("配置键: speed(Alt 调整的记忆倍率)/speedStep/minSpeed/maxSpeed/toggleKey/toggleRestoresSpeed/" +
                     "speedUpKey/speedDownKey/useNumpadKeys/rememberSpeed/repeatInterval/notify");
            Log.Warn("注意: 变速会改变游戏感知的所有时间(动画/演出/回合/网络超时), 倍率别调太高(建议 ≤3x)");
        }

        /// <summary>引擎可用了: 记录当前倍率并打日志。</summary>
        private void MarkReady()
        {
            _ready = true;

            double current = SpeedHack.Speed;
            if (current > 0.0 && Math.Abs(current - 1.0) > 0.0001)
            {
                // 加载器已按 doorstop 的 speedhackBaseSpeed 设过倍率: 以它为准, 并记下来作为
                // "记忆倍率"(用户按 Alt 继续调整时用得上)
                _activeSpeed = SpeedHack.ClampSpeed(current, _minSpeed, _maxSpeed);
            }
            else
            {
                _activeSpeed = SpeedHack.ClampSpeed(_activeSpeed, _minSpeed, _maxSpeed);
            }

            Log.Info("变速热键已就绪: " + Describe());
            Log.Info("引擎: " + SpeedHack.Describe());
            if (_notify) Notify("变速: 热键已就绪 (" + F(SpeedHack.Speed) + "x)", UiNotificationLevel.Success);
        }

        public override void OnUpdate()
        {
            try
            {
                if (!_ready)
                {
                    float now = UnityTime.RealtimeSinceStartup;
                    if (now < _nextProbeAt) return;
                    _nextProbeAt = now + 1.0f;

                    if (!SpeedHack.IsAvailable) return;   // 静默重试, 免得刷屏
                    Log.Info("变速引擎已就绪(重试成功)");
                    MarkReady();
                }

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
            if (InputService.IsKeyPressed(_toggleKey)) ToggleSpeed();
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
            // 永远从"引擎当前真实倍率"起算: 这样 Delete 调到 1.0x 之后, Alt+= 得到的是 1.5x
            // 而不是跳到记忆里的旧倍率。
            double current = CurrentEngineSpeed();
            double next = SpeedHack.StepSpeed(current, dir, _speedStep, _minSpeed, _maxSpeed);

            if (Math.Abs(next - current) < 0.0005)
            {
                // 已经顶到上下限: 只提示, 不改状态也不写盘
                Notify("变速: 已是极限 " + F(current) + "x", UiNotificationLevel.Info);
                return;
            }

            if (!Apply(next)) return;

            _activeSpeed = next;      // 记住用户选的倍率(下次变速沿用)
            MarkSpeedDirty();
            Log.Info("倍率 " + F(current) + "x -> " + F(next) + "x");
            Notify("变速: " + F(next) + "x", UiNotificationLevel.Info);
        }

        /// <summary>
        /// toggleKey(默认 Delete): 在 1.0x 与"刚才的倍率"之间来回切。
        ///
        /// 这里刻意**不是**"关闭变速": 加载器的变速引擎一旦装上 hook 就常驻, 1.0x 时 hook
        /// 原样返回真实时间(等价于不变速)。所以两次切换都是一次普通的倍率写入 ——
        /// 与 Alt 调整走完全相同的代码路径(踩过"按 Delete 卡死、改成 1 倍却正常"的坑)。
        /// 两次也都不写回配置: 1.0x 只是临时恢复正常, 切回的倍率原本就等于记忆倍率。
        ///
        /// 第一次按(当前不是 1.0x): 记下引擎真实倍率 -> 设为 1.0x;
        /// 再按一次(当前是 1.0x): 切回记下的倍率。
        /// 记忆只在本次运行内有效, 并且每次"离开非 1.0x"都会用当时的真实倍率覆盖它 ——
        /// 于是 Alt 调到多少, toggleKey 就能切回多少。
        /// </summary>
        private void ToggleSpeed()
        {
            double current = CurrentEngineSpeed();

            if (Math.Abs(current - 1.0) < 0.0005)
            {
                // 已经在 1.0x: 这一次是"切回刚才的倍率"
                double back = _toggleRestoresSpeed ? UsableRestoreSpeed() : 0.0;
                if (!(back > 1.0))
                {
                    Notify("变速: 已是 1.0x", UiNotificationLevel.Info);
                    return;
                }

                if (!Apply(back)) return;

                Log.Info("倍率 1.0x -> " + F(back) + "x (切回 " + _toggleKey + " 按下前的倍率)");
                Notify("变速: " + F(back) + "x (再按 " + _toggleKey + " 回到 1.0x)", UiNotificationLevel.Info);
                return;
            }

            // 不在 1.0x: 记下这一刻的真实倍率, 再压到 1.0x
            double previous = _restoreSpeed;
            _restoreSpeed = SpeedHack.ClampSpeed(current, _minSpeed, _maxSpeed);

            if (!Apply(1.0))
            {
                _restoreSpeed = previous;   // 写失败就别记下一个没能生效的倍率
                return;
            }

            Log.Info("倍率 " + F(current) + "x -> 1.0x (引擎保持开启, 未卸载 hook" +
                     (_toggleRestoresSpeed ? "; 再按 " + _toggleKey + " 回到 " + F(_restoreSpeed) + "x" : "") + ")");
            Notify(_toggleRestoresSpeed
                       ? "变速: 1.0x (再按 " + _toggleKey + " 回到 " + F(_restoreSpeed) + "x)"
                       : "变速: 1.0x (引擎保持开启)", UiNotificationLevel.Info);
        }

        /// <summary>切回时该用的倍率: 夹到当前范围; 不可用(没记过 / 低于 1.0x)时返回 0。</summary>
        private double UsableRestoreSpeed()
        {
            if (!(_restoreSpeed > 1.0)) return 0.0;
            double clamped = SpeedHack.ClampSpeed(_restoreSpeed, _minSpeed, _maxSpeed);
            return clamped > 1.0 ? clamped : 0.0;
        }

        /// <summary>
        /// 引擎当前真实倍率(读不到就退回记忆值), 并夹到 [minSpeed, maxSpeed]。
        /// Alt 调整与 Delete 都以此为准, 保证"看到的就是引擎里的"。
        /// </summary>
        private double CurrentEngineSpeed()
        {
            double real = SpeedHack.IsAvailable ? SpeedHack.Speed : 1.0;
            if (!(real > 0.0) || real > _maxSpeed)
                real = SpeedHack.ClampSpeed(_activeSpeed, _minSpeed, _maxSpeed);
            return SpeedHack.ClampSpeed(real, _minSpeed, _maxSpeed);
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
            _toggleRestoresSpeed = _config.GetBool("toggleRestoresSpeed", _toggleRestoresSpeed);
            _repeatInterval = _config.GetFloat("repeatInterval", _repeatInterval);

            // 夹紧范围。注意要放在**读完全部键之后**: SaveConfig 会把所有键整份重写,
            // 提前调用会把还没读到的键写回默认值(等于吃掉用户的配置)。
            bool boundsFixed = ClampConfigBounds();

            // 首次运行: 把默认值整份写出来, 省得用户去翻文档猜键名
            if (!_config.Has("speed") || !_config.Has("toggleKey"))
            {
                SaveConfig();
                Log.Info("已生成默认配置: " + (_config.Path ?? "(未知路径)"));
            }
            else if (boundsFixed)
            {
                // 老的 config.json 里可能留着 minSpeed=0.1(那时候还能减速) —— 一次性改掉,
                // 免得每次启动都警告、也免得用户以为"改小就能减速"。
                SaveConfig();
                Log.Info("配置里的倍率范围已按新规则纠正(下限 1.0, 不允许减速)");
            }
        }

        /// <summary>
        /// 把 speed/minSpeed/maxSpeed 夹到合法区间。返回是否真的改动了值(用于决定要不要写回配置)。
        ///
        /// 硬下限 1.0: 手改 config.json 把 minSpeed 写成 0.2 也没用 —— SDK 的
        /// ClampSpeed/SetSpeed 与加载器都会再拦一次, 这里只是让日志/提示如实反映。
        /// </summary>
        private bool ClampConfigBounds()
        {
            bool changed = false;

            if (_minSpeed < SpeedHack.MinSpeed)
            {
                SdkLog.Warn("SpeedHack", "minSpeed=" + F(_minSpeed) + " 低于硬下限, 按 " +
                             F(SpeedHack.MinSpeed) + " 处理(不允许减速)");
                _minSpeed = SpeedHack.MinSpeed;
                changed = true;
            }

            if (_maxSpeed < _minSpeed) { _maxSpeed = _minSpeed; changed = true; }
            if (_maxSpeed > SpeedHack.HardMaxSpeed) { _maxSpeed = SpeedHack.HardMaxSpeed; changed = true; }

            if (_activeSpeed < _minSpeed) { _activeSpeed = _minSpeed; changed = true; }
            if (_activeSpeed > _maxSpeed) { _activeSpeed = _maxSpeed; changed = true; }

            return changed;
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
                _config.Set("toggleRestoresSpeed", _toggleRestoresSpeed);
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
            return "引擎当前 " + F(SpeedHack.IsAvailable ? SpeedHack.Speed : 1.0) + "x" +
                   " (Alt 调整的记忆倍率 " + F(_activeSpeed) + "x, 每次 " + F(_speedStep) + "x)";
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
