# Local STT comparison · 2026-11-05

Measured on this Windows 11 laptop: i5-1235U (2 performance / 8 efficient cores, 12 threads), Iris Xe 80-EU GPU, approximately 16 GB RAM. No API, context correction, live microphone or playback was used. Application preferences and the published application were not changed.

## Japanese read-speech comparison

First ten FLEURS Japanese test rows: **110.04 seconds, 443 normalized reference characters**. Same clips and references for every model. Each clip has three warm timed decodes after a first decode; per-clip warm medians are used. Corpus CER is total edit errors / total reference characters. This small sample is a screening test, not a meeting-accuracy benchmark.

| Configuration | Japanese CER | Warm compute / audio (RTF) | Mean warm decode per clip | 7.2 s Japanese demo | Peak process RAM (FLEURS) |
| --- | ---: | ---: | ---: | ---: | ---: |
| SenseVoice Small INT8 · CPU | 9.9% (44/443) | 0.060 | 0.66 s | 0.27 s | 382 MiB |
| Qwen3-ASR 0.6B INT8 · CPU | 9.0% (40/443) | 0.406 | 4.47 s | 2.75 s | 2087 MiB |
| Whisper Small Q5_1 · Vulkan, fixed language | 14.7% (65/443) | 0.123 | 1.35 s | 1.35 s | 926 MiB |
| Whisper Small Q5_1 · Vulkan + Tiny routing | 14.7% (65/443) | 0.250 | 2.75 s | 3.09 s | 1255 MiB |
| Whisper Base Q5_1 · Vulkan, fixed language | 24.4% (108/443) | 0.051 | 0.56 s | 0.73 s | 778 MiB |

RTF below 1 means faster than real time for one full-file decode. Repeated previews and concurrent sources add work. Process RAM excludes a reliable accounting of shared GPU allocations, so it is not a total-memory comparison. Fixed-language Whisper has prior language knowledge; SenseVoice/Qwen detect language internally, while routed Whisper includes Tiny classification among English/Japanese.

## English and noise checks

WER on the 11-second JFK excerpt, its deterministic 10 dB white-noise variant, and one 19.02-second noisy radio dialogue. These are three conditions from two recordings, not a broad English benchmark.

Whisper's radio output includes literal BEEP event markers, counted as word insertions by the stated tokenizer. The score therefore includes sound-event captions as well as spoken-word errors.

| Configuration | JFK WER | JFK + noise WER | Noisy radio WER | Radio warm decode |
| --- | ---: | ---: | ---: | ---: |
| SenseVoice Small INT8 · CPU | 9.1% | 13.6% | 51.6% | 0.78 s |
| Qwen3-ASR 0.6B INT8 · CPU | 0.0% | 4.5% | 12.9% | 6.64 s |
| Whisper Small Q5_1 · Vulkan, fixed language | 0.0% | 0.0% | 25.8% | 1.82 s |
| Whisper Small Q5_1 · Vulkan + Tiny routing | 0.0% | 0.0% | 25.8% | 3.24 s |
| Whisper Base Q5_1 · Vulkan, fixed language | 0.0% | 0.0% | 54.8% | 0.76 s |

Forcing SenseVoice to **English** improved the radio WER to 45.2%, still behind Whisper Small and Qwen on this recording. Forcing **Japanese** gave CER 10.2% (45/443) on FLEURS. Language forcing alone did not remove the observed English tradeoff.

The Japanese demo's correct reference spelling 弁当制 was produced by SenseVoice. Qwen used that spelling too, but omitted a character elsewhere. The full raw hypotheses and errors are retained in the JSON results. No glossary, context prompt or API correction was applied.

## CPU thread-count check

Separate short runs on the same 7.2-second Japanese demo. These tune one clip only and do not replace the six-thread full-set comparison above.

| Threads | SenseVoice warm median | Qwen warm median |
| ---: | ---: | ---: |
| 1 | 0.502 s | 3.181 s |
| 2 | 0.349 s | 2.272 s |
| 4 | 0.295 s | 3.003 s |
| 6 | 0.271 s | 3.518 s |

A full ten-clip confirmation with **two Qwen threads** took 47.35 s of warm compute for 110.04 s of audio (RTF **0.430**), with CER 9.0% (40/443). The short-clip thread-count improvement did not establish a universal faster configuration. Measurements were sequential rather than randomized/interleaved; OS scheduling, power and thermals were not controlled.

## Hardware acceleration

Whisper measurements above used actual Vulkan offload. The official sherpa-onnx 2.0.1 Windows runtime used for SenseVoice and Qwen exposes only CPUExecutionProvider. A real request for OpenVINO/GPU logged explicit CPU fallback; it was excluded from GPU timing comparisons. Intel's separate OpenVINO plugin 1.8.1, with ONNX Runtime 1.31.1, successfully enumerated this laptop's Iris Xe GPU and CPU, with no NPU device exposed. That establishes device availability, not accelerated transcription for either ONNX export. End-to-end SenseVoice/Qwen GPU latency remains unmeasured. INT8 precision alone does not enable Vulkan, OpenVINO or DirectML.

## Interpretation

SenseVoice is the strongest low-latency CPU candidate in these tests, particularly for Japanese-focused use. Qwen's Japanese CER advantage over SenseVoice is only four edits on this sample, while its English radio result is substantially better. Qwen needs more decode time and memory; the current repeated-preview strategy still needs throughput validation before adopting it. Retain an English-capable fallback if testing SenseVoice in mixed meetings. These results do not establish accuracy for company vocabulary, overlapping speakers, distant microphones or hour-long meetings.

SenseVoice's model weights use the separate custom FunASR model license; Qwen3-ASR weights are Apache-2.0. Runtime/source-code licenses do not automatically cover model weights. Model/dataset sources, hashes, methods, attribution, commands and hardware-probe limitations are documented in [README.md](README.md).

Raw per-clip JSON and the flattened metrics CSV are in `artifacts/model-comparison/`. No new model has been installed into the app's model selector.
