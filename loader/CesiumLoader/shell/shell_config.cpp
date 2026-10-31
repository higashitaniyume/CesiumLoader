// shell_config.cpp - 配置读写: Target 解析 / get / set / ls / reload / loader 热应用(从 shell.cpp 拆出)
//
// 写配置一律"临时文件 + 校验 + 替换", 并备份 .bak(见 shell_config_io.cpp 的 write_text_file);
// 改写后解析失败就不写。命令表在 shell_commands.cpp。

#include "shell/shell_internal.h"

#include "config/config.h"
#include "config/jsonc.h"
#include "config/jsonc_patch.h"
#include "platform/loader.h"
#include "runtime/state.h"
#include "shell/command.h"
#include "shell/schema.h"
#include "speed/speedctl.h"
#include "speed/speedhack.h"
#include "window/windowctl.h"

#include <windows.h>

#include <string>
#include <vector>

#include <fmt/format.h>

namespace cesium::shell::config
{

using cesium::jsonc::Json;
using term::g_quit;
using term::narrow;
using term::pad_right;
using term::print;
using term::truncate_display;

namespace
{

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
    else if (name == "debugShell" && !cfg.debugShell)
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

} // namespace

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

} // namespace cesium::shell::config
