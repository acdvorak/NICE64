"""The small NICE64 script language and its validation-only include expander."""

from dataclasses import dataclass
from decimal import Decimal, InvalidOperation, ROUND_HALF_UP, localcontext
import math
from pathlib import Path
import re
from typing import Sequence


@dataclass(frozen=True)
class Location:
    source: str
    line: int
    column: int

    def __str__(self) -> str:
        return f"{self.source}:{self.line}:{self.column}"


class ScriptError(Exception):
    def __init__(self, message: str, location: Location):
        self.message = message
        self.location = location
        super().__init__(f"{location}: {message}")


@dataclass(frozen=True)
class Inputs:
    buttons: frozenset[str] = frozenset()
    stick: tuple[int, int] | None = None


@dataclass(frozen=True)
class Command:
    action: str
    location: Location
    inputs: Inputs | None = None
    duration: float | None = None
    pause: float | None = None
    path: Path | None = None


_BUTTON_NAMES = (
    "A", "B", "Z", "L", "R", "Start", "DUp", "DDown", "DLeft", "DRight",
    "CUp", "CDown", "CLeft", "CRight",
)
_BUTTONS = {name.lower(): name for name in _BUTTON_NAMES}
_DIRECTIONS = {
    "stickup": (0, 80), "stickdown": (0, -80),
    "stickleft": (-80, 0), "stickright": (80, 0),
}
_INPUT_ALIASES = {
    alias: prefix + direction
    for prefix in ("d", "c", "stick")
    for direction in ("up", "down", "left", "right")
    for alias in (f"{prefix}-{direction}", f"{prefix}-{direction[0]}",
                  f"{prefix}{direction[0]}")
}
_NUMBER = r"(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?"
_SIGNED_NUMBER = re.compile(r"[+-]?" + _NUMBER)
_DURATION = re.compile(r"(" + _NUMBER + r")\s*(ms|s)\b", re.IGNORECASE)
_WORD = re.compile(r"[A-Za-z][A-Za-z0-9_-]*")


def _location(source: str, text: str, offset: int) -> Location:
    return Location(source, text.count("\n", 0, offset) + 1,
                    offset - text.rfind("\n", 0, offset))


class _Cursor:
    def __init__(self, text: str, source: str, original: str, offset: int):
        self.text = text
        self.source = source
        self.original = original
        self.offset = offset
        self.pos = 0

    def location(self, position: int | None = None) -> Location:
        return _location(self.source, self.original,
                         self.offset + (self.pos if position is None else position))

    def fail(self, message: str, position: int | None = None) -> None:
        raise ScriptError(message, self.location(position))

    def whitespace(self) -> None:
        while self.pos < len(self.text) and self.text[self.pos].isspace():
            self.pos += 1

    def word(self, expected: str) -> str:
        self.whitespace()
        match = _WORD.match(self.text, self.pos)
        if not match:
            self.fail(f"expected {expected}")
        self.pos = match.end()
        return match.group().lower()

    def finished(self) -> bool:
        self.whitespace()
        return self.pos == len(self.text)

    def finish(self) -> None:
        if not self.finished():
            self.fail(f"unexpected trailing text: {self.text[self.pos:]!r}")

    def expect(self, character: str) -> None:
        self.whitespace()
        if self.pos == len(self.text) or self.text[self.pos] != character:
            self.fail(f"expected {character!r}")
        self.pos += 1

    def duration(self) -> float:
        self.whitespace()
        start = self.pos
        match = _DURATION.match(self.text, start)
        if not match:
            self.fail("expected a finite, nonnegative duration with ms or s units")
        try:
            decimal = Decimal(match.group(1))
            if match.group(2).lower() == "ms":
                sign, digits, exponent = decimal.as_tuple()
                decimal = Decimal((sign, digits, exponent - 3))
            value = float(decimal)
        except (InvalidOperation, ValueError, OverflowError):
            self.fail("duration must be finite and nonnegative", start)
        if not math.isfinite(value):
            self.fail("duration must be finite and nonnegative", start)
        self.pos = match.end()
        return value

    def coordinate(self) -> int:
        self.whitespace()
        start = self.pos
        match = _SIGNED_NUMBER.match(self.text, start)
        if not match:
            self.fail("expected a finite stick percentage from -100 through 100")
        try:
            percentage = Decimal(match.group())
            if not percentage.is_finite() or not -100 <= percentage <= 100:
                self.fail("stick percentage must be from -100 through 100", start)
            # Retain every significant decimal digit before symmetric tie rounding.
            with localcontext() as context:
                context.prec = max(28, len(percentage.as_tuple().digits) + 3)
                native = int((percentage * Decimal("0.8")).to_integral_value(rounding=ROUND_HALF_UP))
        except (InvalidOperation, ValueError, OverflowError):
            self.fail("expected a finite stick percentage from -100 through 100", start)
        self.pos = match.end()
        return native

    def inputs(self) -> Inputs:
        buttons: set[str] = set()
        stick = None
        while True:
            self.whitespace()
            start = self.pos
            name = self.word("an input name")
            name = _INPUT_ALIASES.get(name, name)
            if name in _BUTTONS:
                buttons.add(_BUTTONS[name])
            elif name in _DIRECTIONS or name == "stick":
                if stick is not None:
                    self.fail("only one stick specification is allowed in an input list", start)
                if name == "stick":
                    self.expect("(")
                    x = self.coordinate()
                    self.expect(",")
                    y = self.coordinate()
                    self.expect(")")
                    stick = (x, y)
                else:
                    stick = _DIRECTIONS[name]
            else:
                self.fail(f"unknown input {self.text[start:self.pos]!r}", start)
            self.whitespace()
            if self.pos == len(self.text) or self.text[self.pos] != "+":
                return Inputs(frozenset(buttons), stick)
            self.pos += 1


