// loader.h - CesiumLoader 加载器核心 (CesiumLoader.dll) 公共头
//
// 分工(详见 loader\shared\cesium_core_abi.h 的契约):
//   * 薄代理 version.dll (loader\CesiumLoader.Proxy): 转发系统 version.dll 的 15 个导出,
//     并在后台线程里加载本 DLL、比对 ABI、调用 CesiumLoaderStart()。原理: UnityPlayer.dll
//     在进程启动时依赖 version.dll(导入 GetFileVersionInfoSizeA / GetFileVersionInfoA /
//     VerQueryValueA, 经 dumpbin 确认), 而 version.dll 不在 KnownDLLs 列表 —— 把代理命名为
//     version.dll 放在游戏 exe 同目录, Windows 加载器会优先加载它(BepInEx/Doorstop 生态的
//     事实标准代理名)。
//   * 本 DLL(加载器核心): 配置 / 变速 / fsredir / profile / steam 与 taptap 绕过 / shell /
//     更新检查 / 崩溃转储 / 控制台 / 音频与窗口通道 / IL2CPP 桥 / 托管模组编排。
//
// 引导(DLL_PROCESS_ATTACH 之后由代理调用 CesiumLoaderStart(), 幂等):
//   1. 读取 doorstop_config.json(enabled 开关/超时/控制台)
//   2. 等待 GameAssembly.dll + IL2CPP + HybridCLR 热更就绪
//   3. 原生加载 sdk\*.dll → mods\{ModId}\{ModId}.dll → 调用 {文件名}.ModEntry.Main()
//      (useManagedBootstrap=true 时改走托管 Bootstrap 编排, 实验特性)
//   4. 启动 activity-mod-<会话>.log → 控制台 转发线程
//
// 附加能力: 变速引擎(speedhack) inline hook 系统时间函数, 原生调用方可经
// ap_speed_* 导出控制倍率(代理会把这三个导出桥接过来)。

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

// UTF-16 文本的显示列宽(同一套宽度规则; shell 的光标定位要按列算)。
int text_width_w(const std::wstring& text);

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

// 一行里前缀与正文分开着色(前缀不参与正文颜色), 用于 mod 日志转发:
// "[时间] [级别] " 置暗, 正文按级别取色。空前缀等价于 console_write(msg)。
void console_write_styled(const char* utf8_prefix, const char* utf8_body,
                          WORD prefix_color, WORD body_color);

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

// ---------- 引导(由代理层经 CesiumLoaderStart 触发) ----------
void maybe_start_boot();                    // 幂等: 只启动一次引导线程

// ---------- 导出(保留, 兼容; 经 __declspec(dllexport)) ----------
// 这三个是旧版本(单 DLL 时代)的原生附加接口; 现在由代理层桥接过来, 名字与签名保持不变。
extern "C" __declspec(dllexport) void WINAPI ap_console_write(const char* msg);
extern "C" __declspec(dllexport) void WINAPI ap_console_write_w(const wchar_t* msg, size_t len);

// ---------- 导出(代理层契约; 见 loader/shared/cesium_core_abi.h) ----------
extern "C" __declspec(dllexport) unsigned int WINAPI CesiumLoaderAbiVersion();
extern "C" __declspec(dllexport) BOOL WINAPI CesiumLoaderStart();

// ---------- 导出(变速引擎; 由代理层的 ap_* 桥接调用) ----------
// SDK 与热更模组不走这里(热更程序集无法 P/Invoke), 它们用 speed\ 下的文件 / UDP 通道。
extern "C" __declspec(dllexport) BOOL WINAPI ap_speed_set(double speed);
extern "C" __declspec(dllexport) double WINAPI ap_speed_get();
extern "C" __declspec(dllexport) BOOL WINAPI ap_speed_active();

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
using ::console_write_styled;
using ::console_write_w;
using ::loader_root;
using ::log_line;
using ::logs_dir;
using ::mods_dir;
using ::sdk_dir;
using ::text_width_w;
}

namespace cesium::runtime
{
using ::boot_il2cpp_and_load_mods;
}
