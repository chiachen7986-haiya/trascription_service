@echo off
setlocal DisableDelayedExpansion
pushd "%~dp0"
rem Prefer the most recently completed portable release.
for /f "delims=" %%D in ('dir /b /ad /o-d "releases\TranscriptionService-*-win-x64*" 2^>nul') do (
    if exist "releases\%%D\build.ready" if exist "releases\%%D\TranscriptionService.exe" (
        if /I "%~1"=="--print-package" (echo releases\%%D\TranscriptionService.exe) else (start "" "releases\%%D\TranscriptionService.exe")
        goto done
    )
)
rem Only fully published builds are launchable; a running older build stays intact.
for /f "delims=" %%D in ('dir /b /ad /o-n "artifacts\win-x64-*" 2^>nul') do (
    if exist "artifacts\%%D\build.ready" if exist "artifacts\%%D\TranscriptionService.exe" (
        if /I "%~1"=="--print-package" (echo artifacts\%%D\TranscriptionService.exe) else (start "" "artifacts\%%D\TranscriptionService.exe")
        goto done
    )
    if exist "artifacts\%%D\build.ready" if exist "artifacts\%%D\BlueJays.exe" (
        if /I "%~1"=="--print-package" (echo artifacts\%%D\BlueJays.exe) else (start "" "artifacts\%%D\BlueJays.exe")
        goto done
    )
)
if exist "artifacts\win-x64\BlueJays.exe" (
    if /I "%~1"=="--print-package" (echo artifacts\win-x64\BlueJays.exe) else (start "" "artifacts\win-x64\BlueJays.exe")
) else (
    if /I "%~1"=="--print-package" goto done
    dotnet run --project "src\BlueJays.App\BlueJays.App.csproj" -c Release
)
:done
popd
