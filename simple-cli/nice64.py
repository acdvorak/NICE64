#!/usr/bin/env python3
"""Standalone NICE64 controller scripting CLI; see README.md for the language."""

import argparse
import os
from pathlib import Path
import sys
from typing import Sequence, TextIO

from controller import Progress, execute_program
from language import Command, Location, ScriptError, expand_commands, parse_fragment
from transport import Nice64Connection, TransportError, detect_port


PORT_CACHE_PATH = Path(__file__).resolve().with_name(".last-port")


def read_cached_port() -> str | None:
    try:
        return PORT_CACHE_PATH.read_text(encoding="utf-8").strip() or None
    except (OSError, UnicodeError):
        return None


def remember_port(port: str) -> None:
    try:
        PORT_CACHE_PATH.write_text(port + "\n", encoding="utf-8")
    except (OSError, UnicodeError):
        pass  # An optional speed-up must not prevent controller use.


def cached_port_available(port: str, baudrate: int) -> bool:
    """Check only the cached device, without changing controller state."""
    connection = Nice64Connection(port, baudrate)
    identified = False
    try:
        connection.open()
        connection.identify()
        identified = True
    except TransportError:
        pass
    finally:
        try:
            connection.close()
        except TransportError:
            identified = False
    return identified


class InputError(Exception):
    """Missing or conflicting command input."""


def positive_integer(value: str) -> int:
    try:
        number = int(value)
    except ValueError as error:
        raise argparse.ArgumentTypeError("must be a positive integer") from error
    if number <= 0:
        raise argparse.ArgumentTypeError("must be a positive integer")
    return number


def argument_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Control a NICE64 Controller over USB serial with .n64script commands.",
        epilog=("Examples: nice64.py 'press A; press B'\n"
                "          nice64.py 'press L+R * 4'\n"
                "          nice64.py run example.n64script\n"
                "Or pipe commands on stdin. The Bridge must be disconnected.\n\n"
                "Verbs:\n"
                "  press INPUTS [for TIME] [pause TIME] [*N]\n"
                "  hold INPUTS [*N]\n"
                "  release INPUTS|all [*N]\n"
                "  wait [TIME] [*N]\n"
                "  run PATH [*N]\n\n"
                "*N executes a command N times; N is a positive integer.\n"
                "Spaces around * are optional. Each press repeats its duration and pause\n"
                "(100ms each by default). For run, use: run file.n64script *4.\n"
                "Quoted path stars and the two-argument CLI run PATH stay literal.\n\n"
                "Buttons:\n"
                "  A B Z L R Start\n"
                "  DUp    (aliases: d-up, d-u, du)\n"
                "  DDown  (aliases: d-down, d-d, dd)\n"
                "  DLeft  (aliases: d-left, d-l, dl)\n"
                "  DRight (aliases: d-right, d-r, dr)\n"
                "  CUp    (aliases: c-up, c-u, cu)\n"
                "  CDown  (aliases: c-down, c-d, cd)\n"
                "  CLeft  (aliases: c-left, c-l, cl)\n"
                "  CRight (aliases: c-right, c-r, cr)\n\n"
                "Stick inputs:\n"
                "  StickUp    (aliases: stick-up, stick-u, sticku)\n"
                "  StickDown  (aliases: stick-down, stick-d, stickd)\n"
                "  StickLeft  (aliases: stick-left, stick-l, stickl)\n"
                "  StickRight (aliases: stick-right, stick-r, stickr)\n"
                "  Stick(x,y) (percentages from -100 through 100)\n\n"
                "Verbs and input names are case-insensitive. Join inputs with +.\n"
                "TIME requires ms or s. PATH must name a .n64script file."),
        formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--port", help="USB serial port (default: last NICE64 port, then auto-detect), e.g. /dev/cu.usbmodemXXXX or COM3")
    parser.add_argument("--baud", "--baudrate", dest="baudrate", type=positive_integer,
                        default=115200, help="serial baud rate (default: 115200)")
    parser.add_argument("commands", nargs="*", metavar="COMMAND",
                        help="ordered command fragments, or run PATH; alternatively use stdin")
    return parser


