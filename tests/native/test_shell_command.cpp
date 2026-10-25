// test_shell_command.cpp - 调试 shell 的纯逻辑: 分词、JSONC 顶层改写、配置字段校验
//
// 这三块决定了 shell 会不会把配置写坏、会不会把带空格的值拆错, 所以必须离线覆盖。
// 不碰 Win32(控制台/提示符由实机验证)。
//
// 运行: powershell -File tests\native\run-native-tests.ps1

#include "../../loader/CesiumLoader/config/jsonc.h"
#include "../../loader/CesiumLoader/config/jsonc_patch.h"
#include "../../loader/CesiumLoader/shell/command.h"
#include "../../loader/CesiumLoader/shell/schema.h"

#include <iostream>
#include <string>
#include <vector>

namespace
{
int g_total = 0;
int g_fail = 0;

void check(const std::string& name, bool ok, const std::string& detail = "")
{
    ++g_total;
    if (!ok)
    {
        ++g_fail;
        std::cout << "  [FAIL] " << name << (detail.empty() ? "" : ": " + detail) << "\n";
    }
}

using cesium::shell::tokenize;

void check_tokens(const std::string& name, const std::string& line,
                  const std::vector<std::string>& expected)
{
    const auto result = tokenize(line);
    if (!result.ok)
    {
        check(name, false, "解析失败: " + result.error);
        return;
    }
    if (result.args != expected)
    {
        std::string got;
        for (const auto& a : result.args) got += "[" + a + "]";
        check(name, false, "期望参数个数 " + std::to_string(expected.size()) + ", 实际 " + got);
        return;
    }
    check(name, true);
}

void test_tokenize()
{
    std::cout << "== 分词 ==\n";

    check_tokens("basic", "set loader.consoleTopmost true", { "set", "loader.consoleTopmost", "true" });
    check_tokens("extra_spaces", "  status   ", { "status" });
    check_tokens("quoted_value", "set TurnFocusMod.soundFile \"C:\\a b\\ding.wav\"",
                 { "set", "TurnFocusMod.soundFile", "C:\\a b\\ding.wav" });
    check_tokens("quoted_empty", "set x \"\"", { "set", "x", "" });
    check_tokens("backslash_not_escape", "set a C:\\Games\\Astral Party", { "set", "a", "C:\\Games\\Astral" , "Party" });
    check_tokens("quote_inside", "get \"a b\"", { "get", "a b" });
    check_tokens("help_flag", "set --help", { "set", "--help" });

    const std::string empty = "";
    check("empty_line", tokenize(empty).args.empty() && tokenize(empty).ok);

    const auto bad = tokenize("set x \"unterminated");
    check("unterminated_quote", !bad.ok);
}

void check_patch(const std::string& name, const std::string& doc, const std::string& key,
                 const std::string& literal, const std::string& expect_contains)
{
    std::string out;
    if (!cesium::jsonc::set_top_level_value(doc, key, literal, out))
    {
        check(name, false, "set_top_level_value 返回 false");
        return;
    }
    const auto parsed = cesium::jsonc::parse(out);
    if (!cesium::jsonc::usable(parsed))
    {
        check(name, false, "改写后无法解析: " + out);
        return;
    }
    if (out.find(expect_contains) == std::string::npos)
    {
        check(name, false, "结果里没有 '" + expect_contains + "': " + out);
        return;
    }
    check(name, true);
}

void test_patch()
{
    std::cout << "== JSONC 顶层改写 ==\n";

    const std::string plain = "{\n  \"enabled\": true,\n  \"speed\": 2\n}\n";
    check_patch("replace_bool", plain, "enabled", "false", "\"enabled\": false");
    check_patch("replace_number", plain, "speed", "3", "\"speed\": 3");

    // 注释与键序必须保留 —— 这是不用 nlohmann 整体重写的唯一理由。
    const std::string with_comments =
        "{\n  // 总开关\n  \"enabled\": true,  // 行尾注释\n  /* 倍率 */\n  \"speed\": 2\n}\n";
    check_patch("keep_comments", with_comments, "speed", "2.5", "/* 倍率 */");
    {
        std::string out;
        cesium::jsonc::set_top_level_value(with_comments, "speed", "2.5", out);
        check("comments_intact", out.find("// 总开关") != std::string::npos &&
                                 out.find("// 行尾注释") != std::string::npos);
    }

    // 插入缺失键(不破坏已有内容)。
    check_patch("insert_missing", plain, "consoleTopmost", "true", "\"consoleTopmost\": true");
    {
        std::string out;
        cesium::jsonc::set_top_level_value(plain, "consoleTopmost", "true", out);
        check("insert_keeps_old", out.find("\"enabled\": true") != std::string::npos &&
                                  out.find("\"speed\": 2") != std::string::npos);
        const auto parsed = cesium::jsonc::parse(out);
        check("insert_parses", cesium::jsonc::usable(parsed) && parsed["consoleTopmost"] == true);
    }

    // 空对象插入。
    check_patch("insert_empty_object", "{}", "a", "1", "\"a\": 1");

    // BOM 保留, 且键能定位。
    {
        const std::string bom = "\xEF\xBB\xBF{\"a\":1}";
        std::string out;
        const bool ok = cesium::jsonc::set_top_level_value(bom, "a", "2", out);
        check("bom_preserved", ok && out.size() > 3 &&
              (unsigned char)out[0] == 0xEF && out.find("\"a\":2") != std::string::npos);
    }

    // 值里出现 "key": 字样不能被误认。
    {
        const std::string tricky = "{\"note\": \"use \\\"a\\\": 1\", \"a\": 1}";
        std::string out;
        const bool ok = cesium::jsonc::set_top_level_value(tricky, "a", "2", out);
        const auto parsed = cesium::jsonc::parse(out);
        check("string_containing_key", ok && cesium::jsonc::usable(parsed) && parsed["a"] == 2 &&
              parsed["note"] == "use \"a\": 1");
    }

    // 嵌套对象里的同名键不能被顶层改写碰到。
    {
        const std::string nested = "{\"outer\": {\"a\": 1}, \"a\": 2}";
        std::string out;
        cesium::jsonc::set_top_level_value(nested, "a", "9", out);
        const auto parsed = cesium::jsonc::parse(out);
        check("nested_untouched", cesium::jsonc::usable(parsed) &&
              parsed["a"] == 9 && parsed["outer"]["a"] == 1);
    }

    // 顶层不是对象时拒绝。
    {
        std::string out;
        check("reject_array_root", !cesium::jsonc::set_top_level_value("[1,2]", "a", "1", out));
        check("reject_empty", !cesium::jsonc::set_top_level_value("", "a", "1", out));
    }

    // list_top_level_keys 顺序与内容。
    {
        std::vector<std::string> keys;
        cesium::jsonc::list_top_level_keys(with_comments, keys);
        check("list_keys", keys.size() == 2 && keys[0] == "enabled" && keys[1] == "speed");
    }
}

void test_schema()
{
    std::cout << "== 配置字段校验 ==\n";
    using cesium::shell::field_assign;
    using cesium::shell::find_loader_field;
    using cesium::shell::loader_fields;

    size_t count = 0;
    const auto* fields = loader_fields(count);
    check("field_count", count >= 20 && fields != nullptr);

    check("lookup", find_loader_field("consoleTopmost") != nullptr);
    check("lookup_missing", find_loader_field("nope") == nullptr);

    LoaderConfig cfg;
    std::string err;

    const auto* topmost = find_loader_field("consoleTopmost");
    check("bool_on", field_assign(cfg, *topmost, "on", err) && cfg.consoleTopmost);
    check("bool_0", field_assign(cfg, *topmost, "0", err) && !cfg.consoleTopmost);
    check("bool_bad", !field_assign(cfg, *topmost, "maybe", err));

    const auto* speed = find_loader_field("speedhackBaseSpeed");
    check("speed_ok", field_assign(cfg, *speed, "2.5", err) && cfg.speedhackBaseSpeed == 2.5);
    check("speed_below_floor", !field_assign(cfg, *speed, "0.5", err));
    check("speed_above_ceiling", !field_assign(cfg, *speed, "1000", err));
    check("speed_hex_rejected", !field_assign(cfg, *speed, "0x2", err));

    const auto* timeout = find_loader_field("domainTimeoutSec");
    check("int_ok", field_assign(cfg, *timeout, "45", err) && cfg.domainTimeoutSec == 45);
    check("int_float_rejected", !field_assign(cfg, *timeout, "4.5", err));
    check("int_range", !field_assign(cfg, *timeout, "0", err));

    const auto* mode = find_loader_field("steamBypassTaskCtorMode");
    check("enum_ok", field_assign(cfg, *mode, "DIRECT", err) && cfg.steamBypassTaskCtorMode == "direct");
    check("enum_bad", !field_assign(cfg, *mode, "other", err));

    const auto* sdk = find_loader_field("sdkVersion");
    check("string_ok", field_assign(cfg, *sdk, "9.9.9", err) && cfg.sdkVersion == "9.9.9");

    // JSON 字面量: 字符串要带引号并转义。
    check("literal_bool", cesium::shell::field_json_literal(*topmost, "true") == "true");
    check("literal_number", cesium::shell::field_json_literal(*speed, "2") == "2");
    check("literal_string", cesium::shell::field_json_literal(*sdk, "a\"b") == "\"a\\\"b\"");
}

} // namespace

int main()
{
    test_tokenize();
    test_patch();
    test_schema();

    std::cout << "  用例: " << g_total << "  失败: " << g_fail << "\n";
    if (g_fail == 0) std::cout << "== 全部通过 ==\n";
    return g_fail == 0 ? 0 : 1;
}
