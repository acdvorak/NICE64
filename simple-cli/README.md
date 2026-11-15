# NICE64 serial CLI

A standalone Python 3.10+ tool for sending controller buttons and analog stick
positions directly to a NICE64 Pico 2 W over USB serial. It requires the existing
NICE64 firmware in **Controller** personality. It does not require the Windows
Bridge application; disconnect the Bridge while this tool owns the serial port.

## Install

From this directory:

```sh
python3 -m venv .venv
source .venv/bin/activate
python -m pip install -r requirements.txt
python nice64.py --help
```

The last verified NICE64 device path is saved in `.last-port` beside `nice64.py`,
whether auto-detected or selected with `--port`. When `--port` is omitted, the
CLI checks that cached path first and skips discovery if it still responds as
NICE64. A missing, unreadable, or failed cached path triggers auto-detection.
Cache-write failures do not prevent controller use. Delete `.last-port` to force
discovery on the next invocation without `--port`.

Discovery enumerates serial ports and uses bounded, read-only `WHO` probes to
find NICE64 firmware; it does not change controller inputs or personality. macOS `/dev/tty.*` and
`/dev/cu.*` aliases are probed once, preferring `/dev/cu.*`. If no NICE64 responds
or multiple devices respond, the CLI reports an error; use `--port PORT` to
select a device explicitly and bypass discovery. The selected port is logged.

For manual selection, run `python -m serial.tools.list_ports`. On macOS the
NICE64 port is typically `/dev/cu.usbmodem...`, on Linux `/dev/ttyACM...`, and on
Windows `COM...`. For example:

```sh
python nice64.py --port /dev/cu.usbmodemXXXX 'press A'
```

The baud rate defaults to 115200 for discovery and execution; override it with
`--baud RATE` (or `--baudrate RATE`). Disconnect the Bridge before either form.

## Invoke

Choose either stdin or positional command arguments:

```sh
printf 'press A; press B\n' | python nice64.py

python nice64.py \
  'hold Z' \
  'PRESS A + STICKup for 500ms; press B' \
  'release all'

python nice64.py run examples/menu.n64script

python nice64.py \
  'run "path with spaces/example.n64script"; press Start'
```

Arguments execute in order, and each argument boundary is a command boundary.
An argument may also contain multiple commands separated by semicolons or
newlines. In the conventional two-argument form `run PATH`, the argument after
`run` is a literal filename, preserving its spaces, punctuation, and case.
Use normal shell quoting to keep that filename in one argument:

```sh
python nice64.py run 'My Scripts/Menu #1.n64script'
```

The literal filename may also begin with `-`; it is still consumed as a path.

Nonempty piped or redirected stdin cannot accompany command arguments. Empty
redirected stdin, such as `/dev/null`, is allowed with arguments. With no
arguments, pipe or redirect script text into stdin. Command text is never
executed through a shell.

## Language

```text
press INPUTS [for TIME] [pause TIME]
hold INPUTS
release INPUTS
release all
wait [TIME]
run PATH

INPUTS = INPUT [+ INPUT ...]
TIME   = nonnegative finite number followed by ms or s
```

Commands, keywords, input names, and duration units are case-insensitive. Paths
retain their exact case. Unknown commands, unknown inputs, extra tokens, and
malformed syntax are errors. There are no variables, loops, or expressions.

Buttons:

```text
A B Z L R Start
DUp DDown DLeft DRight
CUp CDown CLeft CRight
```

Stick inputs:

```text
StickUp StickDown StickLeft StickRight
Stick(x,y)
```

Whitespace around `+` and the coordinate comma is optional:

```text
press A+B
PRESS A + B
press A+ B + Start for 500MS pause 0.25S
press A + Stick(50, 75) for 500ms pause 0ms
```

