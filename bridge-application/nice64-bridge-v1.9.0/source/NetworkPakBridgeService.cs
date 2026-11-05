using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace PicoSwitchController;

public sealed record NetworkPakBridgeSnapshot(
    bool PicoConnected,
    bool HandshakeComplete,
    long RequestCount,
    long ResponseCount,
    long ErrorCount,
    string LastRequest,
    string LastResponse,
    string LastError,
    string DefaultMessage,
    long StreamOpenCount,
    long StreamChunkCount,
    long StreamCloseCount,
    long StreamErrorCount,
    long BytesRequested,
    long BytesDelivered,
    string ActiveModuleId,
    string ActiveModule,
    byte LastStreamId,
    string LastStreamResource,
    uint LastStreamOffset,
    int LastStreamLength,
    string LastStreamError,
    int ActiveStreamCount,
    long BulkFetchCount,
    long BulkSegmentCount,
    long BulkBytesDelivered);

public sealed class NetworkPakBridgeService : IBridgeNetworkPakModuleRuntime, IDisposable
{
    private const int MaxPayloadLength = 23;
    private const int StreamChunkSize = 16;
    private const int BulkLogicalMaximum = 512;
    private const int BulkInternalSegmentMaximum = 224;
    private const byte BulkBeginMarker = 0xB0;
    private const byte ResponseStreamOpened = 0xA0;
    private const byte ResponseStreamInfo = 0xA1;
    private const byte ResponseStreamChunk = 0xA2;
    private const byte ResponseStreamClosed = 0xA3;
    private const byte ResponseStreamWriteAck = 0xA4;
    private const byte ResponseError = 0xFF;
    private const int MaxGeneratedPageBytes = 15360;

    private sealed class StreamSession
    {
        public StreamSession(
            byte id,
            Guid connectionId,
            INetworkPakModule module,
            NetworkPakResource resource)
        {
            Id = id;
            ConnectionId = connectionId;
            Module = module;
            Resource = resource.Name;
            Data = resource.Data;
            Crc32 = resource.IsLive ? 0 : ComputeCrc32(resource.Data);
            CommitRead = resource.CommitRead;
            Live = resource.Live;
        }

        public byte Id { get; }
        public Guid ConnectionId { get; }
        public INetworkPakModule Module { get; }
        public string Resource { get; }
        public byte[] Data { get; }
        public uint Crc32 { get; }
        public Action<int>? CommitRead { get; }
        public INetworkPakLiveResource? Live { get; }
        public bool IsLive => Live is not null;
        public int BytesServed { get; set; }
        public uint LiveOffset { get; set; }
    }

    private sealed class StreamProfile
    {
        public StreamProfile(byte streamId, string resource, int totalBytes, int expectedChunks)
        {
            StreamId = streamId;
            Resource = resource;
            TotalBytes = totalBytes;
            ExpectedChunks = expectedChunks;
            OpenedTicks = Stopwatch.GetTimestamp();
        }

        public byte StreamId { get; }
        public string Resource { get; }
        public int TotalBytes { get; }
        public int ExpectedChunks { get; }
        public long OpenedTicks { get; }
        public long OpenProcessingTicks { get; set; }
        public long ChunkPreparationTicks { get; set; }
        public long ChunkLatencyTicks { get; set; }
        public int ChunkCount { get; set; }
        public int PayloadBytes { get; set; }
    }

    private sealed record PendingProfileEvent(
        byte Sequence,
        string Command,
        byte? StreamId,
        string Resource,
        int ChunkIndex,
        uint Offset,
        int PayloadBytes,
        long RequestReceivedTicks,
        long ResponseReadyTicks,
        long PreparationTicks);

    private readonly object _sync = new();
    private readonly Dictionary<byte, StreamSession> _streams = new();
    private readonly Dictionary<Guid, byte> _liveStreamsByConnection = new();
    private readonly ModuleRegistry _moduleRegistry;
    private readonly IBridgeHostStateProvider _deviceState;
    private readonly IBridgeSessionProvider? _sessionProvider;
    private static readonly Guid LegacyConnectionId =
        Guid.Parse("00000000-0000-0000-0000-000000000001");
    private bool _disposed;
    private bool _picoConnected;
    private bool _handshakeComplete;
    private long _requestCount;
    private long _responseCount;
    private long _errorCount;
    private string _lastRequest = "None";
    private string _lastResponse = "None";
    private string _lastError = "None";
    private string _defaultMessage = "hello from bridge";
    private byte _nextStreamId = 1;
    private long _streamOpenCount;
    private long _streamChunkCount;
    private long _streamCloseCount;
    private long _streamErrorCount;
    private long _bytesRequested;
    private long _bytesDelivered;
    private readonly Dictionary<byte, StreamProfile> _streamProfiles = new();
    private readonly Dictionary<byte, PendingProfileEvent> _pendingProfileEvents = new();
    private long _lastReadPreparationTicks;
    private byte _lastStreamId;
    private string _lastStreamResource = "None";
    private uint _lastStreamOffset;
    private int _lastStreamLength;
    private string _lastStreamError = "None";
    private long _bulkFetchCount;
    private long _bulkSegmentCount;
    private long _bulkBytesDelivered;

