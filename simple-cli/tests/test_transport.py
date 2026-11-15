"""Serial protocol tests; these never open a real serial device."""

import os
import unittest
from unittest.mock import Mock, patch

from transport import Nice64Connection, TransportError


IDENTITY = b"WHO|NICE64|CTRL=1|FW=TIMING_CORE_V2\n"
CONTROLLER = b"MODE|CONTROLLER|NONE\n"


class FakeSerial:
    def __init__(self, replies):
        self.replies = iter(replies)
        self.incoming = bytearray()
        self.writes = []
        self.closed = False
        self.discarded = False

    def reset_input_buffer(self):
        self.discarded = True

    def write(self, data):
        self.writes.append(data)
        reply = next(self.replies)
        if isinstance(reply, BaseException):
            raise reply
        self.incoming.extend(reply)
        return len(data)

    def read(self, size):
        data = bytes(self.incoming[:size])
        del self.incoming[:size]
        return data

    def close(self):
        self.closed = True


def connection_for(replies):
    serial = FakeSerial(replies)
    factory = Mock(return_value=serial)
    connection = Nice64Connection("fake-port", serial_factory=factory)
    connection.open()
    return connection, serial, factory


class TransportTests(unittest.TestCase):
    def test_identity_mode_and_exact_acknowledgements(self):
        connection, serial, factory = connection_for(
            [IDENTITY, CONTROLLER, b"OK MODE SERIAL\n", b"OK\r\n", b"OK\n"]
        )
        connection.initialize()
        self.assertTrue(connection.controller_ready)
        connection.send_state("80000000")
        connection.send_state("00000000")
        connection.close()
        self.assertEqual(serial.writes, [
            b"WHO\n", b"MODE?\n", b"MODE SERIAL\n",
            b"N64 80000000\n", b"N64 00000000\n",
        ])
        self.assertTrue(serial.discarded)
        self.assertTrue(serial.closed)
        options = factory.call_args.kwargs
        self.assertEqual(options["baudrate"], 115200)
        self.assertEqual(options["timeout"], 1.0)
        self.assertEqual(options["write_timeout"], 1.0)
        self.assertEqual(options.get("exclusive"), True if os.name == "posix" else None)

    def test_other_device_never_receives_mode_or_state(self):
        connection, serial, _ = connection_for([b"OK\n"])
        with self.assertRaisesRegex(TransportError, "did not identify"):
            connection.initialize()
        self.assertFalse(connection.controller_ready)
        self.assertEqual(serial.writes, [b"WHO\n"])

    def test_blank_lines_do_not_count_as_acknowledgements(self):
        connection, _, _ = connection_for(
            [b"\r\n\n" + IDENTITY, CONTROLLER, b"\n\r\nOK MODE SERIAL\n"]
        )
        connection.initialize()
        self.assertTrue(connection.controller_ready)

    def test_other_personalities_are_reported_without_switching(self):
        for mode in (b"MODE|STANDALONE|DIAGNOSTICS\n", b"MODE|STANDALONE|KEYBOARD_MOUSE|MOUSE\n"):
            with self.subTest(mode=mode):
                connection, serial, _ = connection_for([IDENTITY, mode])
                with self.assertRaisesRegex(TransportError, "personality"):
                    connection.initialize()
                self.assertFalse(connection.controller_ready)
                self.assertEqual(serial.writes, [b"WHO\n", b"MODE?\n"])

    def test_ownership_conflict_after_identity_and_mode(self):
        connection, serial, _ = connection_for(
            [IDENTITY, CONTROLLER, b"NICE64|BUSY|OWNER=TCP\n"]
        )
        with self.assertRaisesRegex(TransportError, "ownership conflict.*OWNER=TCP"):
            connection.initialize()
        self.assertFalse(connection.controller_ready)
        self.assertEqual(len(serial.writes), 3)

    def test_busy_after_initialization_still_allows_one_cleanup_attempt(self):
        connection, serial, _ = connection_for(
            [IDENTITY, CONTROLLER, b"OK MODE SERIAL\n",
             b"NICE64|BUSY|OWNER=TCP\n", b"NICE64|BUSY|OWNER=TCP\n"]
        )
        connection.initialize()
        with self.assertRaisesRegex(TransportError, "ownership conflict"):
            connection.send_state("80000000")
        self.assertTrue(connection.controller_ready)
        with self.assertRaisesRegex(TransportError, "ownership conflict"):
            connection.send_state("00000000")
        self.assertEqual(serial.writes[-1], b"N64 00000000\n")
        connection.close()
        self.assertFalse(connection.controller_ready)

    def test_busy_after_initialization_can_cleanup_if_owner_has_disconnected(self):
        connection, serial, _ = connection_for(
            [IDENTITY, CONTROLLER, b"OK MODE SERIAL\n",
             b"NICE64|BUSY|OWNER=TCP\n", b"OK\n"]
        )
        connection.initialize()
        with self.assertRaisesRegex(TransportError, "ownership conflict"):
            connection.send_state("80000000")
        connection.send_state("00000000")
        self.assertEqual(serial.writes[-1], b"N64 00000000\n")

    def test_lost_mode_ack_still_allows_best_effort_neutral(self):
        connection, serial, _ = connection_for([IDENTITY, CONTROLLER, b"", b"OK\n"])
        with self.assertRaisesRegex(TransportError, "timed out"):
            connection.initialize()
        self.assertTrue(connection.controller_ready)
        connection.send_state("00000000")
        self.assertEqual(serial.writes[-1], b"N64 00000000\n")

    def test_arbitrary_text_is_not_a_state_ack(self):
        for reply in (b"hello\n", b"OK MODE SERIAL\n", b"OK extra\n"):
            with self.subTest(reply=reply):
                connection, _, _ = connection_for(
                    [IDENTITY, CONTROLLER, b"OK MODE SERIAL\n", reply]
                )
                connection.initialize()
                with self.assertRaisesRegex(TransportError, "unexpected reply"):
                    connection.send_state("80000000")

    def test_device_errors_are_not_acknowledgements(self):
        connection, _, _ = connection_for([IDENTITY, CONTROLLER, b"ERR CORE0\n"])
        with self.assertRaisesRegex(TransportError, "rejected.*ERR CORE0"):
            connection.initialize()

    def test_partial_or_missing_reply_is_a_timeout(self):
        for reply in (b"", b"WHO|NICE64|CTRL=1|FW=TIMING_CORE_V2"):
            with self.subTest(reply=reply):
                connection, _, _ = connection_for([reply])
                with self.assertRaisesRegex(TransportError, "timed out"):
                    connection.initialize()

    def test_total_transaction_deadline_is_bounded(self):
        connection, serial, _ = connection_for([IDENTITY])
        with patch("transport.time.monotonic", side_effect=[0.0, 0.5, 1.1]):
            with self.assertRaisesRegex(TransportError, "timed out"):
                connection.initialize()
        self.assertEqual(serial.timeout, 0.5)

    def test_io_errors_include_command(self):
        connection, _, _ = connection_for([OSError("disconnected")])
        with self.assertRaisesRegex(TransportError, "WHO.*disconnected"):
            connection.initialize()

    def test_incomplete_write_is_failure(self):
        connection, serial, _ = connection_for([])
        serial.write = Mock(return_value=1)
        with self.assertRaisesRegex(TransportError, "incomplete serial write"):
            connection.initialize()

    def test_non_ascii_and_oversized_replies_fail(self):
        for reply in (b"\xff\n", b"x" * 1025 + b"\n"):
            with self.subTest(size=len(reply)):
                connection, _, _ = connection_for([reply])
                with self.assertRaises(TransportError):
                    connection.initialize()

    def test_open_failure_is_actionable_and_close_is_safe(self):
        factory = Mock(side_effect=OSError("busy"))
        connection = Nice64Connection("fake-port", serial_factory=factory)
        with self.assertRaisesRegex(TransportError, "disconnect the Bridge"):
            connection.open()
        connection.close()

    def test_close_failure_is_reported_and_reference_is_cleared(self):
        connection, serial, _ = connection_for([])
        serial.close = Mock(side_effect=OSError("close failed"))
        with self.assertRaisesRegex(TransportError, "cannot close"):
            connection.close()
        connection.close()

    def test_no_state_before_verification_or_invalid_hex(self):
        connection, _, _ = connection_for([IDENTITY, CONTROLLER, b"OK MODE SERIAL\n"])
        with self.assertRaisesRegex(TransportError, "not been initialized"):
            connection.send_state("80000000")
        connection.initialize()
        for invalid in ("8000000", "00000000\nWHO", "GG000000"):
            with self.subTest(invalid=invalid), self.assertRaises(ValueError):
                connection.send_state(invalid)


if __name__ == "__main__":
    unittest.main()
