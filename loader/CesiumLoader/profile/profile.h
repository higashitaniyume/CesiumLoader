// profile.h - 多 profile(每个 profile 一套独立账号/存档目录)
//
// 背景(实测):
//   游戏的账号与存档并不完全跟随 Application.persistentDataPath —— BnSdk 的
//   设备/令牌文件与 Temp\SaveFile.es3 用的是**硬编码绝对路径**
//   %USERPROFILE%\AppData\LocalLow\<company>\<product>(公司/产品名取自 exe 旁的
//   <exe>_Data\app.info)。因此改 app.info 换个产品名并不能把账号隔离出去:
//   Player.log 会跟着新名字走, BnSdk/SaveFile.es3 仍写回原目录。
//
// 本模块负责 profile 的"选择与准备":
//   1. 解析 profile 名(配置字段 profile, 环境变量 CESIUM_PROFILE 在 config 层覆盖);
//   2. 从 <exe>_Data\app.info 推出游戏原地写入目录(下称 source);
//   3. 算出 profile 目录(下称 target), 首次使用时创建, 并把 source 里
//      com.unity.addressables(约 7 GB 的下载缓存)**硬链接**播种过去 ——
//      零字节复制、瞬时完成, 新 profile 不必重新下载; 硬链接在目标侧删除也不会
//      影响源文件, 因此游戏里的"清缓存"不会破坏主 profile。
//
// 真正的重定向在 fsredir 模块(文件 API 前缀改写), 本模块只准备路径。

#pragma once

#include <string>
#include <vector>

namespace cesium::profile
{

struct ProfilePaths
{
    bool active = false;        // false = 不重定向(游戏写自己的目录)
    std::wstring name;          // profile 名(或绝对路径)
    std::wstring source_dir;    // 游戏原本会写的目录
    std::wstring target_dir;    // profile 目录

    // 待播种的下载缓存(相对 com.unity.addressables 的路径)。
    // 枚举必须在**安装文件重定向之前**完成(装好后 FindFirstFileW 会被改写),
    // 所以 prepare 只负责列清单, 真正的硬链接由 start_seed 在后台线程做。
    std::vector<std::wstring> seed_files;
};

// 解析并准备 profile:
//   name_from_config 为空 = 不启用(返回 true 且 active=false);
//   任何失败都只记日志并返回 false —— 调用方应照常启动游戏, 不要因为
//   profile 配置有问题就挡住玩家。
bool prepare(const std::string& name_from_config, ProfilePaths& out);

// 装好文件重定向之后调用: 有待播种文件时起后台线程逐个建硬链接(幂等, 可续传)。
// 放到后台是为了不让首次建 profile 的几秒拖慢引导线程(mod 加载)。
void start_seed(ProfilePaths& paths);

} // namespace cesium::profile