def _segments(text: str, source: str) -> list[tuple[str, int]]:
    """Split only outside quotes/comments, retaining original source offsets."""
    segments: list[tuple[str, int]] = []
    start = 0
    position = 0
    quote = None
    quote_start = 0
    while position < len(text):
        character = text[position]
        if quote is not None:
            if character == quote:
                quote = None
        elif character in "\"'":
            quote = character
            quote_start = position
        elif character == "#":
            segments.append((text[start:position], start))
            newline = text.find("\n", position)
            if newline == -1:
                return segments
            position = newline
            start = position + 1
        elif character in ";\n":
            segments.append((text[start:position], start))
            start = position + 1
        position += 1
    if quote is not None:
        raise ScriptError("unterminated quoted path", _location(source, text, quote_start))
    segments.append((text[start:], start))
    return segments


def parse_fragment(text: str, source: str) -> list[Command]:
    """Parse one input boundary. Includes are retained as unexpanded run commands."""
    commands = []
    for segment, offset in _segments(text, source):
        cursor = _Cursor(segment, source, text, offset)
        if cursor.finished():
            continue
        location = cursor.location()
        verb = cursor.word("a command")
        if verb == "run":
            cursor.whitespace()
            if cursor.finished():
                cursor.fail("run requires a .n64script path")
            if cursor.text[cursor.pos] in "\"'":
                quote = cursor.text[cursor.pos]
                cursor.pos += 1
                end = cursor.text.find(quote, cursor.pos)
                if end == -1:
                    cursor.fail("unterminated quoted path")
                path = cursor.text[cursor.pos:end]
                cursor.pos = end + 1
            else:
                start = cursor.pos
                while cursor.pos < len(cursor.text) and not cursor.text[cursor.pos].isspace():
                    cursor.pos += 1
                path = cursor.text[start:cursor.pos]
            if not path:
                cursor.fail("run path must not be empty")
            cursor.finish()
            commands.append(Command("run", location, path=Path(path)))
        elif verb == "wait":
            duration = 0.1 if cursor.finished() else cursor.duration()
            cursor.finish()
            commands.append(Command("wait", location, duration=duration))
        elif verb in ("press", "hold", "release"):
            cursor.whitespace()
            if verb == "release" and cursor.text[cursor.pos:].lower().strip() == "all":
                commands.append(Command("release_all", location))
                continue
            inputs = cursor.inputs()
            duration = pause = None
            if verb == "press":
                duration = pause = 0.1
                if not cursor.finished():
                    modifier_start = cursor.pos
                    modifier = cursor.word("for or pause")
                    if modifier == "for":
                        duration = cursor.duration()
                        if not cursor.finished():
                            modifier_start = cursor.pos
                            modifier = cursor.word("pause")
                        else:
                            modifier = None
                    if modifier == "pause":
                        pause = cursor.duration()
                    elif modifier is not None:
                        cursor.fail("expected for TIME followed optionally by pause TIME, or pause TIME", modifier_start)
            cursor.finish()
            commands.append(Command(verb, location, inputs=inputs, duration=duration, pause=pause))
        else:
            raise ScriptError(f"unknown command {verb!r}", location)
    return commands


def expand_commands(commands: Sequence[Command], base_dir: Path) -> list[Command]:
    """Read and validate every include before returning an executable program."""
    result: list[Command] = []
    active: list[Path] = []
    # An explicit stack avoids imposing Python's recursion limit on include depth.
    stack = [(iter(commands), Path(base_dir), None)]
    while stack:
        iterator, directory, include_command = stack[-1]
        command = next(iterator, None)
        if command is None:
            stack.pop()
            if include_command is not None:
                active.pop()
                result.append(Command("run_exit", include_command.location, path=include_command.path))
            continue
        if command.action != "run":
            result.append(command)
            continue
        assert command.path is not None
        if command.path.suffix.lower() != ".n64script":
            raise ScriptError("run requires a .n64script file", command.location)
        try:
            path = (directory / command.path).resolve()
        except (OSError, RuntimeError, ValueError) as error:
            raise ScriptError(f"cannot resolve script path {command.path}: {error}", command.location) from error
        if path in active:
            chain = " -> ".join(str(item) for item in [*active, path])
            raise ScriptError(f"active include cycle: {chain}", command.location)
        try:
            text = path.read_text(encoding="utf-8")
        except (OSError, UnicodeError, ValueError) as error:
            raise ScriptError(f"cannot read UTF-8 script {path}: {error}", command.location) from error
        included = parse_fragment(text, str(path))
        entered = Command("run_enter", command.location, path=path)
        result.append(entered)
        active.append(path)
        stack.append((iter(included), path.parent, entered))
    return result


def compile_fragments(fragments: Sequence[tuple[str, str]], base_dir: Path) -> list[Command]:
    """Compile (source identifier, command text) pairs in supplied order."""
    commands = []
    for source, text in fragments:
        commands.extend(parse_fragment(text, source))
    return expand_commands(commands, base_dir)
