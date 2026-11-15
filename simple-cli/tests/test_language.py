"""Observable language and include behavior; no serial dependency or hardware."""

from pathlib import Path
import tempfile
import unittest

from language import Command, Inputs, Location, ScriptError, compile_fragments, expand_commands, parse_fragment


class LanguageTests(unittest.TestCase):
    def parse(self, text):
        return parse_fragment(text, "CLI argument 1")

    def test_case_and_input_spacing(self):
        for text in ("press A+B", "PRESS A + B", "press a+ B"):
            with self.subTest(text=text):
                command, = self.parse(text)
                self.assertEqual(command.inputs, Inputs(frozenset({"A", "B"})))
                self.assertEqual((command.duration, command.pause), (0.1, 0.1))
        command, = self.parse("press A+ B + Start for 500MS pause 0.25S")
        self.assertEqual(command.inputs.buttons, {"A", "B", "Start"})
        self.assertEqual((command.duration, command.pause), (0.5, 0.25))

    def test_separators_and_comments(self):
        commands = self.parse("\n # ignored; press Z\n press A; press B # ;press Z\n press Start;\n")
        self.assertEqual([command.inputs.buttons for command in commands],
                         [{"A"}, {"B"}, {"Start"}])
        self.assertEqual([(command.location.line, command.location.column) for command in commands],
                         [(3, 2), (3, 11), (4, 2)])

    def test_comment_ends_at_fragment_boundary(self):
        commands = compile_fragments([("arg 1", "hold Z # comment; press X"),
                                      ("arg 2", "press A")], Path.cwd())
        self.assertEqual([command.action for command in commands], ["hold", "press"])
        self.assertEqual(commands[1].inputs.buttons, {"A"})

    def test_quoted_paths_are_literal(self):
        for quote in ("'", '"'):
            with self.subTest(quote=quote):
                command, = self.parse(f"RUN {quote}Case sensitive; # path.n64script{quote} # comment")
                self.assertEqual(command.path, Path("Case sensitive; # path.n64script"))
        command, = self.parse(r'run "directory\Case.n64script"')
        self.assertEqual(str(command.path), r"directory\Case.n64script")

    def test_default_and_explicit_durations(self):
        commands = self.parse("wait; wait 0ms; wait .25S; press A pause 0ms; press B for 2s")
        self.assertEqual([command.duration for command in commands], [0.1, 0, 0.25, 0.1, 2])
        self.assertEqual([command.pause for command in commands], [None, None, None, 0, 0.1])

    def test_numeric_stick_scaling_and_symmetric_rounding(self):
        commands = self.parse("press Stick(50, 75); press Stick(-0.625,0.625); hold Stick(100, -100)")
        self.assertEqual([command.inputs.stick for command in commands],
                         [(40, 60), (-1, 1), (80, -80)])
        command, = self.parse("press Stick(0.624999999999999999999999999999999, -0.624999999999999999999999999999999)")
        self.assertEqual(command.inputs.stick, (0, 0))

    def test_cardinal_directions_match_numeric_coordinates(self):
        for name, coordinates in (("StickUp", "0,100"), ("StickDown", "0,-100"),
                                  ("StickLeft", "-100,0"), ("StickRight", "100,0")):
            with self.subTest(name=name):
                self.assertEqual(self.parse(f"hold {name}")[0].inputs,
                                 self.parse(f"hold Stick({coordinates})")[0].inputs)

    def test_all_supported_buttons(self):
        names = "A B Z L R Start DUp DDown DLeft DRight CUp CDown CLeft CRight".split()
        command, = self.parse("hold " + " + ".join(names))
        self.assertEqual(command.inputs.buttons, set(names))

    def test_release_semantics_are_distinguished(self):
        commands = self.parse("release all; release A + StickUp; hold Z")
        self.assertEqual([command.action for command in commands], ["release_all", "release", "hold"])
        self.assertEqual(commands[1].inputs, Inputs(frozenset({"A"}), (0, 80)))
        self.assertIsNone(commands[2].duration)

    def test_invalid_syntax_is_rejected(self):
        invalid = (
            "unknown A", "press Unknown", "press", "hold", "release", "press A +",
            "press A B", "hold A for 1s", "release all A", "wait 1", "wait -1s",
            "wait NaNs", "wait infs", "wait Infinitys", "wait 1e10000s",
            "wait 1e1000000s",
            "press A pause 1s for 1s", "press A for 1s pause 1s extra",
            "press A for 1s for 1s", "press A for 1s pause", "press A pause 1",
            "wait 0s trailing", "run", "run ''", "run \"a.n64script\" trailing",
            "run file with spaces.n64script", "run 'unterminated.n64script",
            "press StickUp + StickRight", "release Stick(0,0) + StickUp",
            "hold Stick(-101,0)", "press Stick(0,100.01)", "press Stick(NaN,0)",
            "press Stick(0,inf)", "press Stick(1)", "press Stick(1,2,3)",
        )
        for text in invalid:
            with self.subTest(text=text), self.assertRaises(ScriptError):
                self.parse(text)

    def test_errors_identify_original_source_line_and_column(self):
        with self.assertRaises(ScriptError) as error:
            parse_fragment("# heading\npress A; hold Unknown", "input.n64script")
        self.assertIn("input.n64script:2:15:", str(error.exception))
        self.assertIn("unknown input", str(error.exception))


class IncludeTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name).resolve()

    def write(self, path, text):
        destination = self.directory / path
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text(text, encoding="utf-8")
        return destination

    def compile(self, text):
        return compile_fragments([("stdin", text)], self.directory)

    def test_nested_relative_includes_execute_inline_and_resume(self):
        self.write("child/inner.n64script", "press B")
        self.write("child/outer.n64script", "hold Z; run inner.n64script; release Z")
        commands = self.compile("press A; run child/outer.n64script; press Start")
        self.assertEqual([command.action for command in commands],
                         ["press", "run_enter", "hold", "run_enter", "press",
                          "run_exit", "release", "run_exit", "press"])
        self.assertEqual(commands[3].path, self.directory / "child/inner.n64script")
        self.assertEqual(commands[5].path, commands[3].path)

    def test_repeated_files_are_allowed_and_suffix_is_case_insensitive(self):
        path = self.write("Example.N64SCRIPT", "press A")
        commands = self.compile("run Example.N64SCRIPT; run Example.N64SCRIPT")
        self.assertEqual([command.action for command in commands],
                         ["run_enter", "press", "run_exit"] * 2)
        self.assertEqual(commands[0].path, path)

    def test_absolute_and_literal_paths_preserve_punctuation(self):
        path = self.write("Case; # 'literal'.n64script", "press A")
        command = Command("run", Location("CLI argument 1", 1, 1), path=path)
        expanded = expand_commands([command], self.directory / "elsewhere")
        self.assertEqual(expanded[0].path, path)
        self.assertEqual(expanded[1].inputs.buttons, {"A"})

    def test_active_cycle_reports_complete_chain(self):
        first = self.write("a.n64script", "run sub/b.n64script")
        second = self.write("sub/b.n64script", "run ../a.n64script")
        with self.assertRaises(ScriptError) as error:
            self.compile("run a.n64script")
        self.assertIn(f"{first} -> {second} -> {first}", str(error.exception))
        self.assertIn(f"{second}:1:1", str(error.exception))

    def test_symlink_alias_does_not_hide_cycle(self):
        path = self.write("a.n64script", "run alias.n64script")
        (self.directory / "alias.n64script").symlink_to(path)
        with self.assertRaisesRegex(ScriptError, "active include cycle"):
            self.compile("run a.n64script")

    def test_late_included_errors_prevent_a_compiled_program(self):
        path = self.write("bad.n64script", "press B\npress Z\nunknown A")
        with self.assertRaises(ScriptError) as error:
            self.compile("press A; run bad.n64script")
        self.assertIn(f"{path}:3:1", str(error.exception))

    def test_missing_non_utf8_and_wrong_suffix_errors(self):
        self.write("wrong.txt", "press A")
        (self.directory / "bad.n64script").write_bytes(b"\xff")
        for text in ("run missing.n64script", "run wrong.txt", "run bad.n64script"):
            with self.subTest(text=text), self.assertRaises(ScriptError) as error:
                self.compile(text)
            self.assertIn("stdin:1:1:", str(error.exception))

    def test_quoted_filenames_contain_separators_and_hashes(self):
        path = self.write("Name ; # file.n64script", "press A")
        commands = self.compile('run "Name ; # file.n64script"; press B')
        self.assertEqual(commands[0].path, path)
        self.assertEqual(commands[-1].inputs.buttons, {"B"})


if __name__ == "__main__":
    unittest.main()
