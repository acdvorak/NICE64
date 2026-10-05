# NICE64 Network Pak Protocol Guide

This document describes the frozen NICE64 Network Pak protocol used by the Network Pak firmware, Windows bridge application, and Network Portal ROM.

The 32-byte Network Pak application protocol below is unchanged by Wi-Fi.
Network Pak can use the existing USB/TCP line path or explicitly use Product
Quick Mode over the reliable ordered UDP carrier. The carrier framing is a
separate layer and does not change application command IDs, payloads, checksums,
or the one-byte application sequence. TCP remains the reference path and Quick
Mode does not silently fall back. See [WIFI_IMPLEMENTATION.md](WIFI_IMPLEMENTATION.md).

The firmware is a transport relay. The PC bridge owns content generation and network/web work. The N64 ROM is a thin client that opens streams, reads chunks, and displays text.

## Architecture

```text
N64 Portal ROM
    v Joybus accessory read/write
Pico Network Pak firmware
    v serial bridge request/response
Windows Bridge Application
    v files / RSS / webpage conversion / local Ollama
Content Providers
```

Do not increase packet sizes or change command IDs without intentionally creating a new protocol version.

## Wire Packet

Every Network Pak packet is exactly one N64 accessory block: 32 bytes.

```c
#define NICE_NET_MAGIC            0x4E50  /* "NP" */
#define NICE_NET_VERSION          0x01
#define NICE_NET_WIRE_BLOCK_SIZE  32
#define NICE_NET_MAX_PAYLOAD      23
#define NICE_NET_STREAM_CHUNK_SIZE 16
```

Packet layout:

| Offset | Size | Field | Notes |
|---:|---:|---|---|
| 0 | 2 | magic | Big-endian bytes `4E 50` (`NP`) |
| 2 | 1 | version | Current version is `01` |
| 3 | 1 | command | Command or response code |
| 4 | 1 | flags | Currently `00` for normal request packets |
| 5 | 1 | sequence | Incrementing request sequence; response must echo it |
| 6 | 1 | payload_length | 0-23 bytes |
| 7 | 23 | payload | Command-specific payload, zero padded on the wire |
| 30 | 2 | checksum | Big-endian 16-bit ones-complement additive checksum |

Checksum algorithm:

1. Sum bytes 0 through 6.
2. Sum only `payload_length` payload bytes from offset 7.
3. Truncate to 16 bits while adding.
4. Store bitwise complement as big-endian bytes at offsets 30 and 31.

```c
uint16_t checksum = 0;
for (int i = 0; i < 7; i++) checksum += packet[i];
for (int i = 0; i < payload_length; i++) checksum += packet[7 + i];
checksum = ~checksum;
```

## Command IDs

Local Pico test commands:

| Command | ID | Response | Purpose |
|---|---:|---:|---|
| `PING` | `0x01` | `PONG` `0x81` | Basic local firmware ping |
| `GET_STATUS` | `0x02` | `STATUS` `0x82` | Local status string |
| `READ_CHUNK` | `0x03` | `CHUNK` `0x83` | Local test data |
| `WRITE_CHUNK` | `0x04` | `ACK` `0x05` | Local write test |
| `RESET` | `0x06` | `ACK` `0x05` | Local reset acknowledgement |

Bridge commands:

