// logging.h - 加载器日志(由 spdlog 驱动, 实现在 logging.cpp)
//
// 分工说明(为什么控制台没走 spdlog):
//   - **文件日志**用 spdlog 的 basic_file_sink: 句柄常开、线程安全、统一时间戳格式、每行落盘。
//     旧的 log_line 是"每行 CreateFileW + WriteFile + CloseHandle", 已被替换。
//   - **控制台输出**仍走 Win32(console.cpp)。这是**有意**的: 加载器要的是"每行任意颜色"的
//     语义色板(标题青 / 成功绿 / 警告黄 / 失败红 / 次要暗灰 / 默认灰白), 而 spdlog 的
//     wincolor_sink 按**日志级别**着色, 表达不了这套色板, 硬套会让 mod 加载报告可读性变差。
//     控制台写入本来就是平台 API, 不属于"自己造轮子"。
//
// 本头文件**不包含 spdlog**(保持编译期依赖只落在 logging.cpp 一个 TU 里)。

#pragma once

#include <string>

namespace cesium::log
{

// 加载器日志 -> <logs>\cesium-loader.log(追加写)。
// 首次调用时惰性打开文件句柄; 线程安全。路径含中文也能正常写入(内部用宽字符 API)。
// 任何失败都静默降级(不抛异常、不影响游戏启动)。
void write(const std::string& msg);
void write(const std::wstring& msg);

// 立即落盘(正常路径每条日志都会落盘, 这里只在需要强制同步时调用)
void flush();

// 释放日志句柄。进程退出时不必调用(故意不注册析构, 避免退出期与其它线程竞争)。
void shutdown();

// mod 加载/入口失败 -> <logs>\mod-errors.log(与托管 SDK 的 SdkLog.ReportCrash 写同一文件)。
// 行格式与旧实现保持一致:
//     [yyyy-MM-dd HH:mm:ss.fff] [ModName] 原因
//     ------------------------------------------------------------
void mod_error(const std::wstring& logs_dir, const std::string& mod_name, const std::string& reason);

} // namespace cesium::log
