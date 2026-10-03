using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;

namespace Cino.Workbench;

public partial class WorkbenchWindow
{
    DispatcherTimer? networkTimer;
    HttpClient? networkClient;
    bool networkBusy;
    int networkTick;
    void StartNetworkBridge()
    {
        if (preview || config.BridgeTokenFile.Length == 0 || !File.Exists(config.BridgeTokenFile)) return;
        var token = File.ReadAllText(LocalPaths.Safe(config.BridgeTokenFile)).Trim();
        if (!Regex.IsMatch(token, "^[a-f0-9]{64}$")) return;
        networkClient = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) {
            BaseAddress = new Uri("http://127.0.0.1:" + port), Timeout = TimeSpan.FromSeconds(5),
            MaxResponseContentBufferSize = 256 * 1024
        };
        networkClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        networkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        networkTimer.Tick += async (_, _) => await NetworkTick();
        networkTimer.Start();
    }
    void StopNetworkBridge() { networkTimer?.Stop(); networkClient?.Dispose(); }
    async Task NetworkTick()
    {
        if (networkBusy || networkClient == null || operation != null || dirty) return;
        networkBusy = true;
        try {
            if (++networkTick % 10 == 0) await RefreshHost();
            using var response = await networkClient.PostAsync("/bridge/poll", new StringContent("{}"));
            if (!response.IsSuccessStatusCode) return;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("task", out var task) || task.ValueKind != JsonValueKind.Object) return;
            var id = task.GetProperty("task_id").GetString()!;
            if (!Regex.IsMatch(id, "^task_[a-f0-9]{32}$")) return;
            var journal = LocalPaths.Safe(Path.Combine(workspace.Root, "_network"));
            Directory.CreateDirectory(journal);
            var receipt = LocalPaths.Safe(Path.Combine(journal, id + ".json"));
            var started = LocalPaths.Safe(Path.Combine(journal, id + ".started"));
            string result;
            if (File.Exists(receipt)) result = await File.ReadAllTextAsync(receipt);
            else {
                object outcome;
                if (File.Exists(started)) outcome = new { outcome = "failed", error = "interrupted_execution_not_repeated" };
                else {
                    LocalPaths.CreateNew(started, Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.ToString("O")));
                    var cap = task.GetProperty("capability_id").GetString();
                    WorkRecord? record = null;
                    operation = new CancellationTokenSource(TimeSpan.FromSeconds(75));
                    Busy(true);
                    try {
                        if (cap is "cino.workbench.show.v1" or "cino.workbench.close.v1") {
                            Show(); WindowState = WindowState.Normal; Activate();
                            outcome = new { outcome = "succeeded", output = new { machine = Environment.MachineName, action = cap == "cino.workbench.close.v1" ? "workbench_closing" : "workbench_shown" } };
                        } else if (cap == "cino.workbench.generate.v1") {
                            var prompt = task.GetProperty("intent_contract").GetProperty("parameters").GetProperty("prompt").GetString()!;
                            if (prompt.Length is < 1 or > 1800) throw new IOException("invalid_prompt");
                            record = workspace.Begin("管理任务 · " + (prompt.Length > 24 ? prompt[..24] : prompt), "managed_model_text", prompt, model.ModelName);
                            TaskInput.Text = prompt; SetResult(""); ResultState.Text = "正在执行管理平台任务…";
                            await EnsureModel(operation.Token);
                            var generated = await model.Generate(prompt, operation.Token);
                            selected = workspace.Complete(record, generated.Text, generated.Tokens, generated.Seconds, generated.Truncated);
                            SetResult(generated.Text); ResultState.Text = StateLabel(selected);
                            outcome = new { outcome = "succeeded", output = new { text = generated.Text, model = generated.Model,
                                tokens = generated.Tokens, seconds = generated.Seconds, truncated = generated.Truncated,
                                local_record_id = selected.Id, machine = Environment.MachineName } };
                        } else throw new IOException("unsupported_interactive_capability");
                    } catch (Exception ex) {
                        if (record != null) workspace.Fail(record, "failed", ex.GetType().Name);
                        ResultState.Text = "管理任务未完成，已保留失败记录";
                        outcome = new { outcome = "failed", error = ex.GetType().Name };
                    } finally { operation.Dispose(); operation = null; Busy(false); RefreshHistory(); }
                }
                result = JsonSerializer.Serialize(outcome, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                LocalPaths.CreateNew(receipt, Encoding.UTF8.GetBytes(result));
            }
            using var delivered = await networkClient.PostAsync("/bridge/result/" + id, new StringContent(result, Encoding.UTF8, "application/json"));
            ProgressText.Text = delivered.IsSuccessStatusCode ? "管理任务已执行，结果正在同步回管理电脑。" : "已保留任务结果，等待管理连接恢复。";
            if (delivered.IsSuccessStatusCode && task.GetProperty("capability_id").GetString() == "cino.workbench.close.v1") Close();
        } catch (HttpRequestException) { }
        catch (OperationCanceledException) { }
        catch (IOException) { ProgressText.Text = "网络任务记录暂不可用，本地成果仍保留。"; }
        catch (JsonException) { ProgressText.Text = "管理任务格式无效，未执行。"; }
        catch (InvalidOperationException) { ProgressText.Text = "管理任务内容无效，未执行。"; }
        catch (KeyNotFoundException) { ProgressText.Text = "管理任务字段不完整，未执行。"; }
        finally { networkBusy = false; }
    }
}
