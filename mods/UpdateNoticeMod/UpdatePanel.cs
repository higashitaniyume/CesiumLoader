using System;
using System.Collections.Generic;
using System.Text;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.Updates;
using CesiumLoader.SDK.UserInterface;
using UnityEngine;

namespace UpdateNoticeMod
{
    /// <summary>
    /// 更新提示面板（uGUI，经 SDK 的 <see cref="WidgetService"/> 反射创建）。
    ///
    /// 为什么用 WidgetService 而不是游戏的 FairyGUI：
    ///   - 这个面板要在**登录界面**就能出现（那时游戏 UI 还没建好），uGUI 独立 Canvas 最省事；
    ///   - 不引用任何游戏类型/资源图集，游戏更新后不会因为 UI 包改版而崩；
    ///   - 层级固定在 3000，压在游戏 UI 之上，但只让按钮接收点击（面板本体不挡游戏操作）。
    ///
    /// 坐标是 CanvasScaler 的参考分辨率（1920×1080）下的像素，面板固定在屏幕中心。
    /// </summary>
    internal sealed class UpdatePanel : IDisposable
    {
        private const int SortingOrder = 3000;
        private const float PanelWidth = 640f;
        private const float PanelHeight = 380f;
        private const float ButtonWidth = 180f;
        private const float ButtonHeight = 44f;
        private const float ButtonGap = 14f;
        private const float ButtonBottomOffset = 56f;

        private readonly ModLogger _log;
        private readonly Action _openPage;
        private readonly Action _ignoreVersion;
        private readonly Action _retry;
        private readonly Action _dismiss;

        private readonly List<ButtonSlot> _slots = new List<ButtonSlot>();

        private WidgetScope _scope;
        private WidgetHandle _panel;
        private WidgetHandle _title;
        private WidgetHandle _version;
        private WidgetHandle _notes;
        private WidgetHandle _hint;

        private ButtonSlot _openSlot;
        private ButtonSlot _ignoreSlot;
        private ButtonSlot _retrySlot;
        private ButtonSlot _closeSlot;

        private bool _built;
        private bool _disposed;
        private bool _warnedUnavailable;

        public UpdatePanel(ModLogger log, Action openPage, Action ignoreVersion, Action retry, Action dismiss)
        {
            _log = log;
            _openPage = openPage;
            _ignoreVersion = ignoreVersion;
            _retry = retry;
            _dismiss = dismiss;
        }

        public bool IsVisible { get; private set; }

        /// <summary>
        /// 面板曾经建好、但底层 Canvas 已经没了（场景切换/容器销毁会带走它）。
        /// 调用方据此决定"要不要重建一次"，避免玩家按 F8 只看到空气。
        /// </summary>
        public bool NeedsRebuild
        {
            get { return _built && (_panel == null || !_panel.IsAlive); }
        }

        // =====================================================================
        // 构建
        // =====================================================================

