# Developer-only native rebuild. Normal .NET builds use the checked-in DLLs.
# All downloads are pinned and verified; no system software is installed.
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$nativeWork = Join-Path $projectRoot 'artifacts/openvino-integration'
New-Item -ItemType Directory -Force $nativeWork | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-PinnedZip($url, $name, $sha, $folder, $entry) {
    $archive = Join-Path $nativeWork $name
    if (!(Test-Path $archive) -or (Get-FileHash $archive -Algorithm SHA256).Hash -ne $sha) {
        Invoke-WebRequest $url -OutFile ($archive + '.partial')
        if ((Get-FileHash ($archive + '.partial') -Algorithm SHA256).Hash -ne $sha) { throw "Checksum mismatch: $name" }
        Move-Item -LiteralPath ($archive + '.partial') -Destination $archive -Force
    }
    $destination = Join-Path $nativeWork $folder
    if (!(Test-Path (Join-Path $destination $entry))) {
        New-Item -ItemType Directory -Force $destination | Out-Null
        [IO.Compression.ZipFile]::ExtractToDirectory($archive, $destination)
    }
}

# Apply our small reviewed session patch without requiring a Git installation.
function Apply-SessionPatch($file, $patchFile) {
    $code = [IO.File]::ReadAllText($file).Replace("`r`n", "`n")
    if ($code.Contains('BlueJaysGetOpenVinoOptionCount')) { return }
    $lines = [IO.File]::ReadAllLines($patchFile)
    for ($i = 0; $i -lt $lines.Length; $i++) {
        if (!$lines[$i].StartsWith('@@')) { continue }
        $old = [Collections.Generic.List[string]]::new(); $new = [Collections.Generic.List[string]]::new()
        for ($i++; $i -lt $lines.Length -and !$lines[$i].StartsWith('@@'); $i++) {
            $line = $lines[$i]
            if ($line.Length -eq 0) { continue }
            if ($line.StartsWith(' ') -or $line.StartsWith('-')) { $old.Add($line.Substring(1)) }
            if ($line.StartsWith(' ') -or $line.StartsWith('+')) { $new.Add($line.Substring(1)) }
        }
        $i--
        $before = ($old -join "`n") + "`n"; $after = ($new -join "`n") + "`n"
        $at = $code.IndexOf($before, [StringComparison]::Ordinal)
        if ($at -lt 0 -or $code.IndexOf($before, $at + 1, [StringComparison]::Ordinal) -ge 0) { throw 'Pinned sherpa session patch did not match uniquely.' }
        $code = $code.Substring(0, $at) + $after + $code.Substring($at + $before.Length)
    }
    [IO.File]::WriteAllText($file, $code)
}
function Replace-Source($file, $before, $after) {
    $code = [IO.File]::ReadAllText($file)
    $changed = $code.Replace($before, $after)
    if ($changed -ne $code) { [IO.File]::WriteAllText($file, $changed) }
}

Get-PinnedZip 'https://github.com/mstorsjo/llvm-mingw/releases/download/20261104/llvm-mingw-20261104-ucrt-x86_64.zip' 'llvm-mingw.zip' '552191B5754C49A5A20723276A6FC56541EA1922C4C50B7B669022333FC1B40F' 'tools' 'llvm-mingw-20261104-ucrt-x86_64/bin/clang.exe'
Get-PinnedZip 'https://github.com/Kitware/CMake/releases/download/v4.4.5/cmake-4.4.5-windows-x86_64.zip' 'cmake.zip' '326C826486D46A23CF9D5A1C9EE3A567D427BDD02C4A34071937A2A675CFF004' 'tools' 'cmake-4.4.5-windows-x86_64/bin/cmake.exe'
Get-PinnedZip 'https://github.com/ninja-build/ninja/releases/download/v1.13.1/ninja-win.zip' 'ninja.zip' '26A40FA8595694DEC2FAD4911E62D29E10525D2133C9A4230B66397774AE25BF' 'tools' 'ninja.exe'
Get-PinnedZip 'https://github.com/k2-fsa/sherpa-onnx/archive/refs/tags/v2.0.1.zip' 'sherpa-source.zip' '71397FBF7C30CF81A56247E4C45BD3748ABB590E761FBCFA2989A33CA7644282' 'source' 'sherpa-onnx-2.0.1/CMakeLists.txt'
Get-PinnedZip 'https://api.nuget.org/v3-flatcontainer/intel.ml.onnxruntime.openvino/1.24.1/intel.ml.onnxruntime.openvino.1.24.1.nupkg' 'legacy-openvino.zip' 'F53AD5F90E3D616970A5C65E4880EBBE92C9774E9727020661DB591CEA74A110' 'legacy-openvino' 'build/native/include/onnxruntime_cxx_api.h'

