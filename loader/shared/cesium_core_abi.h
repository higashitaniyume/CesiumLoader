// cesium_core_abi.h - 代理层 (version.dll) 与加载器核心 (CesiumLoader.dll) 之间的最小契约
//
// 两个原生项目都必须包含本头文件, 而且只有这里能改:
//   * 核心 DLL 的文件名 —— 代理按它去 LoadLibrary;
//   * ABI 版本号 —— 代理加载核心后先比对, 不一致就拒绝启动并留下日志。
//
// 为什么要有 ABI 号: 代理与核心是两个独立文件, 整包安装、手动覆盖、版本回滚都可能让它们
// 不同步(旧代理配新核心, 或者反过来)。多一个显式比对, 坏组合会变成日志里一条能看懂的记录,
// 而不是"模组莫名其妙不加载"。

#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

// 核心 DLL 文件名: 与代理(version.dll)放在同一个目录(游戏 exe 目录)。
#define CESIUM_CORE_DLL_NAME    L"CesiumLoader.dll"

// 契约版本。代理与核心对导出/调用约定的任何不兼容改动都要 +1。
#define CESIUM_CORE_ABI_VERSION 1u

// 核心导出: 返回自己编译时的 CESIUM_CORE_ABI_VERSION, 代理用它比对。
typedef unsigned int(WINAPI* CesiumCoreAbiVersionFn)();

// 核心导出: 启动引导(幂等, 重复调用只启动一次引导线程)。
// 代理在 ABI 比对通过之后调用它。
typedef BOOL(WINAPI* CesiumCoreStartFn)();
