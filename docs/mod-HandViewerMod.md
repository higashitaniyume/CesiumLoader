# 手牌查看 HandViewerMod

CesiumLoader 默认发布包内置的只读模组，版本 1.0.8。**实验性：真机已验证查询成功；最终卡面、布局和所有故障场景尚未完整验证。**

**联网提示：默认启用，在支持的对局中展开手牌时会把观战码发送到下述第三方服务。**全部手牌收起时暂停查询；不希望发送时，可禁用此模组，或设置 `Enabled=false` 后重启游戏。

## 行为

- CesiumLoader 初始化延迟约 30 秒。进入运行中的多人 PVE 对局（MapType 4/12）后，每 250ms 检查一次本地房间与 HUD；初始手牌全部收起，不请求接口。至少一条手牌展开且可见时才查询；观战码稍晚到达时，仍有手牌可见才启动查询。
- 自动把当前观战码发送给 `https://astralpartycards.hiynet.com/query?id=...`，服务通过官方 PVE 观战流程取快照。不会发送本机账号认证、Sid/Extra；不会记录观战码或完整请求 URL。使用前请确认你愿意让该服务接收观战码。
- 四名玩家的升星列表右侧增加红、绿、蓝、黄的 `>`（黑色描边、无外框，点击区边长为头像高度一半），展开后在它右侧按 `DoubleRow` 开关显示双排或单排（默认双排），按原顺序从上到下排列；整个手牌区域高度保持真实头像 `com_Head` 的显示高度，排间距 2，卡牌随排数等比例缩放。四条可以独立同时展开。
- 点击已展开的三角形收起；每条手牌默认展开 5 秒后自动收回，`AutoCollapseSeconds` 可修改等待秒数，`0` 表示不自动收回。每次重新展开重新计时，四条独立计时，HUD 重建保留原截止时间。
- 再次展开会请求刷新，全房间共用一个快照、同一时间最多一个 HTTP 请求。全部收起或 HUD 隐藏时取消在途请求并暂停事件刷新与失败重试，不再为手牌数量请求接口；重新可见后刷新最新数据。还有其他手牌可见时继续正常刷新。
- 同卡牌 ID、净化次数、临时状态、费用的牌合并显示，重复数量在卡牌右上角标出；不同状态不合并。每排最多 ceil(卡牌组数 / 排数) 组；空间不足时滚轮横向查看，所有排一起滚动，不分页。名称、费用、净化、查询状态及采样时间放在悬浮提示中。
- HUD 暂时隐藏或输入遮罩生效时隐藏手牌条，恢复后保留展开状态；玩家身份改变、换房或卸载时清理。
- `handKnown=false` 显示“手牌不可用”，不会当作空手牌；已知的空列表显示“空手牌（0 张）”。卡片身份来自服务，不推断隐藏牌。
- `AutoRefresh=true` 且有手牌展开可见时，收到手牌变化或 1040 的 Card/ConvertCard 变化后合并刷新。请求中又发生变化时，完成后再查一次。429/503 的数值 Retry-After 按秒退避（最多 300 秒）；其他失败指数退避，最大 60 秒。
- 离局、换房、观战码变化或卸载时取消旧请求并清掉缓存。不查询回放、PVP、未运行房间或没有观战码的房间。
- 不修改手牌或游戏状态，不向游戏服务器发送玩家操作。

## 卡面来源

优先复用游戏内部 `UICom_Card.CreateInstance()`、卡牌配置 `GetBattlePlayerCardView(playerId)` 与 `CommonUIManager.RendererCard`。名称/描述来自本地配表与 CardDescription；不把服务传来的文本作为 FairyGUI 富文本执行。

使用同步 RendererCard 以便捕获即时渲染错误；卡框采用当前账号的样式，卡面按目标玩家的配置解析。`battleCost=-1` 使用配表基础费用。卡面控件 Dispose 时交回游戏自身的加载/引用管理，不自行释放游戏共享纹理。

用户提供的 `C:\PublicFiles\materials\StarEngine\Grouped\卡面` 可作为后续备用素材来源，**本版未复制或打包这些 PNG**。原生卡片创建失败时尝试用游戏 `TextureLoader` 加载配表卡图；两条路径均失败才显示“卡图不可用”。两条路径均按 `Card.InfoDict` 查卡牌 ID，而不是用 ID 索引列表；最终异步加载与显示效果仍需真机确认。

