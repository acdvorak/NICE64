namespace PicoSwitchController;

public sealed record GameBoyTransferProfile(
    byte CartType,
    int RomSize,
    int RamSize,
    byte FirmwareFlags);

public sealed record BridgeBulkProgress(
    string Phase,
    int CompletedBytes,
    int TotalBytes,
    int ItemIndex,
    int ItemCount);

public static class BridgeBulkTransfer
{
    public const int ControllerPakSize = 32768;
    public const int BlockSize = 32;
    public const int GameBoyFlashPageSize = 256;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FlashPageTimeout = TimeSpan.FromSeconds(15);

    public static string SendAndExpect(
        IBridgeLineTransport transport,
        string command,
        Predicate<string> expected,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        int maxAttempts = 1)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        Exception? lastTimeout = null;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            transport.WriteLine(command);
            DateTime deadline = DateTime.UtcNow.Add(timeout);
            try
            {
                while (DateTime.UtcNow < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string line = transport.ReadLine(deadline - DateTime.UtcNow,
                                                     cancellationToken).Trim();
                    if (expected(line)) return line;
                    if (line.StartsWith("ERR", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(line);
                    }
                }
            }
            catch (TimeoutException ex)
            {
                lastTimeout = ex;
                if (attempt < maxAttempts) continue;
            }
        }
        throw new TimeoutException(
            $"Timed out waiting for NICE64 response to {CommandName(command)}.",
            lastTimeout);
    }

    public static void SelectControllerPak(
        IBridgeLineTransport transport,
        CancellationToken cancellationToken) =>
        SendAndExpect(transport, "PAKMODE MEM",
            line => line.StartsWith("OK PAKMODE", StringComparison.OrdinalIgnoreCase),
            DefaultTimeout, cancellationToken);

    public static void UploadControllerPak(
        IBridgeLineTransport transport,
        byte[] image,
        Action<BridgeBulkProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Length != ControllerPakSize)
        {
            throw new ArgumentException(
                $"Controller Pak image must be exactly {ControllerPakSize} bytes.",
                nameof(image));
        }
        SelectControllerPak(transport, cancellationToken);
        int blocks = ControllerPakSize / BlockSize;
        for (int offset = 0; offset < image.Length; offset += BlockSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string hex = Convert.ToHexString(image, offset, BlockSize);
            SendAndExpect(transport, $"PAK {offset:X4} {hex}",
                line => line.StartsWith("OK PAK", StringComparison.OrdinalIgnoreCase),
                DefaultTimeout, cancellationToken, maxAttempts: 2);
            progress?.Invoke(new("CONTROLLER_PAK_UPLOAD",
                offset + BlockSize, image.Length, offset / BlockSize + 1, blocks));
        }
    }

    public static byte[] DownloadControllerPak(
        IBridgeLineTransport transport,
        Action<BridgeBulkProgress>? progress,
        CancellationToken cancellationToken)
    {
        SelectControllerPak(transport, cancellationToken);
        byte[] image = new byte[ControllerPakSize];
        int blocks = image.Length / BlockSize;
        for (int offset = 0; offset < image.Length; offset += BlockSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string response = SendAndExpect(transport, $"PAKDUMP {offset:X4}",
                line => line.StartsWith($"PAK {offset:X4} ", StringComparison.OrdinalIgnoreCase),
                DefaultTimeout, cancellationToken, maxAttempts: 2);
            CopyHexBlock(response[9..], image, offset, "Controller Pak");
            progress?.Invoke(new("CONTROLLER_PAK_DOWNLOAD",
                offset + BlockSize, image.Length, offset / BlockSize + 1, blocks));
        }
        return image;
    }

    public static int QueryGameBoyFlashCapacity(
        IBridgeLineTransport transport,
        CancellationToken cancellationToken)
    {
        string response = SendAndExpect(transport, "GBFCAP?",
            line => line.StartsWith("GBFCAP ", StringComparison.OrdinalIgnoreCase),
            DefaultTimeout, cancellationToken);
        if (!int.TryParse(response[7..].Trim(),
                System.Globalization.NumberStyles.HexNumber, null, out int capacity))
        {
            throw new InvalidOperationException($"Unexpected GBFCAP response: {response}");
        }
        return capacity;
    }

    public static void InstallFullGameBoyRom(
        IBridgeLineTransport transport,
        byte[] rom,
        GameBoyTransferProfile profile,
        byte[] save,
        Action<BridgeBulkProgress>? progress,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rom);
        ArgumentNullException.ThrowIfNull(save);
        if (rom.Length == 0) throw new ArgumentException("GB ROM is empty.", nameof(rom));
        if (save.Length == 0 || save.Length % BlockSize != 0)
        {
            throw new ArgumentException("GB save size must be a nonzero multiple of 32 bytes.", nameof(save));
        }

        string response = SendAndExpect(transport, "GBCLR",
            line => line.StartsWith("OK GBCLR", StringComparison.OrdinalIgnoreCase),
            DefaultTimeout, cancellationToken);
        log?.Invoke($"GBCLR ACK {response}");
        SendGameBoyProfile(transport, profile, cancellationToken);
        response = SendAndExpect(transport, $"GBFBEGIN {rom.Length:X8}",
            line => line.StartsWith("OK GBFBEGIN", StringComparison.OrdinalIgnoreCase),
            FlashPageTimeout, cancellationToken);
        log?.Invoke($"GBFBEGIN ACK {response}");

        byte[] page = new byte[GameBoyFlashPageSize];
        int pages = (rom.Length + page.Length - 1) / page.Length;
        for (int offset = 0; offset < rom.Length; offset += page.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Array.Fill(page, (byte)0xFF);
            int count = Math.Min(page.Length, rom.Length - offset);
            Buffer.BlockCopy(rom, offset, page, 0, count);
            response = SendAndExpect(transport,
                $"GBFP {offset:X6} {Convert.ToHexString(page)}",
                line => line.StartsWith("OK GBFP", StringComparison.OrdinalIgnoreCase),
                FlashPageTimeout, cancellationToken, maxAttempts: 2);
            log?.Invoke($"GBFP ACK offset=0x{offset:X6} response={response}");
            progress?.Invoke(new("GB_ROM_UPLOAD", Math.Min(offset + count, rom.Length),
                rom.Length, offset / page.Length + 1, pages));
        }

        response = SendAndExpect(transport, "GBFCOMMIT",
            line => line.StartsWith("OK GBFCOMMIT", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(10), cancellationToken);
        log?.Invoke($"GBFCOMMIT ACK {response}");
        UploadGameBoySave(transport, save, progress, log, cancellationToken);
        response = SendAndExpect(transport, "PAKMODE TRANSFER",
            line => line.StartsWith("OK PAKMODE", StringComparison.OrdinalIgnoreCase),
            DefaultTimeout, cancellationToken);
        log?.Invoke($"PAKMODE TRANSFER ACK {response}");
    }

    public static void UploadGameBoySave(
        IBridgeLineTransport transport,
        byte[] save,
        Action<BridgeBulkProgress>? progress,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        int blocks = save.Length / BlockSize;
        for (int offset = 0; offset < save.Length; offset += BlockSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string hex = Convert.ToHexString(save, offset, BlockSize);
            string response = SendAndExpect(transport, $"GBS {offset:X4} {hex}",
                line => line.StartsWith("OK GBS", StringComparison.OrdinalIgnoreCase),
                DefaultTimeout, cancellationToken, maxAttempts: 2);
            log?.Invoke($"GBS ACK offset=0x{offset:X4} response={response}");
            progress?.Invoke(new("GB_SAVE_UPLOAD", offset + BlockSize, save.Length,
                offset / BlockSize + 1, blocks));
        }
    }

    public static byte[] DownloadGameBoySave(
        IBridgeLineTransport transport,
        int saveSize,
        Action<BridgeBulkProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (saveSize <= 0 || saveSize % BlockSize != 0)
            throw new ArgumentOutOfRangeException(nameof(saveSize));
        byte[] save = new byte[saveSize];
        int blocks = save.Length / BlockSize;
        for (int offset = 0; offset < save.Length; offset += BlockSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string response = SendAndExpect(transport, $"GBSDUMP {offset:X4}",
                line => line.StartsWith($"GBS {offset:X4} ", StringComparison.OrdinalIgnoreCase),
                DefaultTimeout, cancellationToken, maxAttempts: 2);
            CopyHexBlock(response[9..], save, offset, "GB save");
            progress?.Invoke(new("GB_SAVE_DOWNLOAD", offset + BlockSize, save.Length,
                offset / BlockSize + 1, blocks));
        }
        return save;
    }

    private static void SendGameBoyProfile(
        IBridgeLineTransport transport,
        GameBoyTransferProfile profile,
        CancellationToken cancellationToken) =>
        SendAndExpect(transport,
            $"GBP {profile.CartType:X2} {profile.RomSize:X8} {profile.RamSize:X8} {profile.FirmwareFlags:X2}",
            line => line.StartsWith("OK GBP", StringComparison.OrdinalIgnoreCase),
            DefaultTimeout, cancellationToken);

    private static void CopyHexBlock(string hex, byte[] destination, int offset,
                                     string label)
    {
        byte[] block;
        try
        {
            block = Convert.FromHexString(hex.Trim());
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"NICE64 returned malformed {label} data.", ex);
        }
        if (block.Length != BlockSize)
        {
            throw new InvalidOperationException(
                $"NICE64 returned {block.Length} bytes for {label} block 0x{offset:X4}.");
        }
        Buffer.BlockCopy(block, 0, destination, offset, BlockSize);
    }

    private static string CommandName(string command)
    {
        int separator = command.IndexOf(' ');
        return separator < 0 ? command : command[..separator];
    }
}
