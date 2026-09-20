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
    bool consoleTopmost = false;            // 控制台窗口是否置顶(默认不置顶: 置顶会一直压着游戏)

    // mod 日志 -> 控制台 转发线程
    bool forwardActivityLog = true;

    // 变速引擎基础倍率: 加载器 hook 装好后立即应用, 一直保持。
    // 1.0 = 正常(默认), 2.0 = 全程 2 倍速, 0.5 = 全程半速。
    // 设为 1.0 或 0 则不启用基础倍率(SpeedHackMod 可用热键临时变速)。
    double speedhackBaseSpeed = 1.0;

    // 当前分发的 SDK 版本(SemVer)。加载器用它校验 mod 声明的 SdkVersion,
    // 不兼容(mod 要求更高版本)时拒绝加载该 mod 并记录警告。
    // 发布新版 SDK 时手动更新此字段 + dist\sdk\CesiumLoader.SDK.dll。
    std::string sdkVersion = "2.1.1";

    // 加载器自身版本(SemVer)。与发布 tag (modloader-<版本>) 对应, 启动横幅会打印。
    // 与 sdkVersion 独立递增: 改动引导/互操作/打包时递增此值。
    static constexpr const char* loaderVersion = "2.1.1";
};

// 从 config_path 读取配置。文件不存在/解析失败返回默认配置(不抛异常)。
LoaderConfig load_config(const std::wstring& config_path);