| Command | ID | Response | Purpose |
|---|---:|---:|---|
| `PING_BRIDGE` | `0x10` | `BRIDGE_RESPONSE` `0x90` | Verify PC bridge reachability |
| `GET_BRIDGE_STATUS` | `0x11` | `BRIDGE_RESPONSE` `0x90` | Bridge status text |
| `GET_BRIDGE_TIME` | `0x12` | `BRIDGE_RESPONSE` `0x90` | Legacy time/status flow |
| `GET_BRIDGE_MESSAGE` | `0x13` | `BRIDGE_RESPONSE` `0x90` | Legacy message flow |
| `SEND_BRIDGE_MESSAGE` | `0x14` | `BRIDGE_RESPONSE` `0x90` | Legacy message flow |
| `OPEN_STREAM` | `0x20` | `STREAM_OPENED` `0xA0` | Open a named content stream |
| `GET_STREAM_INFO` | `0x21` | `STREAM_INFO` `0xA1` | Reserved/info command |
| `READ_STREAM_CHUNK` | `0x22` | `STREAM_CHUNK` `0xA2` | Read 16-byte stream chunk |
| `WRITE_STREAM_CHUNK` | `0x23` | `STREAM_WRITE_ACK` `0xA4` | Send short client data, used by Ask NICE64 |
| `CLOSE_STREAM` | `0x24` | `STREAM_CLOSED` `0xA3` | Close stream session |
| any error | n/a | `ERROR` `0xFF` | ASCII error payload |

## Stream Resource Names

`OPEN_STREAM` payload is an ASCII resource name.

Maximum resource name length is 23 bytes because the packet payload is 23 bytes. This is why long names such as `bridge://headlines/retro` are not used on N64. Prefer short names:

```text
bridge://menu
bridge://welcome
bridge://rssr
bridge://rssn
bridge://time
bridge://now
bridge://itcorp
bridge://ollama
bridge://aires
```

The bridge maps these names through `portal.json`. The ROM should not know whether content is static text, RSS, webpage text, generated time, or an AI response.

## Opening And Reading A Stream

### 1. OPEN_STREAM

Request payload:

```text
bridge://welcome
```

Response command: `STREAM_OPENED` (`0xA0`)

Response payload, 10 bytes:

| Offset | Size | Field | Notes |
|---:|---:|---|---|
| 0 | 1 | stream_id | Non-zero stream session ID |
| 1 | 4 | total_length | Big-endian byte length |
| 5 | 1 | chunk_size | Currently `16` |
| 6 | 4 | crc32 | Big-endian CRC32 of complete stream data |

### 2. READ_STREAM_CHUNK

Request payload, 5 bytes:

| Offset | Size | Field | Notes |
|---:|---:|---|---|
| 0 | 1 | stream_id | From `OPEN_STREAM` |
| 1 | 4 | offset | Big-endian byte offset |

Response command: `STREAM_CHUNK` (`0xA2`)

Response payload:

| Offset | Size | Field | Notes |
|---:|---:|---|---|
| 0 | 1 | stream_id | Echoed stream ID |
| 1 | 4 | offset | Echoed requested offset |
| 5 | 1 | count | Number of data bytes in this chunk |
| 6 | 1 | flags | Bit 0 = final chunk |
| 7 | count | data | Up to 16 bytes |

The response payload cannot exceed 23 bytes, so chunk data is limited to 16 bytes: `7-byte header + 16-byte data = 23 bytes`.

### 3. CLOSE_STREAM

Request payload:

| Offset | Size | Field |
|---:|---:|---|
| 0 | 1 | stream_id |

Response command: `STREAM_CLOSED` (`0xA3`)

Response payload:

| Offset | Size | Field |
|---:|---:|---|
| 0 | 1 | stream_id |

## Example ROM Flow

```c
open("bridge://welcome");
while (bytes_read < total_length) {
    read_chunk(stream_id, bytes_read);
    append_chunk_data();
}
verify_crc32();
close(stream_id);
display_text();
```

The current portal ROM follows this model and stores the full page in RAM before rendering it.

## Ask NICE64 / Client-To-Bridge Write Flow

`WRITE_STREAM_CHUNK` is used to send short user input from the ROM to the bridge. It does not change the transport protocol.

Request payload:

| Offset | Size | Field | Notes |
|---:|---:|---|---|
| 0 | 1 | flags | Bit 0 = first chunk, bit 1 = last chunk |
| 1 | 1 | count | Number of text bytes following |
| 2 | count | text | Up to 21 bytes per packet |

Response command: `STREAM_WRITE_ACK` (`0xA4`)

