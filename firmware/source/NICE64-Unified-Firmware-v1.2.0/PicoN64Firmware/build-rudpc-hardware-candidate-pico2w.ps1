$ErrorActionPreference = 'Stop'

$ProjectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$BuildDir = Join-Path $ProjectRoot 'build-rudpc-pico2w'
if ([string]::IsNullOrWhiteSpace($env:PICO_SDK_PATH)) {
    throw 'Set PICO_SDK_PATH to your local Pico SDK directory.'
}

$CmakeArgs = @(
    '-S', $ProjectRoot, '-B', $BuildDir, '-G', 'Ninja',
    '-DCMAKE_BUILD_TYPE=Release', '-DPICO_BOARD=pico2_w',
    '-DNICE64_UNIVERSAL_CONTROLLER_DIAGNOSTICS_NETWORK=1',
    '-DNICE_RX_BACKEND_PIO=1', '-DNICE64_RUDPC_HARDWARE_CANDIDATE=1',
    '-DPICO_COPY_TO_RAM=OFF')
if ($env:PIOASM_DIR) { $CmakeArgs += "-Dpioasm_DIR=$env:PIOASM_DIR" }
if ($env:PICOTOOL_DIR) { $CmakeArgs += "-Dpicotool_DIR=$env:PICOTOOL_DIR" }
& cmake @CmakeArgs
if ($LASTEXITCODE -ne 0) { throw 'CMake configuration failed.' }

cmake --build $BuildDir
if ($LASTEXITCODE -ne 0) { throw 'Firmware build failed.' }

$Cache = Get-Content -LiteralPath (Join-Path $BuildDir 'CMakeCache.txt') -Raw
foreach ($Required in @(
    'NICE64_UNIVERSAL_CONTROLLER_DIAGNOSTICS_NETWORK:STRING=1',
    'NICE_RX_BACKEND_PIO:STRING=1',
    'NICE64_RUDPC_HARDWARE_CANDIDATE:STRING=1')) {
    if (-not $Cache.Contains($Required)) { throw "Candidate cache assertion failed: $Required" }
}

$Elf = Join-Path $BuildDir 'pico_n64_controller_bridge.elf'
$StringsTool = if ($env:ARM_NONE_EABI_STRINGS) { $env:ARM_NONE_EABI_STRINGS } else { 'arm-none-eabi-strings' }
$Strings = & $StringsTool $Elf
foreach ($RequiredString in @('REQUEST_MODE NETWORK', 'UNIVERSAL_FIRMWARE')) {
    if (-not ($Strings -match [regex]::Escape($RequiredString))) {
        throw "Candidate ELF personality assertion failed: $RequiredString"
    }
}

Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $BuildDir 'pico_n64_controller_bridge.uf2')
