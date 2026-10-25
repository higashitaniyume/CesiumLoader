// shell.cpp - 加载器调试 shell(见 shell.h)
//
// 线程模型:
//   一个常驻线程负责"读控制台输入 -> 解析 -> 执行 -> 重画提示符"。
//   日志仍然由各后台线程随时写入控制台, 所以提示符必须在每次写入前后被擦掉/重画 ——
//   由 console 的写入钩子(pre/post)在**持有控制台写锁**的情况下调用, 见 on_pre_write。
//
// 安全边界:
//   - 命令只操作原生加载器状态与配置文件, 不调用 il2cpp / Unity(跨线程调用会炸);
//   - 写配置一律"临时文件 + 校验 + 替换", 并备份 .bak; 改写后解析失败就不写;
//   - exit 会结束游戏进程(用户明确要求), detach 只关 shell。

#include "shell/shell.h"

#include "config/config.h"
#include "config/jsonc.h"
#include "config/jsonc_patch.h"
#include "logging/logging.h"
#include "platform/loader.h"
#include "runtime/state.h"
#include "shell/command.h"
#include "shell/schema.h"
#include "speed/speedctl.h"
#include "speed/speedhack.h"
#include "window/windowctl.h"

#include <windows.h>
#include <tlhelp32.h>
#include <psapi.h>

#include <algorithm>
#include <atomic>
#include <cctype>
#include <cstdio>
#include <cstdlib>
#include <filesystem>
#include <string>
#include <vector>

#include <fmt/format.h>

namespace fs = std::filesystem;

namespace
{

using cesium::jsonc::Json;
using cesium::shell::Apply;
using cesium::shell::Field;
using cesium::shell::ValueType;

const wchar_t* kPrompt = L"cesium> ";
constexpr size_t kMaxInput = 512;
constexpr size_t kMaxHistory = 100;

CRITICAL_SECTION g_line_cs;
bool g_line_cs_ready = false;

bool g_prompt_visible = false;
COORD g_prompt_at{};
std::wstring g_input;
std::vector<std::wstring> g_history;
int g_history_pos = 0;
bool g_suppress_redraw = false;

std::atomic<bool> g_running{false};
std::atomic<bool> g_quit{false};
HANDLE g_thread = nullptr;
DWORD g_saved_in_mode = 0;
bool g_have_saved_mode = false;

// ---------- 基础转换 ----------

std::string narrow(const std::wstring& w)
{
    if (w.empty()) return std::string();
    const int len = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), nullptr, 0, nullptr, nullptr);
    if (len <= 0) return std::string();
    std::string out(static_cast<size_t>(len), '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), out.data(), len, nullptr, nullptr);
    return out;
}

std::wstring widen(const std::string& s)
{
    if (s.empty()) return std::wstring();
    const int len = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), nullptr, 0);
    if (len <= 0) return std::wstring();
    std::wstring out(static_cast<size_t>(len), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), out.data(), len);
    return out;
}

void raw_write(const std::wstring& text)
{
    HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);
    if (h == nullptr || h == INVALID_HANDLE_VALUE || text.empty()) return;
    DWORD written = 0;
    WriteConsoleW(h, text.c_str(), (DWORD)text.size(), &written, nullptr);
}

// 在控制台写锁内写原始文本(提示符/回显/换行), 避免和日志行交叉。
void raw_write_locked(const std::wstring& text)
{
    console_lock();
    raw_write(text);
    console_unlock();
}

void print(const std::string& line, WORD color = LOG_DEFAULT)
{
    console_set_color(color);
    console_write(line.c_str());
    console_set_color(LOG_DEFAULT);
}

std::string pad_right(const std::string& text, int width)
{
    const int cur = text_width(text);
    return cur >= width ? text : text + std::string(static_cast<size_t>(width - cur), ' ');
}

std::string truncate_display(const std::string& text, int width)
{
    if (width <= 0 || text_width(text) <= width) return text;
    std::string out;
    int used = 0;
    for (size_t i = 0; i < text.size();)
    {
        const unsigned char c = (unsigned char)text[i];
        size_t len = 1;
        if ((c & 0xE0) == 0xC0) len = 2;
        else if ((c & 0xF0) == 0xE0) len = 3;
        else if ((c & 0xF8) == 0xF0) len = 4;
        const std::string cp = text.substr(i, len);
        const int w = text_width(cp);
        if (used + w > width - 1) break;
        out += cp;
        used += w;
        i += len;
    }
    return out + "…";
}

// ---------- 提示符 ----------
//
// erase/draw 只做 Win32 光标与字符填充, 不经过 console_write(否则会递归触发钩子)。
// 调用方必须已经持有控制台写锁(见 refresh_prompt / 钩子)。

