# 平台约束 — 第三方库与 AOT 托管裁剪

> 这份文档记录一条**用血换来的结论**，避免以后有人重复踩坑。

## 一句话结论

| 位置 | 能否用第三方库 | 原因 |
|---|---|---|
| **SDK / mod（托管 C#，跑在 HybridCLR 解释器上）** | ❌ **不能** | 游戏的 BCL 被 IL2CPP **托管裁剪**过；NuGet 上的 Newtonsoft.Json / Serilog / NLog 在 `Assembly.Load` 阶段就抛 `TypeLoadException` |
| **加载器（C++，`version.dll`）** | ✅ 可以 | nlohmann/json / spdlog / fmt 全是 header-only 或静态链接，`version.dll` 的导入表里**没有新增任何非系统 DLL** |

因此 SDK 的 JSON 与日志保持**自研**（`MiniJson` / `CesiumJson` / `SdkLog`），不引入托管第三方库 —— 这不是"嫌麻烦"，而是在这个平台上第三方托管库**根本加载不起来**。

---

## 1. 三条硬约束（均为实测，不是推测）

### 约束 1：`Assembly.Load(byte[])` 会**急解析** AssemblyRef

被加载程序集引用的每个程序集，必须在 `Load` 那一刻**已经加载好**，否则直接抛异常。

实测：`sdk\` 下按文件名排序加载时，`Serilog.Sinks.File.dll` 排在 `Serilog.dll` 前面 → 前者必然失败。
加载器已改为**多轮重试直到无进展**（`loader.cpp`），日志里能看到 `... 加载成功 (第 2 轮)`。

### 约束 2：TypeRef 在加载期就要解析 —— 缺类型 = `TypeLoadException`

```
TypeLoadException: Could not load type 'System.Diagnostics.TraceEventType' from assembly 'netstandard'
TypeLoadException: Could not load type 'System.IObserver`1' from assembly 'netstandard'
```

* 前者来自 **NuGet 版 Newtonsoft.Json**，后者来自 **System.Diagnostics.DiagnosticSource**（Serilog 的传递依赖，失败后级联到 `ActivityTraceId`）。
* 结论：**stock 的 NuGet Newtonsoft.Json 与 Serilog 在这个游戏里永远加载不起来。**

能加载的只有"纯 BCL 门面"型程序集：`System.Buffers`、`System.Numerics.Vectors`、`System.Threading.Tasks.Extensions`、`System.Memory`、`System.Threading.Channels` ✓（`System.Runtime.CompilerServices.Unsafe` 游戏已自带）。

### 约束 3：方法体里引用**缺失的 API** → 整个方法在解释期编译失败

```
MissingMethodException: MethodNotFind System.Reflection.Assembly::GetType
MissingMethodException: MethodNotFind System.Console::Write
MissingMethodException: MethodNotFind System.IO.FileInfo::get_Length
```

**方法内部的 `try/catch` 救不了它** —— 失败发生在"整个方法被编译成解释器字节码"这一步，还没执行到 `try`。
所以任何"可能不存在的 API"都必须**隔离到独立方法**里调用，由调用方 try/catch。

---

## 2. 游戏内置的那份 Newtonsoft（`BnNewtonsoft.Json`）为什么也不能用

游戏 AOT 域里**确实自带一份真 Newtonsoft**，但它是被**改名 + 裁剪**过的：

* 程序集名：`BnNewtonsoft.Json.dll`
* **命名空间也改名了**：不是 `Newtonsoft.Json.*`，而是 **`BnNewtonsoft.Json.*`**
  （`BnNewtonsoft.Json.JsonConvert`、`BnNewtonsoft.Json.Linq.JObject` …）
* AOT 镜像里共 **292 个类型**，命名空间分布：
  `BnNewtonsoft.Json` 51 / `.Utilities` 44 / `.Serialization` 43 / `.Converters` 30 / `.Linq` 18 / `.Bson` 10 / 其余为编译器生成

**致命的地方是"方法级裁剪"**：类型在 ≠ 方法能调。实测（`il2cpp_class_get_methods` 权威枚举）：

| 可用 ✅（实测跑通） | 被裁 ❌ |
|---|---|
| `JsonConvert.DeserializeObject<T>(string)` | `JsonConvert.SerializeObject(object)`（单参重载没了） |
| `JsonConvert.SerializeObject(obj, Formatting)` | `JObject.Parse`（只剩 `JObject.Load(JsonReader, JsonLoadSettings)`） |
| `JsonSerializer.Create()/CreateDefault()` | `JsonSerializerSettings` 的绝大多数 **setter** |
| `JsonSerializer.Serialize(JsonWriter, object)` | `JToken` 的索引器 `get_Item`（`JObject` 的还在） |
| `JsonSerializer.Deserialize<T>(JsonTextReader)` | `JToken.WriteTo` |
| `JsonSerializer.Formatting / NullValueHandling / MissingMemberHandling` | `JToken.ToString(Formatting)` |
| `JsonTextReader` / `JsonTextWriter` 构造 | |
| `JObject.FromObject` / `JToken.ReadFrom` / `JObject` 索引器 | |
| `JArray` 索引 / `Count` / 遍历 / `JToken.Type` | |
| `JToken.ToObject<T>()` / `JObject.Add` / `JToken.ToString()` | |

另外实测这些**行为仍然正常**（这一点比自己搓的解析器还强）：`//` 与 `/* */` 注释、**尾随逗号**、UTF-8 BOM、垃圾内容抛 `JsonReaderException`。

