// crashdump.h - 原生崩溃转储(致命异常 -> <logs>\crash\<会话>.dmp + 同名 .txt)
//
// 为什么要有它:
//   游戏是 IL2CPP + HybridCLR, 托管调试器挂不上(见 docs/SDK-Diagnostics.md); 原生侧
//   一旦崩在 il2cpp 解析 / inline hook / Steam 绕过这些地方, 现场只剩 Player.log 里的
//   一行, 连"崩在哪个函数"都看不出来。这里在加载器里装一个进程级异常处理器, 致命异常时
//   用 dbghelp 的 MiniDumpWriteDump 落一份 dump, 并在同名 .txt 里写下**发生崩溃时的
//   加载器环境**(版本、路径、配置、游戏程序集指纹、mod 加载结果), 直接用 VS 打开 dmp
//   就能看到线程栈与已加载模块(配合随包分发的 version.pdb / sdk\*.pdb / mods\*\*.pdb)。
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
//   logs_dir     : 本次启动的日志目录; 转储写在 <logs_dir>\crash\ 下
//   enabled      : false = 不安装, 只记一条日志(配置 crashDumpEnabled)
//   full_memory  : true = 连完整内存一起导出(配置 crashDumpFullMemory; 文件可能几 GB)
void install(const LoaderConfig& cfg, const std::wstring& logs_dir, bool enabled, bool full_memory);

// 刷新"崩溃时写进 .txt 的环境信息"。版本号/mod 列表这类信息比 install 更晚才就绪,
// 所以在下面这些时机再调一次即可(调用是廉价的, 不安装转储时是空操作):
//   - 拿到游戏/Unity 版本之后
//   - mod 加载报告生成之后
void refresh_environment();

} // namespace cesium::crashdump
