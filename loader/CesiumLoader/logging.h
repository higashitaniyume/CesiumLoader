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

// ---------- 本次启动的日志文件(每次启动一套, 不共用) ----------
//
// **每次启动(每个进程)各写一套自己的日志文件**, 而不是所有启动都往同一批文件里追加:
// 连续启动 / 双开时, 旧行为会把几段引导记录交错进同一个文件, 分不清哪一行是哪一次启动,
// 也无法从文件判断当时到底起了几个进程。
//
// 同一次启动产生的所有日志文件共用同一个**会话标识** `<yyyyMMdd>-<HHmmss>-<pid>`:
//
//     cesium-loader-<sid>.log     加载器引导日志(原生, 本文件负责)
//     activity-mod-<sid>.log      mod / SDK 日志(托管侧写, 加载器 tail 它转发到控制台)
//     mod-errors-<sid>.log        mod 崩溃堆栈(原生与托管都会写)
//
//   - 时间戳: 按文件名排序即按启动先后;
//   - pid: 同一秒内启动的多个实例(双开)不会撞名 —— 加载器跑在游戏进程里, pid 就是游戏进程号;
//   - 万一仍撞名(如 pid 复用), 会话标识追加 `-2` / `-3` … 序号, **绝不覆盖**已有日志。
//
// 会话标识在首次调用时确定并缓存, 同一进程内恒定不变(即使 shutdown() 之后重新打开也是同一套文件)。

// 本次启动的会话标识(不含目录与扩展名)。
std::wstring session_id();

// 下面三个都在给定目录下, 文件名用同一个会话标识。dir 一般是 logs_dir()。
std::wstring loader_log_path();
std::wstring error_log_path(const std::wstring& dir);
std::wstring activity_log_path(const std::wstring& dir);

// 加载器日志 -> loader_log_path()(首次调用时惰性打开文件句柄)。
// 线程安全。路径含中文也能正常写入(内部用宽字符 API)。任何失败都静默降级(不抛异常、不影响游戏启动)。
void write(const std::string& msg);
void write(const std::wstring& msg);

// 立即落盘(正常路径每条日志都会落盘, 这里只在需要强制同步时调用)
void flush();

// 释放日志句柄。进程退出时不必调用(故意不注册析构, 避免退出期与其它线程竞争)。
// 注意: **不会**重置会话标识 —— 本次启动的日志文件在进程整个生命周期内是同一套。
void shutdown();

// mod 加载/入口失败 -> <logs>\mod-errors-<sid>.log
// (与托管 SDK 的 SdkLog.ReportCrash 写同一个文件 —— 托管侧通过 CESIUM_ERROR_LOG_FILE 拿到同一路径)。
// 行格式与旧实现保持一致:
//     [yyyy-MM-dd HH:mm:ss.fff] [ModName] 原因
//     ------------------------------------------------------------
void mod_error(const std::wstring& logs_dir, const std::string& mod_name, const std::string& reason);

} // namespace cesium::log
