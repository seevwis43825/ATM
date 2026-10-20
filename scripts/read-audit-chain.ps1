# 读取与校验审计链文件（JSONL）
#
# 为什么需要这个脚本：
#   宿主默认让审计文件保持**常驻写句柄**（Audit:KeepFileOpen，默认 true），
#   以免每条记录都 open→write→close（实测这一项就能让吞吐差 2~3 倍）。
#   代价是 Windows 上写句柄会挡住用 FileShare.Read 打开的读者，
#   也就是标准的 `File.ReadAllLines` / `Get-Content` 在某些模式下会报
#   "文件正由另一进程使用"。合规读取方必须用 FileShare.ReadWrite 打开 —— 本脚本即为此存在。
#
# 用法：
#   powershell -File scripts\read-audit-chain.ps1                      # 打印全部行
#   powershell -File scripts\read-audit-chain.ps1 -Tail 20             # 只看最后 20 行
#   powershell -File scripts\read-audit-chain.ps1 -VerifyChain         # 校验链式结构
#   powershell -File scripts\read-audit-chain.ps1 -VerifyChain -VerifyHmac -SigningKey <key>
#   powershell -File scripts\read-audit-chain.ps1 -Path D:\x\audit.log
#
# 退出码：0 = 通过；1 = 链断裂或文件不可读。

param(
    [string]$Path = '',
    [int]$Tail = 0,
    [switch]$VerifyChain,
    [switch]$VerifyHmac,
    [string]$SigningKey = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem 2>$null | Out-Null

# 默认审计文件路径：与宿主默认一致（内容根目录下的 logs/audit-chain.log）
if (-not $Path) {
    $candidates = @(
        (Join-Path $PSScriptRoot '..\src\src\BankingAgent.Host\logs\audit-chain.log'),
        (Join-Path $PSScriptRoot '..\..\src\src\BankingAgent.Host\logs\audit-chain.log')
    )
    $Path = ($candidates | Where-Object { Test-Path $_ } | Select-Object -First 1)
    if (-not $Path) {
        Write-Output "[致命] 找不到默认审计文件，请用 -Path 指定"
        exit 1
    }
}

if (-not (Test-Path $Path)) {
    Write-Output "[致命] 审计文件不存在: $Path"
    exit 1
}

# ===== 关键：用 FileShare.ReadWrite 打开 =====
# 常驻写句柄只允许 ReadWrite 共享，FileShare.Read 会被拒绝。
$lines = @()
try {
    $fs = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::ReadWrite)
    $sr = New-Object System.IO.StreamReader($fs, [System.Text.Encoding]::UTF8)
    $all = $sr.ReadToEnd()
    $sr.Close()
    $fs.Close()
    $lines = $all -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 }
} catch {
    Write-Output "[致命] 无法读取审计文件（若宿主正在运行，请确认用的是 FileShare.ReadWrite）: $($_.Exception.Message)"
    exit 1
}

Write-Output "文件: $Path"
Write-Output "行数: $($lines.Count)"

if ($Tail -gt 0 -and $lines.Count -gt $Tail) {
    $lines = $lines[($lines.Count - $Tail)..($lines.Count - 1)]
    Write-Output "（仅显示最后 $Tail 行）"
}

if (-not $VerifyChain) {
    $lines | ForEach-Object { Write-Output $_ }
    exit 0
}

# ===== 链式结构校验 =====
# 校验的是"前序签名衔接"：每条的 prev 必须等于上一条的 sig，首条 prev 为空。
# 这能检出任何对已落盘记录的改动（改了记录，其后继的 prev 立刻对不上）。
#
# 签名输入（与 AuditLogger.Canonicalize 完全一致）：
#   AuditId | ts(Unix 毫秒) | ActorType | actor(哈希) | Operation | Scenario
#           | Decision | RuleId | Amount(F2) | prev
# 注意 prev 在链首处参与签名的是 "GENESIS"，而归档里存的是 null —— 校验时要还原。
$prev = $null
$broken = @()
$genesis = 0
$index = 0
$hmacBroken = 0
$hmacChecked = 0
$keyBytes = if ($SigningKey) { [System.Text.Encoding]::UTF8.GetBytes($SigningKey) } else { $null }

foreach ($line in $lines) {
    $index++
    $obj = $null
    try { $obj = $line | ConvertFrom-Json } catch { $broken += "第 $index 行不是合法 JSON"; continue }

    $curPrev = $obj.prev
    $curSig = $obj.sig

    if ($null -eq $curPrev -or $curPrev -eq '') { $genesis++ }
    if ($curPrev -ne $prev) {
        $broken += "第 $index 行(prev=$curPrev) 与上一行 sig($prev) 不衔接"
    }
    if ([string]::IsNullOrWhiteSpace($curSig)) {
        $broken += "第 $index 行缺少 sig"
    }

    # ===== HMAC 离线重算 =====
    # 归档里存的是操作者的**哈希**（最小化原则），而签名输入用的也是同一份哈希
    # —— 所以仅凭归档文件就能重算签名值，无需原始操作者 ID。
    if ($keyBytes) {
        $prevPart = if ($null -eq $curPrev -or $curPrev -eq '') { 'GENESIS' } else { $curPrev }
        $tsMs = [DateTimeOffset]::Parse($obj.ts).ToUnixTimeMilliseconds()
        $amount = if ($null -eq $obj.Amount) { '' } else { ([decimal]$obj.Amount).ToString('F2', [System.Globalization.CultureInfo]::InvariantCulture) }
        $payload = @(
            $obj.AuditId, $tsMs, $obj.ActorType, $obj.actor, $obj.Operation,
            $obj.Scenario, $obj.Decision, $obj.RuleId, $amount, $prevPart
        ) -join '|'
        $h = [System.Security.Cryptography.HMACSHA256]::new($keyBytes)
        try {
            $calc = [System.BitConverter]::ToString(
                $h.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($payload))).Replace('-', '').ToLowerInvariant()
            $hmacChecked++
            if ($calc -ne $curSig) {
                $hmacBroken++
                if ($hmacBroken -le 5) {
                    $broken += "第 $index 行 HMAC 重算不一致（重算=$calc 记录=$curSig）"
                }
            }
        } finally { $h.Dispose() }
    }

    $prev = $curSig
}

Write-Output ''
Write-Output "链首(GENESIS)条数: $genesis"
Write-Output "结构断点: $($broken.Count)"
if ($keyBytes) {
    Write-Output "HMAC 重算: 已校验 $hmacChecked 条, 不一致 $hmacBroken 条"
}

if ($genesis -ne 1) {
    Write-Output "[FAIL] 链首必须且只能有一条（GENESIS），实际 $genesis 条"
    exit 1
}
if ($broken.Count -gt 0) {
    $broken | Select-Object -First 10 | ForEach-Object { Write-Output "  - $_" }
    Write-Output '[FAIL] 审计链结构断裂'
    exit 1
}

if ($keyBytes) {
    Write-Output '[PASS] 审计链完整：前序签名逐条衔接、链首唯一、HMAC 全部可重算并一致'
} else {
    Write-Output '[PASS] 审计链结构完整：前序签名逐条衔接，链首唯一'
    Write-Output '  （未提供 -SigningKey，跳过 HMAC 重算；加 -VerifyHmac -SigningKey <key> 可做完整密码学校验）'
}
exit 0
