using System.Buffers.Binary;
using System.Text;
using PicoSwitchController;

NetworkPakBridgeService service = new();

void TestAnalogStickCalibration()
{
    AnalogStickCalibration calibration = AnalogStickCalibration.CreateDefault("DirectInput", "test", "Test");
    calibration.CenterX = 32767;
    calibration.CenterY = 32767;
    calibration.MinX = 0;
    calibration.MaxX = 65535;
    calibration.MinY = 0;
    calibration.MaxY = 65535;
    calibration.Deadzone = 1000;

    AnalogStickOutput center = calibration.Map(32767, 32767, 1, -1);
    if (center.X.N64 != 0 || center.Y.N64 != 0)
        throw new InvalidOperationException("Analog center did not map to N64 neutral");

    AnalogStickOutput insideDeadzone = calibration.Map(33700, 31850, 1, -1);
    if (insideDeadzone.X.N64 != 0 || insideDeadzone.Y.N64 != 0)
        throw new InvalidOperationException("Analog deadzone did not suppress small motion");

    AnalogStickOutput outsideDeadzone = calibration.Map(38000, 26000, 1, -1);
    if (outsideDeadzone.X.N64 <= 0 || outsideDeadzone.Y.N64 <= 0)
        throw new InvalidOperationException("Analog motion outside deadzone did not preserve direction");

    AnalogStickOutput fullPositive = calibration.Map(65535, 0, 1, -1);
    if (fullPositive.X.N64 != 80 || fullPositive.Y.N64 != 80)
        throw new InvalidOperationException($"Analog full positive mismatch: {fullPositive.X.N64},{fullPositive.Y.N64}");

    AnalogStickOutput fullNegative = calibration.Map(0, 65535, 1, -1);
    if (fullNegative.X.N64 != -80 || fullNegative.Y.N64 != -80)
        throw new InvalidOperationException($"Analog full negative mismatch: {fullNegative.X.N64},{fullNegative.Y.N64}");

    calibration.CenterX = 30000;
    calibration.MinX = 12000;
    calibration.MaxX = 60000;
    AnalogStickOutput asymmetric = calibration.Map(60000, 32767, 1, -1);
    if (asymmetric.X.N64 != 80)
        throw new InvalidOperationException("Analog asymmetric positive range did not reach max");

    AnalogAxisOutput clamped = AnalogStickCalibration.MapAxis(90000, 32767, 0, 65535, 1000, 1);
    if (clamped.N64 != 80)
        throw new InvalidOperationException("Analog clamping failed");

    uint stickRightMask = 1u << 17;
    string legacyRight = ControllerProfile.BuildCommand(ConsoleControllerType.N64, stickRightMask);
    if (legacyRight != "N64 00005000")
        throw new InvalidOperationException($"Legacy N64 stick-right command changed: {legacyRight}");

    string explicitAnalog = ControllerProfile.BuildCommand(ConsoleControllerType.N64, 0, 31, -22);
    if (explicitAnalog != "N64 00001FEA")
        throw new InvalidOperationException($"Explicit N64 analog command mismatch: {explicitAnalog}");

    Console.WriteLine("PASS analog stick calibration math/legacy N64 command");
}

void TestBridgeUiPolicyAndSettings()
{
    if (BridgeUiPolicy.RequiredFirmwareForAccessory(BridgeAccessoryMode.None) != FirmwarePersonality.Controller)
        throw new InvalidOperationException("No Accessory firmware mapping changed");
    if (BridgeUiPolicy.RequiredFirmwareForAccessory(BridgeAccessoryMode.ControllerPak) != FirmwarePersonality.Controller)
        throw new InvalidOperationException("Controller Pak firmware mapping changed");
    if (BridgeUiPolicy.RequiredFirmwareForAccessory(BridgeAccessoryMode.TransferPak) != FirmwarePersonality.Controller)
        throw new InvalidOperationException("Transfer Pak firmware mapping changed");
    if (BridgeUiPolicy.RequiredFirmwareForAccessory(BridgeAccessoryMode.NetworkPak) != FirmwarePersonality.Network)
        throw new InvalidOperationException("Network Pak firmware mapping changed");

    if (BridgeUiPolicy.RequiresFirmwareSwitch(BridgeAccessoryMode.TransferPak, FirmwarePersonality.Controller))
        throw new InvalidOperationException("Already-correct Controller firmware requested an unnecessary switch");
    if (!BridgeUiPolicy.RequiresFirmwareSwitch(BridgeAccessoryMode.NetworkPak, FirmwarePersonality.Controller))
        throw new InvalidOperationException("Wrong firmware did not request Network switch");
    if (BridgeUiPolicy.CanMarkFirmwareModeActive(FirmwareModeSwitchState.RequestingMode, FirmwarePersonality.Network, FirmwarePersonality.Network))
        throw new InvalidOperationException("Firmware mode was marked active before MODE? verification");
    if (!BridgeUiPolicy.CanMarkFirmwareModeActive(FirmwareModeSwitchState.ReadyNetwork, FirmwarePersonality.Network, FirmwarePersonality.Network))
        throw new InvalidOperationException("Verified Network firmware was not marked active");

    string path = Path.Combine(Path.GetTempPath(), $"nice64-settings-{Guid.NewGuid():N}.ini");
    try
    {
        BridgeSettings.SaveInputSourceMode(path, "XInput");
        BridgeSettings.SaveConsoleMode(path, "N64");
        BridgeSettings.SaveEnableOnScreenControls(path, false);
        BridgeSettings.SaveDebugMode(path, true);
        BridgeSettings settings = BridgeSettings.Load(path);
        if (settings.InputSourceMode != "XInput" ||
            settings.ConsoleMode != "N64" ||
            settings.EnableOnScreenControls ||
            !settings.DebugMode)
        {
            throw new InvalidOperationException("Bridge UI settings did not persist/reload");
        }
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
    }

    Console.WriteLine("PASS bridge UI policy/settings persistence");
}

