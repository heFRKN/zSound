# Builds the microphone plugin (native/zsapo.dll) and publishes zSound.exe to ./dist
# Requirements: .NET 8 SDK, Zig 0.13 (zig on PATH, $env:ZIG, or `pip install ziglang==0.13.0`)
param([switch]$DebugLog)   # -DebugLog: plugin writes %ProgramData%\zSound\apo.log (development only)

$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $zig = $env:ZIG
    if (-not $zig) { $zig = (Get-Command zig -ErrorAction SilentlyContinue).Source }
    if (-not $zig) {
        $py = (Get-Command python -ErrorAction SilentlyContinue).Source
        if ($py) { $zig = & $py -c "import ziglang, os; print(os.path.join(os.path.dirname(ziglang.__file__), 'zig.exe'))" 2>$null }
    }
    if (-not $zig -or -not (Test-Path $zig)) { throw 'Zig 0.13 not found. Install it or run: pip install ziglang==0.13.0' }

    # -nostdlib ile zig Windows başlıklarını kendisi eklemiyor: kendi mingw başlıklarını göster
    $inc = Join-Path (Split-Path $zig) 'lib/libc/include/any-windows-any'
    $flags = @('c++', 'native/zsapo.cpp', 'native/runtime.cpp', '-isystem', $inc, '-target', 'x86_64-windows-gnu', '-shared', '-O2', '-s',
               '-fno-exceptions', '-fno-rtti', '-fno-stack-protector', '-fno-threadsafe-statics', '-nostdlib',
               '-Wl,--entry,DllMain', '-lkernel32', '-lole32', '-lucrtbase', '-o', 'native/zsapo.dll')
    if ($DebugLog) { $flags += '-DZS_LOG' }
    & $zig @flags
    if ($LASTEXITCODE -ne 0) { throw 'Native build failed' }
    Remove-Item native/zsapo.lib, native/zsapo.pdb -ErrorAction SilentlyContinue

    dotnet publish -c Release -o dist
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
    Write-Host "OK -> dist\zSound.exe"
} finally { Pop-Location }
