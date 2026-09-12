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
//   3. 原生加载 sdk\*.dll → mods\*.dll → 调用 {文件名}.ModEntry.Main()
//      (useManagedBootstrap=true 时改走托管 Bootstrap 编排, 实验特性)
//   4. 启动 activity-mod.log → 控制台 转发线程
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
void log_line(const char* msg);
void log_line(const std::string& msg);
void log_line(const std::wstring& msg);

// ---------- 控制台 ----------
void console_init(bool topmost);
void console_write(const char* utf8_msg);
void console_write_w(const wchar_t* msg, size_t len);

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
