using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CesiumLoader.Overlay
{
    public partial class MainWindow : Window
    {
        private UdpServer _udp;
        private WindowFollower _follower;
        private DispatcherTimer _followTimer;
        private DispatcherTimer _togglePollTimer;
        private bool _clickThrough = true;
        private bool _shopExpanded = true;
        private bool _relicExpanded = true;
        private bool _allCollapsed = false;
        private int _targetPid;
        private bool _demoMode;
        private bool _toggleHeld;
        private string _lastShopText = "";
        private string _lastRelicText = "";
        private bool _shopVisible = false;
        private bool _relicVisible = false;
        private OverlayConfig _cfg;
        private string _cfgPath;
        private FileSystemWatcher _cfgWatcher;
        private bool _dragging;
        private Point _dragStart;
        private double _dragStartLeft;
        private double _dragStartTop;

        public MainWindow()
        {
            InitializeComponent();
            _cfg = OverlayConfig.Load(null, out _cfgPath);
            ApplyConfigToUi();
            Loaded += MainWindow_Loaded;
            SourceInitialized += (s, e) =>
            {
                var app = Application.Current as App;
                _targetPid = app?.TargetPid ?? 0;
                _demoMode = app?.DemoMode ?? false;
                int port = app?.UdpPort ?? 0;
                if (port > 0) StartUdp(port);
                if (_demoMode) StartDemo();
                StartFollower();
                StartTogglePoll();
                StartConfigWatcher();
            };
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                int ex = Win32Native.GetWindowLong(hwnd, Win32Native.GWL_EXSTYLE);
                int want = ex | Win32Native.WS_EX_TRANSPARENT | Win32Native.WS_EX_TOOLWINDOW | Win32Native.WS_EX_NOACTIVATE;
                Win32Native.SetWindowLong(hwnd, Win32Native.GWL_EXSTYLE, want);
            }
            // 工具条可拖（改 Left/Top），松手把 offsetX/Y 写回 overlay_config.json
            WireDrag();
        }

        private void WireDrag()
        {
            // 顶部工具条 Border 是 StackPanel 第一个子元素，用 MouseDown/Up/Move 做拖
            try
            {
                var stack = (FrameworkElement)this.Content;
                // Content 是 Grid，Grid 的唯一子是 StackPanel
                Grid grid = stack as Grid;
                if (grid == null) return;
                var panel = grid.Children[0] as StackPanel;
                if (panel == null || panel.Children.Count == 0) return;
                var toolbar = panel.Children[0] as Border;
                if (toolbar == null) return;
                toolbar.MouseLeftButtonDown += (s, ev) =>
                {
                    if (_clickThrough) return; // 穿透态不拖
                    _dragging = true;
                    _dragStart = ev.GetPosition(this);
                    _dragStartLeft = Left;
                    _dragStartTop = Top;
                    toolbar.CaptureMouse();
                    ev.Handled = true;
                };
                toolbar.MouseMove += (s, ev) =>
                {
                    if (!_dragging) return;
                    Point p = ev.GetPosition(this);
                    Left = _dragStartLeft + (p.X - _dragStart.X);
                    Top = _dragStartTop + (p.Y - _dragStart.Y);
                };
                toolbar.MouseLeftButtonUp += async (s, ev) =>
                {
                    if (!_dragging) return;
                    _dragging = false;
                    try { toolbar.ReleaseMouseCapture(); } catch { }
                    // 把新 Left/Top 换算成相对游戏窗口的 offsetX/Y
                    if (_targetPid > 0 && _follower != null)
                    {
                        var hwnd = _follower.FindGameWindow();
                        if (hwnd != IntPtr.Zero && Win32Native.GetWindowRect(hwnd, out var rect))
                        {
                            double panelW = Math.Max(280, Math.Min(560, ActualWidth));
                            _cfg.OffsetX = Math.Max(0, Math.Min(1200, rect.Right - (Left + panelW)));
                            _cfg.OffsetY = Math.Max(0, Math.Min(1200, Top - rect.Top));
                            await _cfg.SaveAsync(_cfgPath);
                        }
                    }
                };
            }
            catch { }
        }

        private void ApplyConfigToUi()
        {
            try
            {
                if (_cfg.Scale <= 0) _cfg.Scale = 1.0;
                if (RootScale != null)
                {
                    RootScale.ScaleX = _cfg.Scale;
                    RootScale.ScaleY = _cfg.Scale;
                    RootScale.CenterX = 0;
                    RootScale.CenterY = 0;
                }
                Opacity = _cfg.Opacity;
                Width = _cfg.PanelWidth + 10;
            }
            catch { }
        }

        private void StartConfigWatcher()
        {
            try
            {
                string dir = Path.GetDirectoryName(_cfgPath);
                string name = Path.GetFileName(_cfgPath);
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name) || !Directory.Exists(dir)) return;
                _cfgWatcher = new FileSystemWatcher(dir, name + "*");
                _cfgWatcher.Changed += (s, e) =>
                {
                    try
                    {
                        Dispatcher.Invoke(() =>
                        {
                            var c = OverlayConfig.Load(_cfgPath, out _);
                            _cfg = c;
                            ApplyConfigToUi();
                            HintText.Text = _clickThrough ? "反斜杠(\\) 切换穿透 · 拖工具条移位" : "点击穿透：关，可点标题收起（反斜杠切换）";
                        });
                    }
                    catch { }
                };
                _cfgWatcher.EnableRaisingEvents = true;
            }
            catch { }
        }

        private void StartUdp(int port)
        {
            _udp = new UdpServer(port);
            _udp.OnPeek += OnPeek;
            _udp.Start();
        }

        private void OnPeek(PeekMessage msg)
        {
            Dispatcher.Invoke(() =>
            {
                if (msg.Panel == "shop") ApplyPeek(true, msg);
                else if (msg.Panel == "relic") ApplyPeek(false, msg);
            });
        }

        private void ApplyPeek(bool isShop, PeekMessage msg)
        {
            if (isShop)
            {
                _shopVisible = msg.Visible;
                if (msg.Visible && !string.IsNullOrEmpty(msg.Text))
                {
                    if (msg.Text != _lastShopText)
                    {
                        BbCodeParser.SetRichText(ShopText, msg.Text, msg.FontSize);
                        _lastShopText = msg.Text;
                    }
                    if (!_allCollapsed) ShopCard.Visibility = Visibility.Visible;
                    ShopCount.Text = CountPlayers(msg.Text);
                }
                else if (!msg.Visible)
                {
                    ShopCard.Visibility = Visibility.Collapsed;
                    _lastShopText = "";
                }
            }
            else
            {
                _relicVisible = msg.Visible;
                if (msg.Visible && !string.IsNullOrEmpty(msg.Text))
                {
                    if (msg.Text != _lastRelicText)
                    {
                        BbCodeParser.SetRichText(RelicText, msg.Text, msg.FontSize);
                        _lastRelicText = msg.Text;
                    }
                    if (!_allCollapsed) RelicCard.Visibility = Visibility.Visible;
                    RelicCount.Text = CountPlayers(msg.Text);
                }
                else if (!msg.Visible)
                {
                    RelicCard.Visibility = Visibility.Collapsed;
                    _lastRelicText = "";
                }
            }
            RefreshMiniPill();
        }

        private string CountPlayers(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            int n = 0;
            for (int i = 0; i < text.Length; i++) if (text[i] == '【' && i + 2 < text.Length) n++;
            if (n == 0) n = text.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries).Length;
            return n + " 位玩家";
        }

        private void RefreshMiniPill()
        {
            bool anyVisible = _shopVisible || _relicVisible;
            MiniPill.Visibility = (_allCollapsed && anyVisible) ? Visibility.Visible : Visibility.Collapsed;
            if (_allCollapsed)
            {
                ShopCard.Visibility = Visibility.Collapsed;
                RelicCard.Visibility = Visibility.Collapsed;
            }
            HintText.Visibility = _allCollapsed ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ShopHeader_Click(object sender, RoutedEventArgs e)
        {
            _shopExpanded = !_shopExpanded;
            var body = ((ShopCard.Child as Grid)?.Children[1] as FrameworkElement);
            if (body != null) body.Visibility = _shopExpanded ? Visibility.Visible : Visibility.Collapsed;
            ShopChevronBtn.Content = _shopExpanded ? "▾" : "▸";
        }

        private void RelicHeader_Click(object sender, RoutedEventArgs e)
        {
            _relicExpanded = !_relicExpanded;
            var body = ((RelicCard.Child as Grid)?.Children[1] as FrameworkElement);
            if (body != null) body.Visibility = _relicExpanded ? Visibility.Visible : Visibility.Collapsed;
            RelicChevronBtn.Content = _relicExpanded ? "▾" : "▸";
        }

        private void CollapseAll_Click(object sender, RoutedEventArgs e)
        {
            _allCollapsed = true;
            RefreshMiniPill();
        }

        private void ExpandAll_Click(object sender, RoutedEventArgs e)
        {
            _allCollapsed = false;
            if (_shopVisible) ShopCard.Visibility = Visibility.Visible;
            if (_relicVisible) RelicCard.Visibility = Visibility.Visible;
            RefreshMiniPill();
        }

        private void StartFollower()
        {
            _followTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _followTimer.Tick += (s, e) =>
            {
                try
                {
                    if (_targetPid <= 0 || _dragging) return;
                    var hwnd = _follower?.FindGameWindow() ?? IntPtr.Zero;
                    if (hwnd == IntPtr.Zero) return;
                    if (Win32Native.IsIconic(hwnd)) return;
                    if (!Win32Native.GetWindowRect(hwnd, out var rect)) return;
                    double panelW = Math.Max(280, Math.Min(560, ShopCard.ActualWidth > 0 ? ShopCard.ActualWidth : (_cfg.PanelWidth * _cfg.Scale)));
                    double offsetX = _cfg.OffsetX;
                    double offsetY = _cfg.OffsetY;
                    double left = rect.Right - panelW - offsetX;
                    double top = rect.Top + offsetY;
                    Left = left;
                    Top = top;
                }
                catch { }
            };
            _follower = new WindowFollower(_targetPid, this);
            _follower.Start();
            _followTimer.Start();
        }

        private void StartTogglePoll()
        {
            _togglePollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _togglePollTimer.Tick += (s, e) =>
            {
                int vk = _cfg.ToggleVk > 0 ? _cfg.ToggleVk : Win32Native.VK_OEM_5;
                bool down = (Win32Native.GetAsyncKeyState(vk) & 0x8000) != 0;
                if (down && !_toggleHeld)
                {
                    _toggleHeld = true;
                    ToggleClickThrough();
                }
                else if (!down) _toggleHeld = false;
            };
            _togglePollTimer.Start();
        }

        private void ToggleClickThrough()
        {
            _clickThrough = !_clickThrough;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int ex = Win32Native.GetWindowLong(hwnd, Win32Native.GWL_EXSTYLE);
            if (_clickThrough)
            {
                ex |= Win32Native.WS_EX_TRANSPARENT;
                HintText.Text = "反斜杠(\\) 切换穿透 · 拖工具条移位";
            }
            else
            {
                ex &= ~Win32Native.WS_EX_TRANSPARENT;
                HintText.Text = "点击穿透：关，可点标题收起（反斜杠切换）";
            }
            Win32Native.SetWindowLong(hwnd, Win32Native.GWL_EXSTYLE, ex);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Oem5 || e.Key == Key.Insert) ToggleClickThrough();
            base.OnKeyDown(e);
        }

        private void StartDemo()
        {
            var shop = "[color=#FF4646][b]【一楼】Alice[/b][/color]\n  [color=#FFD24A]PVP商店遇牌:[/color] [color=#E6F0FF]火球术[/color]、[color=#E6F0FF]治疗波[/color]\n\n[color=#94FF46][b]【二楼】Bob[/b][/color]\n  [color=#FFD24A]PVE商店遇牌:[/color] [color=#E6F0FF]火球术[/color]、[color=#E6F0FF]闪电链[/color]";
            var relic = "[color=#FF4646][b]【一楼】Alice[/b][/color]\n  [color=#FFFFFF][b]候选筹码:[/b][/color] [color=#5AA9FF]铁心[/color]、[color=#D26DFF]符文[/color]";
            Dispatcher.Invoke(() =>
            {
                ApplyPeek(true, new PeekMessage { Panel = "shop", Visible = true, Text = shop, FontSize = 14, PanelWidth = 340 });
                ApplyPeek(false, new PeekMessage { Panel = "relic", Visible = true, Text = relic, FontSize = 14, PanelWidth = 340 });
            });
        }

        protected override void OnClosed(EventArgs e)
        {
            _udp?.Dispose();
            _follower?.Dispose();
            try { _cfgWatcher?.Dispose(); } catch { }
            _followTimer?.Stop();
            _togglePollTimer?.Stop();
            base.OnClosed(e);
        }
    }
}
