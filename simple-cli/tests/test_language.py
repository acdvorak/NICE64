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

    def test_repetition_matches_explicit_commands_with_optional_spacing(self):
        expected = self.parse("press L+R; press L+R; press L+R; press L+R")
        for suffix in ("*4", "* 4", " *4", " * 4", "\t*\t4"):
            with self.subTest(suffix=suffix):
                commands = self.parse("press L+R" + suffix)
                self.assertEqual(len(commands), 4)
                for command, explicit in zip(commands, expected):
                    self.assertEqual((command.action, command.inputs, command.duration, command.pause),
                                     (explicit.action, explicit.inputs, explicit.duration, explicit.pause))
                    self.assertEqual(command.location, Location("CLI argument 1", 1, 1))

    def test_repetition_applies_to_each_command_and_preserves_timing(self):
        cases = ("press A for 250ms pause 0ms", "press A for 250ms",
                 "press A pause 50ms", "hold Z+StickUp", "release Z",
                 "release all", "wait", "wait 2s", 'run "Case * 3.n64script"')
        for text in cases:
            with self.subTest(text=text):
                command, = self.parse(text)
                self.assertEqual(self.parse(text + " * 3"), [command] * 3)
                self.assertEqual(self.parse(text + " *1"), [command])

    def test_repetition_respects_separators_comments_and_source_locations(self):
        commands = self.parse("hold Z; press A *2 # *99; press B\n release all*2; wait")
        self.assertEqual([command.action for command in commands],
                         ["hold", "press", "press", "release_all", "release_all", "wait"])
        self.assertEqual([command.location for command in commands], [
            Location("CLI argument 1", 1, 1), Location("CLI argument 1", 1, 9),
            Location("CLI argument 1", 1, 9), Location("CLI argument 1", 2, 2),
            Location("CLI argument 1", 2, 2), Location("CLI argument 1", 2, 17)])

    def test_invalid_repetition_is_rejected_with_source_location(self):
        for suffix in ("*", "*0", "*-1", "*+2", "*1.5", "*NaN", "*2e3",
                       "*2extra", "*2 *3", "*2 for 1s"):
            with self.subTest(suffix=suffix), self.assertRaises(ScriptError) as error:
                parse_fragment("# heading\npress L+R " + suffix, "input.n64script")
            self.assertEqual(error.exception.location.source, "input.n64script")
            self.assertEqual(error.exception.location.line, 2)
        with self.assertRaises(ScriptError) as error:
            parse_fragment("# heading\npress L+R * 0", "input.n64script")
        self.assertEqual(error.exception.location, Location("input.n64script", 2, 13))

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

    def test_directional_aliases_normalize_for_each_input_command(self):
        directions = (("up", (0, 80)), ("down", (0, -80)),
                      ("left", (-80, 0)), ("right", (80, 0)))
        for prefix in ("d", "c", "stick"):
            for direction, coordinates in directions:
                expected = (Inputs(stick=coordinates) if prefix == "stick" else
                            Inputs(frozenset({prefix.upper() + direction.title()})))
                for alias in (f"{prefix}-{direction}", f"{prefix}-{direction[0]}",
                              f"{prefix}{direction[0]}"):
                    for spelling in (alias, alias.upper(), alias.title()):
                        for verb in ("press", "hold", "release"):
                            with self.subTest(verb=verb, spelling=spelling):
                                command, = self.parse(f"{verb} {spelling}")
                                self.assertEqual(command.action, verb)
                                self.assertEqual(command.inputs, expected)

    def test_directional_aliases_combine_with_other_inputs(self):
        for verb in ("press", "hold", "release"):
            with self.subTest(verb=verb):
                command, = self.parse(f"{verb} A + d-up + DR + c-l + cd + Stick-R")
                self.assertEqual(command.inputs,
                                 Inputs(frozenset({"A", "DUp", "DRight", "CLeft", "CDown"}),
                                        (80, 0)))

    def test_directional_aliases_do_not_allow_multiple_stick_inputs(self):
        for verb in ("press", "hold", "release"):
            for inputs in ("stick-u + stickright", "stick-left + stickr",
                           "Stick(0,0) + stick-d", "stickup + stick-u"):
                with self.subTest(verb=verb, inputs=inputs):
                    with self.assertRaisesRegex(ScriptError, "only one stick specification"):
                        self.parse(f"{verb} {inputs}")

    def test_other_directional_spellings_are_rejected(self):
        for name in ("d_up", "d--up", "c-upright", "stick--u", "stick_u"):
            with self.subTest(name=name), self.assertRaises(ScriptError):
                self.parse(f"press {name}")

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

    def test_repetition_in_files_and_on_run_commands_expands_inline(self):
        path = self.write("child/Case * 3.n64script", "press A *2; wait *2")
        self.write("outer.n64script", 'run "child/Case * 3.n64script" *2')
        commands = self.compile("hold Z; run outer.n64script *2; release all")
        inner_actions = ["run_enter", "press", "press", "wait", "wait", "run_exit"]
        self.assertEqual([command.action for command in commands],
                         ["hold"] + (["run_enter"] + inner_actions * 2 + ["run_exit"]) * 2
                         + ["release_all"])
        self.assertEqual([command.path for command in commands if command.path == path], [path] * 8)

    def test_repeated_run_still_rejects_active_cycles(self):
        self.write("cycle.n64script", "run cycle.n64script *2")
        with self.assertRaisesRegex(ScriptError, "active include cycle"):
            self.compile("run cycle.n64script *2")

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