        private bool EnsureBuilt()
        {
            if (_disposed) return false;
            if (_built)
            {
                if (_panel != null && _panel.IsAlive) return true;

                // 场景切换/容器销毁把 Canvas 带走了：清句柄重建，否则之后按 F8 只会看到空气。
                _log.Info("面板画布已随场景销毁，重建一次");
                ResetBuild();
            }

            if (!WidgetService.IsAvailable)
            {
                if (!_warnedUnavailable)
                {
                    _warnedUnavailable = true;
                    _log.Warn("uGUI 不可用，更新提示面板无法显示：" + WidgetService.UnavailableReason);
                }
                return false;
            }

            _scope = WidgetService.CreateScope("update-notice");

            var canvas = _scope.Track(WidgetService.Canvas("UpdateNoticeCanvas", SortingOrder, persist: true));
            if (canvas == null)
            {
                _log.Warn("面板构建失败：Canvas 创建失败");
                return false;
            }
            WidgetService.EnsureEventSystem();   // 按钮点击需要 EventSystem，没有才会建

            _panel = WidgetService.Panel(canvas.Rect, new Color(0.055f, 0.07f, 0.105f, 0.95f), rounded: true);
            if (_panel == null)
            {
                _log.Warn("面板构建失败：Panel 创建失败");
                return false;
            }
            WidgetService.Place(_panel.Rect, WidgetAnchor.Center, Vector2.zero, new Vector2(PanelWidth, PanelHeight));
            _scope.Track(_panel);

            // 顶部强调条：一条细色带，和加载器控制台的品红标题呼应。
            var accent = WidgetService.Panel(_panel.Rect, new Color(0.62f, 0.35f, 0.85f, 0.95f), rounded: true);
            if (accent != null)
            {
                WidgetService.Place(accent.Rect, WidgetAnchor.Top, new Vector2(0f, -7f),
                    new Vector2(PanelWidth - 40f, 4f));
                _scope.Track(accent);
            }

            _title = WidgetService.Text(_panel.Rect, "加载器有新版本可用", 26, Color.white, WidgetAnchor.Top);
            if (_title != null)
            {
                WidgetService.Place(_title.Rect, WidgetAnchor.Top, new Vector2(0f, -24f),
                    new Vector2(PanelWidth - 60f, 36f));
                _scope.Track(_title);
            }

            _version = WidgetService.Text(_panel.Rect, "", 19, new Color(0.78f, 0.86f, 1f, 1f), WidgetAnchor.Top);
            if (_version != null)
            {
                WidgetService.Place(_version.Rect, WidgetAnchor.Top, new Vector2(0f, -64f),
                    new Vector2(PanelWidth - 60f, 26f));
                _scope.Track(_version);
            }

            _notes = WidgetService.Text(_panel.Rect, "", 15, new Color(0.88f, 0.9f, 0.96f, 1f), WidgetAnchor.TopLeft);
            if (_notes != null)
            {
                WidgetService.Place(_notes.Rect, WidgetAnchor.Top, new Vector2(0f, -94f),
                    new Vector2(PanelWidth - 80f, 104f));
                _scope.Track(_notes);
            }

            _hint = WidgetService.Text(_panel.Rect, "", 13, new Color(0.68f, 0.7f, 0.78f, 1f), WidgetAnchor.Bottom);
            if (_hint != null)
            {
                WidgetService.Place(_hint.Rect, WidgetAnchor.Bottom, new Vector2(0f, 18f),
                    new Vector2(PanelWidth - 60f, 22f));
                _scope.Track(_hint);
            }

            if (WidgetService.IsButtonAvailable)
            {
                _openSlot = MakeButton("打开发布页", new Color(0.24f, 0.5f, 0.82f, 0.95f), _openPage);
                _ignoreSlot = MakeButton("忽略此版本", new Color(0.24f, 0.26f, 0.32f, 0.95f), _ignoreVersion);
                _retrySlot = MakeButton("立即重试", new Color(0.24f, 0.5f, 0.82f, 0.95f), _retry);
                _closeSlot = MakeButton("稍后再看", new Color(0.2f, 0.22f, 0.27f, 0.95f), _dismiss);
            }
            else
            {
                _log.Info("按钮控件不可用（uGUI Button 缺失），面板改用键盘操作提示");
            }

            SetActive(false);
            _built = true;
            _log.Info("面板已构建：按钮" + (WidgetService.IsButtonAvailable ? "可用" : "不可用") +
                      "，Canvas " + (canvas.IsAlive ? "存活" : "已失效"));
            return true;
        }

        /// <summary>清掉构建状态（保留 <see cref="_disposed"/>），下次 <see cref="EnsureBuilt"/> 重建。</summary>
        private void ResetBuild()
        {
            try { if (_scope != null) _scope.Dispose(); } catch { }
            _scope = null;
            _panel = null;
            _title = null;
            _version = null;
            _notes = null;
            _hint = null;
            _openSlot = null;
            _ignoreSlot = null;
            _retrySlot = null;
            _closeSlot = null;
            _slots.Clear();
            _built = false;
        }

        private ButtonSlot MakeButton(string text, Color background, Action action)
        {
            var button = WidgetService.Button(_panel.Rect, background, rounded: true);
            if (button == null) return null;
            _scope.Track(button);

            var label = WidgetService.Text(button.Rect, text, 16, Color.white, WidgetAnchor.Center);
            if (label != null)
            {
                WidgetService.Stretch(label.Rect);
                _scope.Track(label);
            }

            WidgetService.SetOnClick(button.Rect, () =>
            {
                if (_disposed) return;
                if (_retrySlot != null && _retrySlot.Button == button && action == null)
                {
                    // 「立即重试」：请求加载器强制重查，然后把面板收起来等结果。
                    Hide();
                    return;
                }
                if (action != null) action();
            });

            var slot = new ButtonSlot { Button = button, Label = label, Text = text, Action = action };
            _slots.Add(slot);
            return slot;
        }

