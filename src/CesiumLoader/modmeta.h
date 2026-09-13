// modmeta.h - mod 元数据(sidecar)解析 + 依赖拓扑排序
//
// sidecar 格式 (mods\{ModId}\{ModId}.json, 由 SDK 的 SdkManifest.ExportSidecar() 生成,
// 或由脚手架在开发期生成并随 mod 分发):
//   {"id":"X","name":"显示名","version":"1.0.0","sdkVersion":"2.0.0",
//    "permissions":1,"enabled":true,"dependencies":[{"id":"OtherMod","minVersion":"1.0.0"}]}
//
// 目录布局(与加载器扫描一致): 每 mod 一个文件夹 mods\{ModId}\{ModId}.dll,
// sidecar 在 mod 文件夹内 {ModId}.json; 兼容旧布局 mods\{ModId}.dll 平铺。
//
// enabled 字段: mod 开关(默认 true)。false = 加载器跳过该 mod(由工具/用户
// 通过 sidecar 的 "enabled" 控制)。缺失或非布尔按 true 处理。
//
// 原生层不读托管 attribute(需要反射), 因此依赖/版本/开关信息全部来自 sidecar。
// 无 sidecar 的 mod 视为"无声明", 按文件名排序加载(兼容旧 mod)。
// 本模块是纯标准库, 不依赖 il2cpp/游戏, 可脱离加载器单元测试。

#pragma once

#include <string>
#include <vector>
#include <map>
#include <set>
#include <deque>
#include <algorithm>
#include <filesystem>

namespace cesium
{

struct ModDep
{
    std::string id;
    std::string minVersion;   // 可空
};

struct ModMeta
{
    std::string id;            // 程序集名(不带 .dll), 依赖解析的 key
    std::string name;          // 显示名
    std::string version;
    std::string sdkVersion;    // 可空
    std::vector<ModDep> deps;
    int permissions = 0;       // 声明的能力位掩码(1=读对局 2=操作游戏 4=变速 8=写文件); 仅用于展示/警告
    bool enabled = true;       // mod 开关(默认 true; false = 加载器跳过)
    bool hasSidecar = false;
};

// 一个已定位的 mod: id(程序集名) + DLL 路径 + sidecar 路径(sidecar 缺失为空)
struct ModLoc
{
    std::string id;
    std::string dll;
    std::string sidecar;
};

// 扫描 mods 目录, 返回所有 mod 的位置(与加载器/工具一致的布局规则):
//   新布局: mods\{ModId}\{ModId}.dll (每 mod 一个文件夹, sidecar 在文件夹内 {ModId}.json)
//   兼容旧布局: mods\{ModId}.dll 平铺(直接放 mods 根下的 dll 仍识别, sidecar 在同目录)
// 返回列表按 id 排序。目录损坏/不存在返回空。
std::vector<ModLoc> scan_mods_dir(const std::string& mods_dir);

// 读取 sidecar 文件内容; 失败返回空串
std::string read_sidecar_text(const std::string& path);

// 解析 sidecar JSON → ModMeta; 空/损坏返回 hasSidecar=false
ModMeta parse_sidecar(const std::string& json);

// SemVer 比较(只比较主.次.修订): a>b 返回 >0, 相等 0, a<b <0
int semver_compare(const std::string& a, const std::string& b);

// 从 mods 目录读取所有 mod 的元数据(读 {stem}.json)
// mods_dir: 目录路径; 返回 map: stem → ModMeta(只含有 sidecar 的)
std::map<std::string, ModMeta> read_mods_meta(const std::string& mods_dir);

// 依赖拓扑排序(Kahn):
//   dll_stems: 全部 mod 程序集名列表
//   metas:     有 sidecar 的元数据 map
//   sdk_version: 加载器声明的当前 SDK 版本(空 = 不检查)
//   rejected_out: 输出被拒的 mod 名(缺失依赖/版本不符/循环)
// 返回按依赖顺序排列的程序集名列表(被拒的排除)。确定性(同级按名字排序)。
std::vector<std::string> sort_mods_by_deps(
    const std::vector<std::string>& dll_stems,
    const std::map<std::string, ModMeta>& metas,
    const std::string& sdk_version,
    std::vector<std::string>* rejected_out = nullptr);

} // namespace cesium
