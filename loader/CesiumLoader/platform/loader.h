// loader.h - Astral Party Mod Loader (version.dll 劫持, Doorstop 式) 公共头
//
// 原理:UnityPlayer.dll 在进程启动时依赖 version.dll(导入 GetFileVersionInfoSizeA /
// GetFileVersionInfoA / VerQueryValueA, 经 dumpbin 确认), 而 version.dll 不在
// KnownDLLs 列表——把本 DLL 命名为 version.dll 放在游戏 exe 同目录,
// Windows 加载器会优先加载它(BepInEx/Doorstop 生态的事实标准代理名)。
//
// 引导:DllMain(DLL_PROCESS_ATTACH) 直接启动后台引导线程(开头 Sleep 避开
// loader lock), 流程:
//   1. 读取 doorstop_config.json(enabled 开关/超时/控制台)
//   2. 等待 GameAssembly.dll + IL2CPP + HybridCLR 热更就绪
//   3. 原生加载 sdk\*.dll → mods\{ModId}\{ModId}.dll → 调用 {文件名}.ModEntry.Main()
//      (useManagedBootstrap=true 时改走托管 Bootstrap 编排, 实验特性)
//   4. 启动 activity-mod-<会话>.log → 控制台 转发线程
//
// 所有 version.dll 导出函数转发到系统 C:\Windows\System32\version.dll,
// 保证 UnityPlayer 读取 exe 版本信息等行为正常。
// 附加能力: 变速引擎(speedhack) inline hook 系统时间函数, SDK 可经
// ap_speed_* 导出控制倍率。

#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <cstdint>
#include <string>

// ---------- 日志 ----------
// 控制台与文件的行为约定:
//   - 控制台**按窗口宽度折行**: 以 "[tag] " 开头的行, 标签固定占 kTagColumns 列并置暗灰,
//     正文按调用方设置的颜色; 超宽时在空格/路径分隔符处折行, 续行缩进对齐正文列。
//   - 文件里按原样写 "[tag] 消息"(spdlog 补时间戳), 行内容不折行 —— 文件是给 grep 用的。
//   - 空串 = 只输出一个控制台空行(文件里不留带时间戳的空行)。
void log_line(const char* msg);
void log_line(const std::string& msg);
void log_line(const std::wstring& msg);

// ---------- 彩色分级日志(行业标准风格) ----------
// 颜色码 = Windows 控制台文本属性(FOREGROUND_* 组合)。
// 用法: console_set_color(LOG_CYAN); log_line("..."); console_set_color(LOG_DEFAULT);
// 配色约定(改日志时按角色选, 不要随手挑颜色):
//   LOG_DIM      路径/目录/次要说明/禁用项 —— 视觉上退到背景
//   LOG_CYAN     分组标题、分隔线、mod 名
//   LOG_WHITE    正文强调(版本号、计数、总结)
//   LOG_GREEN    成功; LOG_RED 失败; LOG_YELLOW 警告/被跳过
//   LOG_MAGENTA  加载器自身的版本/会话等标识
//   LOG_BLUE     预留(深蓝在黑底上对比度低, 谨慎使用)
enum LogColor : WORD
{
    LOG_DEFAULT = 0x07,   // 灰白(默认)
    LOG_DIM     = 0x08,   // 暗灰(次要信息)
    LOG_BLUE    = 0x09,   // 蓝
    LOG_GREEN   = 0x0A,   // 亮绿(成功)
    LOG_CYAN    = 0x0B,   // 亮青(标题/分组)
    LOG_RED     = 0x0C,   // 亮红(失败/错误)
    LOG_MAGENTA = 0x0D,   // 品红(加载器标识)
    LOG_YELLOW  = 0x0E,   // 亮黄(警告)
    LOG_WHITE   = 0x0F,   // 亮白(强调)
};
void console_set_color(WORD attr);   // 设置控制台前景色(仅在已初始化时生效)

// ---------- 显示辅助 ----------
// 控制台窗口列数(取不到/无控制台时返回 0, 此时不折行)。console_init 时缓存。
int console_width();

// UTF-8 文本的显示列宽(中文/emoji 记 2 列), 表格对齐用。
int text_width(const std::string& utf8);

// PE 文件版本资源: 优先 ProductVersion 字符串(如 "Unity 2022.3.62f3 (hash)"),
// 退回 FileVersion, 再退回固定版本号; 读不到返回空串。只用于展示, 失败不致命。
std::wstring file_version(const std::wstring& path);

// 把绝对路径压成短形式: <loader>\... / <game>\... / %LOCALAPPDATA%\...。
// 不改变语义, 只为让控制台一行放得下; 真正的绝对根目录由启动块打印一次。
std::wstring short_path(const std::wstring& path);

// ---------- 控制台 ----------
void console_init(bool topmost);
void console_write(const char* utf8_msg);
void console_write_w(const wchar_t* msg, size_t len);

// 控制台是否可用(AllocConsole 成功且拿到了输出句柄)。
bool console_available();

// 设置/取消控制台窗口置顶(只对可见的传统控制台窗口生效; shell 的 set loader.consoleTopmost 用)。
void console_set_topmost(bool topmost);

// 写入钩子: 每次 console_write / console_write_w 真正输出**前后**调用, 调用时持有控制台写锁。
// 调试 shell 用它在日志行之间擦掉/重画输入提示符。传 nullptr 注销。
void console_set_write_hooks(void (*pre)(), void (*post)());

// 控制台写锁(可重入)。shell 自己操作光标/填充字符时用它和日志输出互斥。
void console_lock();
void console_unlock();


// ---------- 路径 ----------
std::wstring loader_root();
std::wstring bootstrap_dir();
std::wstring mods_dir();
std::wstring sdk_dir();
std::wstring logs_dir();
std::wstring config_path();

// ---------- 转发基础设施(供 exports.cpp 使用) ----------
void maybe_start_boot();                    // 首次调用时启动引导线程
void* real_version_handle();                // 系统 version.dll 模块句柄
void* real_version_fn(const char* name);    // 解析系统 version 导出函数地址

// ---------- 导出(保留, 兼容; 经 __declspec(dllexport)) ----------
extern "C" __declspec(dllexport) void WINAPI ap_console_write(const char* msg);
extern "C" __declspec(dllexport) void WINAPI ap_console_write_w(const wchar_t* msg, size_t len);

// ---------- 导出(变速引擎; 经 version.def) ----------
// SDK 通过 P/Invoke 调用这些导出设置/查询游戏变速倍率。
extern "C" BOOL WINAPI ap_speed_set(double speed);
extern "C" double WINAPI ap_speed_get();
extern "C" BOOL WINAPI ap_speed_active();

// ---------- IL2CPP 桥(boot 线程使用) ----------
struct Il2Cpp;
bool boot_il2cpp_and_load_mods();

namespace cesium::platform
{
using ::LogColor;
using ::bootstrap_dir;
using ::config_path;
using ::console_available;
using ::console_init;
using ::console_lock;
using ::console_set_color;
using ::console_set_topmost;
using ::console_set_write_hooks;
using ::console_unlock;
using ::console_write;
using ::console_write_w;
using ::loader_root;
using ::log_line;
using ::logs_dir;
using ::mods_dir;
using ::sdk_dir;
}

namespace cesium::runtime
{
using ::boot_il2cpp_and_load_mods;
}