> 迁移方案本身是可行的（`JsonSerializer` 实例 + `JsonTextWriter` 流式 API 能完成完整读写往返，已实测），
> 但要用它就得把 SDK 的 JSON 层重写成"只用上表左列 API"的形式，且**依赖一张随时可能因游戏版本变动而失效的白名单** ——
> 稳定性收益为负。**已否决**，理由与决策见下。

---

## 3. 托管侧反射基本不可用（所以也别想"运行时探测"）

游戏把反射入口也裁了，实测**全部缺失**：

```
System.Reflection.Assembly::GetType                 ✗
System.Reflection.Assembly::GetExportedTypes        ✗
System.Reflection.Assembly::get_Location            ✗
System.Reflection.ReflectionTypeLoadException::get_Types  ✗
System.Console::Write                               ✗
```

`Type.GetType("X, Y")` 不抛异常但**恒返回 null**，不可作为存在性判据。

**权威判据只有一条：从 C++ 侧用 IL2CPP 自己的表查**（这正是 HybridCLR 解析类型时用的同一张表）：

```cpp
// 类型是否存在
il2cpp_class_from_name(il2cpp_assembly_get_image(asm), "命名空间", "类型名");
// 方法是否存在（类型在 ≠ 方法在）
il2cpp_class_get_methods(klass, &iter) + il2cpp_method_get_name / il2cpp_method_get_param_count
```

⚠️ 反面教材：**"类型名出现在 `global-metadata.dat` 里"不能当作存在性证据** —— TypeRef/MemberRef 表同样会保留名字字符串。
（本次就是先据此误判为"类型存在"，后用 `il2cpp_class_from_name` 才查出命名空间被改名的真相。）

加载器已在 HybridCLR 就绪时把**域内程序集清单**落盘到 `logs\aot-assemblies.txt`，遇到 `TypeLoadException` 先对照它。

---

## 4. 决策记录

| 项 | 决策 | 理由 |
|---|---|---|
| SDK JSON | **保留自研** `MiniJson` / `CesiumJson` | 托管第三方库加载不起来；游戏内置那份方法级裁剪严重，白名单随时可能失效 |
| SDK 日志 | **保留自研** `SdkLog` | 同上（Serilog 及其传递依赖必挂） |
| 加载器 C++ 库 | **保留** nlohmann/json + spdlog + fmt | 运行时零新增依赖（见下表），已实测通过；回退需重写 6 个文件并重新验证整套 Steam 绕过，代价更大 |

`version.dll` 的实际导入表（**没有** fmt/spdlog/nlohmann 的任何 DLL）：

```
WINMM.dll  KERNEL32.dll  USER32.dll  MSVCP140.dll  VCRUNTIME140_1.dll  VCRUNTIME140.dll
api-ms-win-crt-{stdio,heap,convert,filesystem,locale,runtime,string,time,math}-l1-1-0.dll
```

---

## 5. 复现步骤（如果将来还要试）

1. 把待测程序集放进 `AstralParty_ModLoader\sdk\`，启动游戏，看 `logs\cesium-loader.log`：
   * `SDK <名字> 加载成功` / `加载失败: <真实托管异常>`（加载器已把托管异常的类型与消息转成 UTF-8）
2. 出现 `TypeLoadException: Could not load type 'X' from assembly 'Y'` → 打开 `logs\aot-assemblies.txt` 确认 Y 是否在域内、X 是否被裁。
3. 要精确判定"某类型/某方法是否存在"，用 C++ 侧的 `il2cpp_class_from_name` / `il2cpp_class_get_methods`，
   **不要**用托管反射（已全被裁），也不要只看 `global-metadata.dat` 的字符串。
