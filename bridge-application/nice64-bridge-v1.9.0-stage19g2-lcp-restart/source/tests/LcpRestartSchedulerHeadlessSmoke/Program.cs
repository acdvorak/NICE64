using PicoSwitchController;

var tests = new (string Name, Action Run)[]
{
    ("01 Start emits one request and arms deadline", StartEmitsAndArms),
    ("02 no retry before deadline; one retry at deadline", DeadlineIsDeterministic),
    ("03 repeated deadlines honor existing retry limit", RetryLimitIsBounded),
    ("04 retry exhaustion enters deterministic failure", RetryExhaustionFails),
    ("05 valid Ack cancels retry", AckCancels),
    ("06 Nak creates request and resets deadline", NakResets),
    ("07 Reject creates request and resets deadline", RejectResets),
    ("08 Opened cancels retry", OpenedCancels),
    ("09 close before deadline cancels callback", CloseBeforeDeadline),
    ("10 close during retries cancels callback", CloseDuringRetries),
    ("11 second session has independent fresh timer", SecondSessionFresh),
    ("12 CLIENT phase starts no LCP timer", ClientPhaseHasNoTimer),
    ("13 partial CLIENTSERVER drain starts no LCP", PartialResponseHasNoTimer),
    ("14 PPP starts after handshake then retries", HandshakeOrderingThenRetry),
    ("15 CE PFC/ACFC reject then ACCM Ack preserved", CeOptionPolicyPreserved),
};

int passed = 0;
foreach ((string name, Action run) in tests)
{
    try
    {
        run();
        passed++;
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        Console.WriteLine($"FAIL {name}: {exception.Message}");
    }
}

Console.WriteLine($"RESULT {passed}/{tests.Length} PASS");
return passed == tests.Length ? 0 : 1;

static void StartEmitsAndArms()
{
    var clock = new FakeRestartScheduler();
    using PppSession session = NewSession(clock);
    LcpPacket request = ReadOneLcp(session);
    Equal(LcpConstants.ConfigureRequest, request.Code);
    Equal(1, session.LcpConfigureAttempts);
    True(clock.IsArmed, "restart deadline not armed");
    Equal(PppSession.DefaultLcpRestartInterval, clock.Remaining);
    Empty(session);
}

static void DeadlineIsDeterministic()
{
    var clock = new FakeRestartScheduler();
    using PppSession session = NewSession(clock);
    LcpPacket initial = ReadOneLcp(session);
    clock.Advance(TimeSpan.FromMilliseconds(2999));
    Empty(session);
    clock.Advance(TimeSpan.FromMilliseconds(1));
    LcpPacket retry = ReadOneLcp(session);
    Equal(initial.Identifier, retry.Identifier);
    Bytes(initial.Data.Span, retry.Data.Span);
    Equal(2, session.LcpConfigureAttempts);
}

static void RetryLimitIsBounded()
{
    var clock = new FakeRestartScheduler();
    using PppSession session = NewSession(clock);
    ReadOneLcp(session);
    for (int attempt = 2; attempt <= LcpPeer.DefaultMaxConfigureAttempts; attempt++)
    {
        clock.Advance(PppSession.DefaultLcpRestartInterval);
        ReadOneLcp(session);
        Equal(attempt, session.LcpConfigureAttempts);
    }
    Equal(LcpPeer.DefaultMaxConfigureAttempts, session.LcpConfigureAttempts);
    True(clock.IsArmed, "final exhaustion deadline not armed");
}

static void RetryExhaustionFails()
{
    var clock = new FakeRestartScheduler();
    using PppSession session = NewSession(clock);
    ReadOneLcp(session);
    for (int attempt = 2; attempt <= LcpPeer.DefaultMaxConfigureAttempts; attempt++)
    {
        clock.Advance(PppSession.DefaultLcpRestartInterval);
        ReadOneLcp(session);
    }
    clock.Advance(PppSession.DefaultLcpRestartInterval);
    Equal(PppSessionState.Failed, session.State);
    Equal("CONFIGURE_RETRY_EXHAUSTED", session.FailureReason);
    True(!clock.IsArmed, "timer remained armed after exhaustion");
}

static void AckCancels()
{
    var clock = new FakeRestartScheduler();
    using PppSession session = NewSession(clock);
    LcpPacket request = ReadOneLcp(session);
    Feed(session, new LcpPacket(LcpConstants.ConfigureAck, request.Identifier, request.Data));
    Equal(LcpState.AckRcvd, session.LcpState);
    True(!clock.IsArmed, "Ack did not cancel timer");
    clock.Advance(TimeSpan.FromSeconds(30));
    Empty(session);
}

