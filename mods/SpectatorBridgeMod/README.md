# SpectatorBridgeMod 0.1.1 本地原型

用途：通过官方观战码进入 PVE 观战，将当前关注玩家在官方手牌面板可见的卡牌提供给本机 SpectatorApi。需要游戏运行并完成正常登录。不读取登录凭据，不实现脱离客户端登录，不修改 PVP 手牌显示限制。

## 安装

构建 `dotnet build mods\SpectatorBridgeMod\SpectatorBridgeMod.csproj -c Release`。在游戏已有 CesiumLoader 2.3.2 环境下，将 DLL、同次构建 PDB 和目录内 sidecar JSON 放入 `AstralParty_ModLoader\mods\SpectatorBridgeMod\`，重启游戏。不要复制 SDK 或游戏引用程序集到 mod 目录。

默认启动延迟 30 秒，然后由 SDK 主线程回调每 500 毫秒采样。桥接目录由 `CESIUM_MODS_DIR` 的父目录决定，为 `AstralParty_ModLoader\spectator\`。文件读写采用已验证的 HybridCLR BCL 原语；实际 TapTap 热更环境仍需启动日志与真实观战检验。

## 使用

API 程序与完整端点说明见 `../../tools/SpectatorApi/README.md`。

```powershell
dotnet tools\SpectatorApi\bin\Release\net8.0\SpectatorApi.dll --bridge 'X:\TapTap\PC Games\524022\starengine_BuildPC_CN_TAPTAP_8\CN_TAPTAP_V3.2.1\AstralParty_ModLoader\spectator'
```

API 使用启动时输出的 Bearer token，仅监听 127.0.0.1:18742。`POST /watch` 的 JSON 为 `{"watchCode":"有效观战码"}`。返回 202 和命令 id 后通过 `GET /commands/{id}` 查询结果；返回 pending 仅代表请求已排队，done 才代表官方 PVE 观战与手牌数据已就绪。

`GET /state` 返回当前关注玩家的 cards，每项包含 cardId/name/known/battleCost，以及房间 id、玩家 id、可切换玩家列表、sessionId 与 sampledUtc。未知/掩码卡牌保留 known=false，不推断其内容。playerId 和 roomId 均使用字符串，避免 JavaScript 整数精度损失。`POST /focus` 接受 `{"playerId":"列表中的玩家ID"}`，调用官方切换关注玩家方法，并与手动点击玩家一致关闭自动跟随当前行动玩家（0.1.1）；`POST /leave` 调用官方退出观战。

只在官方账号被识别为观战者、房间 RUNNING 且为 PVE 非单人模式时生成手牌快照。未登录、未观战、PVP 或数据未就绪时生成 unavailable 空手牌快照；API 对不可用或超过 5 秒的快照返回 503，不返回旧手牌。

## 生命周期与限制

新观战请求仅从大厅 NONE 状态提交；不会从玩家房间、选角或加载阶段强行转入观战。官方查询创建但未入场的房间在拒绝 PVE/人数/匹配检查时按拥有的 roomId 清理，不清理真实玩家房间。

命令超时 60 秒。超时不会取消游戏已有 RPC；仍在执行的操作由回调完成锁定状态，期间拒绝重复操作。未知/失联 RPC 未恢复时可能需要重启游戏。API 一次只排队一条命令，游戏重启时将旧命令作废而不自动重发。

状态写入使用临时文件再 Delete/Move，替换期间存在很短的文件不可用间隙；API 将其视为 unavailable，可稍后重试。观战码不会写进活动日志，但命令通道在执行前会含观战码，请仅使用本人可读写的游戏目录。

可移除 `mods\SpectatorBridgeMod` 并停止 API，重启游戏后卸载原型。本原型未加入内置 mod 默认清单，未更改现有加载器/其它 mod 配置。

## 2026-10-07 实测

虚拟机内 TapTap CN 3.2.1：0.1.0 mod 初始化成功并持续写 state.json。宿主机通过 X: SMB 共享使用 API 发出命令，虚拟机执行并回写结果。给定有效 PVE 观战码后，/watch 命令返回 done，房间 213541；/state 返回 HTTP 200、officialSpectating=true、4 名玩家及真实卡牌 ID/名称/费用。抽样获取了当前玩家 6 张手牌，切换关注后另一名玩家为 4 张手牌。未做手牌 UI 的逐张目视对照。

实时观战默认自动跟随回合玩家，实测切换后关注对象会随回合变化。0.1.1 补上官方手动点击玩家对应的 SwitchFollow(false)，已通过新增回归测试。运行中的 0.1.0 不会因磁盘 DLL 更新而热重载，固定关注行为需下次重启再实测。

离线验证：API 的 37 项 localhost HTTP 检查通过；链接真实 mod 源码、使用受控游戏替身的生命周期检查 23 项通过。后者不等同于所有服务器/HybridCLR 异常路径都经过真机验证。实际退出观战、重新登录、网络断开和完整对局结束仍待进一步实测。
