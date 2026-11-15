import io
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from controller import execute_program
import nice64
from test_controller import FakeClock, FakeConnection
from transport import TransportError


class InteractiveInput(io.StringIO):
    def isatty(self):
        return True

    def read(self, *args):
        raise AssertionError("interactive stdin must not be read")


class CliTests(unittest.TestCase):
    def test_auto_detection_for_stdin_and_command_arguments(self):
        for argv, text in ((["hold A", "--baud", "57600"], ""),
                           (["--baud", "57600"], "hold A")):
            with self.subTest(argv=argv):
                stdout, stderr = io.StringIO(), io.StringIO()
                connection = FakeConnection()
                with patch("nice64.detect_port", return_value="detected-port") as detect, \
                        patch("nice64.Nice64Connection", return_value=connection) as factory:
                    result = nice64.main(argv, stdin=io.StringIO(text), stdout=stdout, stderr=stderr)
                self.assertEqual(result, 0, stderr.getvalue())
                detect.assert_called_once_with(57600)
                factory.assert_called_once_with("detected-port", 57600)
                self.assertIn("PORT detected-port (auto-detected)", stdout.getvalue())
                self.assertIn("80000000", connection.states)

    def test_explicit_port_bypasses_detection(self):
        with patch("nice64.detect_port") as detect, \
                patch("nice64.Nice64Connection", return_value=FakeConnection()) as factory:
            result = nice64.main(["--port", "chosen-port", "hold A"], stdin=io.StringIO(""),
                                 stdout=io.StringIO(), stderr=io.StringIO())
        self.assertEqual(result, 0)
        detect.assert_not_called()
        factory.assert_called_once_with("chosen-port", 115200)

    def test_validation_precedes_auto_detection(self):
        stderr = io.StringIO()
        with patch("nice64.detect_port") as detect, patch("nice64.Nice64Connection") as factory:
            result = nice64.main(["press Invalid"], stdin=io.StringIO(""),
                                 stdout=io.StringIO(), stderr=stderr)
        self.assertEqual(result, 2)
        self.assertIn("unknown input", stderr.getvalue())
        detect.assert_not_called()
        factory.assert_not_called()

    def test_detection_failure_does_not_execute_commands(self):
        stderr = io.StringIO()
        with patch("nice64.detect_port", side_effect=TransportError("multiple NICE64 devices; use --port")), \
                patch("nice64.Nice64Connection") as factory:
            result = nice64.main(["hold A"], stdin=io.StringIO(""),
                                 stdout=io.StringIO(), stderr=stderr)
        self.assertEqual(result, 1)
        self.assertIn("multiple NICE64 devices; use --port", stderr.getvalue())
        factory.assert_not_called()

    def test_input_modes_and_ordered_argument_boundaries(self):
        commands = nice64.read_program([
            "hold Z # ignore; press R", "press A; press B", "release Z"],
            InteractiveInput(), Path.cwd())
        self.assertEqual([command.action for command in commands],
                         ["hold", "press", "press", "release"])
        commands = nice64.read_program([], io.StringIO("press A\npress B"), Path.cwd())
        self.assertEqual([command.inputs.buttons for command in commands],
                         [frozenset(["A"]), frozenset(["B"])])

    def test_empty_redirected_stdin_allows_arguments(self):
        commands = nice64.read_program(["press A"], io.StringIO(""), Path.cwd())
        self.assertEqual(commands[0].inputs.buttons, frozenset(["A"]))

    def test_mixed_input_is_rejected(self):
        with self.assertRaisesRegex(nice64.InputError, "cannot be combined"):
            nice64.read_program(["press A"], io.StringIO("press B"), Path.cwd())

    def test_missing_input_and_empty_commands_are_useful_errors(self):
        for stream in [InteractiveInput(), io.StringIO("")]:
            with self.subTest(stream=type(stream).__name__):
                with self.assertRaisesRegex(nice64.InputError, "no command input"):
                    nice64.read_program([], stream, Path.cwd())
        with self.assertRaisesRegex(nice64.InputError, "no commands"):
            nice64.read_program(["# comment only"], io.StringIO(""), Path.cwd())

    def test_two_argument_run_treats_filename_literally(self):
        with tempfile.TemporaryDirectory() as directory:
            filename = "MiXeD #; 'quoted'.N64SCRIPT"
            path = Path(directory) / filename
            path.write_text("hold Z", encoding="utf-8")
            commands = nice64.read_program(["RUN", filename, "press A", "release Z"],
                                          io.StringIO(""), Path(directory))
            self.assertEqual([command.action for command in commands],
                             ["run_enter", "hold", "run_exit", "press", "release"])
            self.assertEqual(commands[0].path, path.resolve())

    def test_missing_literal_run_filename(self):
        with self.assertRaisesRegex(nice64.InputError, "run requires"):
            nice64.read_program(["run"], io.StringIO(""), Path.cwd())

    def test_literal_run_path_cannot_be_interpreted_as_an_option(self):
        for filename in ("-foo.n64script", "--port=Other.n64script", "--help.n64script"):
            with self.subTest(filename=filename), tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / filename
                path.write_text("hold A", encoding="utf-8")
                connection = FakeConnection()
                with patch("nice64.Path.cwd", return_value=Path(directory)), \
                        patch("nice64.Nice64Connection", return_value=connection) as factory:
                    result = nice64.main(["--port", "correct-port", "run", filename,
                                          "--baud", "57600", "release all"],
                                         stdin=io.StringIO(""), stdout=io.StringIO(), stderr=io.StringIO())
                self.assertEqual(result, 0)
                factory.assert_called_once_with("correct-port", 57600)
                self.assertIn("80000000", connection.states)

    def test_option_value_named_run_is_not_a_command(self):
        parser = nice64.argument_parser()
        args = parser.parse_intermixed_args(nice64.protect_literal_run_paths(
            ["--port", "run", "press A", "--baud=57600"]))
        self.assertEqual(args.port, "run")
        self.assertEqual(args.baudrate, 57600)
        self.assertEqual(args.commands, ["press A"])

    def test_full_validation_precedes_connection_creation(self):
        with tempfile.TemporaryDirectory() as directory:
            bad = Path(directory) / "late.n64script"
            bad.write_text("press B\npress Unknown", encoding="utf-8")
            stdout, stderr = io.StringIO(), io.StringIO()
            with patch("nice64.Nice64Connection") as factory:
                result = nice64.main(["--port", "unused", "press A", "run", str(bad)],
                                     stdin=io.StringIO(""), stdout=stdout, stderr=stderr)
            self.assertEqual(result, 2)
            factory.assert_not_called()
            self.assertIn("late.n64script:2:", stderr.getvalue())
            self.assertEqual(stdout.getvalue(), "")

    def test_order_state_and_run_progress_across_fragments_and_files(self):
        with tempfile.TemporaryDirectory() as directory:
            script = Path(directory) / "inline.n64script"
            script.write_text("press A pause 0ms", encoding="utf-8")
            connection = FakeConnection()
            clock = FakeClock()
            stdout, stderr = io.StringIO(), io.StringIO()

            def execute_without_wait(commands, connection, progress, stderr):
                execute_program(commands, connection, progress, stderr, clock.sleep)

            with patch("nice64.Nice64Connection", return_value=connection) as factory, \
                    patch("nice64.execute_program", side_effect=execute_without_wait):
                result = nice64.main(["hold Z", "--port", "mock", "--baudrate", "57600",
                                      "run", str(script), "press B", "release Z"],
                                     stdin=InteractiveInput(), stdout=stdout, stderr=stderr)
            self.assertEqual(result, 0, stderr.getvalue())
            factory.assert_called_once_with("mock", 57600)
            self.assertEqual(connection.states, [
                "00000000", "20000000", "A0000000", "20000000",
                "60000000", "20000000", "00000000", "00000000"])
            log = stdout.getvalue()
            self.assertLess(log.index("RUN_ENTER"), log.index("PRESS A"))
            self.assertLess(log.index("PRESS A"), log.index("RUN_RETURN"))
            self.assertLess(log.index("RUN_RETURN"), log.index("PRESS B"))
            self.assertEqual(clock.sleeps, [.1, 0, .1, .1])

    def test_interruption_returns_nonzero_with_cleanup(self):
        connection = FakeConnection()

        def interrupted(commands, connection, progress, stderr):
            def interrupt(_):
                raise KeyboardInterrupt()
            execute_program(commands, connection, progress, stderr, interrupt)

        stderr = io.StringIO()
        with patch("nice64.Nice64Connection", return_value=connection), \
                patch("nice64.execute_program", side_effect=interrupted):
            result = nice64.main(["--port", "mock", "press A"], stdin=io.StringIO(""),
                                 stdout=io.StringIO(), stderr=stderr)
        self.assertEqual(result, 130)
        self.assertIn("interrupted", stderr.getvalue())
        self.assertEqual(connection.states[-1], "00000000")
        self.assertTrue(connection.closed)


if __name__ == "__main__":
    unittest.main()
