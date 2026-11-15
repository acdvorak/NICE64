"""Cached port selection uses fake serial connections, never hardware."""

import io
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock, patch

import nice64
from test_transport import CONTROLLER, IDENTITY, FakeSerial
from transport import Nice64Connection


def successful_session():
    return FakeSerial([IDENTITY, CONTROLLER, b"OK MODE SERIAL\n",
                       b"OK\n", b"OK\n", b"OK\n"])


class PortCacheTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.cache = Path(directory.name) / ".last-port"
        cache_patch = patch("nice64.PORT_CACHE_PATH", self.cache)
        cache_patch.start()
        self.addCleanup(cache_patch.stop)
        self.serial_factory = Mock()

        def connection(*args, **kwargs):
            return Nice64Connection(*args, serial_factory=self.serial_factory, **kwargs)

        connection_patch = patch("nice64.Nice64Connection", side_effect=connection)
        connection_patch.start()
        self.addCleanup(connection_patch.stop)

    def invoke(self, *options):
        stdout, stderr = io.StringIO(), io.StringIO()
        result = nice64.main([*options, "hold A"], stdin=io.StringIO(""),
                             stdout=stdout, stderr=stderr)
        return result, stdout.getvalue(), stderr.getvalue()

    def test_detected_port_is_cached_for_the_next_invocation(self):
        first, probe, second = successful_session(), FakeSerial([IDENTITY]), successful_session()
        self.serial_factory.side_effect = [first, probe, second]
        with patch("nice64.detect_port", return_value="detected-port") as detect:
            result, _, error = self.invoke("--baud", "57600")
            self.assertEqual(result, 0, error)
            self.assertEqual(self.cache.read_text(encoding="utf-8").strip(), "detected-port")
            result, _, error = self.invoke("--baud", "57600")
        self.assertEqual(result, 0, error)
        detect.assert_called_once_with(57600)
        self.assertEqual(probe.writes, [b"WHO\n"])
        self.assertTrue(probe.closed)
        self.assertEqual([call.kwargs["port"] for call in self.serial_factory.call_args_list],
                         ["detected-port"] * 3)
        self.assertTrue(all(call.kwargs["baudrate"] == 57600
                            for call in self.serial_factory.call_args_list))

    def test_failed_cached_probe_falls_back_and_replaces_cache(self):
        for failure in (OSError("device disconnected"), FakeSerial([b"unrelated device\n"])):
            with self.subTest(failure=type(failure).__name__):
                self.cache.write_text("stale-port\n", encoding="utf-8")
                session = successful_session()
                self.serial_factory.reset_mock()
                self.serial_factory.side_effect = [failure, session]
                with patch("nice64.detect_port", return_value="replacement-port") as detect:
                    result, _, error = self.invoke()
                self.assertEqual(result, 0, error)
                detect.assert_called_once_with(115200)
                self.assertEqual(self.cache.read_text(encoding="utf-8").strip(), "replacement-port")
                self.assertEqual([call.kwargs["port"] for call in self.serial_factory.call_args_list],
                                 ["stale-port", "replacement-port"])
                if isinstance(failure, FakeSerial):
                    self.assertEqual(failure.writes, [b"WHO\n"])
                    self.assertTrue(failure.closed)

    def test_explicit_port_bypasses_cached_probe_and_updates_cache(self):
        self.cache.write_text("previous-port\n", encoding="utf-8")
        self.serial_factory.return_value = successful_session()
        with patch("nice64.detect_port") as detect:
            result, _, error = self.invoke("--port", "chosen-port")
        self.assertEqual(result, 0, error)
        detect.assert_not_called()
        self.serial_factory.assert_called_once()
        self.assertEqual(self.serial_factory.call_args.kwargs["port"], "chosen-port")
        self.assertEqual(self.cache.read_text(encoding="utf-8").strip(), "chosen-port")

    def test_unidentified_explicit_port_does_not_overwrite_cache(self):
        self.cache.write_text("previous-port\n", encoding="utf-8")
        serial = FakeSerial([b"unrelated device\n"])
        self.serial_factory.return_value = serial
        with patch("nice64.detect_port") as detect:
            result, _, error = self.invoke("--port", "wrong-port")
        self.assertEqual(result, 1)
        self.assertIn("did not identify", error)
        detect.assert_not_called()
        self.assertEqual(self.cache.read_text(encoding="utf-8").strip(), "previous-port")
        self.assertEqual(serial.writes, [b"WHO\n"])
        self.assertTrue(serial.closed)

    def test_detected_port_is_remembered_even_if_execution_cannot_open_it(self):
        self.serial_factory.side_effect = OSError("device disconnected after discovery")
        with patch("nice64.detect_port", return_value="detected-port"):
            result, _, error = self.invoke()
        self.assertEqual(result, 1)
        self.assertIn("device disconnected after discovery", error)
        self.assertEqual(self.cache.read_text(encoding="utf-8").strip(), "detected-port")

    def test_identity_is_remembered_even_when_personality_prevents_execution(self):
        for explicit in (False, True):
            with self.subTest(explicit=explicit):
                self.cache.unlink(missing_ok=True)
                serial = FakeSerial([IDENTITY, b"MODE|STANDALONE|DIAGNOSTICS\n"])
                self.serial_factory.return_value = serial
                with patch("nice64.detect_port", return_value="identified-port") as detect:
                    options = ("--port", "identified-port") if explicit else ()
                    result, _, error = self.invoke(*options)
                self.assertEqual(result, 1)
                self.assertIn("personality", error)
                self.assertEqual(self.cache.read_text(encoding="utf-8").strip(), "identified-port")
                self.assertEqual(serial.writes, [b"WHO\n", b"MODE?\n"])
                self.assertTrue(serial.closed)
                if explicit:
                    detect.assert_not_called()

    def test_execution_failure_on_cached_port_does_not_replay_commands(self):
        self.cache.write_text("cached-port\n", encoding="utf-8")
        probe = FakeSerial([IDENTITY])
        session = FakeSerial([IDENTITY, CONTROLLER, b"OK MODE SERIAL\n",
                              b"OK\n", b"ERR CORE0\n", b"OK\n"])
        self.serial_factory.side_effect = [probe, session]
        with patch("nice64.detect_port") as detect:
            result, _, error = self.invoke()
        self.assertEqual(result, 1)
        self.assertIn("ERR CORE0", error)
        detect.assert_not_called()
        self.assertEqual(session.writes.count(b"N64 80000000\n"), 1)
        self.assertEqual(session.writes[-1], b"N64 00000000\n")
        self.assertTrue(probe.closed)
        self.assertTrue(session.closed)

    def test_invalid_utf8_cache_is_ignored_and_replaced(self):
        self.cache.write_bytes(b"\xff\n")
        self.serial_factory.return_value = successful_session()
        with patch("nice64.detect_port", return_value="detected-port") as detect:
            result, _, error = self.invoke()
        self.assertEqual(result, 0, error)
        detect.assert_called_once_with(115200)
        self.assertEqual(self.cache.read_text(encoding="utf-8").strip(), "detected-port")

    def test_cache_read_failure_does_not_prevent_detection_or_execution(self):
        self.serial_factory.return_value = successful_session()
        with patch("nice64.Path.read_text", side_effect=OSError("cache unreadable")), \
                patch("nice64.detect_port", return_value="detected-port") as detect:
            result, _, error = self.invoke()
        self.assertEqual(result, 0, error)
        detect.assert_called_once_with(115200)

    def test_cache_write_failure_does_not_prevent_execution(self):
        self.serial_factory.return_value = successful_session()
        with patch("nice64.Path.write_text", side_effect=OSError("cache unwritable")), \
                patch("nice64.detect_port", return_value="detected-port"):
            result, _, error = self.invoke()
        self.assertEqual(result, 0, error)
        self.assertFalse(self.cache.exists())


if __name__ == "__main__":
    unittest.main()