static void NakResets()
{
    var clock = new FakeRestartScheduler();
    using PppSession session = NewSession(clock);
    LcpPacket first = ReadOneLcp(session);
    clock.Advance(TimeSpan.FromSeconds(2));
    Feed(session, new LcpPacket(LcpConstants.ConfigureNak, first.Identifier,
        LcpOptionCodec.Mru(1400)));
    LcpPacket replacement = ReadOneLcp(session);
    True(replacement.Identifier != first.Identifier, "Nak did not allocate a new identifier");
    Equal(PppSession.DefaultLcpRestartInterval, clock.Remaining);
    clock.Advance(TimeSpan.FromSeconds(3));
    Equal(replacement.Identifier, ReadOneLcp(session).Identifier);
}

static void RejectResets()
{
    var clock = new FakeRestartScheduler();
    using PppSession session = NewSession(clock);
    LcpPacket first = ReadOneLcp(session);
    byte[] magicOption = first.Data.Span[^6..].ToArray();
    clock.Advance(TimeSpan.FromSeconds(2));
    Feed(session, new LcpPacket(LcpConstants.ConfigureReject, first.Identifier, magicOption));
    LcpPacket replacement = ReadOneLcp(session);
    True(replacement.Identifier != first.Identifier, "Reject did not allocate a new identifier");
    Equal(PppSession.DefaultLcpRestartInterval, clock.Remaining);
    clock.Advance(TimeSpan.FromSeconds(3));
    Equal(replacement.Identifier, ReadOneLcp(session).Identifier);
}

static void OpenedCancels()
{
    var clock = new FakeRestartScheduler();
    using PppSession session = NewSession(clock);
    LcpPacket ours = ReadOneLcp(session);
    Feed(session, new LcpPacket(LcpConstants.ConfigureRequest, 0x41,
        LcpOptionCodec.Accm(0)));
    Equal(LcpConstants.ConfigureAck, ReadOneLcp(session).Code);
    True(clock.IsArmed, "peer request incorrectly cancelled our timer");
    Feed(session, new LcpPacket(LcpConstants.ConfigureAck, ours.Identifier, ours.Data));
    Equal(LcpState.Opened, session.LcpState);
    True(!clock.IsArmed, "Opened did not cancel LCP timer");
}

static void CloseBeforeDeadline()
{
    var clock = new FakeRestartScheduler();
    PppSession session = NewSession(clock);
    ReadOneLcp(session);
    session.Close("TEST_CLOSE");
    True(!clock.IsArmed && clock.IsDisposed, "close did not dispose timer");
    clock.Advance(TimeSpan.FromSeconds(30));
    Equal(PppSessionState.Closed, session.State);
}

static void CloseDuringRetries()
{
    var clock = new FakeRestartScheduler();
    PppSession session = NewSession(clock);
    ReadOneLcp(session);
    clock.Advance(TimeSpan.FromSeconds(3));
    ReadOneLcp(session);
    session.Close("TEST_CLOSE_AFTER_RETRY");
    clock.Advance(TimeSpan.FromSeconds(30));
    Equal(2, session.LcpConfigureAttempts);
}

static void SecondSessionFresh()
{
    var firstClock = new FakeRestartScheduler();
    var secondClock = new FakeRestartScheduler();
    using PppSession first = NewSession(firstClock, Guid.Parse("11111111-1111-1111-1111-111111111111"));
    using PppSession second = NewSession(secondClock, Guid.Parse("22222222-2222-2222-2222-222222222222"));
    ReadOneLcp(first);
    ReadOneLcp(second);
    firstClock.Advance(TimeSpan.FromSeconds(3));
    Equal(2, first.LcpConfigureAttempts);
    Equal(1, second.LcpConfigureAttempts);
    True(secondClock.IsArmed, "second session timer not independent");
}

static void ClientPhaseHasNoTimer()
{
    var clock = new FakeRestartScheduler();
    using PppLiveResource resource = NewResource(clock);
    True(resource.Session == null, "session started before CLIENT");
    True(!clock.IsArmed, "timer armed before CLIENT");
    Write(resource, "CLIENT"u8);
    True(resource.Session == null && !clock.IsArmed,
        "timer armed while CLIENTSERVER was pending");
}

static void PartialResponseHasNoTimer()
{
    var clock = new FakeRestartScheduler();
    using PppLiveResource resource = NewResource(clock);
    Write(resource, "CLIENT"u8);
    Equal(5, Data(resource.Read(5)).Length);
    True(resource.Session == null && !clock.IsArmed,
        "PPP started before CLIENTSERVER was fully drained");
}

static void HandshakeOrderingThenRetry()
{
    var clock = new FakeRestartScheduler();
    using PppLiveResource resource = NewResource(clock);
    Write(resource, "CLIENT"u8);
    Bytes("CLIENTSERVER"u8, Data(resource.Read(16)));
    True(resource.Session != null && clock.IsArmed, "PPP timer not armed after handshake");
    LcpPacket initial = DecodeOne(Data(resource.Read(256)));
    clock.Advance(TimeSpan.FromSeconds(3));
    LcpPacket retry = DecodeOne(Data(resource.Read(256)));
    Equal(initial.Identifier, retry.Identifier);
}

