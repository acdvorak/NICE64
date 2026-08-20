namespace PicoSwitchController;

using System.Text.Json;

public sealed class ControllerMapping
{
    public Dictionary<string, InputBinding> Bindings { get; set; } = new();

    public static ControllerMapping Load(string path)
    {
        if (!File.Exists(path))
        {
            return CreateDefault(path.Contains("n64", StringComparison.OrdinalIgnoreCase)
                ? ConsoleControllerType.N64
                : ConsoleControllerType.Snes);
        }

        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ControllerMapping>(json)
                ?? CreateDefault(path.Contains("n64", StringComparison.OrdinalIgnoreCase)
                    ? ConsoleControllerType.N64
                    : ConsoleControllerType.Snes);
        }
        catch
        {
            return CreateDefault(path.Contains("n64", StringComparison.OrdinalIgnoreCase)
                ? ConsoleControllerType.N64
                : ConsoleControllerType.Snes);
        }
    }

    public static ControllerMapping LoadEightBitDoParsec(string path)
    {
        if (!File.Exists(path))
        {
            return CreateEightBitDoParsecN64();
        }

        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ControllerMapping>(json)
                ?? CreateEightBitDoParsecN64();
        }
        catch
        {
            return CreateEightBitDoParsecN64();
        }
    }

    public void Save(string path)
    {
        JsonSerializerOptions options = new() { WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(this, options));
    }

    public static ControllerMapping CreateDefault(ConsoleControllerType type = ConsoleControllerType.Snes)
    {
        ControllerMapping mapping = new()
        {
            Bindings =
            {
            }
        };

        if (type == ConsoleControllerType.N64)
        {
            mapping.Bindings["DUp"] = InputBinding.Pov("Up");
            mapping.Bindings["DDown"] = InputBinding.Pov("Down");
            mapping.Bindings["DLeft"] = InputBinding.Pov("Left");
            mapping.Bindings["DRight"] = InputBinding.Pov("Right");
            mapping.Bindings["Stick Up"] = InputBinding.Axis("Y", "-");
            mapping.Bindings["Stick Down"] = InputBinding.Axis("Y", "+");
            mapping.Bindings["Stick Left"] = InputBinding.Axis("X", "-");
            mapping.Bindings["Stick Right"] = InputBinding.Axis("X", "+");
            mapping.Bindings["A"] = InputBinding.Button(0);
            mapping.Bindings["B"] = InputBinding.Button(1);
            mapping.Bindings["L"] = InputBinding.Button(4);
            mapping.Bindings["R"] = InputBinding.Button(5);
            mapping.Bindings["Z"] = InputBinding.Button(6);
            mapping.Bindings["Start"] = InputBinding.Button(9);
        }
        else
        {
            mapping.Bindings["Up"] = InputBinding.Pov("Up");
            mapping.Bindings["Down"] = InputBinding.Pov("Down");
            mapping.Bindings["Left"] = InputBinding.Pov("Left");
            mapping.Bindings["Right"] = InputBinding.Pov("Right");
        }

        return mapping;
    }

    public static ControllerMapping CreateEightBitDoParsecN64()
    {
        ControllerMapping mapping = CreateDefault(ConsoleControllerType.N64);
        mapping.Bindings["A"] = InputBinding.Axis("Z", "+");
        mapping.Bindings["B"] = InputBinding.Button(6);
        mapping.Bindings["Z"] = InputBinding.Axis("Z", "-");
        mapping.Bindings["Start"] = InputBinding.Button(7);
        mapping.Bindings["CUp"] = InputBinding.Button(2);
        mapping.Bindings["CLeft"] = InputBinding.Button(3);
        mapping.Bindings["CRight"] = InputBinding.Button(0);
        mapping.Bindings["CDown"] = InputBinding.Button(1);
        return mapping;
    }
}

public sealed class InputBinding
{
    public string Kind { get; set; } = "";
    public int Index { get; set; }
    public string Direction { get; set; } = "";

    public static InputBinding Button(int index) => new()
    {
        Kind = "Button",
        Index = index
    };

    public static InputBinding Axis(string axis, string direction) => new()
    {
        Kind = "Axis",
        Direction = $"{axis}{direction}"
    };

    public static InputBinding Pov(string direction) => new()
    {
        Kind = "Pov",
        Index = 0,
        Direction = direction
    };

    public override string ToString()
    {
        return Kind switch
        {
            "Button" => $"Button {Index}",
            "Axis" => $"Axis {Direction}",
            "Pov" => $"D-pad {Direction}",
            _ => "Unmapped"
        };
    }
}
