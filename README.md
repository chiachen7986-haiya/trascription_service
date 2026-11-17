# Transcription Service · Rosedale (0.10.1)

Windows 11 x64 desktop application, built with .NET 10 / WPF. Captures microphones
and computer playback, recognizes speech locally with Whisper, SenseVoice, or Qwen3-ASR,
and translates conversations through a configurable Chat Completions API.

## Start

Double-click **Run-Transcription-Service.cmd** to start the newest completed build. Keep the
entire published folder together. The portable Rosedale release is under `releases/`:
extract its ZIP and run **TranscriptionService.exe**. It includes .NET, SenseVoice,
GTCRN and Silero. Qwen and Whisper weights are available through Settings downloads.
Development builds made with `scripts/Build.cmd` still bundle all staged models.

1. Click **New Conversation**. Enter a name, select languages people might speak,
   and choose up to **four output languages**. No output languages means entirely
   local transcription. Topic and terminology help recognition and translation.
   Select a saved dictionary to include names, Japanese kanji and readings. For Japanese speech
   translated to English, select Japanese under Spoken and English under Translate into;
   only mark English as spoken if someone will actually speak it.
2. Expand **+ Add source** to add a **Microphone** or **Computer audio** device. Enable
   the desired sources. Edit **Source name** while stopped to rename a source; this
   updates its caption labels and conversation context without changing its device.
   Names are saved between app launches. The add controls collapse after adding a
   source; the whole source list scrolls on small windows. Computer audio captures all applications on the chosen
   output device: use the same device as Teams/Zoom. Headphones prevent duplicates
   when recording microphone and playback together.
   Each source shows its current status. **Choose device** changes its endpoint while
   preserving its source name and conversation history. Unplugged/disabled sources
   are marked unavailable and skipped at startup; the other selected sources can
   start, with an explicit warning and a count of listening sources. The application
   never silently substitutes a different microphone or output device. If every
   selected source is unavailable, startup reports those sources and asks for a
   reconnect/device selection. A reconfigured endpoint is reopened once with fresh
   audio-client handles before being reported unavailable.
3. In **Settings → API configuration**, configure the provider's API base URL, model and key. The base
   URL excludes `/chat/completions`. DeepSeek's preset disables thinking; turn off
   that extension for providers that do not accept it. The provider needs streaming
   text Chat Completions and must follow the JSON output instructions.
   For DeepSeek, enable **Send DeepSeek's thinking-disabled parameter** to use fast
   mode from the first request. If omitted, the official service can start reasoning;
   live translation now detects this and switches to non-thinking mode for the session.
4. Press **Start / Resume**. Draft speech updates in the same caption; confirmed
   clauses and complete utterances are translated with recent conversation context.
5. **Stop & finish** stops capture, flushes the ending, and finishes pending work.
   This can take a few seconds, up to a 35-second finishing deadline. Timed-out or
   failed work is marked incomplete; original text remains available.
6. The conversation selector switches between conversations while stopped. Resume
   keeps that conversation's context. New Conversation starts with separate history.
7. **Export captions** saves the selected conversation's saved captions,
   including all translation languages and their status, to a UTF-8 text file. If
   an API is configured (key supplied, localhost provider, or previously successful
   connection), it generates a summary at the top: overview, decisions, to-do items
   with mentioned owners/deadlines, and proposed or scheduled future meetings.
   Unspecified owners/deadlines remain unspecified. Summary language follows the UI.
   Long exports are summarized in sections, then combined. **Cancel export** cancels
   summarization and still saves the raw transcript. An unavailable API or malformed
   response also preserves a transcript export with an explanation.

Conversations, transcripts, translations, and preferred caption languages
are saved automatically on this PC, encrypted for the current Windows user. Reopen
them using the conversation selector. **Delete conversation** removes the selected
conversation; **Clear all** removes every saved conversation after confirmation.
Audio is never recorded to disk. Existing API/device settings retain the
LocalAppData/BlueJays location for compatibility. Audio-source labels identify
devices, not individual voices. Conversations still open in older memory-only builds
must be exported before closing that older build.

## Terminology dictionaries and API usage

Open **Settings → Terminology** to create a dictionary, add terms and acronyms,
and assign a language to each entry. Japanese entries can include a reading.
Use **Export** to save a dictionary as JSON and **Import** to add a copy. Saved
dictionaries are kept locally and can be reused in **New Conversation**. Editing
a dictionary applies the next time you start or resume a conversation that uses it.
Deleting a conversation leaves its dictionary in the library.

