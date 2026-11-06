// windowctl.h - 窗口置前控制通道 (mod <-> 加载器, 本地 UDP)
//
// 为什么需要它:
//   热更程序集(经 HybridCLR 加载的 SDK / mod)无法 P/Invoke, 调不到 user32!SetForegroundWindow。
//   所以"轮到我的回合时把游戏窗口放到最前"由原生加载器执行, mod 发一个请求。
//
// 传输: 本地 UDP (127.0.0.1 动态端口, 每进程一个, 多开互不干扰)。
//   加载器启动时在 127.0.0.1 上 bind 一个临时端口(hetero: 端口 0 让系统分配),
//   通过环境变量 CESIUM_FOCUS_PORT 告诉同进程的托管代码(SDK)端口号。
//   报文格式与旧文件版完全一致(见 request.h / WindowFocus.BuildRequestText):
//     第 1 行: 十进制令牌;  0 / 空 / 非法直接丢弃
//     第 2 行可选: 提示音的绝对路径(UTF-8, 目前只支持 .wav), 与置前同时播放
//     第 3 行可选: "always=1" = 窗口已在前台时也播放提示音(缺省只在后台播放)
//     第 4 行起: 忽略(给将来扩展留位置)。
//
// 为什么不用文件:
//   多开时多个游戏进程通过 junction 共用同一个 AstralParty_ModLoader 目录,
//   request.txt / state.txt 天然是全局共享的——一个进程的置前请求会被另一个进程
//   的加载器抢走, 启动时重置 0 还会把别人的请求清掉。UDP 端口按进程隔离后,
//   每个进程只收自己的那份请求, 不再串号。
//
// 语义:
//   - 提示音默认只在窗口不在前台时播放; 请求带 always=1 时, 前台也每次播放一次;
//   - 游戏窗口已经在前台时只跳过置前(不抢焦点);
//   - 系统前台锁定拒绝置前时闪烁任务栏, 不阻塞游戏线程(提示音照常播放);
//   - 提示音失败(非 WAV / 文件不存在 / 超过 16 MB)只记日志, 置前不受影响;
//   - 任何失败都只记日志, 不影响加载器其余功能。
//
// windowControlEnabled=false 时不启动通道, CESIUM_FOCUS_PORT 不设置,
// SDK 的 IsAvailable 为 false。

#pragma once

#include <string>

// 遗留目录(仅供环境变量 CESIUM_WINDOW_DIR 兼容, 不再承载传输):
// 旧版文件通道用 <游戏目录>\AstralParty_ModLoader\window 存 request/state,
// 新版 UDP 不读任何文件; 保留路径只是让旧版 mod/日志里的路径可查, 不影响功能。
std::wstring windowctl_dir();

// 启动 UDP 监听线程(幂等)。绑定 127.0.0.1:<动态端口>, 端口写入进程环境变量
// CESIUM_FOCUS_PORT; 不抛异常、不阻塞调用者。
// 参数 dir 已不再用于传输, 仅保留签名兼容(调用方传入的 window 目录可忽略)。
void windowctl_start(const std::wstring& dir);

namespace cesium::windowing
{
using ::windowctl_dir;
using ::windowctl_start;
}
