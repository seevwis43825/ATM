# 校验 Markdown 跨文档锚点链接是否可达
# 用法: powershell -File scripts/check-anchors.ps1
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$files = @()
foreach ($r in @('docs', 'src')) {
    $files += Get-ChildItem -Recurse -File -Include *.md -Path (Join-Path $root $r) -ErrorAction SilentlyContinue
}
$files += Get-ChildItem -File -Filter *.md -Path $root

function Test-Skip([string]$path) {
    return ($path -like '*\obj\*') -or ($path -like '*\bin\*')
}

# 收集每个文件可用的锚点
$anchorsByFile = @{}
foreach ($f in $files) {
    if (Test-Skip $f.FullName) { continue }
    $text = [System.IO.File]::ReadAllText($f.FullName, [System.Text.Encoding]::UTF8)
    $set = @{}
    foreach ($m in [regex]::Matches($text, '<a id="([^"]+)"')) {
        $set[$m.Groups[1].Value.ToLower()] = $true
    }
    foreach ($m in [regex]::Matches($text, '(?m)^#{1,6}\s+(.+)$')) {
        $h = $m.Groups[1].Value.Trim().ToLower()
        $h = $h -replace '[^\p{L}\p{Nd}\s\-_]', ''
        $h = $h -replace '\s+', '-'
        $set[$h] = $true
    }
    $anchorsByFile[[System.IO.Path]::GetFullPath($f.FullName).ToLower()] = $set
}

$problems = @()
foreach ($f in $files) {
    if (Test-Skip $f.FullName) { continue }
    $text = [System.IO.File]::ReadAllText($f.FullName, [System.Text.Encoding]::UTF8)
    foreach ($m in [regex]::Matches($text, '\]\(([^)\s]+\.md)#([^)\s]+)\)')) {
        $rel = $m.Groups[1].Value
        $anc = $m.Groups[2].Value.ToLower()
        $target = [System.IO.Path]::GetFullPath((Join-Path $f.DirectoryName $rel)).ToLower()
        if (-not $anchorsByFile.ContainsKey($target)) {
            $problems += "$($f.Name) -> $rel#$anc  [target file missing]"
        }
        elseif (-not $anchorsByFile[$target].ContainsKey($anc)) {
            $problems += "$($f.Name) -> $rel#$anc  [anchor missing]"
        }
    }
}

if ($problems.Count -eq 0) {
    Write-Output "OK: all anchors reachable ($($files.Count) files)"
}
else {
    $problems | Sort-Object -Unique | ForEach-Object { Write-Output "  $_" }
    Write-Output "TOTAL: $($problems.Count) unreachable"
}
