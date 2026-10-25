// shell.h - 加载器调试 shell
//
// 在加载器已经分配的控制台里提供交互命令: 查环境/配置、改配置、看 mod 状态、
// 调变速、看日志、结束进程。运行在独立线程, 通过 console 的写入钩子在日志行
// 之间重绘提示符。
//
// 约束(与加载器其它部分一致):
//   - 只碰原生状态(config / speedhack / runtime state / 文件), 不调用 il2cpp/Unity;
//   - 任何命令失败都只打印错误, 不影响游戏与加载流程;
//   - 没有控制台时 shell_start 直接返回 false。

#pragma once

#include <string>

namespace cesium::shell
{

// 启动 shell 线程(幂等)。返回值: 是否已启动。
bool shell_start();

// 是否正在运行(控制台输入线程活着)。
bool shell_running();

// 请求退出 shell 线程(游戏继续运行)。
void shell_request_stop();

} // namespace cesium::shell
