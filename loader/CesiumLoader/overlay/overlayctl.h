// overlayctl.h - WPF Overlay 子进程管理器 (mod <-> WPF Overlay, 本地 UDP)
//
// 为什么需要它:
//   热更程序集无法 P/Invoke user32 / 创建 WPF, 进程外的 WPF Overlay 不能自建窗口,
//   只能由同进程原生加载器拉起。Mod -> Overlay 的数据则走 CESIUM_OVERLAY_PORT 直连
//   WPF 进程(见 SDK Overlay.OverlayChannel), 不经加载器转发。
//
// 传输:
//   Mod  -> WPF Overlay (UDP 127.0.0.1 动态端口, 每进程一个):
//     加载器启动时 bind 127.0.0.1:0 拿到临时端口, 立刻关掉, 把端口写进环境变量
//     CESIUM_OVERLAY_PORT 并作为命令行 --port 传给子进程, 子进程再 bind 同一端口。
//     时序上几微秒的竞态窗口可忽略(端口已空, 子进程立即占住)。
//   Overlay -> Mod (配置落盘):
//     WPF 直接改 mods/{ModId}/config.json, Mod 监听热重载。
//
// 进程管理:
//   - CreateProcess + Job Object(JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE)，游戏崩它必死。
//   - 后台看护线程 WaitForSingleObject，异常退出最多重启 3 次，间隔 1s，失败只记日志。
//   - DLL_PROCESS_DETACH 时关 Job 句柄，子进程自动回收。
//
// overlayEnabled=false 时不启动通道，CESIUM_OVERLAY_PORT 不设置，SDK 的 IsAvailable 为 false。

#pragma once

#include <string>

std::wstring overlayctl_dir();

// 启动 WPF Overlay 子进程(幂等)。分配 UDP 端口、写 CESIUM_OVERLAY_PORT、CreateProcess、
// 建 Job、起看护线程。不抛异常、不阻塞调用者。
void overlayctl_start(const std::wstring& dir);

// 关 Job 句柄，子进程随之终止(进程退出时调用，幂等)。
void overlayctl_shutdown();

namespace cesium::overlay
{
using ::overlayctl_dir;
using ::overlayctl_start;
using ::overlayctl_shutdown;
} // namespace cesium::overlay
