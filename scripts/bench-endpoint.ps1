# 并发测速：对单个端点发 N 个并发请求，报告吞吐与批量耗时
#
# 用法:
#   powershell -File scripts/bench-endpoint.ps1 -Url "http://localhost:5200/api/corebank/v1/cards?userId=u_demo01" -Concurrency 20 -Requests 400
#
# 为什么不用 PowerShell 的 Invoke-WebRequest 循环：
#   那个循环测出来的是 PowerShell 自身的开销（本机约 50 req/s 上限），
#   测不出服务端真实吞吐。这里改用 .NET HttpClient，一次并发发起一批请求再统一等待，
#   测的才是服务端。
#
# 为什么不用后台线程跑脚本块：
#   Windows PowerShell 的脚本块需要 Runspace，丢到线程池线程上会抛
#   「此线程中没有可用于运行脚本的运行空间」。因此所有请求都在主运行空间发起，
#   只用 Task.WhenAll 统一等待；不输出单请求分位数（分位数由 LoadTest 提供）。
param(
    [string]$Url,
    [int]$Concurrency = 20,
    [int]$Requests = 400,
    [string]$Bearer = '',
    [string]$Method = 'GET',
    [string]$Body = '',
    [string]$BodyFile = ''
)

$ErrorActionPreference = 'Stop'

# ===== 请求体来源 =====
# 为什么要有 -BodyFile：
#   Windows 命令行传参会把 JSON 里的双引号吃掉 ——
#   传入 {"a":"b"}，子进程收到的是 {a:b}，服务端直接回 400 Bad Request。
#   这不是服务端的锅，是 PowerShell 5.1 的原生参数传参行为，
#   而一个只报"失败 400 次"不给原因的压测脚本会让人误判成服务有问题。
#   带 JSON 体的 POST 一律用 -BodyFile（UTF-8 文件）。
if ($BodyFile) {
    if (-not (Test-Path $BodyFile)) { throw "请求体文件不存在: $BodyFile" }
    $Body = [System.IO.File]::ReadAllText($BodyFile, [System.Text.Encoding]::UTF8)
}

# Windows PowerShell 5.1 默认不加载 System.Net.Http，用前先显式加载。
Add-Type -AssemblyName System.Net.Http

$handler = New-Object System.Net.Http.HttpClientHandler
$handler.AllowAutoRedirect = $false
$client = New-Object System.Net.Http.HttpClient($handler)
$client.Timeout = [TimeSpan]::FromSeconds(120)
if ($Bearer) {
    $client.DefaultRequestHeaders.Authorization =
        New-Object System.Net.Http.Headers.AuthenticationHeaderValue('Bearer', $Bearer)
}

$httpMethod = New-Object System.Net.Http.HttpMethod($Method)

function New-Request {
    $req = New-Object System.Net.Http.HttpRequestMessage($httpMethod, $Url)
    if ($Body) {
        $req.Content = New-Object System.Net.Http.StringContent(
            $Body, [System.Text.Encoding]::UTF8, 'application/json')
    }
    return $req
}

# 预热：排除 JIT 与连接建立的开销
for ($i = 0; $i -lt 20; $i++) {
    $req = New-Request
    $resp = $client.SendAsync($req).GetAwaiter().GetResult()
    $resp.Dispose()
    $req.Dispose()
}

$ok = 0
$failed = 0
$errors = New-Object System.Collections.Generic.List[string]
$statuses = @{}
$sw = [System.Diagnostics.Stopwatch]::StartNew()

$remaining = $Requests
while ($remaining -gt 0) {
    $batch = [Math]::Min($Concurrency, $remaining)
    $tasks = New-Object System.Collections.Generic.List[System.Threading.Tasks.Task[System.Net.Http.HttpResponseMessage]]

    for ($i = 0; $i -lt $batch; $i++) {
        $req = New-Request
        $tasks.Add($client.SendAsync($req))
    }

    foreach ($t in $tasks) {
        try {
            $resp = $t.GetAwaiter().GetResult()
            $code = [int]$resp.StatusCode
            if ($statuses.ContainsKey($code)) { $statuses[$code]++ } else { $statuses[$code] = 1 }
            if ($resp.IsSuccessStatusCode) { $ok++ } else { $failed++ }
            $resp.Dispose()
        }
        catch {
            $failed++
            # 失败必须能看出原因：只报"失败 400 次"的压测脚本没有诊断价值
            $msg = $_.Exception.Message
            if ($msg -and $errors.Count -lt 5 -and -not $errors.Contains($msg)) {
                $errors.Add($msg)
            }
        }
    }

    $remaining -= $batch
}

$sw.Stop()
$client.Dispose()

$elapsed = $sw.Elapsed.TotalSeconds
Write-Output "URL         : $Url"
Write-Output "并发 / 请求 : $Concurrency / $Requests"
Write-Output ("吞吐        : {0:N1} req/s（批量耗时 {1:N2}s）" -f ($Requests / $elapsed), $elapsed)
Write-Output ("成功 / 失败 : {0} / {1}" -f $ok, $failed)
if ($statuses.Count -gt 0) {
    $dist = ($statuses.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Key)x$($_.Value)" }) -join ', '
    Write-Output "状态码分布  : $dist"
}
if ($errors.Count -gt 0) {
    Write-Output "失败原因（去重后前几条）:"
    foreach ($e in $errors) { Write-Output "  - $e" }
}
