# NICE64 Android Client

This is the D8A Android project foundation. It contains exactly two modules:

- `core`: pure Kotlin/JVM immutable domain models and the frozen
  `latest-state-v1` byte encoder.
- `app`: a minimal single-activity Jetpack Compose development shell using
  ViewModel, StateFlow, and unidirectional state rendering.

D8A intentionally contains no HTTP client, TLS implementation, pairing parser,
credential persistence, QR scanner, UDP socket, report scheduler, Android input
handling, or gameplay controller UI.

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
