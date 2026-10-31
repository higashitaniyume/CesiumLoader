// shell_commands.cpp - 命令表与命令实现(从 shell.cpp 拆出)
//
// 这里只有"看状态"的命令(status/env/paths/mods/hooks/log/proc/clear/detach/exit/help)
// 与分发逻辑; 配置类命令(get/set/ls/reload)在 shell_config.cpp。

#include "shell/shell_internal.h"

#include "config/config.h"
#include "logging/logging.h"
#include "platform/loader.h"
#include "runtime/state.h"
#include "shell/command.h"
#include "shell/shell.h"
#include "speed/speedctl.h"
#include "speed/speedhack.h"
#include "window/windowctl.h"

#include <tlhelp32.h>
#include <psapi.h>

#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>

#include <fmt/format.h>

namespace cesium::shell::commands
{

using config::configs_dir;
using config::file_exists;
using config::format_double;
using term::g_quit;
using term::narrow;
using term::pad_right;
using term::print;

// ---------- 进程信息 ----------

ULONGLONG now_utc_ms()
{
    FILETIME ft{};
    GetSystemTimeAsFileTime(&ft);
    return ((static_cast<ULONGLONG>(ft.dwHighDateTime) << 32) | ft.dwLowDateTime) / 10000ULL;
}

std::string format_duration(ULONGLONG ms)
{
    const ULONGLONG sec = ms / 1000;
    const ULONGLONG h = sec / 3600, m = (sec % 3600) / 60, s = sec % 60;
    return fmt::format("{}h {:02}m {:02}s", h, m, s);
}

std::string format_bytes(ULONGLONG bytes)
{
    const double mb = static_cast<double>(bytes) / (1024.0 * 1024.0);
    if (mb >= 1024.0) return fmt::format("{:.2f} GB", mb / 1024.0);
    return fmt::format("{:.1f} MB", mb);
}

std::string format_speed_line()
{
    return fmt::format("{} · hook {}/4", speed_text(speedhack_get_speed()).c_str(), speedhack_hook_count());
}

DWORD count_threads()
{
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (snap == INVALID_HANDLE_VALUE) return 0;
    const DWORD self = GetCurrentProcessId();
    DWORD count = 0;
    THREADENTRY32 te{};
    te.dwSize = sizeof(te);
    if (Thread32First(snap, &te))
    {
        do { if (te.th32OwnerProcessID == self) ++count; } while (Thread32Next(snap, &te));
    }
    CloseHandle(snap);
    return count;
}

bool process_memory(ULONGLONG& working_set, ULONGLONG& private_bytes)
{
    using Fn = BOOL(WINAPI*)(HANDLE, PROCESS_MEMORY_COUNTERS*, DWORD);
    static const Fn fn = []
    {
        HMODULE k32 = GetModuleHandleW(L"kernel32.dll");
        return k32 ? reinterpret_cast<Fn>(GetProcAddress(k32, "K32GetProcessMemoryInfo")) : nullptr;
    }();
    if (fn == nullptr) return false;

    PROCESS_MEMORY_COUNTERS counters{};
    if (!fn(GetCurrentProcess(), &counters, sizeof(counters))) return false;
    working_set = counters.WorkingSetSize;
    private_bytes = counters.PagefileUsage;
    return true;
}

std::wstring env_value(const wchar_t* name)
{
    wchar_t buf[1024] = {};
    const DWORD n = GetEnvironmentVariableW(name, buf, 1024);
    return (n > 0 && n < 1024) ? std::wstring(buf, n) : std::wstring();
}

// ---------- 命令 ----------

void cmd_help(const std::vector<std::string>& args);

void cmd_status(const std::vector<std::string>&)
{
    const auto snap = cesium::runtime::snapshot();
    int loaded = 0;
    for (const auto& m : snap.mods) if (m.loaded) ++loaded;

    print(fmt::format("CesiumLoader {} · SDK {} · 会话 {}", LoaderConfig::loaderVersion,
                      snap.cfg.sdkVersion, narrow(cesium::log::session_id())), LOG_MAGENTA);

    std::wstring exe = snap.exe_path;
    const size_t slash = exe.find_last_of(L"\\/");
    if (slash != std::wstring::npos) exe = exe.substr(slash + 1);

    print(fmt::format("游戏   {} {} · Unity {} · pid {} (x64)",
                      narrow(exe), snap.game_version.empty() ? "-" : snap.game_version,
                      snap.unity_version.empty() ? "-" : snap.unity_version,
                      GetCurrentProcessId()));

    ULONGLONG ws = 0, priv = 0;
    process_memory(ws, priv);
    const std::string mem = ws ? format_bytes(ws) : "?";
    print(fmt::format("运行   {} · 工作集 {} · 线程 {}", format_duration(now_utc_ms() - snap.started_utc_ms),
                      mem, count_threads()));

    print("变速   " + format_speed_line() +
          fmt::format(" · base {}", format_double(snap.cfg.speedhackBaseSpeed)));    print(fmt::format("mods   {}/{} 已加载", loaded, snap.mods.size()));

    print("日志   " + narrow(short_path(cesium::log::loader_log_path())), LOG_DIM);
}

void cmd_env(const std::vector<std::string>&)
{
    const auto snap = cesium::runtime::snapshot();

    std::wstring exe = snap.exe_path;
    const size_t slash = exe.find_last_of(L"\\/");
    const std::wstring exe_name = (slash == std::wstring::npos) ? exe : exe.substr(slash + 1);

    print(fmt::format("CesiumLoader {} · SDK {} · Debug Shell", LoaderConfig::loaderVersion,
                      snap.cfg.sdkVersion), LOG_MAGENTA);
    print(fmt::format("可执行   {}", narrow(exe.empty() ? std::wstring(L"-") : exe)));
    print(fmt::format("游戏     {} {}", narrow(exe_name), snap.game_version.empty() ? "-" : snap.game_version));
    print(fmt::format("引擎     Unity {} · IL2CPP", snap.unity_version.empty() ? "-" : snap.unity_version));
    print(fmt::format("进程     pid {} · x64 · 线程 {} · 会话 {}", GetCurrentProcessId(), count_threads(),
                      narrow(cesium::log::session_id())));

    ULONGLONG ws = 0, priv = 0;
    if (process_memory(ws, priv))
        print(fmt::format("内存     工作集 {} · 提交 {}", format_bytes(ws), format_bytes(priv)));

    print("根目录   " + narrow(loader_root()), LOG_CYAN);
    print("  mods     " + narrow(short_path(mods_dir())));
    print("  sdk      " + narrow(short_path(sdk_dir())));
    print("  bootstrap " + narrow(short_path(bootstrap_dir())));
    print("  logs     " + narrow(short_path(logs_dir())));
    print("  speed    " + narrow(short_path(speedctl_dir())));
    print("  window   " + narrow(short_path(windowctl_dir())));
    print("  configs  " + narrow(short_path(configs_dir())));

    print("配置     " + narrow(short_path(config_path())));
    print("日志     " + narrow(short_path(cesium::log::loader_log_path())));

    static const wchar_t* kEnv[] = {
        L"CESIUM_MODS_DIR", L"CESIUM_SDK_DIR", L"CESIUM_BOOTSTRAP_DIR", L"CESIUM_LOG_DIR",
        L"CESIUM_SPEED_DIR", L"CESIUM_WINDOW_DIR", L"CESIUM_LOG_FILE",
        L"CESIUM_ACTIVITY_LOG_FILE", L"CESIUM_ERROR_LOG_FILE", nullptr
    };
    print("环境变量", LOG_CYAN);
    for (const wchar_t** p = kEnv; *p; ++p)
    {
        const std::wstring value = env_value(*p);
        print(fmt::format("  {}={}", narrow(*p), narrow(value.empty() ? std::wstring(L"(未设置)") : short_path(value))),
              LOG_DIM);
    }
}

void cmd_paths(const std::vector<std::string>&)
{
    struct Entry { const char* label; std::wstring path; };
    const Entry entries[] = {
        { "loader", loader_root() },
        { "mods", mods_dir() },
        { "sdk", sdk_dir() },
        { "bootstrap", bootstrap_dir() },
        { "logs", logs_dir() },
        { "speed", speedctl_dir() },
        { "window", windowctl_dir() },
        { "configs", configs_dir() },
        { "config", config_path() },
        { "boot log", cesium::log::loader_log_path() },
    };

    for (const Entry& e : entries)
    {
        print(fmt::format("{} {}", pad_right(e.label, 10),
                          narrow(e.path) + (file_exists(e.path) ? "" : "  (不存在)")),
              file_exists(e.path) ? LOG_DEFAULT : LOG_YELLOW);
    }
}

void cmd_mods(const std::vector<std::string>& args)
{
    const auto snap = cesium::runtime::snapshot();
    if (snap.mods.empty())
    {
        print("mod 列表还没准备好(引导仍在进行)", LOG_YELLOW);
        return;
    }

    const bool all = !args.empty() && (args[0] == "--all" || args[0] == "-a");

    int id_w = text_width("mod"), ver_w = text_width("version");
    for (const auto& m : snap.mods)
    {
        id_w = (std::max)(id_w, text_width(m.id));
        ver_w = (std::max)(ver_w, text_width(m.version.empty() ? "-" : m.version));
    }
    id_w += 2;
    ver_w += 2;

    print("mod" + std::string(static_cast<size_t>(id_w - 3), ' ') +
          "version" + std::string(static_cast<size_t>(ver_w - 7), ' ') + "状态", LOG_CYAN);

    for (const auto& m : snap.mods)
    {
        if (!all && !m.loaded && m.detail == "disabled") continue;
        const WORD color = m.loaded ? LOG_GREEN : (m.detail == "disabled" ? LOG_DIM : LOG_YELLOW);
        print(pad_right(m.id, id_w) + pad_right(m.version.empty() ? "-" : m.version, ver_w) + m.detail, color);
    }
}

void cmd_hooks(const std::vector<std::string>&)
{
    const auto snap = cesium::runtime::snapshot();
    print(fmt::format("变速     {} · hook {}/4 · base {}", speed_text(speedhack_get_speed()).c_str(),
                      speedhack_hook_count(), format_double(snap.cfg.speedhackBaseSpeed)));
    print(fmt::format("引擎     {}", speedhack_active() ? "已安装 inline hook" : "不可用"));
    print(fmt::format("控制台   {} 列 · {}", console_width(),
                      console_available() ? "可用" : "不可用"));
    print(fmt::format("转发     {}", cesium::runtime::forward_enabled() ? "开" : "关"));
    print(fmt::format("shell    {}", cesium::shell::shell_running() ? "运行中" : "已停止"));
    print("Steam 绕过状态见启动日志里的 [steamhack] 行", LOG_DIM);
}

std::string read_file_tail(const std::wstring& path, size_t max_bytes, bool& ok)
{
    ok = false;
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                           nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return std::string();

    LARGE_INTEGER size{};
    if (!GetFileSizeEx(h, &size)) { CloseHandle(h); return std::string(); }

    LARGE_INTEGER offset{};
    offset.QuadPart = size.QuadPart > (LONGLONG)max_bytes ? size.QuadPart - (LONGLONG)max_bytes : 0;
    SetFilePointerEx(h, offset, nullptr, FILE_BEGIN);

    std::string buf((size_t)(size.QuadPart - offset.QuadPart), '\0');
    DWORD read = 0;
    const BOOL read_ok = buf.empty() ? TRUE : ReadFile(h, buf.data(), (DWORD)buf.size(), &read, nullptr);
    CloseHandle(h);
    if (!read_ok) return std::string();
    buf.resize(read);
    ok = true;
    return buf;
}

void cmd_log(const std::vector<std::string>& args)
{
    const std::wstring path = cesium::log::loader_log_path();

    if (!args.empty() && (args[0] == "path" || args[0] == "--path"))
    {
        print(narrow(path));
        return;
    }

    size_t lines = 20;
    if (!args.empty())
    {
        char* end = nullptr;
        const unsigned long n = std::strtoul(args[0].c_str(), &end, 10);
        if (end && *end == '\0' && n > 0 && n <= 500) lines = n;
    }

    bool ok = false;
    std::string text = read_file_tail(path, 128 * 1024, ok);
    if (!ok)
    {
        print("读不到日志: " + narrow(path), LOG_RED);
        return;
    }

    std::vector<std::string> all;
    size_t start = 0;
    while (start <= text.size())
    {
        const size_t end = text.find('\n', start);
        std::string line = (end == std::string::npos) ? text.substr(start) : text.substr(start, end - start);
        while (!line.empty() && line.back() == '\r') line.pop_back();
        if (!line.empty()) all.push_back(line);
        if (end == std::string::npos) break;
        start = end + 1;
    }

    const size_t first = all.size() > lines ? all.size() - lines : 0;
    for (size_t i = first; i < all.size(); ++i) print(all[i], LOG_DIM);
    print(fmt::format("-- {} · 共 {} 行 --", narrow(short_path(path)), all.size()), LOG_DIM);
}

void cmd_proc(const std::vector<std::string>&)
{
    const auto snap = cesium::runtime::snapshot();
    ULONGLONG ws = 0, priv = 0;
    process_memory(ws, priv);

    DWORD handles = 0;
    GetProcessHandleCount(GetCurrentProcess(), &handles);

    print(fmt::format("pid      {}", GetCurrentProcessId()));
    print(fmt::format("运行     {}", format_duration(now_utc_ms() - snap.started_utc_ms)));
    print(fmt::format("内存     工作集 {} · 提交 {}", ws ? format_bytes(ws) : "?", priv ? format_bytes(priv) : "?"));
    print(fmt::format("对象     句柄 {} · 线程 {}", handles, count_threads()));
    print(fmt::format("控制台   {} 列", console_width()));
}

void cmd_clear(const std::vector<std::string>&)
{
    term::clear_screen();
}

void cmd_detach(const std::vector<std::string>&)
{
    print("shell 已停止; 控制台保留为日志窗口(游戏继续运行)", LOG_YELLOW);
    g_quit = true;
}

void cmd_exit(const std::vector<std::string>& args)
{
    unsigned code = 0;
    if (!args.empty())
    {
        char* end = nullptr;
        const unsigned long v = std::strtoul(args[0].c_str(), &end, 10);
        if (end && *end == '\0') code = (unsigned)v;
    }

    print(fmt::format("正在结束游戏进程 (pid {})…", GetCurrentProcessId()), LOG_RED);
    cesium::log::flush();
    ExitProcess(code);
}

// ---------- 命令表 ----------

struct Command
{
    const char* name;
    const char* usage;
    const char* desc;
    const char* example;
    void (*fn)(const std::vector<std::string>&);
};

const Command kCommands[] = {
    { "help", "help [命令]", "显示命令列表或某条命令的说明", "help set", cmd_help },
    { "status", "status", "加载器/会话/进程摘要", "", cmd_status },
    { "env", "env", "环境信息: 版本、路径、环境变量", "", cmd_env },
    { "paths", "paths", "全部目录与日志文件(带存在性)", "", cmd_paths },
    { "mods", "mods [--all]", "模组加载结果", "mods --all", cmd_mods },
    { "hooks", "hooks", "变速 hook 与各子系统状态", "", cmd_hooks },
    { "log", "log [行数|path]", "查看本次启动的加载器日志尾部", "log 50", cmd_log },
    { "proc", "proc", "进程资源(内存/句柄/线程)", "", cmd_proc },
    { "ls", "ls [过滤]", "列出所有可配置项(同 set --list)", "ls TurnFocus", config::cmd_ls },
    { "get", "get <键>", "显示某个配置项的当前值", "get loader.consoleTopmost", config::cmd_get },
    { "set", "set <键> <值>", "设置加载器或 mod 的配置项", "set TurnFocusMod.cooldownSeconds 2", config::cmd_set },
    { "reload", "reload", "重新读取 doorstop_config.json", "", config::cmd_reload },
    { "clear", "clear", "清屏(同 Ctrl+L)", "", cmd_clear },
    { "detach", "detach", "关闭 shell, 游戏继续运行", "", cmd_detach },
    { "exit", "exit [代码]", "结束游戏进程", "exit", cmd_exit },
};

const Command* find_command(const std::string& name)
{
    for (const Command& c : kCommands)
        if (name == c.name) return &c;
    return nullptr;
}

void print_command_help(const Command& c)
{
    print(std::string(c.usage), LOG_CYAN);
    print(std::string("  ") + c.desc);
    if (c.example[0] != '\0') print(std::string("  例: ") + c.example, LOG_DIM);
}

void cmd_help(const std::vector<std::string>& args)
{
    if (!args.empty())
    {
        const Command* c = find_command(cesium::shell::lower_ascii(args[0]));
        if (c == nullptr)
        {
            print("没有这个命令: " + args[0], LOG_RED);
            return;
        }
        print_command_help(*c);
        return;
    }

    print("CesiumLoader Debug Shell", LOG_MAGENTA);
    print("用法: <命令> [参数] [--help]", LOG_DIM);
    print("");
    for (const Command& c : kCommands) print("  " + pad_right(c.usage, 30) + c.desc);
    print("");
    print("提示: 配置键 = loader.<字段> 或 <ModId>.<键>; ls 列出全部; exit 会结束游戏", LOG_DIM);
}

// ---------- 执行一行 ----------

void handle_line(const std::string& line)
{
    const auto parsed = cesium::shell::tokenize(line);
    if (!parsed.ok)
    {
        print("! " + parsed.error, LOG_RED);
        return;
    }
    if (parsed.args.empty()) return;

    const std::string name = cesium::shell::lower_ascii(parsed.args[0]);
    const Command* command = find_command(name);
    if (command == nullptr)
    {
        print("未知命令: " + name + "（help 查看）", LOG_RED);
        return;
    }

    // help 自己带参数时交给 cmd_help 处理(help set 要看的是 set 的说明),
    // 其余命令出现 --help 时直接打印该命令说明。
    if (name != "help" && cesium::shell::wants_help(parsed.args))
    {
        print_command_help(*command);
        return;
    }

    std::vector<std::string> args(parsed.args.begin() + 1, parsed.args.end());
    command->fn(args);
}

// 命令名清单(Tab 补全用): 命令表只有这一份定义, 所以这个函数也留在这里。
void collect_command_names(std::vector<std::wstring>& out)
{
    for (const Command& c : kCommands) out.push_back(term::widen(c.name));
}

} // namespace cesium::shell::commands
