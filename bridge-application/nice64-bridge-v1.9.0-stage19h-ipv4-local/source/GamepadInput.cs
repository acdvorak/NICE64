namespace PicoSwitchController;

using SharpDX.DirectInput;

public static class GamepadInput
{
    public const int AxisCenter = 32767;
    public const int AxisThreshold = 16000;

    public static bool IsBindingActive(InputBinding binding, JoystickState state, int axisThreshold = AxisThreshold)
    {
        return binding.Kind switch
        {
            "Button" => binding.Index >= 0
                && binding.Index < state.Buttons.Length
                && state.Buttons[binding.Index],
            "Pov" => IsPovActive(binding.Direction, state.PointOfViewControllers.FirstOrDefault(-1)),
            "Axis" => IsAxisActive(binding.Direction, state, axisThreshold),
            _ => false
        };
    }

    public static IEnumerable<(string Name, int Value)> GetAxes(JoystickState state)
    {
        yield return ("X", state.X);
        yield return ("Y", state.Y);
        yield return ("Z", state.Z);
        yield return ("Rx", state.RotationX);
        yield return ("Ry", state.RotationY);
        yield return ("Rz", state.RotationZ);

        int[] sliders = state.Sliders;
        for (int i = 0; i < sliders.Length; i++)
        {
            yield return ($"Slider{i}", sliders[i]);
        }
    }

    private static bool IsPovActive(string direction, int value)
    {
        if (value < 0)
        {
            return false;
        }

        string active = value switch
        {
            >= 31500 or <= 4500 => "Up",
            > 4500 and <= 13500 => "Right",
            > 13500 and <= 22500 => "Down",
            _ => "Left"
        };

        return active == direction;
    }

    private static bool IsAxisActive(string direction, JoystickState state, int axisThreshold)
    {
        string axisName = direction[..^1];
        char sign = direction[^1];
        (string Name, int Value) axis = GetAxes(state).FirstOrDefault(axis => axis.Name == axisName);
        if (axis.Name is null)
        {
            return false;
        }

        int value = axis.Value;
        int delta = value - AxisCenter;
        return sign == '-'
            ? delta <= -axisThreshold
            : delta >= axisThreshold;
    }
}
