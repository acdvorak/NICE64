using PicoSwitchController;

List<string> log = [];
PppDiagnosticDecoder decoder = new();

byte[] lcp = EncodeFrame([
    0xFF, 0x03, 0xC0, 0x21,
    0x01, 0x7A, 0x00, 0x1C,
    0x01, 0x04, 0x05, 0xDC,
    0x02, 0x06, 0x00, 0x00, 0x00, 0x00,
    0x03, 0x04, 0xC0, 0x23,
    0x05, 0x06, 0x12, 0x34, 0x56, 0x78,
    0x07, 0x02, 0x08, 0x02
]);
for (int offset = 0; offset < lcp.Length;)
{
    int count = Math.Min((offset % 5) + 1, lcp.Length - offset);
    decoder.Feed(lcp.AsSpan(offset, count), log.Add);
    offset += count;
}

Require(log.Any(x => x.Contains("protocol=LCP (0xC021)") && x.Contains("fcs=valid")), "valid LCP/FCS not decoded");
Require(log.Any(x => x.Contains("Configure-Request") && x.Contains("identifier=0x7A")), "LCP request not decoded");
Require(log.Any(x => x.Contains("MRU=1500")), "MRU option missing");
Require(log.Any(x => x.Contains("ACCM=0x00000000")), "ACCM option missing");
Require(log.Any(x => x.Contains("Authentication=0xC023")), "authentication option missing");
Require(log.Any(x => x.Contains("Magic=0x12345678")), "magic option missing");
Require(log.Any(x => x.Contains("Protocol-Field-Compression")), "PFC option missing");
Require(log.Any(x => x.Contains("Address-Control-Field-Compression")), "ACFC option missing");

decoder.Feed(lcp, log.Add);
Require(log.Any(x => x.Contains("LCP retry observed count=2")), "LCP retry not detected");

byte[] invalid = (byte[])lcp.Clone();
invalid[^2] ^= 0x01;
decoder.Feed(invalid, log.Add);
Require(log.Any(x => x.Contains("protocol=LCP") && x.Contains("fcs=INVALID")), "invalid FCS not reported");

byte[] pap = EncodeFrame([0xFF, 0x03, 0xC0, 0x23, 0x01, 0x02, 0x00, 0x04]);
byte[] chap = EncodeFrame([0xFF, 0x03, 0xC2, 0x23, 0x01, 0x03, 0x00, 0x04]);
byte[] ipcp = EncodeFrame([0xFF, 0x03, 0x80, 0x21, 0x01, 0x04, 0x00, 0x04]);
byte[] unknown = EncodeFrame([0xFF, 0x03, 0x12, 0x35, 0xAA, 0x55]);
decoder.Feed(pap.Concat(chap).Concat(ipcp).Concat(unknown).ToArray(), log.Add);
Require(log.Any(x => x.Contains("protocol=PAP")), "PAP not identified");
Require(log.Any(x => x.Contains("protocol=CHAP")), "CHAP not identified");
Require(log.Any(x => x.Contains("protocol=IPCP")), "IPCP not identified");
Require(log.Any(x => x.Contains("protocol=Unknown (0x1235)")), "unknown protocol not tolerated");

decoder.Feed([0x7E, 0xFF, 0x7D, 0x7E], log.Add);
Require(log.Any(x => x.Contains("dangling escape")), "dangling escape not reported");

Console.WriteLine("PppDiagnosticHeadlessSmoke PASS");
Console.WriteLine($"decodedLogEntries={log.Count} partial/multiple/escaped/valid-FCS/invalid-FCS/unknown PASS");

static byte[] EncodeFrame(byte[] content)
{
    ushort fcs = 0xFFFF;
    foreach (byte value in content) fcs = UpdateFcs(fcs, value);
    fcs ^= 0xFFFF;
    List<byte> wire = [0x7E];
    foreach (byte value in content.Concat([(byte)fcs, (byte)(fcs >> 8)]))
    {
        if (value == 0x7E || value == 0x7D || value < 0x20)
        {
            wire.Add(0x7D);
            wire.Add((byte)(value ^ 0x20));
        }
        else wire.Add(value);
    }
    wire.Add(0x7E);
    return wire.ToArray();
}

static ushort UpdateFcs(ushort fcs, byte value)
{
    fcs ^= value;
    for (int bit = 0; bit < 8; ++bit)
        fcs = (ushort)((fcs >> 1) ^ ((fcs & 1) != 0 ? 0x8408 : 0));
    return fcs;
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
