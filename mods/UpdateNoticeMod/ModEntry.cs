using System;
using CesiumLoader.SDK.Inputs;
using CesiumLoader.SDK.Mods;
using CesiumLoader.SDK.Updates;
using UnityEngine;

namespace UpdateNoticeMod
{
    /// <summary>入口：只做一件事 —— 起生命周期控制器。</summary>
    public static class ModEntry
    {
        public static void Main()
        {
            ModBase.Run(new UpdateNoticeController());
        }
    }

    /// <summary>
    /// 加载器更新提示。
    ///
    /// 数据来自原生加载器写好的 <c>&lt;loader&gt;\update\state.json</c>（见 CesiumLoader 的
    /// <c>update/update.h</c>）：<b>模组自己不联网</b>，只负责在游戏里把结论显示出来，
    /// 并把「打开发布页」转成一个本地 UDP 请求交给加载器执行。
    ///
    /// 行为：
    ///   - 启动后读一次状态文件；发现新版本时自动弹一次面板（可配置关闭）；
    ///   - 之后每 10 秒看一次状态文件，加载器后台检查完成（或玩家在控制台 update check）会跟上；
    ///   - 热键（默认 F8）随时开关面板；Esc 关闭；
    ///   - 「忽略此版本」把版本号写进本模组 config.json，该版本不再自动弹；
    ///     换新版本后会重新提示。
    ///
    /// 刻意不做的事：不在游戏里下载/替换 version.dll（正在被进程占用，风险远大于收益）。
    /// 面板只提示 + 跳发布页，安装仍由 Toys 或手动解压完成。
    /// </summary>
    public sealed class UpdateNoticeController : ModBase
    {
        public override string Name { get { return "加载器更新提示"; } }
        public override string Version { get { return "1.0.0"; } }
        public override string Author { get { return "CesiumLoader"; } }

        /// <summary>状态文件轮询间隔（秒）。文件 < 2 KB，读一次的成本可以忽略。</summary>
        private const double PollSeconds = 10.0;

        /// <summary>启动后最多等多久的检查结果；超时就不再自动弹（避免对局中途突然弹窗）。</summary>
        private const double AutoPromptDeadlineSeconds = 90.0;

        /// <summary>开局头几秒不弹：登录/加载动画还在跑，突然盖一层 UI 很突兀。</summary>
        private const double AutoPromptGraceSeconds = 8.0;

        private bool _enabled = true;
        private bool _autoShow = true;
        private bool _showNotes = true;
        private string _ignoredVersion = "";
        private KeyCode _toggleKey = KeyCode.F8;

        private UpdatePanel _panel;
        private UpdateCheckState _state;
        private DateTime _nextPoll = DateTime.MinValue;
        private DateTime _startedUtc;
        private bool _prompted;

        /// <summary>玩家自己关掉了面板（Esc / F8 / 稍后再看 / 忽略）：之后不再自动补显示。</summary>
        private bool _dismissed;

        public override void OnInitialize()
        {
            _startedUtc = DateTime.UtcNow;
            LoadConfig();

            _panel = new UpdatePanel(Log, OpenReleasePage, IgnoreVersion, RequestCheckNow, Dismiss);
            var context = Context;
            if (context != null)
            {
                context.RegisterCleanup(() =>
                {
                    try { if (_panel != null) _panel.Dispose(); } catch { }
                });
            }

            if (!_enabled)
            {
                Log.Info("配置里 enabled=false：不弹任何更新提示（改 true 立即生效）");
                return;
            }

            if (!UpdateCheck.IsAvailable)
            {
                // 旧版加载器没有这个通道：状态文件仍然能读，只是「打开发布页」要玩家自己复制链接。
                Log.Warn("更新通道不可用：" + UpdateCheck.UnavailableReason);
            }
            Log.Info("已就绪：状态文件 " + UpdateCheck.StatePath + "，热键 " + _toggleKey);
        }

        public override void OnConfigChanged()
        {
            LoadConfig();
        }

        public override void OnUpdate()
        {
            if (!_enabled) return;
            PollState();
            HandleKeys();
            MaybeAutoPrompt();
        }

        public override void OnUnload()
        {
            try { if (_panel != null) _panel.Dispose(); } catch { }
        }

        // =====================================================================
        // 配置
        // =====================================================================

        private void LoadConfig()
        {
            var config = Config;
            if (config == null) return;

            var changed = UpdateNoticeConfigDocs.Apply(config);
            _enabled = config.GetBool("enabled", true);
            _autoShow = config.GetBool("autoShow", true);
            _showNotes = config.GetBool("showReleaseNotes", true);
            _ignoredVersion = (config.GetString("ignoredVersion", "") ?? "").Trim();
            _toggleKey = ParseKey(config.GetString("toggleKey", "F8"), KeyCode.F8);
            if (changed) config.SaveIfDirty();
        }

        private static KeyCode ParseKey(string text, KeyCode fallback)
        {
            if (string.IsNullOrEmpty(text)) return fallback;
            text = text.Trim();
            if (text == "`" || text == "~" || text == "·") return KeyCode.BackQuote;

            KeyCode parsed;
            if (Enum.TryParse(text, true, out parsed) && parsed != KeyCode.None) return parsed;
            return fallback;
        }

        // =====================================================================
        // 状态
        // =====================================================================

