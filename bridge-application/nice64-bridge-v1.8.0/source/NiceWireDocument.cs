using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace PicoSwitchController;

public static class NiceWireDocument
{
    public const ushort Version = 1;
    public const uint Magic = 0x4E494345u; // NICE
    public const int MaxDocumentBytes = 16384;
    public const int MaxBlocks = 96;
    public const int MaxTextBytes = 240;
    public const int MaxTargetBytes = 96;
    public const int MaxTextPoolBytes = 12288;
    public const int MaxCanonicalUrlBytes = 128;
    // This block is transport metadata, not a renderable NICE block. It keeps
    // the temporary browser:// route separate from the final HTTP(S) identity.
    public const ushort CanonicalUrlMetadataType = 10;

    public static byte[] Build(uint pageId, IReadOnlyList<NiceWireBlock> blocks, string? canonicalUrl = null)
    {
        string? canonical = NormalizeCanonicalUrl(canonicalUrl);
        int wireBlockCount = blocks.Count + (canonical is null ? 0 : 1);
        if (blocks.Count == 0 || wireBlockCount > MaxBlocks)
        {
            throw new InvalidDataException($"NICE block count must be 1-{MaxBlocks}.");
        }

        using MemoryStream payload = new();
        if (canonical is not null)
        {
            WriteBlock(payload, new NiceWireBlock(CanonicalUrlMetadataType, canonical, null, 0, 0));
        }
        foreach (NiceWireBlock block in blocks)
        {
            WriteBlock(payload, block);
        }

        byte[] payloadBytes = payload.ToArray();
        int totalBytes = 16 + payloadBytes.Length;
        if (totalBytes > MaxDocumentBytes)
        {
            throw new InvalidDataException($"NICE document is {totalBytes} bytes; maximum is {MaxDocumentBytes}.");
        }

        using MemoryStream document = new();
        Span<byte> header = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(header[0..4], Magic);
        BinaryPrimitives.WriteUInt16BigEndian(header[4..6], Version);
        BinaryPrimitives.WriteUInt16BigEndian(header[6..8], (ushort)wireBlockCount);
        BinaryPrimitives.WriteUInt32BigEndian(header[8..12], pageId);
        BinaryPrimitives.WriteUInt32BigEndian(header[12..16], (uint)payloadBytes.Length);
        document.Write(header);
        document.Write(payloadBytes);
        return document.ToArray();
    }

    public static NiceWireBuildResult BuildFromJson(
        string path,
        int maxGeneratedPageBytes,
        Func<string, string?>? mapLinkTarget = null)
    {
        int jsonBytes = Encoding.UTF8.GetByteCount(File.ReadAllText(path));
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("NICE document JSON root must be an object.");
        }

        uint pageId = ReadPageId(root);
        string? canonicalUrl = root.TryGetProperty("finalUrl", out JsonElement finalUrl)
            ? NormalizeCanonicalUrl(ReadOptionalString(finalUrl))
            : null;
        if (!root.TryGetProperty("blocks", out JsonElement blocksElement) || blocksElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("NICE document JSON must contain a blocks array.");
        }

        List<NiceWireBlock> blocks = new();
        foreach (JsonElement blockElement in blocksElement.EnumerateArray())
        {
            NiceWireBlock block = ParseBlock(blockElement, mapLinkTarget);
            if (IsUsefulBlock(block))
            {
                blocks.Add(block);
            }
        }

        int originalBlockCount = blocks.Count;
        List<NiceWireBlock> reduced = ReduceBlocks(blocks, out bool truncated);
        while (reduced.Count + (canonicalUrl is null ? 0 : 1) > MaxBlocks && DropBeforeEnd(reduced))
        {
            truncated = true;
        }
        byte[] wire = Build(pageId, reduced, canonicalUrl);
        int limit = Math.Min(MaxDocumentBytes, maxGeneratedPageBytes);
        while (wire.Length > limit && DropBeforeEnd(reduced))
        {
            truncated = true;
            wire = Build(pageId, reduced, canonicalUrl);
        }

        if (wire.Length > limit)
        {
            throw new InvalidDataException($"NICE document is {wire.Length} bytes; configured maximum is {limit}.");
        }

