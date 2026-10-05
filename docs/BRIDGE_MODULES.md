# Bridge Modules

## Overview

Modules provide application resources while Bridge Core owns serial/Pico transport, Network Pak packet parsing, stream IDs, offsets, CRC, chunking, and write acknowledgements. Modules must not alter protocol framing.

External modules execute inside the Bridge process and are trusted local code.

## Package Location

Built-in packages are located under:

```text
bridge-application/nice64-bridge-v1.8.0/application/modules
```

External packages use one immediate subdirectory per module:

```text
bridge-application/
  nice64-bridge-v1.8.0/
    application/
      PicoSwitchController.exe
      PicoSwitchController.dll
      modules/
        sample-module/
          module.json
          bin/
            SampleModule.dll
            SampleModule.deps.json
```

Discovery is non-recursive. A Bridge restart is required after adding, removing, or replacing a module.

Built-in module IDs are reserved and take precedence over external duplicates.

## Discovery Rules

At startup, the Bridge:

1. Loads built-in package manifests.
2. Scans each immediate subdirectory of `modules`.
3. Looks for `module.json`.
4. Resolves `assemblyPath`, or uses the first top-level DLL under `bin`.
5. Loads the requested `moduleType`, or discovers a compatible concrete type.
6. Validates `protocolVersion` when present.
7. Logs successful loads and controlled skip reasons.

A malformed or incompatible package is skipped without terminating the Bridge.

## Module Contract

External modules implement:

```csharp
PicoSwitchController.INetworkPakModule
```

Contract:

```csharp
string Id { get; }
string DisplayName { get; }

NetworkPakResource? OpenResource(
    string resource,
    ModuleContext context);

NetworkPakWriteResult HandleWrite(
    NetworkPakWriteChunk chunk,
    ModuleContext context);
```

`NetworkPakResource` is:

```csharp
public sealed record NetworkPakResource(
    string Name,
    byte[] Data);
```

Return `null` from `OpenResource` when the module does not own the requested resource.

`ModuleContext` supplies:

- `BaseDirectory`
- `Log`
- `BridgeVersion`
- `MaxPayloadLength`
- `MaxGeneratedPageBytes`
- `DefaultMessage`

Writes arrive as:

```csharp
NetworkPakWriteChunk(
    byte Flags,
    byte[] Data)
```

Convenience helpers include:

- `IsFirst`
- `IsLast`
- `GetUtf8Text()`

Return:

```csharp
NetworkPakWriteResult.Ack(payload)
```

for a supported successful write, or:

```csharp
NetworkPakWriteResult.Fail(error)
```

for a controlled failure.

## Manifest

Every package requires `module.json`.

```json
{
  "id": "sample-module",
  "displayName": "Sample Module",
  "version": "1.0.0",
  "resourcePrefix": "sample://",
  "assemblyPath": "bin/SampleModule.dll",
  "moduleType": "SampleModule.SampleNetworkPakModule"
}
```

Optional `protocolVersion`, when present, must match the Bridge protocol version.

The module class may provide either:

- a public parameterless constructor; or
- a public constructor accepting `ModulePackage`.

## Minimal Project File

Create a class-library project targeting the same framework as the Bridge:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <Reference Include="PicoSwitchController">
        <HintPath>..\..\..\application\PicoSwitchController.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

Adjust the relative `HintPath` to match the exported package layout. Do not reference a private absolute path.

## Minimal Module Example

```csharp
using System.Text;
using PicoSwitchController;

namespace SampleModule;

public sealed class SampleNetworkPakModule : INetworkPakModule
{
    public string Id => "sample-module";
    public string DisplayName => "Sample Module";

    public NetworkPakResource? OpenResource(
        string resource,
        ModuleContext context)
    {
        if (!resource.Equals(
                "sample://hello",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        context.Log("Sample module handled sample://hello.");

        byte[] data = Encoding.UTF8.GetBytes(
            "Sample module is running.");

        return new NetworkPakResource(
            "sample-hello.txt",
            data);
    }

    public NetworkPakWriteResult HandleWrite(
        NetworkPakWriteChunk chunk,
        ModuleContext context)
    {
        context.Log(
            $"Sample module received write: " +
            $"first={chunk.IsFirst}, last={chunk.IsLast}, " +
            $"bytes={chunk.Data.Length}");

        return NetworkPakWriteResult.Fail(
            "This sample module does not support writes.");
    }
}
```

This example registers no core route and handles only `sample://hello`.

## Build

From the module project directory:

```powershell
dotnet restore
dotnet build -c Release
```

Copy the resulting DLL and any private dependencies to:

```text
bridge-application/nice64-bridge-v1.8.0/application/modules/sample-module/bin
```

Copy `module.json` to:

```text
bridge-application/nice64-bridge-v1.8.0/application/modules/sample-module/module.json
```

Restart the Bridge.

## Verification

Review the Bridge log for:

```text
External module loaded
```

or a controlled skip reason.

Then request the module-owned resource through the normal Network Pak request path:

```text
sample://hello
```

Expected result:

```text
Sample module is running.
```

## Logging

Use `context.Log` for concise diagnostics.

Do not:

- write logs to hardcoded absolute paths;
- log credentials;
- dump unbounded request bodies;
- duplicate high-frequency transport logging.

## Failure Handling

The Bridge logs and skips packages with:

- missing or invalid manifests;
- missing assemblies;
- unresolved dependencies;
- interface mismatches;
- unsupported constructors;
- duplicate or reserved IDs;
- protocol-version mismatches;
- constructor failures.

Request-time failures should be returned as controlled module results rather than escaping as unhandled exceptions.

## Security

Modules run within the Bridge trust boundary.

Do not:

- interpolate untrusted input into PowerShell or shell commands;
- embed credentials;
- use machine-specific absolute paths;
- overwrite core routes;
- bypass payload validation;
- disable transport or protocol checks;
- load arbitrary code from network locations.

## Compatibility Rules

Modules must not change:

- Network Pak wire bytes;
- framing;
- ACK semantics;
- stream chunking;
- CRC behavior;
- firmware assumptions;
- NIMG format;
- core Browser Live behavior;
- built-in module IDs without coordinated changes.

## Disable or Remove

1. Stop the Bridge.
2. Remove or rename the external package directory.
3. Restart the Bridge.
4. Confirm the log no longer reports the module.

Built-in modules are compiled into the application and are not removed through this mechanism.

## Troubleshooting

### Module is not discovered

Confirm:

- `module.json` is at the package root;
- the package is an immediate child of `modules`;
- the scan is non-recursive;
- the Bridge was restarted.

### Assembly cannot be loaded

Confirm:

- target framework is `net9.0-windows`;
- the Bridge contract assembly version is compatible;
- private dependencies are copied into `bin`;
- `assemblyPath` is relative to the package root.

### Module type is not found

Confirm `moduleType` contains the fully qualified concrete class name and that the class is public.

### Duplicate module ID

Choose a unique ID. Built-in IDs always win.

### Resource returns no data

Confirm:

- the request matches the module's resource prefix;
- `OpenResource` returns a `NetworkPakResource`;
- the generated data does not exceed `context.MaxGeneratedPageBytes`;
- the log shows the request reached the module.

### A request throws

Catch expected failures and return `NetworkPakWriteResult.Fail(...)` or a controlled resource response. Review the Bridge log for the exception and package identity.
