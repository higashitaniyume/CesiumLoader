// test_json_equiv.cpp - P2 等价性验证
//
// 把 config.cpp / modmeta.cpp 里手写的 JSON 扫描器换成 nlohmann/json 之后, 行为必须保持一致。
// 做法: 把 **旧实现原样** 抄进本文件(oldcfg / oldmeta 命名空间), 与 **真实的新实现**
// (链接 src\CesiumLoader\config.cpp + modmeta.cpp) 在同一批输入上逐字段比对。
//
// 语料分三类:
//   A. 规范输入(全部真实文件 + 规范合成用例) → 必须**逐字段完全一致**;
//   B. 已记录的差异用例 → 差异必须**恰好**落在预期字段上(多一个少一个都算失败);
//   C. 畸形输入 → 新实现必须"整体退回默认值且不崩溃"(旧实现是部分取值, 行为不同且已记录)。
//
// 运行: powershell -File tests\native\run-native-tests.ps1   (会把仓根作为 argv[1] 传入)

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>   // GetTempPathW / DeleteFileW / MAX_PATH

#include "../../src/CesiumLoader/config.h"
#include "../../src/CesiumLoader/jsonc.h"
#include "../../src/CesiumLoader/modmeta.h"
#include "../../src/CesiumLoader/speedhack.h"   // kSpeedMin / kSpeedMax(旧 json_double 用到)

#include <algorithm>
#include <cstdint>
#include <cstdlib>
#include <fstream>
#include <iostream>
#include <sstream>
#include <string>
#include <vector>

// ---------- 桩: config.cpp 需要 log_line(真实实现在 console.cpp, 会牵入 Win32/加载器) ----------
void log_line(const char*) {}
void log_line(const std::string&) {}
void log_line(const std::wstring&) {}

