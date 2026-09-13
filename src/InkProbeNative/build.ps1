# Builds the native C++ twin of the prototype.
#
# It only needs a C++ compiler that can target Windows. Any of these works:
#   * Visual Studio 2022 "Desktop development with C++"  -> cl.exe
#   * Zig (portable, no install, no admin)               -> zig c++
#
# With Zig:
#   curl.exe -L -o zig.zip https://ziglang.org/download/0.15.2/zig-x86_64-windows-0.15.2.zip
#   Expand-Archive zig.zip -DestinationPath .
#   .\zig\zig.exe c++ -target x86_64-windows-gnu -O2 -std=c++17 main.cpp -o inkprobe_native.exe `
#       -ld3d11 -ld2d1 -ldcomp -ldxgi -ldwrite -lole32 -luser32 -lgdi32 -lpsapi

param(
    [string]$Zig = "$env:TEMP\zigprobe\zig\zig.exe"
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$out  = Join-Path $here 'inkprobe_native.exe'

if (Test-Path $Zig) {
    Write-Host "building with zig: $Zig"
    & $Zig c++ -target x86_64-windows-gnu -O2 -std=c++17 (Join-Path $here 'main.cpp') -o $out `
        -ld3d11 -ld2d1 -ldcomp -ldxgi -ldwrite -lole32 -luser32 -lgdi32 -lpsapi
}
else {
    Write-Host "zig not found at $Zig; trying cl.exe"
    & cl.exe /nologo /O2 /std:c++17 /EHsc (Join-Path $here 'main.cpp') /Fe:$out `
        d3d11.lib d2d1.lib dcomp.lib dxgi.lib dwrite.lib ole32.lib user32.lib gdi32.lib psapi.lib
}

if (Test-Path $out) { Write-Host "built: $out" } else { Write-Error "build failed" }
