namespace PicoSwitchController;

using SharpDX.XInput;

public sealed record ControllerButtonDefinition(string Name, int Bit, Rectangle Bounds);

public sealed class ControllerProfile
{
    private const byte XInputTriggerThreshold = 30;

    public required ConsoleControllerType Type { get; init; }
    public required string DisplayName { get; init; }
    public required IReadOnlyList<ControllerButtonDefinition> Buttons { get; init; }
    public required Dictionary<string, int> ButtonBits { get; init; }

    public static ControllerProfile Create(ConsoleControllerType type)
    {
        return type == ConsoleControllerType.N64 ? CreateN64() : CreateSnes();
    }

    public static string GetMappingPath(ConsoleControllerType type)
    {
        string fileName = type == ConsoleControllerType.N64
            ? "controller-mapping-n64.json"
            : "controller-mapping-snes.json";

        return Path.Combine(AppContext.BaseDirectory, fileName);
    }

    public static string BuildCommand(ConsoleControllerType type, uint mask)
        => BridgeControllerCommandBuilder.BuildCommand(type, mask);

    public static string BuildCommand(ConsoleControllerType type, uint mask, sbyte n64StickX, sbyte n64StickY)
        => BridgeControllerCommandBuilder.BuildCommand(type, mask, n64StickX, n64StickY);

    public static uint ReadXInputMask(ConsoleControllerType type, Controller controller, Dictionary<string, int> buttonBits)
    {
        Gamepad gamepad = controller.GetState().Gamepad;
        return type == ConsoleControllerType.N64
            ? ReadN64XInputMask(gamepad, buttonBits)
            : ReadSnesXInputMask(gamepad.Buttons, buttonBits);
    }

    private static ControllerProfile CreateSnes()
    {
        ControllerButtonDefinition[] buttons =
        [
            new("L", 10, new Rectangle(122, 28, 70, 36)),
            new("R", 11, new Rectangle(426, 28, 70, 36)),
            new("Up", 4, new Rectangle(112, 102, 70, 36)),
            new("Left", 6, new Rectangle(44, 148, 70, 36)),
            new("Right", 7, new Rectangle(180, 148, 70, 36)),
            new("Down", 5, new Rectangle(112, 194, 70, 36)),
            new("Select", 2, new Rectangle(248, 200, 78, 34)),
            new("Start", 3, new Rectangle(344, 200, 78, 34)),
            new("Y", 1, new Rectangle(424, 132, 62, 38)),
            new("X", 9, new Rectangle(488, 82, 62, 38)),
            new("B", 0, new Rectangle(488, 184, 62, 38)),
            new("A", 8, new Rectangle(552, 134, 62, 38))
        ];

        return new ControllerProfile
        {
            Type = ConsoleControllerType.Snes,
            DisplayName = "SNES",
            Buttons = buttons,
            ButtonBits = buttons.ToDictionary(button => button.Name, button => button.Bit)
        };
    }

    private static ControllerProfile CreateN64()
    {
        ControllerButtonDefinition[] buttons =
        [
            new("L", 10, new Rectangle(118, 24, 62, 34)),
            new("R", 11, new Rectangle(430, 24, 62, 34)),
            new("Z", 2, new Rectangle(358, 248, 62, 38)),
            new("Start", 3, new Rectangle(284, 100, 78, 36)),
            new("DUp", 4, new Rectangle(104, 96, 66, 34)),
            new("DLeft", 6, new Rectangle(50, 138, 66, 34)),
            new("DRight", 7, new Rectangle(158, 138, 66, 34)),
            new("DDown", 5, new Rectangle(104, 180, 66, 34)),
            new("Stick Up", 14, new Rectangle(270, 144, 74, 32)),
            new("Stick Left", 16, new Rectangle(206, 184, 74, 32)),
            new("Stick Right", 17, new Rectangle(334, 184, 74, 32)),
            new("Stick Down", 15, new Rectangle(270, 224, 74, 32)),
            new("B", 1, new Rectangle(408, 124, 58, 38)),
            new("A", 0, new Rectangle(472, 146, 58, 38)),
            new("CUp", 12, new Rectangle(532, 72, 58, 34)),
            new("CLeft", 18, new Rectangle(494, 108, 58, 34)),
            new("CRight", 19, new Rectangle(570, 108, 58, 34)),
            new("CDown", 13, new Rectangle(532, 144, 58, 34))
        ];

        return new ControllerProfile
        {
            Type = ConsoleControllerType.N64,
            DisplayName = "N64",
            Buttons = buttons,
            ButtonBits = buttons.ToDictionary(button => button.Name, button => button.Bit)
        };
    }