namespace
{
int g_total = 0;
int g_fail = 0;

std::string s(bool v) { return v ? "true" : "false"; }
std::string s(int v) { return std::to_string(v); }
std::string s(unsigned v) { return std::to_string(v); }
std::string s(double v) { std::ostringstream o; o << v; return o.str(); }
std::string s(const std::string& v) { return v; }

std::string read_file_text(const std::string& path)
{
    std::ifstream in(path, std::ios::binary);
    if (!in) return "";
    return std::string((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
}

void fail(const std::string& caseName, const std::string& msg)
{
    ++g_fail;
    std::cout << "  [FAIL] " << caseName << ": " << msg << "\n";
}

// ===================================================================================
// 旧实现(原样抄自 git HEAD: src\CesiumLoader\config.cpp 与 modmeta.cpp)
// ===================================================================================
namespace oldcfg
{
size_t value_start(const std::string& json, const char* key)
{
    std::string needle = std::string("\"") + key + "\"";
    size_t pos = json.find(needle);
    if (pos == std::string::npos) return std::string::npos;
    pos = json.find(':', pos);
    if (pos == std::string::npos) return std::string::npos;
    pos++;
    while (pos < json.size() && (json[pos] == ' ' || json[pos] == '\t' || json[pos] == '\r' || json[pos] == '\n'))
        pos++;
    return pos;
}

bool json_bool(const std::string& json, const char* key, bool def)
{
    size_t pos = value_start(json, key);
    if (pos == std::string::npos) return def;
    if (json.compare(pos, 4, "true") == 0) return true;
    if (json.compare(pos, 5, "false") == 0) return false;
    return def;
}

unsigned json_uint(const std::string& json, const char* key, unsigned def)
{
    size_t pos = value_start(json, key);
    if (pos == std::string::npos) return def;
    long v = strtol(json.c_str() + pos, nullptr, 10);
    if (v < 1 || v > 3600) return def;
    return static_cast<unsigned>(v);
}

double json_double(const std::string& json, const char* key, double def)
{
    size_t pos = value_start(json, key);
    if (pos == std::string::npos) return def;
    double v = strtod(json.c_str() + pos, nullptr);
    if (!(v >= kSpeedMin && v <= kSpeedMax)) return def;
    return v;
}

std::string json_string(const std::string& json, const char* key, const std::string& def)
{
    size_t pos = value_start(json, key);
    if (pos == std::string::npos || pos >= json.size() || json[pos] != '"') return def;
    pos++;
    std::string out;
    while (pos < json.size() && json[pos] != '"')
    {
        if (json[pos] == '\\' && pos + 1 < json.size())
        {
            char c = json[pos + 1];
            switch (c)
            {
                case 'n': out.push_back('\n'); break;
                case 't': out.push_back('\t'); break;
                case 'r': out.push_back('\r'); break;
                default:  out.push_back(c); break;
            }
            pos += 2;
        }
        else { out.push_back(json[pos]); pos++; }
    }
    return out;
}

// 旧 load_config 的取值部分(逐行照抄, 含当时硬编码的默认值)
LoaderConfig load_values(const std::string& json)
{
    LoaderConfig cfg;
    if (json.empty()) return cfg;
    cfg.enabled = json_bool(json, "enabled", true);
    cfg.useManagedBootstrap = json_bool(json, "useManagedBootstrap", false);
    cfg.bootstrapAssembly = json_string(json, "bootstrapAssembly", cfg.bootstrapAssembly);
    cfg.bootstrapType = json_string(json, "bootstrapType", cfg.bootstrapType);
    cfg.bootstrapMethod = json_string(json, "bootstrapMethod", cfg.bootstrapMethod);
    cfg.gameAssemblyTimeoutSec = json_uint(json, "gameAssemblyTimeoutSec", cfg.gameAssemblyTimeoutSec);
    cfg.domainTimeoutSec = json_uint(json, "domainTimeoutSec", cfg.domainTimeoutSec);
    cfg.hybridclrTimeoutSec = json_uint(json, "hybridclrTimeoutSec", cfg.hybridclrTimeoutSec);
    cfg.consoleEnabled = json_bool(json, "consoleEnabled", true);
    cfg.consoleTopmost = json_bool(json, "consoleTopmost", true);   // <- 旧代码的默认值是 true
    cfg.forwardActivityLog = json_bool(json, "forwardActivityLog", true);
    cfg.speedhackBaseSpeed = json_double(json, "speedhackBaseSpeed", 1.0);
    cfg.speedControlEnabled = json_bool(json, "speedControlEnabled", true);
    cfg.steamBypassEnabled = json_bool(json, "steamBypassEnabled", true);
    cfg.steamBypassRestartCheck = json_bool(json, "steamBypassRestartCheck", true);
    cfg.steamBypassMatchmaking = json_bool(json, "steamBypassMatchmaking", true);
    cfg.steamBypassLobbyHasValue = json_bool(json, "steamBypassLobbyHasValue", true);
    cfg.steamBypassTaskCtorMode = json_string(json, "steamBypassTaskCtorMode", cfg.steamBypassTaskCtorMode);
    cfg.steamBypassLobbyMethods = json_bool(json, "steamBypassLobbyMethods", false);
    cfg.steamBypassLobbyQuery = json_bool(json, "steamBypassLobbyQuery", true);
    cfg.sdkVersion = json_string(json, "sdkVersion", cfg.sdkVersion);
    return cfg;
}
} // namespace oldcfg

namespace oldmeta
{
size_t jval(const std::string& json, const char* key)
{
    return oldcfg::value_start(json, key);
}

std::string jstr(const std::string& json, const char* key)
{
    size_t pos = jval(json, key);
    if (pos == std::string::npos || pos >= json.size() || json[pos] != '"') return "";
    pos++;
    std::string out;
    while (pos < json.size() && json[pos] != '"')
    {
        if (json[pos] == '\\' && pos + 1 < json.size())
        {
            char c = json[pos + 1];
            switch (c) { case 'n': out.push_back('\n'); break; case 't': out.push_back('\t'); break; case 'r': out.push_back('\r'); break; default: out.push_back(c); break; }
            pos += 2;
        }
        else { out.push_back(json[pos]); pos++; }
    }
    return out;
}

void jdeps(const std::string& json, const char* key, std::vector<cesium::ModDep>& out)
{
    size_t pos = jval(json, key);
    if (pos == std::string::npos || pos >= json.size() || json[pos] != '[') return;
    pos++;
    while (pos < json.size())
    {
        size_t closeBracket = json.find(']', pos);
        size_t idq = json.find("\"id\"", pos);
        if (idq == std::string::npos || (closeBracket != std::string::npos && idq > closeBracket)) break;
        size_t colon = json.find(':', idq);
        if (colon == std::string::npos) break;
        size_t v1 = json.find('"', colon);
        if (v1 == std::string::npos) break;
        size_t v2 = json.find('"', v1 + 1);
        if (v2 == std::string::npos) break;
        cesium::ModDep dep;
        dep.id = json.substr(v1 + 1, v2 - v1 - 1);
        size_t braceEnd = json.find('}', v2);
        if (braceEnd == std::string::npos) break;
        size_t mq = json.find("\"minVersion\"", v2);
        if (mq != std::string::npos && mq < braceEnd)
        {
            size_t mc = json.find(':', mq);
            size_t m1 = mc == std::string::npos ? std::string::npos : json.find('"', mc);
            size_t m2 = m1 == std::string::npos ? std::string::npos : json.find('"', m1 + 1);
            if (m2 != std::string::npos) dep.minVersion = json.substr(m1 + 1, m2 - m1 - 1);
        }
        out.push_back(dep);
        pos = braceEnd + 1;
    }
}

bool jbool_opt(const std::string& json, const char* key, bool& present)
{
    present = false;
    size_t pos = jval(json, key);
    if (pos == std::string::npos || pos >= json.size()) return false;
    present = true;
    size_t end = pos;
    while (end < json.size() && json[end] != ',' && json[end] != '}' && json[end] != ']') end++;
    std::string token = json.substr(pos, end - pos);
    while (!token.empty() && (token.front() == ' ' || token.front() == '\t' || token.front() == '\r' || token.front() == '\n')) token.erase(token.begin());
    while (!token.empty() && (token.back() == ' ' || token.back() == '\t' || token.back() == '\r' || token.back() == '\n')) token.pop_back();
    return token == "true";
}

int jint_opt(const std::string& json, const char* key, int fallback, bool& present)
{
    present = false;
    size_t pos = jval(json, key);
    if (pos == std::string::npos || pos >= json.size()) return fallback;
    present = true;
    size_t end = pos;
    while (end < json.size() && json[end] != ',' && json[end] != '}' && json[end] != ']') end++;
    std::string token = json.substr(pos, end - pos);
    while (!token.empty() && (token.front() == ' ' || token.front() == '\t' || token.front() == '\r' || token.front() == '\n')) token.erase(token.begin());
    while (!token.empty() && (token.back() == ' ' || token.back() == '\t' || token.back() == '\r' || token.back() == '\n')) token.pop_back();
    if (token.empty()) return fallback;
    return atoi(token.c_str());
}

cesium::ModMeta parse_sidecar(const std::string& json)
{
    cesium::ModMeta m;
    if (json.empty()) return m;
    m.hasSidecar = true;
    m.id = jstr(json, "id");
    if (m.id.empty()) m.id = jstr(json, "name");
    m.name = jstr(json, "name");
    m.version = jstr(json, "version");
    m.sdkVersion = jstr(json, "sdkVersion");
    bool present = false;
    bool en = jbool_opt(json, "enabled", present);
    if (present) m.enabled = en;
    bool permPresent = false;
    m.permissions = jint_opt(json, "permissions", 0, permPresent);
    jdeps(json, "dependencies", m.deps);
    return m;
}
} // namespace oldmeta

// ===================================================================================
// 比对
// ===================================================================================
std::vector<std::pair<std::string, std::string>> fields(const LoaderConfig& c)
{
    return {
        { "enabled", s(c.enabled) },
        { "useManagedBootstrap", s(c.useManagedBootstrap) },
        { "bootstrapAssembly", s(c.bootstrapAssembly) },
        { "bootstrapType", s(c.bootstrapType) },
        { "bootstrapMethod", s(c.bootstrapMethod) },
        { "gameAssemblyTimeoutSec", s(c.gameAssemblyTimeoutSec) },
        { "domainTimeoutSec", s(c.domainTimeoutSec) },
        { "hybridclrTimeoutSec", s(c.hybridclrTimeoutSec) },
        { "consoleEnabled", s(c.consoleEnabled) },
        { "consoleTopmost", s(c.consoleTopmost) },
        { "forwardActivityLog", s(c.forwardActivityLog) },
        { "speedhackBaseSpeed", s(c.speedhackBaseSpeed) },
        { "speedControlEnabled", s(c.speedControlEnabled) },
        { "steamBypassEnabled", s(c.steamBypassEnabled) },
        { "steamBypassRestartCheck", s(c.steamBypassRestartCheck) },
        { "steamBypassMatchmaking", s(c.steamBypassMatchmaking) },
        { "steamBypassLobbyHasValue", s(c.steamBypassLobbyHasValue) },
        { "steamBypassTaskCtorMode", s(c.steamBypassTaskCtorMode) },
        { "steamBypassLobbyMethods", s(c.steamBypassLobbyMethods) },
        { "steamBypassLobbyQuery", s(c.steamBypassLobbyQuery) },
        { "sdkVersion", s(c.sdkVersion) },
    };
}

std::vector<std::pair<std::string, std::string>> fields(const cesium::ModMeta& m)
{
    std::string deps;
    for (size_t i = 0; i < m.deps.size(); i++)
        deps += (i ? "|" : "") + m.deps[i].id + "@" + m.deps[i].minVersion;
    return {
        { "hasSidecar", s(m.hasSidecar) },
        { "id", s(m.id) },
        { "name", s(m.name) },
        { "version", s(m.version) },
        { "sdkVersion", s(m.sdkVersion) },
        { "permissions", s(m.permissions) },
        { "enabled", s(m.enabled) },
        { "deps", deps },
    };
}

// 返回实际发生差异的字段名
std::vector<std::string> diff_fields(
    const std::vector<std::pair<std::string, std::string>>& a,
    const std::vector<std::pair<std::string, std::string>>& b,
    std::string* detail)
{
    std::vector<std::string> diffs;
    std::ostringstream d;
    for (size_t i = 0; i < a.size(); i++)
    {
        if (a[i].second != b[i].second)
        {
            diffs.push_back(a[i].first);
            d << "\n           " << a[i].first << ": 旧=\"" << a[i].second << "\" 新=\"" << b[i].second << "\"";
        }
    }
    if (detail) *detail = d.str();
    return diffs;
}

std::string join(const std::vector<std::string>& v)
{
    std::string out;
    for (size_t i = 0; i < v.size(); i++) out += (i ? "," : "") + v[i];
    return out;
}

// 把差异字段名按"字段表的规范顺序"排好, 便于与 diff_fields 的输出直接比较。
std::vector<std::string> sorted_by_field_order(
    std::vector<std::string> v,
    const std::vector<std::pair<std::string, std::string>>& table)
{
    auto idx = [&table](const std::string& n) {
        for (size_t i = 0; i < table.size(); i++)
            if (table[i].first == n) return i;
        return table.size();
    };
    std::stable_sort(v.begin(), v.end(),
                     [&idx](const std::string& a, const std::string& b) { return idx(a) < idx(b); });
    return v;
}

// ---------- 语料 ----------
struct Case
{
    std::string name;
    std::string text;
    std::vector<std::string> expectDiff;   // 规范输入留空
    bool malformed = false;                // 畸形输入: 只验证"退回默认值且不崩"
};

std::vector<Case> g_configCases;
std::vector<Case> g_sidecarCases;
std::wstring g_tmpPath;

void add_config(const std::string& name, const std::string& text,
                std::vector<std::string> expect = {}, bool malformed = false)
{
    // 配置里若没有 consoleTopmost 键, 新旧实现必然不同 ——
    // 旧代码把这个键的默认值硬编码成 true, 新实现遵循 config.h 的 consoleTopmost=false。
    // 这里自动把它计入预期差异, 于是"差异恰好只有它"这件事本身也被断言住了。
    // 例外: 空输入时新旧都直接返回结构体默认值(不存在差异)。
    if (!malformed && !text.empty() &&
        text.find("\"consoleTopmost\"") == std::string::npos &&
        std::find(expect.begin(), expect.end(), "consoleTopmost") == expect.end())
        expect.push_back("consoleTopmost");
    g_configCases.push_back({ name, text, std::move(expect), malformed });
}

void add_sidecar(const std::string& name, const std::string& text,
                 std::vector<std::string> expect = {}, bool malformed = false)
{
    g_sidecarCases.push_back({ name, text, std::move(expect), malformed });
}

void load_real(const std::string& root, const std::string& rel, bool asConfig, const std::string& label)
{
    const std::string p = root + rel;
    const std::string text = read_file_text(p);
    if (text.empty()) { std::cout << "  (跳过, 读不到) " << label << ": " << p << "\n"; return; }
    if (asConfig) add_config(label, text);
    else add_sidecar(label, text);
}

void build_corpus(const std::string& root)
{
    // ---- A. 全部真实配置文件(都带 // 注释) ----
    load_real(root, "\\dist\\modloader\\AstralParty_ModLoader\\doorstop_config.json", true, "real:dist/doorstop_config");
    load_real(root, "\\smoke\\SpeedCtlSmoke\\bin\\Release\\net8.0\\AstralParty_ModLoader\\doorstop_config.json", true, "real:smoke/doorstop_config");
    load_real(root, "\\staging\\AstralParty_ModLoader\\doorstop_config.json", true, "real:staging/doorstop_config");
    load_real(root, "\\tools\\smoke\\speedtest\\env\\AstralParty_ModLoader\\doorstop_config.json", true, "real:tools-smoke/doorstop_config");

    // ---- A. 全部真实 mod 清单 ----
    load_real(root, "\\dist\\modloader\\AstralParty_ModLoader\\mods\\ActivityLogMod\\ActivityLogMod.json", false, "real:ActivityLogMod");
    load_real(root, "\\dist\\modloader\\AstralParty_ModLoader\\mods\\FreeCameraMod\\FreeCameraMod.json", false, "real:FreeCameraMod");
    load_real(root, "\\dist\\modloader\\AstralParty_ModLoader\\mods\\SpeedHackMod\\SpeedHackMod.json", false, "real:SpeedHackMod");

    // ---- A. 规范合成用例 ----
    add_config("empty", "");
    add_config("jsonc_comments",
               "{\n  // 行注释\n  \"enabled\": true, /* 块注释 */\n  \"speedhackBaseSpeed\": 2.0\n}");
    add_config("bom", "\xEF\xBB\xBF{ \"enabled\": true, \"consoleTopmost\": false }");
    add_config("all_keys",
               "{\n"
               "  \"enabled\": false,\n  \"useManagedBootstrap\": true,\n"
               "  \"bootstrapAssembly\": \"A.dll\",\n  \"bootstrapType\": \"T\",\n  \"bootstrapMethod\": \"M\",\n"
               "  \"gameAssemblyTimeoutSec\": 10,\n  \"domainTimeoutSec\": 20,\n  \"hybridclrTimeoutSec\": 30,\n"
               "  \"consoleEnabled\": false,\n  \"consoleTopmost\": true,\n  \"forwardActivityLog\": false,\n"
               "  \"speedhackBaseSpeed\": 3.5,\n  \"speedControlEnabled\": false,\n"
               "  \"steamBypassEnabled\": false,\n  \"steamBypassRestartCheck\": false,\n"
               "  \"steamBypassMatchmaking\": false,\n  \"steamBypassLobbyHasValue\": false,\n"
               "  \"steamBypassTaskCtorMode\": \"direct\",\n  \"steamBypassLobbyMethods\": true,\n"
               "  \"steamBypassLobbyQuery\": false,\n  \"sdkVersion\": \"9.9.9\"\n"
               "}");

    // ---- B. 已记录的差异: consoleTopmost 缺失(旧硬编码默认 true, 新按 config.h 默认 false) ----
    add_config("consoleTopmost_missing(FIXED)", "{ \"enabled\": true }", { "consoleTopmost" });
    // ---- B. 已记录的差异: 数字写成字符串(旧 strtod 拿到引号→0→越界→默认值; 新按字符串解析) ----
    add_config("speed_as_string(IMPROVED)", "{ \"speedhackBaseSpeed\": \"2.0\" }", { "speedhackBaseSpeed" });
    // ---- B. 已记录的差异: 转义 unicode(旧只吞掉反斜杠; 新正确解码) ----
    add_config("unicode_escape(IMPROVED)", "{ \"bootstrapAssembly\": \"a\\u00e9.dll\" }", { "bootstrapAssembly" });

    // ---- A. 边界值: 范围校验两条路径都退回默认 ----
    add_config("timeout_out_of_range", "{ \"domainTimeoutSec\": 0, \"hybridclrTimeoutSec\": 99999 }");
    add_config("timeout_boundary", "{ \"domainTimeoutSec\": 1, \"hybridclrTimeoutSec\": 3600 }");
    add_config("speed_out_of_range", "{ \"speedhackBaseSpeed\": 0.5 }");
    add_config("speed_boundary", "{ \"speedhackBaseSpeed\": 1.0 }");
    add_config("speed_out_of_range_high", "{ \"speedhackBaseSpeed\": 200.0 }");
    add_config("wrong_types", "{ \"enabled\": 1, \"consoleEnabled\": \"true\", \"domainTimeoutSec\": \"abc\" }");
    add_config("null_values", "{ \"enabled\": null, \"bootstrapAssembly\": null }");
    add_config("nested_and_array", "{ \"enabled\": true, \"extra\": { \"enabled\": false }, \"list\": [1,2] }");
    add_config("whitespace_heavy", "  \n\t {  \"enabled\" :  true ,\n \"consoleTopmost\" : false }  \n ");

    // ---- C. 畸形输入 → 新实现整体退回默认值 ----
    add_config("MALFORMED_truncated", "{ \"enabled\": true, \"bootstrap", {}, true);
    add_config("MALFORMED_trailing_comma", "{ \"enabled\": true, }", {}, true);
    add_config("MALFORMED_unquoted_key", "{ enabled: true }", {}, true);
    add_config("MALFORMED_single_quotes", "{ 'enabled': true }", {}, true);
    add_config("MALFORMED_garbage", "not json at all", {}, true);
    add_config("MALFORMED_only_bom", "\xEF\xBB\xBF", {}, true);

    // ---- A. mod 清单 ----
    add_sidecar("sidecar_empty", "");
    add_sidecar("sidecar_jsonc_comments",
                "{\n  // 注释\n  \"id\": \"X\",\n  \"version\": \"1.0.0\" /* 块 */\n}");
    add_sidecar("sidecar_full",
                "{ \"id\": \"X\", \"name\": \"显示名\", \"version\": \"1.2.3\", \"sdkVersion\": \"2.1.7\","
                " \"permissions\": 7, \"enabled\": false,"
                " \"dependencies\": [ { \"id\": \"A\", \"minVersion\": \"1.0.0\" }, { \"id\": \"B\" } ] }");
    add_sidecar("sidecar_no_id_uses_name", "{ \"name\": \"FallbackName\", \"version\": \"1.0.0\" }");
    add_sidecar("sidecar_enabled_true", "{ \"id\": \"X\", \"enabled\": true }");
    add_sidecar("sidecar_enabled_missing", "{ \"id\": \"X\" }");
    add_sidecar("sidecar_deps_empty", "{ \"id\": \"X\", \"dependencies\": [] }");
    add_sidecar("sidecar_permissions_string(IMPROVED)", "{ \"id\": \"X\", \"permissions\": \"3\" }", { "permissions" });
    add_sidecar("sidecar_unicode_escape(IMPROVED)", "{ \"id\": \"X\", \"name\": \"a\\u00e9\" }", { "name" });
    add_sidecar("sidecar_MALFORMED", "{ \"id\": ", {}, true);
}

void check_config_case(const std::string& name, const std::string& text,
                       const std::vector<std::string>& expectDiff, bool malformed)
{
    ++g_total;
    const LoaderConfig oldCfg = oldcfg::load_values(text);

    // 写临时文件后调用**真实**的新实现(走 read_file, 端到端)
    {
        std::ofstream out(g_tmpPath, std::ios::binary | std::ios::trunc);
        out.write(text.data(), static_cast<std::streamsize>(text.size()));
    }
    const LoaderConfig newCfg = load_config(g_tmpPath);

    if (malformed)
    {
        // 新实现: 解析失败 → 整体默认值(config.h 的成员初始值)
        const LoaderConfig def;
        std::string detail;
        const auto d = diff_fields(fields(def), fields(newCfg), &detail);
        if (!d.empty()) fail(name, "畸形输入未整体退回默认值, 差异字段: " + join(d) + detail);
        return;
    }

    std::string detail;
    const auto actual = diff_fields(fields(oldCfg), fields(newCfg), &detail);
    const auto expected = sorted_by_field_order(expectDiff, fields(LoaderConfig{}));
    if (actual != expected)
        fail(name, "差异字段不符\n           预期: [" + join(expected) + "]\n           实际: [" + join(actual) + "]" + detail);
}

void check_sidecar_case(const std::string& name, const std::string& text,
                        const std::vector<std::string>& expectDiff, bool malformed)
{
    ++g_total;
    const cesium::ModMeta oldMeta = oldmeta::parse_sidecar(text);
    const cesium::ModMeta newMeta = cesium::parse_sidecar(text);

    if (malformed)
    {
        // 非空输入 → hasSidecar=true(与旧一致), 各字段退回默认
        if (!newMeta.hasSidecar) { fail(name, "畸形非空清单应保持 hasSidecar=true"); return; }
        if (!newMeta.id.empty() || !newMeta.name.empty() || !newMeta.version.empty() ||
            !newMeta.sdkVersion.empty() || !newMeta.deps.empty() || newMeta.permissions != 0 ||
            !newMeta.enabled)
            fail(name, "畸形清单的字段未退回默认值");
        return;
    }

    std::string detail;
    const auto actual = diff_fields(fields(oldMeta), fields(newMeta), &detail);
    const auto expected = sorted_by_field_order(expectDiff, fields(cesium::ModMeta{}));
    if (actual != expected)
        fail(name, "差异字段不符\n           预期: [" + join(expected) + "]\n           实际: [" + join(actual) + "]" + detail);
}
} // namespace

int main(int argc, char** argv)
{
    std::cout << "== P2: 手写 JSON 扫描器 vs nlohmann/json 等价性 ==\n";

    if (argc < 2)
    {
        std::cout << "  用法: test_json_equiv.exe <仓根 modding\\msvc 路径>\n";
        return 2;
    }
    const std::string root = argv[1];
    build_corpus(root);

    wchar_t tmp[MAX_PATH] = {};
    GetTempPathW(MAX_PATH, tmp);
    g_tmpPath = std::wstring(tmp) + L"cesium_json_equiv_tmp.json";

    std::cout << "  配置文件用例: " << g_configCases.size()
              << "   mod 清单用例: " << g_sidecarCases.size() << "\n";

    for (const auto& c : g_configCases)
        check_config_case(c.name, c.text, c.expectDiff, c.malformed);
    for (const auto& c : g_sidecarCases)
        check_sidecar_case(c.name, c.text, c.expectDiff, c.malformed);

    DeleteFileW(g_tmpPath.c_str());
    std::cout << "  用例: " << g_total << "  失败: " << g_fail << "\n";
    if (g_fail == 0) std::cout << "== 全部通过 ==\n";
    return g_fail == 0 ? 0 : 1;
}
