import io
import unittest

from controller import BUTTON_BITS, Progress, State, execute_program
from language import compile_fragments
from pathlib import Path
from transport import Nice64Connection, TransportError
from test_transport import CONTROLLER, IDENTITY, FakeSerial


class FakeConnection:
    def __init__(self):
        self.controller_ready = False
        self.opened = False
        self.closed = False
        self.states = []
        self.fail_state = None
        self.fail_cleanup = False

    def open(self):
        self.opened = True

    def initialize(self):
        self.controller_ready = True

    def send_state(self, state):
        self.states.append(state)
        if state == self.fail_state or (self.fail_cleanup and len(self.states) > 1 and state == "00000000"):
            raise TransportError(f"rejected state {state}")

    def close(self):
        self.closed = True


class FakeClock:
    def __init__(self):
        self.now = 0.0
        self.sleeps = []

    def monotonic(self):
        return self.now

    def sleep(self, duration):
        self.sleeps.append(duration)
        self.now += duration


class FlushingOutput(io.StringIO):
    def __init__(self, fail_at=None):
        super().__init__()
        self.flushes = 0
        self.fail_at = fail_at

    def flush(self):
        self.flushes += 1
        if self.fail_at is not None and self.flushes >= self.fail_at:
            raise BrokenPipeError("stdout consumer closed")
        super().flush()