void draw_prompt_locked()
{
    HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);
    CONSOLE_SCREEN_BUFFER_INFO info{};
    if (h == nullptr || h == INVALID_HANDLE_VALUE || !GetConsoleScreenBufferInfo(h, &info)) return;

    g_prompt_at = info.dwCursorPosition;
    SetConsoleTextAttribute(h, LOG_CYAN);
    raw_write(kPrompt);
    SetConsoleTextAttribute(h, LOG_DEFAULT);
    raw_write(g_input);
    g_prompt_visible = true;
}

void erase_prompt_locked()
{
    if (!g_prompt_visible) return;
    HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);
    CONSOLE_SCREEN_BUFFER_INFO info{};
    if (h == nullptr || h == INVALID_HANDLE_VALUE || !GetConsoleScreenBufferInfo(h, &info))
    {
        g_prompt_visible = false;
        return;
    }

    const SHORT width = info.dwSize.X;
    SHORT first = g_prompt_at.Y;
    SHORT last = info.dwCursorPosition.Y;
    if (last < first) first = last;

    DWORD written = 0;
    for (SHORT y = first; y <= last; ++y)
    {
        const COORD at{0, y};
        FillConsoleOutputCharacterW(h, L' ', width, at, &written);
        FillConsoleOutputAttribute(h, LOG_DEFAULT, width, at, &written);
    }
    SetConsoleCursorPosition(h, g_prompt_at);
    g_prompt_visible = false;
}

void on_pre_write()
{
    if (!g_line_cs_ready) return;
    EnterCriticalSection(&g_line_cs);
    erase_prompt_locked();
    LeaveCriticalSection(&g_line_cs);
}

void on_post_write()
{
    if (!g_line_cs_ready) return;
    EnterCriticalSection(&g_line_cs);
    if (!g_suppress_redraw && !g_prompt_visible) draw_prompt_locked();
    LeaveCriticalSection(&g_line_cs);
}

// 用户用滚轮翻到上面看日志后, 一敲键盘就把视口拉回提示符所在行(与 cmd 一致)。
// 只在按键路径调用; 日志写入不调用, 否则回看时会被新日志不停拽回底部。
void ensure_cursor_visible()
{
    HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);
    CONSOLE_SCREEN_BUFFER_INFO info{};
    if (h == nullptr || h == INVALID_HANDLE_VALUE || !GetConsoleScreenBufferInfo(h, &info)) return;

    const SHORT height = info.srWindow.Bottom - info.srWindow.Top + 1;
    const SHORT cursor = info.dwCursorPosition.Y;
    if (cursor >= info.srWindow.Top && cursor <= info.srWindow.Bottom) return;

    SHORT top = cursor - height + 1;
    if (top < 0) top = 0;
    if (top > info.dwSize.Y - height) top = info.dwSize.Y - height;
    if (top < 0) top = 0;

    SMALL_RECT window{0, top, (SHORT)(info.dwSize.X - 1), (SHORT)(top + height - 1)};
    SetConsoleWindowInfo(h, TRUE, &window);
}

void refresh_prompt()
{
    if (!g_line_cs_ready) return;
    console_lock();
    EnterCriticalSection(&g_line_cs);
    if (!g_suppress_redraw)
    {
        erase_prompt_locked();
        draw_prompt_locked();
        ensure_cursor_visible();
    }
    LeaveCriticalSection(&g_line_cs);
    console_unlock();
}

// ---------- 文件 ----------

bool read_text_file(const std::wstring& path, std::string& out)
{
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ,
                           FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                           nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;

    LARGE_INTEGER size{};
    if (!GetFileSizeEx(h, &size) || size.QuadPart > 8 * 1024 * 1024) { CloseHandle(h); return false; }

    out.assign(static_cast<size_t>(size.QuadPart), '\0');
    DWORD read = 0;
    BOOL ok = out.empty() ? TRUE : ReadFile(h, out.data(), (DWORD)out.size(), &read, nullptr);
    CloseHandle(h);
    if (!ok) return false;
    out.resize(read);
    return true;
}

// 备份 .bak -> 写 .tmp -> 刷盘 -> 原子替换。写坏的概率由"调用方先校验"兜住。
bool write_text_file(const std::wstring& path, const std::string& text, std::string& err)
{
    if (GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES)
    {
        const std::wstring bak = path + L".bak";
        CopyFileW(path.c_str(), bak.c_str(), FALSE);
    }

    const std::wstring tmp = path + L".tmp";
    HANDLE h = CreateFileW(tmp.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                           FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) { err = "无法创建临时文件"; return false; }

    DWORD written = 0;
    BOOL ok = text.empty() ? TRUE : WriteFile(h, text.data(), (DWORD)text.size(), &written, nullptr);
    if (ok) ok = FlushFileBuffers(h);
    CloseHandle(h);
    if (!ok) { DeleteFileW(tmp.c_str()); err = "写入失败"; return false; }

    if (!MoveFileExW(tmp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING))
    {
        DeleteFileW(tmp.c_str());
        err = "替换文件失败";
        return false;
    }
    return true;
}