**Settings → Token usage** records provider-reported input and output tokens from
live translation, contextual correction, and export summaries. It shows all-time
totals since tracking began, responses without usage data, and a 14-day chart.
The USD quote calculator accepts prospective token counts or the recorded totals.
Use the selected provider's pricing link to check the dated rates, including
DeepSeek's separate peak and off-peak prices. Dictionaries and usage totals are
stored in local JSON files under `LocalAppData/BlueJays`.

## Speech engines and hardware

The token-efficiency update sends literal Unicode in prompt JSON, accepts compact
unchanged-correction markers, and batches up to three already waiting clauses (up
to 1,800 source characters) without adding a batching delay. Stable instructions,
spoken languages, topic and glossary precede changing request data for provider
prefix caching. The existing context window and output limits are unchanged.

Japanese output is always generated by the API, including Japanese-to-Japanese,
to clean unnatural STT spacing while retaining meaning and spaces in Latin terms.
Other matching-language output is copied from the accepted corrected transcript when
the recognizer provides a language tag or a single spoken language is configured.
If correction is disabled, no Japanese output is selected, and every output matches, the translation HTTP request
is skipped entirely. With correction enabled, a correction request is still made.
Unknown/weak/mixed language and Chinese script conversion stay on the API path.
SenseVoice exports its native language tag; Qwen auto mode may lack that metadata
and conservatively uses the API. This optimization adds no language-detection model.

Choose an engine in **Settings → Speech recognition**. Model paths are retained
independently. SenseVoice and Qwen use a model **folder**; Whisper uses a GGML file.
SenseVoice is bundled in the portable release and selected on a fresh install.
Qwen and Whisper weights are optional downloads. The download button retrieves
pinned official sherpa exports with SHA-256 verification, progress and cancellation.

- **SenseVoice Small INT8:** fast Japanese/Chinese/multilingual captions. It supports
  Japanese, Chinese, English, Korean and Cantonese; English quality can be lower.
  **ONNX Runtime · CPU** is the fastest tested setting on this Iris Xe laptop.
  OpenVINO CPU and Intel GPU are also available; GPU uses FP32 activations.
- **Qwen3-ASR 0.6B INT8:** a slower quality alternative, particularly for English.
  Select regular CPU or OpenVINO CPU. OpenVINO runs its audio encoder; the frontend
  and autoregressive decoder stay on CPU. Qwen GPU failed validation on this
  machine and is not offered in this build.
- **Whisper:** existing Vulkan/CPU and model choices remain available.

The new engines share one persistent multilingual model. They skip Tiny language
detection and duplicate language-model instances. Qwen defaults to final phrases;
its optional live previews have a six-second minimum interval. SenseVoice retains
live previews and can safely commit audio using aligned CTC timestamps. Untimed
Qwen text never causes guessed audio cuts.

Models warm before capture starts. Slow decodes back off previews; cached results
do not reset that backoff. Replaceable previews yield queue capacity to final
audio. The status shows the actual backend and delay. Enabled CPU fallback is
explicitly reported, and cancelling a native decode terminates only its helper.
OpenVINO compilation is cached locally; first GPU startup can take about a minute.
See `native/README.md` for the runtime, rebuild and validation details.

## Pop-out displays

The pinned caption window has small language buttons. Choose the original speech or
an output language to enlarge it; other text remains smaller. This preference is
saved per conversation. Empty translation placeholders are omitted.

**About** displays the supplied logo, Transcription Service name, Rosedale (0.10.1),
the internal-use disclaimer and the supplied image. The logo is also the Windows
application icon. Earlier builds retain their old executable names; the launcher
supports both names.

GTCRN microphone noise suppression is enabled by default and can be disabled under
**Settings → General**. It processes 16 kHz microphone audio continuously before
speech detection, using one CPU thread and separate state for each microphone.
It never filters computer loopback audio. The ending is flushed before capture
is disposed. This is speech denoising, not playback echo cancellation or speaker
separation. Keep existing device enhancements in mind when comparing clarity.

- **Pinned captions**: compact gray window, always above other apps. Shows the
  latest three utterances, their source labels and every selected translation.
  Drag the top strip to move it; resize from the edges/grip; A− / A+ changes text
  size. The × closes only this display, leaving the conversation running.
- **Meeting room**: original speech above colored translation panels, matching the
  conversation's output languages even before speech starts. One language fills the
  area, two display side by side, and three/four use a two-column grid. Drag this
  window to the meeting-room monitor, then use **F11** or the full-screen button.
  **Escape** exits full screen. While the next utterance is being recognized or
  translated, the previous completed original and all its translations remain on
  screen. The room advances only when every target language for the next confirmed
  caption has completed, avoiding blank cards or mismatched source/translation text.
  These displays follow the selected conversation.

