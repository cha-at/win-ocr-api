<#
.SYNOPSIS
    打包成 MSIX 并自签名（Windows AI 的受限能力 systemAIModels 可能只认已签名的包）。

.DESCRIPTION
    生成的 .cer 需要装进「本地计算机 / 受信任人」才允许安装 —— 这一步需要管理员权限。
    pfx 密码默认随机生成，不再写死在脚本里；需要固定密码时用 -PfxPassword 指定。
    签名证书只是本机开发用的自签证书，不要信任到别的机器。

.EXAMPLE
    .\build-msix.ps1
#>
param(
    [string]$OutDir,
    [string]$CertSubject = 'CN=WinOcrApi',
    [string]$PfxPassword
)

$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dist = Join-Path $root 'dist'
$stage = Join-Path $root 'msix_stage'
if (-not $OutDir) {
    $OutDir = Join-Path $root 'out'
}

if (-not (Test-Path (Join-Path $dist 'WinOcrApi.exe'))) {
    throw '先运行 ..\publish.ps1 生成 dist'
}

# 找最新版的 Windows SDK 工具，不写死版本号
function Get-WinTool([string]$tool) {
    $binRoot = 'C:\Program Files (x86)\Windows Kits\10\bin'
    if (-not (Test-Path $binRoot)) {
        return $null
    }

    $preferredArch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
    Get-ChildItem $binRoot -Directory |
        Sort-Object { [version]($_.Name -replace '[^0-9\.].*$', '') } -Descending |
        ForEach-Object {
            Join-Path $_.FullName (Join-Path $preferredArch $tool)
            Join-Path $_.FullName (Join-Path 'x64' $tool)
        } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1
}

$makeappx = Get-WinTool 'makeappx.exe'
if (-not $makeappx) {
    throw '找不到 makeappx.exe —— 装 Windows SDK（或 WindowsAppSDK 的 BuildTools 包）后重试'
}

$signtool = Join-Path (Split-Path $makeappx -Parent) 'signtool.exe'
if (-not (Test-Path $signtool)) {
    throw "找不到 signtool.exe（同目录：$(Split-Path $makeappx -Parent)）"
}

Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item "$dist\*" $stage -Recurse -Force
Copy-Item (Join-Path $PSScriptRoot 'Images') (Join-Path $stage 'Images') -Recurse -Force

# 包内布局：exe 在包里，所以去掉 AllowExternalContent，入口用 FullTrustApplication
$manifest = Get-Content (Join-Path $PSScriptRoot 'AppxManifest.xml') -Raw
$manifest = $manifest -replace '\s*<uap10:AllowExternalContent>true</uap10:AllowExternalContent>', ''
$manifest = $manifest -replace 'uap10:TrustLevel="mediumIL"\s*\r?\n\s*uap10:RuntimeBehavior="win32App"', 'EntryPoint="Windows.FullTrustApplication"'
$manifest | Set-Content -Encoding UTF8 (Join-Path $stage 'AppxManifest.xml')

New-Item -ItemType Directory -Force $OutDir | Out-Null
$msix = Join-Path $OutDir 'WinOcrApi.msix'
& $makeappx pack /d $stage /p $msix /o
if ($LASTEXITCODE -ne 0) {
    throw "makeappx 打包失败（退出码 $LASTEXITCODE）"
}

$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $CertSubject } | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $CertSubject -KeyUsage DigitalSignature `
        -FriendlyName 'WinOcrApi dev cert' -CertStoreLocation 'Cert:\CurrentUser\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
}

if ([string]::IsNullOrWhiteSpace($PfxPassword)) {
    $PfxPassword = ([guid]::NewGuid().ToString('N')).Substring(0, 16)
}

$pfx = Join-Path $OutDir 'WinOcrApi.pfx'
$cer = Join-Path $OutDir 'WinOcrApi.cer'
$secure = ConvertTo-SecureString -String $PfxPassword -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $secure | Out-Null
Export-Certificate -Cert $cert -FilePath $cer -Force | Out-Null
& $signtool sign /fd SHA256 /f $pfx /p $PfxPassword $msix
if ($LASTEXITCODE -ne 0) {
    throw "signtool 签名失败（退出码 $LASTEXITCODE）"
}

$pfxFolder = Split-Path $pfx -Parent
Write-Host ""
Write-Host "已生成：$msix" -ForegroundColor Green
Write-Host "证书：  $cer"
Write-Host "PFX：   $pfx   （本次口令：$PfxPassword，请自己存好，不要提交到仓库）" -ForegroundColor Yellow
Write-Host ""
Write-Host "安装（需要管理员 PowerShell）：" -ForegroundColor Yellow
Write-Host "  Import-Certificate -FilePath '$cer' -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
Write-Host "  Add-AppxPackage '$msix'"
Write-Host ""
Write-Host "装完用 shell:AppsFolder\<PackageFamilyName>!WinOcrApi 启动，再访问 /diag/ai 看 AI 是否放行。"
Write-Host "$pfxFolder 和 *.pfx 都在 .gitignore 里，别手动加进仓库。" -ForegroundColor DarkGray