    private static uint ReadSnesXInputMask(GamepadButtonFlags buttons, Dictionary<string, int> buttonBits)
    {
        uint mask = 0;
        AddXInputButton(buttons, GamepadButtonFlags.A, "B", buttonBits, ref mask);
        AddXInputButton(buttons, GamepadButtonFlags.X, "Y", buttonBits, ref mask);
        AddXInputButton(buttons, GamepadButtonFlags.Back, "Select", buttonBits, ref mask);
        AddXInputButton(buttons, GamepadButtonFlags.Start, "Start", buttonBits, ref mask);
        AddXInputButton(buttons, GamepadButtonFlags.DPadUp, "Up", buttonBits, ref mask);
        AddXInputButton(buttons, GamepadButtonFlags.DPadDown, "Down", buttonBits, ref mask);
        AddXInputButton(buttons, GamepadButtonFlags.DPadLeft, "Left", buttonBits, ref mask);
        AddXInputButton(buttons, GamepadButtonFlags.DPadRight, "Right", buttonBits, ref mask);
        AddXInputButton(buttons, GamepadButtonFlags.B, "A", buttonBits, ref mask);
        AddXInputButton(buttons, GamepadButtonFlags.Y, "X", buttonBits, ref mask);
        AddXInputButton(buttons, GamepadButtonFlags.LeftShoulder, "L", buttonBits, ref mask);
        AddXInputButton(buttons, GamepadButtonFlags.RightShoulder, "R", buttonBits, ref mask);
        return mask;
    }

    private static uint ReadN64XInputMask(Gamepad gamepad, Dictionary<string, int> buttonBits)
    {
        uint mask = 0;
        GamepadButtonFlags buttons = gamepad.Buttons;

        AddXInputButton(buttons, GamepadButtonFlags.A, "A", buttonBits, ref mask);
        AddXInputButton(buttons, GamepadButtonFlags.B, "B", buttonBits, ref mask);
        AddXInputButton(buttons, GamepadButtonFlags.Back, "Z", buttonBits, ref mask);
        AddXInputTrigger(gamepad.LeftTrigger, "Z", buttonBits, ref mask);
        AddXInputButton(buttons, GamepadButtonFlags.Start, "Start", buttonBits, ref mask);
        AddXInputButton(buttons, GamepadButtonFlags.DPadUp, "DUp", buttonBits, ref mask);
        AddXInputButton(buttons, GamepadButtonFlags.DPadDown, "DDown", buttonBits, ref mask);
        AddXInputButton(buttons, GamepadButtonFlags.DPadLeft, "DLeft", buttonBits, ref mask);
        AddXInputButton(buttons, GamepadButtonFlags.DPadRight, "DRight", buttonBits, ref mask);
        AddXInputButton(buttons, GamepadButtonFlags.LeftShoulder, "L", buttonBits, ref mask);
        AddXInputButton(buttons, GamepadButtonFlags.RightShoulder, "R", buttonBits, ref mask);

        AddAnalogDirection(gamepad.LeftThumbX, 12000, "Stick Right", "Stick Left", buttonBits, ref mask);
        AddAnalogDirection(gamepad.LeftThumbY, 12000, "Stick Up", "Stick Down", buttonBits, ref mask);
        AddAnalogDirection(gamepad.RightThumbX, 12000, "CRight", "CLeft", buttonBits, ref mask);
        AddAnalogDirection(gamepad.RightThumbY, 12000, "CUp", "CDown", buttonBits, ref mask);

        return mask;
    }

    private static void AddXInputButton(
        GamepadButtonFlags buttons,
        GamepadButtonFlags flag,
        string logicalButton,
        Dictionary<string, int> buttonBits,
        ref uint mask)
    {
        if (buttons.HasFlag(flag) && buttonBits.TryGetValue(logicalButton, out int bit))
        {
            mask |= 1u << bit;
        }
    }

    private static void AddXInputTrigger(
        byte value,
        string logicalButton,
        Dictionary<string, int> buttonBits,
        ref uint mask)
    {
        if (value >= XInputTriggerThreshold && buttonBits.TryGetValue(logicalButton, out int bit))
        {
            mask |= 1u << bit;
        }
    }

    private static void AddAnalogDirection(
        short value,
        short threshold,
        string positiveButton,
        string negativeButton,
        Dictionary<string, int> buttonBits,
        ref uint mask)
    {
        if (value >= threshold && buttonBits.TryGetValue(positiveButton, out int positiveBit))
        {
            mask |= 1u << positiveBit;
        }
        else if (value <= -threshold && buttonBits.TryGetValue(negativeButton, out int negativeBit))
        {
            mask |= 1u << negativeBit;
        }
    }

}
