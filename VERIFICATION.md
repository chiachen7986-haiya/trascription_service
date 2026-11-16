# Speech quality, latency and display verification · 2026-11-05

Environment: Windows 11 x64 (22631), .NET SDK 10.0.403, i5-1235U CPU,
Whisper.net 1.9.1, quantized multilingual Base/Small and Silero VAD v5.1.2.

## Alternative INT8 STT evaluation · 2026-11-05

- Isolated benchmark projects were added outside the application solution; the
  app's engine/settings and published package remain unchanged. Detailed results,
  methods and commands: `tests/BlueJays.ModelBench/RESULTS.md` and `README.md`.
- Official sherpa-onnx 2.0.1 exports of SenseVoice Small INT8 and Qwen3-ASR 0.6B
  INT8 ran locally with ONNX Runtime 1.28.2 on this i5-1235U / Iris Xe / 16 GB laptop.
  Archive SHA-256 values match the publisher's metadata. The pinned FLEURS Japanese
  shard was also SHA-256 checked; its first ten test rows were selected before
  examining recognition results. All audio was public, with no capture/playback/API.
- Ten FLEURS recordings: 110.04 seconds, 443 normalized reference characters.
  CER: SenseVoice **44/443 (9.9%)**, Qwen **40/443 (9.0%)**, Small Q5_1 **65/443
  (14.7%)**, Base Q5_1 **108/443 (24.4%)**. These are screening results, not general
  meeting-accuracy estimates. Kanji/kana and numeral-spelling differences count.
- Sum of per-clip warm median compute / audio: SenseVoice CPU **0.060 RTF**,
  Qwen CPU **0.406**, fixed-Japanese Small/Vulkan **0.123**, English/Japanese-routed
  Small/Vulkan **0.250**, fixed-Japanese Base/Vulkan **0.051**. Each clip has one
  first pass plus three warm passes. Loading, buffering and translation are excluded.
- English noise tradeoff was explicit: on one 19.02-second radio dialogue, WER was
  SenseVoice **51.6%** (automatic) / **45.2%** (forced English), Qwen **12.9%**,
  and Small/Vulkan **25.8%**. A single example does not establish English ranking.
  Two Japanese demos and controlled white-noise variants are additional diagnostics.
- A CPU thread-count sweep and full two-thread Qwen confirmation were run. Scoring
  self-checks verify normalization, Japanese homophone substitution, edit distance
  and English tokenization. Raw JSON and flattened CSV retain per-clip evidence.
- Intel's official OpenVINO plugin 1.8.1 / ONNX Runtime 1.31.1 successfully exposed
  CPU and Iris Xe GPU devices, with no NPU device. The stock sherpa Windows build
  explicitly reported only CPUExecutionProvider and CPU fallback when OpenVINO was
  requested. No end-to-end SenseVoice/Qwen GPU acceleration or GPU latency is claimed.
- Both benchmark projects build with zero warnings/errors. No models were added to
  the application's model selector. Model license metadata is recorded separately
  from the runtime licenses, including SenseVoice's custom FunASR model license.

## Vulkan and routed language workers · 2026-11-05

- **54/54 regression checks** and **55/55 including off-screen UI rendering**
  pass. The four localized settings layouts and narrow-window latency status
  render without binding/localization errors; Japanese layouts were visually
  inspected. No desktop windows were shown and no desktop input was sent.
- Release build/publish completed with zero warnings/errors. The launcher selects
  `artifacts/win-x64-20261105-022350-660/BlueJays.exe`. Published App/Core DLL hashes
  match the tested build; model hashes, runtime/license files and CPU/Vulkan native
  libraries were checked. The published GPU binary matches the pinned NuGet asset.
  A brief actual saved-configuration startup check reached Listening on Realtek,
  reported the disconnected Airports endpoint and made no API calls or audio exports.
- Confirmed actual offload on this machine, not just native-library selection:
  the native runtime found **Intel Iris Xe Graphics**, allocated model weights in
  `Vulkan0`, and initialized the `Vulkan0` inference backend. The installed Vulkan
  loader is 1.4.304.0. No driver/SDK installation or saved-setting changes were needed.
- Added the pinned official Whisper.net.Runtime.Vulkan 1.9.1 package and retained
  CPU fallback. GPU enable/disable and optional concurrent decoding are saved in
  settings. Older settings enable Vulkan but preserve the user's selected model.
- Two independent contexts serve fixed-language workers for English/Japanese.
  Tiny performs CPU language classification among configured languages, cached
  per utterance; strong routes are rechecked after six new seconds, weak routes
  after two. One spoken language bypasses detection. More spoken languages share
  the two slots. Source-local audio remains ordered; parallelism is between sources.
- A three-second Japanese sentence ending was misclassified as English in the
  real diagnostic. Detection now keeps up to eight seconds of acoustic context;
  the complete routed Japanese/English check passes with the configured languages.
  This does not guarantee classification of rapid switches within a clause.
- Warmed direct Small Q5_1 Vulkan decoding took **1.47 s** for 7.2 s of Japanese
  and **0.99 s** for 11 s of English. Three repeated paired runs took **2.14 s**
  median sequentially versus **2.10 s** concurrently. The small difference does
  not justify forcing concurrent GPU use on this integrated GPU. Both modes work;
  default GPU admission is one decode, with two independent language workers.
