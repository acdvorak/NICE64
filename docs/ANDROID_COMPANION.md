# NICE64 Android Companion

The NICE64 Android companion works with NICE64 Bridge v1.8.0 and Universal
Firmware v1.2.0. The exported APK is an accepted frozen build with project
version identity `0.1.0-dev`, minimum Android API 26, and target API 36.

## Install and pair

1. Install `android-application/NICE64-Android-Companion.apk` on Android.
2. Start Bridge v1.8.0 and connect it to NICE64/Pico.
3. In Bridge API settings, select **LocalNetwork**, choose the exact trusted-LAN
   IPv4 interface, and apply the setting.
4. Open the Bridge onboarding QR presentation and scan it from the Android app.
5. Confirm the Bridge identity shown by the app.

Pairing stores a profile-scoped HTTPS endpoint, bearer credential, SPKI pin,
and certificate fingerprint using Android protected storage. The Bridge uses a
persistent self-signed identity; the app validates both its SPKI and certificate
fingerprint. Re-pair after an intentional Bridge TLS-identity reset.

## Architecture

```text
N64 -> NICE64/Pico -> Bridge

Android management -> authenticated, pinned Bridge HTTPS API
Android controller state -> direct UDP LATEST_STATE -> NICE64/Pico
```

Bridge remains the management and controller-Primary authority. Gameplay
controller packets are not relayed through Bridge or its HTTP API.

## Controller authority and sources

There are exactly two Primary values:

- **Bridge Primary** — desktop/manual/PC controller state is authoritative.
- **NICE Mobile Primary** — the accepted Android gameplay source is
  authoritative.

The Android gameplay source is either **On-Screen** or **Controller**. The app
does not use leases, renewals, ownership heartbeats, or automatic failback.

After returning from Home/background, the accepted app performs bounded
authoritative foreground reconciliation when needed. This restores the valid
controller data plane without selecting or automatically reacquiring Primary.

Android USB and Bluetooth game controllers are supported through Android's
GAMEPAD/JOYSTICK event model. Controller profiles provide mapping and matching,
but universal compatibility with every Android HID controller is not claimed.
The app also provides embedded and full-screen touch controls.

## Blackout Mode

Controller Blackout Mode keeps the NICE64 activity foregrounded, displays a
true-black OLED-friendly surface, prevents normal inactivity display timeout,
and keeps the active physical controller path available. Use **Restore display**
or Android Back to exit.

Blackout Mode does not turn the display hardware off. Physical controller input
is not supported while Android's lock screen or power-button-off state owns
focus. Blackout exits when the accepted `ON_STOP` lifecycle path takes
ownership.

## Management features

The companion includes:

- Bridge status and authoritative operation progress;
- Bridge and NICE Mobile Primary selection;
- History browsing and activation;
- Android-visible accessory choices: None, Controller Pak, Transfer Pak, and
  Network Pak;
- controller profiles and mapping;
- Accessories and Modes presentation;
- bounded Advanced diagnostics.

Rumble Pak, firmware selection, Network Pak module selection, and Network Pak
transport selection remain backend/desktop capabilities but are not advertised
as Android accessory-selection controls.

## Security and network behavior

- Pair only on a trusted LAN.
- Bridge LocalNetwork uses HTTPS on exactly the selected LAN IPv4 and bearer
  authentication for every `/api/v1` route.
- The Android client uses the paired certificate/SPKI pins and literal IPv4; it
  does not use a trust-all TLS policy.
- Controller delivery is Wi-Fi UDP only. There is no cellular or TCP fallback.
- No bearer token, QR payload, certificate private key, or pairing profile is
  included in this repository.

## Artifact

`android-application/NICE64-Android-Companion.apk`

SHA-256:

`28D9E16DBD94E4FC07B32033DE84C621DE50E2A60C7F8D0D2BD87A5E0BF897CC`
