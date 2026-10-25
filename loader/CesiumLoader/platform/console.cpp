// console.cpp - 控制台窗口与日志文件
//
// 游戏进程本身是 GUI(无控制台)。引导线程启动时 AllocConsole 分配一个
// 控制台窗口,loader 日志与 mod 的标准输出都写到这个窗口。
// 同时把日志写入 logs\cesium-loader-<时间>-<pid>.log(**每次启动一个新文件**, 见 logging.h)。
//
// 输出策略(2026-10 改版, 动机见 docs 的"日志布局"):
//   - 控制台**按窗口宽度折行**: 以 "[tag] " 开头的行, 标签固定占 kTagColumns 列并置暗灰,
//     正文按调用方颜色; 超宽时优先在空格/路径分隔符处断开, 续行缩进到正文列。
//     之前长路径(单行 494 列)在窄窗口里折成 4~5 行且续行无缩进, 逻辑行边界完全消失。
//   - 句柄不是控制台(被重定向到文件/管道)时退化为 WriteFile 写 UTF-8,
//     而不是让 WriteConsoleW 静默失败、控制台一片空白。
//   - 文件那一半仍由 spdlog 负责(见 logging.cpp): 句柄常开、线程安全、带时间戳、每行落盘。

#include "platform/loader.h"

#include "logging/logging.h"

#include <io.h>
#include <fcntl.h>
#include <winver.h>
#include <cstdio>
#include <cstring>
#include <cstdint>
#include <algorithm>
#include <mutex>
#include <string>
#include <vector>

namespace
{

HANDLE g_console = INVALID_HANDLE_VALUE;
WORD   g_color = LOG_DEFAULT;
int    g_columns = 0;   // 控制台窗口列数; 0 = 未知(不折行)

// 控制台写锁: 日志行与调试 shell 的提示符/输出必须串行, 否则会出现半行交叉。
// 用 CRITICAL_SECTION(可重入): shell 的钩子里再调 console_lock 不会自锁。
CRITICAL_SECTION g_write_cs;
std::once_flag   g_write_once;
void (*g_pre_write)() = nullptr;
void (*g_post_write)() = nullptr;

void ensure_write_lock()
{
    std::call_once(g_write_once, [] { InitializeCriticalSection(&g_write_cs); });
}

// 标签列宽: "[windowctl] " 正好 12 列 —— 所有日志正文从第 13 列开始, 折行缩进也对齐到这一列。
constexpr int kTagColumns = 12;

bool console_ready()
{
    return g_console != INVALID_HANDLE_VALUE;
}

std::wstring widen(const char* utf8)
{
    if (utf8 == nullptr || *utf8 == '\0') return std::wstring();
    const int src = static_cast<int>(strlen(utf8));
    const int len = MultiByteToWideChar(CP_UTF8, 0, utf8, src, nullptr, 0);
    if (len <= 0) return std::wstring();
    std::wstring w(static_cast<size_t>(len), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, utf8, src, w.data(), len);
    return w;
}

std::string narrow_utf8(const std::wstring& w)
{
    if (w.empty()) return std::string();
    int len = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), nullptr, 0, nullptr, nullptr);
    if (len <= 0) return std::string();
    std::string out(static_cast<size_t>(len), '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), out.data(), len, nullptr, nullptr);
    return out;
}

