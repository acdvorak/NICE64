$ErrorActionPreference = 'Stop'

$ProjectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$BuildDir = Join-Path $ProjectRoot 'build-pico2w'

if ([string]::IsNullOrWhiteSpace($env:PICO_SDK_PATH)) {
    throw 'Set PICO_SDK_PATH to your local Pico SDK directory.'
}

$CmakeArgs = @(
    '-S', $ProjectRoot, '-B', $BuildDir, '-G', 'Ninja',
    '-DCMAKE_BUILD_TYPE=Release', '-DPICO_BOARD=pico2_w',
    '-DNICE64_UNIVERSAL_CONTROLLER_DIAGNOSTICS_NETWORK=1',
    '-DNICE_RX_BACKEND_PIO=1', '-DPICO_COPY_TO_RAM=OFF')
if ($env:PIOASM_DIR) { $CmakeArgs += "-Dpioasm_DIR=$env:PIOASM_DIR" }
if ($env:PICOTOOL_DIR) { $CmakeArgs += "-Dpicotool_DIR=$env:PICOTOOL_DIR" }
& cmake @CmakeArgs

if ($LASTEXITCODE -ne 0) { throw 'CMake configuration failed.' }

cmake --build $BuildDir
if ($LASTEXITCODE -ne 0) { throw 'Firmware build failed.' }
