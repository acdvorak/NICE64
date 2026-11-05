# NICE64 Android accepted source snapshot

This directory contains the accepted post-G2 NICE64 Android companion source,
now targeting version `1.2.0` (version code `2`). It was
synchronized from the authoritative Keyboard/Mouse development tree without
generated builds, Gradle/Kotlin caches, local SDK paths, runtime credentials,
diagnostic output, or signing material.

## Accepted artifact

`../NICE64-Android-Companion.apk`

The locally built debug artifact identifies as version `1.2.0` (version code
`2`) and is signed by one Android Debug signer using APK Signature Scheme v2.

SHA-256:

`3A54F6DC39F6477D8026BF371A467C91FB67A793BB771E92003257491C7177F8`

## Capabilities and validation

The snapshot includes the accepted Controller client, authenticated Bridge
management, direct RandNET Keyboard/N64 Mouse UDP input, physical and on-screen
providers, lifecycle neutralization, and persistent Mouse Invert X/Invert Y.

- v1.2.0 JVM tests: 338/338 PASS
- Android lint: PASS
- Debug and unsigned release builds: PASS
- Prior accepted connected instrumentation baseline: 81/81 PASS
- G2 direct N64 Mouse / Mario Artist hardware validation: PASS

The packaged APK is debug-signed for manual testing. No production signing
identity or private key is included.

## Build

Install Android SDK Platform 36 and Build Tools 36.0.0, configure the SDK path
outside this repository, and run:

```powershell
.\gradlew.bat --no-daemon test lintDebug assembleDebug assembleRelease
```

Generated output remains excluded by the repository `.gitignore`.