## Interface language and settings tabs

**Settings → General → Application language** offers English, Japanese, Simplified
Chinese and Traditional Chinese. Save to apply the language to existing windows;
the choice is retained on restart. This controls the application interface and
export-summary language, separately from spoken and target languages in a conversation.
User-authored source names, conversation names and recognized text are preserved.

Settings has four separate tabs: **General** (UI language and conversation defaults),
**Speech recognition** (local model and audio parameters), **API configuration**
(provider URL, model, key and provider-specific options), and **About**.

**Speech recognition → Use Vulkan GPU acceleration** is enabled by default. The
application includes the Vulkan backend and falls back to CPU if it cannot use a
compatible GPU. The existing Intel Iris Xe driver worked on the tested laptop.
The listening status shows the backend and approximate STT
delay from the processed speech's end to completion of decoding. It excludes API time.
Models are warmed with synthetic silence before capturing audio, so initial GPU
kernel compilation does not build a backlog of meeting audio.

**Decode two languages simultaneously** allows two independent language decoders
to use the same GPU concurrently. It is off by default: on this Iris Xe, repeated
Small-model tests showed approximately 2.14 seconds sequentially versus 2.10 seconds
concurrently for the same Japanese/English pair, a very small difference. Two
language workers still exist with the option off; they take turns on the GPU.
Independent audio sources can progress concurrently; each individual source remains
ordered to preserve recognition context and overlapping chunk boundaries. One mixed
audio stream is routed to the selected language worker, not decoded twice. More
than two configured spoken languages share the two worker slots. Disabling GPU
acceleration limits actual inference to one CPU decode at a time.

## Recognition quality and context correction

The portable release defaults to **SenseVoice Small INT8 on CPU** on a fresh install.
Existing engine selections are retained. In full development builds, previous
Whisper Tiny-model settings upgrade once to bundled **Base Q5_1**; custom models
and later explicit model choices are kept.
**Settings → Speech recognition** provides downloads and, when present, one-click
selection of bundled Whisper Base or **Small Q5_1**, a slower accuracy option. Optional three-beam search
can improve some recordings at further compute cost; it is off by default.

On this i5-1235U / Iris Xe laptop, a 7.2-second public Japanese clip took about
**1.47 seconds** with warm Small Q5_1 and Vulkan, using a fixed Japanese language.
An earlier CPU-only run took 5.67 seconds with fixed Japanese and 17.82 seconds with
automatic detection. The routed pipeline now uses Tiny to classify language, then
fixed-language decoding with the selected main model. These are separate diagnostic
runs, not accuracy scores or end-to-end latency promises. See `VERIFICATION.md` for
conditions, routing/streaming checks and the earlier CPU measurements. More sources
increase the workload; larger models are not guaranteed to keep up.

**Settings → API configuration → Context-assisted transcript correction** is enabled
by default alongside translation. The same request uses recent conversation context
and the glossary to suggest small original-language spelling/homophone corrections,
then translates the resulting clause. It adds no separate correction request.
Adjusted captions are labeled, with expandable **Raw recognition before context
correction**; exports include both versions. The API does not receive audio and
cannot verify what was said. Correction can still be wrong. Prompts prohibit invented
facts; local guards reject large rewrites and detectable changes to numbers, negation
and decision words. Rejected or malformed correction responses fall back once to
raw-text translation. Disable the checkbox to retain only local STT wording.
No output languages means no live correction or translation API requests.

## Coherent speech and translation pipeline

Each source maintains an independent 16 kHz mono speech buffer. Provisional decode
snapshots start after 1.2 seconds; later preview intervals adapt to decoding time.
Under backlog, stale draft previews are skipped while final audio is retained.
Successive hypotheses are compared for a stable prefix. Complete stable clauses
become separate confirmed captions during ongoing speech; one tail remains editable.
A roughly one-second pause finalizes the tail; a default 8-second maximum preserves 1.5 seconds of audio overlap
for the next block. Duplicate boundary text is removed using normalized suffix/prefix
matching. Confirmed segment timestamps safely trim committed audio from future and
already-queued snapshots, retaining 300 ms at the cut. A cut is never estimated from
character count. Where Whisper has not provided a suitable segment boundary, that
audio remains for the next bounded pass. A final pause reuses a matching previous
decode when no new speech has arrived. This reduces repeated inference; the backend
still decodes snapshots rather than offering a native incremental encoder cache.
Whisper receives the glossary, topic and recent raw text from the same source;
unreviewed API corrections are not fed back into recognition prompts.