    internal Action? ActiveModuleChangedForTest { get; set; }

    public string DefaultModuleId => ModuleRegistry.DefaultModuleId;

    public NetworkPakBridgeService(
        string? activeModuleId = null,
        string? readableSourceMode = null,
        string? searchProviderName = null,
        string? searchProviderUrl = null,
        IBridgeHostStateProvider? deviceState = null,
        IBridgeSessionProvider? sessionProvider = null)
    {
        _deviceState = deviceState ?? BridgeDeviceStateProviders.Disconnected;
        _sessionProvider = sessionProvider;
        if (_sessionProvider is not null)
            _sessionProvider.CurrentChanged += SessionProvider_CurrentChanged;
        _moduleRegistry = new ModuleRegistry(activeModuleId, LogStreamEvent, readableSourceMode, searchProviderName, searchProviderUrl);
        LogStreamEvent($"Active module: {_moduleRegistry.ActiveModule.DisplayName} ({_moduleRegistry.ActiveModule.Id})");
        if (!string.IsNullOrWhiteSpace(activeModuleId) &&
            !string.Equals(activeModuleId, _moduleRegistry.ActiveModule.Id, StringComparison.OrdinalIgnoreCase))
        {
            LogStreamEvent($"Module fallback: requested={SanitizeLogValue(activeModuleId)} active={_moduleRegistry.ActiveModule.Id}");
        }
    }

    public IReadOnlyList<INetworkPakModule> ListModules()
    {
        return _moduleRegistry.ListModules();
    }

    public IReadOnlyList<BridgeNetworkPakModuleInfo> ListSelectableModules()
    {
        return _moduleRegistry.ListModules()
            .Select(module => new BridgeNetworkPakModuleInfo(module.Id, module.DisplayName))
            .ToArray();
    }

    public BridgeNetworkPakModuleRuntimeState CurrentModule
    {
        get
        {
            NetworkPakBridgeSnapshot snapshot = GetSnapshot();
            return new(snapshot.ActiveModuleId, snapshot.ActiveModule);
        }
    }

    public bool SetActiveModule(string? moduleId)
    {
        lock (_sync)
        {
            bool selected = _moduleRegistry.SetActiveModule(moduleId);
            LogStreamEvent(selected
                ? $"Module switch: active={_moduleRegistry.ActiveModule.DisplayName} ({_moduleRegistry.ActiveModule.Id})"
                : $"Module switch fallback: requested={SanitizeLogValue(moduleId ?? string.Empty)} active={_moduleRegistry.ActiveModule.DisplayName} ({_moduleRegistry.ActiveModule.Id})");
            ActiveModuleChangedForTest?.Invoke();
            CloseAllStreams("MODULE_SWITCH");
            return selected;
        }
    }

    public void SetPicoConnected(bool connected)
    {
        lock (_sync)
        {
            _picoConnected = connected;
            if (!connected)
            {
                _handshakeComplete = false;
                CloseAllStreams("CARRIER_DISCONNECTED");
            }
        }
    }

    public void SetHandshakeComplete(bool complete)
    {
        lock (_sync)
        {
            _handshakeComplete = complete;
            _lastError = complete ? "None" : _lastError;
        }
    }

    public void SetDefaultMessage(string message)
    {
        lock (_sync)
        {
            _defaultMessage = SanitizePayload(message);
        }
    }

    public bool TryHandleRequest(string line, out string responseLine)
    {
        return TryHandleRequest(ResolveConnectionId(), line, out responseLine);
    }

    public bool TryHandleRequest(
        Guid connectionId,
        string line,
        out string responseLine)
    {
        if (line.StartsWith("NETPAK_REQ_HEX|", StringComparison.Ordinal))
        {
            return TryHandleStreamRequest(connectionId, line, out responseLine);
        }

        responseLine = string.Empty;
        if (!line.StartsWith("NETPAK_REQ|", StringComparison.Ordinal))
        {
            return false;
        }

        string[] parts = line.Split('|', 4);
        if (parts.Length < 3 || !byte.TryParse(parts[1], out byte sequence))
        {
            RecordError("Malformed NETPAK_REQ");
            return true;
        }

        string command = parts[2];
        string payload = parts.Length == 4 ? parts[3] : string.Empty;
        string responsePayload;
        bool ok = true;

        lock (_sync)
        {
            _requestCount++;
            _lastRequest = $"{command} #{sequence}: {payload}";

            responsePayload = command switch
            {
                "PING_BRIDGE" => "BRIDGE_PONG",
                "GET_BRIDGE_STATUS" => "BRIDGE_READY",
                "GET_BRIDGE_TIME" => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                "GET_BRIDGE_MESSAGE" => _defaultMessage,
                "SEND_BRIDGE_MESSAGE" => "MESSAGE_RECEIVED",
                _ => "UNSUPPORTED_BRIDGE_CMD"
            };

            if (command == "SEND_BRIDGE_MESSAGE")
            {
                _lastRequest = $"{command} #{sequence}: {SanitizePayload(payload)}";
            }
            else if (command is not ("PING_BRIDGE" or "GET_BRIDGE_STATUS" or "GET_BRIDGE_TIME" or "GET_BRIDGE_MESSAGE"))
            {
                ok = false;
                _errorCount++;
                _lastError = responsePayload;
            }

            responsePayload = SanitizePayload(responsePayload);
            responseLine = $"NETPAK_RSP|{sequence}|{(ok ? "OK" : "ERR")}|{responsePayload}";
            _responseCount++;
            _lastResponse = $"#{sequence}: {responsePayload}";
            if (ok) _lastError = "None";
        }

        return true;
    }

