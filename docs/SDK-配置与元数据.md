# SDK 配置与元数据

命名空间：`CesiumLoader.SDK.Configuration`（配置）；`CesiumLoader.SDK.Manifests`（元数据）

## SdkConfig — mod 配置 (JSON)

每个 mod 一个独立 JSON 配置文件，**读写即存**，零外部依赖。

```csharp
public static class SdkConfig
{
    public static string ConfigDirectory { get; }             // 配置目录(自动创建)
    public static string ConfigPath(string modName);          // 配置文件完整路径
    public static T Load<T>(string modName) where T : class, new();  // 读取
    public static bool Save<T>(string modName, T value);      // 保存, 失败静默 false
    public static bool Exists(string modName);                // 配置是否存在
}
```

### 路径

- `CESIUM_MODS_DIR\..\configs\{modName}.json`（即 `<游戏目录>/AstralParty_ModLoader/configs/{modName}.json`）
- 未设置环境变量时回退：`%LocalAppData%\AstralParty_ModLoader\configs\{modName}.json`
- `ConfigPath` 里 modName 的非法文件名字符会被替换为下划线。

### 配置对象约定（手写 JSON，零外部依赖）

- **公开字段（public field）才会被读写；属性（property）忽略**
- 支持类型：`string` / `int` / `long` / `float` / `double` / `bool` 及它们的**数组**
- 文件不存在或损坏时 `Load` 返回 `default(T)`（即 `new T()`），**不抛异常**

### 示例

```csharp
public class MyModConfig
{
    public bool Enabled = true;
    public int DelayMs = 100;
    public string[] Keywords = { "攻击", "防御" };
}

// 读取 (首次运行生成默认配置)
var cfg = SdkConfig.Load<MyModConfig>("MyMod");
if (!SdkConfig.Exists("MyMod"))
    SdkConfig.Save("MyMod", cfg);   // 写默认值方便用户编辑

// 保存
cfg.DelayMs = 500;
SdkConfig.Save("MyMod", cfg);
```

生成的 JSON 形如：

```json
{ "Enabled":true, "DelayMs":100, "Keywords":["攻击","防御"] }
```

## ModConfig — 模组配置与热重载

新模组推荐使用 `ModConfig`，文件位于 `mods/{ModId}/config.json`（与 DLL 同目录）。

```csharp
var cfg = Config;                       // ModBase 子类可直接取; 或 ModConfig.ForMod(ctx)
cfg.SetDefault("speed", 2.0);           // 缺键时补默认值
float speed = cfg.GetFloat("speed", 2.0);
cfg.Set("speed", 3.0);
cfg.Save();
```

- 键是字符串；值支持 bool / 整数 / 浮点 / 字符串 / 枚举 / 嵌套简单对象 / List。
- 解析失败不会抛异常；启动时损坏的文件会备份为 `.bak`，之后使用默认值。

### 配置项说明（_comments）

`config.json` 顶层可以带一个保留键 `_comments`，为每个配置项写一句（或多语言）说明。Toys 的模组配置弹窗会按当前界面语言显示这些说明；没有 `_comments` 的旧配置保持原样。

```json
{
  "enabled": true,
  "cooldownSeconds": 1,

  "_comments": {
    "cooldownSeconds": {
      "zh-Hans": "两次置前之间的最短间隔（秒）",
      "zh-Hant": "兩次置前之間的最短間隔（秒）",
      "en": "Minimum interval between focus requests, in seconds",
      "ja": "ウィンドウを前面化する最短間隔（秒）"
    },
    "enabled": "是否启用回合置前"
  }
}
```

- `_comments` 是元数据，不是业务配置：`ModConfig.All` / `Count` 不包含它，Toys 保存表单时原样保留并放在文件末尾。
- 每个字段的值可以是字符串（所有语言共用一句说明，第三方 mod 只写中文也能用），也可以是对象，语言键为 `zh-Hans` / `zh-Hant` / `en` / `ja`，允许只写其中几种。
- 语言别名会归一：`zh-CN` → `zh-Hans`，`zh-TW` / `zh-HK` → `zh-Hant`，`en-US` → `en`，`ja-JP` → `ja`。
- 显示回退顺序：当前语言 → 同语系中文 → 英文 → 日文 → 文件里第一个可用语言。
- 只支持顶层字段；嵌套对象按整个字段给一条说明。说明是纯文本，不解析 HTML，可以带换行，单条最多 2000 字符。

写入说明的 API：

```csharp
public sealed class ConfigDoc
{
    public static ConfigDoc All(string text);                         // 四种语言共用
    public static ConfigDoc Of(string zhHans, string zhHant = null,    // 按语言分别指定,
                               string en = null, string ja = null);    // 未提供的语言不写入
}

public bool Describe(string key, ConfigDoc doc);      // 只写/更新说明, 内容没变不标脏
public bool SetDefault<T>(string key, T defaultValue, ConfigDoc doc);  // 补默认值 + 写说明
```

