# Local speech and detection models

This working folder includes multilingual Whisper **Base Q5_1** (balanced default),
**Small Q5_1** (slower accuracy option), **Tiny** (fast fallback), and **Silero VAD
v5.1.2** (neural speech validation). These files are copied to build/publish outputs.
Recognition and speech detection run locally; the configured API receives text.

Source: https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin

License: MIT; see `../licenses/Whisper-MIT.txt`.

SHA-256 of the bundled Tiny file:
`be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21`

| File | Bytes | SHA-256 |
| --- | ---: | --- |
| `ggml-base-q5_1.bin` | 59,707,625 | `422f1ae452ade6f30a004d7e5c6a43195e4433bc370bf23fac9cc591f01a8898` |
| `ggml-small-q5_1.bin` | 190,085,487 | `ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb` |
| `ggml-silero-v5.1.2.bin` | 885,098 | `29940d98d42b91fbd05ce489f3ecf7c72f0a42f027e4875919a28fb4c04ea2cf` |

Whisper conversions: https://huggingface.co/ggerganov/whisper.cpp
Silero conversion: https://huggingface.co/ggml-org/whisper-vad
These Whisper and Silero models are MIT licensed; retain `licenses/Whisper-MIT.txt`,
`licenses/whisper.cpp-MIT.txt` and `licenses/Silero-VAD-MIT.txt`.

To re-download after cloning the source without model weights, run
`scripts\Get-TestAssets.cmd`, or download a Whisper model using
the application's Settings window. Larger models generally improve recognition
but take more CPU time. Test the intended languages and laptop before selecting
a company-wide default.

## SenseVoice and Qwen exports

The microphone denoiser uses `gtcrn.onnx` (official `gtcrn_simple.onnx` export):
https://github.com/k2-fsa/sherpa-onnx/releases/download/speech-enhancement-models/gtcrn_simple.onnx.
SHA-256: `e77603ac0c23dac3227dd2d7135b3a585cbee2679048aecfa886657d3ae1b534`.
GTCRN is MIT licensed; retain `licenses/GTCRN-MIT.txt`.

The app accepts the official sherpa-onnx export folders below. The Settings download
button verifies the complete compressed archive before extracting it into a unique
staging folder. Interrupted downloads never become selectable models.

| Folder | Archive bytes | Archive SHA-256 |
|---|---:|---|
| `sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2024-07-17` | 163002883 | `7d1efa2138a65b0b488df37f8b89e3d91a60676e416f515b952358d83dfd347e` |
| `sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25` | 878702423 | `393f8a14e2f5fb96746aaab342997a40641001fbd5bf9592a080a8329178ee96` |

Source: `https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/<folder>.tar.bz2`.
SenseVoice requires `model.int8.onnx` and `tokens.txt`. Qwen requires
`conv_frontend.onnx`, `encoder.int8.onnx`, `decoder.int8.onnx`, and its complete
`tokenizer` folder. Model weights are excluded from source control; present folders
are copied into builds. Source model cards: https://huggingface.co/FunAudioLLM/SenseVoiceSmall
and https://huggingface.co/Qwen/Qwen3-ASR-0.6B.

SenseVoice weights use the **FunASR Model License**, separately from the Apache-2.0
sherpa code. Qwen weights use **Apache-2.0**. Keep the full files in `licenses/` and
the model folders when sharing internally.
