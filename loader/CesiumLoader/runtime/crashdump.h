// crashdump.h - 原生崩溃转储(致命异常 -> <loader>\crash\<会话>.dmp + 同名 .txt)
//
// 为什么要有它:
//   游戏是 IL2CPP + HybridCLR, 托管调试器挂不上(见 docs/SDK-Diagnostics.md); 原生侧
//   一旦崩在 il2cpp 解析 / inline hook / Steam 绕过这些地方, 现场只剩 Player.log 里的
//   一行, 连"崩在哪个函数"都看不出来。这里在加载器里装一个进程级异常处理器, 致命异常时
//   用 dbghelp 的 MiniDumpWriteDump 落一份 dump, 并在同名 .txt 里写下**发生崩溃时的
//   加载器环境**(版本、路径、配置、游戏程序集指纹、mod 加载结果), 直接用 VS 打开 dmp
//   就能看到线程栈与已加载模块(配合随包分发的 version.pdb / sdk\*.pdb / mods\*\*.pdb)。
//
// 位置: 转储放在加载器目录下的 crash\ 里(不在 logs\ 里 —— 转储是大文件, 和日志分开好找),
//       日志文件仍旧在 logs\。
//
// 边界(有意为之):
//   - 只接管**一定致命**的原生码(访问冲突/非法指令/整除零/栈溢出/fastfail/堆损坏);
//     托管异常(0xE0434352)、C++ 异常(0xE06D7363)、调试断点(0x80000003)都不碰 ——
//     它们会被游戏或调试器正常处理, 落 dump 只会制造噪声。
//   - **有调试器附加时不落盘**(交给调试器, 不抢现场)。
//   - 每个进程最多落一次(避免异常处理里再崩导致的递归写盘)。
//   - 环境信息在**正常运行时**就格式化好放进静态缓冲区, 异常处理里只做 CreateFile/WriteFile
//     —— 崩溃时堆和锁都不可信, 不能在那里拼字符串、不能碰 spdlog。
//   - 任何失败都只记日志, 绝不影响游戏启动 —— 转储是排障工具, 不是运行依赖。
#pragma once

#include "config/config.h"

#include <string>

namespace cesium::crashdump
{

// 安装崩溃转储。必须在控制台/日志就绪之后、加载 SDK 与 mod 之前调用; 重复调用是幂等的。
//   cfg          : 当时的加载器配置(写入环境信息; 之后可用 refresh_environment 刷新)
//   enabled      : false = 不安装致命异常处理器, 只把 <loader>\crash 与 dbghelp 准备好
//                  (看门狗的按需转储共用这套设施)
//   full_memory  : true = 连完整内存一起导出(配置 crashDumpFullMemory; 文件可能几 GB)
void install(const LoaderConfig& cfg, bool enabled, bool full_memory);

// 刷新"崩溃时写进 .txt 的环境信息"。版本号/mod 列表这类信息比 install 更晚才就绪,
// 所以在下面这些时机再调一次即可(调用是廉价的, 不安装转储时是空操作):
//   - 拿到游戏/Unity 版本之后
//   - mod 加载报告生成之后
void refresh_environment();

// 转储目录(<loader>\crash); 未初始化时为空。
std::wstring crash_dir();

// 按需写一份转储(不依赖异常): 看门狗发现主线程停滞、或探针手动取证时调用。
// 写 `<loader>\crash\<kind>-<会话>[-n].dmp` 与同名 .txt(摘要 + [request] + [threads] + 环境信息)。
//   kind      : 文件名前缀, 只保留 [A-Za-z0-9_-], 空则用 "manual"
//   source    : 请求来源(mod id / "native-watchdog"), 写进 .txt
//   note      : 备注(可为 nullptr)
//   stall_ms  : 已停滞毫秒数(0 = 不适用)
//   rip / rsp : 采样到的现场(0 = 未知)
//   main_tid  : 主线程 id(0 = 未知)
// 返回写成的 .dmp 路径; 空 = 失败(未初始化 / 已有转储在写 / 写盘失败)。
// 线程安全: 与异常转储互斥, 同一时刻只写一份。
std::wstring write_dump_now(const char* kind, const char* source, const char* note,
                            long long stall_ms, unsigned long long rip, unsigned long long rsp,
                            unsigned long main_tid);

} // namespace cesium::crashdump
