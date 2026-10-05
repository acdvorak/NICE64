using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PicoSwitchController;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

WebApplicationBuilder builder = WebApplication.CreateBuilder();
builder.WebHost.UseTestServer();
WebApplication app = builder.Build();
FakeFacade facade = new();
BridgeApiV1ControllerRoutes.Map(app, facade);
await app.StartAsync();
using HttpClient client = app.GetTestClient();

HttpResponseMessage get = await client.GetAsync(
    "/api/v1/sessions/session-1/controller/primary");
if (get.StatusCode != HttpStatusCode.OK || facade.GetCalls != 1)
    throw new InvalidOperationException("GET primary route failed");

HttpResponseMessage mobile = await client.PutAsJsonAsync(
    "/api/v1/sessions/session-1/controller/primary",
    new { primary = "nice-mobile" });
if (mobile.StatusCode != HttpStatusCode.OK ||
    facade.LastPrimary != BridgeControllerPrimary.NiceMobile)
    throw new InvalidOperationException("Mobile PUT route failed");

HttpResponseMessage bridge = await client.PutAsJsonAsync(
    "/api/v1/sessions/session-1/controller/primary",
    new { primary = "bridge" });
if (bridge.StatusCode != HttpStatusCode.OK ||
    facade.LastPrimary != BridgeControllerPrimary.Bridge)
    throw new InvalidOperationException("Bridge PUT route failed");

HttpResponseMessage invalid = await client.PutAsJsonAsync(
    "/api/v1/sessions/session-1/controller/primary",
    new { primary = "external" });
if (invalid.StatusCode != HttpStatusCode.BadRequest)
    throw new InvalidOperationException("Invalid primary accepted");

HttpResponseMessage oldAcquire = await client.PostAsJsonAsync(
    "/api/v1/sessions/session-1/controller/ownership", new { });
if (oldAcquire.StatusCode != HttpStatusCode.NotFound)
    throw new InvalidOperationException("Retired lease route remains mapped");

await app.StopAsync();
Console.WriteLine("BridgeApiV1ControllerRoutesHeadlessSmoke passed");

sealed class FakeFacade : IBridgeApiV1ControllerFacade
{
    public int GetCalls { get; private set; }
    public BridgeControllerPrimary? LastPrimary { get; private set; }
    public Task<BridgeApiV1ControllerApiResult> GetPrimaryAsync(
        string sessionId, CancellationToken cancellationToken = default)
    {
        GetCalls++;
        return Task.FromResult(Ok(sessionId, BridgeControllerPrimary.Bridge));
    }
    public Task<BridgeApiV1ControllerApiResult> SetPrimaryAsync(
        string sessionId, BridgeControllerPrimary primary,
        CancellationToken cancellationToken = default)
    {
        LastPrimary = primary;
        return Task.FromResult(Ok(sessionId, primary));
    }
    private static BridgeApiV1ControllerApiResult Ok(
        string sessionId, BridgeControllerPrimary primary)
    {
        BridgeControllerEndpointSnapshot endpoint = primary ==
            BridgeControllerPrimary.NiceMobile
            ? BridgeControllerEndpointSnapshot.AvailableAt(sessionId, "192.168.2.81")
            : BridgeControllerEndpointSnapshot.Unavailable(sessionId,
                BridgeControllerEndpointUnavailableReason.NoSession);
        return new(200, BridgeApiV1ControllerProjector.Project(sessionId,
            new(sessionId, primary, endpoint,
                primary == BridgeControllerPrimary.NiceMobile,
                BridgeControllerPrimaryFailure.None)));
    }
}
