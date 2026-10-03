using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cino.Native;

public sealed class TaskEngine(StateStore store, WorkbenchBridge bridge, ReleaseClient releases)
{
    public const string Version = "0.4.0-network.2";
    public static readonly string[] ServiceCapabilities = [
        "cino.host.status.read.v1", "cino.host.health.read.v1", "cino.host.self-check.read.v1",
        "cino.workspace.list.v1", "cino.workspace.read.v1", "cino.workspace.write.v1"
    ];
    public static readonly string[] DesktopCapabilities = ["cino.workbench.generate.v1", "cino.workbench.show.v1", "cino.workbench.close.v1"];
    static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public IEnumerable<string> Available => ServiceCapabilities.Concat(bridge.Online ? DesktopCapabilities : []).Concat(releases.Configured ? ["cino.release.stage.v1"] : []);
    public object[] Report => ServiceCapabilities.Concat(DesktopCapabilities).Concat(["cino.release.stage.v1"]).Select(id => (object)new {
        capability_id = id, state = Available.Contains(id) ? "available" : "unavailable",
        version = Version, constraints = new { raw_commands = false, workspace_scope = "CINO shared artifacts", max_text_bytes = 60000 }
    }).ToArray();
    string Area(string area)
    {
        var path = Path.Combine(store.DirectoryPath, area);
        if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("redirected_directory");
        Directory.CreateDirectory(path); return path;
    }
    string FilePath(string area, string id)
    {
        if (!Regex.IsMatch(id, "^task_[a-f0-9]{32}$")) throw new IOException("invalid_task_id");
        var path = Path.Combine(Area(area), id + ".json");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("redirected_file");
        return path;
    }
    static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, Json);
    static void Atomic(string path, object value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
            JsonSerializer.Serialize(stream, value, Json); stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }
    static string Hash(string text) => ProtocolClient.Hex(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    JsonElement ReadArtifact(string id)
    {
        var path = FilePath("artifacts", id);
        if (!File.Exists(path) || new FileInfo(path).Length > 220000) throw new IOException("artifact_not_available");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var artifact = doc.RootElement;
        if (Hash(artifact.GetProperty("text").GetString()!) != artifact.GetProperty("sha256").GetString()) throw new IOException("artifact_integrity_mismatch");
        return artifact.Clone();
    }
    JsonElement SaveArtifact(string id, string name, string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > 60000 || !Regex.IsMatch(name, @"^[\p{L}\p{N} _.-]{1,80}\.(md|txt|json|csv)$") || name.Contains(".."))
            throw new IOException("invalid_artifact");
        var path = FilePath("artifacts", id);
        if (File.Exists(path)) {
            var existing = ReadArtifact(id);
            if (existing.GetProperty("sha256").GetString() != Hash(text)) throw new IOException("artifact_conflict");
            return existing;
        }
        var value = Element(new { artifact_id = id, name, text, sha256 = Hash(text), size_bytes = Encoding.UTF8.GetByteCount(text), created_at = DateTimeOffset.UtcNow });
        Atomic(path, value); return ReadArtifact(id);
    }
    async Task<JsonElement> Execute(ProtocolClient client, JsonElement task, CancellationToken ct)
    {
        var id = task.GetProperty("task_id").GetString()!;
        var cap = task.GetProperty("capability_id").GetString()!;
        var parameters = task.GetProperty("intent_contract").GetProperty("parameters");
        var receipt = FilePath("receipts", id);
        if (File.Exists(receipt)) { using var cached = JsonDocument.Parse(File.ReadAllText(receipt)); return cached.RootElement.Clone(); }
        JsonElement outcome;
        try {
            object output;
            if (ServiceCapabilities.Take(3).Contains(cap)) {
                if (parameters.EnumerateObject().Any()) throw new IOException("unsupported_parameters");
                output = new { machine = Environment.MachineName, os = Environment.OSVersion.VersionString, processors = Environment.ProcessorCount,
                    service_version = Version, uptime_seconds = Environment.TickCount64 / 1000, interactive_workbench = bridge.Online,
                    capabilities = Available.ToArray(), update = releases.Status(), scope = "Windows native service; existing OS retained" };
            } else if (cap == "cino.workspace.write.v1")
                output = new { artifact = SaveArtifact(id, parameters.GetProperty("name").GetString()!, parameters.GetProperty("text").GetString()!) };
            else if (cap == "cino.workspace.read.v1")
                output = new { artifact = ReadArtifact(parameters.GetProperty("artifact_id").GetString()!) };
            else if (cap == "cino.workspace.list.v1")
                output = new { items = Directory.EnumerateFiles(Area("artifacts"), "task_*.json").OrderDescending().Take(100).Select(path => {
                    var a = ReadArtifact(Path.GetFileNameWithoutExtension(path));
                    return new { artifact_id = a.GetProperty("artifact_id").GetString(), name = a.GetProperty("name").GetString(),
                        sha256 = a.GetProperty("sha256").GetString(), size_bytes = a.GetProperty("size_bytes").GetInt32() };
                }).ToArray(), limit = 100 };
            else if (cap == "cino.release.stage.v1")
                output = await releases.Stage(client, parameters.GetProperty("release_id").GetString()!, ct);
            else if (DesktopCapabilities.Contains(cap)) {
                var response = await bridge.Execute(task, ct);
                if (response.GetProperty("outcome").GetString() != "succeeded") throw new IOException("workbench_operation_failed");
                output = response.TryGetProperty("output", out var result) ? result.Clone() : Element(new { });
                if (cap == "cino.workbench.generate.v1") {
                    var resultElement = (JsonElement)output;
                    var artifact = SaveArtifact(id, "generation.md", resultElement.GetProperty("text").GetString()!);
                    output = new { artifact, generation = resultElement };
                }
            } else throw new IOException("capability_not_supported");
            outcome = Element(new { outcome = "succeeded", output });
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            outcome = Element(new { outcome = "failed", error = ex is IOException ? ex.Message : ex.GetType().Name });
        }
        Atomic(receipt, outcome); return outcome;
    }
    public async Task Poll(ProtocolClient client, CancellationToken ct)
    {
        var state = store.Value;
        if (state.PendingTaskResult != null) {
            try {
                var result = await client.Send(state.PendingTaskResult, state.HostToken!, state.HostId, ct);
                if (ProtocolClient.Get(result, "host_id") != state.HostId || ProtocolClient.Get(result, "state") is not ("task_result_recorded" or "task_result_replayed"))
                    throw new ProtocolException("invalid_task_result_receipt");
            } catch (ProtocolException ex) when (ex.Message == "management_http_409" && state.ActiveTask.HasValue &&
                DateTimeOffset.Parse(state.ActiveTask.Value.GetProperty("expires_at").GetString()!) <= DateTimeOffset.UtcNow) { }
            state.PendingTaskResult = null; state.ActiveTask = null; store.Save();
        }
        if (!state.ActiveTask.HasValue) {
            state.PendingClaim ??= new("claim", "POST", "/tasks/claim", JsonSerializer.Serialize(new {
                schema = "cino.host.task-claim.request.v1", supported_capability_ids = Available.ToArray()
            }), "claim_" + Guid.NewGuid().ToString("N"));
            store.Save();
            var claim = await client.Send(state.PendingClaim, state.HostToken!, state.HostId, ct);
            if (ProtocolClient.Get(claim, "host_id") != state.HostId) throw new ProtocolException("invalid_claim_host");
            var claimState = ProtocolClient.Get(claim, "state");
            if (claimState == "task_leased") state.ActiveTask = claim.GetProperty("task").Clone();
            else if (claimState != "no_task_available") throw new ProtocolException("invalid_claim_receipt");
            state.PendingClaim = null; store.Save();
        }
        if (!state.ActiveTask.HasValue) return;
        var task = state.ActiveTask.Value;
        var id = task.GetProperty("task_id").GetString()!;
        var expiry = DateTimeOffset.Parse(task.GetProperty("expires_at").GetString()!);
        if (expiry <= DateTimeOffset.UtcNow.AddSeconds(10)) { state.ActiveTask = null; store.Save(); return; }
        if (task.GetProperty("target_host_id").GetString() != state.HostId) throw new ProtocolException("invalid_task_host");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Min(85, (expiry - DateTimeOffset.UtcNow).TotalSeconds - 8)));
        JsonElement resultBody;
        try { resultBody = await Execute(client, task, deadline.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            resultBody = Element(new { outcome = "failed", error = "execution_deadline_exceeded" });
            Atomic(FilePath("receipts", id), resultBody);
        }
        state.PendingTaskResult = new("task-result", "POST", "/tasks/" + id + "/result", JsonSerializer.Serialize(new {
            schema = "cino.host.task-result.v1", event_id = "event_" + Guid.NewGuid().ToString("N"),
            lease_id = task.GetProperty("lease_id").GetString(), lease_token = task.GetProperty("lease_token").GetString(),
            observed_at = DateTimeOffset.UtcNow, outcome = resultBody.GetProperty("outcome").GetString(),
            output = resultBody.TryGetProperty("output", out var output) ? output : Element(new {}),
            error = resultBody.TryGetProperty("error", out var error) ? error : Element((object?)null!)
        }, Json), "result_" + Guid.NewGuid().ToString("N"));
        store.Save();
        // Persist before sending; a lost response is retried, never re-executed.
    }
}
