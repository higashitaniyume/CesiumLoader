// logging.cpp - 加载器日志实现(spdlog)
//
// ★ 关于本文件的两个编译期开关(改之前请先读完)★
//
// 1) **header-only**(不定义 SPDLOG_COMPILED_LIB):
//    spdlog 的 common.h 在未定义 COMPILED_LIB 时会自动启用 SPDLOG_HEADER_ONLY, 于是所有模板
//    都在本 TU 内实例化。这一点是下面第 2 条的前提 —— SPDLOG_WCHAR_FILENAMES 会改变
//    spdlog::filename_t 与 basic_file_sink/file_helper 的布局, 若与 vcpkg 预编译的 spdlog.lib
//    里的实例化混在一起, 就是 ODR / 对象布局冲突(可能直接内存损坏)。
//    所以**不要**在工程属性里加 SPDLOG_COMPILED_LIB。
//
// 2) **SPDLOG_WCHAR_FILENAMES**:
//    让 file_helper 走 _wfopen 而不是 fopen。加载器日志路径可能是中文(游戏装在中文目录, 或
//    CESIUM_LOG_DIR 指向中文路径), 旧的 CreateFileW 本来就是宽字符 API, 一旦换成窄字符 fopen
//    就会**直接写不出日志** —— 那是功能倒退。spdlog 默认不定义这个宏, 必须显式打开。
#define SPDLOG_WCHAR_FILENAMES 1

#include "logging.h"

#include "loader.h"   // logs_dir()

#include <spdlog/sinks/basic_file_sink.h>
#include <spdlog/spdlog.h>

#include <windows.h>

#include <memory>
#include <mutex>
#include <string>

namespace
{

// 行格式: [本地时间.毫秒] 消息。
// 旧实现写的是**不带时间戳**的纯文本; 这里补上时间戳 —— 排查启动时序问题时没有时间戳基本没法用。
// (%e = 毫秒, spdlog 的本地时间格式化)
constexpr const char* kLinePattern = "[%Y-%m-%d %H:%M:%S.%e] %v";

struct State
{
    std::mutex mtx;
    std::shared_ptr<spdlog::logger> loader;   // cesium-loader.log
    std::shared_ptr<spdlog::logger> errors;   // mod-errors.log
    std::wstring errors_path;                 // errors 当前对应路径(避免重复建 sink)
    bool loader_failed = false;               // 建 sink 失败后不再反复重试
    bool errors_failed = false;
};

// 故意 new 出来且不 delete: 进程/DLL 退出时其它线程(如 mod 日志转发线程)可能仍在写日志,
// 让静态析构与它们竞争会崩。泄漏几十字节换"退出期不崩"是划算的。
State& state()
{
    static State* s = new State();
    return *s;
}

std::string to_utf8(const std::wstring& w)
{
    if (w.empty()) return std::string();
    const int len = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), static_cast<int>(w.size()),
                                       nullptr, 0, nullptr, nullptr);
    if (len <= 0) return std::string();
    std::string out(static_cast<size_t>(len), '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), static_cast<int>(w.size()),
                        out.data(), len, nullptr, nullptr);
    return out;
}

void ensure_parent_dir(const std::wstring& path)
{
    const size_t pos = path.find_last_of(L"\\/");
    if (pos != std::wstring::npos) CreateDirectoryW(path.substr(0, pos).c_str(), nullptr);
}

std::shared_ptr<spdlog::logger> make_file_logger(const std::wstring& path)
{
    ensure_parent_dir(path);
    auto sink = std::make_shared<spdlog::sinks::basic_file_sink_mt>(path, /*truncate=*/false);
    sink->set_pattern(kLinePattern);
    auto lg = std::make_shared<spdlog::logger>("cesium", sink);
    lg->set_level(spdlog::level::trace);    // 全部记录, 不做级别过滤
    lg->flush_on(spdlog::level::trace);     // 每条都落盘: 启动期崩溃时不能丢日志
    return lg;
}

void ensure_loader_locked(State& s)
{
    if (s.loader || s.loader_failed) return;
    try
    {
        s.loader = make_file_logger(logs_dir() + L"\\cesium-loader.log");
    }
    catch (...)
    {
        s.loader_failed = true;   // 只读目录/权限不足等: 静默降级, 绝不阻止游戏启动
    }
}

void ensure_errors_locked(State& s, const std::wstring& dir)
{
    const std::wstring path = dir + L"\\mod-errors.log";
    if (s.errors_path == path && (s.errors || s.errors_failed)) return;
    s.errors_path = path;
    s.errors_failed = false;
    s.errors.reset();
    try
    {
        s.errors = make_file_logger(path);
    }
    catch (...)
    {
        s.errors_failed = true;
    }
}

} // namespace

namespace cesium::log
{

void write(const std::string& msg)
{
    State& s = state();
    std::lock_guard<std::mutex> lock(s.mtx);
    ensure_loader_locked(s);
    if (!s.loader) return;
    try
    {
        // 用 "{}" + 参数, 而不是把 msg 当格式串 —— 消息里可能含 { }(JSON 片段、异常文本),
        // 当格式串会让 fmt 抛异常。
        s.loader->info("{}", msg);
    }
    catch (...) {}
}

void write(const std::wstring& msg)
{
    write(to_utf8(msg));
}

void flush()
{
    State& s = state();
    std::lock_guard<std::mutex> lock(s.mtx);
    if (s.loader) { try { s.loader->flush(); } catch (...) {} }
    if (s.errors) { try { s.errors->flush(); } catch (...) {} }
}

void shutdown()
{
    State& s = state();
    std::lock_guard<std::mutex> lock(s.mtx);
    if (s.loader) { try { s.loader->flush(); } catch (...) {} }
    if (s.errors) { try { s.errors->flush(); } catch (...) {} }
    s.loader.reset();
    s.errors.reset();
    s.loader_failed = false;
    s.errors_failed = false;
    s.errors_path.clear();
}

void mod_error(const std::wstring& dir, const std::string& mod_name, const std::string& reason)
{
    State& s = state();
    std::lock_guard<std::mutex> lock(s.mtx);
    ensure_errors_locked(s, dir);
    if (!s.errors) return;
    try
    {
        // 与旧实现完全相同的行格式: [时间] [mod] 原因 + 60 个 '-'
        s.errors->error("[{}] {}\n{}", mod_name, reason, std::string(60, '-'));
    }
    catch (...) {}
}

} // namespace cesium::log