    public void MarkNetworkPakResponseWritten(string responseLine)
    {
        if (!responseLine.StartsWith("NETPAK_RSP_HEX|", StringComparison.Ordinal))
        {
            return;
        }

        string[] parts = responseLine.Split('|', 5);
        if (parts.Length < 5 || !byte.TryParse(parts[1], out byte sequence))
        {
            return;
        }

        lock (_sync)
        {
            if (!_pendingProfileEvents.Remove(sequence, out PendingProfileEvent? profileEvent))
            {
                return;
            }

            long responseWrittenTicks = Stopwatch.GetTimestamp();
            long latencyTicks = responseWrittenTicks - profileEvent.RequestReceivedTicks;
            long readyTicks = profileEvent.ResponseReadyTicks - profileEvent.RequestReceivedTicks;

            if (profileEvent.Command == "OPEN_STREAM" &&
                profileEvent.StreamId.HasValue &&
                _streamProfiles.TryGetValue(profileEvent.StreamId.Value, out StreamProfile? openProfile))
            {
                openProfile.OpenProcessingTicks = readyTicks;
                LogStreamEvent(
                    $"PROFILE OPEN resource={profileEvent.Resource} stream={profileEvent.StreamId.Value} " +
                    $"requestUtc={TicksToUtc(profileEvent.RequestReceivedTicks):O} readyUtc={TicksToUtc(profileEvent.ResponseReadyTicks):O} " +
                    $"writtenUtc={TicksToUtc(responseWrittenTicks):O} bridgeReadyUs={TicksToMicroseconds(readyTicks)} " +
                    $"bridgeLatencyUs={TicksToMicroseconds(latencyTicks)}");
                return;
            }

            if (profileEvent.Command != "READ_STREAM_CHUNK" || !profileEvent.StreamId.HasValue)
            {
                return;
            }

            if (!_streamProfiles.TryGetValue(profileEvent.StreamId.Value, out StreamProfile? profile))
            {
                return;
            }

            profile.ChunkPreparationTicks += profileEvent.PreparationTicks;
            profile.ChunkLatencyTicks += latencyTicks;
            profile.ChunkCount++;
            profile.PayloadBytes += profileEvent.PayloadBytes;

            LogStreamEvent(
                $"PROFILE CHUNK resource={profile.Resource} stream={profile.StreamId} index={profileEvent.ChunkIndex} " +
                $"offset={profileEvent.Offset} bytes={profileEvent.PayloadBytes} " +
                $"requestUtc={TicksToUtc(profileEvent.RequestReceivedTicks):O} readyUtc={TicksToUtc(profileEvent.ResponseReadyTicks):O} " +
                $"writtenUtc={TicksToUtc(responseWrittenTicks):O} prepUs={TicksToMicroseconds(profileEvent.PreparationTicks)} " +
                $"bridgeReadyUs={TicksToMicroseconds(readyTicks)} bridgeLatencyUs={TicksToMicroseconds(latencyTicks)}");
        }
    }

