// state.h - 加载器运行期状态快照(shell 与引导线程共享)
//
// 为什么需要它:
//   引导线程里的 LoaderConfig / mod 列表 / 版本信息原本都是局部变量, 加载完就丢了。
//   调试 shell 运行在**另一个线程**, 还要能回答"当前配置是什么、哪些 mod 加载成功、
//   游戏和 Unity 版本是多少", 所以把这些事实收进一个带锁的全局快照。
//
// 约定:
//   - 写方只有引导线程(启动阶段)与 shell 线程(改配置), 都走这里的函数;
//   - 读方拿的是**拷贝**(Snapshot), 不持有内部指针, 避免和写方竞争;
//   - 这里不碰 il2cpp / Unity, 纯原生数据。

#pragma once

#include "config/config.h"

#include <windows.h>

#include <string>
#include <vector>

namespace cesium::runtime
{

struct ModStatus
{
    std::string id;
    std::string version;
    std::string detail;      // "ok 12ms" / "load failed: ..." / "disabled" / "skipped (...)"
    bool enabled = true;
    bool loaded = false;
};

struct Snapshot
{
    LoaderConfig cfg;
    std::vector<ModStatus> mods;
    std::string game_version;    // 游戏 bundleVersion, 取不到为空
    std::string unity_version;   // UnityPlayer.dll 的文件版本, 取不到为空
    std::wstring exe_path;       // 游戏 exe 完整路径
    ULONGLONG started_utc_ms = 0;  // 启动时刻(UTC, 毫秒); 用系统时间算, 不受变速影响
};

// 引导线程启动时调用一次(记录起点时间)。
void mark_started();

Snapshot snapshot();

LoaderConfig config();
void set_config(const LoaderConfig& cfg);

void set_mods(std::vector<ModStatus> mods);
void set_versions(const std::string& game_version, const std::string& unity_version);
void set_exe_path(const std::wstring& exe_path);

// 活动日志转发开关(可由 shell 热改; 转发线程每轮读一次)。
bool forward_enabled();
void set_forward_enabled(bool on);

} // namespace cesium::runtime