        private void PollState()
        {
            var now = DateTime.UtcNow;
            if (now < _nextPoll) return;
            _nextPoll = now.AddSeconds(PollSeconds);

            UpdateCheckState state;
            if (!UpdateCheck.TryGetState(out state) || state == null) return;

            var unchanged = _state != null &&
                            string.Equals(_state.Status, state.Status, StringComparison.Ordinal) &&
                            string.Equals(_state.LatestVersion, state.LatestVersion, StringComparison.Ordinal);
            _state = state;
            if (!unchanged) LogState(state);

            // 该显示、但画布被场景切换带走了：补显示一次（用户主动关掉的不补）。
            if (_prompted && !_dismissed && state.HasUpdate && !IsIgnored(state.LatestVersion) &&
                _panel != null && _panel.NeedsRebuild)
            {
                Log.Info("面板画布已失效，重新显示一次");
                ShowPanel();
            }
        }

        private void LogState(UpdateCheckState state)
        {
            if (state.HasUpdate)
            {
                Log.Info("发现新版本 " + state.LatestVersion + "（当前 " + state.CurrentVersion + "）· " +
                         (string.IsNullOrEmpty(state.ReleaseUrl) ? "无发布页地址" : state.ReleaseUrl));
            }
            else if (state.IsError)
            {
                Log.Warn("更新检查失败：" + (string.IsNullOrEmpty(state.Error) ? "未知错误" : state.Error));
            }
            else if (state.IsUpToDate)
            {
                Log.Info("已是最新（" + state.CurrentVersion + "）");
            }
            else if (state.IsDisabled)
            {
                Log.Info("加载器配置里关闭了自动检查（updateCheckEnabled=false）");
            }
        }

        private void MaybeAutoPrompt()
        {
            if (_prompted || !_autoShow) return;

            var elapsed = (DateTime.UtcNow - _startedUtc).TotalSeconds;
            if (_state == null)
            {
                if (elapsed >= AutoPromptDeadlineSeconds) _prompted = true;   // 不再等检查结果
                return;
            }

            if (!_state.HasUpdate || IsIgnored(_state.LatestVersion))
            {
                _prompted = true;
                return;
            }

            if (elapsed < AutoPromptGraceSeconds) return;
            _prompted = true;
            ShowPanel();
        }

        private bool IsIgnored(string version)
        {
            return !string.IsNullOrEmpty(_ignoredVersion) && !string.IsNullOrEmpty(version) &&
                   string.Equals(_ignoredVersion, version, StringComparison.OrdinalIgnoreCase);
        }

        // =====================================================================
        // 输入
        // =====================================================================

        private void HandleKeys()
        {
            if (_panel == null || !InputService.IsAvailable) return;
            if (InputService.IsKeyboardCaptured) return;   // 别的模组正在独占键盘时不抢

            if (InputService.IsKeyPressed(_toggleKey))
            {
                if (_panel.IsVisible)
                {
                    _dismissed = true;
                    _panel.Hide();
                }
                else
                {
                    ShowPanel();
                }
                return;
            }

            if (_panel.IsVisible && InputService.IsKeyPressed(KeyCode.Escape))
            {
                _dismissed = true;
                _panel.Hide();
            }
        }

        // =====================================================================
        // 动作
        // =====================================================================

        private void ShowPanel()
        {
            if (_panel == null) return;

            if (_state == null) UpdateCheck.TryGetState(out _state);
            if (_state == null)
            {
                Log.Info("还没有检查结果（加载器可能关了更新检查，或检查尚未完成）");
                return;
            }

            _dismissed = false;
            var allowIgnore = _state.HasUpdate && !IsIgnored(_state.LatestVersion);
            _panel.Show(_state, allowIgnore, _showNotes);
        }

        private void Dismiss()
        {
            _dismissed = true;
            if (_panel != null) _panel.Hide();
        }

        private void RequestCheckNow()
        {
            if (_panel != null) _panel.Hide();
            _nextPoll = DateTime.MinValue;   // 下一帧就去读状态文件

            if (UpdateCheck.RequestCheck())
            {
                Log.Info("已请求加载器重新检查更新（忽略冷却）");
            }
            else
            {
                Log.Warn("无法请求重新检查：" + UpdateCheck.UnavailableReason);
            }
        }

        private void OpenReleasePage()
        {
            var url = _state != null ? _state.ReleaseUrl : "";
            if (string.IsNullOrEmpty(url)) url = UpdateCheck.FallbackReleasePage;

            if (UpdateCheck.OpenReleasePage())
            {
                Log.Info("已在默认浏览器打开发布页：" + url);
            }
            else
            {
                Log.Warn("打不开浏览器（更新通道不可用？）。发布页：" + url);
            }
        }

        private void IgnoreVersion()
        {
            if (_state == null || !_state.HasUpdate) return;

            _ignoredVersion = _state.LatestVersion;
            var config = Config;
            if (config != null)
            {
                config.Set("ignoredVersion", _ignoredVersion);
                config.SaveIfDirty();
            }
            Log.Info("已忽略版本 " + _ignoredVersion + "（该版本不再自动弹出；改 config.json 的 ignoredVersion 可恢复）");
            _dismissed = true;
            if (_panel != null) _panel.Hide();
        }
    }
}