    private bool TryHandleStreamRequest(
        Guid connectionId,
        string line,
        out string responseLine)
    {
        responseLine = string.Empty;
        long requestReceivedTicks = Stopwatch.GetTimestamp();
        string[] parts = line.Split('|', 4);
        if (parts.Length < 2 || !byte.TryParse(parts[1], out byte sequence))
        {
            RecordError("Malformed NETPAK_REQ_HEX");
            return true;
        }
        if (parts.Length != 4)
        {
            RecordError("Malformed NETPAK_REQ_HEX");
            responseLine = MakeHexError(sequence, "BAD_STREAM_REQUEST");
            return true;
        }
        if (connectionId == Guid.Empty)
        {
            responseLine = MakeHexError(sequence, "NO_BRIDGE_SESSION");
            RecordError("No GUID-backed Bridge session for Network Pak request");
            return true;
        }

        byte[] requestPayload;
        try
        {
            requestPayload = Convert.FromHexString(parts[3]);
        }
        catch (FormatException)
        {
            RecordError("Malformed stream hex");
            responseLine = MakeHexError(sequence, "BRIDGE_BAD_HEX");
            return true;
        }

        lock (_sync)
        {
            _requestCount++;
            _lastRequest = $"{parts[2]} #{sequence}: {Convert.ToHexString(requestPayload)}";

            if (parts[2] == "GET_APPLICATION_CAPABILITIES")
            {
                if (!requestPayload.AsSpan().SequenceEqual("BCAP"u8))
                {
                    responseLine = MakeHexError(sequence, "BAD_CAPS_REQUEST");
                    _responseCount++;
                    _errorCount++;
                    _lastError = "BAD_CAPS_REQUEST";
                    return true;
                }
                byte[] capabilities =
                [
                    (byte)'B', (byte)'C', 1, 1,
                    (byte)(BulkLogicalMaximum >> 8), (byte)(BulkLogicalMaximum & 0xFF),
                    StreamChunkSize, BulkInternalSegmentMaximum
                ];
                responseLine = $"NETPAK_RSP_HEX|{sequence}|OK|{ResponseStreamInfo:X2}|" +
                    Convert.ToHexString(capabilities);
                _responseCount++;
                _lastResponse = $"#{sequence}: BULK_READ_V1";
                _lastError = "None";
                if (_deviceState is IBridgeDeviceStateUpdater updater)
                {
                    updater.SetApplicationCapabilities(new(
                        BridgeCapabilityProvenance.ApplicationNegotiated,
                    [
                        new(BridgeCapabilityId.LegacyNetworkPakStream, true,
                            BridgeCapabilityProvenance.ApplicationNegotiated),
                        new(BridgeCapabilityId.BulkRead, true,
                            BridgeCapabilityProvenance.ApplicationNegotiated)
                    ]));
                }
                return true;
            }

            if (parts[2] is "BULK_READ" or "BULK_READ_SEGMENT")
            {
                (bool bulkOk, string bulkResponse, int bytes) = MakeBulkSegment(
                    connectionId, sequence, requestPayload,
                    parts[2] == "BULK_READ_SEGMENT");
                responseLine = bulkResponse;
                _responseCount++;
                _lastResponse = bulkOk
                    ? $"#{sequence}: BULK_SEG LEN={bytes}"
                    : $"#{sequence}: BULK_ERROR";
                if (bulkOk)
                {
                    _lastError = "None";
                }
                else
                {
                    _errorCount++;
                }
                return true;
            }

            (bool ok, byte responseCommand, byte[] payload) result = parts[2] switch
            {
                "OPEN_STREAM" => OpenStream(connectionId, requestPayload),
                "GET_STREAM_INFO" => GetStreamInfo(connectionId, requestPayload),
                "READ_STREAM_CHUNK" => ReadStreamChunk(connectionId, requestPayload),
                "WRITE_STREAM_CHUNK" => WriteStreamChunk(connectionId, requestPayload),
                "CLOSE_STREAM" => CloseStream(connectionId, requestPayload),
                _ => StreamError("UNSUPPORTED_STREAM_CMD")
            };

            long responseReadyTicks = Stopwatch.GetTimestamp();
            responseLine = $"NETPAK_RSP_HEX|{sequence}|{(result.ok ? "OK" : "ERR")}|{result.responseCommand:X2}|{Convert.ToHexString(result.payload)}";
            RecordPendingProfileEvent(sequence, parts[2], requestPayload, result, requestReceivedTicks, responseReadyTicks);
            _responseCount++;
            _lastResponse = $"#{sequence}: CMD={result.responseCommand:X2} LEN={result.payload.Length}";
            if (result.ok)
            {
                _lastError = "None";
            }
            else
            {
                _errorCount++;
                _lastError = Encoding.ASCII.GetString(result.payload);
            }
        }

        return true;
    }

    private static string MakeHexError(byte sequence, string error)
    {
        return $"NETPAK_RSP_HEX|{sequence}|ERR|FF|{Convert.ToHexString(Encoding.ASCII.GetBytes(error))}";
    }

    private (bool, byte, byte[]) OpenStream(Guid connectionId, byte[] request)
    {
        long openStartTicks = Stopwatch.GetTimestamp();
        string resource = Encoding.ASCII.GetString(request).TrimEnd('\0');
        LogStreamEvent($"OPEN_STREAM {resource}");
        (INetworkPakModule Module, NetworkPakResource Resource)? opened =
            OpenActiveModuleResource(connectionId, resource);
        if (opened is null)
        {
            LogStreamEvent($"ERROR {resource} STREAM_NOT_FOUND");
            return StreamError("STREAM_NOT_FOUND");
        }
        NetworkPakResource moduleResource = opened.Value.Resource;
        if (_streams.Count >= 16)
        {
            moduleResource.Live?.Close("STREAM_LIMIT");
            return StreamError("STREAM_LIMIT");
        }
        if (moduleResource.IsLive && _liveStreamsByConnection.ContainsKey(connectionId))
        {
            moduleResource.Live!.Close("LIVE_STREAM_EXISTS");
            return StreamError("LIVE_STREAM_EXISTS");
        }

        byte id = AllocateStreamId();
        StreamSession session = new(id, connectionId, opened.Value.Module, moduleResource);
        _streams.Add(id, session);
        if (session.IsLive) _liveStreamsByConnection.Add(connectionId, id);
        int chunks = (session.Data.Length + StreamChunkSize - 1) / StreamChunkSize;
        _streamProfiles[id] = new StreamProfile(id, session.Resource, session.Data.Length, chunks);
        _streamOpenCount++;
        UpdateLastStream(session, 0, "None");
        LogStreamEvent($"StreamId: {id}");
        LogStreamEvent($"OPEN_STREAM metadata returned id={id} resource={session.Resource} length={session.Data.Length} chunkSize={StreamChunkSize} crc32={session.Crc32:X8}");
        LogStreamEvent($"PROFILE OPEN_READY resource={session.Resource} stream={id} bytes={session.Data.Length} chunks={chunks} prepUs={TicksToMicroseconds(Stopwatch.GetTimestamp() - openStartTicks)}");
        return (true, ResponseStreamOpened, MakeStreamMetadata(session));
    }