class ExecutionTests(unittest.TestCase):
    def execute(self, text, connection=None, output=None, sleep=None):
        connection = connection or FakeConnection()
        output = output if output is not None else FlushingOutput()
        clock = FakeClock()
        commands = compile_fragments([("test", text)], Path.cwd())
        execute_program(commands, connection, Progress(output, clock.monotonic),
                        io.StringIO(), sleep or clock.sleep)
        return connection, output, clock

    def test_every_canonical_button_and_signed_coordinates(self):
        expected = {
            "A": "8000", "B": "4000", "Z": "2000", "Start": "1000",
            "DUp": "0800", "DDown": "0400", "DLeft": "0200", "DRight": "0100",
            "L": "0020", "R": "0010", "CUp": "0008", "CDown": "0004",
            "CLeft": "0002", "CRight": "0001",
        }
        self.assertEqual(set(expected), set(BUTTON_BITS))
        for button, mask in expected.items():
            with self.subTest(button=button):
                self.assertEqual(State(frozenset([button]), (-80, 80)).encode(), mask + "B050")
        self.assertEqual(State(frozenset(expected)).encode(), "FF3F0000")

    def test_defaults_and_explicit_pause_replaces_default(self):
        connection, output, clock = self.execute(
            "press A; press B for 0.25s pause 0ms; wait; wait 2s")
        self.assertEqual(clock.sleeps, [.1, .1, .25, 0, .1, 2])
        self.assertEqual(connection.states,
                         ["00000000", "80000000", "00000000", "40000000", "00000000", "00000000"])
        self.assertIn("PAUSE 0ms", output.getvalue())

    def test_persistent_buttons_survive_temporary_overlay(self):
        connection, _, clock = self.execute(
            "hold Z+A; press A+B; release A; wait 0s; release Z")
        self.assertEqual(connection.states, [
            "00000000", "A0000000", "E0000000", "A0000000",
            "20000000", "00000000", "00000000"])
        self.assertEqual(clock.sleeps, [.1, .1, 0])

    def test_stick_replacement_restoration_and_selective_release(self):
        connection, output, _ = self.execute(
            "hold Z+StickLeft; hold A; press B+Stick(50,75); "
            "release A; release StickRight; release all")
        self.assertEqual(connection.states, [
            "00000000", "2000B000", "A000B000", "E000283C",
            "A000B000", "2000B000", "20000000", "00000000", "00000000"])
        self.assertIn("restore persistent held state: buttons=A+Z stick=(-80,0)", output.getvalue())
        self.assertIn("RELEASE_ALL", output.getvalue())

    def test_progress_is_flushed_before_every_sleep(self):
        output = FlushingOutput()
        connection = FakeConnection()
        snapshots = []

        def observe_sleep(duration):
            lines = output.getvalue().splitlines()
            self.assertEqual(len(lines), output.flushes)
            snapshots.append((duration, lines[-1], list(connection.states)))

        self.execute("press A; wait 1s", connection, output, observe_sleep)
        self.assertIn("HOLD_FOR 100ms", snapshots[0][1])
        self.assertEqual(snapshots[0][2][-1], "80000000")
        self.assertIn("PAUSE 100ms", snapshots[1][1])
        self.assertEqual(snapshots[1][2][-1], "00000000")
        self.assertIn("WAIT 1000ms", snapshots[2][1])
        self.assertIn("[    0.000s]", output.getvalue())
        self.assertTrue(output.getvalue().splitlines()[-1].endswith("connection closed"))

    def test_progress_escapes_embedded_newlines(self):
        output = FlushingOutput()
        Progress(output).emit("RUN_ENTER", "path\nwith\rnewlines.n64script")
        self.assertEqual(len(output.getvalue().splitlines()), 1)
        self.assertIn(r"path\nwith\rnewlines.n64script", output.getvalue())

    def test_keyboard_interrupt_cleans_up_temporary_and_persistent_state(self):
        connection = FakeConnection()

        def interrupt(_):
            raise KeyboardInterrupt()

        with self.assertRaises(KeyboardInterrupt):
            self.execute("hold Z; press A", connection, sleep=interrupt)
        self.assertEqual(connection.states, ["00000000", "20000000", "A0000000", "00000000"])
        self.assertTrue(connection.closed)

    def test_transport_failure_cleans_up(self):
        connection = FakeConnection()
        connection.fail_state = "80000000"
        with self.assertRaisesRegex(TransportError, "rejected state 80000000"):
            self.execute("hold A", connection)
        self.assertEqual(connection.states[-1], "00000000")
        self.assertTrue(connection.closed)

    def test_delayed_ack_after_failure_does_not_claim_neutral_acknowledged(self):
        serial = FakeSerial([IDENTITY, CONTROLLER, b"OK MODE SERIAL\n", b"OK\n",
                             b"", b"OK\nNICE64|BUSY|OWNER=TCP\n"])
        connection = Nice64Connection("mock", serial_factory=lambda **_: serial)
        output = io.StringIO()
        with self.assertRaisesRegex(TransportError, "timed out"):
            self.execute("hold A", connection, output)
        self.assertEqual(serial.writes[-1], b"N64 00000000\n")
        self.assertIn("ACK may belong to an earlier command", output.getvalue())
        self.assertNotIn("final neutral:", output.getvalue())
        self.assertNotIn("COMPLETE", output.getvalue())
        self.assertTrue(serial.closed)

    def test_stdout_failure_cannot_skip_neutral_or_close(self):
        connection = FakeConnection()
        output = FlushingOutput(fail_at=5)
        with self.assertRaises(BrokenPipeError):
            self.execute("hold A", connection, output)
        self.assertEqual(connection.states, ["00000000", "80000000", "00000000"])
        self.assertTrue(connection.closed)

    def test_cleanup_failure_does_not_hide_original_error(self):
        connection = FakeConnection()
        connection.fail_state = "80000000"
        connection.fail_cleanup = True
        stderr = io.StringIO()
        commands = compile_fragments([("test", "hold A")], Path.cwd())
        with self.assertRaisesRegex(TransportError, "rejected state 80000000"):
            execute_program(commands, connection, Progress(io.StringIO()), stderr)
        self.assertIn("cleanup failed", stderr.getvalue())
        self.assertTrue(connection.closed)

    def test_cleanup_failure_makes_successful_program_fail(self):
        connection = FakeConnection()
        connection.fail_cleanup = True
        with self.assertRaisesRegex(TransportError, "cleanup failed"):
            self.execute("hold A", connection)
        self.assertTrue(connection.closed)

    def test_lost_initialization_ack_still_attempts_neutral(self):
        connection = FakeConnection()

        def fail_initialization():
            connection.controller_ready = True
            raise TransportError("lost MODE SERIAL ACK")

        connection.initialize = fail_initialization
        with self.assertRaisesRegex(TransportError, "lost MODE SERIAL ACK"):
            self.execute("hold A", connection)
        self.assertEqual(connection.states, ["00000000"])
        self.assertTrue(connection.closed)

    def test_rejected_personality_is_closed_without_controller_writes(self):
        connection = FakeConnection()

        def reject_personality():
            raise TransportError("not Controller personality")

        connection.initialize = reject_personality
        with self.assertRaisesRegex(TransportError, "not Controller"):
            self.execute("hold A", connection)
        self.assertEqual(connection.states, [])
        self.assertTrue(connection.closed)


if __name__ == "__main__":
    unittest.main()
