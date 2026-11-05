# 多 profile（一份安装，多套账号）

CesiumLoader 可以把游戏读写账号、存档、回放与缓存的目录**在游戏进程内**重定向到一个独立目录（下称 profile）。一份游戏安装即可保存多套账号与本地进度，切换时只改一个配置字段，不复制任何游戏文件。

## 适用场景

- 同一台电脑登录不同账号/区服的小号，各自的本地登录缓存与会话互不覆盖；
- 保留多份本地设置（`SaveFile.es3`）与回放；
- 需要同时运行多个实例时，每个实例指向不同 profile（见「同时运行多个实例」）。

## 配置

`AstralParty_ModLoader/doorstop_config.json` 中的 `profile` 字段：

| 取值 | 效果 |
| --- | --- |
| `""`（默认） | 不重定向，游戏写自己的 `%USERPROFILE%\AppData\LocalLow\<公司>\<产品>` |
| `"P2"` | 重定向到同一父目录下的 `<产品>_profiles\P2` |
| `"D:\\AstralParty\\P2"` | 绝对路径：profile 放到指定目录（可以放在别的盘） |

也可以用环境变量 `CESIUM_PROFILE` 指定，它的优先级高于配置字段，适合启动脚本：

```powershell
$env:CESIUM_PROFILE = "P2"
Start-Process "AstralParty_CN.exe"
```

> 从 Steam 客户端启动的游戏不会继承你终端里的环境变量，这种情况请改配置字段。

