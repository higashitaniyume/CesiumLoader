// config.h - Doorstop 式配置 (doorstop_config.json)
//
// 加载器从 AstralParty_ModLoader\doorstop_config.json 读取配置。
// 所有字段可选, 解析失败/缺失时使用默认值 —— 配置损坏不会阻止游戏启动。

#pragma once

#include <string>

struct LoaderConfig
{
    // 总开关: false 时加载器完全静默(不弹控制台、不加载任何 mod), 游戏原样运行
    bool enabled = true;

    // 托管引导程序: 原生层只负责加载这一个 DLL 并调用入口
    std::string bootstrapAssembly = "CesiumLoader.Bootstrap.dll";
    std::string bootstrapType = "CesiumLoader.Bootstrap.Bootstrap";
    std::string bootstrapMethod = "Main";

    // 编排层选择:
    //   false (默认): 原生层直接加载 sdk/mods 并调用入口 —— 可靠路径,
    //                  用 il2cpp 原生 API, 不受 HybridCLR AOT 反射裁剪影响。
    //   true (实验):   原生层只加载 bootstrap DLL, 由托管代码编排一切。
    //                  注意: HybridCLR 会裁剪部分反射 API (如 Assembly.GetType),
    //                  托管编排可能不可用; 此开关用于实验/验证。
    bool useManagedBootstrap = false;

    // 等待超时(秒)
    unsigned gameAssemblyTimeoutSec = 60;   // GameAssembly.dll 出现
    unsigned domainTimeoutSec = 30;         // il2cpp domain 就绪
    unsigned hybridclrTimeoutSec = 60;      // HybridCLR 热更(AstralParty.Runtime)就绪

    // 控制台
    bool consoleEnabled = true;             // 是否分配控制台窗口
    bool consoleTopmost = true;             // 控制台窗口是否置顶

    // mod 日志 -> 控制台 转发线程
    bool forwardActivityLog = true;
};

// 从 config_path 读取配置。文件不存在/解析失败返回默认配置(不抛异常)。
LoaderConfig load_config(const std::wstring& config_path);