// 单码点显示列宽: 东亚宽字符/全角/emoji 记 2 列, 组合符与控制符记 0 列。
// 折行必须按显示列算, 否则带中文的行会算短、带 emoji 的行会算长, 折行位置全错。
int cp_width(uint32_t cp)
{
    if (cp < 0x20 || cp == 0x7F) return 0;
    if ((cp >= 0x0300 && cp <= 0x036F) || (cp >= 0x20D0 && cp <= 0x20FF)) return 0;   // 组合符
    if ((cp >= 0x1100 && cp <= 0x115F) ||   // 谚文字母
        (cp >= 0x2E80 && cp <= 0xA4CF) ||   // CJK 部首/汉字/日文/韩文
        (cp >= 0xAC00 && cp <= 0xD7A3) ||   // 谚文音节
        (cp >= 0xF900 && cp <= 0xFAFF) ||   // CJK 兼容汉字
        (cp >= 0xFE30 && cp <= 0xFE6F) ||   // CJK 兼容形式
        (cp >= 0xFF00 && cp <= 0xFF60) ||   // 全角 ASCII
        (cp >= 0xFFE0 && cp <= 0xFFE6) ||   // 全角符号
        (cp >= 0x1F300 && cp <= 0x1FAFF) || // emoji
        (cp >= 0x20000 && cp <= 0x3FFFD))   // CJK 扩展 B+
        return 2;
    return 1;
}

size_t next_cp(const std::wstring& s, size_t i, uint32_t& cp)
{
    cp = static_cast<uint32_t>(s[i]);
    if (cp >= 0xD800 && cp <= 0xDBFF && i + 1 < s.size())
    {
        const uint32_t lo = static_cast<uint32_t>(s[i + 1]);
        if (lo >= 0xDC00 && lo <= 0xDFFF)
        {
            cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
            return 2;
        }
    }
    return 1;
}

int width_of(const std::wstring& s)
{
    int w = 0;
    size_t i = 0;
    while (i < s.size())
    {
        uint32_t cp = 0;
        i += next_cp(s, i, cp);
        w += cp_width(cp);
    }
    return w;
}

// 写一段文本到控制台。是控制台就走 WriteConsoleW; 否则(重定向到文件/管道)
// 退化为 WriteFile —— 旧实现只看 WriteConsoleW 的返回值, 重定向时整段内容凭空消失。
bool raw_write(const std::wstring& text)
{
    if (!console_ready() || text.empty()) return false;

    DWORD written = 0;
    if (WriteConsoleW(g_console, text.c_str(), (DWORD)text.size(), &written, nullptr) && written > 0)
        return true;

    DWORD mode = 0;
    if (GetConsoleMode(g_console, &mode)) return false;   // 确实是控制台但写失败: 不重试

    const std::string utf8 = narrow_utf8(text);
    if (utf8.empty()) return false;
    return WriteFile(g_console, utf8.data(), (DWORD)utf8.size(), &written, nullptr) != FALSE;
}

// 按显示列宽折行。first_used = 首行已被前缀占掉的列数; indent = 续行缩进列数。
// 断点优先取空格/路径分隔符(读起来自然), 找不到断点才硬切。
std::vector<std::wstring> wrap_display(const std::wstring& text, int first_used, int indent)
{
    std::vector<std::wstring> rows;
    if (g_columns <= 0) { rows.push_back(text); return rows; }

    const int limit = g_columns - 1;   // 留一列余量, 避免正好顶满时终端自己再折一次
    size_t i = 0;
    size_t row_start = 0;
    size_t last_break = std::wstring::npos;
    int used = first_used;

    while (i < text.size())
    {
        uint32_t cp = 0;
        const size_t len = next_cp(text, i, cp);
        const int w = cp_width(cp);

        if (used + w > limit && i > row_start)
        {
            const size_t brk = (last_break != std::wstring::npos && last_break > row_start) ? last_break : i;
            std::wstring row = text.substr(row_start, brk - row_start);
            while (!row.empty() && row.back() == L' ') row.pop_back();
            rows.push_back(row);

            row_start = brk;
            while (row_start < text.size() && text[row_start] == L' ') ++row_start;
            used = indent;
            last_break = std::wstring::npos;
            i = row_start;
            continue;
        }

        if (cp == L' ' || cp == L'\\' || cp == L'/' || cp == L'|' || cp == L'·')
            last_break = i + len;
        used += w;
        i += len;
    }

    std::wstring tail = text.substr(row_start);
    while (!tail.empty() && tail.back() == L' ') tail.pop_back();
    rows.push_back(tail);
    return rows;
}

