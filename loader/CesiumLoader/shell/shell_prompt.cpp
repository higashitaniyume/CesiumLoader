// shell_prompt.cpp - 控制台原语与提示符(从 shell.cpp 拆出)
//
// erase/draw 只做 Win32 光标与字符填充, 不经过 console_write(否则会递归触发钩子)。
// 调用方必须已经持有控制台写锁(见 refresh_prompt / 钩子)。
//
// 共享的提示符状态与 g_line_cs 在这里定义; 行编辑/命令/配置三个文件只通过
// shell_internal.h 访问。

#include "shell/shell_internal.h"

#include "shell/lineedit.h"

#include <string>
#include <vector>

namespace cesium::shell::term
{

const wchar_t* kPrompt = L"cesium> ";

CRITICAL_SECTION g_line_cs;
bool g_line_cs_ready = false;
bool g_prompt_visible = false;
COORD g_prompt_at{};
COORD g_prompt_end{};          // 输入末尾的屏幕位置: 擦除按"提示符首行 ~ 输入末行"整块清
bool g_prompt_end_valid = false;
int g_prompt_scroll = 0;       // 画输入时整屏被顶上去的行数(提示符首行的旧坐标要减掉它)
bool g_suppress_redraw = false;

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

void print(const std::string& line, WORD color)
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

struct EditPos
{
    int row = 0;   // 相对提示符首行
    int col = 0;
};

// 从提示符首行开始, 按显示列宽推进到 text[0, upto)。
// 不能简单用"总列数 ÷ 窗口宽度": 宽字符(中文/emoji 占 2 列)在行尾放不下时控制台会先换行。
EditPos advance_edit_pos(const std::wstring& text, size_t upto, int start_col, int width)
{
    EditPos pos{ 0, start_col };
    size_t i = 0;
    while (i < upto)
    {
        const size_t next = lineedit::next_boundary(text, i);
        const int w = text_width_w(text.substr(i, next - i));
        if (w == 2 && pos.col + 2 > width) { ++pos.row; pos.col = 0; }   // 宽字符放不下: 先换行
        pos.col += w;
        if (pos.col >= width) { ++pos.row; pos.col = 0; }
        i = next;
    }
    return pos;
}

// 输入跨行时控制台会把整屏内容往上滚, 提示符首行的旧坐标随即失效。
// 用"画完后测到的输入末尾位置"和推算出的末尾位置做对比, 得到滚动了多少行;
// 唯一需要排除的是"输入正好填满一行"的情形: 此时控制台可能延迟折行(光标仍停在最后一列),
// 行数差 1 但不是滚动。
int detect_scroll_rows(const EditPos& at_end, SHORT width)
{
    if (!g_prompt_end_valid) return 0;
    const int measured_end_row = g_prompt_end.Y - g_prompt_at.Y;
    const bool delayed_fill = at_end.col == 0 && g_prompt_end.X == width - 1 &&
                              at_end.row == measured_end_row + 1;
    if (delayed_fill) return 0;
    const int scroll = at_end.row - measured_end_row;
    return scroll > 0 ? scroll : 0;
}

// 光标落在输入中间时, 把它从行末挪回去。光标正好在末尾时不用动(控制台本来就在那里)。
void place_edit_cursor_locked(const std::wstring& text, size_t cursor, SHORT width)
{
    const int start_col = g_prompt_at.X + text_width_w(kPrompt);
    const EditPos at_end = advance_edit_pos(text, text.size(), start_col, width);

    // 滚动行数每次重画都要重新算: 它决定擦除范围和提示符首行坐标。
    g_prompt_scroll = detect_scroll_rows(at_end, width);

    if (cursor >= text.size()) return;

    HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);
    if (h == nullptr || h == INVALID_HANDLE_VALUE) return;

    const EditPos at_cursor = advance_edit_pos(text, cursor, start_col, width);
    SetConsoleCursorPosition(
        h, COORD{ static_cast<SHORT>(at_cursor.col),
                  static_cast<SHORT>(g_prompt_at.Y - g_prompt_scroll + at_cursor.row) });
}

void draw_prompt_locked()
{
    HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);
    CONSOLE_SCREEN_BUFFER_INFO info{};
    if (h == nullptr || h == INVALID_HANDLE_VALUE || !GetConsoleScreenBufferInfo(h, &info)) return;

    g_prompt_at = info.dwCursorPosition;
    SetConsoleTextAttribute(h, LOG_CYAN);
    raw_write(kPrompt);
    SetConsoleTextAttribute(h, LOG_DEFAULT);
    raw_write(g_line.text);
    g_prompt_visible = true;

    if (!GetConsoleScreenBufferInfo(h, &info)) return;
    g_prompt_end = info.dwCursorPosition;
    g_prompt_end_valid = true;

    const SHORT width = info.dwSize.X > 0 ? info.dwSize.X : 80;
    place_edit_cursor_locked(g_line.text, g_line.cursor, width);
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
    SHORT first = g_prompt_at.Y - g_prompt_scroll;
    if (first < 0) first = 0;
    SHORT last = g_prompt_end_valid ? g_prompt_end.Y : info.dwCursorPosition.Y;
    if (last < first) last = first;

    DWORD written = 0;
    for (SHORT y = first; y <= last; ++y)
    {
        const COORD at{0, y};
        FillConsoleOutputCharacterW(h, L' ', width, at, &written);
        FillConsoleOutputAttribute(h, LOG_DEFAULT, width, at, &written);
    }
    SetConsoleCursorPosition(h, COORD{ g_prompt_at.X,
                                       static_cast<SHORT>(g_prompt_at.Y - g_prompt_scroll) });
    g_prompt_visible = false;
}

// 提交/取消整行前把控制台光标挪到输入末尾: 用户可能把光标停在行中间,
// 直接在中间写 "\r\n" 会把同一行劈成两半。
void snap_cursor_to_end_locked()
{
    if (!g_prompt_end_valid) return;
    HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);
    if (h == nullptr || h == INVALID_HANDLE_VALUE) return;
    SetConsoleCursorPosition(h, g_prompt_end);
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


void clear_screen()
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

// ---------- 内部入口(命令与线程共用) ----------

void install_write_hooks()
{
    console_set_write_hooks(on_pre_write, on_post_write);
}

void uninstall_write_hooks()
{
    console_set_write_hooks(nullptr, nullptr);
}

// 钩子注册后可能已经有一行日志把提示符画出来了(见 on_post_write), 这里只在没有时补画,
// 否则会在同一行写出第二个提示符。
void draw_prompt_if_hidden()
{
    console_lock();
    EnterCriticalSection(&g_line_cs);
    if (!g_prompt_visible) draw_prompt_locked();
    LeaveCriticalSection(&g_line_cs);
    console_unlock();
}

// 退出前把提示符擦干净(detach 之后不该还留一个空提示符)。
void erase_prompt_before_exit()
{
    console_lock();
    EnterCriticalSection(&g_line_cs);
    erase_prompt_locked();
    LeaveCriticalSection(&g_line_cs);
    console_unlock();
}

} // namespace cesium::shell::term
