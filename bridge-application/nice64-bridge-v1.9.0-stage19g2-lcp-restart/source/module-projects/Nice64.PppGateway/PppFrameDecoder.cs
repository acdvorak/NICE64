namespace PicoSwitchController;

/// <summary>
/// Synchronous, bounded streaming decoder for canonical asynchronous PPP
/// frames. One instance belongs to one future PPP session.
/// </summary>
public sealed class PppFrameDecoder
{
    public const int DefaultMaximumDeescapedFrameBytes = 2048;
    private const int MinimumCanonicalFrameBytes = 6;

    private readonly byte[] _frame;
    private int _length;
    private bool _collecting;
    private bool _escapePending;
    private bool _dropUntilFlag;

    public PppFrameDecoder(int maximumDeescapedFrameBytes =
        DefaultMaximumDeescapedFrameBytes)
    {
        if (maximumDeescapedFrameBytes < MinimumCanonicalFrameBytes)
            throw new ArgumentOutOfRangeException(
                nameof(maximumDeescapedFrameBytes));
        _frame = new byte[maximumDeescapedFrameBytes];
    }

    public long FramesAccepted { get; private set; }
    public long FramesRejectedFcs { get; private set; }
    public long FramesRejectedMalformed { get; private set; }
    public long FramesRejectedOversize { get; private set; }

    public int MaximumDeescapedFrameBytes => _frame.Length;

    public void Feed(ReadOnlySpan<byte> data, Action<PppFrame> onFrame)
    {
        ArgumentNullException.ThrowIfNull(onFrame);
        foreach (byte wireByte in data)
        {
            if (wireByte == PppFrameEncoder.Flag)
            {
                FinishAtFlag(onFrame);
                continue;
            }

            if (!_collecting || _dropUntilFlag) continue;

            if (_escapePending)
            {
                _escapePending = false;
                Append((byte)(wireByte ^ PppFrameEncoder.EscapeXor));
            }
            else if (wireByte == PppFrameEncoder.ControlEscape)
            {
                _escapePending = true;
            }
            else
            {
                Append(wireByte);
            }
        }
    }

    /// <summary>
    /// Ends the current input lifetime. A partial frame or dangling escape is
    /// malformed. The next Feed call must observe a new opening flag.
    /// </summary>
    public void Reset()
    {
        if (!_dropUntilFlag && _collecting &&
            (_length != 0 || _escapePending))
        {
            FramesRejectedMalformed++;
        }
        ClearState(collecting: false);
    }

    private void Append(byte value)
    {
        if (_length == _frame.Length)
        {
            FramesRejectedOversize++;
            _dropUntilFlag = true;
            _escapePending = false;
            _length = 0;
            return;
        }
        _frame[_length++] = value;
    }

    private void FinishAtFlag(Action<PppFrame> onFrame)
    {
        if (!_collecting)
        {
            ClearState(collecting: true);
            return;
        }

        if (_dropUntilFlag)
        {
            ClearState(collecting: true);
            return;
        }

        if (_escapePending)
        {
            FramesRejectedMalformed++;
            ClearState(collecting: true);
            return;
        }

        if (_length == 0)
        {
            ClearState(collecting: true);
            return;
        }

        DecodeCandidate(onFrame);
        ClearState(collecting: true);
    }

    private void DecodeCandidate(Action<PppFrame> onFrame)
    {
        ReadOnlySpan<byte> candidate = _frame.AsSpan(0, _length);
        if (candidate.Length < MinimumCanonicalFrameBytes)
        {
            FramesRejectedMalformed++;
            return;
        }
        if (!PppFcs16.HasGoodResidue(candidate))
        {
            FramesRejectedFcs++;
            return;
        }
        if (candidate[0] != PppFrameEncoder.Address ||
            candidate[1] != PppFrameEncoder.Control)
        {
            FramesRejectedMalformed++;
            return;
        }

        byte protocolHigh = candidate[2];
        byte protocolLow = candidate[3];
        if ((protocolHigh & 1) != 0 || (protocolLow & 1) == 0)
        {
            FramesRejectedMalformed++;
            return;
        }

        ushort protocol = (ushort)((protocolHigh << 8) | protocolLow);
        byte[] information = candidate[4..^2].ToArray();
        FramesAccepted++;
        onFrame(new PppFrame(protocol, information));
    }

    private void ClearState(bool collecting)
    {
        _length = 0;
        _collecting = collecting;
        _escapePending = false;
        _dropUntilFlag = false;
    }
}
