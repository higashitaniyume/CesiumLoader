# apcsm (CesiumLoader CLI)

《吉星派对》（Astral Party）CesiumLoader 模组脚手架与管理工具。

[![NuGet](https://img.shields.io/nuget/v/apcsm.svg)](https://www.nuget.org/packages/apcsm)

## 安装

作为 .NET 全局工具安装（需要安装 [.NET 8.0 SDK](https://dotnet.microsoft.com/download)）：

```shell
dotnet tool install -g apcsm
```

更新到最新版：

```shell
dotnet tool update -g apcsm
```

## 快速使用

### 1. 创建新模组项目

```shell
apcsm new MyMod --author "YourName" --desc "模组描述"
```

会自动生成符合规范的项目结构，并自动在项目的 `refs\` 附带配套的 `CesiumLoader.SDK.dll` 与 XML 注释文档，开箱即建。

### 2. 构建模组

```shell
cd MyMod
apcsm build
```

或者指定目录：

```shell
apcsm build MyMod
```

### 3. 打包模组

```shell
apcsm package MyMod
```

会生成可直接分发给玩家的 zip 压缩包，解压到游戏目录的 `AstralParty_ModLoader\mods\` 即可使用。

### 4. 检查与验证

列出 mods 目录下所有模组信息：

```shell
apcsm list <mods_dir>
```

预检依赖完整性与版本兼容性：

```shell
apcsm verify <mods_dir>
```

## 开源协议

本项目基于 [AGPL-3.0](https://github.com/higashitaniyume/CesiumLoader/blob/master/LICENSE) 开源。
更多开发文档与 API 说明请参阅 [GitHub 仓库](https://github.com/higashitaniyume/CesiumLoader)。
