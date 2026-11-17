# Transcription Service · Rosedale (0.10.0)

- Added reusable terminology dictionaries with per-entry language, optional Japanese
  readings, and JSON import and export. Select a dictionary when creating a conversation.
- Added a local API token meter for translation and meeting summaries, with a 14-day
  chart and USD price quotes for OpenAI, Mistral AI, and DeepSeek.

## Rosedale (0.9)

First internal portable release for Windows x64, November 5, 2026.

Japanese correction fix:

- Restores API-generated Japanese output even for Japanese speech, so unnatural
  STT word spacing can be cleaned up. Other token-efficiency changes are retained.

Token-efficiency update, November 5, 2026:

- Literal Unicode in API prompts avoids expanded Japanese/Chinese escape sequences.
- Unchanged correction markers avoid repeating unmodified transcript text.
- Known matching-language outputs other than Japanese reuse accepted corrected text
  locally. With correction disabled and only those matching targets, no translation API request is made.
- Unknown/mixed language and Chinese script conversion retain API translation.
- Up to three already queued clauses share a request without waiting to fill a batch.
- Stable instructions, topic and glossary precede changing data for provider prefix caching.
- Existing history limits, correction safeguards and output allowances are retained.

- Microphone and computer-output capture with named sources.
- Local multilingual speech recognition and contextual API translation.
- SenseVoice Small INT8 included and selected for fresh installs; Qwen3-ASR and
  Whisper weights can be downloaded in Settings. CPU and hardware backend options remain available.
- GTCRN microphone denoising enabled by default, independently for each microphone.
- Encrypted saved conversations, resume, deletion and clear-all controls.
- Always-on-top captions with language priority and a meeting-room display.
- Transcript exports with API-generated summaries, action items and mentioned next meetings.
- English, Japanese, Simplified Chinese and Traditional Chinese interface options.
- Updated icon, About tab and Rosedale version branding.

Extract the whole ZIP and read START-HERE.txt. The .NET runtime is included.
API translation requires a configured provider; local transcription works offline.
Saved settings and conversations remain in the existing LocalAppData/BlueJays folder.

Speaker labels identify audio sources; individual voices on one device are not
automatically identified. Qwen GPU is unavailable in this build; Qwen OpenVINO
accelerates the audio encoder on CPU. Recognition and translation can make errors.

This software is strictly for internal use only, for direct profit distribution or resale is prohibited
