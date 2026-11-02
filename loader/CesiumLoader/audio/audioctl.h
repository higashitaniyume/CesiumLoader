// audioctl.h - 原生音频控制通道 (mod <-> 加载器)
//
// 为什么需要它:
//   1. Astral Party 为 IL2CPP 构建, Unity 原生 UnityEngine.AudioSource 方法因未在游戏代码中被调用
//      而被代码裁剪(stripping)全部剥离, 导致托管侧无法通过 AudioSource 播放自定义音频。
//   2. 热更程序集(经 HybridCLR 加载的 SDK / mod)无法 P/Invoke, 调不到 Win32 音频 API。
//   所以由原生加载器 (version.dll) 提供底层音频播放通道。
//
// 通信机制 (双通道):
//   1. UDP Socket 通道 (首选, 亚毫秒级超低延迟):
//      - 加载器监听 127.0.0.1 上的 UDP 端口 (写入环境变量 CESIUM_AUDIO_PORT 与 port.txt);
//      - 格式: UTF-8 单行命令, 如:
//          PLAY|sfx|<volume 0-1000>|<path>
//          PLAY|voice|<volume 0-1000>|<path>
//          BGM_PLAY|<volume 0-1000>|<loop 0/1>|<path>
//          BGM_STOP
//          BGM_PAUSE
//          BGM_RESUME
//          BGM_VOL|<volume 0-1000>
//          STOP|voice
//   2. 文件控制通道 (fallback, 轮询 request.txt):
//      - 目录: <游戏目录>\AstralParty_ModLoader\audio\ (环境变量 CESIUM_AUDIO_DIR)
//      - request.txt: 每次写入新指令
//      - state.txt: version=<版本> available=1 port=<端口>
//
#pragma once

#include <string>

// 控制文件目录(供环境变量 CESIUM_AUDIO_DIR 使用)。
std::wstring audioctl_dir();

// 启动原生音频服务(UDP 监听器 + MCI 播放器 + 文件通道监听)。
void audioctl_start(const std::wstring& dir);

// 停止原生音频服务并释放资源。
void audioctl_stop();

namespace cesium::audio
{
using ::audioctl_dir;
using ::audioctl_start;
using ::audioctl_stop;
}
