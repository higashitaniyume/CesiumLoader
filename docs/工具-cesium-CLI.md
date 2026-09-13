# cesium CLI — 模组脚手架与包分发

模组开发者的 DX 工具 (对标 `dotnet new` + `nuget pack` 的心智模型)。
解决"模组开发第一痛点": 从零搭项目、构建、打包分发、预检兼容性。

## 构建

```
dotnet build tools\cesium\CesiumCli.csproj -c Release
# 产物: tools\cesium\bin\Release\net8.0\cesium.exe
```

## 命令

### new — 生成 mod 项目模板

```
cesium new <Name> [-o <dir>] [--author <名>] [--desc <描述>]
```

生成:
- `{Name}.csproj` — netstandard2.0, 引用 SDK (HintPath 指向仓库 SDK 产物)
- `AssemblyInfo.cs` — 程序集级 `[ModManifest]` (权威位置, 含能力声明)
- `ModEntry.cs` — 事件驱动入口 (订阅 GameEvents + StartAutoHook, 无轮询)
- `{Name}.json` — sidecar (加载前就存在, 依赖解析用)
- `README.md`

### build — 构建 mod

```
cesium build <dir> [-c Release]
```

### package — 打包分发

```
cesium package <dir> [-o <out.zip>]
```

包布局 (解压到 `AstralParty_ModLoader\mods\` 即安装, 与加载器扫描布局一致):
```
MyMod\MyMod.dll
MyMod\MyMod.json    (sidecar, 可选)
```
解压后生成 `mods\MyMod\` 文件夹, 加载器扫描 `mods\` 下每个子目录加载。

### list — 列出 mods 目录元数据

```
cesium list <mods_dir>
```

### verify — 预检兼容性 (模拟加载器判定)

```
cesium verify <mods_dir>
```

检查每个 mod: SDK 版本协商 / 缺失依赖 / 依赖版本过低。输出 `[通过]` / `[拒绝]`,
exit code 0 = 全兼容, 2 = 存在不兼容 (CI 可用)。

## 与加载器的关系

- 加载器 (version.dll) 在游戏启动时执行**同样的**依赖解析逻辑 (modmeta.cpp)
- `cesium verify` 是它的离线镜像 —— 发布前不启动游戏即可预检
- sidecar 由脚手架生成 → 随包分发 → 加载器读它做依赖解析; 首次运行后 SDK
  也会刷新 sidecar (运行时元数据与声明一致)
