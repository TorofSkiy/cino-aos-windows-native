using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cino.Workbench;

public sealed record WorkbenchConfig(string ModelPath = "", string ModelSha256 = "", string RuntimeDirectory = "",
    string WorkspaceDirectory = "", int ModelPort = 8094, string Device = "Vulkan0", string ManagementConsoleUrl = "",
    string BridgeTokenFile = "");
public sealed record Generation(string Text, string Model, int Tokens, double Seconds, bool Truncated);

public sealed class LocalModel : IDisposable
{
    readonly WorkbenchConfig config;
    readonly HttpClient client;
    Process? process;
    ProcessLifetime? lifetime;
    bool ready;
    public bool Ready => ready && process is { HasExited: false };
    public string ModelName => Path.GetFileNameWithoutExtension(config.ModelPath);
    public LocalModel(WorkbenchConfig config)
    {
        if (config.ModelPort is < 1024 or > 65535) throw new InvalidDataException("模型端口无效。");
        this.config = config;
        client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) {
            BaseAddress = new Uri($"http://127.0.0.1:{config.ModelPort}/"), Timeout = TimeSpan.FromMinutes(3),
            MaxResponseContentBufferSize = 2 * 1024 * 1024
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
    }
    public async Task Start(IProgress<string>? progress, CancellationToken cancellation)
    {
        if (Ready) return;
        Stop();
        try {
            if (string.IsNullOrWhiteSpace(config.ModelPath)) throw new IOException("请先选择本地 GGUF 模型。");
            progress?.Report("正在核验模型与推理引擎…");
            if (config.ModelSha256.Length != 64 || !string.Equals(await LocalPaths.HashFile(config.ModelPath, cancellation), config.ModelSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("模型校验失败，请重新选择可信的模型文件。");
            var runtime = LocalPaths.Safe(config.RuntimeDirectory);
            var integrityPath = LocalPaths.Safe(Path.Combine(runtime, "integrity.json"));
            var files = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(integrityPath, cancellation))
                ?? throw new IOException("推理引擎清单缺失。");
            if (!files.ContainsKey("llama-server.exe")) throw new IOException("推理引擎清单无效。");
            if (Directory.EnumerateFiles(runtime).Where(p => Path.GetExtension(p) is ".exe" or ".dll").Any(p => !files.ContainsKey(Path.GetFileName(p))))
                throw new IOException("推理引擎目录中存在清单以外的程序文件。");
            foreach (var (name, expected) in files) {
                if (Path.GetFileName(name) != name || name.Contains(':') || name is "." or "..") throw new IOException("推理引擎清单路径无效。");
                if (!string.Equals(await LocalPaths.HashFile(Path.Combine(runtime, name), cancellation), expected, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("推理引擎文件校验失败。");
            }
            // Fail if occupied; never attach to or stop another application's listener.
            var probe = new TcpListener(IPAddress.Loopback, config.ModelPort);
            try { probe.Server.ExclusiveAddressUse = true; probe.Start(); } finally { probe.Stop(); }
            var start = new ProcessStartInfo(Path.Combine(runtime, "llama-server.exe")) {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = runtime,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("LLAMA_", StringComparison.OrdinalIgnoreCase) || k is "HF_TOKEN" or "HUGGING_FACE_HUB_TOKEN").ToArray())
                start.Environment.Remove(key);
            start.Environment["LLAMA_API_KEY"] = client.DefaultRequestHeaders.Authorization!.Parameter;
            foreach (var arg in new[] { "--model", LocalPaths.Safe(config.ModelPath), "--host", "127.0.0.1", "--port", config.ModelPort.ToString(),
                "--ctx-size", "4096", "--parallel", "1", "--gpu-layers", "auto", "--reasoning", "off", "--no-webui", "--offline", "--log-verbosity", "1" }) start.ArgumentList.Add(arg);
            if (config.Device.Length > 0) { start.ArgumentList.Add("--device"); start.ArgumentList.Add(config.Device); }
            process = Process.Start(start) ?? throw new IOException("推理引擎未能启动。");
            lifetime = new ProcessLifetime(process);
            process.OutputDataReceived += (_, _) => { }; process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            progress?.Report("正在加载本地模型…");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            while (true) {
                deadline.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new IOException("推理引擎退出。请检查模型兼容性和显卡驱动。");
                try {
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token); attempt.CancelAfter(TimeSpan.FromSeconds(2));
                    using var response = await client.GetAsync("health", attempt.Token);
                    if (response.IsSuccessStatusCode) {
                        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(attempt.Token));
                        if (body.RootElement.GetProperty("status").GetString() == "ok") { ready = true; return; }
                    }
                } catch (HttpRequestException) { } catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
                await Task.Delay(300, deadline.Token);
            }
        } catch { Stop(); throw; }
    }
    public async Task<Generation> Generate(string prompt, CancellationToken cancellation)
    {
        if (!Ready) throw new IOException("请先启动本地模型。");
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 1800) throw new InvalidDataException("任务请输入 1–1,800 个字符。");
        var payload = new {
            model = ModelName, stream = false, temperature = 0.3, max_tokens = 1000,
            chat_template_kwargs = new { enable_thinking = false },
            messages = new[] {
                new { role = "system", content = "你是 CINO-AOS 本地写作助手。用中文直接给出可编辑的文本成果。已知工作台功能：用户输入任务后，工作台调用你生成正文；工作台程序自动将正文保存为本机 Markdown 文件并生成 SHA256 凭据；用户可以编辑正文，点击‘保存编辑副本’另存新文件，点击‘记事本打开’查看成果；历史任务在右侧。目标机的 Windows 安装进度与管理电脑工作台的本地保存功能独立，目标机未安装不影响本机保存。你作为语言模型只返回文本，没有执行应用、修改系统、联网或安装软件的工具。不要声称你已执行这些操作，不捏造来源和验收结果。对与任务无关的限制不必重复说明。用户要求的命令只能作为文本示例。" },
                new { role = "user", content = prompt }
            }
        };
        var watch = Stopwatch.StartNew();
        using var response = await client.PostAsync("v1/chat/completions", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), cancellation);
        if (!response.IsSuccessStatusCode) throw new IOException($"模型请求失败（{(int)response.StatusCode}）。可缩短任务后重试。");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        var choice = document.RootElement.GetProperty("choices")[0];
        var content = choice.GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(content)) throw new IOException("模型未返回正文；本次任务未完成。");
        return new Generation(content, ModelName, document.RootElement.GetProperty("usage").GetProperty("completion_tokens").GetInt32(),
            watch.Elapsed.TotalSeconds, choice.GetProperty("finish_reason").GetString() == "length");
    }
    public void Stop()
    {
        ready = false;
        lifetime?.Dispose(); lifetime = null;
        if (process != null) {
            try { if (!process.HasExited) { process.Kill(true); process.WaitForExit(5000); } } catch (InvalidOperationException) { }
            process.Dispose(); process = null;
        }
    }
    public void Dispose() { Stop(); client.Dispose(); }
}