// 输出一行: prefix 用 prefix_color, 正文用 body_color; 续行缩进到 prefix 的显示宽度。
void emit(const std::wstring& prefix, const std::wstring& body, WORD prefix_color, WORD body_color)
{
    if (!console_ready()) return;

    // 每行都重新取一次窗口宽度: 用户中途拖动窗口大小后, 折行位置要跟着变。
    // (GetConsoleScreenBufferInfo 是读内核里的控制台结构, 每次几十纳秒级, 不在热路径上。)
    CONSOLE_SCREEN_BUFFER_INFO info{};
    if (GetConsoleScreenBufferInfo(g_console, &info))
        g_columns = info.srWindow.Right - info.srWindow.Left + 1;

    const int prefix_cols = width_of(prefix);
    const std::vector<std::wstring> rows = wrap_display(body, prefix_cols, prefix_cols);

    for (size_t r = 0; r < rows.size(); r++)
    {
        if (r == 0)
        {
            if (!prefix.empty())
            {
                SetConsoleTextAttribute(g_console, prefix_color);
                raw_write(prefix);
            }
        }
        else if (prefix_cols > 0)
        {
            SetConsoleTextAttribute(g_console, LOG_DIM);
            raw_write(std::wstring(static_cast<size_t>(prefix_cols), L' '));
        }

        SetConsoleTextAttribute(g_console, body_color);
        raw_write(rows[r]);
        SetConsoleTextAttribute(g_console, LOG_DEFAULT);
        raw_write(L"\n");
    }
}

// 标签只接受 ASCII 标识符, 避免把 "[2026-10-25 ...]" 这类正文误当标签。
bool is_tag(const std::wstring& s)
{
    if (s.empty() || s.size() > 16) return false;
    for (wchar_t c : s)
    {
        const bool ok = (c >= L'a' && c <= L'z') || (c >= L'A' && c <= L'Z') ||
                        (c >= L'0' && c <= L'9') || c == L'_' || c == L'-';
        if (!ok) return false;
    }
    return true;
}

// 把 "[tag] 消息" 拆成对齐好的前缀 + 正文; 不是标签行就原样返回。
void split_tag(const std::wstring& line, std::wstring& prefix, std::wstring& body)
{
    prefix.clear();
    body = line;
    if (line.size() < 4 || line[0] != L'[') return;

    const size_t close = line.find(L']');
    if (close == std::wstring::npos || close < 2 || close + 1 >= line.size()) return;
    if (line[close + 1] != L' ') return;

    const std::wstring tag = line.substr(1, close - 1);
    if (!is_tag(tag)) return;

    prefix = L"[" + tag + L"]";
    const int pad = kTagColumns - static_cast<int>(prefix.size());
    if (pad > 0) prefix.append(static_cast<size_t>(pad), L' ');
    body = line.substr(close + 2);
}

} // namespace

// ---------- 路径 ----------

// loader_root = 游戏 exe 目录\AstralParty_ModLoader
// 通过 GetModuleFileNameW(NULL) 拿主模块(游戏 exe)路径定位游戏目录。
std::wstring loader_root()
{
    wchar_t buf[1024];
    DWORD n = GetModuleFileNameW(nullptr, buf, 1024);
    if (n == 0) return L".";
    std::wstring exe(buf, n);
    auto pos = exe.find_last_of(L"\\/");
    std::wstring game_dir = (pos == std::wstring::npos) ? L"." : exe.substr(0, pos);
    return game_dir + L"\\AstralParty_ModLoader";
}

std::wstring bootstrap_dir()
{
    wchar_t buf[1024];
    DWORD n = GetEnvironmentVariableW(L"CESIUM_BOOTSTRAP_DIR", buf, 1024);
    if (n > 0 && n < 1024) return std::wstring(buf, n);
    return loader_root() + L"\\bootstrap";
}

