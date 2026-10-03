using Cino.Native;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Hosting.WindowsServices;

if (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture != Architecture.X64)
    throw new PlatformNotSupportedException("Windows x64 is required");

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders(); // Never log configuration, credentials or server response bodies.
builder.Host.UseWindowsService(o => o.ServiceName = "CinoNativeHost");
var configPath = builder.Configuration["config"] ?? Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CINO-AOS", "NativeHost", "host.json");
var dataPath = builder.Configuration["data"] ?? Path.Combine(Path.GetDirectoryName(configPath)!, "state");
var portText = builder.Configuration["port"] ?? "8093";
if (!int.TryParse(portText, out var port) || port is < 1024 or > 65535) throw new ArgumentException("invalid_port");
var config = File.Exists(configPath) ? JsonSerializer.Deserialize<HostConfig>(File.ReadAllText(configPath))! : new HostConfig();
if (config == null || string.IsNullOrWhiteSpace(config.DisplayName) || config.DisplayName.Length > 255)
    throw new ArgumentException("invalid_host_config");
if (config.ManagementUrl.Length > 0) _ = ProtocolClient.ValidateBase(config.ManagementUrl, config.AllowLoopbackForTesting);
using var store = new StateStore(dataPath);
builder.WebHost.UseUrls("http://127.0.0.1:" + port);
builder.Services.AddSingleton(config);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton<LiveStatus>();
builder.Services.AddSingleton<WorkbenchBridge>();
builder.Services.AddSingleton<TaskEngine>();
builder.Services.AddSingleton<ReleaseClient>();
builder.Services.AddHostedService<HostWorker>();
var app = builder.Build();
app.Use(async (context, next) =>
{
    if (context.Request.Host.Host is not ("127.0.0.1" or "localhost"))
    { context.Response.StatusCode = 400; return; }
    context.Response.Headers.CacheControl = "no-store";
    await next(context);
});
app.MapPost("/bridge/poll", (HttpContext context, WorkbenchBridge bridge) =>
    bridge.Authorized(context.Request.Headers.Authorization.ToString()) ? Results.Json(bridge.Poll()) : Results.Unauthorized());
app.MapPost("/bridge/result/{id}", async (string id, HttpContext context, WorkbenchBridge bridge) => {
    if (!bridge.Authorized(context.Request.Headers.Authorization.ToString())) return Results.Unauthorized();
    if (context.Request.ContentLength is null or > 180000) return Results.BadRequest();
    using var body = await JsonDocument.ParseAsync(context.Request.Body);
    return bridge.Complete(id, body.RootElement) ? Results.Ok() : Results.Conflict();
});
app.MapGet("/health", () => new { schema = "cino.native-host.health.v1", status = "ok",
    version = TaskEngine.Version, production_ready = false });
app.MapGet("/api/status", (LiveStatus status) => new {
    schema = "cino.native-host.status.v1", observed_at = DateTimeOffset.UtcNow,
    version = TaskEngine.Version, operating_system = RuntimeInformation.OSDescription,
    os_version = Environment.OSVersion.Version.ToString(),
    architecture = RuntimeInformation.OSArchitecture.ToString(), logical_processors = Environment.ProcessorCount,
    uptime_seconds = Environment.TickCount64 / 1000, windows_vm_required = false,
    link_state = status.LinkState, host_id = status.HostId, last_success = status.LastSuccess,
    last_error_code = status.ErrorCode, registered = status.HostId != null,
    remote_tasks_enabled = true, application_automation_verified = false,
    model_inference_verified = false, production_ready = false
});
await app.RunAsync();

namespace Cino.Native
{
    public sealed class LiveStatus
    {
        public string LinkState { get; set; } = "not_configured";
        public string? HostId { get; set; }
        public DateTimeOffset? LastSuccess { get; set; }
        public string? ErrorCode { get; set; }
    }
}
