<#
.SYNOPSIS
    构建并部署"AI agent 接管桥接"(AstralParty.AgentMod) + MCP server 到本机, 并打印 MCP 客户端配置。

.DESCRIPTION
    这是 mcp\ 这一块的部署入口(与加载器本体、其它内置 mod 分开)。步骤:
      1. dotnet build mcp\AstralParty.AgentMod (Release)  → 游戏内 mod
      2. dotnet build mcp\AstralParty.Mcp      (Release)  → stdio MCP server
      3. 校验游戏目录已装加载器(version.dll + AstralParty_ModLoader\)
      4. 复制 AstralParty.AgentMod.dll/.json 到 <游戏exe目录>\AstralParty_ModLoader\mods\AstralParty.AgentMod\
      5. 打印可直接粘进 MCP 客户端的 JSON 配置

    注意:
      - 游戏目录多在 Program Files, 可能需要管理员身份运行; 部署前请先退出游戏。
      - 这个 mod **不在** tools\builtin-mods.json 里(发布包默认不带 AI 接管能力),
        所以它是"可选安装": 想用就部署, 不想用就删掉 mods\AstralParty.AgentMod\ 目录。
      - 部署完记得确认动作总开关: enableActions 默认 true(能被 agent 操作)。
        只想让 agent 看、不让它动, 用 astral_control 或直接改 mod 的 config.json 把 enableActions 设成 false。

.PARAMETER GameDir
    游戏 exe 所在目录(含 AstralParty_CN.exe 与 version.dll)。默认国服 Steam 安装路径。

.PARAMETER SelfContained
    MCP server 用自包含方式发布(不依赖目标机装 .NET 8 运行时, 体积大一些)。
#>
[CmdletBinding()]
param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Astral Party\8vJXn6CN",
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot   # modding\msvc

Write-Host "== 构建 AstralParty.AgentMod (Release) =="
dotnet build (Join-Path $repo 'mcp\AstralParty.AgentMod\AstralParty.AgentMod.csproj') -c Release --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "AgentMod 构建失败" }

Write-Host "== 构建 AstralParty.Mcp (Release) =="
$mcpProj = Join-Path $repo 'mcp\AstralParty.Mcp\AstralParty.Mcp.csproj'
$mcpArgs = @('publish', $mcpProj, '-c', 'Release', '--nologo')
if ($SelfContained) { $mcpArgs += @('-r', 'win-x64', '--self-contained', 'true') }
else { $mcpArgs += @('-r', 'win-x64', '--self-contained', 'false') }
dotnet @mcpArgs | Out-Null
if ($LASTEXITCODE -ne 0) { throw "MCP server 发布失败" }

$dll = Join-Path $repo 'mcp\AstralParty.AgentMod\bin\Release\netstandard2.0\AstralParty.AgentMod.dll'
$json = Join-Path $repo 'mcp\AstralParty.AgentMod\AstralParty.AgentMod.json'
if (-not (Test-Path $dll)) { throw "找不到产物: $dll" }
if (-not (Test-Path $json)) { throw "找不到 sidecar: $json" }

$mcpExe = Join-Path $repo 'mcp\AstralParty.Mcp\bin\Release\net8.0\win-x64\publish\AstralParty.Mcp.exe'
if (-not (Test-Path $mcpExe)) { throw "找不到 MCP server: $mcpExe" }

if (-not (Test-Path (Join-Path $GameDir 'version.dll'))) {
    Write-Warning "游戏目录下没有 version.dll —— 似乎未安装 CesiumLoader 加载器。"
    Write-Warning "请先用 AstralParty.Toys 的『模组』页安装加载器, 或部署 dist\modloader。"
}
$loaderDir = Join-Path $GameDir 'AstralParty_ModLoader'
if (-not (Test-Path $loaderDir)) { throw "找不到加载器目录: $loaderDir (请先安装加载器)" }

$proc = Get-Process -Name 'AstralParty_CN' -ErrorAction SilentlyContinue
if ($proc) { Write-Warning "检测到游戏正在运行 —— 新 DLL 需重启游戏才加载, 且可能占用文件导致复制失败。" }

$modDir = Join-Path $loaderDir 'mods\AstralParty.AgentMod'
New-Item -ItemType Directory -Path $modDir -Force | Out-Null
Copy-Item $dll  (Join-Path $modDir 'AstralParty.AgentMod.dll')  -Force
Copy-Item $json (Join-Path $modDir 'AstralParty.AgentMod.json') -Force

# 桥接用的是 SDK 的 GameEvents.RawAction(第 16 个事件)。SDK 旧了会 MissingMethodException,
# 所以和其它 mod 的部署脚本一样, 顺手把 SDK 同步过去。
$sdkDll = Join-Path $repo 'loader\CesiumLoader.SDK\bin\Release\netstandard2.0\CesiumLoader.SDK.dll'
$sdkDestDir = Join-Path $loaderDir 'sdk'
if ((Test-Path $sdkDll) -and (Test-Path $sdkDestDir)) {
    Copy-Item $sdkDll (Join-Path $sdkDestDir 'CesiumLoader.SDK.dll') -Force
    Write-Host "== 已同步 SDK: $sdkDestDir\CesiumLoader.SDK.dll =="
} else {
    Write-Warning "未同步 SDK(找不到 $sdkDll 或目标 $sdkDestDir); 桥接依赖 GameEvents.RawAction, 旧 SDK 会报 MissingMethodException。"
}

# 排障用: 打印 server 实际会用的桥接目录, 免得两侧目录不一致。
Write-Host ""
Write-Host "== 已部署到: $modDir =="
Write-Host "== MCP server: $mcpExe =="
Write-Host ""
Write-Host "MCP 客户端配置(Claude Desktop / 任意 MCP host):"
Write-Host (@{ mcpServers = @{ 'astral-party' = @{ command = $mcpExe } } } | ConvertTo-Json -Depth 6)
Write-Host ""
Write-Host "启动游戏后先跑一次自检(能看到『心跳: 活着』就说明通了):"
Write-Host "  & '$mcpExe' --print-config"
Write-Host ""
Write-Host "卸载: 删除 $modDir 目录即可(不影响加载器与其它 mod)。"
