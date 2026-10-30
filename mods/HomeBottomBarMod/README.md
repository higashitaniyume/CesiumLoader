# 主页底栏调整 HomeBottomBarMod

把主页底栏固定为：

```text
任务 | 好友 | 公会 | 图鉴 | 仓库 | 邮件 | 设置 | 退出
```

- 隐藏「公告」按钮
- 「设置」左移一位（移到公告原来的位置）
- 最右侧增加「退出」按钮，沿用游戏原生的退出确认流程

只修改客户端内存里的主页 UI 配置，不改游戏文件，也不影响协议或其他玩家。

## 安装

把 `HomeBottomBarMod` 文件夹放到：

```text
<游戏 exe 目录>\AstralParty_ModLoader\mods\HomeBottomBarMod\
```

文件夹内包含 `HomeBottomBarMod.dll` 与 `HomeBottomBarMod.json`，重启游戏后生效。

## 卸载

删除 `AstralParty_ModLoader\mods\HomeBottomBarMod\` 并重启游戏，底栏恢复原版。

## 兼容性

适用于任何已安装 CesiumLoader 的客户端；功能编号与主页配置由游戏自身提供，不依赖具体区服或语言。