手写配置时 profile 名允许字母、数字、`_`、`-`、`.`，最长 64 个字符；也可以直接写绝对路径。用 [AstralParty.Toys](https://github.com/higashitaniyume/AstralParty.Toys) 的「游戏多开」页新建或改名时，只接受**英文字母、数字和下划线**（最多 32 个字符），页面会实时校验名称是否合法、是否重名。

也可以用 AstralParty.Toys 的「游戏多开」页管理档案（新建 / 改名 / 删除 / 切换），并从那里直接启动游戏。

## 窗口标题

同时开多个实例时，各窗口标题默认完全一样，任务栏和 Alt+Tab 里分不清是哪个 profile。加载器会在游戏窗口出现后把 profile 名追加到标题后面：

```text
吉星派对 · P2
```

配置字段 `profileWindowTitle`（默认 `true`）：

| 取值 | 效果 |
| --- | --- |
| `true`（默认） | 选了 profile 时，窗口标题后追加 ` · <profile 名>` |
| `false` | 标题保持游戏原样；目录隔离照常生效 |

- 只在配置了 `profile`（或环境变量 `CESIUM_PROFILE`）时生效；绝对路径形式的 profile 只取目录名显示。
- 游戏自己重置标题时加载器会补回后缀，不会重复叠加。
- 写入的是窗口标题，不改变窗口大小、位置或焦点。
- 修改后下次启动游戏生效；也可以在同一页的「游戏窗口标题带上 profile 名」开关里切换。

## 目录

```text
%USERPROFILE%\AppData\LocalLow\<公司>\
├── <产品>\                       主目录（默认 profile，始终保留）
│   ├── BnSdk\                    账号 SDK 缓存与令牌
│   ├── Temp\SaveFile.es3         本地设置
│   ├── Temp\Replay\              回放
│   └── com.unity.addressables\   下载缓存
└── <产品>_profiles\
    ├── P2\                       一个 profile = 一整套上面的内容
    └── P3\
```

## 首次使用某个 profile

加载器会自动创建目录，并把主目录里的 Unity 下载缓存（`com.unity.addressables`，通常数 GB）用**硬链接**播种过去：瞬时完成、不额外占用磁盘，新 profile 不必重新下载。

硬链接是独立的目录项，所以在某个 profile 里清理缓存不会破坏主目录的数据。播种完成会写一个隐藏标记文件，之后启动不再处理。

首次建立 profile 的那一次启动，游戏内下载缓存的补齐会略慢一些；之后与其他 profile 一致。

## 隔离范围

隔离到 profile 的内容：

- BnSdk 账号缓存、设备信息与登录令牌；
- `Temp\SaveFile.es3`（本地设置）、`Temp\Replay`（回放）、`Temp\ScreenShot`（截图）；
- 公告/背景图等下载素材、`com.unity.addressables` 下载缓存。

**不隔离**的内容：

- **PlayerPrefs**（注册表 `HKCU\Software\<公司>\<产品>`）：分辨率、窗口模式、自动登录标记等各 profile 共用；
- **`Player.log`**：进程启动瞬间创建，仍写在主目录（其他文件都已重定向）；
- **游戏安装目录**：多实例共用安装目录下的运行期缓存（如 `_Data\com.unity.addressables`）。

## 同时运行多个实例

Unity 的单实例互斥与 **exe 路径**绑定：

- 同一个 exe 路径只能开一个实例；
- 需要同时多开时，为每个实例准备不同路径的启动外壳（把 `version.dll`、`GameAssembly.dll`、`UnityPlayer.dll` 等与 exe 一起硬链接到不同目录，`_Data` 目录使用 junction 或硬链接外壳），并对每个实例打开 Steam 绕过（`steamBypassEnabled` / `steamBypassRestartCheck`），否则非 Steam 启动会自行退出；
- 同一个账号不能同时在线，服务器会互相踢下线，多开请使用不同账号；
- 变速与窗口置前通道的目录位于安装目录，多个实例会互相影响。

## 回放与 AstralParty.Toys

目录重定向只作用于游戏进程。profile 内的回放位于 `<profile>\Temp\Replay\`，而 Toys 的默认回放库指向主目录；需要时把回放来源指向对应 profile 目录。

## 删除 profile

关闭游戏后直接删除对应的 profile 目录即可，主目录不受影响。删除前确认其中没有要保留的回放或截图。

## 排错

加载器日志（`AstralParty_ModLoader\logs\cesium-loader-*.log`）中相关关键字：

| 日志 | 含义 |
| --- | --- |
| `[config] profile=... (from CESIUM_PROFILE)` | 由环境变量指定的 profile |
| `[profile] active "P2"` 与两行目录 | profile 已生效，第二行是实际目录 |
| `[fsredir] installed N/N file hooks` | 文件重定向安装结果 |
| `[profile] first run of this profile: linking ...` | 首次建 profile 时的缓存播种 |
| `[profile] seeded com.unity.addressables: ...` | 播种完成（或部分失败的统计） |
| `[fsredir] <API> failed (error ...) -> ...` | 某个改写后的文件操作失败（见下方「登录界面只有封面」） |
| `[profile] warning: ...` | profile 被拒绝（名称非法、目录不可写等），游戏退回主目录继续运行 |
| `[windowtitle] title is now "..."` | 窗口标题已追加 profile 名 |
| `[windowtitle] cannot start the title thread` | 标题线程启动失败，标题保持原样（不影响游戏） |

### 登录界面只有封面、没有登录按钮

先看主目录的 `Player.log`（`%USERPROFILE%\AppData\LocalLow\<公司>\<产品>\Player.log`，它不随 profile 隔离）。如果里面有

```text
IOException: The file '...\Temp\SaveFile.es3.tmp.bak' already exists.
  at ES3Internal.ES3IO.CommitBackup ...
  at Core.Scene.LoginSceneController.InitGameBeforeShowLoginUI ...
```

说明本机加载器版本缺少某个文件 API 的重定向：游戏的托管 `System.IO.File.Copy` 由 il2cpp 实现，走的是 `CopyFileExW`，旧版只挂了 `CopyFileW`，于是"删除备份"被重定向到 profile、"复制备份"落回主目录，撞上主目录里已有的备份文件，异常把登录界面初始化打断。更新到包含 `CopyFileExW` / `ReplaceFileW` hook 的加载器版本即可。

profile 相关问题只影响目录隔离，加载器与模组仍会正常加载。
