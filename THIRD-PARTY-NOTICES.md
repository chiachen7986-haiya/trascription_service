# Third-party software and model notices

BlueJays uses the following open-source components. Their upstream licenses are
included in `licenses/` and copied into published builds. Preserve these notices
when sharing the application internally. No paid STT service or Python installation
is required. Translation providers have their own terms and usage charges.

| Component | Version | License | Source / notice |
| --- | --- | --- | --- |
| Whisper.net | 1.9.1 | MIT | https://github.com/sandrohanea/whisper.net · `licenses/Whisper.net-MIT.txt` |
| Whisper.net.Runtime / Runtime.Metal | 1.9.1 | MIT | Same project; Runtime.Metal is an upstream transitive packaging dependency, not used on Windows |
| Whisper.net.Runtime.Vulkan | 1.9.1 | MIT | Same project; Vulkan-enabled whisper.cpp / ggml. The Vulkan loader and graphics driver are supplied by the operating system/driver installation. |
| whisper.cpp / ggml | Native build supplied by Whisper.net.Runtime 1.9.1 | MIT | https://github.com/ggml-org/whisper.cpp · `licenses/whisper.cpp-MIT.txt` |
| NAudio.Wasapi / NAudio.Core | 2.2.1 | MIT | https://github.com/naudio/NAudio · `licenses/NAudio-MIT.txt` |
| Whisper multilingual tiny model | GGML conversion, `ggml-tiny.bin` | MIT | Original weights https://github.com/openai/whisper; conversion https://huggingface.co/ggerganov/whisper.cpp · `licenses/Whisper-MIT.txt` |
| Whisper multilingual Base / Small Q5_1 models | GGML quantized conversions | MIT | Same Whisper weights/conversion sources and license as above |
| Silero VAD | v5.1.2 GGML conversion | MIT | https://github.com/snakers4/silero-vad; conversion https://huggingface.co/ggml-org/whisper-vad · `licenses/Silero-VAD-MIT.txt` |
| Microsoft.Extensions.AI.Abstractions | 10.2.0 | MIT | Upstream Whisper.net dependency · https://github.com/dotnet/extensions · `licenses/dotnet-extensions-MIT.txt` |
| sherpa-onnx | 2.0.1, patched Windows native build | Apache-2.0 | https://github.com/k2-fsa/sherpa-onnx · `native/README.md`, `licenses/sherpa-onnx-Apache-2.0.txt` |
| GTCRN speech denoising model | `gtcrn_simple.onnx` official sherpa export | MIT | https://github.com/Xiaobin-Rong/gtcrn · `licenses/GTCRN-MIT.txt`; SHA-256 in `models/README.md` |
| ONNX Runtime with OpenVINO | Intel NuGet 1.24.1 | MIT; bundled dependencies have their own notices | `licenses/ONNX-Runtime-MIT.txt`, `licenses/ONNX-Runtime-ThirdPartyNotices.txt` |
| OpenVINO | 2025.4.1.20426, supplied by Intel's pinned runtime | Apache-2.0 and dependency notices | https://github.com/openvinotoolkit/openvino/tree/2025.4.1 · `licenses/OpenVINO-Apache-2.0.txt`, `licenses/OpenVINO-2025.4.1-*.txt` |
| SenseVoice Small weights | INT8 export 2024-07-17 | FunASR Model License | https://huggingface.co/FunAudioLLM/SenseVoiceSmall · `licenses/SenseVoice-FunASR-MODEL-LICENSE.txt` |
| Qwen3-ASR 0.6B weights | INT8 export 2026-03-25 | Apache-2.0 | https://huggingface.co/Qwen/Qwen3-ASR-0.6B · `licenses/Qwen3-ASR-Apache-2.0.txt` |
| LLVM libc++ / libunwind, MinGW runtime | LLVM-MinGW 20261104 UCRT | Apache-2.0 with LLVM exceptions; MinGW notices | `licenses/LLVM-Runtime-LICENSE.txt`, `licenses/MinGW-*.txt` |
| kaldi-native-fbank, kaldi-decoder, kaldifst, OpenFst, simple-sentencepiece | Pinned by sherpa 2.0.1 CMake | Apache-2.0, with additional file notices | Corresponding `licenses/*-src-*.txt`; upstream URLs in the pinned sherpa CMake files and `scripts/Build-SpeechNative.ps1` |
| Eigen | 5.0.1 | MPL-2.0 and component notices | https://gitlab.com/libeigen/eigen/-/tree/5.0.1 · unmodified source available there; `licenses/eigen-src-*.txt` |
| utf8proc, KissFFT, nlohmann/json | Pinned by sherpa 2.0.1 | MIT/Unicode; BSD; MIT respectively | Corresponding `licenses/*-src-*.txt` |

Published self-contained builds also carry the .NET runtime's license and
third-party notices supplied by Microsoft. The .NET SDK is a build tool and is not
distributed by this project. The source project's owner retains its own licensing
decision; this file does not apply an open-source license to BlueJays itself.

The sample `jfk.wav` is a public presidential speech excerpt distributed by
whisper.cpp for recognition testing; it is not part of the published application.

## Model provenance

The package includes Tiny, quantized Base/Small, Silero VAD, SenseVoice and Qwen. See `models/README.md`
for their SHA-256 checksums, sizes and sources. Model weights are excluded from Git by default to keep
the source repository small. If redistributing the runnable folder, include its
`models/` and `licenses/` folders.

The optional Japanese diagnostic clip in `artifacts/test-data/japanese.wav` comes
from https://huggingface.co/spaces/k2-fsa/automatic-speech-recognition/tree/main/test_wavs/sense_voice
and is used only for local verification. It is not included in the published app.
