# SDK-能力声明与元数据

> Mod 能力声明 (仅展示/警告) + 模组元数据标准 + 依赖解析 + API 版本协商。

## 能力声明 (已取消权限门控)

**已取消权限机制**: 任何 mod 都能调用 SDK 全部 API (读对局 / 模拟操作 / 变速 / 写文件),
不再有默认拒绝、声明请求或覆盖配置。

`Permissions` 只用于**声明 mod 会用到的能力**, 供工具和加载器展示警告:

| 能力位 | 含义 | 用途 |
|---|---|---|
| `ReadGameState` | 读对局状态/订阅事件 (只读) | 仅展示 |
| `GameActions` | 向服务器发送操作 (投骰/移动/用牌) — 真实影响对局 | ⚠️ 声明后加载时/工具列表显示警告 |
| `SpeedHack` | 变速 | 仅展示 (变速本身是加载器内置功能) |
| `FileWrite` | 写文件 | 仅展示 |

### 声明 (程序集级, AssemblyInfo.cs)

```csharp
[assembly: ModManifest("我的Mod", "1.0.0", "作者", "描述",
    Permissions = ModPermission.GameActions,   // 声明会用到的能力
    SdkVersion = "2.2.0")]
```

### 警告 (仅提示, 不阻止)

声明了 `GameActions` (操作游戏) 的 mod:
- 加载器启动时控制台输出: `⚠ 警告: mod 'X' 声明了可操作游戏(模拟操作)的能力, 请确认来源可信`
- AstralParty.Toys 模组列表显示 `⚠️ 可操作游戏` 徽标

### 运行时 API

`Permissions.Has(perm)` / `Permissions.Require(perm, api)` 恒返回 `true`
(为兼容旧 mod 保留签名, 不再执行任何检查)。

## 模组元数据标准 (sidecar)

每个 mod 一个 `mods\{程序集名}.json` (sidecar), 由 SDK `SdkManifest.ExportSidecar()`
运行时生成, 或由脚手架 `cesium new` 在开发期生成并随包分发 (加载前就存在)。

```json
{"id":"MyMod","name":"我的Mod","version":"1.0.0","author":"作者",
 "description":"描述","permissions":1,"sdkVersion":"2.2.0",
 "dependencies":[{"id":"LibMod","minVersion":"1.0.0"}]}
```

| 字段 | 含义 |
|---|---|
| `id` | 程序集名 (依赖解析的 key, 不带 .dll) |
| `name` | 显示名 |
| `permissions` | 权限位掩码 (与 C# 枚举一致: 1=ReadGameState, 2=GameActions, 4=SpeedHack, 8=FileWrite) |
| `sdkVersion` | 需要的 SDK 最低版本 (SemVer) |
| `dependencies` | 依赖的其他 mod (id + 可选 minVersion) |

## 依赖解析 (加载器)

加载器 (modmeta.cpp, 纯标准库可单测) 在加载前:
1. 读全部 sidecar
2. **SDK 版本协商**: mod 要求 > 当前 → 拒绝
3. **依赖拓扑排序** (Kahn): 被依赖的 mod 先加载
4. **缺失依赖 / 版本过低 / 循环依赖** → 跳过该 mod 并写入日志
5. 无 sidecar 的旧 mod → 按文件名排序兼容加载

`tools\cesium verify <mods_dir>` 可离线预检同样逻辑 (无需启动游戏)。

## API 版本协商

- SDK 当前版本: `SdkVersion.Current = "2.2.0"` (与 csproj Version 一致)
- 加载器声明的当前版本: `doorstop_config.json` 的 `sdkVersion` (发布时同步更新)
- mod 声明 `SdkVersion`: 高于当前 → 加载器拒绝加载该 mod
- 运行时防御: `SdkVersion.Accepts("1.5.0")` / `SdkVersion.Compare(a, b)`
