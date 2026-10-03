using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cino.Workbench;
public static class Diagnostics
{
    public static async Task<JsonElement> Read(int port)
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(5), MaxResponseContentBufferSize = 65536 };
        var json = await client.GetStringAsync("http://127.0.0.1:" + port + "/api/status");
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("schema", out var schema) ||
            schema.GetString() != "cino.native-host.status.v1") throw new InvalidDataException("invalid_native_status");
        return document.RootElement.Clone();
    }

    public static async Task<string> Export(int port, string directory)
    {
        var status = await Read(port);
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Evidence directory cannot be a reparse point");
        var id = Guid.NewGuid().ToString("N");
        var path = Path.Combine(directory, "diagnostics-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + id + ".json");
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
            schema = "cino.native-workbench.diagnostics.v1", task_id = id,
            created_at = DateTimeOffset.UtcNow, source = "local_user_session",
            capability = "local_diagnostics_export", management_task = false,
            target_machine_identity_verified = false, status
        }, new JsonSerializerOptions { WriteIndented = true }));
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { await file.WriteAsync(bytes); file.Flush(true); }
        var receipt = JsonSerializer.Serialize(new {
            schema = "cino.native-workbench.artifact.v1", artifact_name = Path.GetFileName(path),
            size_bytes = bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            verified_at = DateTimeOffset.UtcNow
        }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path + ".receipt.json", receipt, new UTF8Encoding(false));
        return path;
    }
}

