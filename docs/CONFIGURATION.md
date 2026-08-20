# Bridge Configuration

Bridge v1.6.0 reads and updates `picoswitchcontroller.ini` beside the executable.
The file is user-specific and is intentionally not distributed or tracked.

```ini
[Serial]
BaudRate=115200

[BrowserLive]
ReadableSourceMode=AUTO
SearchProviderName=Brave Search
SearchProviderUrl=https://search.brave.com/search?q={query}

[NetworkPak]
ActiveNetworkPakModuleId=browser-live
WifiTransport=TCP
```

`ReadableSourceMode=AUTO` prefers local in-process readability and falls back to the direct converter when its quality gate fails. `DIRECT` disables local readability. `FROGFIND` is diagnostic only.

`SearchProviderUrl` must contain `{query}`. The Bridge URL-encodes the ROM query before substitution. A provider may be changed without rebuilding the ROM. Keep configuration and module paths relative to the executable; do not store credentials in this file.

`WifiTransport` is `TCP` or `RUDP`. The UI labels RUDP as **Quick Mode
(Reliable UDP)**. It remains an explicit selection and does not silently fall
back to TCP. Wi-Fi credentials are provisioned over USB and stored by firmware;
they are not written to the Bridge INI.
