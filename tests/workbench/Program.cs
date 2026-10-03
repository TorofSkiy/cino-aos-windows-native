using Cino.Workbench;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

var root = Path.GetFullPath(args[0]);
Directory.CreateDirectory(root);
var run = Path.Combine(root, "workbench-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(run);
var results = new List<object>();
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
void Reject(Action action) { bool rejected = false; try { action(); } catch (Exception ex) when (ex is IOException or InvalidDataException) { rejected = true; } Assert(rejected, "Expected refusal"); }
async Task Check(string name, Func<Task> test) {
    try { await test(); results.Add(new { name, status = "passed" }); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { results.Add(new { name, status = "failed", error = ex.Message }); throw; }
}
var workspace = new Workspace(Path.Combine(run, "tasks"));
try {
    await Check("One active workbench per workspace; released lease can reopen", () => {
        var leasedRoot = Path.Combine(run, "leased-workspace");
        using (var first = WorkspaceLease.Acquire(leasedRoot)) {
            Reject(() => { using var duplicate = WorkspaceLease.Acquire(leasedRoot); });
            using var independent = WorkspaceLease.Acquire(Path.Combine(run, "other-workspace"));
        }
        using var reopened = WorkspaceLease.Acquire(leasedRoot);
        return Task.CompletedTask;
    });
    WorkRecord? original = null;
    await Check("Unicode artifact, receipt and durable history round-trip", () => {
        original = workspace.Complete(workspace.Begin("中文测试", "manual_text", "测试输入"), "# 原始成果\n这是实际文件。\n");
        var reopened = new Workspace(workspace.Root).History().Single();
        Assert(reopened.Id == original.Id && workspace.ReadVerified(reopened).Contains("实际文件"), "History/artifact mismatch");
        using var receipt = JsonDocument.Parse(File.ReadAllText(workspace.ArtifactPath(original) + ".receipt.json"));
        Assert(receipt.RootElement.GetProperty("sha256").GetString() == original.Sha256, "Receipt hash mismatch"); return Task.CompletedTask;
    });
    await Check("New edited copy preserves original; duplicate completion cannot overwrite", () => {
        var path = workspace.ArtifactPath(original!); var before = File.ReadAllBytes(path);
        workspace.Complete(workspace.Begin("编辑副本", "edited_text", ""), "修改后的内容");
        Reject(() => workspace.Complete(original!, "should not overwrite"));
        Assert(File.ReadAllBytes(path).SequenceEqual(before), "Original changed"); return Task.CompletedTask;
    });
    await Check("External artifact modification detected", () => {
        File.AppendAllText(workspace.ArtifactPath(original!), "外部变动"); Reject(() => workspace.ReadVerified(original!)); return Task.CompletedTask;
    });
    await Check("Task path traversal and unexpected artifact names rejected", () => {
        Reject(() => workspace.ArtifactPath(original! with { Id = "../../outside" }));
        Reject(() => workspace.ArtifactPath(original! with { Artifact = "../../outside" })); return Task.CompletedTask;
    });
    await Check("Failed, cancelled and interrupted tasks are not reported as completed", () => {
        var a = workspace.Begin("失败", "test", ""); var b = workspace.Begin("停止", "test", ""); var c = workspace.Begin("中断", "test", "");
        workspace.Fail(a, "failed", "expected test failure"); workspace.Fail(b, "cancelled", "test cancelled");
        var history = new Workspace(workspace.Root).History();
        Assert(history.Single(r => r.Id == c.Id).State == "interrupted", "Crash not distinguished");
        Assert(history.Where(r => new[] { a.Id, b.Id, c.Id }.Contains(r.Id)).All(r => r.Artifact == null && r.State != "completed"), "False completion"); return Task.CompletedTask;
    });
    await Check("Empty output rejected without creating successful artifact", () => {
        var empty = workspace.Begin("空白", "test", ""); Reject(() => workspace.Complete(empty, " "));
        Assert(!File.Exists(Path.Combine(workspace.Root, empty.Id, "result.md")), "Empty result written"); return Task.CompletedTask;
    });
    await Check("Inventory reads filenames, observes cancellation and reports limits", () => {
        var input = Path.Combine(run, "inventory"); Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "中文.txt"), "private contents should not be in inventory");
        var listing = Workspace.Inventory(input, CancellationToken.None);
        Assert(listing.Contains("中文.txt") && !listing.Contains("private contents"), "Inventory leaked contents");
        bool cancelled = false; try { Workspace.Inventory(input, new CancellationToken(true)); } catch (OperationCanceledException) { cancelled = true; }
        Assert(cancelled, "Cancellation ignored");
        for (int i = 0; i < 2001; i++) File.WriteAllText(Path.Combine(input, i + ".txt"), "");
        Assert(Workspace.Inventory(input, CancellationToken.None).Contains("此清单不完整"), "Scan cap not reported"); return Task.CompletedTask;
    });
    if (args.Length > 2) await Check("Junction ancestors are rejected for workspace and model access", () => {
        Assert((File.GetAttributes(args[2]) & FileAttributes.ReparsePoint) != 0, "Fixture is not a junction");
        Reject(() => new Workspace(Path.Combine(args[2], "nested"))); return Task.CompletedTask;
    });
    await Check("Wrong model hash fails before any engine process starts", async () => {
        var path = Path.Combine(run, "dummy.gguf"); File.WriteAllText(path, "fixture");
        using var model = new LocalModel(new WorkbenchConfig(ModelPath: path, ModelSha256: new string('0', 64)));
        bool failed = false; try { await model.Start(null, CancellationToken.None); } catch (IOException) { failed = true; }
        Assert(failed && !model.Ready, "Hash failure not enforced");
    });
    if (args.Length > 1) {
        var config = JsonSerializer.Deserialize<WorkbenchConfig>(File.ReadAllText(args[1]), LocalPaths.Json)!;
        await Check("Occupied port rejected without disrupting other listener", async () => {
            var listener = new TcpListener(IPAddress.Loopback, config.ModelPort); listener.Start();
            try {
                using var model = new LocalModel(config); bool blocked = false;
                try { await model.Start(null, CancellationToken.None); } catch (SocketException) { blocked = true; }
                Assert(blocked && listener.Server.IsBound && !model.Ready, "Existing listener was not protected");
            } finally { listener.Stop(); }
        });
        await Check("Real local 9B inference creates a verified artifact; owned process stops", async () => {
            using (var model = new LocalModel(config)) {
                await model.Start(null, CancellationToken.None); Assert(model.Ready, "Not ready");
                var output = await model.Generate("请用中文写三条本地文件归档建议，每条一句话。", CancellationToken.None);
                Assert(output.Text.Length > 20 && output.Tokens > 0, "No real output");
                var task = workspace.Complete(workspace.Begin("真实模型验收", "local_model_text", "三条本地文件归档建议", output.Model), output.Text, output.Tokens, output.Seconds, output.Truncated);
                Assert(workspace.ReadVerified(task) == output.Text, "Saved output differs");
                File.WriteAllText(Path.Combine(run, "inference.json"), JsonSerializer.Serialize(new { output.Model, output.Tokens, output.Seconds, output.Truncated, task_id = task.Id, text = output.Text }, LocalPaths.Json));
            }
            bool released = false;
            for (var attempt = 0; attempt < 40; attempt++) {
                var listener = new TcpListener(IPAddress.Loopback, config.ModelPort);
                try { listener.Start(); released = true; break; } catch (SocketException) { await Task.Delay(100); }
                finally { listener.Stop(); }
            }
            Assert(released, "Model listener was not released within 4 seconds");
        });
    }
} finally {
    File.WriteAllText(Path.Combine(run, "report.json"), JsonSerializer.Serialize(new { schema = "cino.workbench.tests.v1", management_computer_only = true, target_installed = false, at = DateTimeOffset.UtcNow, results }, LocalPaths.Json));
    Console.WriteLine("Evidence: " + run);
}