Response payload:

```text
01
```

Example for `HELLO` in one packet:

```text
flags = 0x03   // first + last
count = 0x05
text  = HELLO
```

After the bridge receives the full prompt, it starts the Ollama request asynchronously. The ROM can immediately show a short pending page and later poll/open:

```text
bridge://aires
```

Possible result states:

- `Thinking...`
- `Still thinking...`
- final answer text
- readable error text

## Bridge Serial Forwarding Format

The Pico forwards bridge-bound commands over serial to the Windows bridge. The bridge responds with a text line.

Hex request format:

```text
NETPAK_REQ_HEX|<sequence>|<command-name>|<payload-hex>
```

Hex response format:

```text
NETPAK_RSP_HEX|<sequence>|OK|<response-command-hex>|<payload-hex>
```

Error response format:

```text
NETPAK_RSP_HEX|<sequence>|ERR|FF|<ascii-error-as-hex>
```

Example open request from a smoke test:

```text
NETPAK_REQ_HEX|1|OPEN_STREAM|6272696467653A2F2F77656C636F6D65
```

The payload hex decodes to:

```text
bridge://welcome
```

A successful response uses response command `A0` (`STREAM_OPENED`) and returns stream metadata.

Example read request payload fields:

```text
stream_id = 03
offset    = 00000000
```

As hex:

```text
0300000000
```

Request line:

```text
NETPAK_REQ_HEX|2|READ_STREAM_CHUNK|0300000000
```

## Current Public Resources

The exported `portal.json` drives the bridge resources. Typical resources include:

| Resource | Provider |
|---|---|
| `bridge://menu` | generated menu from `portal.json` |
| `bridge://welcome` | static text file |
| `bridge://about` | static text file |
| `bridge://diagnostics` | static text file |
| `bridge://news` | static text file |
| `bridge://rssr` | Retro Gaming RSS converted to text |
| `bridge://rssn` | General News RSS converted to text |
| `bridge://time` | generated time page |
| `bridge://now` | compact sync payload: `yyyy-mm-dd|hh:mm:ss|timezone` |
| `bridge://itcorp` | webpage converted to plain text |
| `bridge://ollama` | Ask NICE64 prompt submission/status |
| `bridge://aires` | Ask NICE64 result polling page |

## Error Strings

Common ASCII error payloads:

| Error | Meaning |
|---|---|
| `BAD_PACKET` | Packet magic/version/length/checksum validation failed |
| `UNSUPPORTED` | Command is not supported by this layer |
| `BRIDGE_OFFLINE` | Firmware has no connected PC bridge |
| `BRIDGE_BUSY` | A prior bridge request is still pending |
| `BRIDGE_TIMEOUT` | PC bridge did not answer within firmware timeout |
| `RESOURCE_TOO_LONG` | ROM attempted to open a resource name longer than 23 bytes |
| `CRC_MISMATCH` | Full stream data failed CRC32 validation on ROM side |

## Implementation References

- Firmware protocol constants:
  `firmware/source/NICE64-Unified-Firmware-v1.2.0/PicoN64Firmware/src/n64_accessory.c`
- ROM packet structure and command enum:
  `rom/Legacy/network-portal-v0.7/source/include/protocol.h`
- ROM stream helper flow:
  `rom/Legacy/network-portal-v0.7/source/src/main.c`
- Bridge stream/session implementation:
  `bridge-application/nice64-bridge-v1.8.0/source/NetworkPakBridgeService.cs`
- Smoke test examples:
  `bridge-application/nice64-bridge-v1.8.0/source/tests/NetworkPakLayer2Smoke/Program.cs`

## Guardrails

- Firmware is frozen.
- Layer 2 is frozen.
- Do not increase `NICE_NET_MAX_PAYLOAD`.
- Do not change command IDs.
- Do not make the Pico a browser, TCP/IP stack, RSS client, or AI client.
- Add new content by adding bridge resources and `portal.json` entries whenever possible.

