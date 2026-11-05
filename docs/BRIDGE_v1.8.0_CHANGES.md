# NICE64 Bridge v1.8.0 changes

Bridge v1.8.0 is the release-preparation version for the accepted Android
companion integration and the accepted Keyboard/Mouse extensions. The current
package preserves Joybus timing, accessory behavior, Controller packet
compatibility, Network Pak protocols, and transport parameters.

## Highlights

- Exports the authenticated Bridge API and onboarding workflows used by the
  Android companion.
- Documents exactly two controller Primary values: Bridge Primary and NICE
  Mobile Primary.
- Supports the companion's Bridge, History, Controllers, accessories, and
  operation-status presentation through authoritative Bridge state.
- Retains direct Android-to-Pico UDP `LATEST_STATE` gameplay delivery rather
  than relaying controller packets through HTTP or the desktop Bridge.
- Includes the accepted Home/return sender reconciliation and mobile
  presentation/accessibility validation in the companion release relationship.
- Documents Android USB/Bluetooth controller profiles, touch controls, and
  Controller Blackout Mode.
- Adds verified RandNET Keyboard and N64 Mouse firmware selection through the
  same desktop/API firmware-mode service.
- Adds explicit foreground Windows Raw Input capture, Any/Specific device
  selection, `Alt+Left` release, fail-neutral lifecycle handling, and persistent
  client-side Invert X/Invert Y settings.
- Exposes the existing source-neutral Keyboard/Mouse state and LED API routes,
  while official Android Keyboard/Mouse gameplay uses the typed direct UDP
  human-input data plane.

## Compatibility

- NICE64 Universal Firmware remains v1.2.0.
- API version remains v1.
- Bridge remains one Pico per desktop instance.
- Network Pak TCP and Reliable UDP Quick behavior is unchanged.
- USB carrier selection remains desktop/internal.
- The frozen Quick tuple remains UDP port 6465, window 8, physical flight cap
  2, payload 512 bytes, RTO 40 ms, and retries 4.

## Validation

- Stage F/G Bridge regression and nine relevant post-G2 smoke suites: PASS.
- Bridge Release build: 0 warnings / 0 errors.
- Android JVM: 338/338 PASS (294 app + 44 core).
- Android connected instrumentation: 81/81 PASS.
- Android lint, debug/release build, and signature verification: PASS.
- Real-N64 validation: RandNET Keyboard, N64 Mouse, Android direct Mouse,
  Mario Artist: Paint Studio, Pokémon Stadium 2 GB Tower/Transfer Pak, Star
  Fox 64 Controller, and Network Pak workflows PASS.

See [Android companion](ANDROID_COMPANION.md),
[Bridge user's guide](BRIDGE_USER_GUIDE.md), and
[Bridge API v1](BRIDGE_API.md).
