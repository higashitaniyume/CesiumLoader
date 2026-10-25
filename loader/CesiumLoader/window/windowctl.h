// windowctl.h - 窗口置前控制文件通道 (mod <-> 加载器)
//
// 为什么需要它:
//   热更程序集(经 HybridCLR 加载的 SDK / mod)无法 P/Invoke, 调不到 user32!SetForegroundWindow。
//   所以"轮到我的回合时把游戏窗口放到最前"由原生加载器执行, mod 只写一个请求文件。
//
// 目录: <游戏目录>\AstralParty_ModLoader\window\   (环境变量 CESIUM_WINDOW_DIR)
//   request.txt   mod -> 加载器: 每次请求写一个新的十进制令牌(内容变化即触发)
//                 第 2 行可选: 提示音的绝对路径(UTF-8, 目前只支持 .wav), 与置前同时播放
//                 第 3 行可选: "always=1" = 窗口已在前台时也播放提示音(缺省只在后台播放)
//   state.txt     加载器 -> mod: version=<加载器版本>  available=0/1  audio=wav
//
// 语义:
//   - 加载器启动时把 request.txt 重置为 0, 上一次会话残留的请求不会在下次启动时被误执行;
//   - 之后只处理**内容变化**; 内容为 0 / 空 / 非法时忽略;
//   - 提示音默认只在窗口不在前台时播放; 请求带 always=1 时, 前台也每次播放一次;
//   - 游戏窗口已经在前台时只跳过置前(不抢焦点);
//   - 系统前台锁定拒绝置前时闪烁任务栏, 不阻塞游戏线程(提示音照常播放);
//   - 提示音失败(非 WAV / 文件不存在 / 超过 16 MB)只记日志, 置前不受影响;
//   - 任何失败都只记日志, 不影响加载器其余功能。

#pragma once

#include <string>

// 控制文件目录(供环境变量 CESIUM_WINDOW_DIR 使用)。
std::wstring windowctl_dir();

// 启动监听线程(幂等)。不抛异常、不阻塞调用者。
void windowctl_start(const std::wstring& dir);

namespace cesium::windowing
{
using ::windowctl_dir;
using ::windowctl_start;
}
