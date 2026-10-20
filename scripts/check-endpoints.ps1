# 比对「代码里实际存在的端点」与「文档里列出的端点」
# 用法: powershell -File scripts/check-endpoints.ps1
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot

# ===== 1. 从代码里抽出实际端点 =====
$hostEndpoints = @()
Get-ChildItem -File -Filter *.cs -Path (Join-Path $root 'src\src\BankingAgent.Host\Endpoints') -ErrorAction SilentlyContinue | ForEach-Object {
    $t = [System.IO.File]::ReadAllText($_.FullName, [System.Text.Encoding]::UTF8)
    foreach ($m in [regex]::Matches($t, 'Map(Get|Post|Put|Delete)\("([^"]+)"')) {
        $hostEndpoints += ("{0} {1}" -f $m.Groups[1].Value.ToUpper(), $m.Groups[2].Value)
    }
}
$bankEndpoints = @()
Get-ChildItem -File -Filter *.cs -Path (Join-Path $root 'src\mock-bank\MockBank.Api\Endpoints') -ErrorAction SilentlyContinue | ForEach-Object {
    $t = [System.IO.File]::ReadAllText($_.FullName, [System.Text.Encoding]::UTF8)
    # 端点文件里的端点多挂在 group 上，实际路径 = 组前缀 + 相对路径
    $prefix = ''
    $pm = [regex]::Match($t, 'RoutePrefix\s*=\s*"([^"]*)"')
    if ($pm.Success) { $prefix = $pm.Groups[1].Value }
    foreach ($m in [regex]::Matches($t, 'Map(Get|Post|Put|Delete)\("([^"]*)"')) {
        $rel = $m.Groups[2].Value
        # 有组前缀时，相对路径一律拼在组前缀之后（路径以 / 开头也只是组内子路径）
        if ($prefix -and -not $rel.StartsWith($prefix)) { $full = if ($rel -eq '') { $prefix } else { "$prefix$rel" } }
        else { $full = $rel }
        $bankEndpoints += ("{0} {1}" -f $m.Groups[1].Value.ToUpper(), $full)
    }
}
# /health 与 /openapi/v1.json 在 OpsEndpoints 里也是同样写法，已被上面的正则覆盖

# ===== 2. 从文档里抽出已列出的端点 =====
function Get-DocEndpoints([string]$relativePath) {
    $p = Join-Path $root $relativePath
    if (-not (Test-Path $p)) { return @() }
    $t = [System.IO.File]::ReadAllText($p, [System.Text.Encoding]::UTF8)
    $found = @()
    foreach ($m in [regex]::Matches($t, '(?m)^\|\s*\*{0,2}(GET|POST|PUT|DELETE)\*{0,2}\s*\|\s*`([^`]+)`')) {
        $found += ("{0} {1}" -f $m.Groups[1].Value, $m.Groups[2].Value)
    }
    return $found
}

$docHost = (Get-DocEndpoints 'README.md') + (Get-DocEndpoints 'docs\09-uml\03-api-contract.md') + (Get-DocEndpoints 'docs\plugin\02-api-reference.md')
$docBank = (Get-DocEndpoints 'docs\09-uml\03-api-contract.md') + (Get-DocEndpoints 'docs\plugin\02-api-reference.md') + (Get-DocEndpoints 'docs\plugin\00-quick-start.md')

Write-Output "代码端点：Host $($hostEndpoints.Count) 个 / MockBank $($bankEndpoints.Count) 个"
Write-Output ""

# 把 {param} 占位统一成 *，便于比对
function Normalize([string]$e) {
    # 忽略查询串：文档里常写成 ?keyword= 的形式
    $e = $e -replace '\?.*$', ''
    return ($e -replace '\{[^}]+\}', '{p}')
}

Write-Output "=== Host 端点：代码有但文档未列 ==="
$missing = @()
foreach ($e in ($hostEndpoints | Select-Object -Unique)) {
    $n = Normalize $e
    if (-not ($docHost | Where-Object { (Normalize $_) -eq $n })) { $missing += $e }
}
if ($missing.Count -eq 0) { Write-Output "  （无）" } else { $missing | ForEach-Object { Write-Output "  $_" } }

Write-Output ""
Write-Output "=== MockBank 端点：代码有但文档未列 ==="
$missing2 = @()
foreach ($e in ($bankEndpoints | Select-Object -Unique)) {
    $n = Normalize $e
    if (-not ($docBank | Where-Object { (Normalize $_) -eq $n })) { $missing2 += $e }
}
if ($missing2.Count -eq 0) { Write-Output "  （无）" } else { $missing2 | ForEach-Object { Write-Output "  $_" } }
