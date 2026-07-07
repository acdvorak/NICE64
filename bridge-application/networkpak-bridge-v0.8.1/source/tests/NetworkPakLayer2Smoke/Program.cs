using System.Buffers.Binary;
using System.Text;
using PicoSwitchController;

NetworkPakBridgeService service = new();

string Exchange(string line)
{
    if (!service.TryHandleRequest(line, out string response) || string.IsNullOrEmpty(response))
        throw new InvalidOperationException($"No response for {line}");
    return response;
}

byte[] ReadStream(string resource, int? expectedLength = null)
{
    byte sequence = 1;
    string open = Exchange($"NETPAK_REQ_HEX|{sequence}|OPEN_STREAM|{Convert.ToHexString(Encoding.ASCII.GetBytes(resource))}");
    string[] openParts = open.Split('|');
    if (openParts[2] != "OK" || openParts[3] != "A0") throw new InvalidOperationException(open);
    byte[] metadata = Convert.FromHexString(openParts[4]);
    byte id = metadata[0];
    int length = (int)BinaryPrimitives.ReadUInt32BigEndian(metadata.AsSpan(1, 4));
    uint expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(metadata.AsSpan(6, 4));
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

void TestStream(string resource, int expectedLength)
{
    byte[] received = ReadStream(resource, expectedLength);
    uint actualCrc = Crc32(received);
    Console.WriteLine($"PASS {resource} bytes={expectedLength} crc={actualCrc:X8}");
}

void TestTextStream(string resource, string contentFileName)
{
    string contentPath = Path.Combine(AppContext.BaseDirectory, "content", contentFileName);
    string expectedText = File.ReadAllText(contentPath);
    byte[] received = ReadStream(resource, Encoding.UTF8.GetByteCount(expectedText));
    string actual = Encoding.UTF8.GetString(received);
    if (!string.Equals(actual, expectedText, StringComparison.Ordinal))
        throw new InvalidOperationException($"{resource} text mismatch");
    Console.WriteLine($"PASS {resource} text bytes={received.Length}");
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
        "0.8",
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
Console.WriteLine($"sessions open={snapshot.StreamOpenCount} chunks={snapshot.StreamChunkCount} close={snapshot.StreamCloseCount} errors={snapshot.StreamErrorCount}");
Console.WriteLine($"bytes requested={snapshot.BytesRequested} delivered={snapshot.BytesDelivered} active={snapshot.ActiveStreamCount}");