        private sealed class ButtonSlot
        {
            public WidgetHandle Button;
            public WidgetHandle Label;
            public string Text;
            public Action Action;
        }

        // =====================================================================
        // 显示
        // =====================================================================

        /// <summary>显示面板。<paramref name="allowIgnore"/> 为 false 时不显示「忽略此版本」。</summary>
        public void Show(UpdateCheckState state, bool allowIgnore, bool showNotes)
        {
            if (_disposed || state == null) return;
            if (!EnsureBuilt()) return;

            var visible = new List<ButtonSlot>();

            if (state.HasUpdate)
            {
                SetText(_title, "加载器有新版本可用");
                SetText(_version, state.CurrentVersion + "   →   " + state.LatestVersion);
                SetText(_notes, BuildNotes(state, showNotes, allowIgnore));
                visible.Add(_openSlot);
                if (allowIgnore) visible.Add(_ignoreSlot);
                visible.Add(_closeSlot);
                SetText(_hint, WidgetService.IsButtonAvailable
                    ? "安装请用 Toys 或发布页 · F8 关闭"
                    : "按钮不可用：请手动访问 " + ShortUrl(state) + " · F8 关闭");
            }
            else if (state.IsError)
            {
                SetText(_title, "更新检查失败");
                SetText(_version, "当前 " + state.CurrentVersion);
                SetText(_notes, BuildNotes(state, showNotes, allowIgnore));
                visible.Add(_retrySlot);
                visible.Add(_closeSlot);
                SetText(_hint, "不影响游戏与模组 · F8 关闭");
            }
            else if (state.IsDisabled)
            {
                SetText(_title, "更新检查已关闭");
                SetText(_version, "当前 " + state.CurrentVersion);
                SetText(_notes, BuildNotes(state, showNotes, allowIgnore));
                visible.Add(_closeSlot);
                SetText(_hint, "在 doorstop_config.json 里把 updateCheckEnabled 改成 true 可开启 · F8 关闭");
            }
            else
            {
                SetText(_title, "已是最新版本");
                SetText(_version, "当前 " + state.CurrentVersion);
                SetText(_notes, BuildNotes(state, showNotes, allowIgnore));
                visible.Add(_closeSlot);
                SetText(_hint, state.CheckedAtUtc == DateTime.MinValue
                    ? "来自加载器的更新检查结果 · F8 关闭"
                    : "检查于 " + state.CheckedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + " · F8 关闭");
            }

            Layout(visible);
            SetActive(true);
            IsVisible = true;
            _log.Info("面板显示：" + state.Describe());
        }

        public void Hide()
        {
            IsVisible = false;
            SetActive(false);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            IsVisible = false;
            try { if (_scope != null) _scope.Dispose(); } catch { }
            _scope = null;
            _panel = null;
            _title = null;
            _version = null;
            _notes = null;
            _hint = null;
            _slots.Clear();
        }

        // =====================================================================
        // 内容
        // =====================================================================

        private static string BuildNotes(UpdateCheckState state, bool showNotes, bool allowIgnore)
        {
            var sb = new StringBuilder();

            if (state.HasUpdate)
            {
                // 第一行：tag · 日期。发布页不再单独占一行 —— 它就在「打开发布页」按钮后面；
                // 按钮不可用时由底部提示条给出地址。
                if (!string.IsNullOrEmpty(state.ReleaseTag)) sb.Append(state.ReleaseTag);
                if (!string.IsNullOrEmpty(state.ReleaseDate))
                {
                    if (sb.Length > 0) sb.Append(" · ");
                    sb.Append(state.ReleaseDate);
                }
                if (sb.Length > 0) sb.Append('\n');

                if (showNotes && !string.IsNullOrEmpty(state.ReleaseNotes))
                {
                    sb.Append('\n').Append(TrimNotes(state.ReleaseNotes));
                }
                else if (!string.IsNullOrEmpty(state.ReleaseUrl))
                {
                    sb.Append('\n').Append(state.ReleaseUrl);
                }

                if (!allowIgnore) sb.Append("\n\n此版本已在「忽略」列表里，不再自动弹出。");
            }
            else if (state.IsError)
            {
                sb.Append("原因：").Append(string.IsNullOrEmpty(state.Error) ? "未知错误" : state.Error).Append('\n');
                sb.Append('\n').Append("检查失败不影响游戏与模组；可以点「立即重试」，或在发布页手动查看。");
            }
            else if (state.IsDisabled)
            {
                sb.Append("加载器配置里 updateCheckEnabled=false，启动时不会联网检查。\n");
                sb.Append("想收到提示就把它改成 true，然后重启游戏。");
            }
            else
            {
                sb.Append("当前 ").Append(state.CurrentVersion).Append(" 已经是发布通道上的最新版本。");
            }

            return sb.ToString();
        }