std::wstring mods_dir()
{
    wchar_t buf[1024];
    DWORD n = GetEnvironmentVariableW(L"CESIUM_MODS_DIR", buf, 1024);
    if (n > 0 && n < 1024) return std::wstring(buf, n);
    return loader_root() + L"\\mods";
}

std::wstring sdk_dir()
{
    wchar_t buf[1024];
    DWORD n = GetEnvironmentVariableW(L"CESIUM_SDK_DIR", buf, 1024);
    if (n > 0 && n < 1024) return std::wstring(buf, n);
    return loader_root() + L"\\sdk";
}

std::wstring logs_dir()
{
    wchar_t buf[1024];
    DWORD n = GetEnvironmentVariableW(L"CESIUM_LOG_DIR", buf, 1024);
    if (n > 0 && n < 1024) return std::wstring(buf, n);
    return loader_root() + L"\\logs";
}

std::wstring config_path()
{
    return loader_root() + L"\\doorstop_config.json";
}

std::wstring short_path(const std::wstring& path)
{
    struct Alias
    {
        std::wstring prefix;
        const wchar_t* label;
    };

    std::vector<Alias> aliases;
    const std::wstring root = loader_root();
    aliases.push_back({ root, L"<loader>" });

    const size_t slash = root.find_last_of(L"\\/");
    if (slash != std::wstring::npos && slash > 2) aliases.push_back({ root.substr(0, slash), L"<game>" });

    wchar_t buf[MAX_PATH] = {};
    const DWORD n = GetEnvironmentVariableW(L"LOCALAPPDATA", buf, MAX_PATH);
    if (n > 0 && n < MAX_PATH) aliases.push_back({ std::wstring(buf, n), L"%LOCALAPPDATA%" });

    // 最长前缀优先: 否则 <game> 会把 <loader> 下的路径也认领掉。
    std::sort(aliases.begin(), aliases.end(),
              [](const Alias& a, const Alias& b) { return a.prefix.size() > b.prefix.size(); });

    for (const Alias& a : aliases)
    {
        if (a.prefix.empty() || path.size() < a.prefix.size()) continue;
        if (_wcsnicmp(path.c_str(), a.prefix.c_str(), a.prefix.size()) != 0) continue;
        if (path.size() > a.prefix.size() && path[a.prefix.size()] != L'\\' && path[a.prefix.size()] != L'/')
            continue;
        return std::wstring(a.label) + path.substr(a.prefix.size());
    }
    return path;
}

