#Requires -Version 5.1
<#
.SYNOPSIS
    发布 ShotTool 单文件版本到 dist\（双击即用，模型随包）。

.DESCRIPTION
    形态：exe 单文件（自包含，目标机无需 .NET）+ 旁边 models\ 目录（NLLB 权重）。
    模型**绝不进单文件 exe**（1.07 GiB 每次启动解压是灾难级体验），只复制到 dist\models\。

    产出：
      dist\ShotTool-<Version>-<Runtime>\
      ├── ShotTool.exe
      ├── README.txt
      └── models\nllb-200-distilled-600M-int8\   （源存在时）

    分发边界校验：
      * dist\models\ 下的 onnx 合法（随包模型）；
      * 其它任何位置出现 onnx/xnet/ort 一律失败；
      * dist 出现 Assets\ 目录失败（图标由代码绘制，不应复制）；
      * exe 超过 400 MB 失败（说明模型被误打进单文件）。

.PARAMETER Version
    版本号，默认 0.1.0。

.PARAMETER Runtime
    目标 RID，目前只支持 win-x64。

.PARAMETER FrameworkDependent
    框架依赖发布（体积小，目标机需 .NET 8 Desktop Runtime）。

.PARAMETER NoRestore
    跳过 restore（注意：必须已有 win-x64 RID 资产，否则 NETSDK1047）。

.PARAMETER ModelSource
    NLLB 模型源目录；默认 %LOCALAPPDATA%\ShotTool\models\nllb-200-distilled-600M-int8。
    源不存在时只警告、跳过复制，不失败。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\publish.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -ModelSource D:\nllb-int8
#>
[CmdletBinding()]
param(
    [string]$Version = '0.5.0',

    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',

    [switch]$FrameworkDependent,

    [switch]$NoRestore,

    [string]$ModelSource,

    [switch]$IncludeTranslationModels,

    [switch]$LinkTranslationModels
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\ShotTool\ShotTool.csproj'
$distRoot = Join-Path $root 'dist'
$outDir = Join-Path $distRoot "ShotTool-$Version-$Runtime"

if (-not (Test-Path -LiteralPath $project)) {
    throw "找不到项目文件：$project"
}

# 安全检查：只允许清理 dist 下的本版本目录。
$distFull = [System.IO.Path]::GetFullPath($distRoot)
$outFull = [System.IO.Path]::GetFullPath($outDir)
if (-not $outFull.StartsWith($distFull + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "拒绝清理非 dist 路径：$outFull"
}

if (Test-Path -LiteralPath $outFull) {
    Write-Host "==> 清理旧产物 $outFull"
    Remove-Item -LiteralPath $outFull -Recurse -Force
}

New-Item -ItemType Directory -Path $outFull -Force | Out-Null

$publishArgs = @(
    'publish', $project,
    '-c', 'Release',
    '-r', $Runtime,
    '-o', $outFull,
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:DebugType=none',
    '-p:EnableCompressionInSingleFile=false',
    "-p:Version=$Version"
)

if ($FrameworkDependent) {
    $publishArgs += '-p:SelfContained=false'
}
else {
    $publishArgs += '-p:SelfContained=true'
}

if ($NoRestore) {
    $publishArgs += '--no-restore'
}

Push-Location $root
try {
    Write-Host "==> dotnet publish（$Runtime, self-contained=$(-not $FrameworkDependent)）"
    dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "发布失败（exit $LASTEXITCODE）"
    }
}
finally {
    Pop-Location
}

# ---- 单文件清洁：清理链接库（*.lib）----
# ONNX Runtime 的 NuGet 包会把 onnxruntime.lib 作为内容文件复制到发布目录；
# 它是编译期链接库，运行期不需要，单文件发布不应携带。
$libFiles = @(Get-ChildItem -LiteralPath $outFull -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like '*.lib' })
if ($libFiles.Count -gt 0) {
    foreach ($lib in $libFiles) {
        Remove-Item -LiteralPath $lib.FullName -Force
    }
    Write-Host "==> 已清理 $($libFiles.Count) 个链接库（*.lib，运行期不需要）"
}

# ---- 随 dist 复制 NLLB 模型（exe 之外；源缺失只警告）----
$modelDirName = 'nllb-200-distilled-600M-int8'
if ([string]::IsNullOrWhiteSpace($ModelSource)) {
    $ModelSource = Join-Path $env:LOCALAPPDATA "ShotTool\models\$modelDirName"
}
$modelTarget = Join-Path $outFull "models\$modelDirName"

