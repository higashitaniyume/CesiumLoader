# 吉星派对 · 观战台

React + TypeScript + Vite + Tailwind CSS 的本地临时观战页面，展示官方 PVE 观战快照中的四位玩家、公开属性和实际可见的手牌。不会生成示例手牌。星币采用游戏正式名称。

## 构建

在此目录打开 PowerShell，使用 Harness 捆绑的 Node 和 pnpm：

```powershell
$node = 'C:\Users\shimikoi\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\node\bin\node.exe'
$pnpm = 'C:\Users\shimikoi\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\pnpm\bin\pnpm.mjs'
$env:PATH = (Split-Path $node) + ';' + $env:PATH
& $node $pnpm install
& $node $pnpm check
& $node $pnpm build
```

生产产物写入 `dist/`，由 StandaloneSpectator 的 ASP.NET 服务在 `http://127.0.0.1:18743/` 同源提供。构建不会启动常驻服务器。开发时可运行 `& $node $pnpm dev`，Vite 会把 API 请求代理到 18743；正式使用应打开 ASP.NET 服务的地址。

## 浏览器冒烟

构建后可运行以下命令，使用已有 Chrome 与 Harness 的 Playwright，不下载浏览器。测试会在随机本地端口提供生产页面，以隔离的 mock API 验证四人排序、本地化 / ID 回退、被遮蔽手牌、登录 → 离开 → 观战、503 清空、五秒过期、恢复与手机布局；结束后关闭临时服务和浏览器。测试数据仅存在于测试脚本，不在页面或生产包中。

```powershell
& $node scripts/smoke.mjs
```

可用 `PLAYWRIGHT_MODULE` 指定已有 Playwright 模块路径、`BROWSER_EXECUTABLE` 指定已有 Chromium / Chrome 路径。此脚本不需要安装额外依赖。

## API 约定

- `GET /ui/config`：请求头 `X-Spectator-UI: 1`，返回 `{ token }`。令牌只存在页面内存，不写入浏览器持久存储。
- 其余 API 使用 `Authorization: Bearer <token>`。
- `GET /status`：至少提供 `authenticated`，房间 ID 可以来自 `spectator.roomId` 或 `roomId`；可提供 `loginAttempted`。
- `POST /login {}`、`POST /watch { watchCode }`、`POST /leave {}`。
- `GET /state`：`{ mode, officialSpectating, status, roomId, players }`；玩家按 `slot` 排列，至多展示四人。玩家字段为 `playerId, slot, nick, heroId?, heroName?, hp?, maxHp?, gold?, attack?, defense?, handKnown, handCount?, cards`。
- 卡牌字段为 `cardUid, cardId, purifyNum?, isTemp?, battleCost?, name?, description?, iconUrl?`。本地化字段与图标 URL 可由服务的目录表提供；缺失时保留真实 ID，不猜测名称。图标不是必须字段。

启动时自动读取既有观战状态。提交观战码会先检查状态，尚未认证时调用登录，然后退出已有观战房间，再发起新的观战请求。操作执行期间按钮与输入禁用。服务登录错误保留在页面上，不自动重复登录。

每秒轮询 `/status` 和 `/state`。`/state` 返回 503 时立即清空房间数据，同时独立显示 `/status` 的认证状态。网络故障期间快照最多保留 5 秒，随后清空并标记过期。`handKnown: false` 时即使包含卡牌数组也不显示，避免把被遮蔽的数据误作可见手牌。页面不接收或保存游戏账号凭据。