$source = Join-Path $nativeWork 'source/sherpa-onnx-2.0.1'
$build = Join-Path $nativeWork 'build'
$compiler = Join-Path $nativeWork 'tools/llvm-mingw-20261104-ucrt-x86_64'
$cmake = Join-Path $nativeWork 'tools/cmake-4.4.5-windows-x86_64/bin/cmake.exe'
$env:SHERPA_ONNXRUNTIME_INCLUDE_DIR = Join-Path $nativeWork 'legacy-openvino/build/native/include'
$env:SHERPA_ONNXRUNTIME_LIB_DIR = Join-Path $nativeWork 'legacy-openvino/runtimes/win-x64/native'
Apply-SessionPatch (Join-Path $source 'sherpa-onnx/csrc/session.cc') (Join-Path $PSScriptRoot 'native/sherpa-openvino.patch')
Replace-Source (Join-Path $source 'sherpa-onnx/csrc/parse-options.h') 'setlinebuf(stderr);' 'setvbuf(stderr, nullptr, _IOLBF, BUFSIZ);'
# Preserve CPU KV-cache decoding. Whole-graph OpenVINO produced empty Qwen text.
Replace-Source (Join-Path $source 'sherpa-onnx/csrc/offline-qwen3-asr-model.cc') 'sess_opts_decoder_(GetSessionOptions(config)),' 'sess_opts_decoder_(GetSessionOptions(config.num_threads, config.provider.rfind("openvino:", 0) == 0 ? "cpu" : config.provider)),'
# The small convolution frontend is cheaper and more compatible on CPU; the
# substantial audio encoder remains on the selected OpenVINO device.
Replace-Source (Join-Path $source 'sherpa-onnx/csrc/offline-qwen3-asr-model.cc') 'sess_opts_conv_(GetSessionOptions(config)),' 'sess_opts_conv_(GetSessionOptions(config.num_threads, config.provider.rfind("openvino:", 0) == 0 ? "cpu" : config.provider)),'

$arguments = @('-S', $source, '-B', $build, '-G', 'Ninja',
    "-DCMAKE_MAKE_PROGRAM=$(Join-Path $nativeWork 'tools/ninja.exe')",
    "-DCMAKE_C_COMPILER=$compiler/bin/x86_64-w64-mingw32-clang.exe",
    "-DCMAKE_CXX_COMPILER=$compiler/bin/x86_64-w64-mingw32-clang++.exe",
    '-DCMAKE_BUILD_TYPE=Release', '-DBUILD_SHARED_LIBS=ON', '-DSHERPA_ONNX_ENABLE_GPU=ON',
    '-DCMAKE_CXX_FLAGS=-fsized-deallocation -D_USE_MATH_DEFINES -DM_LN2=0.69314718055994530942',
    '-DCMAKE_SHARED_LINKER_FLAGS=-Wl,--export-all-symbols', '-DSBPE_COMPILER_SUPPORTS_CXX14=TRUE',
    '-DSHERPA_ONNX_ENABLE_BINARY=OFF', '-DSHERPA_ONNX_BUILD_C_API_EXAMPLES=OFF',
    '-DSHERPA_ONNX_ENABLE_PORTAUDIO=OFF', '-DSHERPA_ONNX_ENABLE_WEBSOCKET=OFF',
    '-DSHERPA_ONNX_ENABLE_TTS=OFF', '-DSHERPA_ONNX_ENABLE_SPEAKER_DIARIZATION=OFF')
& $cmake @arguments
if ($LASTEXITCODE -ne 0) { throw 'Native configuration failed.' }

# Portability repairs for this pinned upstream release and LLVM's standard library.
Replace-Source (Join-Path $build '_deps/kaldifst-src/kaldifst/csrc/basic-filebuf.h') 'fseek(_M_file_,' 'fseek(_M_file,'
$textUtils = Join-Path $build '_deps/kaldifst-src/kaldifst/csrc/text-utils.h'
$text = [IO.File]::ReadAllText($textUtils)
if (!$text.Contains('#include <cstdlib>')) { [IO.File]::WriteAllText($textUtils, "#include <cstdlib>`n" + $text) }
Replace-Source (Join-Path $build '_deps/simple-sentencepiece-src/ssentencepiece/csrc/threadpool.h') 'std::result_of<F(Args...)>' 'std::invoke_result<F, Args...>'
foreach ($folder in @($source, (Join-Path $build '_deps'))) {
    Get-ChildItem -LiteralPath $folder -Recurse -File | Where-Object { $_.Name -eq 'CMakeLists.txt' -or $_.Extension -eq '.cmake' } | ForEach-Object {
        $text = [IO.File]::ReadAllText($_.FullName); $changed = [regex]::Replace($text, '/wd\d+|/bigobj', '')
        if ($changed -ne $text) { [IO.File]::WriteAllText($_.FullName, $changed) }
    }
}
& $cmake --build $build --target sherpa-onnx-c-api -j 4
if ($LASTEXITCODE -ne 0) { throw 'Native compilation failed.' }
$destination = Join-Path $projectRoot 'native/win-x64'
New-Item -ItemType Directory -Force $destination | Out-Null
Copy-Item (Join-Path $build 'bin/libsherpa-onnx-c-api.dll') (Join-Path $destination 'sherpa-onnx-c-api.dll') -Force
foreach ($name in @('libc++.dll', 'libunwind.dll')) { Copy-Item (Join-Path $compiler "x86_64-w64-mingw32/bin/$name") $destination -Force }
Write-Host 'Native speech runtime ready. Run scripts/Build.cmd to package the application.'
