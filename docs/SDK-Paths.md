# SDK 路径服务 (Path Service)

> 适用版本：CesiumLoader 3.1.1 起。
> 提供可靠的游戏、加载器与模组各级路径发现、定位及子路径拼接 API，规避 HybridCLR 裁剪和 BCL 缺失陷阱。

---

## 背景与设计原则

在 Unity IL2CPP + HybridCLR 运行时环境中，传统 .NET 的路径查找方式存在以下已知问题：
- **`AppDomain.CurrentDomain.BaseDirectory` 与 `Assembly.Location`**：在热更程序集或解释执行层中通常返回空字符串、抛出异常或指向不可预期的临时缓存路径。
- **手动拼接容易出错**：各模组作者自行拼接 `..` 向上推导，或因 Windows 与 Unity 斜杠差异导致路径解析失败。
- **环境隔离**：加载器原生层会创建每次启动独立的会话日志和专用通信目录（如 `speed/`、`window/`、`audio/`），模组侧必须准确对齐加载器注入的环境变量。

CesiumLoader SDK 提供了官方路径服务，统一由加载器注入的 `CESIUM_*` 环境变量与游戏基准路径推导，支持智能回退与安全操作。

---

## 命名空间与类设计

为了兼顾代码简洁性并彻底避免与 `System.IO.Path` 发生编译器命名冲突，SDK 提供了三种访问形式：

| 类全名 | 适用场景 |
|---|---|
| **`CesiumLoader.SDK.IO.Path`** | 完整实现类。当代码未引入 `using System.IO;` 时推荐使用，内置对常用 `System.IO.Path` 方法（如 `Combine`、`GetFileName` 等）的包装转发。 |
| **`CesiumLoader.SDK.SdkPath`** | 根命名空间别名门面。位于 `CesiumLoader.SDK` 命名空间下，在任何同时使用 `System.IO` 的文件中均可直接调用，**彻底避免名称歧义**。 |
| **`CesiumLoader.SDK.IO.Paths`** | 复数别名门面。位于 `CesiumLoader.SDK.IO` 命名空间下，适合偏好使用 `Paths.GameDirectory` 语法的开发者。 |

---

## 核心目录属性 (Properties)

所有目录属性均返回不带末尾分隔符的标准绝对路径（环境变量优先，带自动回退）：

| 属性 | 说明 | 示例路径 |
|---|---|---|
| `GameDirectory`<br>`GameRootDirectory` | 游戏根目录（包含 `AstralParty_CN.exe` 的目录） | `C:\Games\Astral Party\8vJXn6CN` |
| `LoaderDirectory`<br>`LoaderRootDirectory` | 加载器根目录（默认为游戏目录下的 `AstralParty_ModLoader`） | `C:\Games\...\AstralParty_ModLoader` |
| `ModsDirectory` | 模组总目录（`AstralParty_ModLoader\mods`） | `...\AstralParty_ModLoader\mods` |
| `ConfigsDirectory` | 模组配置总目录（`AstralParty_ModLoader\configs`） | `...\AstralParty_ModLoader\configs` |
| `LogsDirectory` | 日志总目录（`AstralParty_ModLoader\logs`） | `...\AstralParty_ModLoader\logs` |
| `SkinsDirectory` | 卡牌/角色皮肤总目录（`AstralParty_ModLoader\skins`） | `...\AstralParty_ModLoader\skins` |
| `AudioDirectory` | 音频子系统通信目录（`AstralParty_ModLoader\audio`） | `...\AstralParty_ModLoader\audio` |
| `AudioSchemesDirectory` | 音效替换方案目录（`AstralParty_ModLoader\audio-schemes`） | `...\AstralParty_ModLoader\audio-schemes` |
| `SpeedDirectory` | 变速控制通信目录（`AstralParty_ModLoader\speed`） | `...\AstralParty_ModLoader\speed` |
| `WindowDirectory` | 窗口焦点通信目录（`AstralParty_ModLoader\window`） | `...\AstralParty_ModLoader\window` |
| `SdkDirectory` | SDK 依赖程序集目录（`AstralParty_ModLoader\sdk`） | `...\AstralParty_ModLoader\sdk` |
| `BootstrapDirectory` | 托管引导程序目录（`AstralParty_ModLoader\bootstrap`） | `...\AstralParty_ModLoader\bootstrap` |
| `CurrentModDirectory` | 当前正在执行的模组目录（从 `ModContext.Current` 动态获取，无上下文时回退为 `ModsDirectory`） | `...\mods\MyMod` |

