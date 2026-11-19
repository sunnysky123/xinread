<#
.SYNOPSIS
    构建面向 Microsoft Store 的 x64 + ARM64 多架构 MSIX bundle。

.DESCRIPTION
    依次发布 x64 与 ARM64 的自包含、裁剪后的单架构 .msix，再用 MakeAppx 合成
    一个 .msixbundle。产物不签名——商店在认证通过后会重新签名。

    相关配置见 Properties\PublishProfiles\StoreBundle.pubxml。

.PARAMETER Version
    覆盖包版本，例如 -Version 1.2.3.0。默认取 Package.appxmanifest 里的值。

.PARAMETER NoClean
    保留已有的中间产物，只做增量构建。

.EXAMPLE
    .\pack-store.ps1

.EXAMPLE
    .\pack-store.ps1 -Version 1.0.1.0
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$NoClean
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$publishDir = Join-Path $root 'AppPackages\publish'
$outDir = Join-Path $root 'AppPackages\store'
$stageDir = Join-Path $outDir 'staging'

Push-Location $root
try {
    if (-not $NoClean) {
        Write-Host '清理旧的打包产物…' -ForegroundColor Cyan
        foreach ($p in @($publishDir, $outDir, (Join-Path $root 'AppPackages\*_Test'))) {
            Get-ChildItem -Path $p -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null

    $common = @(
        'publish', '-c', 'Release',
        '-p:PublishProfile=StoreBundle',
        "-p:AppxPackageDir=$publishDir\",
        '-v', 'minimal'
    )
    if ($Version) { $common += "-p:AppxPackageVersion=$Version" }

    # 逐架构发布。每次都会覆盖同名中间目录，因此先把产物挪到暂存目录。
    $built = @()
    foreach ($arch in @(
        @{ Platform = 'x64';   Rid = 'win-x64' },
        @{ Platform = 'ARM64'; Rid = 'win-arm64' }
    )) {
        Write-Host "`n=== 构建 $($arch.Platform) ===" -ForegroundColor Cyan

        & dotnet @common "-p:Platform=$($arch.Platform)" "-p:RuntimeIdentifier=$($arch.Rid)"
        if ($LASTEXITCODE -ne 0) { throw "$($arch.Platform) 发布失败（退出码 $LASTEXITCODE）" }

        $msix = Get-ChildItem -Path $publishDir -Recurse -Filter "*_$($arch.Rid -replace 'win-','').msix" -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -notmatch 'WindowsAppRuntime' } |
                Sort-Object LastWriteTime -Descending |
                Select-Object -First 1

        if (-not $msix) { throw "未找到 $($arch.Platform) 的 .msix 产物" }

        New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
        $target = Join-Path $stageDir $msix.Name
        Copy-Item $msix.FullName $target -Force
        $built += $target

        Write-Host "  $($msix.Name)  $([math]::Round($msix.Length/1MB,2)) MB" -ForegroundColor Green
    }

    # 合成 bundle。MakeAppx 随 Microsoft.Windows.SDK.BuildTools 提供，无需单独安装 SDK。
    $makeappx = Get-ChildItem -Path (Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools') `
                    -Recurse -Filter 'makeappx.exe' -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match '\\x64\\' } |
                Sort-Object FullName -Descending |
                Select-Object -First 1

    if (-not $makeappx) {
        throw '未找到 MakeAppx.exe（应随 Microsoft.Windows.SDK.BuildTools 自动还原）'
    }

    # 文件名带版本号，与包内 Identity/@Version 保持一致
    $bundleName = $null
    if ($Version) {
        $bundleName = "xinread_$($Version)_x64_arm64.msixbundle"
    }
    else {
        # 从生成的清单里读实际版本号，避免文件名与包内版本不一致
        $anyMsix = $built | Select-Object -First 1
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [System.IO.Compression.ZipFile]::OpenRead($anyMsix)
        try {
            $entry = $zip.Entries | Where-Object { $_.Name -eq 'AppxManifest.xml' } | Select-Object -First 1
            $reader = New-Object System.IO.StreamReader($entry.Open())
            $xml = [xml]$reader.ReadToEnd()
            $reader.Dispose()
            $bundleName = "xinread_$($xml.Package.Identity.Version)_x64_arm64.msixbundle"
        }
        finally { $zip.Dispose() }
    }

    $bundlePath = Join-Path $outDir $bundleName
    Write-Host "`n=== 合成 bundle ===" -ForegroundColor Cyan

    & $makeappx.FullName bundle /d $stageDir /p $bundlePath /o
    if ($LASTEXITCODE -ne 0) { throw "bundle 合成失败（退出码 $LASTEXITCODE）" }

    Write-Host "`n完成：" -ForegroundColor Green
    Get-Item $bundlePath | ForEach-Object {
        Write-Host ("  {0}  {1} MB" -f $_.FullName, [math]::Round($_.Length / 1MB, 2))
    }
    Write-Host "`n该 bundle 未签名，可直接上传到 Microsoft Store。" -ForegroundColor Yellow
}
finally {
    Pop-Location
}