## 构建和安装

在加载器仓库根目录执行：

```powershell
dotnet build mods\HandViewerMod\HandViewerMod.csproj -c Release
dotnet test tests\HandViewerMod.Tests\HandViewerMod.Tests.csproj -c Release
```

安装前退出游戏，并确保已安装 CesiumLoader，配置的 `sdkVersion` 至少为 2.3.2：

```powershell
pwsh -NoProfile -File tools\deploy-handviewer.ps1 -GameDir '<游戏 exe 所在目录>'
```

脚本会构建、备份目标目录已有文件，再安装：

```text
AstralParty_ModLoader/
  sdk/CesiumLoader.SDK.dll + .pdb
  mods/HandViewerMod/HandViewerMod.dll + .pdb + .json
```

SDK 必须一并更新，本版修正了 HandChanged 忽略空手牌的问题。脚本不覆盖玩家 config.json 或 doorstop_config.json；自加载器 2.3.4 起加入默认内置 mod 清单；认证交接与观战桥接 mod 仅提交源码，不默认安装。仅想预览部署目标可加 `-WhatIf`。

卸载时退出游戏，移除 `mods/HandViewerMod`；如需撤销 SDK 变动，使用部署时 `backups/HandViewerMod-<随机标识>/` 下的备份恢复同名文件。

## 配置

首次初始化会生成 `mods/HandViewerMod/config.json`，改完重启游戏：

```json
{
  "Enabled": true,
  "AutoRefresh": true,
  "ServiceUrl": "https://astralpartycards.hiynet.com/",
  "TimeoutSeconds": 20,
  "DoubleRow": true,
  "AutoCollapseSeconds": 5
}
```

`Enabled=false` 不查询、不创建按钮。`AutoRefresh=false` 关闭手牌变化事件触发的自动刷新，展开或恢复可见时仍查询一次。`AutoCollapseSeconds` 是展开后自动收回的等待秒数（默认 `5`，`0` 不自动收回，负数按 `0` 保存）；可在模组配置中直接修改此数值，首次运行新版会自动补上该项，重启游戏生效。超时限于 5–60 秒。服务地址支持 HTTP/HTTPS，不能含用户信息/查询串/fragment；禁止在地址中放账号凭证。HTTP 不加密观战码，仅建议在可信局域网中使用。可在 Toys 的模组配置中修改 `ServiceUrl`，或直接编辑此文件；重启游戏后生效。

在 Toys 的「模组 → 手牌查看 → 配置」中勾选 `DoubleRow`（双排显示）开关：开启为半高双排，关闭为原尺寸单排。默认开启。首次运行新版会自动补上该项，保存后重启游戏生效。

## 验证边界与待测清单

离线测试使用合成 ID 和假 HTTP 请求，未访问真实服务。覆盖响应校验、64 位身份、已知空/未知手牌、收起时零请求、可见时查询、观战码晚到、单在途与事件合并、收起取消请求与暂停重试、重新展开刷新、旧快照标识、独立自动收回计时与 0 秒禁用、SDK 空手牌通知及已知缺失 BCL lint。

仍需游戏内确认：

1. FairyGUI 按钮位置、缩放、点击不冒泡，且不会覆盖原头像/详情功能。
2. 公网 HTTPS/TLS 与自定义 User-Agent 的实际请求表现；此前局域网查询已在真机日志中确认成功。
3. 原生卡片图片/文案/净化效果、卡面异步加载失败、释放行为正确。
4. 抽牌、用牌至空、转换、临时牌触发更新；全房快照玩家对应准确。
5. HUD 重建、输入遮罩、战斗期间显隐、退房与切房无残留。

离线测试通过不等于上述真机项已通过。2026-10-09 已部署到本机 Steam CN 游戏目录，部署同步 SDK 与 mod 的 DLL/PDB 并备份旧文件。真机日志确认过局域网查询成功，并据日志修复卡牌配置列表索引错误；随后默认地址与本机配置均改为 `https://astralpartycards.hiynet.com/`。最终四色 `>`、修复后的卡面以及公网 HTTPS 请求仍待完整真机验证。
