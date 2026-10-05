# NICE64 Android Companion

This folder contains the accepted frozen NICE64 Android companion APK for
NICE64 Bridge v1.8.0 and Universal Firmware v1.2.0.

Install `NICE64-Android-Companion.apk` manually on an Android device, then pair
it using the onboarding QR shown locally by Bridge. Android 8.0 / API 26 or
newer is required; the application targets API 36. Its existing project version
identity is `0.1.0-dev`; no new Android semantic release number was invented for
this export.

Controller Blackout Mode keeps the NICE64 activity foregrounded on a true-black
surface and prevents normal inactivity timeout. It does not provide physical
controller input while the Android lock screen or power-button-off state owns
focus.

SHA-256:

`28D9E16DBD94E4FC07B32033DE84C621DE50E2A60C7F8D0D2BD87A5E0BF897CC`

See `../docs/ANDROID_COMPANION.md` for pairing, architecture, controller, and
security details.

## Source and build

The frozen project snapshot is in `source/`. It retains its authoritative
project identity: version code `1`, version name `0.1.0-dev`.

Install Android SDK Platform 36 and Build Tools 36.0.0, configure the local SDK
path outside the exported source, and run from `android-application/source`:

```powershell
.\gradlew.bat --no-daemon :core:test :app:testDebugUnitTest :app:lint :app:assembleDebug
```

The debug APK is produced at `app/build/outputs/apk/debug/app-debug.apk`.
Production signing material is not included.