if ($IncludeTranslationModels -and (Test-Path -LiteralPath $ModelSource)) {
    $requiredFiles = @('encoder_model_int8.onnx', 'decoder_model_int8.onnx', 'tokenizer.json', 'config.json')
    $missing = @($requiredFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $ModelSource $_)) })
    if ($missing.Count -gt 0) {
        Write-Warning ("模型源目录缺少文件：" + ($missing -join ', ') + "（仍复制现有文件，翻译可能不可用）")
    }

    New-Item -ItemType Directory -Path $modelTarget -Force | Out-Null
    if ($LinkTranslationModels) {
        # 本地迭代优先硬链接，避免每个修订再占用一份 1.1 GiB 权重；跨卷则正常复制。
        foreach ($file in Get-ChildItem -LiteralPath $ModelSource -Recurse -File) {
            $relative = $file.FullName.Substring($ModelSource.TrimEnd('\').Length).TrimStart('\')
            $target = Join-Path $modelTarget $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            try { New-Item -ItemType HardLink -Path $target -Target $file.FullName -ErrorAction Stop | Out-Null }
            catch { Copy-Item -LiteralPath $file.FullName -Destination $target -Force }
        }
    }
    else {
        Copy-Item -Path (Join-Path $ModelSource '*') -Destination $modelTarget -Recurse -Force
    }

    $modelBytes = (Get-ChildItem -LiteralPath $modelTarget -Recurse -File | Measure-Object -Property Length -Sum).Sum
    Write-Host ("==> 已随包复制模型：models\$modelDirName（" + [math]::Round($modelBytes / 1MB, 1) + " MiB）")
}
else {
    Write-Host '默认发布截图/OCR 版，不复制翻译模型；需要时添加 -IncludeTranslationModels。'
}

# ---- 产物与分发边界校验 ----
$exe = Join-Path $outFull 'ShotTool.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw "发布目录里没有 ShotTool.exe：$outFull"
}

$exeInfo = Get-Item -LiteralPath $exe
$exeMiB = [math]::Round($exeInfo.Length / 1MB, 2)
# 体积区间断言（architect 建议）：PaddleOCR 以 EmbeddedResource 内嵌进程序集。
# 实测：Paddle 已内嵌后 214.48 MiB（未落地时 199.57）；区间 195–225 同时兜住两种情况。
if ((-not $FrameworkDependent -and $exeMiB -lt 195) -or $exeMiB -gt 225) {
    throw ("exe 体积异常（$exeMiB MiB）：期望 195–225 MiB（含内嵌 PaddleOCR）。" +
        "低于 195 说明 Paddle 内嵌未生效（或依赖被裁掉）；" +
        "高于 225 说明有模型被误内嵌（例如把 NLLB 放进 Assets\Models\paddle-ocr\）。")
}

# PaddleOCR 是 EmbeddedResource（进 exe），dist 里不该出现它的目录。
# 这条比下面的 "dist 出现 Assets\" 更具体，便于定位是谁把规则改成了 PreserveNewest。
if (Test-Path -LiteralPath (Join-Path $outFull 'Assets\Models\paddle-ocr')) {
    throw "dist 出现 Assets\Models\paddle-ocr：PaddleOCR 应为 EmbeddedResource 内嵌进 exe，不应复制到 dist。"
}

# 允许：dist\models\ 下的模型文件（随包）。禁止：其它位置的模型文件。
$modelsRoot = Join-Path $outFull 'models'
$modelPatterns = @('*.onnx', '*.onnx.data', '*.xnet', '*.ort')
$allFiles = @(Get-ChildItem -LiteralPath $outFull -Recurse -File -ErrorAction SilentlyContinue)

$modelLike = @(
    $allFiles | Where-Object {
        $name = $_.Name
        @($modelPatterns | Where-Object { $name -like $_ }).Count -gt 0
    }
)
$strayModels = @(
    $modelLike | Where-Object {
        -not $_.FullName.StartsWith($modelsRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)
    }
)
if ($strayModels.Count -gt 0) {
    $list = ($strayModels | ForEach-Object { $_.FullName }) -join [Environment]::NewLine
    throw "模型文件出现在 models\ 之外，违反分发边界：$([Environment]::NewLine)$list"
}

