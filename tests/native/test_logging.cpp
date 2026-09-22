// test_logging.cpp - P3 验证: 加载器文件日志(spdlog)
//
// 重点验证三件事(都是"推理不可靠、必须实测"的):
//   1) **中文路径**能写出日志。spdlog 默认 filename_t = std::string + fopen, 中文路径会直接
//      写不出文件; 本项目在 logging.cpp 里开了 SPDLOG_WCHAR_FILENAMES 走 _wfopen。
//      旧实现用的是 CreateFileW, 本来就是宽字符 —— 这里必须确认没退化。
//   2) 行格式: [yyyy-MM-dd HH:mm:ss.fff] 消息(spdlog 的 %Y-%m-%d %H:%M:%S.%e)
//   3) mod-errors.log 的行格式与旧实现一致: [时间] [mod] 原因 + 60 个 '-'
//
// 链接 logging.cpp + console.cpp(提供 logs_dir(), 并让 log_line 走完整路径)。

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

#include "../../src/CesiumLoader/loader.h"
#include "../../src/CesiumLoader/logging.h"

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
} // namespace

int main()
{
    std::cout << "== P3: 加载器文件日志(spdlog) ==\n";

    // 中文目录名: 这是本测试的核心 —— 窄字符 fopen 路径会在这里失败。
    wchar_t tmp[MAX_PATH] = {};
    GetTempPathW(MAX_PATH, tmp);
    const std::wstring dir = std::wstring(tmp) + L"cesium日志测试\\";
    const std::wstring loaderLog = dir + L"cesium-loader.log";
    const std::wstring errorLog = dir + L"mod-errors.log";

    CreateDirectoryW(dir.c_str(), nullptr);
    DeleteFileW(loaderLog.c_str());
    DeleteFileW(errorLog.c_str());

    // logs_dir() 会读这个环境变量(console.cpp)
    SetEnvironmentVariableW(L"CESIUM_LOG_DIR", dir.c_str());

    // ---- 1) log_line: 控制台(无控制台时静默跳过) + 文件 ----
    log_line("测试消息 hello");
    log_line(std::string("第二行: 带 {花括号} 与 JSON {\"a\":1} 的消息"));
    log_line(std::wstring(L"宽字符串重载: 中文正常"));
    cesium::log::mod_error(dir, "TestMod", "加载失败: 中文原因");
    cesium::log::flush();

    // ---- 2) 文件确实写出来了(中文路径) ----
    const std::wstring logText = read_text_w(loaderLog);
    if (logText.empty())
    {
        fail("cesium-loader.log 没写出来(中文路径?) —— 检查 SPDLOG_WCHAR_FILENAMES");
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

    // ---- 3) mod-errors.log 格式与旧实现一致 ----
    const std::wstring errText = read_text_w(errorLog);
    if (errText.empty())
    {
        fail("mod-errors.log 没写出来");
    }
    else
    {
        const std::wstring l1 = first_line(errText);
        if (!has_timestamp_prefix(l1)) fail("mod-errors.log 首行缺少时间戳前缀");
        if (!contains(l1, L"[TestMod] 加载失败: 中文原因")) fail("mod-errors.log 缺少 [mod] 原因");
        if (!contains(errText, std::wstring(60, L'-'))) fail("mod-errors.log 缺少 60 个 '-' 分隔线");
    }

    // ---- 4) 目录只读/不存在时不崩(降级为不写日志) ----
    SetEnvironmentVariableW(L"CESIUM_LOG_DIR", L"Z:\\不存在的盘\\nope");
    cesium::log::write("这条不该让程序崩溃");
    cesium::log::mod_error(L"Z:\\不存在的盘\\nope", "M", "R");

    DeleteFileW(loaderLog.c_str());
    DeleteFileW(errorLog.c_str());
    RemoveDirectoryW(dir.c_str());

    std::cout << "  失败: " << g_fail << "\n";
    if (g_fail == 0) std::cout << "== 全部通过 ==\n";
    return g_fail == 0 ? 0 : 1;
}
