@echo off
setlocal
pushd "%~dp0.."
set "BLUEJAYS_INCLUDE_RUNTIME=true"
if /I "%~1"=="--framework-dependent" set "BLUEJAYS_INCLUDE_RUNTIME=false"
rem A separate release directory lets users finish/export an open conversation.
for /f %%T in ('powershell.exe -NoProfile -Command "[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')"') do set "BLUEJAYS_BUILD_STAMP=%%T"
if not defined BLUEJAYS_BUILD_STAMP goto failed
set "BLUEJAYS_PACKAGE=artifacts\win-x64-%BLUEJAYS_BUILD_STAMP%"
dotnet restore BlueJays.slnx --locked-mode
if errorlevel 1 goto failed
dotnet build BlueJays.slnx --no-restore -c Release
if errorlevel 1 goto failed
dotnet run --project tests/BlueJays.Tests -c Release --no-build
if errorlevel 1 goto failed
dotnet publish src/BlueJays.App -c Release -r win-x64 --self-contained %BLUEJAYS_INCLUDE_RUNTIME% -o "%BLUEJAYS_PACKAGE%" -p:PublishSingleFile=false
if errorlevel 1 goto failed
dotnet publish src/BlueJays.SpeechHost -c Release -r win-x64 --self-contained %BLUEJAYS_INCLUDE_RUNTIME% -o "%BLUEJAYS_PACKAGE%\speech-host" -p:PublishSingleFile=false
if errorlevel 1 goto failed
> "%BLUEJAYS_PACKAGE%\build.ready" echo Ready
if errorlevel 1 goto failed
echo Ready: %BLUEJAYS_PACKAGE%\TranscriptionService.exe
echo Run-BlueJays.cmd starts the newest complete build. Distribute its complete folder.
popd
exit /b 0
:failed
echo Build failed. Review the error above.
popd
exit /b 1
