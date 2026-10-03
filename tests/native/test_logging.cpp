// test_logging.cpp - P3 验证: 加载器文件日志(spdlog)
//
// 重点验证五件事(都是"推理不可靠、必须实测"的):
//   1) **中文路径**能写出日志。spdlog 默认 filename_t = std::string + fopen, 中文路径会直接
//      写不出文件; 本项目在 logging.cpp 里开了 SPDLOG_WCHAR_FILENAMES 走 _wfopen。
//      旧实现用的是 CreateFileW, 本来就是宽字符 —— 这里必须确认没退化。
//   2) **每次启动一套日志文件**: 文件名带会话标识 <yyyyMMdd>-<HHmmss>-<pid>,
//      引导日志 / activity 日志 / 错误日志三个文件共用同一个标识, 且**不再**生成
//      共享的 cesium-loader.log、activity-mod.log、mod-errors.log
//      (多开/连续启动时旧行为会把几段记录交错进同一个文件, 分不清是哪一次启动)。
//   3) 行格式: [yyyy-MM-dd HH:mm:ss.fff] 消息(spdlog 的 %Y-%m-%d %H:%M:%S.%e)
//   4) mod-errors 的行格式与旧实现一致: [时间] [mod] 原因 + 60 个 '-'
//   5) 目录不可用时不崩(降级为不写日志)
//
// 链接 logging.cpp + console.cpp(提供 logs_dir(), 并让 log_line 走完整路径)。

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

#include "../../loader/CesiumLoader/loader.h"
#include "../../loader/CesiumLoader/logging.h"

#include <algorithm>
#include <fstream>
#include <iostream>
#include <string>