void TestFirmwareModeProtocol()
{
    if (!FirmwareModeProtocol.TryParseMode("MODE|CONTROLLER|NONE", out FirmwareModeInfo controller) ||
        controller.Personality != FirmwarePersonality.Controller)
        throw new InvalidOperationException("Controller MODE parse failed");

    if (!FirmwareModeProtocol.TryParseMode("MODE|STANDALONE|DIAGNOSTICS", out FirmwareModeInfo diagnostics) ||
        diagnostics.Personality != FirmwarePersonality.Diagnostics)
        throw new InvalidOperationException("Diagnostics MODE parse failed");

    if (!FirmwareModeProtocol.TryParseMode("MODE|STANDALONE|NETWORK", out FirmwareModeInfo network) ||
        network.Personality != FirmwarePersonality.Network)
        throw new InvalidOperationException("Network MODE parse failed");

    FirmwareModeRequestResult ackController = FirmwareModeProtocol.ParseRequestResponse("REQUEST_MODE|ACK|CONTROLLER|NONE");
    if (ackController.Kind != FirmwareModeRequestKind.Ack || ackController.Mode?.Personality != FirmwarePersonality.Controller)
        throw new InvalidOperationException("ACK Controller parse failed");

    FirmwareModeRequestResult ackDiagnostics = FirmwareModeProtocol.ParseRequestResponse("REQUEST_MODE|ACK|STANDALONE|DIAGNOSTICS");
    if (ackDiagnostics.Kind != FirmwareModeRequestKind.Ack || ackDiagnostics.Mode?.Personality != FirmwarePersonality.Diagnostics)
        throw new InvalidOperationException("ACK Diagnostics parse failed");

    FirmwareModeRequestResult ackNetwork = FirmwareModeProtocol.ParseRequestResponse("REQUEST_MODE|ACK|STANDALONE|NETWORK");
    if (ackNetwork.Kind != FirmwareModeRequestKind.Ack || ackNetwork.Mode?.Personality != FirmwarePersonality.Network)
        throw new InvalidOperationException("ACK Network parse failed");

    FirmwareModeRequestResult alreadyController = FirmwareModeProtocol.ParseRequestResponse("REQUEST_MODE|ALREADY_ACTIVE|CONTROLLER|NONE");
    if (alreadyController.Kind != FirmwareModeRequestKind.AlreadyActive || alreadyController.Mode?.Personality != FirmwarePersonality.Controller)
        throw new InvalidOperationException("ALREADY_ACTIVE Controller parse failed");

    FirmwareModeRequestResult alreadyDiagnostics = FirmwareModeProtocol.ParseRequestResponse("REQUEST_MODE|ALREADY_ACTIVE|STANDALONE|DIAGNOSTICS");
    if (alreadyDiagnostics.Kind != FirmwareModeRequestKind.AlreadyActive || alreadyDiagnostics.Mode?.Personality != FirmwarePersonality.Diagnostics)
        throw new InvalidOperationException("ALREADY_ACTIVE Diagnostics parse failed");

    FirmwareModeRequestResult alreadyNetwork = FirmwareModeProtocol.ParseRequestResponse("REQUEST_MODE|ALREADY_ACTIVE|STANDALONE|NETWORK");
    if (alreadyNetwork.Kind != FirmwareModeRequestKind.AlreadyActive || alreadyNetwork.Mode?.Personality != FirmwarePersonality.Network)
        throw new InvalidOperationException("ALREADY_ACTIVE Network parse failed");

    if (FirmwareModeProtocol.ParseRequestResponse("REQUEST_MODE|ERR|UNSUPPORTED").Kind != FirmwareModeRequestKind.Unsupported)
        throw new InvalidOperationException("Unsupported parse failed");

    if (FirmwareModeProtocol.ParseRequestResponse("REQUEST_MODE|ACK|NETWORK|NETWORK").Kind != FirmwareModeRequestKind.Unknown)
        throw new InvalidOperationException("Malformed request response was accepted");

    FirmwareModeStateMachine machine = new();
    if (machine.State != FirmwareModeSwitchState.Disconnected)
        throw new InvalidOperationException("Initial firmware mode state mismatch");
    machine.Connected();
    machine.Querying();
    machine.Ready(FirmwarePersonality.Controller);
    if (machine.State != FirmwareModeSwitchState.ReadyController)
        throw new InvalidOperationException("Ready Controller transition failed");
    machine.Requesting(FirmwarePersonality.Diagnostics);
    machine.WaitingForDisconnect();
    machine.WaitingForReconnect();
    machine.Verifying();
    machine.Ready(FirmwarePersonality.Diagnostics);
    if (machine.State != FirmwareModeSwitchState.ReadyDiagnostics)
        throw new InvalidOperationException("Normal switch transition failed");
    machine.Requesting(FirmwarePersonality.Network);
    machine.WaitingForDisconnect();
    machine.WaitingForReconnect();
    machine.Verifying();
    machine.Ready(FirmwarePersonality.Network);
    if (machine.State != FirmwareModeSwitchState.ReadyNetwork)
        throw new InvalidOperationException("Ready Network transition failed");
    machine.Requesting(FirmwarePersonality.Controller);
    machine.WaitingForReconnect();
    machine.Ready(FirmwarePersonality.Controller);
    if (machine.State != FirmwareModeSwitchState.ReadyController)
        throw new InvalidOperationException("Lost-ACK reconnect transition failed");
    machine.Requesting(FirmwarePersonality.Diagnostics);
    machine.Failed();
    if (machine.State != FirmwareModeSwitchState.ModeSwitchFailed)
        throw new InvalidOperationException("Failure transition failed");

    Console.WriteLine("PASS firmware mode parser/state transitions");
}

void TestModuleRegistry()
{
    NetworkPakBridgeSnapshot initial = service.GetSnapshot();
    if (initial.ActiveModuleId != "network-portal" || initial.ActiveModule != "Network Portal")
        throw new InvalidOperationException($"Unexpected default module: {initial.ActiveModuleId} {initial.ActiveModule}");

    IReadOnlyList<INetworkPakModule> modules = service.ListModules();
    if (!modules.Any(module => module.Id == "network-portal" && module.DisplayName == "Network Portal"))
        throw new InvalidOperationException("Module list missing Network Portal");
    if (!modules.Any(module => module.Id == "diagnostics" && module.DisplayName == "Diagnostics"))
        throw new InvalidOperationException("Module list missing Diagnostics");
    if (!modules.Any(module => module.Id == "speed-test" && module.DisplayName == "Speed Test"))
        throw new InvalidOperationException("Module list missing Speed Test");
    if (!modules.Any(module => module.Id == "browser-test" && module.DisplayName == "Browser Test"))
        throw new InvalidOperationException("Module list missing Browser Test");
    if (!modules.Any(module => module.Id == "browser-live" && module.DisplayName == "Browser Live"))
        throw new InvalidOperationException("Module list missing Browser Live");
    if (modules.Any(module => module.Id == "example-module"))
        throw new InvalidOperationException("Reference-only example module should not be registered");

    if (!service.SetActiveModule("diagnostics"))
        throw new InvalidOperationException("Could not select Diagnostics module");
    NetworkPakBridgeSnapshot diagnostics = service.GetSnapshot();
    if (diagnostics.ActiveModuleId != "diagnostics" || diagnostics.ActiveModule != "Diagnostics")
        throw new InvalidOperationException($"Unexpected diagnostics module: {diagnostics.ActiveModuleId} {diagnostics.ActiveModule}");

    string status = Encoding.UTF8.GetString(ReadStream("diag://status"));
    if (!status.Contains("NICE64 NETWORK PAK DIAGNOSTICS", StringComparison.Ordinal) ||
        !status.Contains("Protocol:", StringComparison.Ordinal) ||
        !status.Contains("unchanged", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("diag://status did not return diagnostics text");
    }

    OpenStream("diag://status", 211);
    if (service.GetSnapshot().ActiveStreamCount != 1)
        throw new InvalidOperationException("Module switch regression setup did not open a stream");

    if (service.SetActiveModule("missing-module"))
        throw new InvalidOperationException("Invalid module unexpectedly selected");
    NetworkPakBridgeSnapshot fallback = service.GetSnapshot();
    if (fallback.ActiveModuleId != "network-portal" || fallback.ActiveModule != "Network Portal")
        throw new InvalidOperationException($"Invalid module did not fall back: {fallback.ActiveModuleId} {fallback.ActiveModule}");
    if (fallback.ActiveStreamCount != 0)
        throw new InvalidOperationException("Module selection no longer clears active streams");

    Console.WriteLine("PASS module registry default/list/select/fallback and stream clearing");
}

void TestBrowserTestModule()
{
    byte[] data = ReadStream("browser://test");
    if (data.Length < 16)
        throw new InvalidOperationException("browser://test returned too little data");
    if (Encoding.ASCII.GetString(data, 0, 4) != "NICE")
        throw new InvalidOperationException("browser://test missing NICE magic");
    ushort version = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4, 2));
    ushort blockCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(6, 2));
    uint pageId = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8, 4));
    uint payloadSize = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(12, 4));
    if (version != 1 || blockCount == 0 || pageId != 0x0000090D || payloadSize != data.Length - 16)
        throw new InvalidOperationException("browser://test header mismatch");
    string text = Encoding.UTF8.GetString(data);
    if (!text.Contains("Bridge Delivered NICE Document", StringComparison.Ordinal))
        throw new InvalidOperationException("browser://test missing expected title text");

    Console.WriteLine("PASS browser://test fixed NICE document");
}

