using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cino.Native;

// Interactive actions remain in the signed-in user's workbench. The system
// service never launches a desktop process in Session 0.
public sealed class WorkbenchBridge(HostConfig config)
{
    readonly object gate = new();
    string? currentId;
    JsonElement? pending;
    TaskCompletionSource<JsonElement>? completion;
    DateTimeOffset lastPoll;
    public bool Online { get { lock (gate) return lastPoll > DateTimeOffset.UtcNow.AddSeconds(-15); } }
    public bool Authorized(string header)
    {
        if (config.BridgeTokenFile.Length == 0 || !File.Exists(config.BridgeTokenFile)) return false;
        var expected = File.ReadAllText(config.BridgeTokenFile).Trim();
        if (expected.Length != 64 || !header.StartsWith("Bearer ")) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(header[7..]));
    }
    public object Poll()
    {
        lock (gate) { lastPoll = DateTimeOffset.UtcNow; return new { task = pending }; }
    }
    public bool Complete(string id, JsonElement result)
    {
        lock (gate) {
            if (id != currentId || completion == null) return false;
            if (!result.TryGetProperty("outcome", out var outcome) || outcome.GetString() is not ("succeeded" or "failed" or "cancelled")) return false;
            if (Encoding.UTF8.GetByteCount(result.GetRawText()) > 180000) return false;
            return completion.TrySetResult(result.Clone());
        }
    }
    public async Task<JsonElement> Execute(JsonElement task, CancellationToken ct)
    {
        TaskCompletionSource<JsonElement> promise;
        lock (gate) {
            if (!Online || pending != null) throw new InvalidOperationException("workbench_not_available");
            currentId = task.GetProperty("task_id").GetString();
            pending = JsonSerializer.SerializeToElement(new {
                task_id = currentId,
                capability_id = task.GetProperty("capability_id"),
                intent_contract = task.GetProperty("intent_contract")
            });
            completion = promise = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try { return await promise.Task.WaitAsync(ct); }
        finally { lock (gate) { pending = null; completion = null; currentId = null; } }
    }
}