    private (bool, byte, byte[]) GetStreamInfo(Guid connectionId, byte[] request)
    {
        if (request.Length != 1 || !_streams.TryGetValue(request[0], out StreamSession? session))
            return StreamError("BAD_STREAM_ID");
        if (session.IsLive && session.ConnectionId != connectionId)
            return StreamError("BAD_STREAM_ID");
        UpdateLastStream(session, 0, "None");
        return (true, ResponseStreamInfo, MakeStreamMetadata(session));
    }

    private (bool, byte, byte[]) ReadStreamChunk(Guid connectionId, byte[] request)
    {
        long preparationStartTicks = Stopwatch.GetTimestamp();
        if (request.Length != 5 || !_streams.TryGetValue(request[0], out StreamSession? session))
            return StreamError("BAD_STREAM_ID");
        if (session.IsLive && session.ConnectionId != connectionId)
            return StreamError("BAD_STREAM_ID");

        uint offset = BinaryPrimitives.ReadUInt32BigEndian(request.AsSpan(1, 4));
        if (session.IsLive)
            return ReadLiveStreamChunk(session, offset, preparationStartTicks);
        if (offset > session.Data.Length) return StreamError("BAD_STREAM_OFFSET");

        int count = Math.Min(StreamChunkSize, session.Data.Length - (int)offset);
        byte[] payload = new byte[7 + count];
        payload[0] = session.Id;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(1, 4), offset);
        payload[5] = (byte)count;
        payload[6] = (byte)(offset + count >= session.Data.Length ? 1 : 0);
        session.Data.AsSpan((int)offset, count).CopyTo(payload.AsSpan(7));
        session.BytesServed = Math.Max(session.BytesServed, (int)offset + count);

