// schema.h - 加载器配置字段的元数据(供 shell 的 ls / get / set 使用)
//
// 唯一事实来源是 config.h 的 LoaderConfig; 这里只补三件 config.h 表达不了的东西:
//   1) 取值范围/枚举值(用于 set 校验, 避免把 0.5 倍速、99 秒超时写进配置);
//   2) 生效时机(立即 / 仅下次启动 / 只能开启) —— 决定 set 之后要不要提示重启;
//   3) 一行说明(ls 直接展示, 不用去翻 config.h 的长注释)。

#pragma once

#include "config/config.h"

#include <cstddef>
#include <string>

namespace cesium::shell
{

enum class ValueType
{
    Bool,
    Int,
    Double,
    String,
    Enum,
};

// 生效时机
enum class Apply
{
    Immediate,   // 改完立刻生效
    StartOnly,   // 只能"从关到开"(线程/通道是常驻的, 关不掉)
    Restart,     // 写进配置, 下次启动才生效
};

struct Field
{
    const char* name;
    ValueType type;
    double min_value;                 // Int / Double 用
    double max_value;
    const char* const* enum_values;   // 以 nullptr 结尾; 仅 Enum 用
    Apply apply;
    const char* desc;
};

const Field* loader_fields(size_t& count);
const Field* find_loader_field(const std::string& name);

// 当前值(展示文本; 字符串原样, 布尔 true/false, 浮点去掉无意义尾零)。
std::string field_value(const LoaderConfig& cfg, const Field& f);

// 类型说明: "bool" / "int 1..3600" / "double 1..100" / "enum auto|direct|invoke" / "string"
std::string field_type_text(const Field& f);

// 生效说明: "立即生效" / "只能开启" / "重启后生效"
std::string field_apply_text(const Field& f);

// 解析并写入 cfg; 失败时 err 给出原因, cfg 不变。
bool field_assign(LoaderConfig& cfg, const Field& f, const std::string& text, std::string& err);

// 写入 JSON 用的字面量(布尔/数字/带引号字符串), 由 field_assign 相同的规则归一化。
std::string field_json_literal(const Field& f, const std::string& text);

} // namespace cesium::shell
