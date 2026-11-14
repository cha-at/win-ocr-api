<#
.SYNOPSIS
    以「有包身份」的方式启动服务：Windows AI 引擎只对带包身份的进程开放。

.DESCRIPTION
    前提是先跑过 .\register.ps1。启动后顺手查一次 /health。

.EXAMPLE
    .\launch.ps1

.EXAMPLE
    .\launch.ps1 -Url http://127.0.0.1:9000
#>
param(
    [switch]$Foreground,
    [string]$Url
)

$ErrorActionPreference = 'Stop'
$packageName = 'WinOcrApi'
$appId = 'WinOcrApi'

if ([string]::IsNullOrWhiteSpace($Url)) {
    $Url = $env:OCR_API_URLS
}
if ([string]::IsNullOrWhiteSpace($Url)) {
    $Url = 'http://127.0.0.1:8737'
}
$Url = $Url.TrimEnd('/')

$pkg = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue
if (-not $pkg) {
    throw "包未注册，先运行 .\register.ps1（或在 dist 目录直接跑 WinOcrApi.exe，AI 引擎不可用）"
}

$target = "shell:AppsFolder\$($pkg.PackageFamilyName)!$appId"

if ($Foreground) {
    Write-Host "通过包身份启动：$target"
}

Start-Process explorer.exe -ArgumentList $target

Start-Sleep -Seconds 3
$proc = Get-Process WinOcrApi -ErrorAction SilentlyContinue | Sort-Object StartTime -Descending | Select-Object -First 1
if ($proc) {
    Write-Host "已启动 PID $($proc.Id)（$($proc.Path)）" -ForegroundColor Green
    try {
        $health = Invoke-RestMethod "$Url/health" -TimeoutSec 5
        Write-Host "HTTP 正常，版本 $($health.version)，AI 状态：$($health.ai.status)"
    } catch {
        Write-Host "HTTP 还没起来：$($_.Exception.Message)" -ForegroundColor Yellow
    }
} else {
    Write-Host "没看到启动的进程，可能被安全策略拦了" -ForegroundColor Yellow
}
