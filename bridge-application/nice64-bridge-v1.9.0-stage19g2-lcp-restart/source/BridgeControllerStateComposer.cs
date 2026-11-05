namespace PicoSwitchController;

internal static class BridgeControllerStateComposer
{
    internal const uint N64PhysicalPseudoStickMask =
        (1u << 14) | (1u << 15) | (1u << 16) | (1u << 17);

    public static BridgeControllerState Merge(
        ConsoleControllerType controllerType,
        uint manualButtons,
        uint physicalButtons,
        bool analogActive,
        sbyte analogX,
        sbyte analogY)
    {
        uint ownedPhysicalButtons = controllerType == ConsoleControllerType.N64 && analogActive
            ? physicalButtons & ~N64PhysicalPseudoStickMask
            : physicalButtons;

        return new(controllerType,
            manualButtons | ownedPhysicalButtons,
            analogX,
            analogY,
            controllerType == ConsoleControllerType.N64 && analogActive);
    }
}
