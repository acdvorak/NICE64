namespace PicoSwitchController;

using System.Net;
using System.Net.Sockets;

public sealed class BridgeControllerEndpointProvider :
    IBridgeControllerEndpointProvider
{
    private readonly IBridgeSessionProvider _sessions;
    private readonly IBridgeDeviceStateProvider _device;
    private readonly IBridgeControllerTcpEndpointAuthority _tcpAuthority;
    private readonly IBridgeControllerUsbWifiStatusAuthority _usbAuthority;
    private readonly IBridgeControllerEndpointProtectionProvider _protection;

    internal BridgeControllerEndpointProvider(
        IBridgeSessionProvider sessions,
        IBridgeDeviceStateProvider device,
        IBridgeControllerTcpEndpointAuthority tcpAuthority,
        IBridgeControllerUsbWifiStatusAuthority usbAuthority,
        IBridgeControllerEndpointProtectionProvider protection)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _tcpAuthority = tcpAuthority ??
            throw new ArgumentNullException(nameof(tcpAuthority));
        _usbAuthority = usbAuthority ??
            throw new ArgumentNullException(nameof(usbAuthority));
        _protection = protection ??
            throw new ArgumentNullException(nameof(protection));
    }

    public async Task<BridgeControllerEndpointSnapshot> ResolveCurrentAsync(
        CancellationToken cancellationToken = default)
        => await ResolveAsync(requireController: true, cancellationToken)
            .ConfigureAwait(false);

    public async Task<BridgeControllerEndpointSnapshot> ResolveMobileHumanInputAsync(
        CancellationToken cancellationToken = default)
        => await ResolveAsync(requireController: false, cancellationToken)
            .ConfigureAwait(false);

    private async Task<BridgeControllerEndpointSnapshot> ResolveAsync(
        bool requireController,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BridgeSessionSnapshot? session = _sessions.Current;
        BridgeControllerEndpointSnapshot? unavailable =
            ValidateEligibility(session, _device.Current, _protection.Current,
                requireController);
        if (unavailable is not null) return unavailable;

        string? address;
        switch (session!.ConnectionKind)
        {
            case BridgeControlConnectionKind.WifiTcp:
                address = NormalizeIpv4(_tcpAuthority.GetVerifiedIpv4(session));
                break;

            case BridgeControlConnectionKind.SerialUsb:
                BridgeControllerUsbWifiStatusResult usb;
                try
                {
                    usb = await _usbAuthority.ReadFreshAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    usb = BridgeControllerUsbWifiStatusResult.Unavailable;
                }

                BridgeSessionSnapshot? current = _sessions.Current;
                unavailable = ValidateEligibility(
                    current, _device.Current, _protection.Current,
                    requireController);
                if (unavailable is not null) return unavailable;
                if (!string.Equals(current!.SessionId, session.SessionId,
                        StringComparison.Ordinal) ||
                    current.ConnectionKind != BridgeControlConnectionKind.SerialUsb)
                {
                    return BridgeControllerEndpointSnapshot.Unavailable(
                        current.SessionId,
                        BridgeControllerEndpointUnavailableReason.PicoWifiEndpointUnverified);
                }
                if (usb.Outcome ==
                    BridgeControllerUsbWifiStatusOutcome.ProtectedOperation)
                {
                    return BridgeControllerEndpointSnapshot.Unavailable(
                        current.SessionId,
                        BridgeControllerEndpointUnavailableReason.ProtectedOperation);
                }
                address = usb.Outcome == BridgeControllerUsbWifiStatusOutcome.Confirmed &&
                    usb.Connected
                    ? NormalizeIpv4(usb.IpAddress)
                    : null;
                break;

            default:
                address = null;
                break;
        }

        return address is null
            ? BridgeControllerEndpointSnapshot.Unavailable(
                session.SessionId,
                BridgeControllerEndpointUnavailableReason.PicoWifiEndpointUnverified)
            : BridgeControllerEndpointSnapshot.AvailableAt(session.SessionId, address);
    }

    private static BridgeControllerEndpointSnapshot? ValidateEligibility(
        BridgeSessionSnapshot? session,
        BridgeDeviceStateSnapshot device,
        BridgeControllerEndpointProtection protection,
        bool requireController)
    {
        if (session is null)
        {
            return BridgeControllerEndpointSnapshot.Unavailable(null,
                BridgeControllerEndpointUnavailableReason.NoSession);
        }
        if (session.State != BridgeSessionState.Connected ||
            device.ControlConnectionState != BridgeConnectionState.Connected ||
            session.ConnectionKind == BridgeControlConnectionKind.None ||
            device.ControlConnectionKind != session.ConnectionKind)
        {
            return BridgeControllerEndpointSnapshot.Unavailable(session.SessionId,
                BridgeControllerEndpointUnavailableReason.SessionNotConnected);
        }
        bool controller = session.FirmwarePersonality == FirmwarePersonality.Controller &&
                          device.FirmwarePersonality == FirmwarePersonality.Controller;
        bool keyboardMouse = session.FirmwarePersonality == device.FirmwarePersonality &&
            device.FirmwarePersonality is FirmwarePersonality.Keyboard or
                FirmwarePersonality.Mouse &&
            device.DeviceRole == BridgeDeviceRole.Standalone &&
            device.DeviceService == BridgeDeviceService.KeyboardMouse &&
            device.FirmwareTransition.Phase == BridgeFirmwareTransitionPhase.Completed &&
            device.FirmwareTransition.Target == device.FirmwarePersonality &&
            string.IsNullOrEmpty(device.FirmwareTransition.Error);
        if ((requireController && !controller) || (!requireController && !keyboardMouse))
        {
            return BridgeControllerEndpointSnapshot.Unavailable(session.SessionId,
                BridgeControllerEndpointUnavailableReason.ControllerFirmwareUnavailable);
        }
        if (protection == BridgeControllerEndpointProtection.All)
        {
            return BridgeControllerEndpointSnapshot.Unavailable(session.SessionId,
                BridgeControllerEndpointUnavailableReason.ProtectedOperation);
        }
        return null;
    }

    private static string? NormalizeIpv4(string? candidate)
    {
        if (!IPAddress.TryParse(candidate, out IPAddress? address) ||
            address.AddressFamily != AddressFamily.InterNetwork)
            return null;
        return address.ToString();
    }
}
