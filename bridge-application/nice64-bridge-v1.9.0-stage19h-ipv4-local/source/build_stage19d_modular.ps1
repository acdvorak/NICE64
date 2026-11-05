param(
    [string]$OutputRoot = "C:\Users\tcantu\Documents\Codex\GIT\NICE64\bridge-application\nice64-bridge-v1.9.0-stage19d-modular\application"
)

$ErrorActionPreference = "Stop"
$sourceRoot = $PSScriptRoot
$configuration = "Release"
$framework = "net9.0-windows"

dotnet build "$sourceRoot\module-projects\Nice64.SerialTunnel\Nice64.SerialTunnel.csproj" -c $configuration
if ($LASTEXITCODE -ne 0) { throw "Serial Tunnel module build failed." }
dotnet build "$sourceRoot\module-projects\Nice64.PppGateway\Nice64.PppGateway.csproj" -c $configuration
if ($LASTEXITCODE -ne 0) { throw "PPP Gateway module build failed." }

$modules = @(
    @{ Name = "Nice64.SerialTunnel"; Package = "serial-tunnel" },
    @{ Name = "Nice64.PppGateway"; Package = "ppp-gateway" }
)
foreach ($module in $modules) {
    $buildRoot = "$sourceRoot\module-projects\$($module.Name)\bin\$configuration\$framework"
    $packageRoot = "$sourceRoot\modules\$($module.Package)"
    foreach ($extension in @("dll", "deps.json", "pdb")) {
        Copy-Item -LiteralPath "$buildRoot\$($module.Name).$extension" -Destination $packageRoot -Force
    }
}

dotnet publish "$sourceRoot\PicoSwitchController.csproj" -c $configuration -r win-x64 --self-contained false -o $OutputRoot
if ($LASTEXITCODE -ne 0) { throw "Bridge core publish failed." }
