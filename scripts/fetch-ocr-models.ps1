#Requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$modelDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'Assets\Models\paddle-ocr'
New-Item -ItemType Directory -Path $modelDirectory -Force | Out-Null
$files = @(
    @{ Name = 'det.onnx'; Hash = 'D2A7720D45A54257208B1E13E36A8479894CB74155A5EFE29462512D42F49DA9'; Url = 'https://huggingface.co/SWHL/RapidOCR/resolve/main/PP-OCRv4/ch_PP-OCRv4_det_infer.onnx' },
    @{ Name = 'rec.onnx'; Hash = '48FC40F24F6D2A207A2B1091D3437EB3CC3EB6B676DC3EF9C37384005483683B'; Url = 'https://huggingface.co/SWHL/RapidOCR/resolve/main/PP-OCRv4/ch_PP-OCRv4_rec_infer.onnx' },
    @{ Name = 'keys.txt'; Hash = '28B2362AD4AB2DC38769AA72FEB535E3A9DDB3FD2A7585A05920E6393B1DC7F7'; Url = 'https://raw.githubusercontent.com/PaddlePaddle/PaddleOCR/main/ppocr/utils/ppocr_keys_v1.txt' }
)
foreach ($file in $files) {
    $target = Join-Path $modelDirectory $file.Name
    if ((Test-Path -LiteralPath $target) -and (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -eq $file.Hash) {
        Write-Host "Verified: $($file.Name)"
        continue
    }
    $download = $target + '.download'
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $file.Url -OutFile $download
        if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $file.Hash) {
            throw "Checksum mismatch: $($file.Name). Upstream may have changed; existing model was preserved."
        }
        Move-Item -LiteralPath $download -Destination $target -Force
        Write-Host "Downloaded and verified: $($file.Name)"
    }
    finally {
        if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download -Force }
    }
}
Write-Host 'Paddle OCR models ready. Their Apache-2.0 notices are in licenses/.'
