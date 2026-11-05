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
    {
        cancellationToken.ThrowIfCancellationRequested();
        BridgeSessionSnapshot? session = _sessions.Current;
        BridgeControllerEndpointSnapshot? unavailable =
            ValidateEligibility(session, _device.Current, _protection.Current);
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
                    current, _device.Current, _protection.Current);
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
        BridgeControllerEndpointProtection protection)
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
        if (session.FirmwarePersonality != FirmwarePersonality.Controller ||
            device.FirmwarePersonality != FirmwarePersonality.Controller)
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
