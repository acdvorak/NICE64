# Configuration — NICE64 Browser v1.4.0

The Bridge reads `picoswitchcontroller.ini` beside the executable at `bridge-application/nice64-bridge-v1.4.0/application/PicoSwitchController.exe`.

```ini
[Serial]
BaudRate=115200

[BrowserLive]
ReadableSourceMode=AUTO
SearchProviderName=Brave Search
SearchProviderUrl=https://search.brave.com/search?q={query}

[NetworkPak]
ActiveNetworkPakModuleId=browser-live
```

`ReadableSourceMode=AUTO` prefers local in-process readability and falls back to the direct converter when its quality gate fails. `DIRECT` disables local readability. `FROGFIND` is diagnostic only.

`SearchProviderUrl` must contain `{query}`. The Bridge URL-encodes the ROM query before substitution. A provider may be changed without rebuilding the ROM. Keep configuration and module paths relative to the executable; do not store credentials in this file.