std::wstring file_version(const std::wstring& path)
{
    // 走**系统** version.dll, 不链接 version.lib, 也不经过本 DLL 自己的导出:
    // 本模块就是 version.dll 代理, 链接进来的导入在运行时会被解析回自己的转发函数,
    // 平白多一层自我回环。这里按完整路径取系统实现(与 exports.cpp 的转发同理)。
    using SizeFn = DWORD(WINAPI*)(LPCWSTR, LPDWORD);
    using InfoFn = BOOL(WINAPI*)(LPCWSTR, DWORD, DWORD, LPVOID);
    using QueryFn = BOOL(WINAPI*)(LPCVOID, LPCWSTR, LPVOID*, PUINT);

    struct Api
    {
        SizeFn size = nullptr;
        InfoFn info = nullptr;
        QueryFn query = nullptr;
    };
    static const Api api = []
    {
        Api a;
        HMODULE mod = LoadLibraryW(L"C:\\Windows\\System32\\version.dll");
        if (mod == nullptr) return a;
        a.size = reinterpret_cast<SizeFn>(GetProcAddress(mod, "GetFileVersionInfoSizeW"));
        a.info = reinterpret_cast<InfoFn>(GetProcAddress(mod, "GetFileVersionInfoW"));
        a.query = reinterpret_cast<QueryFn>(GetProcAddress(mod, "VerQueryValueW"));
        return a;
    }();
    if (api.size == nullptr || api.info == nullptr || api.query == nullptr) return std::wstring();

    DWORD handle = 0;
    const DWORD size = api.size(path.c_str(), &handle);
    if (size == 0) return std::wstring();

    std::vector<BYTE> data(size);
    if (!api.info(path.c_str(), 0, size, data.data())) return std::wstring();

    // 1) 字符串版本(最可读, 例如 Unity 的 "2022.3.62f3 (96770f904ca7)")
    static const wchar_t* kKeys[] = { L"ProductVersion", L"FileVersion" };
    static const wchar_t* kBlocks[] = { L"040904B0", L"000004B0" };
    for (const wchar_t* block : kBlocks)
    {
        for (const wchar_t* key : kKeys)
        {
            const std::wstring sub = std::wstring(L"\\StringFileInfo\\") + block + L"\\" + key;
            wchar_t* value = nullptr;
            UINT len = 0;
            if (!api.query(data.data(), sub.c_str(), reinterpret_cast<LPVOID*>(&value), &len)) continue;
            if (value == nullptr || len == 0) continue;
            std::wstring text(value, len);
            while (!text.empty() && (text.back() == L'\0' || text.back() == L' ')) text.pop_back();
            if (!text.empty()) return text;
        }
    }

    // 2) 固定版本号(退而求其次)
    VS_FIXEDFILEINFO* fixed = nullptr;
    UINT fixed_len = 0;
    if (api.query(data.data(), L"\\", reinterpret_cast<LPVOID*>(&fixed), &fixed_len) && fixed != nullptr)
    {
        wchar_t text[64] = {};
        swprintf_s(text, L"%u.%u.%u.%u",
                   HIWORD(fixed->dwProductVersionMS), LOWORD(fixed->dwProductVersionMS),
                   HIWORD(fixed->dwProductVersionLS), LOWORD(fixed->dwProductVersionLS));
        return text;
    }
    return std::wstring();
}

int console_width()
{
    return g_columns;
}

int text_width(const std::string& utf8)
{
    return width_of(widen(utf8.c_str()));
}

// ---------- 彩色分级日志 ----------

void console_set_color(WORD attr)
{
    g_color = attr;
    if (!console_ready()) return;
    SetConsoleTextAttribute(g_console, attr);
}

// ---------- 日志 ----------

// 一行日志 = 控制台(当前属性色) + 文件(logs\cesium-loader-<时间>-<pid>.log)。
// 文件那一半由 spdlog 负责(见 logging.cpp): 句柄常开、线程安全、带时间戳、每行落盘。
// 空串只写控制台空行: 文件里的空行如果也带时间戳, 只会变成噪声(排查时全是 [时间] 空行)。
void log_line(const char* msg)
{
    if (msg == nullptr) return;
    if (*msg == '\0')
    {
        if (console_ready())
        {
            ensure_write_lock();
            EnterCriticalSection(&g_write_cs);
            if (g_pre_write) g_pre_write();
            raw_write(L"\n");
            if (g_post_write) g_post_write();
            LeaveCriticalSection(&g_write_cs);
        }
        return;
    }
    console_write(msg);
    cesium::log::write(msg);
}

// std::string 便捷重载(UTF-8 内容)
void log_line(const std::string& msg)
{
    log_line(msg.c_str());
}

void log_line(const std::wstring& msg)
{
    // 转 UTF-8 后走公共路径
    log_line(narrow_utf8(msg).c_str());
}

// ---------- 控制台 ----------

