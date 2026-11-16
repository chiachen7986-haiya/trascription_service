@echo off
setlocal
pushd "%~dp0.."
if not exist "models" mkdir "models"
if not exist "artifacts\test-data" mkdir "artifacts\test-data"
call :model "ggml-base-q5_1.bin" "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base-q5_1.bin" "422f1ae452ade6f30a004d7e5c6a43195e4433bc370bf23fac9cc591f01a8898"
if errorlevel 1 goto failed
call :model "ggml-silero-v5.1.2.bin" "https://huggingface.co/ggml-org/whisper-vad/resolve/main/ggml-silero-v5.1.2.bin" "29940d98d42b91fbd05ce489f3ecf7c72f0a42f027e4875919a28fb4c04ea2cf"
if errorlevel 1 goto failed
if /I not "%~1"=="--accuracy" goto tiny
call :model "ggml-small-q5_1.bin" "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small-q5_1.bin" "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb"
if errorlevel 1 goto failed
:tiny
if exist "models\ggml-tiny.bin" goto sample
curl.exe --fail --location "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin" --output "models\ggml-tiny.bin.partial"
if errorlevel 1 goto failed
move /Y "models\ggml-tiny.bin.partial" "models\ggml-tiny.bin" >nul
if errorlevel 1 goto failed
:sample
curl.exe --fail --location "https://raw.githubusercontent.com/ggml-org/whisper.cpp/master/samples/jfk.wav" --output "artifacts\test-data\jfk.wav"
if errorlevel 1 goto failed
curl.exe --fail --location "https://huggingface.co/spaces/k2-fsa/automatic-speech-recognition/resolve/main/test_wavs/sense_voice/ja.wav" --output "artifacts\test-data\japanese.wav"
if errorlevel 1 goto failed
echo Test assets ready.
popd
exit /b 0
:failed
echo Download failed. The application can also download a model in Settings.
popd
exit /b 1
:model
if exist "models\%~1" exit /b 0
curl.exe --fail --location "%~2" --output "models\%~1.partial"
if errorlevel 1 exit /b 1
powershell.exe -NoProfile -Command "if ((Get-FileHash -LiteralPath 'models\%~1.partial' -Algorithm SHA256).Hash -ne '%~3') { throw 'Model checksum mismatch' }"
if errorlevel 1 exit /b 1
move /Y "models\%~1.partial" "models\%~1" >nul
exit /b %errorlevel%
