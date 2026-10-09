# StandaloneSpectator 独立 PVE 观战原型

.NET 8 TCP/protobuf 客户端，不调用 Unity 场景、游戏单例或 SDK。首次取得有效认证参数需要正常登录的本人 TapTap 游戏和可选 SpectatorAuthHandoffMod。导入后认证仅保存在 API 进程内存中，游戏退出后可尝试正常登录及观战；SDK 认证过期后需重新正常登录取得参数。本程序尚未实现完全独立的 TapTap/BnSdk 登录票据获取/刷新。

## 启动

在 CesiumLoader 仓库根：

```powershell
dotnet build tools\StandaloneSpectator\StandaloneSpectator.csproj -c Release
dotnet tools\StandaloneSpectator\bin\Release\net8.0\StandaloneSpectator.dll --protocol 'C:\src\Study\astralparty\extracted_dlls' --handoff 'X:\TapTap\PC Games\524022\starengine_BuildPC_CN_TAPTAP_8\CN_TAPTAP_V3.2.1\AstralParty_ModLoader\auth-handoff' --port 18743
```

仅监听 127.0.0.1，Bearer token 每次启动随机生成并输出到本机控制台。除 /health 外均需 Authorization: Bearer <token>。与旧文件桥接 SpectatorApi（18742）是不同程序，接口未伪装成已有成功的 mod 观战。

## 初次认证交接

1. 安装可选 SpectatorAuthHandoffMod 并在虚拟机内重启游戏，正常登录并停在大厅。
2. POST /auth/import。独立 API 写入带随机 nonce 的一次性 request.json；mod 只在收到该请求后导出当前正常 China 登录 protobuf 和实际服务器地址。没有主动触发就不会导出。
3. API 验证 nonce、30 秒有效期及数据大小，导入内存后删除交接文件。程序不提供读回认证数据的 HTTP 端点。
4. 退出虚拟机内游戏，避免同一账号同时建立两个游戏会话，然后 POST /login。
5. POST /watch {"watchCode":"有效PVE观战码"}，再 GET /state。

注意：临时认证交接文件含有效敏感参数（base64 不是加密），共享目录必须仅本人可访问。删除属于普通删除，并非安全擦除。此程序不读取原游戏凭据缓存、不打印 SID/Extra/原始登录报文、不使用 Dev 认证。测试是否会话可复用要以官方服务器结果为准。

## 接口

- GET /health：程序存活状态，不证明已登录。
- GET /status：认证/登录/观战状态。
- POST /auth/import：一次性交接，等待最多20秒。此请求必须在 mod 已初始化且本人游戏正常登录后提交。
- POST /login：使用内存认证参数提交正常登录。单进程一次尝试，失败后重新启动程序，不自动重试。
- POST /watch：仅支持官方多玩家 PVE MapType4/12，不允许PVP或单人模式；顺序查询5191、入场5193。
- POST /focus {"playerId":"数字字符串"}：本地选择服务器已授权观战名单中的玩家，不发送用牌等操作。
- GET /state：返回官方观战房间中的关注玩家手牌，不确定/掩码/断线/心跳过期时503。
- POST /leave：官方退出5195。

与文件桥接 API 不同，该原型的 POST 等待操作完成后返回，不返回供轮询的命令 id。错误只返回类型分类，不返回可能包含凭据的异常字符串。

## 实现与验证范围

传输为35字节大端头+protobuf载荷，版本1/0/0；RPC用UPSN配对，DOWNSN发送0，与现有代码一致。心跳5003每5秒一次。任何断线/无法确认的操作超时均停止信任旧手牌；目前不自动重连。

房间/卡牌由游戏 protobuf 生成程序集通过反射解析，不调用Unity业务逻辑。只接受服务器确认的本人观战身份与允许的PVE房间。手牌以真实 UniqueId 区分卡牌实例，1109/1040完整替换与转换消息维护状态；任何掩码不会反推身份或保留旧牌。

离线协议测试、真实生成程序集解析、状态测试不等同于已经完成真实独立登录。实测结果另记录；在独立进程完成官方登录、游戏已退出、持续心跳和新观战码入场前，不宣称完全独立观战可用。
