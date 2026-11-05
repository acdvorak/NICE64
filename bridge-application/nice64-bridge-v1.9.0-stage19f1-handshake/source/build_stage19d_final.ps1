param(
    [string]$OutputRoot = "C:\Users\tcantu\Documents\Codex\GIT\NICE64\bridge-application\nice64-bridge-v1.9.0-stage19d-final\application",
    [string]$AcceptedModuleRoot = "C:\Users\tcantu\Documents\Codex\GIT\NICE64\bridge-application\nice64-bridge-v1.9.0-stage19d-modular\application\modules"
)

$ErrorActionPreference = "Stop"
$sourceRoot = $PSScriptRoot

if (Test-Path -LiteralPath $OutputRoot) {
    throw "Refusing to overwrite existing Stage 19D final output: $OutputRoot"
}

dotnet publish "$sourceRoot\PicoSwitchController.csproj" `
    -c Release -r win-x64 --self-contained false -o $OutputRoot
if ($LASTEXITCODE -ne 0) { throw "Bridge core publish failed." }

# Stage 19D-FINAL is a core-only corrective rebuild. Preserve the accepted
# standalone service modules byte-for-byte instead of rebuilding them.
$expected = @{
    "serial-tunnel\Nice64.SerialTunnel.dll" = "061B98B6D5001E89F4E629B4AFE5F2BE2807F102EADDE4B9D3BA0BE283F8BF5E"
    "serial-tunnel\Nice64.SerialTunnel.deps.json" = "5A890C26203342953ABD3FA738131934566332095789A9DD315F1DBF29620DB1"
    "serial-tunnel\module.json" = "E0D158C15F2EB77D4801DDCB757899F9645D999CA1C10E0FBED235F6170EA3C9"
    "ppp-gateway\Nice64.PppGateway.dll" = "E52B058148C122E267E3CD6B37D451B34CEB9EF463649757BF04469169315647"
    "ppp-gateway\Nice64.PppGateway.deps.json" = "00CDE5AACBCE046A1700F31D30C5178B89B20C3AA7DA89C33274E07D7E88AAA5"
    "ppp-gateway\module.json" = "A516BC42B652D124C0C6C38E0ADF5DB2AE290A9D98BA505129BBB2F8252D4646"
}

foreach ($relative in $expected.Keys) {
    $source = Join-Path $AcceptedModuleRoot $relative
    $destination = Join-Path (Join-Path $OutputRoot "modules") $relative
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Accepted module artifact is missing: $source"
    }
    $sourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $source).Hash
    if ($sourceHash -ne $expected[$relative]) {
        throw "Accepted module artifact hash mismatch: $source"
    }
    $destinationDirectory = Split-Path -Parent $destination
    if (-not (Test-Path -LiteralPath $destinationDirectory)) {
        New-Item -ItemType Directory -Path $destinationDirectory | Out-Null
    }
    Copy-Item -LiteralPath $source -Destination $destination -Force
    $destinationHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $destination).Hash
    if ($destinationHash -ne $expected[$relative]) {
        throw "Staged module artifact hash mismatch: $destination"
    }
}

Write-Host "Stage 19D final Bridge published to $OutputRoot"
Write-Host "Accepted Serial Tunnel and PPP Gateway module identities preserved."
