// jsonc_patch.h - JSONC 顶层键值的"文本级"改写
//
// 为什么不用 nlohmann 重新序列化:
//   doorstop_config.json 与部分 mod 配置里**带注释**(见 jsonc.h), 而且键的顺序是人为排的。
//   整体 dump 会把注释全部丢掉、键按字母重排 —— 用户手改过的文件被 shell 写一次就毁了。
//   这里只做"把某个顶层键的值区间换成新字面量", 其余字节原样保留(包括注释、缩进、BOM)。
//
// 只处理**顶层对象**的键(加载器配置与 mod 的 config.json 都是这个形状)。
// 不负责语法校验: 调用方写盘前必须用 cesium::jsonc::parse 再解析一次。

#pragma once

#include <string>
#include <vector>

namespace cesium::jsonc
{

// 在 text 的顶层对象里查找 key 的值区间(去掉了值前后的空白/注释)。
// 找到返回 true 并写入 [begin, end); 找不到或顶层不是对象返回 false。
bool find_top_level_value(const std::string& text, const std::string& key,
                          size_t& begin, size_t& end);

// 把顶层键 key 的值替换为 literal(JSON 字面量, 由调用方保证合法);
// 键不存在时插入到根对象末尾。成功返回 true 并写 out。
// 顶层不是 JSON 对象时返回 false。
bool set_top_level_value(const std::string& text, const std::string& key,
                         const std::string& literal, std::string& out);

// 顶层对象现有的键(按出现顺序)。用于 "值不存在时提示可用键"。
// 解析失败时返回空列表。
void list_top_level_keys(const std::string& text, std::vector<std::string>& keys);

} // namespace cesium::jsonc