namespace
{
int g_fail = 0;

void fail(const std::string& what)
{
    ++g_fail;
    std::cout << "  [FAIL] " << what << "\n";
}

std::wstring read_text_w(const std::wstring& path)
{
    std::ifstream in(path, std::ios::binary);
    if (!in) return std::wstring();
    std::string bytes((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
    if (bytes.empty()) return std::wstring();
    int len = MultiByteToWideChar(CP_UTF8, 0, bytes.data(), (int)bytes.size(), nullptr, 0);
    std::wstring out(len, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, bytes.data(), (int)bytes.size(), out.data(), len);
    return out;
}

bool contains(const std::wstring& hay, const std::wstring& needle)
{
    return hay.find(needle) != std::wstring::npos;
}

// 校验前缀形如 "[2026-09-22 19:27:48.123] "
bool has_timestamp_prefix(const std::wstring& line)
{
    if (line.size() < 26) return false;
    if (line[0] != L'[' || line[24] != L']' || line[25] != L' ') return false;
    const wchar_t sep[19] = { L'-', L'-', L' ', L':', L':', L'.' };
    const int pos[6] = { 5, 8, 11, 14, 17, 20 };
    for (int i = 0; i < 6; i++)
        if (line[pos[i]] != sep[i]) return false;
    for (int i = 1; i <= 23; i++)
    {
        if (i == 5 || i == 8 || i == 11 || i == 14 || i == 17 || i == 20) continue;
        if (line[i] < L'0' || line[i] > L'9') return false;
    }
    return true;
}

std::wstring first_line(const std::wstring& text)
{
    size_t p = text.find(L'\n');
    std::wstring line = (p == std::wstring::npos) ? text : text.substr(0, p);
    if (!line.empty() && line.back() == L'\r') line.pop_back();
    return line;
}

// 会话标识是否是 <8位日期>-<6位时间>-<pid>[-序号], 且 pid 就是本进程
bool matches_session_id(const std::wstring& sid, unsigned long expected_pid)
{
    const size_t least = 8 + 1 + 6 + 1 + 1;   // 日期-时间-pid(至少 1 位)
    if (sid.size() < least) return false;

    size_t i = 0;
    for (int k = 0; k < 8; ++k, ++i)
        if (sid[i] < L'0' || sid[i] > L'9') return false;
    if (sid[i++] != L'-') return false;
    for (int k = 0; k < 6; ++k, ++i)
        if (sid[i] < L'0' || sid[i] > L'9') return false;
    if (sid[i++] != L'-') return false;

    unsigned long pid = 0;
    int digits = 0;
    while (i < sid.size() && sid[i] >= L'0' && sid[i] <= L'9')
    {
        pid = pid * 10 + (unsigned long)(sid[i] - L'0');
        ++i;
        ++digits;
    }
    if (digits == 0 || pid != expected_pid) return false;

    // 允许可选的 "-序号"(撞名时的兜底)
    if (i == sid.size()) return true;
    if (sid[i++] != L'-') return false;
    int suffix = 0;
    while (i < sid.size() && sid[i] >= L'0' && sid[i] <= L'9') { ++i; ++suffix; }
    return suffix > 0 && i == sid.size();
}

bool file_exists(const std::wstring& path)
{
    return GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES;
}
} // namespace

int main()
{
    std::cout << "== P3: 加载器文件日志(spdlog) ==\n";

    // 中文目录名: 这是本测试的核心 —— 窄字符 fopen 路径会在这里失败。
    wchar_t tmp[MAX_PATH] = {};
    GetTempPathW(MAX_PATH, tmp);
    const std::wstring dir = std::wstring(tmp) + L"cesium日志测试\\";
    // 改动前的固定文件名: 现在**一个都不该再生成**
    const std::wstring oldLoader = dir + L"cesium-loader.log";
    const std::wstring oldActivity = dir + L"activity-mod.log";
    const std::wstring oldErrors = dir + L"mod-errors.log";

    CreateDirectoryW(dir.c_str(), nullptr);
    DeleteFileW(oldLoader.c_str());
    DeleteFileW(oldActivity.c_str());
    DeleteFileW(oldErrors.c_str());

    // logs_dir() 会读这个环境变量(console.cpp)
    SetEnvironmentVariableW(L"CESIUM_LOG_DIR", dir.c_str());

    // ---- 0) 本次启动的三个日志路径: 同目录 + 同一个会话标识 + 各自的前缀 ----
    const std::wstring sid = cesium::log::session_id();
    const std::wstring loaderLog = cesium::log::loader_log_path();
    const std::wstring activityLog = cesium::log::activity_log_path(dir);
    const std::wstring errorLog = cesium::log::error_log_path(dir);
    {
        if (!matches_session_id(sid, GetCurrentProcessId()))
        {
            fail("会话标识不是 <yyyyMMdd>-<HHmmss>-<pid>");
            std::wcout << L"         实际: " << sid << L"\n";
        }
        struct Expected { const wchar_t* prefix; const std::wstring* actual; const char* what; };
        const Expected expected[3] = {
            { L"cesium-loader-", &loaderLog, "引导日志" },
            { L"activity-mod-", &activityLog, "activity 日志" },
            { L"mod-errors-", &errorLog, "错误日志" },
        };
        for (const Expected& e : expected)
        {
            const std::wstring want = dir + L"\\" + e.prefix + sid + L".log";
            if (*e.actual != want)
            {
                fail(std::string(e.what) + "路径不符合 <日志目录>\\<前缀><会话标识>.log");
                std::wcout << L"         期望: " << want << L"\n         实际: " << *e.actual << L"\n";
            }
        }
        // 同一进程内必须恒定(首次确定后缓存), 否则"一次启动一套文件"会被打破
        if (cesium::log::session_id() != sid || cesium::log::loader_log_path() != loaderLog)
            fail("会话标识/路径在同一进程内应恒定");
        DeleteFileW(loaderLog.c_str());
        DeleteFileW(errorLog.c_str());
    }

    // ---- 1) log_line: 控制台(无控制台时静默跳过) + 文件 ----
    log_line("测试消息 hello");
    log_line(std::string("第二行: 带 {花括号} 与 JSON {\"a\":1} 的消息"));
    log_line(std::wstring(L"宽字符串重载: 中文正常"));
    cesium::log::mod_error(dir, "TestMod", "加载失败: 中文原因");
    cesium::log::flush();

    // ---- 2) 文件确实写出来了(中文路径), 且**没有**生成旧的固定文件名 ----
    const std::wstring logText = read_text_w(loaderLog);
    if (logText.empty())
    {
        fail("引导日志没写出来(中文路径?) —— 检查 SPDLOG_WCHAR_FILENAMES");
        std::wcout << L"         期望路径: " << loaderLog << L"\n";
        // 后续检查无意义, 但仍继续跑完以便看到全部结论
    }
    else
    {
        if (!contains(logText, L"测试消息 hello")) fail("日志缺少第一条消息");
        if (!contains(logText, L"第二行: 带 {花括号} 与 JSON {\"a\":1} 的消息"))
            fail("含 { } 的消息未原样写出(格式串注入?)");
        if (!contains(logText, L"宽字符串重载: 中文正常")) fail("std::wstring 重载未写出");
        if (!has_timestamp_prefix(first_line(logText)))
        {
            fail("行格式不是 [yyyy-MM-dd HH:mm:ss.fff] 前缀");
            std::wcout << L"         实际首行: " << first_line(logText) << L"\n";
        }
        const int lines = (int)std::count(logText.begin(), logText.end(), L'\n');
        if (lines != 3) fail("应有 3 行, 实际 " + std::to_string(lines) + " 行");
    }
    if (file_exists(oldLoader)) fail("不应再生成共享的 cesium-loader.log(每次启动应各写一套)");
    if (file_exists(oldActivity)) fail("不应再生成共享的 activity-mod.log(每次启动应各写一套)");

    // ---- 3) 错误日志: 格式与旧实现一致, 且是本次启动的那个文件 ----
    const std::wstring errText = read_text_w(errorLog);
    if (errText.empty())
    {
        fail("mod-errors-<会话>.log 没写出来");
        std::wcout << L"         期望路径: " << errorLog << L"\n";
    }
    else
    {
        const std::wstring l1 = first_line(errText);
        if (!has_timestamp_prefix(l1)) fail("错误日志首行缺少时间戳前缀");
        if (!contains(l1, L"[TestMod] 加载失败: 中文原因")) fail("错误日志缺少 [mod] 原因");
        if (!contains(errText, std::wstring(60, L'-'))) fail("错误日志缺少 60 个 '-' 分隔线");
    }
    if (file_exists(oldErrors)) fail("不应再生成共享的 mod-errors.log(每次启动应各写一套)");

    // ---- 4) 目录不可用/不存在时不崩(降级为不写日志) ----
    //     注意: 本次启动的日志路径在首次用时已经定下, 之后改 CESIUM_LOG_DIR **不会**换文件
    //     (这正是"一次启动一套"的要求), 所以这里只验证坏路径不会抛异常/崩溃。
    SetEnvironmentVariableW(L"CESIUM_LOG_DIR", L"Z:\\不存在的盘\\nope");
    cesium::log::write("这条不该让程序崩溃");
    cesium::log::mod_error(L"Z:\\不存在的盘\\nope", "M", "R");
    if (cesium::log::session_id() != sid) fail("会话标识在整个进程内必须恒定");

    DeleteFileW(loaderLog.c_str());
    DeleteFileW(errorLog.c_str());
    DeleteFileW(activityLog.c_str());
    RemoveDirectoryW(dir.c_str());

    std::cout << "  失败: " << g_fail << "\n";
    if (g_fail == 0) std::cout << "== 全部通过 ==\n";
    return g_fail == 0 ? 0 : 1;
}
