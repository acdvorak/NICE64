"""Automatic port selection uses mocked identity probes, never hardware."""

from types import SimpleNamespace
import unittest
from unittest.mock import Mock

from test_transport import FakeSerial, IDENTITY
from transport import TransportError, detect_port


def ports(*devices):
    return lambda: [SimpleNamespace(device=device) for device in devices]


class DetectionTests(unittest.TestCase):
    def test_single_device_is_identified_without_controller_commands(self):
        serial = FakeSerial([IDENTITY])
        factory = Mock(return_value=serial)
        selected = detect_port(port_enumerator=ports("COM4"), serial_factory=factory)
        self.assertEqual(selected, "COM4")
        self.assertEqual(serial.writes, [b"WHO\n"])
        self.assertTrue(serial.closed)
        self.assertTrue(serial.discarded)

    def test_unrelated_devices_are_skipped_and_every_probe_is_closed(self):
        devices = {
            "COM1": FakeSerial([b"unrelated device\n"]),
            "COM2": FakeSerial([IDENTITY]),
            "COM3": FakeSerial([b""]),
        }
        factory = Mock(side_effect=lambda **options: devices[options["port"]])
        selected = detect_port(port_enumerator=ports(*devices), serial_factory=factory)
        self.assertEqual(selected, "COM2")
        self.assertEqual(factory.call_count, 3)
        for serial in devices.values():
            self.assertEqual(serial.writes, [b"WHO\n"])
            self.assertTrue(serial.closed)

    def test_no_ports_and_no_matching_identity_are_actionable(self):
        factory = Mock()
        with self.assertRaisesRegex(TransportError, "--port"):
            detect_port(port_enumerator=ports(), serial_factory=factory)
        factory.assert_not_called()

        serial = FakeSerial([b"OK\n"])
        with self.assertRaisesRegex(TransportError, "--port"):
            detect_port(port_enumerator=ports("COM1"), serial_factory=lambda **_: serial)
        self.assertTrue(serial.closed)

    def test_multiple_devices_require_explicit_selection(self):
        devices = {name: FakeSerial([IDENTITY]) for name in ("COM1", "COM2")}
        with self.assertRaises(TransportError) as error:
            detect_port(port_enumerator=ports(*devices),
                        serial_factory=lambda **options: devices[options["port"]])
        self.assertIn("--port", str(error.exception))
        for name, serial in devices.items():
            self.assertIn(name, str(error.exception))
            self.assertTrue(serial.closed)

    def test_duplicate_names_and_macos_aliases_are_probed_once(self):
        serial = FakeSerial([IDENTITY])
        factory = Mock(return_value=serial)
        selected = detect_port(
            port_enumerator=ports("/dev/tty.usbmodem123", "/dev/cu.usbmodem123",
                                  "/dev/cu.usbmodem123"),
            serial_factory=factory)
        self.assertEqual(selected, "/dev/cu.usbmodem123")
        factory.assert_called_once()
        self.assertEqual(factory.call_args.kwargs["port"], "/dev/cu.usbmodem123")

    def test_baudrate_and_bounded_timeouts_reach_serial_factory(self):
        factory = Mock(return_value=FakeSerial([IDENTITY]))
        detect_port(57600, port_enumerator=ports("COM4"), serial_factory=factory)
        options = factory.call_args.kwargs
        self.assertEqual(options["baudrate"], 57600)
        self.assertGreater(options["timeout"], 0)
        self.assertLessEqual(options["timeout"], 1)
        self.assertGreater(options["write_timeout"], 0)
        self.assertLessEqual(options["write_timeout"], 1)

    def test_enumeration_failure_is_wrapped(self):
        enumerator = Mock(side_effect=OSError("enumeration unavailable"))
        with self.assertRaisesRegex(TransportError, "enumeration unavailable"):
            detect_port(port_enumerator=enumerator, serial_factory=Mock())

    def test_busy_or_unopenable_port_does_not_hide_a_matching_device(self):
        serial = FakeSerial([IDENTITY])

        def factory(**options):
            if options["port"] == "COM1":
                raise OSError("port in use")
            return serial

        selected = detect_port(port_enumerator=ports("COM1", "COM2"), serial_factory=factory)
        self.assertEqual(selected, "COM2")
        self.assertTrue(serial.closed)

    def test_close_failure_makes_candidate_ineligible(self):
        serial = FakeSerial([IDENTITY])
        serial.close = Mock(side_effect=OSError("cannot close probe"))
        with self.assertRaisesRegex(TransportError, "--port"):
            detect_port(port_enumerator=ports("COM1"), serial_factory=lambda **_: serial)
        serial.close.assert_called_once()

    def test_interruption_closes_probe_and_preserves_original_error(self):
        serial = FakeSerial([KeyboardInterrupt()])
        serial.close = Mock(side_effect=OSError("secondary close failure"))
        with self.assertRaises(KeyboardInterrupt):
            detect_port(port_enumerator=ports("COM1"), serial_factory=lambda **_: serial)
        serial.close.assert_called_once()


if __name__ == "__main__":
    unittest.main()