std::wstring configs_dir()
{
    return loader_root() + L"\\configs";
}

// ---------- mod 配置定位 ----------

std::vector<std::string> list_mod_ids()
{
    std::vector<std::string> ids;
    std::error_code ec;

    for (fs::directory_iterator it(mods_dir(), ec), end; !ec && it != end; it.increment(ec))
    {
        const fs::path& p = it->path();
        if (it->is_directory(ec)) ids.push_back(narrow(p.filename().wstring()));
        else if (it->is_regular_file(ec) && narrow(p.extension().wstring()) == ".dll")
            ids.push_back(narrow(p.stem().wstring()));   // 旧布局: mods\X.dll
    }

    std::sort(ids.begin(), ids.end());
    ids.erase(std::unique(ids.begin(), ids.end()), ids.end());
    return ids;
}

bool file_exists(const std::wstring& path)
{
    return GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES;
}

// mod 配置文件: 优先 mods\<Id>\config.json(ModConfig), 退回 configs\<Id>.json(旧式 SdkConfig)。
bool find_mod_config(const std::string& mod, std::wstring& path, std::string& canonical)
{
    for (const std::string& id : list_mod_ids())
    {
        if (_stricmp(id.c_str(), mod.c_str()) != 0) continue;
        canonical = id;
        const std::wstring primary = mods_dir() + L"\\" + widen(id) + L"\\config.json";
        if (file_exists(primary)) { path = primary; return true; }

        const std::wstring legacy = configs_dir() + L"\\" + widen(id) + L".json";
        if (file_exists(legacy)) { path = legacy; return true; }
        return false;
    }
    return false;
}

bool mod_loaded(const std::string& id)
{
    for (const auto& m : cesium::runtime::snapshot().mods)
        if (_stricmp(m.id.c_str(), id.c_str()) == 0) return m.loaded;
    return false;
}

// ---------- JSON 小工具 ----------

const char* json_type_name(const Json& v)
{
    if (v.is_boolean()) return "bool";
    if (v.is_number_integer() || v.is_number_unsigned()) return "int";
    if (v.is_number_float()) return "number";
    if (v.is_string()) return "string";
    if (v.is_object()) return "object";
    if (v.is_array()) return "array";
    if (v.is_null()) return "null";
    return "?";
}

// 展示用: 字符串去掉 JSON 引号(路径里的 \\ 不再成对出现), 其它类型保持 JSON 形式。
std::string display_json(const Json& v)
{
    return v.is_string() ? v.get<std::string>() : v.dump();
}

std::string quote_json(const std::string& text)
{
    std::string out = "\"";
    for (char c : text)
    {
        switch (c)
        {
        case '"': out += "\\\""; break;
        case '\\': out += "\\\\"; break;
        case '\n': out += "\\n"; break;
        case '\r': out += "\\r"; break;
        case '\t': out += "\\t"; break;
        default: out += c; break;
        }
    }
    out += '"';
    return out;
}

bool parse_bool_text(const std::string& text, bool& out)
{
    const std::string v = cesium::shell::lower_ascii(cesium::shell::trim(text));
    if (v == "true" || v == "1" || v == "on" || v == "yes" || v == "y") { out = true; return true; }
    if (v == "false" || v == "0" || v == "off" || v == "no" || v == "n") { out = false; return true; }
    return false;
}

// 严格数字: 只接受 [+-]?数字[.数字], 拒绝 0x/指数/尾随垃圾。
bool parse_number_text(const std::string& text, bool integer_only, double& out)
{
    const std::string t = cesium::shell::trim(text);
    size_t i = 0;
    if (i < t.size() && (t[i] == '+' || t[i] == '-')) ++i;

    int digits = 0, dots = 0;
    while (i < t.size() && (std::isdigit((unsigned char)t[i]) || t[i] == '.'))
    {
        if (t[i] == '.') ++dots; else ++digits;
        ++i;
    }
    if (digits == 0 || dots > (integer_only ? 0 : 1) || i != t.size()) return false;

    out = std::strtod(t.c_str(), nullptr);
    return !(out != out);
}

std::string format_double(double v)
{
    char buf[64];
    std::snprintf(buf, sizeof(buf), "%.10g", v);
    return buf;
}

