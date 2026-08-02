# Build the Bridge

Requirements: Windows, .NET SDK 9, PowerShell 5.1 or later, and internet access for NuGet restore.

From `bridge-application/nice64-bridge-v1.4.0/source`:

```powershell
dotnet restore PicoSwitchController.csproj
dotnet build PicoSwitchController.csproj -c Release
dotnet publish PicoSwitchController.csproj -c Release -o ..\publish
```

The project targets `net9.0-windows`. Copy `picoswitchcontroller.ini` and the `modules` directory beside the published executable. The distributed runtime is located at `bridge-application/nice64-bridge-v1.4.0/application/PicoSwitchController.exe`.

The publish output is the runtime package; do not validate a distribution by running from a source `bin` directory.

The public Bridge release is v1.4.0. The validated internal runtime used for this release is recorded in [BUILD_MANIFEST.md](BUILD_MANIFEST.md).

Use `Get-FileHash -Algorithm SHA256 <file>` to verify files against `CHECKSUMS_SHA256.txt`.