Only confirmed clauses are translated. Final utterances always flush. Each clause
has its own ID; later speech cannot invalidate its response. Pending audio revisions
replace older revisions of the same window without dropping other utterances.
Live API requests take one clause at a time, covering all selected output languages.

Each request includes chronological confirmed conversation turns **across sources**,
recent valid translations, topic and terminology. History is limited to 24 turns /
roughly 8 KB of serialized text. Only the current captions are translated. A response
must contain exactly one caption-id/language result for each requested pair; results
are displayed as soon as each complete JSON record has been validated from the SSE
stream, rather than waiting for the whole response. Incomplete tokens/JSON are not
shown. Translation validity follows exact clause content, so a harmless revision
change does not discard useful output, and changed words reject obsolete results.
The meeting-room display swaps a coherent original/all-language card only when
that confirmed clause's translations are ready.

Up to two independent warm speech-model contexts serve language workers. A small
CPU-only Tiny model classifies the audio among the conversation's configured spoken
languages. A single configured language bypasses classification entirely. Language
choices are cached across previews and reconsidered after six new seconds of speech
(two seconds when confidence was weak). Detection uses up to eight seconds of audio;
a short sentence ending alone was unreliable in the Japanese diagnostic. Source-local
language history stabilizes weak evidence. This is not diarization, and rapid language
switches within one clause can still be recognized incorrectly.

Decode drafts are coalesced rather than accumulating an unbounded queue. The audio
queue holds 64 utterance snapshots;
translation holds up to 500 captions. Capacity errors stop the session visibly instead
of silently skipping discussion. API requests time out after 30 seconds. API failures
leave original captions intact and show a warning.