推荐在 mod 初始化时补齐说明，用 `SaveIfDirty()` 落盘；已经存在的用户值不会被覆盖：

```csharp
public override void OnInitialize()
{
    var cfg = Config;
    if (cfg != null)
    {
        cfg.SetDefault("cooldownSeconds", 1, ConfigDoc.Of(
            "两次置前之间的最短间隔（秒）",
            "兩次置前之間的最短間隔（秒）",
            "Minimum interval between focus requests, in seconds",
            "ウィンドウを前面化する最短間隔（秒）"));
        cfg.SetDefault("enabled", true, ConfigDoc.All("是否启用本模组"));
        cfg.SaveIfDirty();
    }
}
```

旧配置升级后会补上说明，值保持不变；重复调用相同内容不会触发保存。旧式 `SdkConfig`（集中放在 `configs/{modName}.json`）不参与这套机制，说明只在该文件里本来就存在时才会被 Toys 显示。

### 热重载

SDK 在主线程约每秒检查一次已缓存的 `ModConfig` 文件，内容变化且解析成功时自动重载：

- 新式 `ModBase` 子类覆写 `OnConfigChanged()`，重载成功后调用；
- 旧式 `ModBase.Run(init, tick)` 模组可订阅 `Config.Changed`（只在从磁盘重载成功时触发；`Set` / `Save` 不触发，避免回调自触发）；
- `Config.Revision` 可用于自行比较版本；
- `Config.ReloadIfChanged()` 可手动触发一次检查；
- 外部修改写到一半或 JSON 损坏时**保留当前内存配置**，不覆盖磁盘文件；同一份坏内容只警告一次；
- 需要重启才能应用的设置（例如资源重载、总开关）由各模组文档说明。

```csharp
public override void OnConfigChanged()
{
    LoadConfig(isInitial: false);
    Log.Info("配置已热重载");
}
```

## ModManifest — mod 元数据声明

打在 mod 程序集的 `ModEntry` 类上（或程序集级），SDK 通过 `SdkInfo` 读取。

```csharp
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class ModManifestAttribute : Attribute
{
    public ModManifestAttribute(string name, string version = "1.0.0",
        string author = "", string description = "");

    public string Name { get; }
    public string Version { get; }
    public string Author { get; }
    public string Description { get; }
}
```

### 用法

```csharp
[ModManifest("我的Mod", "1.0.0", "作者名", "这个 mod 做什么")]
public static class ModEntry
{
    public static void Main() { /* ... */ }
}
```

## SdkInfo — 当前 mod 信息

```csharp
public static class SdkInfo
{
    public static ModManifestAttribute ManifestOf(Assembly assembly);  // 读程序集元数据
    public static Assembly CallingAssembly();                          // 调用者程序集(一般是 mod 自己的)
}
```

- `ManifestOf`：未声明 `[ModManifest]` 时返回基于程序集名的默认值（`name = 程序集名`，其余空）。
- `CallingAssembly`：优先 `GetCallingAssembly()`；失败时用 `StackTrace` 找第一个非 SDK 程序集（适配 HybridCLR 解释器栈遍历）。

## SdkManifest — sidecar 导出

把 mod 的 `[ModManifest]` 元数据导出成**同名 .json**（`ActivityLogMod.dll` → `ActivityLogMod.json`），供外部工具（如 mod 列表工具）读取显示，**无需加载 mod 程序集**。

```csharp
public static class SdkManifest
{
    public static void ExportSidecar();                    // 幂等, 失败静默
    public static string Serialize(ModManifestAttribute m); // 转 JSON
}
```

- 写入位置：`CESIUM_MODS_DIR\{程序集名}.json`。
- 不依赖 `Assembly.Location`（HybridCLR 解释器下会抛 `MissingMethodException`），而是用环境变量定位 mods 目录。
- 典型调用：在 `ModEntry.Main()` 里执行一次 `SdkManifest.ExportSidecar()`。

### sidecar JSON 格式

```json
{ "name":"ActivityLogMod", "version":"1.0.0", "author":"", "description":"行为日志" }
```

## 完整示例：带配置 + 元数据的 mod

```csharp
[ModManifest("示例Mod", "1.0.0", "作者", "演示配置与元数据")]
public static class ModEntry
{
    public static void Main()
    {
        SdkManifest.ExportSidecar();              // 导出 sidecar
        ModBase.Run(OnInit, OnTick, tag: "Demo");
    }

    static void OnInit()
    {
        var cfg = SdkConfig.Load<DemoConfig>("Demo");
        // ... 按 cfg 初始化
    }

    static void OnTick() { GameEvents.EnsureHooked(); }
}

public class DemoConfig
{
    public bool Enabled = true;
    public int IntervalSec = 1;
}
```
