<#
.SYNOPSIS
    发布到 dist\（默认按当前架构自动选 RID）。

.DESCRIPTION
    不要加 PublishSingleFile：Windows App SDK 要求 EnableMsixTooling，
    而且 sparse package 本来就需要一个能被 -ExternalLocation 指向的目录。

.EXAMPLE
    .\publish.ps1

.EXAMPLE
    .\publish.ps1 -Runtime win-x64 -OutDir dist-x64

.EXAMPLE
    .\publish.ps1 -SelfContained      # 目标机不用装 .NET 运行时，体积大很多
#>
param(
    [string]$Runtime,
    [string]$OutDir = 'dist',
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '找不到 dotnet，请先安装 .NET 10 SDK：https://dotnet.microsoft.com/download/dotnet/10.0'
}

if ([string]::IsNullOrWhiteSpace($Runtime)) {
    $Runtime = switch ($env:PROCESSOR_ARCHITECTURE) {
        'ARM64' { 'win-arm64' }
        'AMD64' { 'win-x64' }
        default { 'win-x64' }
    }
    Write-Host "未指定 -Runtime，按当前进程架构使用 $Runtime" -ForegroundColor DarkGray
}

if ($Runtime -notin @('win-x64', 'win-arm64')) {
    throw "只支持 win-x64 / win-arm64，收到：$Runtime"
}

if (Test-Path $OutDir) {
    Remove-Item $OutDir -Recurse -Force
}

$selfContainedValue = if ($SelfContained) { 'true' } else { 'false' }
$publishArgs = @(
    'publish', '-c', 'Release', '-r', $Runtime,
    "-p:SelfContained=$selfContainedValue",
    '-p:DebugType=None',
    '-o', $OutDir
)

Write-Host "dotnet $($publishArgs -join ' ')" -ForegroundColor DarkGray
dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败（退出码 $LASTEXITCODE）"
}

Write-Host ""
Write-Host "输出：$(Join-Path $PSScriptRoot $OutDir)" -ForegroundColor Green
Get-ChildItem $OutDir | Sort-Object Length -Descending |
    Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } } |
    Format-Table -AutoSize

Write-Host "运行：$(Join-Path (Join-Path $PSScriptRoot $OutDir) 'WinOcrApi.exe')" -ForegroundColor Green
Write-Host "要用 AI 引擎再加包身份：.\package\register.ps1 -ExternalLocation '$(Join-Path $PSScriptRoot $OutDir)' 然后 .\package\launch.ps1" -ForegroundColor DarkGray
