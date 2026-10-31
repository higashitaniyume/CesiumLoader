// shell_config_io.cpp - 文件 / JSON 小工具 + mod 配置文件定位(从 shell.cpp 拆出)
//
// 纯工具层: 不打印、不依赖 shell 命令; 供 shell_config.cpp 与 shell_lineedit.cpp(补全)使用。

#include "shell/shell_internal.h"

#include "config/config.h"
#include "config/jsonc.h"
#include "runtime/state.h"
#include "shell/command.h"

#include <windows.h>

#include <cctype>
#include <cstdio>
#include <cstdlib>
#include <filesystem>
#include <string>
#include <vector>

namespace fs = std::filesystem;

namespace cesium::shell::config
{

using cesium::jsonc::Json;
using term::narrow;
using term::widen;

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

} // namespace cesium::shell::config
