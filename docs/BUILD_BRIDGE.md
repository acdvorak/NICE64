# Build the Bridge

Requirements: Windows, .NET SDK 9, PowerShell 5.1 or later, and internet access for NuGet restore.

From `bridge-application/nice64-bridge-v1.6.0/source`:

```powershell
dotnet restore PicoSwitchController.csproj
dotnet build PicoSwitchController.csproj -c Release
dotnet publish PicoSwitchController.csproj -c Release -o ..\publish
```

The project targets `net9.0-windows`. Copy the `modules`, `Assets`, mapping, and
runtime dependency content beside the published executable. Do not publish a
developer INI, logs, PDBs, or Browser session/request data. The application
creates user-local mutable settings as needed.

The distributed candidate is located at
`bridge-application/nice64-bridge-v1.6.0/application/PicoSwitchController.exe`.

The publish output is the runtime package; do not validate a distribution by running from a source `bin` directory.

The current Bridge candidate is v1.6.0. Normal Product Quick Mode operation has
passed available real-N64 validation. Reconnect/session-renewal and TCP↔Quick
handoff remain scheduled for additional lifecycle testing; this is not a
confirmed persistent defect. Build identity and the corresponding firmware
artifact are recorded in [BUILD_MANIFEST.md](BUILD_MANIFEST.md).

Use `Get-FileHash -Algorithm SHA256 <file>` to verify files against `CHECKSUMS_SHA256.txt`.
