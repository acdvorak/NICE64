namespace PicoSwitchController;

using System.Runtime.InteropServices;

/// <summary>
/// Passive, bounded RFC 1662 decoder used only for Bridge diagnostics. It
/// observes Serial Tunnel bytes and never produces bytes or changes transport.
/// </summary>
public sealed class PppDiagnosticDecoder
{
    private const int MaximumFrameBytes = 4096;
    private const int MaximumLoggedFrames = 128;
    private readonly List<byte> _frame = [];
    private bool _inFrame;
    private bool _escaped;
    private bool _firstByteLogged;
    private bool _firstFlagLogged;
    private bool _overflow;
    private int _loggedFrames;
    private int _lcpConfigureRequests;

    public void Feed(ReadOnlySpan<byte> bytes, Action<string> log)
    {
        if (!_firstByteLogged && !bytes.IsEmpty)
        {
            log($"PPP first raw byte observed (0x{bytes[0]:X2})");
            _firstByteLogged = true;
        }
        foreach (byte value in bytes)
        {
            if (value == 0x7E)
            {
                if (!_firstFlagLogged)
                {
                    log("PPP first flag byte observed (0x7E)");
                    _firstFlagLogged = true;
                }
                if (_inFrame && (_frame.Count != 0 || _overflow))
                    FinishFrame(log);
                _frame.Clear();
                _inFrame = true;
                _escaped = false;
                _overflow = false;
                continue;
            }
            if (!_inFrame || _overflow) continue;
            if (_escaped)
            {
                Append((byte)(value ^ 0x20));
                _escaped = false;
            }
            else if (value == 0x7D)
                _escaped = true;
            else
                Append(value);
        }
    }

    private void Append(byte value)
    {
        if (_frame.Count < MaximumFrameBytes) _frame.Add(value);
        else _overflow = true;
    }

    private void FinishFrame(Action<string> log)
    {
        if (_loggedFrames >= MaximumLoggedFrames) return;
        ++_loggedFrames;
        if (_overflow)
        {
            log($"PPP frame dropped: exceeds {MaximumFrameBytes} decoded bytes");
            return;
        }
        if (_escaped)
        {
            log("PPP frame invalid: dangling escape before flag");
            return;
        }
        if (_frame.Count < 3)
        {
            log($"PPP frame too short: decodedBytes={_frame.Count}");
            return;
        }

        ReadOnlySpan<byte> frame = CollectionsMarshal.AsSpan(_frame);
        bool fcsValid = ComputeFcs(frame) == 0xF0B8;
        int contentEnd = Math.Max(0, frame.Length - 2);
        int offset = 0;
        if (contentEnd >= 2 && frame[0] == 0xFF && frame[1] == 0x03) offset = 2;
        if (offset >= contentEnd)
        {
            log($"PPP frame missing protocol: bytes={frame.Length} fcs={(fcsValid ? "valid" : "INVALID")}");
            return;
        }

        ushort protocol;
        if ((frame[offset] & 1) != 0)
            protocol = frame[offset++];
        else if (offset + 1 < contentEnd)
        {
            protocol = (ushort)((frame[offset] << 8) | frame[offset + 1]);
            offset += 2;
        }
        else
        {
            log($"PPP frame truncated protocol: bytes={frame.Length}");
            return;
        }

        string protocolName = protocol switch
        {
            0xC021 => "LCP",
            0xC023 => "PAP",
            0xC223 => "CHAP",
            0x8021 => "IPCP",
            0x0021 => "IPv4",
            _ => "Unknown"
        };
        string raw = Convert.ToHexString(frame[..Math.Min(frame.Length, 128)]);
        if (frame.Length > 128) raw += "...";
        log($"PPP frame protocol={protocolName} (0x{protocol:X4}) " +
            $"payloadBytes={Math.Max(0, contentEnd - offset)} fcs={(fcsValid ? "valid" : "INVALID")} raw={raw}");

        if (offset + 4 > contentEnd || protocol is not (0xC021 or 0xC023 or 0xC223 or 0x8021))
            return;
        byte code = frame[offset];
        byte identifier = frame[offset + 1];
        int declaredLength = (frame[offset + 2] << 8) | frame[offset + 3];
        string codeName = CodeName(protocol, code);
        log($"PPP {protocolName} code={codeName} ({code}) identifier=0x{identifier:X2} length={declaredLength}");
        if (protocol == 0xC021 && code == 1)
        {
            ++_lcpConfigureRequests;
            if (_lcpConfigureRequests > 1)
                log($"PPP LCP retry observed count={_lcpConfigureRequests} identifier=0x{identifier:X2}");
            if (declaredLength >= 4 && offset + declaredLength <= contentEnd)
                LogLcpOptions(frame.Slice(offset + 4, declaredLength - 4), log);
        }
    }

    private static string CodeName(ushort protocol, byte code)
    {
        if (protocol == 0xC021 || protocol == 0x8021)
            return code switch { 1 => "Configure-Request", 2 => "Configure-Ack", 3 => "Configure-Nak", 4 => "Configure-Reject", 5 => "Terminate-Request", 6 => "Terminate-Ack", 9 => "Echo-Request", 10 => "Echo-Reply", _ => "Unknown" };
        if (protocol == 0xC023)
            return code switch { 1 => "Authenticate-Request", 2 => "Authenticate-Ack", 3 => "Authenticate-Nak", _ => "Unknown" };
        if (protocol == 0xC223)
            return code switch { 1 => "Challenge", 2 => "Response", 3 => "Success", 4 => "Failure", _ => "Unknown" };
        return "Unknown";
    }

    private static void LogLcpOptions(ReadOnlySpan<byte> options, Action<string> log)
    {
        int offset = 0;
        while (offset + 2 <= options.Length)
        {
            byte type = options[offset];
            int length = options[offset + 1];
            if (length < 2 || offset + length > options.Length)
            {
                log($"PPP LCP option invalid type={type} length={length}");
                return;
            }
            ReadOnlySpan<byte> value = options.Slice(offset + 2, length - 2);
            string detail = type switch
            {
                1 when value.Length == 2 => $"MRU={(value[0] << 8) | value[1]}",
                2 when value.Length == 4 => $"ACCM=0x{ReadU32(value):X8}",
                3 when value.Length >= 2 => $"Authentication=0x{((value[0] << 8) | value[1]):X4}",
                5 when value.Length == 4 => $"Magic=0x{ReadU32(value):X8}",
                7 when value.Length == 0 => "Protocol-Field-Compression",
                8 when value.Length == 0 => "Address-Control-Field-Compression",
                _ => $"type={type} value={Convert.ToHexString(value)}"
            };
            log($"PPP LCP option {detail}");
            offset += length;
        }
        if (offset != options.Length) log("PPP LCP options contain trailing byte");
    }

    private static uint ReadU32(ReadOnlySpan<byte> value) =>
        ((uint)value[0] << 24) | ((uint)value[1] << 16) |
        ((uint)value[2] << 8) | value[3];

    private static ushort ComputeFcs(ReadOnlySpan<byte> bytes)
    {
        ushort fcs = 0xFFFF;
        foreach (byte value in bytes)
        {
            fcs ^= value;
            for (int bit = 0; bit < 8; ++bit)
                fcs = (ushort)((fcs >> 1) ^ ((fcs & 1) != 0 ? 0x8408 : 0));
        }
        return fcs;
    }
}