        _streamChunkCount++;
        _bytesRequested += StreamChunkSize;
        _bytesDelivered += count;
        UpdateLastStream(session, offset, "None");
        int chunkIndex = (int)(offset / StreamChunkSize);
        long preparationTicks = Stopwatch.GetTimestamp() - preparationStartTicks;
        _lastReadPreparationTicks = preparationTicks;
        if (offset == 0)
        {
            LogStreamEvent($"READ_STREAM_CHUNK first id={session.Id} resource={session.Resource} total={session.Data.Length}");
        }
        if (offset + count >= session.Data.Length)
        {
            LogStreamEvent($"READ_STREAM_CHUNK last id={session.Id} resource={session.Resource} finalOffset={offset} finalBytes={count}");
        }
        LogStreamEvent($"READ_STREAM_CHUNK id={session.Id} resource={session.Resource} index={chunkIndex} offset={offset} bytes={count} prepUs={TicksToMicroseconds(preparationTicks)}");
        return (true, ResponseStreamChunk, payload);
    }

    private (bool, byte, byte[]) ReadLiveStreamChunk(
        StreamSession session,
        uint offset,
        long preparationStartTicks)
    {
        if (offset != session.LiveOffset)
            return StreamError("BAD_STREAM_OFFSET");

        NetworkPakLiveReadResult result = session.Live!.Read(StreamChunkSize);
        if (result.Status == NetworkPakLiveReadStatus.Error)
            return StreamError(result.Error ?? "LIVE_READ_FAILED");

        byte[] data = result.Data;
        if (result.Status == NetworkPakLiveReadStatus.Data &&
            (data.Length is < 1 or > StreamChunkSize))
            return StreamError("BAD_LIVE_READ");
        if (result.Status != NetworkPakLiveReadStatus.Data && data.Length != 0)
            return StreamError("BAD_LIVE_READ");

        int count = data.Length;
        bool eof = result.Status == NetworkPakLiveReadStatus.Eof;
        byte[] payload = new byte[7 + count];
        payload[0] = session.Id;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(1, 4), offset);
        payload[5] = (byte)count;
        payload[6] = eof ? (byte)1 : (byte)0;
        data.CopyTo(payload, 7);

        if (count > 0)
        {
            session.LiveOffset = checked(session.LiveOffset + (uint)count);
            session.BytesServed = checked(session.BytesServed + count);
        }
        _streamChunkCount++;
        _bytesRequested += StreamChunkSize;
        _bytesDelivered += count;
        UpdateLastStream(session, offset, "None");
        long preparationTicks = Stopwatch.GetTimestamp() - preparationStartTicks;
        _lastReadPreparationTicks = preparationTicks;
        if (count > 0 || eof)
        {
            LogStreamEvent($"READ_STREAM_CHUNK live id={session.Id} " +
                $"resource={session.Resource} offset={offset} bytes={count} eos={(eof ? 1 : 0)}");
        }
        return (true, ResponseStreamChunk, payload);
    }

    private (bool Ok, string Response, int Bytes) MakeBulkSegment(
        Guid connectionId, byte sequence, byte[] request, bool continuation)
    {
        int expectedLength = continuation ? 12 : 10;
        if (request.Length != expectedLength || request[0] != BulkBeginMarker)
            return (false, MakeHexError(sequence, "BAD_BULK_REQUEST"), 0);

        byte streamId = request[1];
        ushort generation = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(2, 2));
        uint baseOffset = BinaryPrimitives.ReadUInt32BigEndian(request.AsSpan(4, 4));
        ushort totalLength = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(8, 2));
        ushort segmentOffset = continuation
            ? BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(10, 2))
            : (ushort)0;

        if (generation == 0 || totalLength is 0 or > BulkLogicalMaximum ||
            !_streams.TryGetValue(streamId, out StreamSession? session) ||
            session.IsLive ||
            baseOffset > session.Data.Length ||
            (ulong)baseOffset + totalLength > (ulong)session.Data.Length ||
            segmentOffset >= totalLength)
            return (false, MakeHexError(sequence, "BAD_BULK_RANGE"), 0);

        int count = Math.Min(BulkInternalSegmentMaximum, totalLength - segmentOffset);
        ReadOnlySpan<byte> block = session.Data.AsSpan((int)baseOffset, totalLength);
        ReadOnlySpan<byte> segment = block.Slice(segmentOffset, count);
        uint crc = ComputeCrc32(block);
        byte flags = (ulong)baseOffset + totalLength == (ulong)session.Data.Length ? (byte)1 : (byte)0;
        string response = $"NETPAK_BULK_SEG|{sequence}|{generation:X4}|{streamId:X2}|" +
            $"{baseOffset:X8}|{totalLength:X4}|{segmentOffset:X4}|{count:X2}|" +
            $"{crc:X8}|{flags:X2}|{Convert.ToHexString(segment)}";
        if (response.Length >= 600)
            return (false, MakeHexError(sequence, "BULK_FRAME_LONG"), 0);

        if (!continuation)
        {
            _bulkFetchCount++;
            _bytesRequested += totalLength;
        }
        _bulkSegmentCount++;
        _bulkBytesDelivered += count;
        _bytesDelivered += count;
        UpdateLastStream(session, baseOffset + segmentOffset, "None");
        LogStreamEvent(
            $"BULK_READ stream={streamId} generation={generation} offset={baseOffset} " +
            $"total={totalLength} segment={segmentOffset} bytes={count} crc32={crc:X8}");
        return (true, response, count);
    }

    private (bool, byte, byte[]) CloseStream(Guid connectionId, byte[] request)
    {
        if (request.Length != 1 || !_streams.TryGetValue(request[0], out StreamSession? session))
            return StreamError("BAD_STREAM_ID");
        if (session.IsLive && session.ConnectionId != connectionId)
            return StreamError("BAD_STREAM_ID");
        _streams.Remove(request[0]);
        _streamCloseCount++;
        UpdateLastStream(session, session.IsLive ? session.LiveOffset : (uint)session.Data.Length, "None");
        LogStreamEvent($"CLOSE_STREAM id={session.Id} resource={session.Resource}");
        CloseSessionResource(session, "CLOSE_STREAM");
        LogStreamProfileSummary(session);
        _streamProfiles.Remove(session.Id);
        return (true, ResponseStreamClosed, new[] { session.Id });
    }

    private (bool, byte, byte[]) WriteStreamChunk(Guid connectionId, byte[] request)
    {
        if (request.Length < 2)
        {
            return StreamError("BAD_WRITE_CHUNK");
        }

        byte flags = request[0];
        int count = request[1];
        if (count < 0 || count > request.Length - 2)
        {
            return StreamError("BAD_WRITE_LENGTH");
        }

        byte[] data = request.AsSpan(2, count).ToArray();
        if (_liveStreamsByConnection.TryGetValue(connectionId, out byte liveStreamId))
        {
            if (!_streams.TryGetValue(liveStreamId, out StreamSession? liveSession) ||
                liveSession.Live is null)
            {
                _liveStreamsByConnection.Remove(connectionId);
                return StreamError("LIVE_STREAM_CLOSED");
            }
            NetworkPakWriteResult liveResult = liveSession.Live.Write(
                new NetworkPakWriteChunk(flags, data),
                CreateModuleContext(connectionId));
            return liveResult.Success
                ? (true, ResponseStreamWriteAck, liveResult.Payload)
                : StreamError(liveResult.Error ?? "WRITE_FAILED");
        }

        NetworkPakWriteResult result = _moduleRegistry.ActiveModule.HandleWrite(
            new NetworkPakWriteChunk(flags, data),
            CreateModuleContext(connectionId));
        return result.Success
            ? (true, ResponseStreamWriteAck, result.Payload)
            : StreamError(result.Error ?? "WRITE_FAILED");
    }

    private (INetworkPakModule Module, NetworkPakResource Resource)?
        OpenActiveModuleResource(Guid connectionId, string resource)
    {
        LogStreamEvent($"Module route: {_moduleRegistry.ActiveModule.DisplayName} resource={resource}");
        ModuleContext context = CreateModuleContext(connectionId);
        INetworkPakModule activeModule = _moduleRegistry.ActiveModule;
        NetworkPakResource? result = activeModule.OpenResource(resource, context);
        if (result is not null)
        {
            LogStreamEvent($"Module served: {_moduleRegistry.ActiveModule.DisplayName} resource={resource}");
            return (activeModule, result);
        }

        INetworkPakModule? fallbackModule = _moduleRegistry.FindModuleForResource(resource);
        if (fallbackModule is null)
        {
            return null;
        }

        LogStreamEvent($"Module fallback route: {fallbackModule.DisplayName} resource={resource}");
        result = fallbackModule.OpenResource(resource, context);
        if (result is not null)
        {
            LogStreamEvent($"Module served: {fallbackModule.DisplayName} resource={resource}");
        }

        return result is null ? null : (fallbackModule, result);
    }

    private void RecordPendingProfileEvent(
        byte sequence,
        string command,
        byte[] requestPayload,
        (bool ok, byte responseCommand, byte[] payload) result,
        long requestReceivedTicks,
        long responseReadyTicks)
    {
        if (!result.ok)
        {
            return;
        }

        if (command == "OPEN_STREAM" && result.responseCommand == ResponseStreamOpened && result.payload.Length == 10)
        {
            byte streamId = result.payload[0];
            string resource = _streams.TryGetValue(streamId, out StreamSession? session)
                ? session.Resource
                : Encoding.ASCII.GetString(requestPayload).TrimEnd('\0');
            _pendingProfileEvents[sequence] = new PendingProfileEvent(
                sequence, command, streamId, resource, 0, 0, 0, requestReceivedTicks, responseReadyTicks,
                responseReadyTicks - requestReceivedTicks);
            return;
        }

        if (command == "READ_STREAM_CHUNK" &&
            result.responseCommand == ResponseStreamChunk &&
            requestPayload.Length == 5 &&
            result.payload.Length >= 7)
        {
            byte streamId = result.payload[0];
            uint offset = BinaryPrimitives.ReadUInt32BigEndian(result.payload.AsSpan(1, 4));
            int count = result.payload[5];
            string resource = _streams.TryGetValue(streamId, out StreamSession? session)
                ? session.Resource
                : "Unknown";
            int chunkIndex = (int)(offset / StreamChunkSize);
            _pendingProfileEvents[sequence] = new PendingProfileEvent(
                sequence, command, streamId, resource, chunkIndex, offset, count, requestReceivedTicks, responseReadyTicks,
                _lastReadPreparationTicks);
        }
    }

    private void LogStreamProfileSummary(StreamSession session)
    {
        if (!_streamProfiles.TryGetValue(session.Id, out StreamProfile? profile))
        {
            return;
        }

        long bridgeProcessingTicks = profile.OpenProcessingTicks + profile.ChunkLatencyTicks;
        long averagePreparationUs = profile.ChunkCount > 0
            ? TicksToMicroseconds(profile.ChunkPreparationTicks) / profile.ChunkCount
            : 0;
        long averageLatencyUs = profile.ChunkCount > 0
            ? TicksToMicroseconds(profile.ChunkLatencyTicks) / profile.ChunkCount
            : 0;
        double bridgeProcessingMs = TicksToMillisecondsDouble(bridgeProcessingTicks);

        LogStreamEvent(
            $"PROFILE SUMMARY resource={profile.Resource} bytes={profile.PayloadBytes}/{profile.TotalBytes} " +
            $"chunks={profile.ChunkCount}/{profile.ExpectedChunks} bridgeProcessingMs={bridgeProcessingMs:F3} " +
            $"avgPreparationUs={averagePreparationUs} avgChunkLatencyUs={averageLatencyUs}");
    }

    private ModuleContext CreateModuleContext(Guid connectionId)
    {
        return new ModuleContext(
            AppContext.BaseDirectory,
            LogStreamEvent,
            BridgeConstants.BridgeVersion,
            MaxPayloadLength,
            MaxGeneratedPageBytes,
            _defaultMessage)
        {
            DeviceState = _deviceState,
            Capabilities = _deviceState,
            ConnectionId = connectionId
        };
    }

    internal ModuleContext CreateModuleContextForTesting() =>
        CreateModuleContext(LegacyConnectionId);

    private (bool, byte, byte[]) StreamError(string error)
    {
        string clean = SanitizePayload(error);
        _streamErrorCount++;
        _lastStreamError = clean;
        LogStreamEvent($"ERROR {clean}");
        return (false, ResponseError, Encoding.ASCII.GetBytes(clean));
    }

    private byte AllocateStreamId()
    {
        for (int i = 0; i < byte.MaxValue; i++)
        {
            byte candidate = _nextStreamId++;
            if (_nextStreamId == 0) _nextStreamId = 1;
            if (!_streams.ContainsKey(candidate)) return candidate;
        }
        throw new InvalidOperationException("No stream ID available.");
    }

    private static byte[] MakeStreamMetadata(StreamSession session)
    {
        byte[] payload = new byte[10];
        payload[0] = session.Id;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(1, 4), (uint)session.Data.Length);
        payload[5] = StreamChunkSize;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(6, 4), session.Crc32);
        return payload;
    }

    private void UpdateLastStream(StreamSession session, uint offset, string error)
    {
        _lastStreamId = session.Id;
        _lastStreamResource = session.Resource;
        _lastStreamOffset = offset;
        _lastStreamLength = session.Data.Length;
        _lastStreamError = error;
    }

    public NetworkPakBridgeSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return new NetworkPakBridgeSnapshot(
                _picoConnected, _handshakeComplete, _requestCount, _responseCount, _errorCount,
                _lastRequest, _lastResponse, _lastError, _defaultMessage,
                _streamOpenCount, _streamChunkCount, _streamCloseCount, _streamErrorCount,
                _bytesRequested, _bytesDelivered, _moduleRegistry.ActiveModule.Id, _moduleRegistry.ActiveModule.DisplayName, _lastStreamId, _lastStreamResource,
                _lastStreamOffset, _lastStreamLength, _lastStreamError, _streams.Count,
                _bulkFetchCount, _bulkSegmentCount, _bulkBytesDelivered);
        }
    }

    internal void DisconnectConnectionForTesting(Guid connectionId)
    {
        lock (_sync) CloseConnectionStreams(connectionId, "CONNECTION_DISCONNECTED");
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            CloseAllStreams("BRIDGE_SHUTDOWN");
        }
        if (_sessionProvider is not null)
            _sessionProvider.CurrentChanged -= SessionProvider_CurrentChanged;
    }

    private Guid ResolveConnectionId()
    {
        if (_sessionProvider is null) return LegacyConnectionId;
        return Guid.TryParse(_sessionProvider.Current?.SessionId, out Guid id) &&
            id != Guid.Empty
                ? id
                : Guid.Empty;
    }

    private void SessionProvider_CurrentChanged(
        object? sender,
        BridgeSessionChangedEventArgs e)
    {
        if (!Guid.TryParse(e.Previous?.SessionId, out Guid previous) ||
            previous == Guid.Empty)
            return;
        if (Guid.TryParse(e.Current?.SessionId, out Guid current) &&
            current == previous)
            return;
        lock (_sync) CloseConnectionStreams(previous, "CONNECTION_DISCONNECTED");
    }

    private void CloseConnectionStreams(Guid connectionId, string reason)
    {
        byte[] ids = _streams.Values
            .Where(session => session.ConnectionId == connectionId)
            .Select(session => session.Id)
            .ToArray();
        foreach (byte id in ids)
        {
            StreamSession session = _streams[id];
            _streams.Remove(id);
            CloseSessionResource(session, reason, commitFiniteRead: false);
            _streamProfiles.Remove(id);
        }
    }

    private void CloseAllStreams(string reason)
    {
        foreach (StreamSession session in _streams.Values)
            CloseSessionResource(session, reason, commitFiniteRead: false);
        _streams.Clear();
        _liveStreamsByConnection.Clear();
        _streamProfiles.Clear();
        _pendingProfileEvents.Clear();
    }

    private void CloseSessionResource(
        StreamSession session,
        string reason,
        bool commitFiniteRead = true)
    {
        if (session.IsLive)
        {
            if (_liveStreamsByConnection.TryGetValue(session.ConnectionId, out byte id) &&
                id == session.Id)
                _liveStreamsByConnection.Remove(session.ConnectionId);
            session.Live!.Close(reason);
        }
        else if (commitFiniteRead)
        {
            session.CommitRead?.Invoke(session.BytesServed);
        }
    }

    private void RecordError(string error)
    {
        lock (_sync)
        {
            _errorCount++;
            _lastError = error;
        }
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }

    private static string SanitizePayload(string value)
    {
        string clean = value.Replace('|', '_').Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length <= MaxPayloadLength ? clean : clean[..MaxPayloadLength];
    }

    private static string SanitizeLogValue(string value)
    {
        string clean = value.Replace('|', '_').Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length <= 180 ? clean : clean[..180];
    }

    private static void LogStreamEvent(string message)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "networkpak-bridge.log");
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never block serial request handling.
        }
    }

    private static long TicksToMicroseconds(long ticks)
    {
        return (long)(ticks * 1_000_000.0 / Stopwatch.Frequency);
    }

    private static double TicksToMillisecondsDouble(long ticks)
    {
        return ticks * 1000.0 / Stopwatch.Frequency;
    }

    private static DateTimeOffset TicksToUtc(long ticks)
    {
        long currentTicks = Stopwatch.GetTimestamp();
        double elapsedSeconds = (ticks - currentTicks) / (double)Stopwatch.Frequency;
        return DateTimeOffset.UtcNow.AddSeconds(elapsedSeconds);
    }
}
