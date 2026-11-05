# NICE64 Android Client

Source and packaged debug APK version: `1.2.0` (`versionCode` 2).

This accepted Android companion project contains two modules:

- `core`: pure Kotlin/JVM domain models and the compatible `latest-state-v1`
  plus typed mobile-input encoders.
- `app`: the single-activity Jetpack Compose client, including authenticated
  Bridge management, Controller input, RandNET Keyboard, N64 Mouse, physical
  Android input, and on-screen controls.

Bridge management uses authenticated pinned HTTPS. Latency-sensitive
Controller, Keyboard, and Mouse state uses direct Wi-Fi UDP to Pico. Mouse
preferences use Preferences DataStore; Android pointer Y is normalized before
optional Invert X/Invert Y and cumulative movement accounting.

## Toolchain

- Android Gradle Plugin 9.4.0
- Gradle 9.6.0
- Kotlin 2.3.21
- Java 17
- minSdk 26
- compileSdk/targetSdk 36 (latest stable platform; API 37 remains preview)
- Compose stable BOM 2026.06.00 (the latest stable BOM retained for API 36)

Run `gradlew.bat test assembleDebug assembleRelease` from this directory after
setting up Android SDK Platform 36 and Build Tools 36.0.0. The checked-in Gradle
wrapper controls Gradle itself.
