param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '未找到 dotnet。请安装 .NET 10 SDK。'
}

# Publish into a clean, verified build-output directory. dotnet publish does not
# remove obsolete dependencies from an existing output directory by itself.
$publishRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts/publish'))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
if (-not $publishRoot.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) { throw '发布目录不在 artifacts 内。' }
if (Test-Path -LiteralPath $publishRoot) {
    $publishItem = Get-Item -LiteralPath $publishRoot
    if (($publishItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        @(Get-ChildItem -LiteralPath $publishRoot -Recurse -Force -Attributes ReparsePoint).Count -gt 0) {
        throw '发布目录包含链接，拒绝清理。'
    }
    Remove-Item -LiteralPath $publishRoot -Recurse -Force
}

dotnet publish .\OfficeTouchBar.csproj `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -o .\artifacts\publish `
    -p:PublishSingleFile=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败：$LASTEXITCODE" }

$payloadFiles = @(Get-ChildItem -LiteralPath $publishRoot -File -Recurse | ForEach-Object {
    [IO.Path]::GetRelativePath($publishRoot, $_.FullName)
} | Sort-Object)
if ($payloadFiles -notcontains 'OfficeTouchBar.exe' -or $payloadFiles -notcontains 'OfficeTouchBar.dll') {
    throw '发布文件不完整，拒绝打包。'
}
$payloadFiles | Set-Content -LiteralPath (Join-Path $artifactsRoot 'publish-files.txt') -Encoding UTF8

$iscc = Join-Path $PSScriptRoot 'Inno Setup 6\ISCC.exe'
if (-not (Test-Path -LiteralPath $iscc)) {
    $command = Get-Command iscc -ErrorAction SilentlyContinue
    if ($null -eq $command) { throw '未找到 Inno Setup 编译器，未生成安装包。' }
    $iscc = $command.Source
}

& $iscc .\Setup\OfficeTouchBar.iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup 编译失败：$LASTEXITCODE" }
Write-Host '安装包已生成到 artifacts 目录。'