---

## 会话日志文件属性

加载器每次启动会独立生成带时间戳与 PID 的会话日志。SDK 路径服务直接提供当前启动对应的日志文件路径：

| 属性 | 来源环境变量 | 默认回退 |
|---|---|---|
| `CurrentLogFile` | `CESIUM_LOG_FILE` | `<LogsDirectory>\cesium-loader.log` |
| `CurrentActivityLogFile` | `CESIUM_ACTIVITY_LOG_FILE` | `<LogsDirectory>\activity-mod.log` |
| `CurrentErrorLogFile` | `CESIUM_ERROR_LOG_FILE` | `<LogsDirectory>\mod-errors.log` |

---

## 查找与拼接方法 (Methods)

无需手动多次调用 `Path.Combine`，直接通过语义化 API 定位各级资源：

### 1. 游戏与加载器子路径
- `GetGamePath(params string[] subPaths)`：拼接游戏根目录下的子路径。
  ```csharp
  string exePath = SdkPath.GetGamePath("AstralParty_CN.exe");
  string dataDir = SdkPath.GetGamePath("AstralParty_CN_Data", "Managed");
  ```
- `GetLoaderPath(params string[] subPaths)`：拼接加载器目录下的子路径。
  ```csharp
  string versionDll = SdkPath.GetLoaderPath("version.dll");
  ```

### 2. 模组与数据目录
- `GetModDirectory(string modId)`：获取指定模组目录（`mods\{modId}`）。
- `GetModPath(string modId, params string[] subPaths)`：拼接指定模组目录下的子路径。
  ```csharp
  string icon = SdkPath.GetModPath("MyMod", "assets", "icon.png");
  ```
- `GetModPath(params string[] subPaths)`：拼接**当前执行模组**目录下的子路径。
  ```csharp
  string localConfig = SdkPath.GetModPath("config.json");
  ```
- `GetModDataDirectory(string modId)`：获取模组数据目录（`mods\{modId}\data`）。
- `GetModDataPath(string modId, params string[] subPaths)`：拼接模组数据目录下的子路径。
  ```csharp
  string packFile = SdkPath.GetModDataPath("LanguageUnlockMod", "en.pack");
  ```

### 3. 配置与日志
- `GetModConfigPath(string modId)`：按优先级探测模组配置文件路径：
  1. 优先检查 `mods\{modId}\config.json`（若文件存在）。
  2. 其次检查 `configs\{modId}.json`（若文件存在）。
  3. 若均不存在，模组目录存在时返回 `mods\{modId}\config.json`，否则返回 `configs\{modId}.json`。
- `GetConfigPath(string fileNameOrModId)`：获取 `configs` 目录下的指定配置文件（未带 `.json` 时会自动追加）。
- `GetLogPath(params string[] subPaths)`：拼接 `logs` 目录下的子路径。

### 4. 皮肤与音效方案
- `GetSkinDirectory(string skinName)`：获取指定卡牌/角色皮肤目录（`skins\{skinName}`）。
- `GetSkinPath(string skinName, params string[] subPaths)`：拼接指定皮肤目录下的子路径。
- `GetAudioPath(params string[] subPaths)`：拼接 `audio` 目录下的子路径。
- `GetAudioSchemeDirectory(string schemeId)`：获取指定音效方案目录（`audio-schemes\{schemeId}`）。
- `GetAudioSchemePath(string schemeId, params string[] subPaths)`：拼接音效方案下的音频文件。