static void CeOptionPolicyPreserved()
{
    var clock = new FakeRestartScheduler();
    using PppSession session = NewSession(clock);
    ReadOneLcp(session);
    byte[] firstOptions = LcpOptionCodec.Accm(0)
        .Concat(new byte[] { LcpConstants.OptionProtocolFieldCompression, 2,
                             LcpConstants.OptionAddressControlFieldCompression, 2 })
        .ToArray();
    Feed(session, new LcpPacket(LcpConstants.ConfigureRequest, 1, firstOptions));
    LcpPacket reject = ReadOneLcp(session);
    Equal(LcpConstants.ConfigureReject, reject.Code);
    Bytes(new byte[] { LcpConstants.OptionProtocolFieldCompression, 2,
                       LcpConstants.OptionAddressControlFieldCompression, 2 }, reject.Data.Span);

    byte[] revised = LcpOptionCodec.Accm(0);
    Feed(session, new LcpPacket(LcpConstants.ConfigureRequest, 2, revised));
    LcpPacket ack = ReadOneLcp(session);
    Equal(LcpConstants.ConfigureAck, ack.Code);
    Equal((byte)2, ack.Identifier);
    Bytes(revised, ack.Data.Span);
    True(clock.IsArmed, "peer negotiation incorrectly stopped our outstanding timer");
}

static PppSession NewSession(FakeRestartScheduler clock, Guid? id = null) => new(
    id ?? Guid.Parse("11111111-1111-1111-1111-111111111111"),
    Addresses(),
    0x11223344,
    lcpRestartSchedulerFactory: callback => clock.Attach(callback));

static PppLiveResource NewResource(FakeRestartScheduler clock)
{
    Guid id = Guid.Parse("11111111-1111-1111-1111-111111111111");
    return new PppLiveResource(id, Addresses(), 0x11223344, Context(id),
        callback => clock.Attach(callback));
}

static PppAddressAssignment Addresses() => new(
    Ipv4Address.FromOctets(10, 64, 0, 1),
    Ipv4Address.FromOctets(10, 64, 0, 2));

static ModuleContext Context(Guid connection) => new(
    AppContext.BaseDirectory, _ => { }, "test", 23, 64 * 1024, "test")
{ ConnectionId = connection };

static void Write(PppLiveResource resource, ReadOnlySpan<byte> bytes)
{
    Guid id = Guid.Parse("11111111-1111-1111-1111-111111111111");
    NetworkPakWriteResult result = resource.Write(
        new NetworkPakWriteChunk(3, bytes.ToArray()), Context(id));
    True(result.Success, result.Error ?? "write failed");
}

static void Feed(PppSession session, LcpPacket packet)
{
    byte[] wire = new PppFrameEncoder().Encode(packet.ToPppFrame());
    True(session.Feed(wire), session.FailureReason ?? "feed failed");
}

static LcpPacket ReadOneLcp(PppSession session)
{
    NetworkPakLiveReadResult result = session.Read(4096);
    Equal(NetworkPakLiveReadStatus.Data, result.Status);
    return DecodeOne(result.Data);
}

static LcpPacket DecodeOne(ReadOnlySpan<byte> wire)
{
    var frames = new List<PppFrame>();
    var decoder = new PppFrameDecoder();
    decoder.Feed(wire, frames.Add);
    Equal(1, frames.Count);
    True(LcpPacket.TryParse(frames[0], out LcpPacket? packet, out string? error),
        error ?? "LCP decode failed");
    return packet!;
}

static void Empty(PppSession session) =>
    Equal(NetworkPakLiveReadStatus.WouldBlock, session.Read(4096).Status);

static byte[] Data(NetworkPakLiveReadResult result)
{
    Equal(NetworkPakLiveReadStatus.Data, result.Status);
    return result.Data;
}

static void Bytes(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
{
    if (!expected.SequenceEqual(actual))
        throw new InvalidOperationException(
            $"expected {Convert.ToHexString(expected)}, actual {Convert.ToHexString(actual)}");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"expected {expected}, actual {actual}");
}

static void True(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

internal sealed class FakeRestartScheduler : ILcpRestartScheduler
{
    private Action? _callback;
    public bool IsArmed { get; private set; }
    public bool IsDisposed { get; private set; }
    public TimeSpan Remaining { get; private set; }

    public FakeRestartScheduler Attach(Action callback)
    {
        if (_callback != null) throw new InvalidOperationException("scheduler already attached");
        _callback = callback;
        return this;
    }

    public void Arm(TimeSpan delay)
    {
        if (IsDisposed) return;
        IsArmed = true;
        Remaining = delay;
    }

    public void Cancel()
    {
        IsArmed = false;
        Remaining = TimeSpan.Zero;
    }

    public void Advance(TimeSpan elapsed)
    {
        if (!IsArmed || IsDisposed) return;
        Remaining -= elapsed;
        if (Remaining > TimeSpan.Zero) return;
        IsArmed = false;
        Remaining = TimeSpan.Zero;
        _callback!();
    }

    public void Dispose()
    {
        Cancel();
        IsDisposed = true;
    }
}
