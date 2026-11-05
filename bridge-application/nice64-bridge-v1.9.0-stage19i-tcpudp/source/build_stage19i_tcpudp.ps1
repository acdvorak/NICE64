param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'

$sourceRoot = $PSScriptRoot
$packageRoot = Split-Path -Parent $sourceRoot
$application = Join-Path $packageRoot 'application'
$project = Join-Path $sourceRoot 'module-projects\Nice64.PppGateway\Nice64.PppGateway.csproj'
$moduleOut = Join-Path $sourceRoot "module-projects\Nice64.PppGateway\bin\$Configuration\net9.0-windows"
$modulePackage = Join-Path $application 'modules\ppp-gateway'

dotnet build $project -c $Configuration --nologo
if ($LASTEXITCODE) { throw 'PPP Gateway build failed' }

$tests = @(
    'LocalTransportEndpointHeadlessSmoke',
    'LocalIpv4EndpointHeadlessSmoke',
    'LiveBridgePppHeadlessSmoke',
    'LcpRestartSchedulerHeadlessSmoke',
    'DirectConnectionHandshakeHeadlessSmoke',
    'NetworkPakLayer2Smoke',
    'SerialTunnelHeadlessSmoke',
    'KeyboardMouseStateTransportSmoke',
    'KeyboardMouseWindowsInputSmoke'
)
foreach ($name in $tests) {
    dotnet run --project (Join-Path $sourceRoot "tests\$name\$name.csproj") `
        -c $Configuration --no-restore
    if ($LASTEXITCODE) { throw "$name failed" }
}
dotnet run --project (Join-Path $sourceRoot 'tests\ModularDeploymentSmoke\ModularDeploymentSmoke.csproj') `
    -c $Configuration --no-restore -- --serial --ppp
if ($LASTEXITCODE) { throw 'ModularDeploymentSmoke failed' }

Copy-Item (Join-Path $moduleOut 'Nice64.PppGateway.dll') $modulePackage -Force
Copy-Item (Join-Path $moduleOut 'Nice64.PppGateway.deps.json') $modulePackage -Force
Copy-Item (Join-Path $moduleOut 'Nice64.PppGateway.pdb') $modulePackage -Force

$expectedCore = @{
    'PicoSwitchController.exe' = '96AA0732F5192D8FC8B7E6632009C6F3D5591F403FF94E843CCD75416F8446C1'
    'PicoSwitchController.dll' = '6FE082744E4574223CFD127566309E753D416E62619AFCCE9491D73F10B1BCAB'
    'PicoSwitchController.deps.json' = 'AAD64F24A17A477689BEE3763954C3C329C6736DEDA2167037295A5BC5C67267'
}
foreach ($entry in $expectedCore.GetEnumerator()) {
    $actual = (Get-FileHash (Join-Path $application $entry.Key) -Algorithm SHA256).Hash
    if ($actual -ne $entry.Value) { throw "Bridge core identity changed: $($entry.Key)" }
}

Write-Host 'STAGE 19I MODULE BUILD / REGRESSION PASS'
