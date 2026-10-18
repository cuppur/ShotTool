# 可选模型

模型权重不提交到 Git。截图与系统 OCR 可以在没有额外权重时构建和运行。

## Paddle OCR

执行 `scripts/fetch-ocr-models.ps1` 获取并校验约 15 MB 的 PP-OCRv4 文件，放在本目录的 `paddle-ocr/` 中：

- `det.onnx`
- `rec.onnx`
- `keys.txt`

构建时这些文件会作为资源内嵌。下载来源、固定 SHA-256 在脚本内；相关 Apache-2.0 声明在 `licenses/`。

## NLLB 翻译

大模型不嵌入 exe。可在程序旁的 `models/nllb-200-distilled-600M-int8/` 放置编码器、解码器、分词器及配置，详见 `docs/usage.md`。它的上游模型许可独立于代码许可，使用前需核对。
