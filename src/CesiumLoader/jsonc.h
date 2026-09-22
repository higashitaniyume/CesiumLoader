// jsonc.h - 统一的 JSON / JSONC 读取封装(基于 nlohmann/json)
//
// 本文件只做三件事, 都是加载器的硬需求:
//
//  1) **接受注释**: 配置文件(doorstop_config.json)与部分清单是 JSONC —— 实测 dist 与游戏目录
//     里生效的配置**都带 // 注释**。nlohmann 默认拒绝注释, 必须显式 ignore_comments=true,
//     否则现有配置会全部解析失败、静默退回默认值(表现为"配置突然不生效")。
//
//  2) **绝不抛异常**: 加载器有"任何解析失败都只退回默认值, 绝不崩游戏"的契约。
//     - 解析: parse(..., allow_exceptions=false) —— 失败返回 discarded, 不抛。
//     - 取值: 一律走下面的 is_* 守卫, 类型不符就返回默认值, 不存在抛异常的路径。
//     **注意不要定义 JSON_NOEXCEPTION**: 该宏会把 nlohmann 的错误路径变成 std::abort(),
//     那是直接终止游戏进程, 与契约相反。这里的做法是"不抛 + 绝不走到错误路径"。
//
//  3) **剥离 UTF-8 BOM**: 记事本一类编辑器保存 UTF-8 时会加 BOM, nlohmann 遇到 BOM 会解析失败。
//
// 用法:
//     auto j = cesium::jsonc::parse(text);        // 失败时 j.is_discarded() == true
//     bool b = cesium::jsonc::boolean(j, "enabled", true);
//     std::string s = cesium::jsonc::str(j, "name");

#pragma once

#include <nlohmann/json.hpp>

#include <cstdint>
#include <string>

namespace cesium
{
namespace jsonc
{

using Json = nlohmann::json;

// 解析 JSONC 文本(BOM 已剥离、允许 // 与 /* */ 注释、失败不抛异常)。
// 输入为空或解析失败 → 返回 discarded(调用方用 is_discarded() 判断)。
inline Json parse(const std::string& text)
{
    const char* p = text.data();
    size_t n = text.size();

    // 剥离 UTF-8 BOM
    if (n >= 3 && static_cast<unsigned char>(p[0]) == 0xEF &&
        static_cast<unsigned char>(p[1]) == 0xBB && static_cast<unsigned char>(p[2]) == 0xBF)
    {
        p += 3;
        n -= 3;
    }
    if (n == 0) return Json::value_t::discarded;

    return Json::parse(p, p + n, nullptr,
                       /*allow_exceptions=*/false,
                       /*ignore_comments=*/true);
}

// 解析结果可用(是对象)才继续; 否则一切取值都返回默认值。
inline bool usable(const Json& j)
{
    return !j.is_discarded() && j.is_object();
}

// ---------- 取值(全部带默认值, 类型不符即返回默认值) ----------

inline std::string str(const Json& j, const char* key, const std::string& def = std::string())
{
    if (!usable(j)) return def;
    auto it = j.find(key);
    if (it == j.end() || !it->is_string()) return def;
    return it->get<std::string>();
}

inline bool boolean(const Json& j, const char* key, bool def)
{
    if (!usable(j)) return def;
    auto it = j.find(key);
    if (it == j.end() || !it->is_boolean()) return def;
    return it->get<bool>();
}

// 需要区分"键缺失"与"键存在"时用(如 sidecar 的 enabled: 缺失保持默认, 存在则按值覆盖)。
// present 置为"键是否存在"; 键存在但类型不是 bool 时返回 def(调用方传 false 即
// 复刻旧行为: 非 true 的token 一律当 false)。
inline bool boolean_present(const Json& j, const char* key, bool& present, bool def = false)
{
    present = false;
    if (!usable(j)) return def;
    auto it = j.find(key);
    if (it == j.end()) return def;
    present = true;
    return it->is_boolean() ? it->get<bool>() : def;
}

// 取整数: 支持 JSON 整数/浮点(截断)与字符串形式的数字(兼容手改过的配置)。
// 失败返回 def。注意范围校验(如超时 [1,3600])由调用方负责, 这里不做业务裁剪。
inline int64_t integer(const Json& j, const char* key, int64_t def)
{
    if (!usable(j)) return def;
    auto it = j.find(key);
    if (it == j.end()) return def;
    if (it->is_number_integer()) return it->get<int64_t>();
    if (it->is_number_unsigned()) return static_cast<int64_t>(it->get<uint64_t>());
    if (it->is_number_float()) return static_cast<int64_t>(it->get<double>());
    if (it->is_string())
    {
        const std::string s = it->get<std::string>();
        try { return std::stoll(s); } catch (...) { return def; }
    }
    return def;
}

// 取浮点。失败返回 def(范围校验同样由调用方负责)。
inline double number(const Json& j, const char* key, double def)
{
    if (!usable(j)) return def;
    auto it = j.find(key);
    if (it == j.end()) return def;
    if (it->is_number()) return it->get<double>();
    if (it->is_string())
    {
        const std::string s = it->get<std::string>();
        try { return std::stod(s); } catch (...) { return def; }
    }
    return def;
}

// 取子对象(不存在/类型不符返回 discarded, usable() 为 false)
inline const Json& object(const Json& j, const char* key)
{
    static const Json kEmpty = Json::value_t::discarded;
    if (!usable(j)) return kEmpty;
    auto it = j.find(key);
    if (it == j.end() || !it->is_object()) return kEmpty;
    return *it;
}

// 取数组(不存在/类型不符返回空数组, 便于直接 for-range)
inline const Json& array(const Json& j, const char* key)
{
    static const Json kEmpty = Json::array();
    if (!usable(j)) return kEmpty;
    auto it = j.find(key);
    if (it == j.end() || !it->is_array()) return kEmpty;
    return *it;
}

} // namespace jsonc
} // namespace cesium