Live output budgeting reserves for CJK, JSON fields/escapes and correction text:
at least 2,048 tokens for translation, or 4,096 with correction, bounded at 8,192.
A cut-off answer is retried once for remaining work with an 8,192-token allowance,
retaining completed translations and the exact accepted correction. Complete valid
records in the final SSE event are kept even if the enclosing JSON terminator is
cut off. Recovery remains within the 30-second deadline and honors Stop/cancellation.
DeepSeek reasoning detection permits one additional fast-mode restart, remembers it
for following clauses, and never injects its extension into another provider.
DeepSeek's default reasoning behavior is documented in its
[thinking-mode guide](https://api-docs.deepseek.com/guides/thinking_mode/).

## Settings and requirements

- Windows 11 x64, tested on build 22631.
- CPU with AVX/AVX2/FMA/F16C as required by the packaged Whisper native runtime.
- Microsoft Visual C++ 2022 x64 Redistributable or compatible newer runtime:
  https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist
- Microphone access for desktop applications must be allowed in Windows.
- .NET SDK **10.0.403** for development; pinned in `global.json`.

The self-contained package includes .NET. Framework-dependent publishing instead
requires .NET 10 Desktop Runtime on the target computer. Neither requires Python,
CUDA or a paid STT service. Quantized Base and Small are bundled, alongside Tiny.
Other models can be selected or downloaded in Settings. Larger models increase compute
use and latency, and do not improve every recording.

Conversation languages override the defaults in Settings. Selecting one spoken
language fixes Whisper to it. With multiple spoken languages, a decode in an unrelated
language triggers a restricted-language retry. This does not guarantee perfect
code switching. Speech capture uses an adaptive energy gate with attack/hysteresis,
DC removal, bounded gain and optional local Silero neural speech validation before
Whisper. Very short speech and background sound can still be misrecognized.

The original MVP's short timing defaults are migrated on load. Pause duration can
now be set to 700–2000 ms; maximum utterance length to 8–20 seconds. Preview
intervals do not guarantee the same end-to-end latency: inference and API timing
depend on hardware, languages and provider. Full-sentence translation sometimes
needs the sentence ending, especially across different word orders.

Device selections are pinned. If changing headset or call output, stop and use
**Choose device** on that source. A device lost during recording stops the session
with a source-specific message; reconnect or change it before resuming.
No per-application capture or voice diarization
is implemented. There is no speech synthesis, meeting bot or transcript cloud store.

## Build and verify

On Windows x64, install the .NET SDK specified in `global.json`. The source
repository includes the native speech runtime, GTCRN and Silero. It leaves out
the large speech recognition weights. See [model sources and checksums](models/README.md)
to stage the SenseVoice export under `models/` before building if you want it
available on first launch. You can also download a model from **Settings → Speech
recognition** after building. `scripts\Get-TestAssets.cmd` fetches Whisper weights
and optional test recordings. API credentials are configured per user after launch.

```powershell
scripts\Build.cmd
# Smaller output, requiring the desktop runtime on the destination PC:
scripts\Build.cmd --framework-dependent
```

The command restores locked packages, builds, runs the core executable checks, and
publishes to a new `artifacts/win-x64-<UTC build stamp>` directory. A `build.ready`
marker is written only after publishing succeeds; the launcher chooses the newest
completed directory and falls back to the original `artifacts/win-x64` package.
An open older app is left running so its conversation can be exported before closing.
Close the older app and run the launcher to use the update. Distribute the entire
new package folder. `Run-BlueJays.cmd --print-package` shows which executable will
be selected without launching a window.
Start from source with `dotnet run --project src/BlueJays.App -c Release`.

Additional verification:

```powershell
# Optional test assets (already present in this workspace):
scripts\Get-TestAssets.cmd
# Real local model, loopback and complete session integration checks:
dotnet run --project tests/BlueJays.Tests -c Release -- --integration models/ggml-base-q5_1.bin artifacts/test-data/jfk.wav artifacts/test-data/japanese.wav
# Offline file diagnostic, with no audio playback or API usage:
dotnet run --project tests/BlueJays.Tests -c Release -- --benchmark models/ggml-base-q5_1.bin artifacts/test-data/japanese.wav ja
# Compare actual Vulkan sequential/concurrent decoding (append cpu for CPU-only):
dotnet run --project tests/BlueJays.Tests -c Release -- --gpu-benchmark models/ggml-small-q5_1.bin artifacts/test-data/japanese.wav artifacts/test-data/jfk.wav
# Verify real language classification plus the routed GPU decoder pair:
dotnet run --project tests/BlueJays.Tests -c Release -- --routed-benchmark models/ggml-small-q5_1.bin artifacts/test-data/japanese.wav artifacts/test-data/jfk.wav
# Feed two public recordings at wall-clock speed through the production session:
# No microphone, playback, paid API or audio/transcript files. Append parallel to test concurrent GPU use.
dotnet run --project tests/BlueJays.Tests -c Release -- --streaming-benchmark models/ggml-small-q5_1.bin artifacts/test-data/japanese.wav artifacts/test-data/jfk.wav seconds=120
# Render all layouts and detect WPF binding errors, with no desktop interaction:
dotnet run --project tests/BlueJays.Tests -c Release -- --render-ui artifacts/ui-review
# Opt-in: synthetic Japanese → English through the saved official DeepSeek provider.
# Uses a small amount of API quota; never sends meeting content or prints the key:
dotnet run --project tests/BlueJays.Tests -c Release -- --live-translation
# Inspect saved endpoint state/activation without starting audio capture:
dotnet run --project tests/BlueJays.Tests -c Release -- --audio-diagnostics
# Brief startup check using the real saved model/sources. No API calls or saved audio:
dotnet run --project tests/BlueJays.Tests -c Release -- --audio-startup-smoke
```

Integration checks play a brief tone and public speech excerpt through the default
output device. All translation checks use a fake HTTP handler: no paid requests or
meeting content are sent to external providers. Real-provider translation quality
and actual multi-monitor full-screen behavior still need user validation.

## Code map

- `BlueJays.Core/Audio/StreamingSpeechBuffer`: overlapping utterance revisions.
- `BlueJays.Core/Conversation`: text agreement, bounded revision queues and history.
- `BlueJays.Core/Translation`: contextual multilingual batches, shared SSE transport,
  strict response mapping and sectioned meeting-summary generation for exports.
- `BlueJays.App/Services`: WASAPI capture, warm Whisper model, session lifecycle,
  native full-screen bounds, model download and encrypted settings.
- `BlueJays.App/ViewModels`: conversation-local captions and per-language state.
- `BlueJays.App/Views`: creation dialog, pinned captions and room display.
- `BlueJays.App/Localization`: embedded English/Japanese/Chinese UI strings and live
  bindings. The TSV has one English key and three translated values per row.
- `BlueJays.Tests`: regression tests, real audio integration and off-screen UI renders.

Settings are stored at `%LOCALAPPDATA%/BlueJays/settings.json`; API keys are encrypted
with Windows DPAPI for the current user. Audio stays in memory. Conversations with
translation languages send recent transcript/translation history, current captions,
topic and glossary to the selected provider. Export summarization additionally sends
the retained export snapshot to that provider, even for a transcription-only conversation
when a provider is configured. Summary requests time out after 45 seconds each and
can be canceled; raw transcript export is retained on failure. Export is the only
transcript-writing operation. See `THIRD-PARTY-NOTICES.md` and `licenses/` for upstream MIT notices.