void TestBrowserImageContract()
{
    (int SourceWidth, int SourceHeight, ushort Width, ushort Height)[] cases =
    {
        (4000, 3000, 128, 96),
        (1200, 400, 160, 53),
        (600, 1200, 48, 96),
        (64, 64, 64, 64),
        (160, 96, 160, 96),
        (160, 120, 128, 96)
    };
    foreach (var test in cases)
    {
        var size = BrowserImageContract.CalculateDimensions(test.SourceWidth, test.SourceHeight);
        if (size.Width != test.Width || size.Height != test.Height)
            throw new InvalidOperationException($"image resize mismatch for {test.SourceWidth}x{test.SourceHeight}: {size.Width}x{size.Height}");
    }

    (int SourceWidth, int SourceHeight, int ScaledWidth, int ScaledHeight, int CropX, int CropY)[] fillCases =
    {
        (4000, 3000, 160, 120, 0, 12),
        (400, 300, 160, 120, 0, 12),
        (1200, 400, 288, 96, 64, 0),
        (400, 1200, 160, 480, 0, 192),
        (64, 64, 160, 160, 0, 32)
    };
    foreach (var test in fillCases)
    {
        BrowserImageGeometry geometry = BrowserImageContract.CalculateGeometry(test.SourceWidth, test.SourceHeight, BrowserImageFitMode.Fill);
        if (geometry.Width != 160 || geometry.Height != 96 || geometry.ScaledWidth != test.ScaledWidth ||
            geometry.ScaledHeight != test.ScaledHeight || geometry.CropX != test.CropX || geometry.CropY != test.CropY)
        {
            throw new InvalidOperationException($"image fill mismatch for {test.SourceWidth}x{test.SourceHeight}: {geometry}");
        }
    }

    string path = Path.Combine(Path.GetTempPath(), $"nice64-image-contract-{Guid.NewGuid():N}.nimg");
    try
    {
        const ushort width = 160;
        const ushort height = 96;
        uint payloadLength = (uint)(width * height * BrowserImageContract.BytesPerPixel);
        byte[] nimg = new byte[BrowserImageContract.NimgHeaderSize + (int)payloadLength];
        Encoding.ASCII.GetBytes("NIMG").CopyTo(nimg, 0);
        BinaryPrimitives.WriteUInt16BigEndian(nimg.AsSpan(4, 2), BrowserImageContract.NimgVersion);
        BinaryPrimitives.WriteUInt16BigEndian(nimg.AsSpan(6, 2), width);
        BinaryPrimitives.WriteUInt16BigEndian(nimg.AsSpan(8, 2), height);
        BinaryPrimitives.WriteUInt16BigEndian(nimg.AsSpan(10, 2), BrowserImageContract.Rgb5A1Format);
        BinaryPrimitives.WriteUInt32BigEndian(nimg.AsSpan(12, 4), payloadLength);
        File.WriteAllBytes(path, nimg);
        if (!BrowserImageContract.TryReadNimg(path, out ushort parsedWidth, out ushort parsedHeight, out _) ||
            parsedWidth != width || parsedHeight != height)
        {
            throw new InvalidOperationException("valid 160x96 NIMG did not pass contract validation");
        }

        BinaryPrimitives.WriteUInt16BigEndian(nimg.AsSpan(8, 2), 97);
        File.WriteAllBytes(path, nimg);
        if (BrowserImageContract.TryReadNimg(path, out _, out _, out _))
            throw new InvalidOperationException("160x97 NIMG unexpectedly passed contract validation");

        BinaryPrimitives.WriteUInt16BigEndian(nimg.AsSpan(8, 2), height);
        BinaryPrimitives.WriteUInt16BigEndian(nimg.AsSpan(6, 2), 161);
        File.WriteAllBytes(path, nimg);
        if (BrowserImageContract.TryReadNimg(path, out _, out _, out _))
            throw new InvalidOperationException("161x96 NIMG unexpectedly passed contract validation");
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
    }

    Console.WriteLine("PASS browser image 160x96 dimension/NIMG contract");
}