def protect_literal_run_paths(argv: Sequence[str]) -> list[str]:
    """Keep argparse from treating a literal run filename as an option.

    Prefixing an option-looking relative path with ./ preserves its filesystem
    meaning. Option values themselves (for example --port run) are not commands.
    """
    protected = list(argv)
    index = 0
    while index < len(protected):
        argument = protected[index]
        if argument == "--":
            break  # argparse already treats everything remaining as positional.
        if argument in ("--port", "--baud", "--baudrate"):
            index += 2
        elif argument.strip().casefold() == "run" and index + 1 < len(protected):
            if protected[index + 1].startswith("-"):
                protected[index + 1] = "./" + protected[index + 1]
            index += 2
        else:
            index += 1
    return protected


def read_program(fragments: Sequence[str], stdin: TextIO, base_dir: Path) -> list[Command]:
    # A terminal is never read here: arguments must not wait on interactive stdin.
    text = "" if stdin.isatty() else stdin.read()
    if fragments and text:
        raise InputError("command arguments cannot be combined with nonempty stdin; choose one input source. "
                         "Pass --help for usage information.")
    commands: list[Command] = []
    if fragments:
        index = 0
        while index < len(fragments):
            fragment = fragments[index]
            source = f"CLI argument {index + 1}"
            if fragment.strip().casefold() == "run":
                if index + 1 >= len(fragments):
                    raise InputError(f"{source}:1:1: run requires a .n64script path. "
                                     "Pass --help for usage information.")
                # This form deliberately bypasses script tokenization of filenames.
                commands.append(Command("run", Location(source, 1, 1),
                                        path=Path(fragments[index + 1])))
                index += 2
            else:
                commands.extend(parse_fragment(fragment, source))
                index += 1
    elif text:
        commands = parse_fragment(text, "stdin")
    else:
        raise InputError("no command input supplied; pass command arguments or pipe a script on stdin. "
                         "Pass --help for usage information.")
    expanded = expand_commands(commands, base_dir)
    if not expanded:
        raise InputError("input contains no commands. Pass --help for usage information.")
    return expanded


def main(argv: Sequence[str] | None = None, *, stdin: TextIO | None = None,
         stdout: TextIO | None = None, stderr: TextIO | None = None) -> int:
    stdin = sys.stdin if stdin is None else stdin
    stdout = sys.stdout if stdout is None else stdout
    stderr = sys.stderr if stderr is None else stderr
    parser = argument_parser()
    arguments = sys.argv[1:] if argv is None else argv
    args = parser.parse_intermixed_args(protect_literal_run_paths(arguments))
    try:
        commands = read_program(args.commands, stdin, Path.cwd())
        progress = Progress(stdout)
        progress.emit("VALIDATED", "complete program and all included files")
        port = args.port
        if port is None:
            cached = read_cached_port()
            if cached is not None:
                progress.emit("PORT", f"{cached} (cached; checking NICE64 identity)")
            if cached is not None and cached_port_available(cached, args.baudrate):
                port = cached
            else:
                progress.emit("DETECT", "probing serial ports for NICE64 identity")
                port = detect_port(args.baudrate)
                remember_port(port)
                progress.emit("PORT", f"{port} (auto-detected)")
        else:
            progress.emit("PORT", f"{port} (explicit --port)")
        connection = Nice64Connection(port, args.baudrate, on_identified=remember_port)
        execute_program(commands, connection, progress, stderr)
        return 0
    except KeyboardInterrupt:
        print("error: interrupted; attempted neutral cleanup for initialized controller", file=stderr, flush=True)
        return 130
    except (InputError, ScriptError, TransportError, OSError, UnicodeError) as error:
        print(f"error: {error}", file=stderr, flush=True)
        return 2 if isinstance(error, (InputError, ScriptError)) else 1


if __name__ == "__main__":
    status = main()
    # Avoid a second broken-pipe exception during the interpreter's final flush.
    try:
        sys.stdout.flush()
    except BrokenPipeError:
        with open(os.devnull, "w") as sink:
            os.dup2(sink.fileno(), sys.stdout.fileno())
        status = status or 1
    raise SystemExit(status)
