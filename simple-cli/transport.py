"""Bounded, synchronous NICE64 USB serial protocol (no Bridge dependency)."""

import math
import os
import re
import time


class TransportError(Exception):
    """A connection, protocol, ownership, or serial I/O failure."""


class Nice64Connection:
    """Own one serial connection; the caller owns neutral-state cleanup.

    ``controller_ready`` becomes true immediately before MODE SERIAL is sent,
    so a lost acknowledgement still permits a best-effort neutral report.
    An initial ownership rejection clears it. Once initialization succeeded,
    later conflicts retain cleanup eligibility for one neutral attempt; the
    firmware will reject that attempt while another transport still owns it.
    No state is sent to an unidentified device or another personality.
    """

    def __init__(self, port, baudrate=115200, timeout=1.0, serial_factory=None):
        if not math.isfinite(timeout) or timeout <= 0:
            raise ValueError("serial timeout must be finite and positive")
        self.port = port
        self.baudrate = baudrate
        self.timeout = timeout
        self._serial_factory = serial_factory
        self._serial = None
        self.controller_ready = False
        self._initialized = False

    def open(self):
        if self._serial is not None:
            raise TransportError("serial connection is already open")
        factory = self._serial_factory
        if factory is None:
            try:
                import serial
            except ImportError as exc:
                raise TransportError(
                    "pyserial is required; install with python3 -m pip install -r requirements.txt"
                ) from exc
            factory = serial.Serial
        options = dict(
            port=self.port,
            baudrate=self.baudrate,
            timeout=self.timeout,
            write_timeout=self.timeout,
        )
        if os.name == "posix":
            options["exclusive"] = True
        try:
            self._serial = factory(**options)
            # Discard stale replies/startup text before the identity handshake.
            self._serial.reset_input_buffer()
        except Exception as exc:
            raise TransportError(
                f"cannot open {self.port}: {exc}; disconnect the Bridge and any other serial client"
            ) from exc

    def identify(self):
        """Verify identity without enabling serial input or changing state."""
        identity = self._exchange("WHO")
        if not re.fullmatch(r"WHO\|NICE64\|CTRL=1\|FW=[^|\s]+", identity):
            raise TransportError(f"{self.port} did not identify as a supported NICE64: {identity!r}")

    def initialize(self):
        self.identify()
        mode = self._exchange("MODE?")
        if mode != "MODE|CONTROLLER|NONE":
            if mode.startswith("MODE|"):
                raise TransportError(
                    f"NICE64 is in {'/'.join(mode.split('|')[1:])} personality; "
                    "select Controller personality separately, then reconnect"
                )
            raise TransportError(f"unexpected NICE64 personality response: {mode!r}")
        self.controller_ready = True
        self._expect("MODE SERIAL", "OK MODE SERIAL")
        self._initialized = True

    def send_state(self, hex_state):
        if not self.controller_ready:
            raise TransportError("NICE64 Controller personality has not been initialized")
        if not re.fullmatch(r"[0-9A-Fa-f]{8}", hex_state):
            raise ValueError("controller state must contain exactly eight hexadecimal digits")
        self._expect("N64 " + hex_state.upper(), "OK")

    def close(self):
        connection, self._serial = self._serial, None
        self.controller_ready = False
        self._initialized = False
        if connection is not None:
            try:
                # Do not flush: POSIX tcdrain can wait without a timeout.
                connection.close()
            except Exception as exc:
                raise TransportError(f"cannot close {self.port}: {exc}") from exc

    def _expect(self, command, expected):
        reply = self._exchange(command)
        if reply != expected:
            raise TransportError(
                f"unexpected reply to {command!r}: {reply!r}; expected {expected!r}"
            )

    def _exchange(self, command):
        connection = self._serial
        if connection is None:
            raise TransportError("serial connection is not open")
        deadline = time.monotonic() + self.timeout
        try:
            payload = (command + "\n").encode("ascii")
            connection.write_timeout = self.timeout
            written = connection.write(payload)
            if written != len(payload):
                raise TransportError(f"incomplete serial write for {command!r}")
            line = bytearray()
            while True:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise TransportError(f"timed out waiting for NICE64 reply to {command!r}")
                connection.timeout = remaining
                chunk = connection.read(1)
                if not chunk:
                    raise TransportError(f"timed out waiting for NICE64 reply to {command!r}")
                if chunk == b"\n":
                    if not line or line == b"\r":
                        line.clear()
                        continue
                    reply = line.rstrip(b"\r").decode("ascii")
                    if reply.startswith("NICE64|BUSY"):
                        if not self._initialized:
                            self.controller_ready = False
                        raise TransportError(
                            f"NICE64 transport ownership conflict: {reply}; "
                            "disconnect the Bridge/TCP owner before using this tool"
                        )
                    if reply.upper().startswith("ERR") or "|ERR|" in reply.upper():
                        raise TransportError(f"NICE64 rejected {command!r}: {reply}")
                    return reply
                line.extend(chunk)
                if len(line) > 1024:
                    raise TransportError(f"oversized NICE64 response to {command!r}")
        except TransportError:
            raise
        except Exception as exc:
            raise TransportError(f"serial I/O failed during {command!r}: {exc}") from exc


def detect_port(baudrate=115200, *, port_enumerator=None, serial_factory=None):
    """Find one NICE64 using identity-only probes, closing every probe port.

    Pico USB descriptors are not unique to NICE64. Check the firmware's WHO
    response instead, leaving personality checks and MODE SERIAL to execution.
    """
    if port_enumerator is None:
        try:
            from serial.tools import list_ports
        except ImportError as exc:
            raise TransportError(
                "pyserial is required; install with python3 -m pip install -r requirements.txt"
            ) from exc
        port_enumerator = list_ports.comports
    try:
        ports = {port.device for port in port_enumerator()}
    except Exception as exc:
        raise TransportError(f"cannot enumerate serial ports: {exc}; specify --port PORT") from exc
    # macOS callout and dial-in paths can describe the same physical device.
    ports = sorted(port for port in ports
                   if not (port.startswith("/dev/tty.")
                           and "/dev/cu." + port[len("/dev/tty."):] in ports))
    if not ports:
        raise TransportError("no serial ports found; connect NICE64 over USB or specify --port PORT")

    matches = []
    failures = []
    for port in ports:
        connection = Nice64Connection(port, baudrate, serial_factory=serial_factory)
        identified = False
        try:
            connection.open()
            connection.identify()
            identified = True
        except TransportError as exc:
            failures.append(f"{port}: {exc}")
        finally:
            try:
                connection.close()
            except TransportError as exc:
                identified = False
                failures.append(f"{port}: {exc}")
        if identified:
            matches.append(port)
    if len(matches) > 1:
        raise TransportError(
            f"multiple NICE64 devices detected: {', '.join(matches)}; select one with --port PORT"
        )
    if not matches:
        detail = "; ".join(failures)
        raise TransportError(
            "no NICE64 device detected; connect it over USB and disconnect the Bridge, "
            f"or specify --port PORT. Probes: {detail}"
        )
    return matches[0]
