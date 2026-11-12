<#
.SYNOPSIS
    扫描热更侧代码里"可能被 AOT 裁剪"的 BCL 调用。

.DESCRIPTION
    这个游戏是 IL2CPP + HybridCLR: 热更代码能用的 BCL 成员, 只有**游戏自己也用到**的那些。
    用了游戏没用过的成员, 运行时不是返回 null, 而是 JIT 阶段抛
    <c>MissingMethodException: MethodNotFind</c> —— 而且异常发生在"调用它"的那一刻,
    那个方法根本编译不出来, **同方法里的 try/catch 接不住**, 异常会从模组入口逃出去。

    3.7.2 就是这么坏的: 闸门里读了 <c>Assembly.ManifestModule</c>, 25 个模组里 23 个直接起不来。
    所以这里做静态拦截: 下列成员一旦出现, 打包就失败, 必须先换成安全写法。

    安全写法要点: 风险调用放进**单独的方法**, 由外层 try/catch 包住调用。

.EXAMPLE
    pwsh -NoProfile -File tools\check-hotupdate-apis.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

# 会被 AOT 裁剪 / 在 HybridCLR 里不可用的成员。
$forbidden = @(
    @{ Pattern = '\.ManifestModule';                     Why = 'Assembly.ManifestModule 在热更环境被裁剪(3.7.2 事故)' },
    @{ Pattern = '\.EscapedCodeBase';                     Why = 'Assembly.EscapedCodeBase 在热更环境被裁剪' },
    @{ Pattern = '\.CodeBase\b';                         Why = 'Assembly.CodeBase 在热更环境被裁剪' },
    @{ Pattern = '\.GlobalAssemblyCache';                Why = 'Assembly.GlobalAssemblyCache 在热更环境被裁剪' },
    @{ Pattern = '\.Location\b';                         Why = 'Assembly.Location 在热更环境为空, 不能用来推路径' },
    @{ Pattern = 'GetType\([^;]*,\s*(true|false)\s*\)'; Why = 'Type.GetType/Assembly.GetType 的两参重载会被 AOT 裁剪(SDK 与 Bootstrap 里都记着)' },
    @{ Pattern = 'CurrentDomain\.BaseDirectory';          Why = 'AppDomain.CurrentDomain.BaseDirectory 在 HybridCLR 里不支持' },
    @{ Pattern = 'AppContext\.BaseDirectory';              Why = 'AppContext.BaseDirectory 在热更环境不可靠' },
    @{ Pattern = '\.GetExportedTypes\(';                  Why = 'Assembly.GetExportedTypes 会被裁剪' },
    @{ Pattern = '\.DefinedTypes\b';                      Why = 'Type.DefinedTypes 在热更环境不可用' },
    @{ Pattern = '\.ReflectionOnly';                       Why = '反射专用 API 在热更环境不存在' }
)

# 扫描范围: SDK + Bootstrap + 全部内置模组(与打包时的模组清单一致)。
$roots = @('loader\CesiumLoader.SDK', 'loader\CesiumLoader.Bootstrap')
$builtInMods = @(
    (Get-Content (Join-Path $PSScriptRoot 'builtin-mods.json') -Raw | ConvertFrom-Json) |
        ForEach-Object { [string]$_ }
)
foreach ($mod in $builtInMods) { $roots += "mods\$mod" }

$files = @()
foreach ($root in $roots) {
    if (-not (Test-Path -LiteralPath $root)) { continue }
    $files += Get-ChildItem -LiteralPath $root -Recurse -File -Filter '*.cs' |
        Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\' }
}
if ($files.Count -eq 0) { throw '没有扫描到任何源文件, 路径可能不对' }

$problems = @()
foreach ($rule in $forbidden) {
    foreach ($file in $files) {
        $hits = Select-String -LiteralPath $file.FullName -Pattern $rule.Pattern
        foreach ($hit in $hits) {
            $line = $hit.Line.Trim()
            # 注释里提到这些名字是允许的(文档要解释为什么不能用)。
            if ($line -match '^\s*(//|///|\*|/\*)') { continue }
            if ($line -match '^\s*//') { continue }
            $problems += [pscustomobject]@{
                File    = $file.FullName.Substring($repo.Length + 1)
                Line    = $hit.LineNumber
                Why     = $rule.Why
                Content = $line
            }
        }
    }
}

if ($problems.Count -gt 0) {
    Write-Host '热更侧代码里出现了可能被 AOT 裁剪的 BCL 调用:'
    foreach ($p in $problems) {
        Write-Host ("  {0}:{1}  ({2})" -f $p.File, $p.Line, $p.Why)
        Write-Host ("      {0}" -f $p.Content)
    }
    throw "发现 $($problems.Count) 处风险调用; 参考 tools\check-hotupdate-apis.ps1 顶部说明改成安全写法(风险调用单独成方法 + 外层 try/catch)"
}

Write-Host "热更侧 BCL 扫描通过: $($files.Count) 个源文件, 无风险调用"
