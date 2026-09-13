# SDK-权限与元数据

> Mod 权限模型 + 模组元数据标准 + 依赖解析 + API 版本协商。

## 权限模型 (敏感 API 默认关闭)

敏感能力默认**拒绝**, mod 必须显式声明请求, 且用户可通过配置文件强制覆盖。

| 权限 | 影响 | 默认 |
|---|---|---|
| `ReadGameState` | 读对局状态/订阅事件 (只读, 无副作用) | ✅ 授予 |
| `FileWrite` | 写 mods 目录内文件 | ✅ 授予 |
| `GameActions` | 向服务器发送操作 (投骰/移动/用牌) — 真实影响对局 | ❌ 拒绝 |
| `SpeedHack` | 变速 — 联机有检测风险 | ❌ 拒绝 |

### 声明 (程序集级, AssemblyInfo.cs)

```csharp
[assembly: ModManifest("我的Mod", "1.0.0", "作者", "描述",
    Permissions = ModPermission.GameActions,   // 请求敏感权限
    SdkVersion = "2.0.0")]
```

### 判定顺序 (从高到低)

1. `mods\{name}.permissions.json` 显式覆盖 (用户/管理员最高权限)
2. `[ModManifest]` 声明
3. 默认策略 (上表)

### 权限覆盖配置 (mods\{name}.permissions.json)

```json
{
  "MyMod": { "GameActions": true, "SpeedHack": false }
}
```

### SDK 运行时门控

```csharp
if (!Permissions.Require(ModPermission.GameActions, "GameActions.ThrowDice"))
    return false;   // 未授权: 静默失败 + 告警
```

敏感 API (GameActions / SpeedHack.SetSpeed) 内部已自动调用 Require,
mod 无需手动检查 —— 未授权时调用返回 false。

## 模组元数据标准 (sidecar)

每个 mod 一个 `mods\{程序集名}.json` (sidecar), 由 SDK `SdkManifest.ExportSidecar()`
运行时生成, 或由脚手架 `cesium new` 在开发期生成并随包分发 (加载前就存在)。

```json
{"id":"MyMod","name":"我的Mod","version":"1.0.0","author":"作者",
 "description":"描述","permissions":1,"sdkVersion":"2.0.0",
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

- SDK 当前版本: `SdkVersion.Current = "2.0.0"` (与 csproj Version 一致)
- 加载器声明的当前版本: `doorstop_config.json` 的 `sdkVersion` (发布时同步更新)
- mod 声明 `SdkVersion`: 高于当前 → 加载器拒绝加载该 mod
- 运行时防御: `SdkVersion.Accepts("1.5.0")` / `SdkVersion.Compare(a, b)`