void TestBrowserLiveModule()
{
    byte[] home = ReadStream("nice://home");
    string homeText = Encoding.UTF8.GetString(home);
    if (!homeText.Contains("Welcome.", StringComparison.Ordinal) ||
        !homeText.Contains("CNN Lite", StringComparison.Ordinal) ||
        !homeText.Contains("browser://live?u=cnn", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("nice://home missing browser home content");
    }

    byte[] data = ReadStream("browser://live");
    if (data.Length < 16)
        throw new InvalidOperationException("browser://live returned too little data");
    if (Encoding.ASCII.GetString(data, 0, 4) != "NICE")
        throw new InvalidOperationException("browser://live missing NICE magic");
    ushort version = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4, 2));
    ushort blockCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(6, 2));
    uint pageId = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8, 4));
    uint payloadSize = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(12, 4));
    if (version != 1 || blockCount == 0 || pageId != 0x00001002 || payloadSize != data.Length - 16 || data.Length > NiceWireDocument.MaxDocumentBytes || blockCount > NiceWireDocument.MaxBlocks)
        throw new InvalidOperationException("browser://live header mismatch");
    string text = Encoding.UTF8.GetString(data);
    string livePoll = ExtractFirstRoute(text, "browser://p/");
    byte[] finalLive = PollBrowserJob(livePoll);
    string finalLiveText = Encoding.UTF8.GetString(finalLive);
    bool finalLiveHasToken = finalLiveText.Contains("browser://g/", StringComparison.Ordinal);
    if (!finalLiveHasToken && !finalLiveText.Contains("did not provide static page content", StringComparison.Ordinal))
        throw new InvalidOperationException("browser://live final page did not contain navigation or the static-content fallback message");
    if (!ContainsCanonicalUrlMetadata(finalLive))
        throw new InvalidOperationException("browser://live final page missing canonical URL metadata");

    byte[] cnn = ReadStream("browser://live?u=cnn");
    string cnnText = Encoding.UTF8.GetString(ResolveBrowserPage(cnn));
    bool cnnHasToken = cnnText.Contains("browser://g/", StringComparison.Ordinal);
    if (!cnnHasToken && !cnnText.Contains("did not provide static page content", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("browser://live?u=cnn did not contain navigation or the static-content fallback message");
    }

    if (cnnHasToken)
    {
        string token = ExtractFirstRoute(cnnText, "browser://g/");
        byte[] linked = ResolveBrowserPage(ReadStream(token));
        string linkedText = Encoding.UTF8.GetString(linked);
        if (!linkedText.Contains("NICE", StringComparison.OrdinalIgnoreCase) && !linkedText.Contains("browser://g/", StringComparison.Ordinal))
            throw new InvalidOperationException("browser://go token did not resolve to a NICE document");
    }

    string invalidToken = Encoding.UTF8.GetString(ReadStream("browser://g/ZZZZ"));
    if (!invalidToken.Contains("ROUTE NOT FOUND", StringComparison.Ordinal))
        throw new InvalidOperationException("invalid browser://go token did not fail safely");

    if (!service.SetActiveModule("browser-live"))
        throw new InvalidOperationException("Could not select Browser Live for URL registration test");
    string registeredRoute = WriteBrowserUrl("https://en.wikipedia.org/wiki/Nintendo_64");
    if (!registeredRoute.StartsWith("browser://u/", StringComparison.Ordinal))
        throw new InvalidOperationException($"URL registration returned bad route: {registeredRoute}");
    string registeredText = Encoding.UTF8.GetString(ResolveBrowserPage(ReadStream(registeredRoute)));
    if (!registeredText.Contains("Nintendo", StringComparison.OrdinalIgnoreCase) &&
        !registeredText.Contains("GENERATOR", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("registered URL route did not resolve safely");
    service.SetActiveModule("network-portal");

    Console.WriteLine("PASS nice://home Bridge generated browser home");
    Console.WriteLine("PASS browser://live fetched real webpage and tokenized links");
}

bool ContainsCanonicalUrlMetadata(byte[] wire)
{
    if (wire.Length < 16) return false;
    int cursor = 16;
    int blocks = BinaryPrimitives.ReadUInt16BigEndian(wire.AsSpan(6, 2));
    for (int index = 0; index < blocks && cursor + 12 <= wire.Length; index++)
    {
        ushort type = BinaryPrimitives.ReadUInt16BigEndian(wire.AsSpan(cursor, 2));
        ushort textLength = BinaryPrimitives.ReadUInt16BigEndian(wire.AsSpan(cursor + 4, 2));
        ushort targetLength = BinaryPrimitives.ReadUInt16BigEndian(wire.AsSpan(cursor + 6, 2));
        cursor += 12;
        if (cursor + textLength + targetLength > wire.Length) return false;
        if (type == NiceWireDocument.CanonicalUrlMetadataType)
        {
            string value = Encoding.UTF8.GetString(wire, cursor, textLength);
            return value.StartsWith("http://", StringComparison.Ordinal) || value.StartsWith("https://", StringComparison.Ordinal);
        }
        cursor += textLength + targetLength;
    }
    return false;
}

byte[] PollBrowserJob(string pollRoute)
{
    for (int attempt = 0; attempt < 40; attempt++)
    {
        byte[] page = ReadStream(pollRoute);
        string text = Encoding.UTF8.GetString(page);
        if (!text.Contains("browser://p/", StringComparison.Ordinal))
        {
            return page;
        }
        Thread.Sleep(250);
    }
    throw new InvalidOperationException($"browser job did not become ready: {pollRoute}");
}

byte[] ResolveBrowserPage(byte[] page)
{
    string text = Encoding.UTF8.GetString(page);
    int start = text.IndexOf("browser://p/", StringComparison.Ordinal);
    return start < 0 ? page : PollBrowserJob(ExtractFirstRoute(text, "browser://p/"));
}

void TestBrowserFriendlyErrors()
{
    string logPath = Path.Combine(Path.GetTempPath(), $"nice64-generator-error-{Guid.NewGuid():N}.log");
    try
    {
        File.WriteAllText(logPath, """
            [2026-07-11T00:00:00] FETCH FAILED: The remote server returned an error: (403) Forbidden.
            [2026-07-11T00:00:00] ERROR_CODE=HTTP_403
            [2026-07-11T00:00:00] ERROR_TITLE=SITE REFUSED REQUEST
            [2026-07-11T00:00:00] ERROR_DETAIL=The website returned HTTP 403 and the Bridge could not download the page.
            [2026-07-11T00:00:00] HTTP_STATUS=403
            [2026-07-11T00:00:00] EXCEPTION_TYPE=System.Net.WebException
            [2026-07-11T00:00:00] FETCH_ELAPSED_MS=321
            """);

        BrowserLiveModule.BrowserGeneratorError error = BrowserLiveModule.ParseGeneratorError(logPath);
        if (error.Title != "SITE REFUSED REQUEST" ||
            error.Code != "HTTP_403" ||
            error.HttpStatus != "403" ||
            error.ElapsedMs != 321)
        {
            throw new InvalidOperationException($"HTTP 403 generator error mapping failed: {error}");
        }

        byte[] wire = NiceWireDocument.BuildErrorDocument(error.Title, error.Detail);
        string text = Encoding.UTF8.GetString(wire);
        if (!text.Contains("SITE REFUSED REQUEST", StringComparison.Ordinal) ||
            !text.Contains("HTTP 403", StringComparison.Ordinal) ||
            !text.Contains("nice://home", StringComparison.Ordinal) ||
            !text.Contains("browser://test", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Friendly HTTP error NICE document missing expected content");
        }

        Console.WriteLine("PASS browser friendly HTTP error document mapping");
    }
    finally
    {
        if (File.Exists(logPath)) File.Delete(logPath);
    }
}

void TestBrowserRouteRegistry()
{
    string root = Path.Combine(Path.GetTempPath(), $"nice64-browser-routes-{Guid.NewGuid():N}");
    try
    {
        BrowserSessionStore store = new(root);
        BrowserToken first = store.CreateToken("pico-local", "session-default", "HTTPS://MILK.COM:443/about.html#top", "page-a", BrowserTokenKind.Link);
        BrowserToken second = store.CreateToken("pico-local", "session-default", "https://milk.com/about.html", "page-b", BrowserTokenKind.Link);
        if (first.Token != second.Token || first.Route != second.Route)
            throw new InvalidOperationException("route registry did not reuse normalized URL token");
        if (!File.Exists(store.RoutesPath))
            throw new InvalidOperationException("routes.csv was not created");

        BrowserSessionStore reloaded = new(root);
        BrowserTokenLookup lookup = reloaded.Lookup("pico-local", "session-default", first.Token);
        if (lookup.Status != BrowserTokenStatus.Found || lookup.Token?.TargetUrl != "https://milk.com/about.html")
            throw new InvalidOperationException("route registry did not reload token from CSV");

        Parallel.For(0, 32, index =>
        {
            reloaded.CreateToken("pico-local", "session-default", $"https://example.com/page-{index}?q=1", "parallel", BrowserTokenKind.Link);
        });

        string[] routeLines = File.ReadAllLines(reloaded.RoutesPath);
        string[] tokens = routeLines.Skip(1).Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => line.Split(',')[1]).ToArray();
        if (tokens.Length != tokens.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            throw new InvalidOperationException("route registry produced duplicate tokens");

        reloaded.RecordCall(new BrowserRouteCall(DateTime.UtcNow, "pico-local", "session-default", first.Route, first.Token, first.TargetUrl, "Success", "200", 123, "test-request", ""));
        if (!File.ReadAllText(reloaded.CallsPath).Contains("test-request", StringComparison.Ordinal))
            throw new InvalidOperationException("calls.csv did not record route request");

        Console.WriteLine("PASS browser persistent route registry reload/reuse/concurrency/logging");
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

void TestDuckDuckGoRedirectUnwrapping()
{
    const string wrapper = "https://duckduckgo.com/l/?uddg=https%3A%2F%2Fwww.merriam-webster.com%2Fdictionary%2Ftest&rut=tracking";
    if (!BrowserLiveModule.TryUnwrapDuckDuckGoRedirect(wrapper, out string target, out string? reason) ||
        target != "https://www.merriam-webster.com/dictionary/test" || reason is not null)
    {
        throw new InvalidOperationException("valid DuckDuckGo HTTPS redirect was not unwrapped");
    }

    const string httpWrapper = "https://www.duckduckgo.com/l/?uddg=http%3A%2F%2Fexample.com%2Fpath%3Fa%3D1%26b%3Dtwo";
    if (!BrowserLiveModule.TryUnwrapDuckDuckGoRedirect(httpWrapper, out target, out reason) ||
        target != "http://example.com/path?a=1&b=two" || reason is not null)
    {
        throw new InvalidOperationException("DuckDuckGo HTTP redirect with query string was not unwrapped");
    }

    string[] invalidWrappers =
    {
        "https://duckduckgo.com/l/?rut=tracking",
        "https://duckduckgo.com/l/?uddg=",
        "https://duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fbad%ZZ",
        "https://duckduckgo.com/l/?uddg=%2Frelative",
        "https://duckduckgo.com/l/?uddg=javascript%3Aalert(1)",
        "https://duckduckgo.com/l/?uddg=data%3Atext%2Fplain%2Ctest"
    };
    foreach (string invalid in invalidWrappers)
    {
        if (BrowserLiveModule.TryUnwrapDuckDuckGoRedirect(invalid, out _, out reason) || reason is null)
            throw new InvalidOperationException($"invalid DuckDuckGo redirect was accepted: {invalid}");
    }

    const string direct = "https://example.com/path?uddg=https%3A%2F%2Fignored.example";
    if (BrowserLiveModule.TryUnwrapDuckDuckGoRedirect(direct, out target, out reason) || target != direct || reason is not null)
        throw new InvalidOperationException("non-DuckDuckGo URL was changed");

    Console.WriteLine("PASS DuckDuckGo result redirects unwrap to validated direct targets");
}

string ExtractFirstRoute(string text, string prefix)
{
    int start = text.IndexOf(prefix, StringComparison.Ordinal);
    if (start < 0)
        throw new InvalidOperationException($"No {prefix} token found");
    int end = start;
    while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] != '\0')
    {
        end++;
    }
    return text[start..end].TrimEnd('\0', '"', '<', '>');
}

string WriteBrowserUrl(string url)
{
    byte sequence = 180;
    byte[] data = Encoding.UTF8.GetBytes(url);
    int offset = 0;
    bool first = true;
    string route = string.Empty;
    while (offset < data.Length || first)
    {
        int count = Math.Min(21, data.Length - offset);
        bool last = offset + count >= data.Length;
        byte[] payload = new byte[2 + count];
        payload[0] = (byte)((first ? 1 : 0) | (last ? 2 : 0));
        payload[1] = (byte)count;
        if (count > 0)
        {
            data.AsSpan(offset, count).CopyTo(payload.AsSpan(2));
        }
        string response = Exchange($"NETPAK_REQ_HEX|{sequence++}|WRITE_STREAM_CHUNK|{Convert.ToHexString(payload)}");
        if (!response.Contains("|OK|A4|", StringComparison.Ordinal))
            throw new InvalidOperationException($"Bad browser URL write ack: {response}");
        if (last)
        {
            route = Encoding.ASCII.GetString(Convert.FromHexString(response.Split('|')[4]));
        }
        offset += count;
        first = false;
        if (last) break;
    }

    return route;
}

void TestModuleSettingsPersistence()
{
    string path = Path.Combine(Path.GetTempPath(), $"nice64-module-settings-{Guid.NewGuid():N}.ini");
    try
    {
        File.WriteAllText(path, """
            [Timing]
            GamepadPollIntervalMs=2

            [NetworkPak]
            ActiveNetworkPakModuleId=diagnostics
            SearchProviderName=Test Search
            SearchProviderUrl=https://search.example.test/find?q={query}
            """);
        BridgeSettings settings = BridgeSettings.Load(path);
        if (settings.ActiveNetworkPakModuleId != "diagnostics")
            throw new InvalidOperationException($"INI read failed: {settings.ActiveNetworkPakModuleId}");
        if (settings.SearchProviderName != "Test Search" ||
            settings.SearchProviderUrl != "https://search.example.test/find?q={query}")
        {
            throw new InvalidOperationException("Search provider INI values were not loaded.");
        }

        BridgeSettings.SaveActiveNetworkPakModuleId(path, "network-portal");
        settings = BridgeSettings.Load(path);
        if (settings.ActiveNetworkPakModuleId != "network-portal")
            throw new InvalidOperationException($"INI update failed: {settings.ActiveNetworkPakModuleId}");

        string missingPath = Path.Combine(Path.GetTempPath(), $"nice64-module-settings-{Guid.NewGuid():N}.ini");
        try
        {
            BridgeSettings.SaveActiveNetworkPakModuleId(missingPath, "diagnostics");
            settings = BridgeSettings.Load(missingPath);
            if (settings.ActiveNetworkPakModuleId != "diagnostics")
                throw new InvalidOperationException($"INI create failed: {settings.ActiveNetworkPakModuleId}");
        }
        finally
        {
            if (File.Exists(missingPath)) File.Delete(missingPath);
        }
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
    }

    Console.WriteLine("PASS module INI persistence read/update/create");
}

void TestReadableSourceModeConfiguration()
{
    if (BrowserReadableSourceModeParser.Parse("LOCAL") != BrowserReadableSourceMode.Local ||
        BrowserReadableSourceModeParser.Parse("DIRECT") != BrowserReadableSourceMode.Direct ||
        BrowserReadableSourceModeParser.Parse("FROGFIND") != BrowserReadableSourceMode.FrogFind ||
        BrowserReadableSourceModeParser.Parse("invalid") != BrowserReadableSourceMode.Auto)
    {
        throw new InvalidOperationException("Readable source mode parsing did not preserve safe AUTO fallback.");
    }

    Console.WriteLine("PASS browser readable source mode configuration/fallback");
}

void TestExternalModuleFailureHandling()
{
    string modulesRoot = Path.Combine(AppContext.BaseDirectory, "modules");
    string duplicateRoot = Path.Combine(modulesRoot, "test-duplicate-network-portal");
    string badRoot = Path.Combine(modulesRoot, "test-bad-external");
    try
    {
        Directory.CreateDirectory(Path.Combine(duplicateRoot, "bin"));
        Directory.CreateDirectory(Path.Combine(badRoot, "bin"));
        File.WriteAllText(Path.Combine(duplicateRoot, "module.json"), """
            {
              "id": "network-portal",
              "displayName": "Duplicate Network Portal",
              "version": "0.0.1",
              "author": "Smoke Test",
              "description": "Should not override built-in Network Portal.",
              "resourcePrefix": "bridge://",
              "defaultResource": "bridge://menu",
              "contentRoot": "content",
              "assemblyPath": "bin/MissingDuplicate.dll",
              "protocolVersion": 1
            }
            """);
        File.WriteAllText(Path.Combine(badRoot, "module.json"), """
            {
              "id": "bad-external",
              "displayName": "Bad External",
              "version": "0.0.1",
              "author": "Smoke Test",
              "description": "Should fail to load without crashing.",
              "resourcePrefix": "bad://",
              "defaultResource": "bad://status",
              "contentRoot": "content",
              "assemblyPath": "bin/MissingBad.dll",
              "protocolVersion": 1
            }
            """);

        NetworkPakBridgeService isolated = new("bad-external");
        IReadOnlyList<INetworkPakModule> modules = isolated.ListModules();
        if (!modules.Any(module => module.Id == "network-portal" && module.DisplayName == "Network Portal"))
            throw new InvalidOperationException("Built-in Network Portal missing after external failure test");
        if (!modules.Any(module => module.Id == "diagnostics" && module.DisplayName == "Diagnostics"))
            throw new InvalidOperationException("Built-in Diagnostics missing after external failure test");
        if (modules.Any(module => module.Id == "bad-external"))
            throw new InvalidOperationException("Bad external module was registered");

        NetworkPakBridgeSnapshot snapshot = isolated.GetSnapshot();
        if (snapshot.ActiveModuleId != "network-portal" || snapshot.ActiveModule != "Network Portal")
            throw new InvalidOperationException($"Bad external did not fall back: {snapshot.ActiveModuleId} {snapshot.ActiveModule}");
    }
    finally
    {
        if (Directory.Exists(duplicateRoot)) Directory.Delete(duplicateRoot, recursive: true);
        if (Directory.Exists(badRoot)) Directory.Delete(badRoot, recursive: true);
    }

    Console.WriteLine("PASS external duplicate/bad modules skipped without crash");
}

string Exchange(string line)
{
    if (!service.TryHandleRequest(line, out string response) || string.IsNullOrEmpty(response))
        throw new InvalidOperationException($"No response for {line}");
    service.MarkNetworkPakResponseWritten(response);
    return response;
}

byte[] ReadStream(string resource, int? expectedLength = null)
{
    byte sequence = 1;
    (byte id, int length, uint expectedCrc) = OpenStream(resource, sequence);
    if (expectedLength.HasValue && length != expectedLength.Value)
        throw new InvalidOperationException($"Length {length} != {expectedLength.Value}");

    List<byte> received = new(length);
    for (int offset = 0; offset < length; offset += 16)
    {
        byte[] request = new byte[5];
        request[0] = id;
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(1, 4), (uint)offset);
        sequence++;
        string response = Exchange($"NETPAK_REQ_HEX|{sequence}|READ_STREAM_CHUNK|{Convert.ToHexString(request)}");
        string[] parts = response.Split('|');
        if (parts[2] != "OK" || parts[3] != "A2") throw new InvalidOperationException(response);
        byte[] chunk = Convert.FromHexString(parts[4]);
        if (chunk[0] != id || BinaryPrimitives.ReadUInt32BigEndian(chunk.AsSpan(1, 4)) != offset)
            throw new InvalidOperationException("Stream ID or offset mismatch");
        int count = chunk[5];
        received.AddRange(chunk.AsSpan(7, count).ToArray());
    }

    sequence++;
    string close = Exchange($"NETPAK_REQ_HEX|{sequence}|CLOSE_STREAM|{id:X2}");
    if (!close.Contains("|OK|A3|", StringComparison.Ordinal)) throw new InvalidOperationException(close);
    uint actualCrc = Crc32(received.ToArray());
    if (received.Count != length || actualCrc != expectedCrc)
        throw new InvalidOperationException($"Verify failed: {received.Count}, {actualCrc:X8} != {expectedCrc:X8}");
    return received.ToArray();
}

(byte Id, int Length, uint ExpectedCrc) OpenStream(string resource, byte sequence)
{
    string open = Exchange($"NETPAK_REQ_HEX|{sequence}|OPEN_STREAM|{Convert.ToHexString(Encoding.ASCII.GetBytes(resource))}");
    string[] openParts = open.Split('|');
    if (openParts[2] != "OK" || openParts[3] != "A0") throw new InvalidOperationException(open);
    byte[] metadata = Convert.FromHexString(openParts[4]);
    byte id = metadata[0];
    int length = (int)BinaryPrimitives.ReadUInt32BigEndian(metadata.AsSpan(1, 4));
    uint expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(metadata.AsSpan(6, 4));
    return (id, length, expectedCrc);
}

void TestStream(string resource, int expectedLength)
{
    byte[] received = ReadStream(resource, expectedLength);
    uint actualCrc = Crc32(received);
    Console.WriteLine($"PASS {resource} bytes={expectedLength} crc={actualCrc:X8}");
}

void TestSpeedStreamMetadata(string resource, int expectedLength)
{
    byte sequence = 1;
    (byte id, int length, uint expectedCrc) = OpenStream(resource, sequence);
    uint deterministicCrc = Crc32(MakeSpeedPattern(expectedLength));
    if (length != expectedLength || expectedCrc != deterministicCrc)
    {
        throw new InvalidOperationException($"{resource} metadata mismatch length={length}/{expectedLength} crc={expectedCrc:X8}/{deterministicCrc:X8}");
    }

    sequence++;
    string close = Exchange($"NETPAK_REQ_HEX|{sequence}|CLOSE_STREAM|{id:X2}");
    if (!close.Contains("|OK|A3|", StringComparison.Ordinal)) throw new InvalidOperationException(close);
    Console.WriteLine($"PASS {resource} metadata bytes={expectedLength} crc={expectedCrc:X8}");
}

void TestSpeedStream(string resource, int expectedLength)
{
    byte[] received = ReadStream(resource, expectedLength);
    byte[] expected = MakeSpeedPattern(expectedLength);
    if (!received.AsSpan().SequenceEqual(expected))
        throw new InvalidOperationException($"{resource} deterministic pattern mismatch");
    uint actualCrc = Crc32(received);
    Console.WriteLine($"PASS {resource} full path bytes={expectedLength} crc={actualCrc:X8}");
}

byte[] MakeSpeedPattern(int length)
{
    byte[] data = new byte[length];
    for (uint index = 0; index < data.Length; index++)
    {
        data[index] = (byte)((index * 251u + 7u) & 0xFFu);
    }
    return data;
}

void TestTextStream(string resource, string contentFileName)
{
    string contentPath = Path.Combine(AppContext.BaseDirectory, "modules", "network-portal", "content", contentFileName);
    string expectedText = File.ReadAllText(contentPath);
    byte[] received = ReadStream(resource, Encoding.UTF8.GetByteCount(expectedText));
    string actual = Encoding.UTF8.GetString(received);
    if (!string.Equals(actual, expectedText, StringComparison.Ordinal))
        throw new InvalidOperationException($"{resource} text mismatch");
    Console.WriteLine($"PASS {resource} text bytes={received.Length}");
}

void TestModulePackages()
{
    string modulesRoot = Path.Combine(AppContext.BaseDirectory, "modules");
    ModulePackage portal = ModulePackage.Load(Path.Combine(modulesRoot, "network-portal"));
    ModulePackage diagnostics = ModulePackage.Load(Path.Combine(modulesRoot, "diagnostics"));
    if (portal.Manifest.Id != "network-portal" ||
        portal.Manifest.ResourcePrefix != "bridge://" ||
        portal.TryReadContentBytes("portal.json") is null ||
        portal.TryReadContentBytes("welcome.txt") is null)
    {
        throw new InvalidOperationException("Network Portal package did not load expected content");
    }
    if (diagnostics.Manifest.Id != "diagnostics" ||
        diagnostics.Manifest.ResourcePrefix != "diag://" ||
        diagnostics.TryReadContentBytes("status.txt") is null)
    {
        throw new InvalidOperationException("Diagnostics package did not load expected content");
    }

    Console.WriteLine("PASS module packages loaded from modules/");
}

void WriteQuery(string query)
{
    byte sequence = 90;
    byte[] data = Encoding.UTF8.GetBytes(query);
    int offset = 0;
    bool first = true;
    while (offset < data.Length || first)
    {
        int count = Math.Min(21, data.Length - offset);
        bool last = offset + count >= data.Length;
        byte[] payload = new byte[2 + count];
        payload[0] = (byte)((first ? 1 : 0) | (last ? 2 : 0));
        payload[1] = (byte)count;
        if (count > 0)
        {
            data.AsSpan(offset, count).CopyTo(payload.AsSpan(2));
        }
        string response = Exchange($"NETPAK_REQ_HEX|{sequence++}|WRITE_STREAM_CHUNK|{Convert.ToHexString(payload)}");
        if (!response.Contains("|OK|A4|01", StringComparison.Ordinal))
            throw new InvalidOperationException($"Bad write ack: {response}");
        offset += count;
        first = false;
        if (last) break;
    }
}

void TestGeneratedMenu()
{
    string actual = Encoding.UTF8.GetString(ReadStream("bridge://menu"));
    string[] required =
    [
        "Welcome|bridge://welcome|smile",
        "Headlines|bridge://rssr|news",
        "ITcorp Demo|bridge://itcorp|web",
        "About|bridge://about|info",
        "Diagnostics|bridge://diagnostics|diag",
        "Ask NICE64|bridge://ollama|chat"
    ];
    foreach (string value in required)
    {
        if (!actual.Contains(value, StringComparison.Ordinal))
            throw new InvalidOperationException($"bridge://menu missing {value}");
    }

    foreach (string line in actual.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        string[] parts = line.Split('|');
        if (parts.Length < 2)
            throw new InvalidOperationException($"Bad menu line: {line}");
        if (Encoding.ASCII.GetByteCount(parts[1]) > 23)
            throw new InvalidOperationException($"Menu resource too long: {parts[1]}");
    }

    Console.WriteLine($"PASS bridge://menu generated from portal.json bytes={Encoding.UTF8.GetByteCount(actual)}");
}

void TestGeneratedTime()
{
    string actual = Encoding.UTF8.GetString(ReadStream("bridge://time"));
    string[] required =
    [
        "CURRENT TIME",
        "Date:",
        "Time:",
        "Timezone:",
        "America/Phoenix",
        "Bridge Version:",
        BridgeConstants.BridgeVersion,
        "Portal:",
        "Online"
    ];
    foreach (string value in required)
    {
        if (!actual.Contains(value, StringComparison.Ordinal))
            throw new InvalidOperationException($"bridge://time missing {value}");
    }
    Console.WriteLine($"PASS bridge://time generated bytes={Encoding.UTF8.GetByteCount(actual)}");
}

void TestAskNice64()
{
    const string query = "WHAT IS A TRANSFER PAK";
    WriteQuery(query);
    string actual = Encoding.UTF8.GetString(ReadStream("bridge://aires"));
    if (!actual.Contains("ASK NICE64", StringComparison.Ordinal) ||
        !actual.Contains(query, StringComparison.Ordinal) ||
        !actual.Contains("Retrieved by NICE64 Bridge", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("bridge://aires did not return an Ask NICE64 page");
    }
    Console.WriteLine($"PASS bridge://aires ask page bytes={Encoding.UTF8.GetByteCount(actual)}");
}

void TestCompactNow()
{
    string actual = Encoding.UTF8.GetString(ReadStream("bridge://now")).Trim();
    string[] parts = actual.Split('|');
    if (parts.Length != 3 ||
        !parts[0].Contains('-', StringComparison.Ordinal) ||
        !parts[1].Contains(':', StringComparison.Ordinal) ||
        parts[2] != "America/Phoenix")
    {
        throw new InvalidOperationException($"bridge://now bad payload: {actual}");
    }
    Console.WriteLine($"PASS bridge://now compact time {actual}");
}

void TestItcorp()
{
    string actual = Encoding.UTF8.GetString(ReadStream("bridge://itcorp"));
    if (!actual.Contains("ITCORP", StringComparison.OrdinalIgnoreCase) ||
        !actual.Contains("Retrieved by NICE64 Bridge", StringComparison.Ordinal) ||
        actual.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
        actual.Contains("<script", StringComparison.OrdinalIgnoreCase) ||
        actual.Contains("<style", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("bridge://itcorp did not return converted text");
    }
    Console.WriteLine($"PASS bridge://itcorp converted bytes={Encoding.UTF8.GetByteCount(actual)}");
}

void TestHeadlines(string resource, string expectedSource)
{
    string actual = Encoding.UTF8.GetString(ReadStream(resource));
    string[] required =
    [
        "HEADLINES",
        $"Source: {expectedSource}",
        "Retrieved by NICE64 Bridge"
    ];
    foreach (string value in required)
    {
        if (!actual.Contains(value, StringComparison.Ordinal))
            throw new InvalidOperationException($"{resource} missing {value}");
    }
    if (actual.Contains("<rss", StringComparison.OrdinalIgnoreCase) ||
        actual.Contains("<item", StringComparison.OrdinalIgnoreCase) ||
        actual.Contains("<title", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException($"{resource} leaked RSS/XML markup");
    }
    if (!actual.Contains("HEADLINES UNAVAILABLE", StringComparison.Ordinal) &&
        !actual.Contains("1.", StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"{resource} did not return headlines or readable unavailable page");
    }
    Console.WriteLine($"PASS {resource} rss text bytes={Encoding.UTF8.GetByteCount(actual)}");
}

static uint Crc32(ReadOnlySpan<byte> data)
{
    uint crc = 0xFFFFFFFFu;
    foreach (byte value in data)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
    }
    return ~crc;
}

void TestModuleSelectionOpenStreamAtomicity()
{
    NetworkPakBridgeService isolated = new();
    using ManualResetEventSlim moduleChanged = new(false);
    using ManualResetEventSlim releaseSelection = new(false);
    isolated.ActiveModuleChangedForTest = () =>
    {
        moduleChanged.Set();
        if (!releaseSelection.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("Module-selection race test was not released.");
    };

    Task<bool> selection = Task.Run(() => isolated.SetActiveModule("diagnostics"));
    if (!moduleChanged.Wait(TimeSpan.FromSeconds(5)))
        throw new TimeoutException("Module selection did not reach the atomic transition.");

    string resourceHex = Convert.ToHexString(Encoding.ASCII.GetBytes("diag://status"));
    Task<string> open = Task.Run(() =>
    {
        if (!isolated.TryHandleRequest(
                $"NETPAK_REQ_HEX|231|OPEN_STREAM|{resourceHex}", out string response))
            throw new InvalidOperationException("Concurrent OPEN_STREAM was not handled.");
        return response;
    });

    if (open.Wait(TimeSpan.FromMilliseconds(150)))
        throw new InvalidOperationException(
            "OPEN_STREAM crossed the in-progress module mutation/invalidation boundary.");

    releaseSelection.Set();
    if (!selection.GetAwaiter().GetResult())
        throw new InvalidOperationException("Diagnostics selection unexpectedly fell back.");
    string opened = open.GetAwaiter().GetResult();
    if (!opened.Contains("|OK|A0|", StringComparison.Ordinal))
        throw new InvalidOperationException($"Concurrent OPEN_STREAM failed: {opened}");

    byte streamId = Convert.FromHexString(opened.Split('|')[4])[0];
    byte[] readRequest = [streamId, 0, 0, 0, 0];
    if (!isolated.TryHandleRequest(
            $"NETPAK_REQ_HEX|232|READ_STREAM_CHUNK|{Convert.ToHexString(readRequest)}",
            out string read) || !read.Contains("|OK|A2|", StringComparison.Ordinal))
        throw new InvalidOperationException(
            $"Module selection invalidated the concurrently opened stream: {read}");

    isolated.ActiveModuleChangedForTest = null;
    Console.WriteLine("PASS module selection/OPEN_STREAM atomicity");
}

TestAnalogStickCalibration();
TestBridgeUiPolicyAndSettings();
TestFirmwareModeProtocol();
TestModulePackages();
TestExternalModuleFailureHandling();
TestModuleRegistry();
TestModuleSelectionOpenStreamAtomicity();
TestBrowserTestModule();
TestBrowserImageContract();
TestBrowserLiveModule();
TestBrowserFriendlyErrors();
TestBrowserRouteRegistry();
TestDuckDuckGoRedirectUnwrapping();
TestModuleSettingsPersistence();
TestReadableSourceModeConfiguration();
TestTextStream("bridge://welcome", "welcome.txt");
TestGeneratedMenu();
string longtext = Encoding.UTF8.GetString(ReadStream("bridge://longtext"));
if (!longtext.Contains("Headlines|bridge://rssr|news", StringComparison.Ordinal))
    throw new InvalidOperationException("bridge://longtext did not return menu compatibility content");
Console.WriteLine($"PASS bridge://longtext generated compatibility bytes={Encoding.UTF8.GetByteCount(longtext)}");
TestTextStream("bridge://about", "about.txt");
TestTextStream("bridge://diagnostics", "diagnostics.txt");
TestTextStream("bridge://news", "news.txt");
TestTextStream("bridge://projects", "projects.txt");
TestGeneratedTime();
TestCompactNow();
TestAskNice64();
TestItcorp();
TestHeadlines("bridge://headlines", "Retro Gaming");
TestHeadlines("bridge://rssr", "Retro Gaming");
TestHeadlines("bridge://rssn", "General News");
TestStream("bridge://test1k", 1024);
TestStream("bridge://test4k", 4096);
TestStream("bridge://test16k", 16384);
TestSpeedStreamMetadata("speed://1k", 1024);
TestSpeedStreamMetadata("speed://4k", 4096);
TestSpeedStreamMetadata("speed://16k", 16 * 1024);
TestSpeedStreamMetadata("speed://64k", 64 * 1024);
TestSpeedStreamMetadata("speed://256k", 256 * 1024);
TestSpeedStreamMetadata("speed://1m", 1024 * 1024);
TestSpeedStream("speed://1k", 1024);
TestSpeedStream("speed://4k", 4096);
TestSpeedStream("speed://16k", 16 * 1024);
TestSpeedStream("speed://64k", 64 * 1024);

string missing = Exchange("NETPAK_REQ_HEX|7|OPEN_STREAM|6272696467653A2F2F6D697373696E67");
if (!missing.Contains("|ERR|FF|", StringComparison.Ordinal) ||
    Encoding.ASCII.GetString(Convert.FromHexString(missing.Split('|')[4])) != "STREAM_NOT_FOUND")
    throw new InvalidOperationException($"Bad missing-resource result: {missing}");
Console.WriteLine("PASS missing resource -> STREAM_NOT_FOUND");

for (int index = 0; index < 100; index++)
{
    byte sequence = (byte)index;
    string resourceHex = Convert.ToHexString(Encoding.ASCII.GetBytes("bridge://welcome"));
    string open = Exchange($"NETPAK_REQ_HEX|{sequence}|OPEN_STREAM|{resourceHex}");
    byte id = Convert.FromHexString(open.Split('|')[4])[0];
    byte[] readRequest = new byte[] { id, 0, 0, 0, 0 };
    string read = Exchange($"NETPAK_REQ_HEX|{sequence}|READ_STREAM_CHUNK|{Convert.ToHexString(readRequest)}");
    string close = Exchange($"NETPAK_REQ_HEX|{sequence}|CLOSE_STREAM|{id:X2}");
    if (!open.Contains("|OK|A0|", StringComparison.Ordinal) ||
        !read.Contains("|OK|A2|", StringComparison.Ordinal) ||
        !close.Contains("|OK|A3|", StringComparison.Ordinal))
        throw new InvalidOperationException($"Session loop failed at {index}");
}
Console.WriteLine("PASS 100 open/read/close sessions");

NetworkPakBridgeSnapshot snapshot = service.GetSnapshot();
if (snapshot.ActiveModuleId != "network-portal" || snapshot.ActiveModule != "Network Portal")
    throw new InvalidOperationException($"Unexpected active module: {snapshot.ActiveModuleId} {snapshot.ActiveModule}");
Console.WriteLine($"PASS active module {snapshot.ActiveModule}");
Console.WriteLine($"sessions open={snapshot.StreamOpenCount} chunks={snapshot.StreamChunkCount} close={snapshot.StreamCloseCount} errors={snapshot.StreamErrorCount}");
Console.WriteLine($"bytes requested={snapshot.BytesRequested} delivered={snapshot.BytesDelivered} active={snapshot.ActiveStreamCount}");
