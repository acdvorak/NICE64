# NICE64 Android Companion

This folder contains Android Companion v1.2.0 (`versionCode` 2) source and its
locally built debug-signed APK. It is packaged with NICE64 Bridge v1.9.0 and the
Universal Firmware v1.3.0 release candidate.

Install `NICE64-Android-Companion.apk` manually on an Android device, then pair
it using the onboarding QR shown locally by Bridge. Android 8.0 / API 26 or
newer is required; the application targets API 36. The APK is not
production-signed.

The companion supports Controller input plus the standalone RandNET Keyboard
and N64 Mouse modes. Management and verified mode selection use the Bridge API;
latency-sensitive Controller, Keyboard, and Mouse state travels directly to
Pico over UDP. Mouse input provides persistent client-side Invert X and Invert
Y options. Android's down-positive pointer coordinates are normalized before
optional inversion and cumulative movement accounting.

Controller Blackout Mode keeps the NICE64 activity foregrounded on a true-black
surface and prevents normal inactivity timeout. It does not provide physical
controller input while the Android lock screen or power-button-off state owns
focus.

SHA-256:

`E40C976B9D5A5BDE26B77F834B4F0EE59719D9007DB58AD3A531E4BEF680260A`

See `../docs/ANDROID_COMPANION.md` for pairing, architecture, controller, and
security details.

## Source and build

The project snapshot is in `source/`. Its version is code `2`, name `1.2.0`.

Install Android SDK Platform 36 and Build Tools 36.0.0, configure the local SDK
path outside the exported source, and run from `android-application/source`:

```powershell
.\gradlew.bat --no-daemon :core:test :app:testDebugUnitTest :app:lint :app:assembleDebug
```

The debug APK is produced at `app/build/outputs/apk/debug/app-debug.apk`.
Production signing material is not included.