void console_init(bool topmost)
{
    // 由 Windows 按默认终端设置选择宿主；Windows Terminal 不可用或接管失败时，
    // 系统会回退到 conhost。不要直接启动 wt.exe，以免绕过用户的终端选择。
    BOOL ok = AllocConsole();
    if (!ok)
    {
        // 可能已存在控制台或全屏独占模式; 记录但不致命
        log_line("[loader] AllocConsole returned 0 (console already present or exclusive fullscreen)");
    }
    SetConsoleTitleW(L"CesiumLoader Console");
    g_console = GetStdHandle(STD_OUTPUT_HANDLE);

    // 窗口尺寸决定折行位置: 取一次缓存下来, 后续每行按它折(emit 里还会随窗口变化刷新)。
    CONSOLE_SCREEN_BUFFER_INFO info{};
    if (console_ready() && GetConsoleScreenBufferInfo(g_console, &info))
    {
        g_columns = info.srWindow.Right - info.srWindow.Left + 1;
    }

    // ConPTY/Windows Terminal 下 GetConsoleWindow 返回隐藏的消息窗口，
    // 不能把它显示或置顶。只操作可见的传统控制台窗口，终端窗口由宿主管理。
    HWND hwnd = GetConsoleWindow();
    if (hwnd && IsWindowVisible(hwnd))
    {
        if (topmost)
        {
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                         SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            log_line("[loader] console window set to topmost");
        }
        else
        {
            ShowWindow(hwnd, SW_SHOW);
        }
    }
    else
    {
        log_line("[loader] no visible console window; display/topmost managed by the terminal host");
    }
}

// 写一行文本到控制台(UTF-8 输入,内部转 UTF-16 用 WriteConsoleW)。
// 仅在 console_init 已调用后有效;未初始化时静默跳过(避免在 loader lock 下开窗口)。
// "[tag] " 前缀会被识别: 标签置暗灰并补齐到固定列宽, 正文用当前颜色, 超宽自动折行。
void console_write(const char* utf8_msg)
{
    if (!console_ready() || utf8_msg == nullptr) return;

    ensure_write_lock();
    EnterCriticalSection(&g_write_cs);
    if (g_pre_write) g_pre_write();

    std::wstring prefix;
    std::wstring body;
    split_tag(widen(utf8_msg), prefix, body);
    emit(prefix, body, LOG_DIM, g_color);

    if (g_post_write) g_post_write();
    LeaveCriticalSection(&g_write_cs);
}

void console_write_w(const wchar_t* msg, size_t len)
{
    if (!console_ready() || msg == nullptr || len == 0) return;

    ensure_write_lock();
    EnterCriticalSection(&g_write_cs);
    if (g_pre_write) g_pre_write();

    std::wstring prefix;
    std::wstring body;
    split_tag(std::wstring(msg, len), prefix, body);
    emit(prefix, body, LOG_DIM, g_color);

    if (g_post_write) g_post_write();
    LeaveCriticalSection(&g_write_cs);
}

bool console_available()
{
    return console_ready();
}

void console_set_topmost(bool topmost)
{
    // 与 console_init 同一套判断: ConPTY/Windows Terminal 下 GetConsoleWindow 是隐藏消息窗,
    // 不能拿它去 SetWindowPos(会动到终端宿主)。默认终端设置目前不会接管 AllocConsole 的控制台,
    // 所以实际几乎总是传统 conhost —— 这里的判断只是防御。
    HWND hwnd = GetConsoleWindow();
    if (hwnd == nullptr || !IsWindowVisible(hwnd)) return;

    SetWindowPos(hwnd, topmost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0,
                 SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
}

void console_set_write_hooks(void (*pre)(), void (*post)())
{
    g_pre_write = pre;
    g_post_write = post;
}

void console_lock()
{
    ensure_write_lock();
    EnterCriticalSection(&g_write_cs);
}

void console_unlock()
{
    LeaveCriticalSection(&g_write_cs);
}

// ---------- 导出(供 C# mod 调用) ----------

// 签名:C 字符串(UTF-8),以 \0 结尾。
extern "C" __declspec(dllexport) void WINAPI ap_console_write(const char* msg)
{
    if (msg == nullptr) return;
    console_write(msg);
}

// 写一段 UTF-16 文本到控制台(长度按 wchar_t 字符数)。
extern "C" __declspec(dllexport) void WINAPI ap_console_write_w(const wchar_t* msg, size_t len)
{
    if (msg == nullptr || len == 0) return;
    console_write_w(msg, len);
}
