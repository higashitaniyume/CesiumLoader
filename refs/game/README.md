# 游戏引用程序集（编译期依赖）

这里的 DLL 是《Astral Party》客户端里的托管程序集与它们依赖的 Unity/第三方程序集，**只用于编译期绑定**：SDK、内置模组、引用游戏类型的测试靠它们解析 `UIManager`、`RoomInfo`、`party.protocol`、`Vector3` 之类符号。

- 运行时不从仓库复制、也不随发布包分发；实际运行用的是游戏自己加载的同名程序集，所有引用都标了 `Private=false`，不会出现在构建输出里。
- 版本随游戏热更变化。核对/更新方法见下面《刷新引用程序集》。
- 这些程序集由游戏提供，**不在本仓库的 AGPL-3.0 / MIT 授权范围内**，仅作为编译引用随仓库提供。
  各文件的 SHA256 见 [sources.json](sources.json)。

## 内容

| 程序集 | 用途 |
| --- | --- |
| `AstralParty.Runtime.dll` | 游戏热更侧全部游戏逻辑与协议类型 |
| `UnityEngine.CoreModule.dll` | `MonoBehaviour` / `GameObject` / `Vector3` / `Time` |
| `UniTask.dll` | 游戏使用的异步库，SDK 生命周期与 RPC 回调依赖 |
| `Google.Protobuf.Runtime.dll` | `RepeatedField` / `MapField` / `ByteString` |
| `Unity.Addressables.dll`、`Unity.ResourceManager.dll` | 角色皮肤等资源加载路径 |

只有这几个程序集需要入库。游戏目录里其它 DLL（`mscorlib.dll`、`Cinemachine.dll`、`spine-*.dll` 等）没有被任何项目引用，留在本地即可。

## 定位方式

构建统一通过 `GameRefsDir` 属性定位本目录（见仓库根 `Directory.Build.props`）：

```text
默认值      $(RepoRoot)refs\game\
覆盖方式    $env:CESIUM_GAME_REFS = <目录>         # 环境变量
            MSBuild ... /p:GameRefsDir=<目录>\      # 单次构建
```

项目文件里写的是 `$(GameRefsDir)AstralParty.Runtime.dll`，不含 `..\..\` 这类相对层级，所以新增项目或调整目录都不会改变引用路径。

## 刷新引用程序集

游戏更新后如果新版本改了类型/枚举，把新提取的目录整体同步进来：

```powershell
pwsh -NoProfile -File tools/fetch-game-refs.ps1 -Source D:\extract\extracted_dlls
```

脚本只拷贝上表里的程序集（缺任何一个就报错退出），并重写 `sources.json` 的哈希，避免出现"只更新了其中几个 DLL"的半新组合。随后提交 `refs/game/` 下的改动即可；CI 与本地构建用的是同一份文件。

只核对新版本、不改仓库时，用 `CESIUM_GAME_REFS` 指向提取目录构建一次即可。
