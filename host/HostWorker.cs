using System.Runtime.InteropServices;
using System.Text.Json;

namespace Cino.Native;

public sealed class HostWorker(HostConfig config, StateStore store, LiveStatus live, TaskEngine tasks, ReleaseClient releases) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Yield();
        if (config.ManagementUrl.Length == 0) return;
        ProtocolClient client;
        try { client = new ProtocolClient(config); }
        catch (Exception ex)
        {
            live.LinkState = "blocked";
            live.ErrorCode = ex is ProtocolException ? ex.Message : ex.GetType().Name;
            return;
        }
        using var ownedClient = client;
        var normalized = client.BaseUri.ToString();
        var state = store.Value;
        if (state.ManagementUrl.Length > 0 && state.ManagementUrl != normalized)
        { live.LinkState = "blocked"; live.ErrorCode = "management_identity_changed"; return; }
        state.ManagementUrl = normalized;
        store.Save();
        int failures = 0;
        var nextReport = DateTimeOffset.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (state.HostToken == null)
                    await Register(client, ct);
                else if (!state.ExpiresAt.HasValue || state.ExpiresAt <= DateTimeOffset.UtcNow)
                    throw new ProtocolException("credential_expired_reenrollment_required");
                live.HostId = state.HostId;
                if (state.Pending != null) await Flush(client, ct);
                if (DateTimeOffset.UtcNow >= nextReport) {
                await Report(client, "capabilities", "PUT", "/capabilities", new {
                    schema = "cino.host.capability-report.v1", sequence = NextSequence(),
                    observed_at = DateTimeOffset.UtcNow, capabilities = tasks.Report
                }, ct);
                await Report(client, "heartbeat", "POST", "/heartbeat", new {
                    schema = "cino.host.heartbeat.v1", sequence = NextSequence(), observed_at = DateTimeOffset.UtcNow,
                    connection_state = "online", uptime_seconds = Environment.TickCount64 / 1000,
                    metrics = new { platform = "windows-native", logical_processors = Environment.ProcessorCount,
                        remote_tasks_enabled = true, agent_version = TaskEngine.Version, update = releases.Status(),
                        application_automation_verified = false, model_inference_verified = false }
                }, ct);
                await Report(client, "health", "POST", "/health", new {
                    schema = "cino.host.health-report.v1", sequence = NextSequence(), observed_at = DateTimeOffset.UtcNow,
                    overall_status = "healthy", checks = new[] {
                        new { id = "native_service", status = "healthy", message = "Windows native service responds" },
                        new { id = "task_channel", status = "healthy", message = "Typed task receiver installed; execution evidence is recorded per task" }
                    }
                }, ct);
                nextReport = DateTimeOffset.UtcNow.AddSeconds(30);
                }
                await tasks.Poll(client, ct);
                live.LinkState = "online";
                live.LastSuccess = DateTimeOffset.UtcNow;
                live.ErrorCode = null;
                failures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                live.LinkState = "degraded";
                live.ErrorCode = ex is ProtocolException ? ex.Message : ex.GetType().Name;
                failures = Math.Min(failures + 1, 5);
                if (ex is ProtocolException && (ex.Message is "management_http_401" or "management_http_403" or
                    "credential_expired_reenrollment_required" or "invalid_registration_receipt" or "invalid_report_receipt"))
                { live.LinkState = "blocked"; break; }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(failures == 0 ? 5 : Math.Min(300, 30 * Math.Pow(2, failures))), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    long NextSequence()
    {
        var next = checked(++store.Value.Sequence);
        store.Save();
        return next;
    }

    async Task Register(ProtocolClient client, CancellationToken ct)
    {
        var state = store.Value;
        if (!File.Exists(config.EnrollmentTokenFile)) throw new ProtocolException("enrollment_token_file_missing");
        var token = (await File.ReadAllTextAsync(config.EnrollmentTokenFile, ct)).Trim();
        if (token.Length < 32 || token.Length > 4096) throw new ProtocolException("invalid_enrollment_token");
        if (state.Pending == null)
        {
            state.Pending = new PendingRequest("register", "POST", "/register", JsonSerializer.Serialize(new {
                schema = "cino.host.register.request.v1", external_host_id = state.ExternalId,
                display_name = config.DisplayName, protocol_version = "1.0", agent_version = TaskEngine.Version,
                os = new { family = "Windows", version = Environment.OSVersion.Version.ToString(), architecture = "x86_64" },
                hardware = new { logical_processors = Environment.ProcessorCount },
                attestation = new { windows_native = true, tpm_verified = false, production_ready = false }
            }), "register_" + Guid.NewGuid().ToString("N"));
            store.Save();
        }
        if (state.Pending.Kind != "register") throw new ProtocolException("invalid_persisted_registration");
        var receipt = await client.Send(state.Pending, token, null, ct);
        ProtocolClient.ValidateRegistration(receipt);
        state.HostId = ProtocolClient.Get(receipt, "host_id");
        state.HostToken = ProtocolClient.Get(receipt, "host_token");
        state.ExpiresAt = DateTimeOffset.Parse(ProtocolClient.Get(receipt, "expires_at"));
        state.Pending = null;
        store.Save();
    }

    async Task Report(ProtocolClient client, string kind, string method, string path, object payload, CancellationToken ct)
    {
        store.Value.Pending = new PendingRequest(kind, method, path, JsonSerializer.Serialize(payload), kind + "_" + Guid.NewGuid().ToString("N"));
        store.Save();
        await Flush(client, ct);
    }

    async Task Flush(ProtocolClient client, CancellationToken ct)
    {
        var state = store.Value;
        var request = state.Pending!;
        var route = request.Kind switch {
            "heartbeat" => ("POST", "/heartbeat", "host_heartbeat_recorded"),
            "capabilities" => ("PUT", "/capabilities", "host_capabilities_recorded"),
            "health" => ("POST", "/health", "host_health_recorded"),
            _ => throw new ProtocolException("invalid_outbox_route")
        };
        if (request.Method != route.Item1 || request.Path != route.Item2) throw new ProtocolException("invalid_outbox_route");
        var receipt = await client.Send(request, state.HostToken!, state.HostId, ct);
        if (ProtocolClient.Get(receipt, "state") != route.Item3 || ProtocolClient.Get(receipt, "host_id") != state.HostId)
            throw new ProtocolException("invalid_report_receipt");
        state.Pending = null;
        store.Save();
    }
}
