// shell_internal.h - 调试 shell 的内部接口(只在 shell 的各个实现文件之间使用)
//
// 拆分后的落点(对外仍然只有 shell/shell.h 的 shell_start / shell_running / shell_request_stop):
//   shell.cpp            线程模型: 控制台模式 / 输入循环 / 公共入口
//   shell_prompt.cpp     控制台原语(打印/宽度/提示符绘制·擦除·光标定位)+ 清屏
//   shell_lineedit.cpp   行编辑: 历史 / 剪贴板 / Tab 补全 / 按键分发 / 提交
//   shell_commands.cpp   命令表 + status/env/paths/mods/hooks/log/proc/help/clear/detach/exit + 分发
//   shell_config.cpp     配置目标解析、get/set/ls/reload、loader 热应用
//   shell_config_io.cpp  文件 / JSON 小工具 + mod 配置文件定位
//
// 共享状态(g_line / g_line_cs / print() / refresh_prompt() ...)**只有一份定义**, 声明集中
// 在这里; 各实现文件不得再定义同名对象。命名空间约定:
//   cesium::shell::term      控制台原语、提示符与行编辑
//   cesium::shell::commands  命令表与命令分发
//   cesium::shell::config    配置读写与文件/JSON 小工具

#pragma once

#include "config/jsonc.h"

#include "platform/loader.h"

#include "shell/lineedit.h"

#include <atomic>
#include <string>
#include <vector>

#include <windows.h>

namespace cesium::shell::term
{

// ---------- 共享状态 ----------
// 提示符与控制台写锁(定义在 shell_prompt.cpp)
extern CRITICAL_SECTION g_line_cs;
extern bool g_line_cs_ready;
extern bool g_prompt_visible;
extern bool g_prompt_end_valid;
extern int g_prompt_scroll;
extern bool g_suppress_redraw;

// 当前输入 / 历史 / 剪贴板(定义在 shell_lineedit.cpp)
extern lineedit::State g_line;
extern std::vector<std::wstring> g_history;
extern int g_history_pos;
extern std::wstring g_history_draft;
extern std::wstring g_kill;

// shell 线程退出标志(定义在 shell.cpp)
extern std::atomic<bool> g_quit;

// ---------- 控制台原语 ----------
std::string narrow(const std::wstring& w);
std::wstring widen(const std::string& s);
void raw_write(const std::wstring& text);
void raw_write_locked(const std::wstring& text);
void print(const std::string& line, WORD color = LOG_DEFAULT);
std::string pad_right(const std::string& text, int width);
std::string truncate_display(const std::string& text, int width);

// ---------- 提示符 ----------
void refresh_prompt();
void clear_screen();
void install_write_hooks();
void uninstall_write_hooks();
void draw_prompt_if_hidden();
void erase_prompt_before_exit();
void snap_cursor_to_end_locked();

// 按键分发(定义在 shell_lineedit.cpp): 输入循环把每个 KEY_EVENT 交给它。
void handle_key(const KEY_EVENT_RECORD& key);

} // namespace cesium::shell::term

namespace cesium::shell::config
{

// ---------- 文件 / JSON / mod 配置定位(定义在 shell_config_io.cpp) ----------
std::wstring configs_dir();
bool read_text_file(const std::wstring& path, std::string& out);
bool write_text_file(const std::wstring& path, const std::string& text, std::string& err);
bool file_exists(const std::wstring& path);
std::vector<std::string> list_mod_ids();
bool find_mod_config(const std::string& mod, std::wstring& path, std::string& canonical);
bool mod_loaded(const std::string& id);
const char* json_type_name(const cesium::jsonc::Json& v);
std::string display_json(const cesium::jsonc::Json& v);
std::string quote_json(const std::string& text);
bool parse_bool_text(const std::string& text, bool& out);
bool parse_number_text(const std::string& text, bool integer_only, double& out);
std::string format_double(double v);

// ---------- 配置类命令(定义在 shell_config.cpp; 命令表在 shell_commands.cpp) ----------
void cmd_ls(const std::vector<std::string>& args);
void cmd_get(const std::vector<std::string>& args);
void cmd_set(const std::vector<std::string>& args);
void cmd_reload(const std::vector<std::string>& args);

} // namespace cesium::shell::config

namespace cesium::shell::commands
{

// 解析并执行一行命令(由 shell_lineedit.cpp 的 submit 调用)。
void handle_line(const std::string& line);

// 命令名清单(Tab 补全用): 命令表只有这一份定义(在 shell_commands.cpp)。
void collect_command_names(std::vector<std::wstring>& out);

} // namespace cesium::shell::commands