# dist 不应有 Assets\：图标由 ToolbarIcons 代码绘制，复制进 dist 说明打包配置回归。
if (Test-Path -LiteralPath (Join-Path $outFull 'Assets')) {
    throw "dist 出现 Assets\ 目录：图标应为代码内嵌（ToolbarIcons），不应复制进 dist。"
}

# 随包模型的完整性（存在时校验两个 onnx 都有合理体积；缺文件只警告不失败）
if (Test-Path -LiteralPath $modelsRoot) {
    foreach ($required in @('encoder_model_int8.onnx', 'decoder_model_int8.onnx')) {
        $path = Join-Path (Join-Path $modelsRoot $modelDirName) $required
        if (-not (Test-Path -LiteralPath $path)) {
            Write-Warning "随包模型缺少 $required（翻译将不可用）"
        }
        elseif ((Get-Item -LiteralPath $path).Length -lt 50MB) {
            Write-Warning "随包模型 $required 体积异常（$([math]::Round((Get-Item -LiteralPath $path).Length / 1MB, 1)) MiB），请核对文件。"
        }
    }
}

# ---- 随包说明 ----
$translationInfo = if (Test-Path -LiteralPath $modelTarget) {
    '本包附带离线翻译模型。其它电脑首次使用请在设置中选择 NLLB 本地模型；模型只在首次翻译时加载。'
} else {
    '本包不含翻译模型。翻译入口会给出配置说明；可自行准备模型或使用本机 Ollama。'
}
$readme = @"
ShotTool v$Version — 独立 Windows 截图工具

双击 ShotTool.exe 后托盘常驻。Alt+A 截图，拖拽框选后点工具标注。
Enter / Ctrl+C 复制，Ctrl+S 保存，Esc / 右键取消。
框选后点图钉或按 F3 固定截图。贴图可拖动、滚轮缩放，双击恢复大小，Esc 关闭。
Ctrl+Z 撤销，Ctrl+Y 重做，Ctrl+1..6 切工具，Tab 切换颜色与线宽/字号。
再次点击已选工具可退出绘图并移动选区；拖八个控制点调整大小。
工具条「提取文字」使用本地 OCR。$translationInfo

系统：Windows 10 2004+ / Windows 11，x64。无需安装 .NET。
默认保存：%USERPROFILE%\Pictures\ShotTool
设置：%LOCALAPPDATA%\ShotTool\settings.json

本程序参考本机微信 4.1.15.13 交互，自研实现，不依赖微信进程或账号。
未携带腾讯组件。models 目录若存在，应与程序一同保留。
第三方组件和模型声明见 licenses 目录。

已验证构建与轻量整合自检；实际 UI 自动化画面读取超时，尚未完成真人体验验收。
限制：窗口吸附、长截图、贴图、自动打码尚未实现，多屏/HDR/受保护画面待验证。
本版未签名。
"@

$readmePath = Join-Path $outFull 'README.txt'
$readme | Set-Content -LiteralPath $readmePath -Encoding UTF8

# ---- dist 顶层白名单（architect 建议）----
# 单文件发布只应有：ShotTool.exe + README.txt（models\ 是目录）。
# 任何 native 包再带垃圾进 dist（历史上 onnxruntime*.lib 就是这么进来的）都会被立刻抓到。
$topFiles = @(Get-ChildItem -LiteralPath $outFull -File -ErrorAction SilentlyContinue)
$extraTop = @($topFiles | Where-Object { $_.Name -ne 'ShotTool.exe' -and $_.Name -ne 'README.txt' })
if ($extraTop.Count -gt 0) {
    $list = ($extraTop | ForEach-Object { $_.Name + ' (' + $_.Length + ' B)' }) -join ', '
    throw "dist 顶层多出文件（单文件发布只应有 ShotTool.exe + README.txt）：$list"
}

Copy-Item -LiteralPath (Join-Path $root 'licenses') -Destination $outFull -Recurse -Force

# ---- 报告 ----
$totalBytes = (Get-ChildItem -LiteralPath $outFull -Recurse -File | Measure-Object -Property Length -Sum).Sum
$fileCount = @(Get-ChildItem -LiteralPath $outFull -Recurse -File).Count

Write-Host ''
Write-Host "发布成功：$exe（$exeMiB MiB）" -ForegroundColor Green
Write-Host ("dist 总大小：" + [math]::Round($totalBytes / 1MB, 1) + " MiB，文件数：$fileCount")
Write-Host "说明文件：$readmePath"
Write-Host '把整个 dist 目录拷给用户即可，双击 ShotTool.exe 运行。'