- With real Tiny classification included for each new utterance, three warmed
  Japanese/English pair runs took **4.31 / 4.07 / 4.22 s**. Both languages were
  correctly routed and transcribed. The first pair cost 15.65 s, including initial
  GPU initialization. The app now warms with synthetic silence before capture.
- A **30-second wall-clock dual-source run** through the actual session, buffer,
  routing, STT and clause assembly produced **nine finalized captions** with no
  errors. Small/Vulkan, default sequential GPU admission: **4.67 s maximum lag**,
  **4.61 s p95**, **3.77 s last lag**, **3.96 s finish**. Startup warmup took 10.10 s.
  Inputs were repeated public Japanese/English files, with no audio playback,
  device capture, export or translation API. These are not real meeting accuracy
  scores, API-inclusive latency or guarantees under Teams/video workload.
- A longer **120-second** default-mode run finalized **34 captions**, with no
  errors or unfinished captions: **4.87 s maximum lag**, **4.26 s p95**, **2.41 s
  last lag**, **3.96 s finish**. A separate **30-second concurrent-GPU run** also
  finalized all nine captions: **3.98 s maximum**, **3.82 s p95**, no errors.
  Both use the real production session and wall-clock public-file inputs. They
  demonstrate bounded delay over these runs, not an hour-long Teams meeting.
- Stale optional previews are suppressed under backlog; final audio stays queued.
  Adaptive preview intervals reserve headroom instead of capping at four seconds.
  Status exposes backend and approximate capture-end-to-decode delay, with an
  overload notice at 15 seconds. The queues still have finite capacity and stop
  visibly on overflow; this is not a guarantee of real-time operation on every GPU.
- Routing/queue checks cover per-source ordering, independent-source progress,
  final-audio retention, completion/cancellation, cached detection and language
  changes, configured-language restriction, weak evidence, two concurrent GPU
  lanes, CPU/shared-GPU admission and partial-startup native-resource cleanup.
  Synthetic-silence warmup never enters conversation history.
- Japanese lexical errors remain possible: Small still produced 弁当性 on this
  sample. GPU acceleration improves throughput; context correction is separately
  labeled and cannot prove what was spoken. Evaluate representative company audio.

## Audio-device startup fix · 2026-11-04

- Final results: **45/45** regression checks, **50/50** including real speech/audio
  integration, and **46/46** with off-screen UI rendering. Release build has zero
  warnings/errors. The launcher selects the new complete package; published App/Core
  DLL hashes match the tested build and required runtime/model/license files exist.
- Reproduced the exact reported `0x88890004` in the original capture constructor:
  Windows reported the saved Airports Pro 2 render endpoint as **Unplugged**, and
  WASAPI activation failed. The saved Realtek output and built-in microphone were
  both **Active** and could open. This was an endpoint failure, not model loading.
- The fixed full session startup was tested with the actual saved model and source
  configuration: two sources reached Listening and the unplugged source was reported
  unavailable. This brief check created no audio/transcript files and called no API.
- Startup now checks endpoint state/type, retries transient invalidation once with
  fresh handles for the same endpoint, and retains healthy selected sources. A final
  check detects endpoints invalidated while another source opens (e.g. Bluetooth
  profile changes). It never switches to an unselected device. All-unavailable
  startup produces a named audio-source error rather than a generic runtime hint.
- Regression coverage includes unplugged-source isolation, bounded retry and cleanup,
  partial-start cancellation/rollback, disabled sources, access-denied diagnosis,
  late invalidation, and preservation of source identity/name on device replacement.
- New source status/count indicators and a localized Choose device dialog make
  recovery possible while preserving caption labels/history. Layouts are rendered
  off-screen; no desktop windows are shown or desktop input sent.

## Translation cutoff fix · 2026-11-04

- At the time of the translation fix, **37/37** regression checks passed; **38/38** with off-screen UI rendering.
- Reproduced the reported DeepSeek configuration (thinking-disable parameter off)
  using a synthetic Japanese sentence and the saved provider/key. The real stream
  emitted reasoning; the client recovered in non-thinking mode and returned an
  English translation in **1.46 seconds**, including recovery. No meeting content
  was sent, no credentials were printed, and no saved settings were changed.
- The synthetic result was: “Tomorrow's meeting is at 3 PM. Tanaka-san will prepare
  the materials.” This is a single connectivity/recovery check, not a latency guarantee.
- New simulated SSE checks cover the CJK/JSON output allowance, reasoning recovery
  remembered across clauses, retained final-chunk content, retrying missing languages
  with the accepted correction, complete objects with a truncated JSON terminator,
  bounded repeat failures, generic-provider compatibility, and cancellation.
- Release built with zero warnings/errors and published separately while the
  previous app remained open. The launcher selects only complete marked builds.
  Published App/Core hashes match the Release build; runtime/model/license files
  were checked. The prior running package was left intact.
