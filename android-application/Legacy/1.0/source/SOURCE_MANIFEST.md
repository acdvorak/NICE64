# NICE64 Android frozen source snapshot

This directory is a byte-preserving source snapshot of the accepted NICE64
Android companion project. The snapshot was prepared on 2026-10-04 from the
frozen accepted Android development baseline documented by the release audit.

## Identity

- Version code: `1`
- Version name: `0.1.0-dev`
- Accepted APK SHA-256:
  `28D9E16DBD94E4FC07B32033DE84C621DE50E2A60C7F8D0D2BD87A5E0BF897CC`
- Source files copied byte-for-byte: `130`
- Missing intended source files: `0`
- Mismatched copied source files: `0`

The manifest itself is release documentation added after the 130 project files
were verified.

## Accepted validation

- JVM tests: 327/327 PASS
- Connected instrumentation: 81/81 PASS
- Android lint: PASS
- Physical freeze: PASS

## Build

Install Android SDK Platform 36 and Build Tools 36.0.0, then configure the SDK
location locally outside this snapshot. From this directory run:

```powershell
.\gradlew.bat --no-daemon :core:test :app:testDebugUnitTest :app:lint :app:assembleDebug
```

The debug APK is produced at `app/build/outputs/apk/debug/app-debug.apk`.

## Exclusions

The snapshot intentionally excludes generated builds and APKs, Gradle/Kotlin
caches, IDE state, `local.properties`, machine-local SDK paths, diagnostic
capture tooling and outputs, traces, runtime logs, test-result output,
screenshots, pairing/runtime data, credentials, certificates/private keys,
keystores, and temporary files. The accepted APK remains one level above this
directory as `NICE64-Android-Companion.apk`.

No signing key or production signing instructions are included. Repository
licensing terms apply; no separate Android license was created for this export.
