// update.h - 加载器更新检查(见 update.cpp 顶部注释)
//
// 为什么在**原生层**做这件事:
//   热更程序集(经 HybridCLR 加载的 SDK / mod)的 BCL 是裁剪过的, TLS/证书链(HttpClient /
//   WebRequest / SslStream)在游戏里没有可靠先例; 而加载器本体是原生 DLL, 用系统
//   WinHTTP 走 HTTPS 是纯 Win32 能力, 不需要游戏提供任何东西。所以"联网"留在原生层,
//   "显示"交给 mod —— 两者通过 <loader>\update\state.json 与本地 UDP 通道交接。
//
// 对外三条通道:
//   1) 控制台/日志: 检查开始、结果、跳过原因都写一行 [update] …, 不阻塞引导线程;
//   2) 状态文件: <loader>\update\state.json(原子替换, UTF-8 JSON), mod 只读这个文件;
//   3) UDP IPC: 127.0.0.1 临时端口(CESIUM_UPDATE_PORT), mod 用它请求强制检查/打开浏览器。
//
// 失败语义: 任何失败都只写一行日志 + error 状态, 不影响游戏与加载流程。

#pragma once

#include "config/config.h"

#include <string>

namespace cesium::update
{

// 更新检查状态目录: <loader>\update
std::wstring update_dir(const std::wstring& loader_root);

// 启动后台检查(幂等)。
//   - cfg.updateCheckEnabled=false 时只写一份 disabled 状态, 不联网;
//   - 距上次成功/失败检查不足 updateCheckIntervalHours 时复用 state.json 的缓存结果,
//     不重复联网(用 request_check() 可以强制忽略该间隔);
//   - 同时启动 UDP IPC(端口写入环境变量 CESIUM_UPDATE_PORT)。
void start(const std::wstring& loader_root, const LoaderConfig& cfg);

// 强制重新检查(忽略冷却)。后台执行, 立即返回。
void request_check();

// 最近一次检查的摘要(线程安全拷贝), 供 shell 的 update/status 命令使用。
struct Summary
{
    bool enabled = false;
    bool checking = false;
    std::string status;             // disabled|checking|update-available|up-to-date|error|cooldown
    std::string current_version;
    std::string latest_version;
    std::string release_tag;
    std::string release_date;
    std::string release_url;
    std::string source;             // direct|mirror|""
    std::string error;
    unsigned long long checked_at_unix = 0;
};

Summary summary();

// 进程内打开一个 https 链接(只接受 GitHub 与已配置镜像的域名)。供 shell 的 update open 用。
bool open_url(const std::string& url);

// 版本比较(a 比 b 新返回 >0, 相等 0, 更旧 <0)。只比较前三段数字,
// 因此 "3.8.3-android-mobile" 与 "3.8.3" 相等。
int compare_versions(const std::string& a, const std::string& b);

} // namespace cesium::update
