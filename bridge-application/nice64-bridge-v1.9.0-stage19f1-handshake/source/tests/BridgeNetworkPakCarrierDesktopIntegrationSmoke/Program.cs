using PicoSwitchController;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void RequireSubsequence(IReadOnlyList<string> actual, params string[] expected)
{
    int next = 0;
    foreach (string item in actual)
    {
        if (next < expected.Length && item == expected[next]) next++;
    }
    Require(next == expected.Length,
        $"Missing integration sequence. Expected {string.Join(", ", expected)}; " +
        $"actual {string.Join(", ", actual)}");
}

string tempDirectory = Path.Combine(Path.GetTempPath(),
    $"nice64-f13c-{Guid.NewGuid():N}");
string settingsPath = Path.Combine(tempDirectory, "picoswitchcontroller.ini");
Directory.CreateDirectory(tempDirectory);
try
{
    BridgeNetworkPakCarrierSelectionStoreAdapter store = new(settingsPath);
    BridgeNetworkPakCarrierSelectionLoadResult missing = await store.LoadAsync();
    Require(missing.Success && missing.UsedDefault &&
            missing.Selection == BridgeNetworkPakTransport.Tcp,
        "Missing legacy settings did not retain the TCP default.");

    foreach ((BridgeNetworkPakTransport bridge, NetworkPakWifiTransport legacy) in
             new[]
             {
                 (BridgeNetworkPakTransport.UsbSerial,
                     NetworkPakWifiTransport.UsbSerial),
                 (BridgeNetworkPakTransport.Tcp, NetworkPakWifiTransport.Tcp),
                 (BridgeNetworkPakTransport.ReliableUdpQuick,
                     NetworkPakWifiTransport.ReliableUdpQuick)
             })
    {
        BridgeNetworkPakCarrierSelectionSaveResult saved =
            await store.SaveAsync(bridge);
        BridgeNetworkPakCarrierSelectionLoadResult loaded = await store.LoadAsync();
        Require(saved.Success && loaded.Success && loaded.Selection == bridge &&
                NetworkPakTransportSettings.Load(settingsPath) == legacy,
            $"Legacy carrier persistence did not round-trip {bridge}.");
    }

    File.WriteAllText(settingsPath,
        "[NetworkPak]\nNetworkPakTransport=not-a-carrier\n");
    BridgeNetworkPakCarrierSelectionLoadResult malformed = await store.LoadAsync();
    Require(malformed.Success && malformed.Selection == BridgeNetworkPakTransport.Tcp,
        "Malformed legacy selection did not preserve its accepted TCP default.");

    using CancellationTokenSource cancelled = new();
    cancelled.Cancel();
    BridgeNetworkPakCarrierSelectionSaveResult cancelledSave =
        await store.SaveAsync(BridgeNetworkPakTransport.Tcp, cancelled.Token);
    BridgeNetworkPakCarrierSelectionSaveResult invalidSave =
        await store.SaveAsync(BridgeNetworkPakTransport.None);
    Require(!cancelledSave.Success && cancelledSave.Failure ==
            BridgeNetworkPakCarrierFailure.Cancelled &&
            !invalidSave.Success && invalidSave.Failure ==
            BridgeNetworkPakCarrierFailure.InvalidCarrier,
        "The concrete store leaked cancellation or invalid selection into filesystem behavior.");
    Console.WriteLine("PASS legacy INI adapter preserves default, names, format, and typed failures");

    await store.SaveAsync(BridgeNetworkPakTransport.Tcp);
    List<string> calls = [];
    FakeStatePort state = new(calls);
    bool activationRequired = true;
    bool lineAvailable = true;
    bool quickAvailable = true;
    bool applicationConnected = false;
    bool handshakeComplete = false;
    List<TimeSpan> delays = [];

    DelegateBridgeNetworkPakLineCarrierPort line = new(
        carrier => lineAvailable && carrier is
            BridgeNetworkPakTransport.UsbSerial or BridgeNetworkPakTransport.Tcp,
        (carrier, generation, token) =>
        {
            token.ThrowIfCancellationRequested();
            calls.Add($"line.begin:{carrier}:{generation}");
            return ValueTask.FromResult(
                BridgeNetworkPakCarrierPortResult.Succeeded);
        },
        (carrier, token) =>
        {
            token.ThrowIfCancellationRequested();
            calls.Add($"line.release:{carrier}");
            return ValueTask.FromResult(
                BridgeNetworkPakCarrierPortResult.Succeeded);
        });
    DelegateBridgeNetworkPakQuickCarrierPort quick = new(
        () => quickAvailable,
        (generation, token) =>
        {
            token.ThrowIfCancellationRequested();
            calls.Add($"quick.start:{generation}");
            return ValueTask.FromResult(
                BridgeNetworkPakCarrierPortResult.Succeeded);
        },
        (generation, token) =>
        {
            token.ThrowIfCancellationRequested();
            calls.Add($"quick.stop:{generation}");
            return ValueTask.FromResult(
                BridgeNetworkPakCarrierPortResult.Succeeded);
        });
    DelegateBridgeNetworkPakApplicationOwnerPort application = new(
        connected =>
        {
            applicationConnected = connected;
            calls.Add($"app.connected:{connected}");
        },
        complete =>
        {
            handshakeComplete = complete;
            calls.Add($"app.handshake:{complete}");
        });
    DelegateBridgeNetworkPakCarrierDelay delay = new((duration, token) =>
    {
        token.ThrowIfCancellationRequested();
        delays.Add(duration);
        calls.Add($"delay:{duration.TotalMilliseconds:0}");
        return ValueTask.CompletedTask;
    });

    BridgeNetworkPakCarrierService service = new(
        state,
        new DelegateBridgeNetworkPakCarrierActivationContext(() =>
            activationRequired),
        store,
        line,
        quick,
        application,
        delay);

    BridgeNetworkPakCarrierResult initialized = await service.InitializeAsync();
    Require(initialized.Success &&
            state.Current.SelectedNetworkPakTransport == BridgeNetworkPakTransport.Tcp &&
            state.Current.ActiveNetworkPakTransport == BridgeNetworkPakTransport.None,
        "Desktop composition did not initialize authoritative selection from the adapter.");

    calls.Clear();
    BridgeNetworkPakCarrierResult pendingTcp = await service.ActivateSelectedAsync();
    long tcpGeneration = service.PendingLineActivationGeneration ?? 0;
    Require(pendingTcp.Outcome ==
            BridgeNetworkPakCarrierOutcome.ActivationPendingHandshake &&
            tcpGeneration > 0 && !handshakeComplete && applicationConnected &&
            state.Current.ActiveNetworkPakTransport == BridgeNetworkPakTransport.None,
        "Desktop line adapter claimed TCP active before ACK.");
    await service.ObserveLineHandshakeAcknowledgedAsync(
        BridgeNetworkPakTransport.Tcp, tcpGeneration);
    Require(state.Current.ActiveNetworkPakTransport == BridgeNetworkPakTransport.Tcp &&
            applicationConnected && handshakeComplete,
        "Generation-aware desktop ACK did not activate TCP.");

    calls.Clear();
    BridgeNetworkPakCarrierResult quickResult = await service.SelectCarrierAsync(
        BridgeNetworkPakTransport.ReliableUdpQuick);
    long quickGeneration = service.ActiveQuickGeneration ?? 0;
    Require(quickResult.Success && quickGeneration > 0 &&
            delays.SequenceEqual(new[] { TimeSpan.FromMilliseconds(50) }) &&
            state.Current.ActiveNetworkPakTransport ==
                BridgeNetworkPakTransport.ReliableUdpQuick,
        "Desktop adapter composition did not complete TCP to Quick.");
    RequireSubsequence(calls,
        "line.release:Tcp", "app.handshake:False", "app.connected:False",
        "delay:50", "state.select:ReliableUdpQuick",
        $"quick.start:{quickGeneration}", "app.connected:True",
        "app.handshake:True", "state.activate:ReliableUdpQuick");

    BridgeNetworkPakCarrierResult fault = await service.ReportQuickFaultAsync(
        quickGeneration, "integration fault");
    Require(!fault.Success && fault.Failure == BridgeNetworkPakCarrierFailure.Faulted &&
            state.Current.SelectedNetworkPakTransport ==
                BridgeNetworkPakTransport.ReliableUdpQuick &&
            state.Current.ActiveNetworkPakTransport == BridgeNetworkPakTransport.None &&
            !applicationConnected && !handshakeComplete,
        "Desktop Quick fault routing lost selection or retained ownership.");

    calls.Clear();
    BridgeNetworkPakCarrierResult retry = await service.SelectCarrierAsync(
        BridgeNetworkPakTransport.ReliableUdpQuick);
    long retryGeneration = service.ActiveQuickGeneration ?? 0;
    Require(retry.Success && retryGeneration > quickGeneration &&
            calls.SequenceEqual(new[]
            {
                $"quick.start:{retryGeneration}",
                "app.connected:True",
                "app.handshake:True",
                "state.activate:ReliableUdpQuick"
            }),
        "Desktop same-selection retry did not replace the faulted Quick lifetime.");

    calls.Clear();
    BridgeNetworkPakCarrierResult retired =
        await service.RetirePhysicalCarrierAsync("replacement");
    Require(retired.Success &&
            state.Current.SelectedNetworkPakTransport ==
                BridgeNetworkPakTransport.ReliableUdpQuick &&
            state.Current.ActiveNetworkPakTransport == BridgeNetworkPakTransport.None &&
            calls.Contains($"quick.stop:{retryGeneration}") &&
            !applicationConnected && !handshakeComplete,
        "Desktop replacement retirement did not preserve selected-only truth.");
    BridgeNetworkPakCarrierResult reactivated = await service.ActivateSelectedAsync();
    Require(reactivated.Success &&
            state.Current.ActiveNetworkPakTransport ==
                BridgeNetworkPakTransport.ReliableUdpQuick,
        "Explicit desktop post-replacement reactivation failed.");
    Console.WriteLine("PASS desktop adapters preserve ACK, handoff, Quick fault, retry, and replacement semantics");

    Require(NetworkPakCarrierSelection.DisplayName(
                NetworkPakWifiTransport.UsbSerial) == "USB" &&
            NetworkPakCarrierSelection.DisplayName(
                NetworkPakWifiTransport.Tcp) == "TCP" &&
            NetworkPakCarrierSelection.DisplayName(
                NetworkPakWifiTransport.ReliableUdpQuick) ==
                "Quick Mode (Reliable UDP)",
        "Accepted desktop carrier names changed.");
    Require(NetworkPakRudpTransport.DefaultPort == 6465 &&
            NetworkPakRudpTransport.Window == 8 &&
            NetworkPakRudpTransport.PhysicalFlightCap == 2 &&
            NetworkPakRudpTransport.PayloadBytes == 512 &&
            NetworkPakRudpTransport.RtoMs == 40 &&
            NetworkPakRudpTransport.RetryBudget == 4,
        "Frozen Quick protocol parameters changed.");
    Console.WriteLine("PASS user-visible names and frozen Quick parameters remain unchanged");
    Console.WriteLine("ALL BRIDGE NETWORK PAK DESKTOP INTEGRATION TESTS PASSED");
}
finally
{
    if (Directory.Exists(tempDirectory))
        Directory.Delete(tempDirectory, recursive: true);
}

