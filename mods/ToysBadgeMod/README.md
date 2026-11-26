# 工具箱玩家标识 (ToysBadgeMod) v1.0.2

在联机对局中，通过房间服务接口 `POST /api/v1/rooms/{watchCode}/join` 与 `/leave` 识别同一对局内同样使用了 **Astral Party Toys（吉星派对工具箱）** 或兼容加载器的玩家，并在左侧玩家列表对应头像左边固定位置显示圆形标识图标，鼠标悬停时显示对应客户端提示。

## 工作原理

1. 进入联机对局（`RoomStateType.RUNNING`，排除单机模式与回放）后，模组读取当前区服与对局观战码。
2. 通过 `UnityWebRequest` 向 `https://connect.hiynet.com/api/v1/rooms/{watchCode}/join` 发送加入与保活请求（自适应频率：`6s` → `10s` → 稳态 `25s`），获取同局在线成员列表。
3. 自动剥离游戏昵称中的 FairyGUI UBB / 富文本标签（支持彩色/渐变昵称），与左侧战斗 HUD（`UIBattleInfoPanel`）中 4 个玩家槽位比对，在匹配玩家头像（`com_Head`）左侧固定位置渲染圆形图标（不随回合动画左右摇摆）：默认玩家显示圆形 Astral Party Toys 图标并提示「该玩家正在使用AstralParty.Toys」；对 `modVersion` 以 `APE-` 开头的兼容加载器玩家，渲染游戏内置 `UT_Item_PlayerPhoto_70120` 圆形黑底图标并在悬停时提示「该玩家正在使用吉星ModLab」。
4. 对局结束或离开房间时自动发送 `/leave` 并隐藏图标；模组**不会将观战码或会话 Token 写入任何日志**。
5. 默认单开且同机启用 `InMatchChatMod` 时自动复用其 `ClientId`，避免占用两个名额；检测到加载器多 `profile`（`CESIUM_PROFILE` / `doorstop_config.json`）或非共享模式时自动按账号 ID 派生独立 `ClientId`，支持同目录双开互相识别。

## 配置

配置文件位于 `mods\ToysBadgeMod\config.json`（Toys 模组配置页可直接编辑，保存后自动热重载）：

| 字段 | 默认值 | 说明 |
| --- | --- | --- |
| `Enabled` | `true` | 总开关；关闭后不连接服务、不显示图标 |
| `ServiceUrl` | `https://connect.hiynet.com` | 房间服务地址；清空则完全不发起网络请求 |
| `RefreshSeconds` | `25` | 局内刷新与保活间隔（15–55 秒） |
| `TimeoutSeconds` | `15` | 单次请求超时秒数（5–60 秒） |
| `IconSize` | `26` | 圆形工具箱图标直径（14–64 像素） |
| `Gap` | `4` | 图标右边缘与头像左边缘的间距（-32–48） |
| `OffsetX` | `0` | 水平微调偏移（-200–200） |
| `OffsetY` | `0` | 垂直微调偏移（-200–200） |
| `ShowSelfBadge` | `true` | 是否在自己的头像左侧也显示图标 |
| `ShowOfflineMembers` | `false` | 是否为已判定离线的成员继续保留显示图标 |
| `ShareChatClientId` | `true` | 同时启用 `InMatchChatMod` 时复用其 `ClientId` |
| `IconPath` | `""` | 可选自定义 PNG 图标路径；留空使用内置圆形图标 |
| `ClientId` | 自动生成 | 本机安装标识，用于断线重连复用成员身份，请勿手改 |

## 构建

```powershell
dotnet build mods/ToysBadgeMod/ToysBadgeMod.csproj -c Release
```

- 图标资源来源：[Astral Party Toys](https://github.com/aspatoys)（`assets/toys_icon.png`，可由 `scripts/generate_icon.py` 重新生成 `ToysIconData.cs`）