- Live checks are opt-in: `--live-translation` on the test executable sends only
  the fixed synthetic example to the configured official DeepSeek endpoint and
  consumes a small amount of API quota. Ordinary tests never call a paid API.

## Earlier speech integration baseline

- Release build completed with zero warnings/errors. The self-contained Windows
  publish was checked for required runtime/model/license files, matching App/Core
  DLL hashes against the final build, and matching model SHA-256 checksums.
- **35/35** checks in the integration run: 30 core/regression checks plus real
  Whisper overlapping/final inference, real WASAPI loopback finalization, and a
  complete playback → capture → revised transcription → multilingual translation
  session that flushes the last caption on Stop, neural rejection of silence/tones,
  and Japanese speech recognized with English/Japanese language choices.
- Translation uses a fake HTTP handler. Tests exercise one request for multiple
  captions/languages, shared conversation history, glossary, Unicode SSE chunks,
  exact caption/language response mapping, rejected missing/duplicate results,
  HTTP errors and cancellation. Context correction was tested with simulated API
  responses, including small Japanese edits, prohibited rewrites, preserved raw
  text, and correction-off compatibility. No real API key or paid provider was used.
- Buffer checks cover silence, clicks, tiny input buffers, normal pauses within
  an utterance, same-id overlapping revisions, hard limits, boundary overlap and flush.
- Queue checks cover coalescing, retention/order of other utterances, capacity,
  cancellation and completion/drain behavior.
- Text checks cover Latin/CJK overlap matching and stable clause extraction.
- New clause checks verify confirmation during continuous Japanese speech, stable
  clause IDs, an editable tail, safe timestamp cuts (never character-based cuts),
  trimming from future audio snapshots, retained repeated words, content-based
  response validity, and reuse of translations across same-text revisions.
- Streaming tests hold the end of an HTTP response open and verify that a validated
  correction and first translation arrive before the remaining response is released.
  Correction guards check numbers, negation, decision words and rewrite size.
- Audio checks cover DC removal, finite samples, bounded gain and adaptive gating.
  Model migration is tested with isolated settings; explicit later choices remain.
- Memory/UI checks cover cross-source context, source-local recognition prompts,
  bounded history, isolated conversations, in-place captions and rejected stale
  translations. Settings checks cover DPAPI and malformed JSON recovery.
- New regression checks cover retention of completed room translations until all
  target languages finish, failed/stale responses, isolated room state across conversations,
  source renaming and device identity, localized format arguments, export summaries with
  tasks/deadlines and proposed meeting labels, long-input summary reduction, and raw
  transcript fallback when the API is unconfigured, fails or is canceled.
- Persisted settings checks include the UI language and renamed source/device fields.
- **31/31** in the render run: the same 30 checks plus rendering layouts with
  WPF binding-error tracing. There are 32 images in `artifacts/ui-review/`, including
  the source panel at 980×620, retained room translations during a new draft, English,
  Japanese and both Chinese scripts, all three settings tabs, conversation creation,
  and context-adjustment indicators in the main, pinned and room displays.
  Key small-window and localized layouts were visually inspected. XAML localization
  keys were checked against the embedded catalog.
  No desktop windows were shown and no input was sent to the user's desktop.

The sample speech was recognized locally as: “And so my fellow Americans ask not
what your country can do for you, ask what you can do for your country.”

## Local timing sample

A 7.2-second public Japanese WAV from the k2-fsa SenseVoice demonstration was
decoded locally, with a fixed Japanese language, greedy decoding, neural VAD and
no glossary/prompt or external API. Timings below are single runs after loading and
warming the model; they are not meeting accuracy scores or end-to-end latency.

| Model | First decode | Warm decode | Warm compute / audio duration |
| --- | ---: | ---: | ---: |
| Base Q5_1 | 4.23 s | 3.35 s | 0.47 |
| Small Q5_1 | 10.80 s | 10.29 s | 1.43 |

Base is the new balanced default. Small remains available, but would fall behind
this recording in live use on this CPU. Base transcribed the phrase 弁当制 while
Small produced 弁当性 on this sample: more parameters do not fix every homophone.
Both test models and the neural VAD asset were SHA-256 checked against the model
repositories; hashes and sources are recorded in `models/README.md`.

Use `--benchmark <model> <16k-mono.wav> <language> [beam]` with the test executable
to compare models on representative recordings without audio playback or API calls.
The additional simulated-buffer timings are diagnostic CPU totals, not measured
real-time capture latency; repeated preview passes add work beyond one full decode.

## Remaining validation

Real-provider correction/translation quality, representative Japanese homophones,
meeting microphones/noise, code switching, sustained multi-source throughput on the
deployment laptops, and physical multi-monitor
full-screen placement should be evaluated with the company's intended environment.
The render checks validate layout/bindings, not native monitor switching or keyboard
interaction. Voice diarization remains outside scope: source labels are explicit.
Summary content was tested with simulated API responses, not a paid provider. The
prompt explicitly prohibits invented owners/deadlines/commitments and distinguishes
proposed meetings from scheduled ones; real summary quality still needs validation.
Context corrections are explicitly unverified suggestions; heuristics cannot prove
that a changed word was spoken. Raw recognition remains visible and is exported.
