# 从 trx 里提取失败用例，输出 Markdown
#
# 为什么需要这个脚本：
#   GitHub Actions 的**日志**需要仓库管理员权限才能下载，
#   公开仓库的外部审阅者看不到 —— 于是 CI 一红，失败原因就成了黑盒。
#   而**作业摘要**（step summary）对公开仓库是匿名可见的。
#   所以 CI 失败时把「失败用例名 + 错误消息 + 调用栈」写进摘要，
#   排查不必再靠猜，也不用每次都去申请日志权限。
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\summarize-trx-failures.ps1 -TrxPath "trx 路径"
#   CI 里则直接追加到 $env:GITHUB_STEP_SUMMARY。
param(
    [Parameter(Mandatory = $true)]
    [string]$TrxPath
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $TrxPath))
{
    Write-Output "未找到 trx：$TrxPath（测试可能没走到产出阶段，看上一步的输出）"
    return
}

[xml]$trx = Get-Content -LiteralPath $TrxPath -Raw -Encoding UTF8

# trx 的默认命名空间不影响 PowerShell 的 XML 属性访问（按本地名匹配）
$results = @($trx.TestRun.Results.UnitTestResult)
$failed = @($results | Where-Object { $_.outcome -eq 'Failed' })

if ($failed.Count -eq 0)
{
    $total = $results.Count
    Write-Output "trx 中没有失败用例（共 $total 条）。"
    Write-Output "=> 失败发生在**测试宿主启动 / 用例收集**阶段，而不是某个用例内部 ——"
    Write-Output "   典型原因是测试程序集加载失败或缺少运行时框架（检查上一步的输出）。"
    return
}

Write-Output "失败用例共 $($failed.Count) 条（总计 $($results.Count) 条）。"
Write-Output ""

foreach ($r in $failed)
{
    Write-Output "### [失败] $($r.testName)"
    Write-Output ""

    $message = $r.Output.ErrorInfo.Message
    if ($message)
    {
        Write-Output $message
        Write-Output ""
    }

    $stack = $r.Output.ErrorInfo.StackTrace
    if ($stack)
    {
        Write-Output "``````text"
        Write-Output $stack
        Write-Output "``````"
        Write-Output ""
    }
}
