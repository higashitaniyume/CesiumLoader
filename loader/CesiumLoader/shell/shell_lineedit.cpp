// shell_lineedit.cpp - 行编辑: 历史 / 剪贴板 / Tab 补全 / 按键分发 / 提交(从 shell.cpp 拆出)
//
// 只处理字符串与光标下标(lineedit.h)+ 提示符重绘, 不碰命令实现; 提交后把整行交给
// cesium::shell::commands::handle_line 分发。行为与拆分前逐行一致。

#include "shell/shell_internal.h"

#include "config/jsonc.h"
#include "shell/lineedit.h"
#include "shell/schema.h"

#include <algorithm>
#include <string>
#include <vector>

namespace cesium::shell::term
{

using cesium::jsonc::Json;

constexpr size_t kMaxInput = 512;
constexpr size_t kMaxHistory = 100;

lineedit::State g_line;        // 当前输入(文本 + 光标下标)
std::vector<std::wstring> g_history;
int g_history_pos = 0;
std::wstring g_history_draft;  // 开始翻历史前正在编辑的内容(翻回最新时恢复)
std::wstring g_kill;           // 最近一次 Ctrl+K/U/W 剪切的内容(Ctrl+Y 粘回)

void set_line(const std::wstring& text)
{
    EnterCriticalSection(&g_line_cs);
    lineedit::set_text(g_line, text);
    LeaveCriticalSection(&g_line_cs);
}

// 在锁内改一行, 然后重画。fn 形如 void(lineedit::State&)。
template <typename Fn>
void edit_line(Fn fn)
{
    EnterCriticalSection(&g_line_cs);
    fn(g_line);
    LeaveCriticalSection(&g_line_cs);
    refresh_prompt();
}

void submit()
{
    std::wstring line;
    {
        EnterCriticalSection(&g_line_cs);
        line = g_line.text;
        lineedit::set_text(g_line, std::wstring());
        g_history_draft.clear();
        g_prompt_visible = false;
        snap_cursor_to_end_locked();
        LeaveCriticalSection(&g_line_cs);
    }

    raw_write_locked(L"\r\n");

    if (!line.empty())
    {
        if (g_history.empty() || g_history.back() != line) g_history.push_back(line);
        if (g_history.size() > kMaxHistory) g_history.erase(g_history.begin());
        g_history_pos = (int)g_history.size();
    }

    commands::handle_line(narrow(line));

    if (g_quit) return;
    refresh_prompt();
}

void history_move(int delta)
{
    if (g_history.empty()) return;

    EnterCriticalSection(&g_line_cs);
    // 与 bash 一致: 开始翻历史前先把正在编辑的内容存成草稿, 翻回最新时恢复。
    if (g_history_pos >= (int)g_history.size()) g_history_draft = g_line.text;

    int pos = g_history_pos + delta;
    if (pos < 0) pos = 0;
    if (pos > (int)g_history.size()) pos = (int)g_history.size();
    g_history_pos = pos;

    lineedit::set_text(g_line, pos == (int)g_history.size() ? g_history_draft : g_history[pos]);
    LeaveCriticalSection(&g_line_cs);
    refresh_prompt();
}

// ---------- 剪贴板 ----------
//
// QuickEdit 的右键粘贴由控制台宿主直接把字符注入输入缓冲区, 不需要这里做;
// 这里补的是快捷键: Ctrl+V / Ctrl+Shift+V / Shift+Insert。
// 粘贴只填进当前行、不自动回车 —— 避免把一整段日志粘成一条命令直接执行。

std::wstring read_clipboard_text()
{
    bool opened = false;
    for (int attempt = 0; attempt < 5 && !opened; ++attempt)
    {
        opened = OpenClipboard(nullptr) != FALSE;
        if (!opened) Sleep(10);
    }
    if (!opened) return std::wstring();

    std::wstring text;
    if (IsClipboardFormatAvailable(CF_UNICODETEXT))
    {
        HANDLE handle = GetClipboardData(CF_UNICODETEXT);
        if (handle != nullptr)
        {
            const wchar_t* p = static_cast<const wchar_t*>(GlobalLock(handle));
            if (p != nullptr)
            {
                text = p;
                GlobalUnlock(handle);
            }
        }
    }
    CloseClipboard();
    return text;
}

void paste_clipboard()
{
    std::wstring text = read_clipboard_text();
    if (text.empty())
    {
        print("剪贴板里没有文本", LOG_DIM);
        return;
    }

    // 换行/制表符压成空格: 多行内容合并成一行, 让用户自己确认后再回车。
    std::wstring clean;
    clean.reserve(text.size());
    for (wchar_t c : text)
    {
        if (c == L'\r' || c == L'\n' || c == L'\t') clean += L' ';
        else clean += c;
    }
    while (!clean.empty() && clean.back() == L' ') clean.pop_back();

    bool truncated = false;
    EnterCriticalSection(&g_line_cs);
    const size_t room = kMaxInput > g_line.text.size() ? kMaxInput - g_line.text.size() : 0;
    if (clean.size() > room)
    {
        clean.resize(room);
        truncated = true;
    }
    lineedit::insert(g_line, clean);
    LeaveCriticalSection(&g_line_cs);

    refresh_prompt();
    if (truncated)
        print("粘贴内容过长, 已截断到 " + std::to_string(kMaxInput) + " 个字符", LOG_YELLOW);
}

// ---------- Tab 补全 ----------
//
// 词按空白切分(与命令分词一致): 第一个词补命令名, help 补命令名,
// get / set / ls 补配置键(loader.* 与 <ModId>.<键>)。引号里的词是值, 不补全。

void collect_config_keys(std::vector<std::wstring>& out)
{
    size_t count = 0;
    const Field* fields = cesium::shell::loader_fields(count);
    for (size_t i = 0; i < count; ++i)
        out.push_back(widen(std::string("loader.") + fields[i].name));

    for (const std::string& id : config::list_mod_ids())
    {
        std::wstring path;
        std::string canonical;
        if (!config::find_mod_config(id, path, canonical)) continue;

        std::string doc;
        if (!config::read_text_file(path, doc)) continue;
        const Json json = cesium::jsonc::parse(doc);
        if (!cesium::jsonc::usable(json)) continue;

        for (auto it = json.begin(); it != json.end(); ++it)
            out.push_back(widen(canonical + "." + it.key()));
    }
}

void print_candidates(const std::vector<std::wstring>& items)
{
    if (items.empty()) return;

    const int width = console_width() > 0 ? console_width() : 80;
    int cell = 0;
    for (const std::wstring& item : items) cell = (std::max)(cell, text_width(narrow(item)));
    cell = (std::min)(cell + 2, width);
    if (cell <= 0) cell = 1;
    int columns = width / cell;
    if (columns < 1) columns = 1;

    // 列表要写在提示符下方: 先停掉钩子里的自动重画, 打完再统一重画一次,
    // 否则每行候选之间都会插一个提示符。
    EnterCriticalSection(&g_line_cs);
    g_suppress_redraw = true;
    LeaveCriticalSection(&g_line_cs);

    for (size_t i = 0; i < items.size(); i += (size_t)columns)
    {
        std::string line;
        for (size_t j = i; j < items.size() && j < i + (size_t)columns; ++j)
            line += pad_right(narrow(items[j]), cell);
        while (!line.empty() && line.back() == ' ') line.pop_back();
        print(line, LOG_DIM);
    }

    EnterCriticalSection(&g_line_cs);
    g_suppress_redraw = false;
    LeaveCriticalSection(&g_line_cs);
    refresh_prompt();
}

void complete_current_word()
{
    lineedit::State snapshot;
    EnterCriticalSection(&g_line_cs);
    snapshot = g_line;
    LeaveCriticalSection(&g_line_cs);

    const std::wstring& text = snapshot.text;
    const size_t cursor = snapshot.cursor;

    size_t begin = cursor;
    while (begin > 0 && !lineedit::is_space(text[begin - 1])) --begin;
    size_t end = cursor;
    while (end < text.size() && !lineedit::is_space(text[end])) ++end;

    if (begin < text.size() && text[begin] == L'"') return;   // 引号里是值

    // 光标所在的词前面已经完整输入了几个词(行首空格不算): 0 表示在命令名上。
    int token_index = 0;
    for (size_t i = 0; i < begin;)
    {
        while (i < begin && lineedit::is_space(text[i])) ++i;
        if (i >= begin) break;
        ++token_index;
        while (i < begin && !lineedit::is_space(text[i])) ++i;
    }

    std::vector<std::wstring> candidates;
    if (token_index == 0)
    {
        commands::collect_command_names(candidates);
    }
    else
    {
        size_t cmd_end = 0;
        while (cmd_end < text.size() && !lineedit::is_space(text[cmd_end])) ++cmd_end;

        std::wstring cmd;
        for (size_t i = 0; i < cmd_end; ++i) cmd += lineedit::lower_ascii_w(text[i]);

        if (cmd == L"help") commands::collect_command_names(candidates);
        else if (cmd == L"get" || cmd == L"set" || cmd == L"ls") collect_config_keys(candidates);
        else return;
    }

    std::sort(candidates.begin(), candidates.end());
    candidates.erase(std::unique(candidates.begin(), candidates.end()), candidates.end());

    const lineedit::Completion result = lineedit::complete(snapshot, begin, end, candidates);
    if (!result.changed && !result.list) return;

    if (result.changed)
    {
        EnterCriticalSection(&g_line_cs);
        g_line = snapshot;
        LeaveCriticalSection(&g_line_cs);
        refresh_prompt();
    }

    if (result.list) print_candidates(result.matches);
}

void handle_key(const KEY_EVENT_RECORD& key)
{
    const wchar_t ch = key.uChar.UnicodeChar;
    const WORD vk = key.wVirtualKeyCode;
    const bool ctrl = (key.dwControlKeyState & (LEFT_CTRL_PRESSED | RIGHT_CTRL_PRESSED)) != 0;
    const bool shift = (key.dwControlKeyState & SHIFT_PRESSED) != 0;

    // 粘贴: Ctrl+V(部分键盘布局只给字符 0x16) / Ctrl+Shift+V / Shift+Insert
    if (ch == 22 || (ctrl && vk == 'V') || (shift && vk == VK_INSERT))
    {
        paste_clipboard();
        return;
    }

    if (vk == VK_RETURN) { submit(); return; }
    if (vk == VK_TAB) { complete_current_word(); return; }

    switch (vk)
    {
    case VK_BACK:
        if (ctrl) edit_line([](lineedit::State& s) { g_kill = lineedit::kill_word(s); });
        else edit_line([](lineedit::State& s) { lineedit::backspace(s); });
        return;

    case VK_DELETE:
        if (ctrl) edit_line([](lineedit::State& s) { g_kill = lineedit::kill_word_forward(s); });
        else edit_line([](lineedit::State& s) { lineedit::delete_forward(s); });
        return;

    case VK_LEFT:
        edit_line([ctrl](lineedit::State& s)
                  { if (ctrl) lineedit::move_word_left(s); else lineedit::move_left(s); });
        return;

    case VK_RIGHT:
        edit_line([ctrl](lineedit::State& s)
                  { if (ctrl) lineedit::move_word_right(s); else lineedit::move_right(s); });
        return;

    case VK_HOME: edit_line([](lineedit::State& s) { lineedit::move_home(s); }); return;
    case VK_END:  edit_line([](lineedit::State& s) { lineedit::move_end(s); }); return;

    case VK_UP:   history_move(-1); return;
    case VK_DOWN: history_move(1); return;

    case VK_ESCAPE:
        set_line(L"");
        refresh_prompt();
        return;

    default: break;
    }

    // Ctrl+字母按字符码判断: 不依赖键盘布局的虚拟键, 也不会和 AltGr 组合冲突。
    switch (ch)
    {
    case 1:   // Ctrl+A
        edit_line([](lineedit::State& s) { lineedit::move_home(s); });
        return;
    case 5:   // Ctrl+E
        edit_line([](lineedit::State& s) { lineedit::move_end(s); });
        return;
    case 11:  // Ctrl+K: 删到行尾
        edit_line([](lineedit::State& s) { g_kill = lineedit::kill_to_end(s); });
        return;
    case 21:  // Ctrl+U: 删到行首
        edit_line([](lineedit::State& s) { g_kill = lineedit::kill_to_start(s); });
        return;
    case 23:  // Ctrl+W: 删前一个词
        edit_line([](lineedit::State& s) { g_kill = lineedit::kill_word(s); });
        return;
    case 25:  // Ctrl+Y: 粘回最近一次删除的内容
        if (!g_kill.empty())
            edit_line([](lineedit::State& s) { lineedit::insert(s, g_kill); });
        return;
    case 4:   // Ctrl+D: 删光标处字符(不退出进程 —— exit 才会结束游戏)
        edit_line([](lineedit::State& s) { lineedit::delete_forward(s); });
        return;
    case 16: history_move(-1); return;   // Ctrl+P
    case 14: history_move(1); return;    // Ctrl+N
    case 3:   // Ctrl+C: 只取消当前输入, 不杀进程
    {
        EnterCriticalSection(&g_line_cs);
        lineedit::set_text(g_line, std::wstring());
        g_prompt_visible = false;
        snap_cursor_to_end_locked();
        LeaveCriticalSection(&g_line_cs);
        raw_write_locked(L"^C\r\n");
        refresh_prompt();
        return;
    }
    case 12:  // Ctrl+L
        clear_screen();
        refresh_prompt();
        return;
    default: break;
    }

    if (ch >= 0x20)
    {
        edit_line([ch](lineedit::State& s)
                  {
                      if (s.text.size() < kMaxInput) lineedit::insert(s, std::wstring(1, ch));
                  });
    }
}

} // namespace cesium::shell::term
