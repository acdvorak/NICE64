# NICE64 Bridge v1.8.0 changes

Bridge v1.8.0 is the release-preparation version for the accepted Android
companion integration. It is a version/package/documentation release over the
accepted Bridge behavior; it does not change firmware, Joybus, accessory wire
semantics, controller delivery grammar, Network Pak protocols, or transport
parameters.

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

## Compatibility

- NICE64 Universal Firmware remains v1.2.0.
- API version remains v1.
- Bridge remains one Pico per desktop instance.
- Network Pak TCP and Reliable UDP Quick behavior is unchanged.
- USB carrier selection remains desktop/internal.
- The frozen Quick tuple remains UDP port 6465, window 8, physical flight cap
  2, payload 512 bytes, RTO 40 ms, and retries 4.

## Validation

- Bridge deterministic non-hardware matrix: 60/60 PASS.
- Bridge Release build: 0 warnings / 0 errors.
- Android JVM: 327/327 PASS.
- Android connected instrumentation: 81/81 PASS.
- Android lint/build/physical freeze: PASS.

See [Android companion](ANDROID_COMPANION.md),
[Bridge user's guide](BRIDGE_USER_GUIDE.md), and
[Bridge API v1](BRIDGE_API.md).