sealed class FakeStatePort : IBridgeNetworkPakCarrierStatePort
{
    private readonly List<string> _calls;
    private BridgeDeviceStateSnapshot _current;

    public FakeStatePort(List<string> calls)
    {
        _calls = calls;
        _current = new("FAKE", "1", BridgeDeviceRole.Standalone,
            BridgeDeviceService.Network, FirmwarePersonality.Network,
            BridgeControlConnectionKind.WifiTcp, BridgeConnectionState.Connected,
            BridgeNetworkPakTransport.None, BridgeNetworkPakTransport.None,
            BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.DeviceReported),
            BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.BridgeImplemented),
            BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.ApplicationNegotiated),
            BridgeCapabilitySet.Empty(BridgeCapabilityProvenance.Effective),
            1, DateTimeOffset.UtcNow);
    }

    public BridgeDeviceStateSnapshot Current => _current;
    public event EventHandler<BridgeDeviceStateChangedEventArgs>? StateChanged
    {
        add { }
        remove { }
    }

    public void SelectCarrier(BridgeNetworkPakTransport carrier)
    {
        _calls.Add($"state.select:{carrier}");
        _current = _current with
        {
            SelectedNetworkPakTransport = carrier,
            ActiveNetworkPakTransport = _current.ActiveNetworkPakTransport == carrier
                ? carrier : BridgeNetworkPakTransport.None
        };
    }

    public void ActivateCarrier(BridgeNetworkPakTransport carrier)
    {
        _calls.Add($"state.activate:{carrier}");
        _current = _current with { ActiveNetworkPakTransport = carrier };
    }

    public void DeactivateCarrier(BridgeNetworkPakTransport carrier)
    {
        _calls.Add($"state.deactivate:{carrier}");
        if (_current.ActiveNetworkPakTransport == carrier)
            _current = _current with
            {
                ActiveNetworkPakTransport = BridgeNetworkPakTransport.None
            };
    }
}