        return new NiceWireBuildResult(wire, originalBlockCount, reduced.Count, truncated, jsonBytes, canonicalUrl);
    }

    public static byte[] BuildErrorDocument(string title, string detail)
    {
        NiceWireBlock[] blocks =
        [
            new(0, "NICE64 Browser", null, 0, 0),
            new(1, title, null, 0, 0),
            new(3, Shorten(detail, MaxTextBytes), null, 0, 0),
            new(6, "Retry", "nice://retry", 0, 0),
            new(6, "Home", "nice://home", 0, 0),
            new(6, "Known-good browser://test", "browser://test", 0, 0),
            new(9, null, null, 0, 0)
        ];

        return Build(0x00001000u, blocks);
    }

    private static List<NiceWireBlock> ReduceBlocks(IEnumerable<NiceWireBlock> source, out bool truncated)
    {
        truncated = false;
        List<NiceWireBlock> blocks = new();
        bool imageRetained = false;

        foreach (NiceWireBlock block in source)
        {
            NiceWireBlock clean = block with
            {
                Text = Shorten(NormalizeText(CleanText(block.Text)), MaxTextBytes),
                Target = Shorten(CleanText(block.Target), MaxTargetBytes)
            };

            if (IsLowValue(clean))
            {
                truncated = true;
                continue;
            }

            if (clean.Type == 7)
            {
                if (imageRetained || string.IsNullOrWhiteSpace(clean.Target)) { truncated = true; continue; }
                imageRetained = true;
            }

            if (IsRedundantOpeningBlock(blocks, clean) || (blocks.Count > 0 && BlocksEquivalent(blocks[^1], clean)))
            {
                truncated = true;
                continue;
            }

            blocks.Add(clean);
        }

        if (blocks.Count == 0 || blocks[0].Type != 0)
        {
            blocks.Insert(0, new NiceWireBlock(0, "NICE64 Browser", null, 0, 0));
            truncated = true;
        }

        blocks.RemoveAll(block => block.Type == 9);
        blocks = PrioritizeLinks(blocks);
        blocks.Add(new NiceWireBlock(9, null, null, 0, 0));

        while (blocks.Count > MaxBlocks && DropBeforeEnd(blocks))
        {
            truncated = true;
        }

        while (TextPoolBytes(blocks) > MaxTextPoolBytes && DropBeforeEnd(blocks))
        {
            truncated = true;
        }

        while (!CanBuild(blocks) && DropBeforeEnd(blocks))
        {
            truncated = true;
        }

        return blocks;
    }

    private static List<NiceWireBlock> PrioritizeLinks(List<NiceWireBlock> blocks)
    {
        List<NiceWireBlock> result = new();
        List<NiceWireBlock> links = new();
        int linkKeep = 0;

        foreach (NiceWireBlock block in blocks)
        {
            if (block.Type == 6 && linkKeep < 8)
            {
                links.Add(block);
                linkKeep++;
                continue;
            }

            result.Add(block);
        }

        int insertAt = result.FindIndex(block => block.Type is not 0 and not 1 and not 2);
        if (insertAt < 0)
        {
            insertAt = result.Count;
        }
        result.InsertRange(insertAt, links);
        return result;
    }

    private static bool IsRedundantOpeningBlock(List<NiceWireBlock> blocks, NiceWireBlock candidate)
    {
        if (blocks.Count > 8 || string.IsNullOrWhiteSpace(candidate.Text)) return false;
        foreach (NiceWireBlock previous in blocks)
        {
            if (string.IsNullOrWhiteSpace(previous.Text) || !TextEquivalent(previous.Text, candidate.Text)) continue;
            if ((previous.Type == 0 && candidate.Type is 1 or 2) ||
                (previous.Type == 1 && candidate.Type == 2) ||
                (previous.Type == candidate.Type && candidate.Type is 3 or 6)) return true;
        }
        return false;
    }

    private static bool DropBeforeEnd(List<NiceWireBlock> blocks)
    {
        if (blocks.Count <= 2)
        {
            return false;
        }

        blocks.RemoveAt(blocks.Count - 2);
        return true;
    }

    private static bool CanBuild(List<NiceWireBlock> blocks)
    {
        try
        {
            _ = Build(0, blocks);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? NormalizeCanonicalUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        string canonical = uri.AbsoluteUri;
        return Encoding.UTF8.GetByteCount(canonical) <= MaxCanonicalUrlBytes ? canonical : null;
    }

    private static int TextPoolBytes(IEnumerable<NiceWireBlock> blocks)
    {
        int total = 0;
        foreach (NiceWireBlock block in blocks)
        {
            if (block.Text is not null) total += Encoding.UTF8.GetByteCount(block.Text) + 1;
            if (block.Target is not null) total += Encoding.UTF8.GetByteCount(block.Target) + 1;
        }

        return total;
    }

    private static bool BlocksEquivalent(NiceWireBlock left, NiceWireBlock right)
    {
        return left.Type == right.Type &&
               TextEquivalent(left.Text, right.Text) &&
               string.Equals(left.Target, right.Target, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TextEquivalent(string? left, string? right) =>
        string.Equals(CleanText(left), CleanText(right), StringComparison.OrdinalIgnoreCase);

    private static bool IsUsefulBlock(NiceWireBlock block)
    {
        if (block.Type == 8 || block.Type == 9)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(block.Text);
    }

    private static bool IsLowValue(NiceWireBlock block)
    {
        string text = block.Text ?? string.Empty;
        string lower = text.Trim().ToLowerInvariant();
        if (lower.Length == 0 && block.Type != 8 && block.Type != 9)
        {
            return true;
        }

        string[] noise =
        [
            "privacy", "terms", "cookie", "sign in", "log in", "subscribe", "advertisement",
            "newsletter", "footer", "account", "cart", "skip to", "share this", "follow us"
        ];
        return noise.Any(item => lower.Contains(item, StringComparison.Ordinal));
    }

    private static string? CleanText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string clean = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return clean.Length == 0 ? null : clean;
    }

    public static string? NormalizeText(string? value)
    {
        if (value is null) return null;
        return value.Replace('\u2018', '\'').Replace('\u2019', '\'')
            .Replace('\u201C', '\"').Replace('\u201D', '\"')
            .Replace('\u2013', '-').Replace('\u2014', '-')
            .Replace("\u2026", "...").Replace('\u00A0', ' ');
    }

    private static string? Shorten(string? value, int maxBytes)
    {
        if (value is null || Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            return value;
        }

        string suffix = "...";
        int maxChars = Math.Max(0, maxBytes - suffix.Length);
        string result = value;
        while (result.Length > 0 && Encoding.UTF8.GetByteCount(result + suffix) > maxBytes)
        {
            int next = result.LastIndexOf(' ', Math.Min(result.Length - 1, maxChars));
            result = next > 24 ? result[..next] : result[..^1];
        }

        return result.TrimEnd() + suffix;
    }

    private static uint ReadPageId(JsonElement root)
    {
        if (!root.TryGetProperty("pageId", out JsonElement pageIdElement))
        {
            return 0x00001000u;
        }

        return pageIdElement.ValueKind switch
        {
            JsonValueKind.Number when pageIdElement.TryGetUInt32(out uint value) => value,
            JsonValueKind.String when uint.TryParse(pageIdElement.GetString(), out uint value) => value,
            _ => throw new InvalidDataException("pageId must be an unsigned integer.")
        };
    }

    private static NiceWireBlock ParseBlock(JsonElement blockElement, Func<string, string?>? mapLinkTarget)
    {
        if (blockElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Each NICE block must be an object.");
        }

        string typeName = ReadString(blockElement, "type", required: true);
        ushort type = MapType(typeName);
        string? text = ReadOptionalString(blockElement, "text");
        string? target = ReadOptionalString(blockElement, "target");
        ushort imageWidth = ReadUInt16(blockElement, "imageWidth");
        ushort imageHeight = ReadUInt16(blockElement, "imageHeight");

        if (type == 6 && target is not null && mapLinkTarget is not null)
        {
            target = mapLinkTarget(target);
        }

        return new NiceWireBlock(type, text, target, imageWidth, imageHeight);
    }

    private static ushort MapType(string typeName) => typeName.Trim().ToLowerInvariant() switch
    {
        "title" => 0,
        "h1" => 1,
        "h2" or "h3" => 2,
        "paragraph" or "p" => 3,
        "list" or "listitem" or "li" or "list_item" => 4,
        "quote" or "blockquote" => 5,
        "link" => 6,
        "image" or "imageslot" or "image_placeholder" => 7,
        "hr" or "rule" or "horizontal_rule" => 8,
        "end" => 9,
        _ => throw new InvalidDataException($"Unknown NICE block type: {typeName}")
    };

    private static string ReadString(JsonElement element, string propertyName, bool required)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            if (required)
            {
                throw new InvalidDataException($"Missing required property: {propertyName}");
            }

            return string.Empty;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"{propertyName} must be a string.");
        }

        return property.GetString() ?? string.Empty;
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"{propertyName} must be a string.");
        }

        string value = property.GetString() ?? string.Empty;
        return value.Length == 0 ? null : value;
    }

    private static string? ReadOptionalString(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("finalUrl must be a string.");
        }
        string value = element.GetString() ?? string.Empty;
        return value.Length == 0 ? null : value;
    }

    private static ushort ReadUInt16(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            return 0;
        }

        if (!property.TryGetUInt16(out ushort value))
        {
            throw new InvalidDataException($"{propertyName} must fit in uint16.");
        }

        return value;
    }

    private static void WriteBlock(Stream stream, NiceWireBlock block)
    {
        byte[] text = block.Text is null ? [] : Encoding.UTF8.GetBytes(block.Text);
        byte[] target = block.Target is null ? [] : Encoding.UTF8.GetBytes(block.Target);
        if (text.Length > MaxTextBytes)
        {
            throw new InvalidDataException($"text is {text.Length} bytes; maximum is {MaxTextBytes}.");
        }
        if (target.Length > MaxTargetBytes)
        {
            throw new InvalidDataException($"target is {target.Length} bytes; maximum is {MaxTargetBytes}.");
        }

        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header[0..2], block.Type);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..4], 0);
        BinaryPrimitives.WriteUInt16BigEndian(header[4..6], (ushort)text.Length);
        BinaryPrimitives.WriteUInt16BigEndian(header[6..8], (ushort)target.Length);
        BinaryPrimitives.WriteUInt16BigEndian(header[8..10], block.ImageWidth);
        BinaryPrimitives.WriteUInt16BigEndian(header[10..12], block.ImageHeight);
        stream.Write(header);
        stream.Write(text);
        stream.Write(target);
    }
}

public readonly record struct NiceWireBlock(
    ushort Type,
    string? Text,
    string? Target,
    ushort ImageWidth,
    ushort ImageHeight);

public sealed record NiceWireBuildResult(
    byte[] Wire,
    int OriginalBlockCount,
    int RetainedBlockCount,
    bool Truncated,
    int JsonBytes,
    string? CanonicalUrl);
