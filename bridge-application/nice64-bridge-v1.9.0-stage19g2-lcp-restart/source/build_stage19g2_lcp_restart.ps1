param(
    [string]$OutputRoot = "C:\Users\tcantu\Documents\Codex\GIT\NICE64\bridge-application\nice64-bridge-v1.9.0-stage19g2-lcp-restart\application",
    [string]$Stage19dApplication = "C:\Users\tcantu\Documents\Codex\GIT\NICE64\bridge-application\nice64-bridge-v1.9.0-stage19d-final\application"
)

$ErrorActionPreference = "Stop"
$sourceRoot = $PSScriptRoot
$configuration = "Release"
$framework = "net9.0-windows"

$coreHashes = @{
    "PicoSwitchController.exe" = "96AA0732F5192D8FC8B7E6632009C6F3D5591F403FF94E843CCD75416F8446C1"
    "PicoSwitchController.dll" = "6FE082744E4574223CFD127566309E753D416E62619AFCCE9491D73F10B1BCAB"
    "PicoSwitchController.deps.json" = "AAD64F24A17A477689BEE3763954C3C329C6736DEDA2167037295A5BC5C67267"
}

foreach ($name in $coreHashes.Keys) {
    $parent = Join-Path $Stage19dApplication $name
    $output = Join-Path $OutputRoot $name
    if (-not (Test-Path -LiteralPath $parent -PathType Leaf) -or
        -not (Test-Path -LiteralPath $output -PathType Leaf)) {
        throw "Missing core artifact: $name"
    }
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $parent).Hash -ne $coreHashes[$name] -or
        (Get-FileHash -Algorithm SHA256 -LiteralPath $output).Hash -ne $coreHashes[$name]) {
        throw "Stage 19D core identity mismatch before module build: $name"
    }
}

dotnet build "$sourceRoot\module-projects\Nice64.PppGateway\Nice64.PppGateway.csproj" -c $configuration
if ($LASTEXITCODE -ne 0) { throw "PPP Gateway module build failed." }

dotnet run --project "$sourceRoot\tests\DirectConnectionHandshakeHeadlessSmoke\DirectConnectionHandshakeHeadlessSmoke.csproj" -c $configuration
if ($LASTEXITCODE -ne 0) { throw "Direct handshake tests failed." }

dotnet run --project "$sourceRoot\tests\LcpRestartSchedulerHeadlessSmoke\LcpRestartSchedulerHeadlessSmoke.csproj" -c $configuration
if ($LASTEXITCODE -ne 0) { throw "LCP restart scheduler tests failed." }

$buildRoot = "$sourceRoot\module-projects\Nice64.PppGateway\bin\$configuration\$framework"
$sourcePackage = "$sourceRoot\modules\ppp-gateway"
$outputPackage = "$OutputRoot\modules\ppp-gateway"
foreach ($destination in @($sourcePackage, $outputPackage)) {
    if (-not (Test-Path -LiteralPath $destination)) {
        New-Item -ItemType Directory -Path $destination | Out-Null
    }
    foreach ($extension in @("dll", "deps.json", "pdb")) {
        Copy-Item -LiteralPath "$buildRoot\Nice64.PppGateway.$extension" -Destination $destination -Force
    }
    if ($destination -ne $sourcePackage) {
        Copy-Item -LiteralPath "$sourcePackage\module.json" -Destination $destination -Force
    }
}

dotnet run --project "$sourceRoot\tests\LiveBridgePppHeadlessSmoke\LiveBridgePppHeadlessSmoke.csproj" -c $configuration
if ($LASTEXITCODE -ne 0) { throw "Existing live PPP regression suite failed." }

foreach ($name in $coreHashes.Keys) {
    $output = Join-Path $OutputRoot $name
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $output).Hash -ne $coreHashes[$name]) {
        throw "Bridge core changed during module-only build: $name"
    }
}

$moduleDll = Join-Path $outputPackage "Nice64.PppGateway.dll"
$moduleManifest = Join-Path $outputPackage "module.json"
Write-Host "Stage 19G.2 PPP Gateway module staged to $outputPackage"
Write-Host "DLL SHA-256: $((Get-FileHash -Algorithm SHA256 -LiteralPath $moduleDll).Hash)"
Write-Host "Manifest SHA-256: $((Get-FileHash -Algorithm SHA256 -LiteralPath $moduleManifest).Hash)"
Write-Host "Stage 19D-final Bridge core hashes remain byte-identical."
