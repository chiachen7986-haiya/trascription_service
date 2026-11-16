# Windows speech runtime

`win-x64/` contains the native speech bridge and the two LLVM runtime DLLs it needs.
Normal `scripts/Build.cmd` builds use these files; users do not need a C++ compiler,
Python, an OpenVINO installation, or CUDA.

The isolated `BlueJays.SpeechHost` process uses sherpa-onnx **2.0.1**, rebuilt against
Intel's **Intel.ML.OnnxRuntime.OpenVino 1.24.1** package. The ordinary sherpa NuGet
native runtime is CPU-only and is explicitly excluded from this host. Preserve the
entire `speech-host` folder when distributing the app. Do not mix ORT DLL versions.

## Supported execution

| Engine | ONNX CPU | OpenVINO CPU | OpenVINO Intel GPU |
|---|---|---|---|
| SenseVoice Small INT8 | Yes; fastest tested choice here | Yes | Yes, FP32 activations |
| Qwen3-ASR 0.6B INT8 | Yes | Audio encoder; CPU frontend and decoder | Not offered in this build |

Qwen's whole-graph OpenVINO decoder returned empty text during validation. Keeping
its autoregressive KV-cache loop on ONNX Runtime CPU fixes this. Its GPU encoder
also failed native inference on the tested i5-1235U / Iris Xe laptop, so the UI
offers verified CPU choices. Saved Qwen GPU selections explicitly fall back to
OpenVINO CPU when fallback is allowed; strict settings report an actionable error.
This is a backend validation limitation, not a conclusion that the laptop lacks GPU
acceleration: SenseVoice OpenVINO GPU and Whisper Vulkan both work.

SenseVoice's default FP16 GPU path returned empty speech. FP32 activations produced
valid Japanese and English results. INT8 model weights are retained. CPU remains
the default for the new engines because regular CPU SenseVoice was faster here.

The native session patch refuses silent provider-initialization fallback and
exports an initialization counter. Real ORT profiles in
`artifacts/openvino-integration/profiles/` confirm OpenVINO kernels executed.
GPU sessions can also use CPU kernels. Optional profiling is enabled only through
`BLUEJAYS_STT_PROFILE_PREFIX`; normal sessions do not generate trace files.

## Rebuild

From an approved PowerShell developer environment, run
`scripts/Build-SpeechNative.ps1`, then `scripts/Build.cmd`. The rebuild script pins
and SHA-256 verifies sherpa source, Intel's runtime SDK, LLVM-MinGW, CMake, and
Ninja. It downloads them into `artifacts/openvino-integration`, installs nothing
system-wide, and builds with four compiler workers. It respects the environment's
PowerShell execution policy.

`scripts/native/sherpa-openvino.patch` contains the session changes. The rebuild
script also applies the CPU Qwen frontend/decoder routing and small portability
repairs: portable stderr buffering, a missing standard header, an upstream file
pointer typo, modern `invoke_result`, sized deallocation/math constants, and
removal of MSVC-only warning switches for LLVM-MinGW. No Eigen library implementation
files are modified; the compiler-switch cleanup also covers its build files.
Upstream dependency downloads are pinned by sherpa's CMake manifests.

`SHERPA_ONNX_ENABLE_GPU=ON` selects the shared runtime import path in the upstream
build; its generic CUDA warning does not mean this package requires CUDA. The host
selects only CPU or OpenVINO providers.

Runtime and model licenses are retained in `licenses/` and in published packages.
`SHA256SUMS.txt` records the staged native DLL checksums. The speech host manifest
opts into UTF-8 Windows code-page handling for Japanese model/user paths.