Coordinates are percentages from -100 through 100. Positive X is right and
positive Y is up. Each coordinate is multiplied by 80/100 and rounded to the
nearest integer, with ties rounded away from zero. This gives deterministic,
symmetric native N64 coordinates from -80 through 80; `StickUp` equals
`Stick(0,100)`. Use numeric coordinates for diagonals. An input list may specify
the stick only once.

Blank lines and surrounding whitespace are allowed. Newlines and semicolons
separate commands. A `#` comment lasts until the physical newline or the end of
that CLI argument; a semicolon inside a comment does not start another command.
Single or double quotes around a `run` path protect spaces, semicolons, and `#`:

```text
press A; press B  # this is one comment; press Start is not executed
run 'Scenes/Menu #1; ready.n64script'
```

### Timing and held state

By default, `press` applies its inputs for **100ms**, restores the persistent
held state, then pauses for **100ms**. `for TIME` replaces the press duration;
`pause TIME` replaces the post-press pause. `wait` defaults to **100ms** and is
an additional delay that leaves the controller state unchanged. Durations
require `ms` or `s`; fractional values such as `0.25s` and zero are allowed.
Negative values, NaN, infinity, and missing units are rejected.

`hold` adds buttons to the persistent held state. A specified stick replaces
the held stick position; buttons alone leave the stick unchanged. `release`
removes the listed buttons, and any listed stick input centers the stick.
`release all` releases all buttons and centers the stick together.

`press` temporarily adds buttons and, if supplied, replaces the stick. When the
press ends, it restores the complete persistent state, including buttons that
were already held. For example, Z stays held through the A press and its pause:

```text
hold Z
press A for 500ms
release Z
```

Inputs in one command are sent together in a complete controller report.
Persistent state continues across CLI arguments and included files. `hold`,
`release`, and `run` add no implicit delay. Timing uses host-side sleeps and is
not synchronized to N64 game frames; serial communication also takes time.

### Inline script files

`run PATH` executes a UTF-8 `.n64script` file at that point, then resumes its
caller. The suffix is case-insensitive. Paths in CLI/stdin input resolve from
the current working directory; paths inside a file resolve from that file's
parent directory. Absolute paths are supported. Files can include other files
and can run repeatedly. Active recursive includes are rejected with a file
chain.

The complete program and all included files are parsed and validated **before
opening any serial port, including discovery probes**. Errors identify the source and location where
practical, so a late syntax error cannot execute earlier controller actions.
See [menu.n64script](examples/menu.n64script),
[held-stick.n64script](examples/held-stick.n64script), and
[session.n64script](examples/session.n64script).

## Progress, protocol, and cleanup

Progress is line-oriented on stdout, with elapsed timestamps, action names,
inputs, and resolved durations. Every line is flushed immediately, including
when output is redirected or piped; `python -u` is unnecessary. Logs announce
presses, state changes, restoration of persistent holds, waits and pauses before
sleeping, entry/return from files, completion, and neutral cleanup. Errors go to
stderr and failures return a nonzero exit status.

Discovery probes are closed before the selected port is opened for execution.
The execution connection stays open until cleanup. Initialization verifies NICE64
identity and Controller personality, enables serial controller input, and
starts neutral. Another personality is reported without rebooting or switching
it. Serial reads and writes have bounded timeouts; unexpected replies and
ownership conflicts are errors. Firmware acknowledgements indicate command
acceptance, not that the N64 console or game observed the input.
After a failure, cleanup responses are reported conservatively: a delayed ACK
may belong to an earlier command because the protocol has no transaction IDs.

On success, Ctrl+C, or execution failure, the tool attempts to send neutral
before closing. This attempt is bounded and also runs if stdout fails. Cleanup
failure is reported without replacing the original error. A disconnected or
unresponsive device may prevent neutral from reaching it.

## Offline validation

```sh
python3 -m unittest discover -s tests -v
```

Tests use mocked serial I/O and timing. They send no input to a real controller;
passing them does not constitute hardware testing.
