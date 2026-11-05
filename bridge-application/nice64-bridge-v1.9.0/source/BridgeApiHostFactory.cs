namespace PicoSwitchController;

internal sealed class BridgeApiHostFactory : IBridgeApiHostFactory
{
    private readonly IBridgeStateProvider _state;
    private readonly IBridgeApiTokenProvider _tokens;
    private readonly IBridgeApiCommandFacade? _commands;
    private readonly IBridgeApiV1HistoryProvider? _history;
    private readonly IBridgeSessionProvider? _sessions;
    private readonly IBridgeApiV1ControllerFacade? _controller;
    private readonly IBridgeApiV1HistoryActivationFacade? _historyActivation;
    private readonly IBridgeKeyboardMouseStateFacade? _keyboardMouse;
    private readonly Action<string>? _log;

    public BridgeApiHostFactory(
        IBridgeStateProvider state,
        IBridgeApiTokenProvider tokens,
        IBridgeApiCommandFacade? commands = null,
        Action<string>? log = null,
        IBridgeApiV1HistoryProvider? history = null,
        IBridgeSessionProvider? sessions = null,
        IBridgeApiV1ControllerFacade? controller = null,
        IBridgeApiV1HistoryActivationFacade? historyActivation = null,
        IBridgeKeyboardMouseStateFacade? keyboardMouse = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _commands = commands;
        _log = log;
        _history = history;
        _sessions = sessions;
        _controller = controller;
        _historyActivation = historyActivation;
        _keyboardMouse = keyboardMouse;
    }

    public IBridgeApiHost Create(BridgeApiResolvedHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        BridgeApiHostOptions hostOptions = new()
        {
            Enabled = true,
            Port = options.Port,
            ShutdownTimeout = options.ShutdownTimeout,
            Listeners = options.Listeners
        };
        return new BridgeApiHost(_state, _tokens, hostOptions,
            _commands, _log, _history, _sessions, _controller,
            _historyActivation, _keyboardMouse);
    }
}
