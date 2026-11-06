using System;
using System.Text;
using System.Threading;
using System.Windows;

namespace CesiumLoader.Overlay
{
    public sealed class WindowFollower : IDisposable
    {
        private readonly int _targetPid;
        private Thread _thread;
        private volatile bool _running;
        private IntPtr _gameHwnd = IntPtr.Zero;
        private readonly Window _overlay;
        public event Action<IntPtr, Win32Native.RECT> OnLayout;

        public WindowFollower(int targetPid, Window overlay)
        {
            _targetPid = targetPid;
            _overlay = overlay;
        }

        public void Start()
        {
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "CesiumOverlay.Follow" };
            _thread.Start();
        }

        private void Loop()
        {
            while (_running)
            {
                try
                {
                    IntPtr hwnd = FindGameWindow();
                    if (hwnd != IntPtr.Zero)
                    {
                        _gameHwnd = hwnd;
                        if (Win32Native.GetWindowRect(hwnd, out var rect))
                        {
                            int w = rect.Right - rect.Left;
                            int h = rect.Bottom - rect.Top;
                            if (w > 0 && h > 0 && !Win32Native.IsIconic(hwnd))
                            {
                                OnLayout?.Invoke(hwnd, rect);
                            }
                        }
                    }
                }
                catch { }
                Thread.Sleep(120);
                // 游戏退了就退出（pid 不存在则自杀）
                if (_targetPid > 0)
                {
                    try
                    {
                        var p = System.Diagnostics.Process.GetProcessById(_targetPid);
                        if (p.HasExited) { _overlay.Dispatcher.Invoke(() => { try { _overlay.Close(); } catch { } }); break; }
                    }
                    catch
                    {
                        _overlay.Dispatcher.Invoke(() => { try { _overlay.Close(); } catch { } });
                        break;
                    }
                }
            }
        }

        public IntPtr FindGameWindow()
        {
            if (_targetPid <= 0) return IntPtr.Zero;
            IntPtr found = IntPtr.Zero;
            Win32Native.EnumWindows((hwnd, lParam) =>
            {
                Win32Native.GetWindowThreadProcessId(hwnd, out uint pid);
                if ((int)pid != _targetPid) return true;
                if (!Win32Native.IsWindowVisible(hwnd)) return true;
                var sb = new StringBuilder(64);
                Win32Native.GetClassName(hwnd, sb, sb.Capacity);
                if (sb.ToString() == "UnityWndClass") { found = hwnd; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        public void Dispose()
        {
            _running = false;
        }
    }
}