// ---------- 键解析 ----------

struct Target
{
    enum class Kind { Loader, Mod } kind = Kind::Loader;
    const Field* field = nullptr;
    std::string mod;
    std::string key;
    std::wstring path;
};

bool resolve_key(const std::string& raw, Target& target, std::string& err)
{
    const std::string key = cesium::shell::trim(raw);
    if (key.empty()) { err = "缺少键名"; return false; }

    if (cesium::shell::lower_ascii(key).rfind("loader.", 0) == 0)
    {
        const std::string name = key.substr(7);
        const Field* field = cesium::shell::find_loader_field(name);
        if (field == nullptr) { err = "未知的加载器字段: " + name + "(用 ls 查看)"; return false; }
        target.kind = Target::Kind::Loader;
        target.field = field;
        return true;
    }

    // ModId 自身可能带点(AstralParty.AgentMod), 所以从右往左试前缀。
    size_t search = key.size();
    while (search > 0)
    {
        const size_t dot = key.rfind('.', search - 1);
        if (dot == std::string::npos) break;

        std::wstring path;
        std::string canonical;
        if (find_mod_config(key.substr(0, dot), path, canonical))
        {
            target.kind = Target::Kind::Mod;
            target.mod = canonical;
            target.key = key.substr(dot + 1);
            target.path = path;
            return true;
        }
        search = dot;
    }

    err = "无法识别的键: " + key + "（加载器: loader.<字段>; mod: <ModId>.<键>; 用 ls 查看）";
    return false;
}

// ---------- 设置: 加载器 ----------

void apply_loader_hot(const Field& field, const LoaderConfig& cfg, const std::string& before)
{
    const std::string after = cesium::shell::field_value(cfg, field);
    if (after == before) return;

    const std::string name = field.name;
    if (name == "speedhackBaseSpeed")
    {
        if (speedhack_set_speed(cfg.speedhackBaseSpeed))
            print("  已应用: 当前倍率 " + speed_text(speedhack_get_speed()), LOG_GREEN);
        else
            print("  变速引擎不可用, 倍率没有改变", LOG_YELLOW);
    }
    else if (name == "consoleTopmost")
    {
        console_set_topmost(cfg.consoleTopmost);
        print(std::string("  已应用: 控制台") + (cfg.consoleTopmost ? "置顶" : "取消置顶"), LOG_GREEN);
    }
    else if (name == "forwardActivityLog")
    {
        cesium::runtime::set_forward_enabled(cfg.forwardActivityLog);
        print(std::string("  已应用: 日志转发") + (cfg.forwardActivityLog ? "开启" : "关闭"), LOG_GREEN);
    }
    else if (name == "speedControlEnabled")
    {
        if (cfg.speedControlEnabled)
        {
            speedctl_start(speedctl_dir(), cfg.speedhackBaseSpeed);
            print("  已启动变速文件通道", LOG_GREEN);
        }
        else
        {
            print("  该通道只能从关到开; 关闭需要重启游戏", LOG_YELLOW);
        }
    }
    else if (name == "windowControlEnabled")
    {
        if (cfg.windowControlEnabled)
        {
            windowctl_start(windowctl_dir());
            print("  已启动窗口置前通道", LOG_GREEN);
        }
        else
        {
            print("  该通道只能从关到开; 关闭需要重启游戏", LOG_YELLOW);
        }
    }
    else if (name == "shellEnabled" && !cfg.shellEnabled)
    {
        print("  shell 将在本次命令后停止", LOG_YELLOW);
        g_quit = true;
    }
}

void set_loader_value(const Field& field, const std::string& text)
{
    LoaderConfig cfg = cesium::runtime::config();
    const std::string before = cesium::shell::field_value(cfg, field);

    std::string err;
    if (!cesium::shell::field_assign(cfg, field, text, err))
    {
        print("! " + err, LOG_RED);
        return;
    }

    const std::string literal = cesium::shell::field_json_literal(field, text);
    if (literal.empty())
    {
        print("! 值无效", LOG_RED);
        return;
    }

    std::string doc;
    if (!read_text_file(config_path(), doc)) doc = "{\n}\n";

    std::string patched;
    if (!cesium::jsonc::set_top_level_value(doc, field.name, literal, patched))
    {
        print("! doorstop_config.json 顶层不是 JSON 对象, 拒绝写入", LOG_RED);
        return;
    }
    if (!cesium::jsonc::usable(cesium::jsonc::parse(patched)))
    {
        print("! 改写后的配置无法解析, 已放弃写入(你的文件没有被修改)", LOG_RED);
        return;
    }
    if (!write_text_file(config_path(), patched, err))
    {
        print("! " + err + ": " + narrow(config_path()), LOG_RED);
        return;
    }

    cesium::runtime::set_config(cfg);

    print(fmt::format("loader.{} = {}   原值 {}", field.name,
                      cesium::shell::field_value(cfg, field), before), LOG_GREEN);
    apply_loader_hot(field, cfg, before);
    if (field.apply != Apply::Immediate)
        print("  " + cesium::shell::field_apply_text(field), LOG_DIM);
}

