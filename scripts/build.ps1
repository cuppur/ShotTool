#Requires -Version 5.1
<#
.SYNOPSIS
    构建 ShotTool（只编译，不发布）。

.DESCRIPTION
    产物：src\ShotTool\bin\<Configuration>\net8.0-windows10.0.19041.0\ShotTool.exe
    发布单文件请用 scripts\publish.ps1。

.PARAMETER Configuration
    Debug 或 Release（默认 Release）。

.PARAMETER NoRestore
    跳过 dotnet restore（CI 里已还原过时用）。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\build.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\ShotTool\ShotTool.csproj'

if (-not (Test-Path -LiteralPath $project)) {
    throw "找不到项目文件：$project"
}

Push-Location $root
try {
    if (-not $NoRestore) {
        Write-Host '==> dotnet restore'
        dotnet restore $project
        if ($LASTEXITCODE -ne 0) {
            throw "restore 失败（exit $LASTEXITCODE）"
        }
    }

    Write-Host "==> dotnet build -c $Configuration"
    $buildArgs = @('build', $project, '-c', $Configuration)
    if ($NoRestore) {
        $buildArgs += '--no-restore'
    }

    dotnet @buildArgs
    if ($LASTEXITCODE -ne 0) {
        throw "构建失败（exit $LASTEXITCODE）"
    }

    $exe = Join-Path $root "src\ShotTool\bin\$Configuration\net8.0-windows10.0.19041.0\ShotTool.exe"
    Write-Host ''
    Write-Host "构建成功：$exe" -ForegroundColor Green
}
finally {
    Pop-Location
}
