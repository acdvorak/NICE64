"""Persistent N64 input state and observable, host-timed execution."""

from dataclasses import dataclass
import time
from typing import Callable, Sequence, TextIO

from language import Command, Inputs
from transport import Nice64Connection, TransportError


# Canonical wire bits, from BridgeControllerCommandBuilder.WriteCanonicalN64State.
BUTTON_BITS = {
    "A": 0x8000, "B": 0x4000, "Z": 0x2000, "Start": 0x1000,
    "DUp": 0x0800, "DDown": 0x0400, "DLeft": 0x0200, "DRight": 0x0100,
    "L": 0x0020, "R": 0x0010,
    "CUp": 0x0008, "CDown": 0x0004, "CLeft": 0x0002, "CRight": 0x0001,
}


@dataclass(frozen=True)
class State:
    buttons: frozenset[str] = frozenset()
    stick: tuple[int, int] = (0, 0)

    def overlay(self, inputs: Inputs) -> "State":
        return State(self.buttons | inputs.buttons,
                     self.stick if inputs.stick is None else inputs.stick)

    def release(self, inputs: Inputs) -> "State":
        return State(self.buttons - inputs.buttons,
                     self.stick if inputs.stick is None else (0, 0))

    def encode(self) -> str:
        mask = 0
        for button in self.buttons:
            mask |= BUTTON_BITS[button]
        return bytes((mask >> 8, mask & 255,
                      self.stick[0] & 255, self.stick[1] & 255)).hex().upper()

    def describe(self) -> str:
        buttons = "+".join(name for name in BUTTON_BITS if name in self.buttons)
        return f"buttons={buttons or 'none'} stick=({self.stick[0]},{self.stick[1]})"


def describe_inputs(inputs: Inputs) -> str:
    names = [name for name in BUTTON_BITS if name in inputs.buttons]
    if inputs.stick is not None:
        names.append(f"StickNative({inputs.stick[0]},{inputs.stick[1]})")
    return "+".join(names)


def milliseconds(seconds: float) -> str:
    return f"{seconds * 1000:g}ms"


class Progress:
    def __init__(self, stream: TextIO,
                 clock: Callable[[], float] = time.monotonic) -> None:
        self.stream = stream
        self.clock = clock
        self.started = clock()

    def emit(self, action: str, detail: str) -> None:
        detail = detail.replace("\r", "\\r").replace("\n", "\\n")
        print(f"[{self.clock() - self.started:9.3f}s] {action} {detail}",
              file=self.stream, flush=True)


def execute_program(commands: Sequence[Command], connection: Nice64Connection,
                    progress: Progress, stderr: TextIO,
                    sleep: Callable[[float], None] = time.sleep) -> None:
    """Own the connection for one invocation; always try bounded neutral cleanup.

    An ACK means the firmware accepted a report, not that a game observed it.
    Cleanup logging is independent of cleanup I/O so a broken pipe cannot leave
    a held controller state behind merely by preventing the neutral write.
    """
    held = State()
    original_error: BaseException | None = None
    cleanup_error: BaseException | None = None

    def transition(state: State, reason: str) -> None:
        connection.send_state(state.encode())
        progress.emit("STATE", f"{reason}: {state.describe()} N64 {state.encode()} "
                      "ACK=OK (firmware accepted)")

    try:
        progress.emit("CONNECT", "opening serial connection")
        connection.open()
        connection.initialize()
        progress.emit("READY", "NICE64 Controller verified; ACK=OK MODE SERIAL")
        transition(held, "initial neutral")
        for command in commands:
            action = command.action
            location = command.location
            source = f"{location.source}:{location.line}:{location.column}"
            if action in ("run_enter", "run_exit"):
                progress.emit("RUN_ENTER" if action == "run_enter" else "RUN_RETURN",
                              str(command.path))
            elif action == "wait":
                progress.emit("WAIT", f"{milliseconds(command.duration)} at {source}")
                sleep(command.duration)
            elif action == "release_all":
                progress.emit("RELEASE_ALL", f"at {source}")
                held = State()
                transition(held, "release all")
            else:
                inputs = command.inputs
                detail = f"{describe_inputs(inputs)} at {source}"
                if action == "press":
                    detail += (f" hold={milliseconds(command.duration)}"
                               f" pause={milliseconds(command.pause)}")
                progress.emit(action.upper(), detail)
                if action == "hold":
                    held = held.overlay(inputs)
                    transition(held, "persistent hold")
                elif action == "release":
                    held = held.release(inputs)
                    transition(held, "persistent release")
                elif action == "press":
                    transition(held.overlay(inputs), "temporary press applied")
                    progress.emit("HOLD_FOR", milliseconds(command.duration))
                    sleep(command.duration)
                    transition(held, "restore persistent held state")
                    progress.emit("PAUSE", milliseconds(command.pause))
                    sleep(command.pause)
                else:
                    raise ValueError(f"Unsupported compiled action: {action}")
    except BaseException as error:
        original_error = error
    finally:
        def cleanup_log(action: str, detail: str) -> None:
            nonlocal original_error
            try:
                progress.emit(action, detail)
            except BaseException as error:
                if original_error is None:
                    original_error = error

        if connection.controller_ready:
            cleanup_log("CLEANUP", "sending neutral: release all buttons and center stick")
            try:
                connection.send_state("00000000")
            except BaseException as error:
                cleanup_error = error
            else:
                if original_error is None:
                    cleanup_log("STATE", "final neutral: buttons=none stick=(0,0) "
                                "N64 00000000 ACK=OK (firmware accepted)")
                else:
                    # The wire protocol has no transaction IDs; a delayed OK
                    # from the failed command can arrive during cleanup.
                    cleanup_log("CLEANUP", "neutral report sent: N64 00000000; response=OK "
                                "(after failure, ACK may belong to an earlier command)")
        try:
            connection.close()
        except BaseException as error:
            if cleanup_error is None:
                cleanup_error = error

    if original_error is not None:
        if cleanup_error is not None:
            try:
                print(f"warning: controller cleanup failed: {cleanup_error}",
                      file=stderr, flush=True)
            except OSError:
                pass
        raise original_error
    if cleanup_error is not None:
        raise TransportError(f"Controller cleanup failed: {cleanup_error}") from cleanup_error
    progress.emit("COMPLETE", "program finished; neutral cleanup acknowledged; connection closed")
