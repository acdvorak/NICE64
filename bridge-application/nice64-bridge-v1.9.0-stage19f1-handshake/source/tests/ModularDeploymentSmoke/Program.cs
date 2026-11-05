using System.Text;
using PicoSwitchController;

bool expectSerial = args.Contains("--serial", StringComparer.OrdinalIgnoreCase);
bool expectPpp = args.Contains("--ppp", StringComparer.OrdinalIgnoreCase);
Guid connection = Guid.Parse("59c56577-05e6-44e7-afd3-ccf78d25b1f2");

using var service = new NetworkPakBridgeService();
IReadOnlyList<BridgeNetworkPakModuleInfo> modules = service.ListSelectableModules();
string[] ids = modules.Select(module => module.Id).OrderBy(id => id).ToArray();
Console.WriteLine("UI_MODULES=" + string.Join(',', ids));

Require(ids.Contains("serial-tunnel") == expectSerial, "Serial Tunnel UI presence mismatch");
Require(ids.Contains("ppp-gateway") == expectPpp, "PPP Gateway UI presence mismatch");
Require(ids.Contains("network-portal"), "Network Portal content module missing");
Require(ids.Contains("browser-live"), "Browser Live content module missing");

bool serialOpen = OpenAndClose(service, connection, "serial://tunnel", 10);
bool pppOpen = OpenAndClose(service, connection, "ppp://gateway", 20);
Require(serialOpen == expectSerial, "serial://tunnel availability mismatch");
Require(pppOpen == expectPpp, "ppp://gateway availability mismatch");
Require(OpenAndClose(service, connection, "bridge://menu", 30), "Network Portal content resource failed");
Require(OpenAndClose(service, connection, "nice://home", 40), "Browser Live content resource failed");

Console.WriteLine($"RESOURCES=serial:{serialOpen},ppp:{pppOpen},bridge:True,browser:True");
Console.WriteLine("ModularDeploymentSmoke PASS");
return 0;

static bool OpenAndClose(NetworkPakBridgeService service, Guid connection, string resource, byte sequence)
{
    string request = $"NETPAK_REQ_HEX|{sequence}|OPEN_STREAM|" +
        Convert.ToHexString(Encoding.ASCII.GetBytes(resource));
    Require(service.TryHandleRequest(connection, request, out string response), "request not handled");
    string[] parts = response.Split('|');
    if (parts.Length < 5 || parts[2] != "OK") return false;
    byte[] payload = Convert.FromHexString(parts[4]);
    byte streamId = payload[0];
    string close = $"NETPAK_REQ_HEX|{sequence + 1}|CLOSE_STREAM|{streamId:X2}";
    Require(service.TryHandleRequest(connection, close, out string closeResponse), "close not handled");
    Require(closeResponse.Split('|')[2] == "OK", "close failed");
    return true;
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
