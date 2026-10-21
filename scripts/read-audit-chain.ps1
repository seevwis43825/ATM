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
#
# ===== 关于时间戳：为什么优先用 tsMs 而不是 ts =====
# 归档里同时有 ts（ISO-8601 字符串，给人读）和 tsMs（整数毫秒，给机器算）。
# tsMs 与参与签名的那个整数完全同源，因此校验时直接用它 —— 不做任何日期解析。
#
# 为什么必须这样：ISO 字符串的解析结果依工具而异。PowerShell 7 的 ConvertFrom-Json
# 会把日期串转成 [DateTime]，再被 [DateTimeOffset]::Parse 消费时先按当前区域格式
# 转成字符串，亚毫秒小数被丢掉（07:53:38.1234567 → "07:53:38"），
# epoch 毫秒差了几毫秒 → 签名载荷整体不同 → **每一条都判为 HMAC 不一致**，
# 而链结构其实完好（曾表现为「结构断点 5」，极易误判成链断裂）。
#
# 老归档没有 tsMs 时才回退解析 ts，并对「已被强转为 [DateTime]」做保瞬间处理。

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
#
# 用 [System.IO.Path]::Combine 逐段拼接，**不要**用带反斜杠的字符串：
# 反斜杠在 Linux 上不是路径分隔符，'..\src\src\...' 在 Linux 上会被当成
# 一个文件名，候选路径全部匹配不上 ⇒ 脚本报「找不到默认审计文件」而退出 1。
if (-not $Path) {
    $candidates = @(
        [System.IO.Path]::Combine($PSScriptRoot, '..', 'src', 'src', 'BankingAgent.Host', 'logs', 'audit-chain.log'),
        [System.IO.Path]::Combine($PSScriptRoot, '..', '..', 'src', 'src', 'BankingAgent.Host', 'logs', 'audit-chain.log')
    )
    $Path = ($candidates | Where-Object { Test-Path $_ } | Select-Object -First 1)
    if (-not $Path) {
        Write-Output "[致命] 找不到默认审计文件，请用 -Path 指定。已尝试："
        $candidates | ForEach-Object { Write-Output "  - $_" }
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
# 结构问题与 HMAC 明细必须**分开计数**：
# 早期版本把两者塞进同一个 $broken，于是「结构断点: 5」其实是 0 个结构断点
# 加 5 条 HMAC 不一致明细 —— 标签误导，排查时容易误判成链断裂。
$structBroken = @()
$hmacMessages = @()
$genesis = 0
$index = 0
$hmacBroken = 0
$hmacChecked = 0
$keyBytes = if ($SigningKey) { [System.Text.Encoding]::UTF8.GetBytes($SigningKey) } else { $null }

# ===== ConvertFrom-Json 的日期强制必须关掉 =====
# PowerShell 7.5 起 ConvertFrom-Json 会把 ISO-8601 日期串转成 [DateTime]（默认 -DateKind Local）。
# 它再被 [DateTimeOffset]::Parse 消费时，会先按**当前区域格式**转成字符串，
# 亚毫秒小数被丢掉（07:53:38.1234567 → "07:53:38"），epoch 毫秒差了几毫秒 ——
# 于是签名载荷整体不同，**每一条都被判为 HMAC 不一致**，而链结构其实完好。
# （Windows PowerShell 5.1 不做这个转换，所以同一份脚本在本地过、在 CI 挂。）
# 有 -DateKind 就强制按字符串解析；5.1 没有这个参数，本来就不会转。
$cfjParams = @{}
if ((Get-Command ConvertFrom-Json).Parameters.ContainsKey('DateKind')) {
    $cfjParams['DateKind'] = 'String'
}

foreach ($line in $lines) {
    $index++
    $obj = $null
    try { $obj = $line | ConvertFrom-Json @cfjParams } catch { $structBroken += "第 $index 行不是合法 JSON"; continue }

    $curPrev = $obj.prev
    $curSig = $obj.sig

    if ($null -eq $curPrev -or $curPrev -eq '') { $genesis++ }
    if ($curPrev -ne $prev) {
        $structBroken += "第 $index 行(prev=$curPrev) 与上一行 sig($prev) 不衔接"
    }
    if ([string]::IsNullOrWhiteSpace($curSig)) {
        $structBroken += "第 $index 行缺少 sig"
    }

    # ===== HMAC 离线重算 =====
    # 归档里存的是操作者的**哈希**（最小化原则），而签名输入用的也是同一份哈希
    # —— 所以仅凭归档文件就能重算签名值，无需原始操作者 ID。
    if ($keyBytes) {
        $prevPart = if ($null -eq $curPrev -or $curPrev -eq '') { 'GENESIS' } else { $curPrev }
        # ===== 时间戳：优先用归档里的整数毫秒 =====
        # 归档自 2026-10-21 起带 tsMs 字段，其值与 C# Canonicalize 里参与签名的
        # Timestamp.ToUnixTimeMilliseconds() **完全同源**，因此不需要任何日期解析 ——
        # 这正是它能绕开「不同工具解析 ISO 日期结果不同」这个坑的原因。
        # 老归档没有 tsMs 时才回退到解析 ts；此时必须拿到原始字符串（见上面的 $cfjParams）。
        $tsMs = if ($null -ne $obj.tsMs) {
            [long]$obj.tsMs
        } elseif ($obj.ts -is [datetime]) {
            # 已经是 [DateTime]：直接转 DateTimeOffset 保瞬间，不要先转字符串
            ([DateTimeOffset]$obj.ts).ToUnixTimeMilliseconds()
        } else {
            [DateTimeOffset]::Parse([string]$obj.ts).ToUnixTimeMilliseconds()
        }
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
                    $hmacMessages += "第 $index 行 HMAC 重算不一致（重算=$calc 记录=$curSig）"
                }
            }
        } finally { $h.Dispose() }
    }

    $prev = $curSig
}

Write-Output ''
Write-Output "链首(GENESIS)条数: $genesis"
Write-Output "结构断点: $($structBroken.Count)"
if ($keyBytes) {
    Write-Output "HMAC 重算: 已校验 $hmacChecked 条, 不一致 $hmacBroken 条"
}
if ($hmacMessages.Count -gt 0) {
    $hmacMessages | ForEach-Object { Write-Output "  - $_" }
}

if ($genesis -ne 1) {
    Write-Output "[FAIL] 链首必须且只能有一条（GENESIS），实际 $genesis 条"
    exit 1
}
if ($structBroken.Count -gt 0) {
    $structBroken | Select-Object -First 10 | ForEach-Object { Write-Output "  - $_" }
    Write-Output '[FAIL] 审计链结构断裂'
    exit 1
}
if ($keyBytes -and $hmacBroken -gt 0) {
    Write-Output "[FAIL] 有 $hmacBroken 条记录的 HMAC 无法离线重算一致（密钥不匹配，或归档缺少 tsMs 字段）"
    exit 1
}

if ($keyBytes) {
    Write-Output '[PASS] 审计链完整：前序签名逐条衔接、链首唯一、HMAC 全部可重算并一致'
} else {
    Write-Output '[PASS] 审计链结构完整：前序签名逐条衔接，链首唯一'
    Write-Output '  （未提供 -SigningKey，跳过 HMAC 重算；加 -VerifyHmac -SigningKey <key> 可做完整密码学校验）'
}
exit 0
