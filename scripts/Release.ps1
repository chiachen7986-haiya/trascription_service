# Build an isolated, self-contained Windows x64 release without touching older builds.
# Run from PowerShell: .\scripts\Release.ps1
[CmdletBinding()]
param([ValidatePattern('^$|^-[a-z0-9-]+$')][string] $PackageSuffix = '')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$workspace = Split-Path $PSScriptRoot -Parent
[xml]$project = Get-Content -Raw (Join-Path $workspace 'src/BlueJays.App/BlueJays.App.csproj')
$version = $project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
$codename = $project.SelectSingleNode('/Project/PropertyGroup/ReleaseCodename').InnerText
$name = "TranscriptionService-$codename-$version-win-x64" + $PackageSuffix
$releaseRoot = Join-Path $workspace 'releases'
$package = Join-Path $releaseRoot $name
$archive = "$package.zip"
$qa = Join-Path $workspace ('artifacts\release-qa-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
if ((Test-Path -LiteralPath $package) -or (Test-Path -LiteralPath $archive)) {
    throw 'This release already exists. Move it aside before rebuilding; published releases are never overwritten.'
}
New-Item -ItemType Directory -Path $releaseRoot,$qa -Force | Out-Null

function Invoke-DotNet {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]] $Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE." }
}

Push-Location $workspace
try {
    Start-Transcript -Path (Join-Path $qa 'build.log') | Out-Null
    Invoke-DotNet @('restore','BlueJays.slnx','--locked-mode','-r','win-x64','-p:SelfContained=true')
    Invoke-DotNet @('build','BlueJays.slnx','--no-restore','-c','Release')
    $tests = Join-Path $workspace 'tests\BlueJays.Tests\bin\Release\net10.0-windows\win-x64\BlueJays.Tests.dll'
    Invoke-DotNet @($tests,'--render-ui',(Join-Path $qa 'ui'))
    Invoke-DotNet @('publish','src/BlueJays.App','-c','Release','-r','win-x64','--self-contained','true','--no-restore',
        '-o',$package,'-p:PublishSingleFile=false','-p:IncludeOptionalSpeechModels=false')
    Invoke-DotNet @('publish','src/BlueJays.SpeechHost','-c','Release','-r','win-x64','--self-contained','true','--no-restore',
        '-o',(Join-Path $package 'speech-host'),'-p:PublishSingleFile=false')

    $senseName = 'sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2024-07-17'
    $sense = Join-Path $package "models\$senseName"
    $required = @('TranscriptionService.exe','TranscriptionService.dll','coreclr.dll','PresentationFramework.dll',
        'speech-host\BlueJays.SpeechHost.exe','speech-host\coreclr.dll','speech-host\onnxruntime.dll',
        'speech-host\sherpa-onnx-c-api.dll','models\gtcrn.onnx','models\ggml-silero-v5.1.2.bin',
        "models\$senseName\model.int8.onnx","models\$senseName\tokens.txt",'licenses\GTCRN-MIT.txt',
        'licenses\SenseVoice-FunASR-MODEL-LICENSE.txt','THIRD-PARTY-NOTICES.md')
    foreach ($relative in $required) {
        if (!(Test-Path -LiteralPath (Join-Path $package $relative) -PathType Leaf)) { throw "Missing release file: $relative" }
    }
    # Fresh publish directories prevent stale, excluded weights from leaking into the ZIP.
    if (@(Get-ChildItem (Join-Path $package 'models') -Directory).Count -ne 1 -or
        (Get-ChildItem (Join-Path $package 'models') -Filter 'ggml-*.bin' | Where-Object Name -ne 'ggml-silero-v5.1.2.bin')) {
        throw 'Unexpected optional model in the slim release.'
    }
    if (Get-ChildItem $package -Recurse -File | Where-Object { $_.Name -in @('settings.json','conversations.v1.dat','terminology.v1.json','token-usage.v1.json') }) {
        throw 'User data must never be included in a release.'
    }
    $fixture = Join-Path $workspace 'artifacts\model-comparison\audio'
    if (!(Test-Path (Join-Path $fixture 'ja-lunch.wav')) -or !(Test-Path (Join-Path $fixture 'en-jfk.wav'))) {
        throw 'Japanese and English release smoke-test fixtures are missing.'
    }
    # Decode with the actual published helper and bundled weights, with CPU fallback disabled.
    Invoke-DotNet @($tests,'--onnx-smoke','sensevoice',$sense,'cpu',(Join-Path $fixture 'ja-lunch.wav'),
        (Join-Path $fixture 'en-jfk.wav'),(Join-Path $qa 'sensevoice.json'),
        ('host=' + (Join-Path $package 'speech-host\BlueJays.SpeechHost.exe')),'verify-language')
    Invoke-DotNet @($tests,'--denoise-smoke',(Join-Path $fixture 'ja-lunch.wav'))
    foreach ($relative in @('models\gtcrn.onnx','speech-host\sherpa-onnx-c-api.dll')) {
        $buildFile = Join-Path (Split-Path $tests -Parent) $relative
        if ((Get-FileHash $buildFile).Hash -ne (Get-FileHash (Join-Path $package $relative)).Hash) {
            throw "Tested native component does not match release: $relative"
        }
    }
    Copy-Item (Join-Path $workspace 'release\START-HERE.txt') (Join-Path $package 'START-HERE.txt')
    Copy-Item (Join-Path $workspace 'release\RELEASE-NOTES.md') (Join-Path $package 'RELEASE-NOTES.md')
    Copy-Item (Join-Path $workspace 'release\Start-Transcription-Service.cmd') $package
    Set-Content (Join-Path $package 'build.ready') 'Ready' -Encoding Ascii

    # Hash every shipped file. Verification also decompresses every ZIP entry so
    # successful packaging is not mistaken for a working archive.
    $files = @(Get-ChildItem $package -Recurse -File | Sort-Object FullName)
    $manifest = foreach ($file in $files) {
        $relative = $file.FullName.Substring($package.Length + 1).Replace('\','/')
        (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $relative
    }
    Set-Content (Join-Path $package 'SHA256SUMS.txt') $manifest -Encoding Ascii
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($package,$archive,[IO.Compression.CompressionLevel]::Optimal,$true)
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $entries = @($zip.Entries | Where-Object { $_.Name -ne '' })
        if ($entries.Count -ne $files.Count + 1) { throw 'Archive file count differs from the package.' }
        foreach ($entry in $entries) {
            $relative = $entry.FullName.Substring($name.Length + 1).Replace('/','\')
            if ($entry.FullName -ne "$name/" + $relative.Replace('\','/') -or $relative.Contains('..')) { throw 'Unexpected archive path.' }
            $stream = $entry.Open()
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','') }
            finally { $sha.Dispose(); $stream.Dispose() }
            if ($hash -ne (Get-FileHash -LiteralPath (Join-Path $package $relative)).Hash) { throw "Archive verification failed: $relative" }
        }
    }
    finally { $zip.Dispose() }
    $archiveHash = (Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant()
    Set-Content "$archive.sha256" ($archiveHash + '  ' + [IO.Path]::GetFileName($archive)) -Encoding Ascii
    $bytes = (Get-ChildItem $package -File -Recurse | Measure-Object Length -Sum).Sum
    $report = "Release: $name`nFiles: $($files.Count + 1)`nUnpacked bytes: $bytes`nZIP bytes: $((Get-Item $archive).Length)`nSHA-256: $archiveHash`nAll archive entries decompressed and hash-verified.`n"
    Set-Content (Join-Path $qa 'RESULTS.txt') $report -Encoding UTF8
    Write-Output $report
    Write-Output "Ready to distribute: $archive"
}
finally {
    Stop-Transcript -ErrorAction SilentlyContinue | Out-Null
    Pop-Location
}
