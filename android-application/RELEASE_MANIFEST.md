# NICE64 Android companion export manifest

- Artifact: `NICE64-Android-Companion.apk`
- Version name: `1.2.0`
- Version code: `2`
- Minimum Android API: `26`
- Target Android API: `36`
- Release relationship: NICE64 Bridge v1.9.0
- Compatible firmware candidate: NICE64 Universal Firmware v1.3.0
- Build validation: JVM 338/338 PASS, lint PASS, debug build PASS, APK metadata
  PASS, APK Signature Scheme v2 PASS with one Android Debug signer
- Prior accepted source lineage: connected instrumentation 81/81 and physical
  G2 Mouse/Mario Artist plus Controller/Transfer Pak regressions PASS

SHA-256:

`E40C976B9D5A5BDE26B77F834B4F0EE59719D9007DB58AD3A531E4BEF680260A`

The APK is the locally built v1.2.0 debug-signed artifact. Install it manually.
It is not production-signed, and no production signing identity or private key
is included. It retains direct RandNET Keyboard/N64 Mouse UDP input, natural
Mouse orientation, and persistent Invert X/Invert Y settings.
