# 注册 / 注销 sparse package：给未打包的 WinOcrApi.exe 授予包身份（Windows AI API 的前提）。
param(
    [string]$ExternalLocation = (Join-Path $PSScriptRoot '..\dist'),
    [switch]$Unregister
)

$ErrorActionPreference = 'Stop'
$packageName = 'WinOcrApi'

if ($Unregister) {
    Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue | Remove-AppxPackage
    Write-Host "已注销 $packageName" -ForegroundColor Yellow
    return
}

$manifest = Join-Path $PSScriptRoot 'AppxManifest.xml'
$loc = (Resolve-Path $ExternalLocation).Path

if (-not (Test-Path (Join-Path $loc 'WinOcrApi.exe'))) {
    throw "外部目录里没有 WinOcrApi.exe：$loc（先运行 ..\publish.ps1，或用 -ExternalLocation 指定）"
}

Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue | Remove-AppxPackage

Write-Host "注册 $packageName -> $loc"
Add-AppxPackage -Register $manifest -ExternalLocation $loc

$pkg = Get-AppxPackage -Name $packageName
if (-not $pkg) { throw '注册失败，未查到包' }

Write-Host ""
Write-Host "已注册：" -ForegroundColor Green
$pkg | Select-Object Name, Version, PackageFullName, InstallLocation | Format-List
Write-Host "带包身份启动：.\package\launch.ps1   （或 shell:AppsFolder\$($pkg.PackageFamilyName)!WinOcrApi）"
Write-Host "注意：直接运行 dist\WinOcrApi.exe 不带包身份，AI 引擎不可用，传统引擎照常。"
