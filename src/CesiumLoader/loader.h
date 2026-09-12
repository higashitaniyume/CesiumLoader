// loader.h - Astral Party Mod Loader (winmm.dll 劫持) 公共头
//
// 原理:UnityPlayer.dll 在进程启动时依赖 winmm.dll(导入 22 个函数)。
// 把本 DLL 命名为 winmm.dll 放在游戏 exe 同目录,Windows 加载器会优先加载它
// (winmm.dll 不在 KnownDLLs 列表)。首个 winmm 导出被调用时启动后台引导线程,
// 等待 GameAssembly.dll + IL2CPP + HybridCLR 热更就绪后,通过
// System.Reflection.Assembly.Load(byte[]) 加载托管 mod。
//
// 所有 winmm 导出函数转发到系统 C:\Windows\System32\winmm.dll。

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
void console_init();
void console_write(const char* utf8_msg);
void console_write_w(const wchar_t* msg, size_t len);

// ---------- 路径 ----------
std::wstring loader_root();
std::wstring mods_dir();
std::wstring sdk_dir();
std::wstring logs_dir();

// ---------- 转发基础设施(供 generated_forwards.cpp 使用) ----------
void maybe_start_boot();                    // 首次调用时启动引导线程
void* real_winmm_handle();                  // 系统 winmm.dll 模块句柄
void* real_winmm_fn(const char* name);      // 解析系统 winmm 导出函数地址

// ---------- 导出(供 C# mod 调用) ----------
extern "C" __declspec(dllexport) void WINAPI ap_console_write(const char* msg);
extern "C" __declspec(dllexport) void WINAPI ap_console_write_w(const wchar_t* msg, size_t len);

// ---------- IL2CPP 桥(boot 线程使用) ----------
struct Il2Cpp;
bool boot_il2cpp_and_load_mods();
