using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using OpenDesk.Core;
using OpenDesk.Relay;

namespace OpenDesk.Service;

public sealed class DirectoryHost : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly PairingServer relay;
    public string Url => app.Urls.Single();
    public int RelayPort => relay.Port;
    private DirectoryHost(WebApplication app, PairingServer relay) { this.app = app; this.relay = relay; }
    public static async Task<DirectoryHost> StartAsync(string registryPath, X509Certificate2 certificate, int servicePort = 47834, int relayPort = 47832, bool loopback = false)
    {
        var relay = new PairingServer(); relay.Start(loopback ? IPAddress.Loopback : IPAddress.Any, relayPort);
        try {
            var registry = new DeviceRegistry(registryPath, relay.IsRoomOnline, (i,owner) => i.Port == relay.Port && relay.OwnsRoom(i.Room,owner));
            var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server => {
                server.Limits.MaxRequestBodySize = Wire.MaxControl; server.Limits.MaxConcurrentConnections = 128;
                server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
                server.Listen(loopback ? IPAddress.Loopback : IPAddress.Any, servicePort, endpoint => endpoint.UseHttps(certificate));
            });
            builder.Services.AddRateLimiter(options => {
                options.RejectionStatusCode = 429;
                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext,string>(context => RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions { PermitLimit=600, Window=TimeSpan.FromMinutes(1), QueueLimit=0 }));
            });
            var app = builder.Build(); app.UseRateLimiter();
            app.MapGet("/v1/info", () => Results.Json(new OpenDesk.Core.DirectoryInfo(1,relay.Port)));
            app.MapPost("/v1/challenge", () => {
                try { return Results.Json(new DirectoryClient.ChallengeReply(registry.Challenge(DateTimeOffset.UtcNow))); }
                catch (InvalidOperationException) { return Results.StatusCode(429); }
            });
            app.MapPost("/v1/register", (DeviceRegistration request) => {
                try { return Results.Json(registry.Register(request,DateTimeOffset.UtcNow)); }
                catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
                catch (InvalidOperationException) { return Results.StatusCode(409); }
                catch (FormatException) { return Results.BadRequest(); }
            });
            app.MapGet("/v1/device/{id}", (string id) => {
                try { var found = registry.Lookup(id,DateTimeOffset.UtcNow); return found is null ? Results.NotFound() : Results.Json(found); }
                catch (FormatException) { return Results.BadRequest(); }
            });
            await app.StartAsync(); return new(app,relay);
        } catch { await relay.DisposeAsync(); throw; }
    }
    public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); await relay.DisposeAsync(); }
}