// ---------- 设置: mod ----------

void set_mod_value(const Target& target, const std::string& text)
{
    std::string doc;
    if (!read_text_file(target.path, doc))
    {
        print("! 读不到配置文件: " + narrow(target.path), LOG_RED);
        return;
    }

    const Json json = cesium::jsonc::parse(doc);
    if (!cesium::jsonc::usable(json))
    {
        print("! 配置文件解析失败, 拒绝改写", LOG_RED);
        return;
    }

    std::string key = target.key;
    if (!json.contains(key))
    {
        std::string found;
        for (auto it = json.begin(); it != json.end(); ++it)
        {
            if (cesium::shell::lower_ascii(it.key()) == cesium::shell::lower_ascii(key)) { found = it.key(); break; }
        }
        if (found.empty())
        {
            print("! 没有这个键: " + key + "（ls " + target.mod + " 查看）", LOG_RED);
            return;
        }
        key = found;
    }

    const Json& current = json[key];
    std::string literal;
    if (current.is_boolean())
    {
        bool value = false;
        if (!parse_bool_text(text, value))
        {
            print("! 这个键是 bool, 需要 true/false", LOG_RED);
            return;
        }
        literal = value ? "true" : "false";
    }
    else if (current.is_number_integer() || current.is_number_unsigned())
    {
        double value = 0;
        if (!parse_number_text(text, true, value))
        {
            print("! 这个键是整数", LOG_RED);
            return;
        }
        literal = std::to_string(static_cast<long long>(value));
    }
    else if (current.is_number_float())
    {
        double value = 0;
        if (!parse_number_text(text, false, value))
        {
            print("! 这个键是数字", LOG_RED);
            return;
        }
        literal = format_double(value);
    }
    else if (current.is_string())
    {
        literal = quote_json(text);
    }
    else
    {
        print(std::string("! 这个键是 ") + json_type_name(current) + ", 请直接编辑文件", LOG_YELLOW);
        return;
    }

    std::string patched;
    if (!cesium::jsonc::set_top_level_value(doc, key, literal, patched) ||
        !cesium::jsonc::usable(cesium::jsonc::parse(patched)))
    {
        print("! 改写失败, 已放弃写入(你的文件没有被修改)", LOG_RED);
        return;
    }

    std::string err;
    if (!write_text_file(target.path, patched, err))
    {
        print("! " + err + ": " + narrow(target.path), LOG_RED);
        return;
    }

    print(fmt::format("{}.{} = {}   原值 {}", target.mod, key, literal, display_json(current)), LOG_GREEN);
    if (!mod_loaded(target.mod))
        print("  注意: 该 mod 本次没有加载", LOG_YELLOW);
    print("  ModConfig 模组约 1 秒内热重载; 旧式 SdkConfig 需重启", LOG_DIM);
}

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

    print(fmt::format("CesiumLoader {} · SDK {} · 调试 shell", LoaderConfig::loaderVersion,
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

void cmd_ls(const std::vector<std::string>& args)
{
    const std::string filter = args.empty() ? std::string() : cesium::shell::lower_ascii(args[0]);

    struct Row
    {
        std::string name;
        std::string value;
        std::string meta;
        std::string note;
        bool loaded;
    };
    std::vector<Row> rows;

    const LoaderConfig cfg = cesium::runtime::config();
    size_t field_count = 0;
    const Field* fields = cesium::shell::loader_fields(field_count);
    for (size_t i = 0; i < field_count; ++i)
    {
        const Field& f = fields[i];
        const std::string name = std::string("loader.") + f.name;
        if (!filter.empty() && cesium::shell::lower_ascii(name).find(filter) == std::string::npos) continue;
        rows.push_back({ name, cesium::shell::field_value(cfg, f),
                         cesium::shell::field_type_text(f) + " · " + cesium::shell::field_apply_text(f),
                         f.desc, true });
    }

    for (const std::string& id : list_mod_ids())
    {
        std::wstring path;
        std::string canonical;
        if (!find_mod_config(id, path, canonical)) continue;

        std::string doc;
        if (!read_text_file(path, doc)) continue;
        const Json json = cesium::jsonc::parse(doc);
        if (!cesium::jsonc::usable(json)) continue;

        const bool loaded = mod_loaded(canonical);
        for (auto it = json.begin(); it != json.end(); ++it)
        {
            const std::string name = canonical + "." + it.key();
            if (!filter.empty() && cesium::shell::lower_ascii(name).find(filter) == std::string::npos) continue;
            rows.push_back({ name, display_json(it.value()),
                             std::string(json_type_name(it.value())) + (loaded ? "" : " · 未加载"), "", loaded });
        }
    }

    if (rows.empty())
    {
        print(filter.empty() ? "没有可配置项" : "没有匹配的配置项: " + args[0], LOG_YELLOW);
        return;
    }

    int name_w = 0, value_w = 0;
    for (const Row& r : rows)
    {
        name_w = (std::max)(name_w, text_width(r.name));
        value_w = (std::max)(value_w, text_width(r.value));
    }
    name_w = (std::min)(name_w, 46);
    value_w = (std::min)(value_w, 36);

    print(fmt::format("配置项 {} 个（loader.* 与 <ModId>.<键>; set 用法: set --help）", rows.size()), LOG_CYAN);
    for (const Row& r : rows)
    {
        const std::string name = pad_right(truncate_display(r.name, name_w), name_w + 2);
        const std::string value = pad_right(truncate_display(r.value, value_w), value_w + 2);
        print("  " + name + "= " + value + r.meta + (r.note.empty() ? "" : "  " + r.note),
              r.loaded ? LOG_DEFAULT : LOG_DIM);
    }
}

void cmd_get(const std::vector<std::string>& args)
{
    if (args.empty())
    {
        print("用法: get <键>", LOG_YELLOW);
        return;
    }

    Target target;
    std::string err;
    if (!resolve_key(args[0], target, err))
    {
        print("! " + err, LOG_RED);
        return;
    }

    if (target.kind == Target::Kind::Loader)
    {
        const LoaderConfig cfg = cesium::runtime::config();
        print("loader." + std::string(target.field->name) + " = " +
              cesium::shell::field_value(cfg, *target.field), LOG_WHITE);
        print("  类型  " + cesium::shell::field_type_text(*target.field), LOG_DIM);
        print("  生效  " + cesium::shell::field_apply_text(*target.field), LOG_DIM);
        print(std::string("  说明  ") + target.field->desc, LOG_DIM);
        return;
    }

    std::string doc;
    if (!read_text_file(target.path, doc))
    {
        print("! 读不到 " + narrow(target.path), LOG_RED);
        return;
    }
    const Json json = cesium::jsonc::parse(doc);
    if (!cesium::jsonc::usable(json) || !json.contains(target.key))
    {
        print("! 配置里没有这个键(可能尚未生成)", LOG_RED);
        return;
    }
    print(target.mod + "." + target.key + " = " + display_json(json[target.key]), LOG_WHITE);
    print("  类型  " + std::string(json_type_name(json[target.key])), LOG_DIM);
    print("  文件  " + narrow(short_path(target.path)), LOG_DIM);
    print("  状态  " + std::string(mod_loaded(target.mod) ? "mod 已加载" : "mod 未加载"), LOG_DIM);
}

void cmd_set(const std::vector<std::string>& args)
{
    if (args.empty() || args[0] == "--list" || args[0] == "-l" || args[0] == "list")
    {
        cmd_ls({});
        return;
    }
    if (args.size() < 2)
    {
        print("用法: set <键> <值>", LOG_YELLOW);
        print("  set loader.consoleTopmost true", LOG_DIM);
        print("  set TurnFocusMod.cooldownSeconds 2", LOG_DIM);
        print("  set --list   列出全部可配置项", LOG_DIM);
        return;
    }

    std::string value;
    for (size_t i = 1; i < args.size(); ++i)
    {
        if (i > 1) value += ' ';
        value += args[i];
    }

    Target target;
    std::string err;
    if (!resolve_key(args[0], target, err))
    {
        print("! " + err, LOG_RED);
        return;
    }

    if (target.kind == Target::Kind::Loader) set_loader_value(*target.field, value);
    else set_mod_value(target, value);
}

void cmd_reload(const std::vector<std::string>&)
{
    const LoaderConfig before = cesium::runtime::config();
    const LoaderConfig after = load_config(config_path());
    cesium::runtime::set_config(after);

    size_t field_count = 0;
    const Field* fields = cesium::shell::loader_fields(field_count);
    int changed = 0;
    for (size_t i = 0; i < field_count; ++i)
    {
        const std::string old_value = cesium::shell::field_value(before, fields[i]);
        const std::string new_value = cesium::shell::field_value(after, fields[i]);
        if (old_value == new_value) continue;
        ++changed;
        print(fmt::format("{}: {} -> {}", fields[i].name, old_value, new_value), LOG_WHITE);
        if (fields[i].apply == Apply::Immediate) apply_loader_hot(fields[i], after, old_value);
    }

    print(changed ? fmt::format("已重新读取配置, {} 项变化", changed)
                  : std::string("配置没有变化"), changed ? LOG_GREEN : LOG_DIM);
}

void cmd_clear(const std::vector<std::string>&)
{
    HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);
    CONSOLE_SCREEN_BUFFER_INFO info{};
    if (h != nullptr && h != INVALID_HANDLE_VALUE && GetConsoleScreenBufferInfo(h, &info))
    {
        console_lock();
        const DWORD cells = (DWORD)info.dwSize.X * (DWORD)info.dwSize.Y;
        const COORD home{0, 0};
        DWORD written = 0;
        FillConsoleOutputCharacterW(h, L' ', cells, home, &written);
        FillConsoleOutputAttribute(h, LOG_DEFAULT, cells, home, &written);
        SetConsoleCursorPosition(h, home);

        EnterCriticalSection(&g_line_cs);
        g_prompt_visible = false;
        LeaveCriticalSection(&g_line_cs);
        console_unlock();
    }
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
    { "ls", "ls [过滤]", "列出所有可配置项(同 set --list)", "ls TurnFocus", cmd_ls },
    { "get", "get <键>", "显示某个配置项的当前值", "get loader.consoleTopmost", cmd_get },
    { "set", "set <键> <值>", "设置加载器或 mod 的配置项", "set TurnFocusMod.cooldownSeconds 2", cmd_set },
    { "reload", "reload", "重新读取 doorstop_config.json", "", cmd_reload },
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

    print(fmt::format("CesiumLoader 调试 shell (v{})", LoaderConfig::loaderVersion), LOG_MAGENTA);
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

// ---------- 行编辑与线程 ----------

void enter_raw_mode()
{
    HANDLE in = GetStdHandle(STD_INPUT_HANDLE);
    if (in == nullptr || in == INVALID_HANDLE_VALUE) return;
    if (!GetConsoleMode(in, &g_saved_in_mode)) return;
    g_have_saved_mode = true;

    DWORD mode = g_saved_in_mode;
    // 鼠标的两个模式位分工不同, 这里要**一个开一个关**:
    //   ENABLE_MOUSE_INPUT   = 把鼠标事件塞进输入缓冲区交给客户端。打开时 conhost 不再自己
    //                          滚窗口, 而本 shell 不处理鼠标事件 —— 表现为滚轮没反应。必须关。
    //   ENABLE_QUICK_EDIT_MODE = 控制台宿主自己处理鼠标(拖选/右键复制)。关掉就只能看不能选,
    //                          必须开, 否则没法复制日志。
    // QuickEdit 的固有行为: 有选区时控制台输出会暂停(和 cmd 一样), 取消选区/复制完即恢复。
    mode &= ~(ENABLE_LINE_INPUT | ENABLE_ECHO_INPUT | ENABLE_PROCESSED_INPUT |
              ENABLE_MOUSE_INPUT | ENABLE_WINDOW_INPUT);
    mode |= ENABLE_EXTENDED_FLAGS | ENABLE_QUICK_EDIT_MODE;
    SetConsoleMode(in, mode);
}

void restore_console_mode()
{
    HANDLE in = GetStdHandle(STD_INPUT_HANDLE);
    if (!g_have_saved_mode || in == nullptr || in == INVALID_HANDLE_VALUE) return;
    SetConsoleMode(in, g_saved_in_mode);
    g_have_saved_mode = false;
}

BOOL WINAPI ctrl_handler(DWORD type)
{
    // 输入模式里已经关掉 ENABLE_PROCESSED_INPUT, 正常情况下 Ctrl+C 只是按键事件;
    // 这里再兜一层: 万一别处把模式改回去, 也不至于把游戏进程直接打死。
    if (type == CTRL_C_EVENT || type == CTRL_BREAK_EVENT) return TRUE;
    return FALSE;
}

void set_input(const std::wstring& text)
{
    EnterCriticalSection(&g_line_cs);
    g_input = text;
    LeaveCriticalSection(&g_line_cs);
}

void submit()
{
    std::wstring line;
    {
        EnterCriticalSection(&g_line_cs);
        line = g_input;
        g_input.clear();
        g_prompt_visible = false;
        LeaveCriticalSection(&g_line_cs);
    }

    raw_write_locked(L"\r\n");

    if (!line.empty())
    {
        if (g_history.empty() || g_history.back() != line) g_history.push_back(line);
        if (g_history.size() > kMaxHistory) g_history.erase(g_history.begin());
        g_history_pos = (int)g_history.size();
    }

    handle_line(narrow(line));

    if (g_quit) return;
    refresh_prompt();
}

void history_move(int delta)
{
    if (g_history.empty()) return;
    int pos = g_history_pos + delta;
    if (pos < 0) pos = 0;
    if (pos > (int)g_history.size()) pos = (int)g_history.size();

    g_history_pos = pos;
    set_input(pos == (int)g_history.size() ? std::wstring() : g_history[pos]);
    refresh_prompt();
}

void handle_key(const KEY_EVENT_RECORD& key)
{
    const wchar_t ch = key.uChar.UnicodeChar;
    const WORD vk = key.wVirtualKeyCode;

    if (vk == VK_RETURN) { submit(); return; }

    if (vk == VK_BACK)
    {
        EnterCriticalSection(&g_line_cs);
        if (!g_input.empty()) g_input.pop_back();
        LeaveCriticalSection(&g_line_cs);
        refresh_prompt();
        return;
    }

    if (vk == VK_UP) { history_move(-1); return; }
    if (vk == VK_DOWN) { history_move(1); return; }

    if (vk == VK_ESCAPE)
    {
        set_input(L"");
        refresh_prompt();
        return;
    }

    if (ch == 3)   // Ctrl+C: 只取消当前输入, 不杀进程
    {
        EnterCriticalSection(&g_line_cs);
        g_input.clear();
        g_prompt_visible = false;
        LeaveCriticalSection(&g_line_cs);
        raw_write_locked(L"^C\r\n");
        refresh_prompt();
        return;
    }
    if (ch == 12)  // Ctrl+L
    {
        cmd_clear({});
        refresh_prompt();
        return;
    }
    if (ch == 21)  // Ctrl+U
    {
        set_input(L"");
        refresh_prompt();
        return;
    }

    if (ch >= 0x20)
    {
        EnterCriticalSection(&g_line_cs);
        if (g_input.size() < kMaxInput) g_input += ch;
        LeaveCriticalSection(&g_line_cs);
        refresh_prompt();
    }
}

DWORD WINAPI thread_proc(LPVOID)
{
    HANDLE in = GetStdHandle(STD_INPUT_HANDLE);
    if (in == nullptr || in == INVALID_HANDLE_VALUE)
    {
        g_running = false;
        return 1;
    }

    enter_raw_mode();
    SetConsoleCtrlHandler(ctrl_handler, TRUE);
    console_set_write_hooks(on_pre_write, on_post_write);

    // 钩子注册后可能已经有一行日志把提示符画出来了(见 on_post_write), 这里只在没有时补画,
    // 否则会在同一行写出第二个提示符。
    console_lock();
    EnterCriticalSection(&g_line_cs);
    if (!g_prompt_visible) draw_prompt_locked();
    LeaveCriticalSection(&g_line_cs);
    console_unlock();

    while (!g_quit)
    {
        INPUT_RECORD record{};
        DWORD count = 0;
        if (!ReadConsoleInputW(in, &record, 1, &count))
        {
            Sleep(30);
            continue;
        }
        if (record.EventType != KEY_EVENT || !record.Event.KeyEvent.bKeyDown) continue;
        handle_key(record.Event.KeyEvent);
    }

    g_suppress_redraw = true;
    // 退出前把提示符擦干净(detach 之后不该还留一个空提示符)。
    console_lock();
    EnterCriticalSection(&g_line_cs);
    erase_prompt_locked();
    LeaveCriticalSection(&g_line_cs);
    console_unlock();

    console_set_write_hooks(nullptr, nullptr);
    restore_console_mode();
    SetConsoleCtrlHandler(ctrl_handler, FALSE);
    raw_write_locked(L"\r\n");
    g_running = false;
    return 0;
}

} // namespace

namespace cesium::shell
{

bool shell_start()
{
    if (g_running.exchange(true)) return true;
    if (!console_available())
    {
        g_running = false;
        return false;
    }

    InitializeCriticalSection(&g_line_cs);
    g_line_cs_ready = true;
    g_quit = false;
    g_suppress_redraw = false;
    g_history_pos = 0;

    g_thread = CreateThread(nullptr, 0, thread_proc, nullptr, 0, nullptr);
    if (g_thread == nullptr)
    {
        g_running = false;
        return false;
    }
    CloseHandle(g_thread);
    g_thread = nullptr;
    return true;
}

bool shell_running()
{
    return g_running.load();
}

void shell_request_stop()
{
    g_quit = true;
}

} // namespace cesium::shell