---

## 辅助与安全方法

### 1. 目录创建与文件名净化
- **`EnsureDirectoryExists(string directoryPath)`**：
  安全递归创建目录（若不存在），内部捕获并吸收潜在异常，返回该目录路径。
  ```csharp
  string exportDir = SdkPath.EnsureDirectoryExists(SdkPath.GetModPath("exports"));
  ```
- **`SanitizeFileName(string fileName, char replacement = '_')`**：
  将传入字符串中的非法文件名字符（如 `:`、`*`、`?`、`/`、`\` 等）替换为安全字符（默认 `_`）。
- **`Normalize(string path)`**：
  标准化路径分隔符为当前操作系统的标准分隔符，并去除尾部多余的分隔符。

### 2. System.IO.Path 常用操作转发
`CesiumLoader.SDK.IO.Path`（以及 `SdkPath` / `Paths`）对 `System.IO.Path` 的常用静态方法进行了完整包装转发：
- `Combine(...)`：支持 2 个、3 个、4 个参数及 `params string[]`。
- `GetDirectoryName(path)`
- `GetFileName(path)`
- `GetFileNameWithoutExtension(path)`
- `GetExtension(path)`
- `ChangeExtension(path, newExtension)`
- `GetFullPath(path)`
- `HasExtension(path)`
- `GetPathRoot(path)`
- `IsPathRooted(path)`
- `GetInvalidFileNameChars()`
- `GetInvalidPathChars()`
- `DirectorySeparatorChar`、`AltDirectorySeparatorChar`、`PathSeparator`、`VolumeSeparatorChar`

---

## 代码使用示例

### 示例 1：在模组中读取自身的数据与配置

```csharp
using CesiumLoader.SDK;
using CesiumLoader.SDK.Mods;
using System.IO;

public class MyModController : ModBase
{
    public override void OnInitialize()
    {
        // 直接使用 SdkPath，完全无需手动拼接环境或处理 System.IO 命名冲突
        string configPath = SdkPath.GetModConfigPath("MyMod");
        string assetPath = SdkPath.GetModPath("assets", "banner.png");
        string dataPath = SdkPath.GetModDataPath("MyMod", "rules.json");

        if (File.Exists(dataPath))
        {
            string content = File.ReadAllText(dataPath);
            Log.Info("已加载数据文件: " + dataPath);
        }
    }
}
```

### 示例 2：纯粹使用 CesiumLoader.SDK.IO.Path

```csharp
using CesiumLoader.SDK.IO;
using CesiumLoader.SDK.Mods;

public class AnotherModController : ModBase
{
    public override void OnInitialize()
    {
        // 无需 using System.IO; Path 类已内置 Combine / GetFileName 等常用工具
        string gameExe = Path.GetGamePath("AstralParty_CN.exe");
        string skinCard = Path.GetSkinPath("default", "card_back.png");

        string fullCustomPath = Path.Combine(Path.LogsDirectory, "CustomOutput", "trace.log");
        Path.EnsureDirectoryExists(Path.GetDirectoryName(fullCustomPath));
    }
}
```

---

## 注意事项与平台约束

1. **避免使用 `AppDomain.CurrentDomain.BaseDirectory`**：在 IL2CPP + HybridCLR 下，请一律使用 `SdkPath.GameDirectory` 或 `SdkPath.LoaderDirectory`。
2. **避免使用不支持的 BCL 文件流方法**：在热更层中，`FileInfo.Length` 和 `FileStream.Flush(bool)` 已知会在真机报 `MethodNotFind`。建议直接复用 `File.ReadAllText`、`File.WriteAllText`、`File.Exists`、`Directory.Exists` 等成熟轻量 API。
3. **日志输出**：若需记录诊断数据，建议优先使用 `Log.Info` / `Log.Warn` 或写入 `SdkPath.LogsDirectory`，请勿将日志散落写入游戏根目录。
