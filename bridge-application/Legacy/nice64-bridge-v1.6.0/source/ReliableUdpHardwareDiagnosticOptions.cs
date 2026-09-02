namespace PicoSwitchController;

public sealed record Nice64RudpcHardwareDiagnosticOptions(
    string? Target,
    int Port,
    bool Quick,
    int FlightCap,
    int? SelectedRto,
    bool TraceEnabled,
    bool CharacterizeRto,
    int Samples,
    bool AcceptRudpc,
    int Runs,
    bool CloseoutRudpc)
{
    public IReadOnlyList<int> BaselineRtos =>
        SelectedRto is int selected ? [selected] : [10, 20, 40];

    public int ImpairmentRto => SelectedRto ?? 20;
    public bool StopAfterPausedConsumer => Quick && SelectedRto is not null;

    public static Nice64RudpcHardwareDiagnosticOptions Parse(string[] args)
    {
        string? target = null;
        int port = Nice64RudpcSyntheticClient.DefaultPort;
        bool quick = false;
        int flightCap = 2;
        int? selectedRto = null;
        bool traceEnabled = false;
        bool characterizeRto = false;
        int samples = 10;
        bool acceptRudpc = false;
        int runs = 5;
        bool closeoutRudpc = false;
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index] == "--target" && index + 1 < args.Length)
                target = args[++index];
            else if (args[index] == "--port" && index + 1 < args.Length &&
                     int.TryParse(args[++index], out int parsedPort))
                port = parsedPort;
            else if (args[index] == "--quick")
                quick = true;
            else if (args[index] == "--flight-cap" && index + 1 < args.Length &&
                     int.TryParse(args[++index], out int parsedCap))
                flightCap = parsedCap;
            else if (args[index] == "--rto" && index + 1 < args.Length &&
                     int.TryParse(args[++index], out int parsedRto))
                selectedRto = parsedRto;
            else if (args[index] == "--trace")
                traceEnabled = true;
            else if (args[index] == "--characterize-rto")
                characterizeRto = true;
            else if (args[index] == "--samples" && index + 1 < args.Length &&
                     int.TryParse(args[++index], out int parsedSamples))
                samples = parsedSamples;
            else if (args[index] == "--accept-rudpc")
                acceptRudpc = true;
            else if (args[index] == "--runs" && index + 1 < args.Length &&
                     int.TryParse(args[++index], out int parsedRuns))
                runs = parsedRuns;
            else if (args[index] == "--closeout-rudpc")
                closeoutRudpc = true;
            else
                throw new ArgumentException($"Unknown, incomplete, or non-integer argument: {args[index]}");
        }

        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be 1 through 65535.");
        if (flightCap is not (1 or 2 or 4 or 8))
            throw new ArgumentOutOfRangeException(nameof(flightCap), "Flight cap must be 1, 2, 4, or 8.");
        if (selectedRto is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(selectedRto), "RTO must be 1 through 1000 ms.");
        if (samples is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(samples), "Samples must be 1 through 100.");
        if (runs is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(runs), "Runs must be 1 through 100.");
        if (acceptRudpc && (quick || characterizeRto))
            throw new ArgumentException("--accept-rudpc conflicts with --quick and --characterize-rto.");
        if (closeoutRudpc && (quick || characterizeRto || acceptRudpc))
            throw new ArgumentException("--closeout-rudpc conflicts with other diagnostic modes.");
        if (characterizeRto && quick)
            throw new ArgumentException("--characterize-rto cannot be combined with --quick.");
        if (characterizeRto && selectedRto is null)
            throw new ArgumentException("--characterize-rto requires one explicit --rto value.");
        if (characterizeRto && flightCap != 2)
            throw new ArgumentException("RTO characterization is intentionally fixed at flight cap 2.");
        if (acceptRudpc && selectedRto is null)
            throw new ArgumentException("--accept-rudpc requires one explicit --rto value.");
        if (acceptRudpc && flightCap != 2)
            throw new ArgumentException("RUDP-C acceptance is intentionally fixed at flight cap 2.");
        if (closeoutRudpc && selectedRto != 40)
            throw new ArgumentException("RUDP-C closeout requires explicit RTO 40 ms.");
        if (closeoutRudpc && flightCap != 2)
            throw new ArgumentException("RUDP-C closeout requires physical flight cap 2.");
        return new(target, port, quick, flightCap, selectedRto, traceEnabled,
            characterizeRto, samples, acceptRudpc, runs, closeoutRudpc);
    }
}