        /// <summary>
        /// 更新说明只给 4 行版面：多出来的部分交给发布页。每行还要按**显示宽度**裁一次 ——
        /// 中文/全角记 2 列、ASCII 记 1 列，15px 字号下正文区约 74 列；
        /// 只按"字符数"裁的话，一行 40 个汉字会折成两行，整个面板就撑出按钮区。
        /// </summary>
        private static string TrimNotes(string notes)
        {
            var text = notes.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            if (text.Length == 0) return "";

            var lines = text.Split('\n');
            var sb = new StringBuilder();
            var truncated = false;
            for (var i = 0; i < lines.Length; i++)
            {
                if (i >= 4)
                {
                    truncated = true;
                    break;
                }

                if (i > 0) sb.Append('\n');
                var line = ClampLine(lines[i].Trim(), MaxNoteColumns, out var clipped);
                truncated |= clipped;
                sb.Append(line);
            }

            if (truncated)
            {
                var result = sb.ToString();
                return result.Length > 0 ? result + "…" : result;
            }
            return sb.ToString();
        }

        /// <summary>正文区一行的最大显示列数（15px 字号、560px 宽）。</summary>
        private const int MaxNoteColumns = 74;

        /// <summary>按显示列宽裁一行；CJK/全角/emoji 记 2 列。</summary>
        private static string ClampLine(string line, int maxColumns, out bool clipped)
        {
            clipped = false;
            if (string.IsNullOrEmpty(line)) return "";

            var width = 0;
            for (var i = 0; i < line.Length; i++)
            {
                var w = IsWide(line[i]) ? 2 : 1;
                if (width + w > maxColumns)
                {
                    clipped = true;
                    return line.Substring(0, i).TrimEnd() + "…";
                }
                width += w;
            }
            return line;
        }

        private static bool IsWide(char c)
        {
            return (c >= 0x1100 && c <= 0x115F) ||
                   (c >= 0x2E80 && c <= 0xA4CF) ||
                   (c >= 0xAC00 && c <= 0xD7A3) ||
                   (c >= 0xF900 && c <= 0xFAFF) ||
                   (c >= 0xFE30 && c <= 0xFE6F) ||
                   (c >= 0xFF00 && c <= 0xFF60) ||
                   (c >= 0xFFE0 && c <= 0xFFE6);
        }

        private static string ShortUrl(UpdateCheckState state)
        {
            if (!string.IsNullOrEmpty(state.ReleaseUrl)) return state.ReleaseUrl;
            return UpdateCheck.FallbackReleasePage;
        }

        // =====================================================================
        // 布局
        // =====================================================================

        private void Layout(List<ButtonSlot> visible)
        {
            // 先全部收起来，再按本次要显示的按钮居中排开 —— 三/四个按钮混用时不会留空洞。
            for (var i = 0; i < _slots.Count; i++)
            {
                var slot = _slots[i];
                if (slot != null && slot.Button != null) slot.Button.SetActive(false);
            }

            var shown = new List<ButtonSlot>();
            for (var i = 0; i < visible.Count; i++)
            {
                if (visible[i] != null && visible[i].Button != null) shown.Add(visible[i]);
            }
            if (shown.Count == 0) return;

            var total = shown.Count * ButtonWidth + (shown.Count - 1) * ButtonGap;
            var x = -total / 2f + ButtonWidth / 2f;
            for (var i = 0; i < shown.Count; i++)
            {
                shown[i].Button.SetActive(true);
                WidgetService.Place(shown[i].Button.Rect, WidgetAnchor.Bottom,
                    new Vector2(x, ButtonBottomOffset), new Vector2(ButtonWidth, ButtonHeight));
                x += ButtonWidth + ButtonGap;
            }
        }

        private static void SetText(WidgetHandle handle, string text)
        {
            if (handle != null) handle.SetText(text ?? "");
        }

        private void SetActive(bool active)
        {
            if (_panel != null) _panel.SetActive(active);
        }
    }
}
