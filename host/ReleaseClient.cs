using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cino.Native;

public sealed class ReleaseClient(HostConfig config, StateStore store)
{
    public bool Configured => config.UpdateInbox.Length > 0 && File.Exists(config.ReleasePublicKeyFile);
    static string Safe(string path)
    {
        path = Path.GetFullPath(path);
        if (path.StartsWith(@"\\")) throw new IOException("local_update_path_required");
        for (var p = path; p != null; p = Path.GetDirectoryName(p))
            if ((Directory.Exists(p) || File.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("update_path_redirected");
        return path;
    }
    public object Status()
    {
        if (!Configured) return new { state = "not_configured" };
        try {
            var path = Safe(Path.Combine(Path.GetDirectoryName(config.UpdateInbox)!, "status.json"));
            if (!File.Exists(path)) return new { state = "ready" };
            if (new FileInfo(path).Length > 16384) return new { state = "invalid_receipt" };
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            return json.RootElement.Clone();
        } catch { return new { state = "receipt_unavailable" }; }
    }
    public async Task<object> Stage(ProtocolClient client, string id, CancellationToken ct)
    {
        if (!Configured || !Regex.IsMatch(id, "^release_[0-9]{6}$")) throw new IOException("update_not_configured");
        async Task<JsonElement> Request(string action, object body) {
            var response = await client.Send(new("release", "POST", "/releases/" + action, JsonSerializer.Serialize(body),
                "release_" + Guid.NewGuid().ToString("N")), store.Value.HostToken!, store.Value.HostId, ct);
            if (ProtocolClient.Get(response, "host_id") != store.Value.HostId) throw new IOException("release_host_mismatch");
            return response;
        }
        var receipt = await Request("manifest", new { schema = "cino.native.release-request.v1", release_id = id });
        var manifest = Convert.FromBase64String(receipt.GetProperty("manifest_base64").GetString()!);
        var signature = Convert.FromBase64String(receipt.GetProperty("signature_base64").GetString()!);
        if (manifest.Length > 65536 || signature.Length > 1024) throw new IOException("invalid_release_envelope");
        using var rsa = RSA.Create(); rsa.FromXmlString(File.ReadAllText(Safe(config.ReleasePublicKeyFile)));
        if (!rsa.VerifyData(manifest, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw new IOException("release_signature_invalid");
        using var doc = JsonDocument.Parse(manifest);
        var m = doc.RootElement;
        if (m.GetProperty("schema").GetString() != "cino.native.release.v1" || m.GetProperty("release_id").GetString() != id ||
            m.GetProperty("platform").GetString() != "win-x64" || m.GetProperty("expires_at").GetDateTimeOffset() <= DateTimeOffset.UtcNow) throw new IOException("release_metadata_invalid");
        var size = m.GetProperty("archive").GetProperty("size_bytes").GetInt64();
        var digest = m.GetProperty("archive").GetProperty("sha256").GetString();
        if (size is < 1 or > 419430400 || !Regex.IsMatch(digest ?? "", "^[a-f0-9]{64}$")) throw new IOException("release_archive_invalid");
        Directory.CreateDirectory(Safe(config.UpdateInbox));
        var archive = Safe(Path.Combine(config.UpdateInbox, id + ".zip"));
        var temporary = archive + "." + Guid.NewGuid().ToString("N") + ".partial";
        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true)) {
            long offset = 0;
            while (offset < size) {
                var chunk = await Request("chunk", new { schema = "cino.native.release-request.v1", release_id = id, offset });
                var bytes = Convert.FromBase64String(chunk.GetProperty("data_base64").GetString()!);
                if (bytes.Length is < 1 or > 524288 || bytes.Length + offset > size || chunk.GetProperty("offset").GetInt64() != offset ||
                    ProtocolClient.Hex(SHA256.HashData(bytes)) != chunk.GetProperty("sha256").GetString()) throw new IOException("release_chunk_invalid");
                await output.WriteAsync(bytes, ct); offset += bytes.Length;
            }
            output.Flush(true);
        }
        using (var input = File.OpenRead(temporary))
            if (ProtocolClient.Hex(await SHA256.HashDataAsync(input, ct)) != digest) throw new IOException("release_integrity_mismatch");
        File.Move(temporary, archive, true);
        var request = Safe(Path.Combine(config.UpdateInbox, id + ".request.json"));
        var requestTemp = request + "." + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(requestTemp, JsonSerializer.Serialize(new {
            manifest_base64 = Convert.ToBase64String(manifest), signature_base64 = Convert.ToBase64String(signature)
        }), ct);
        File.Move(requestTemp, request, true);
        return new { state = "staged", release_id = id, version = m.GetProperty("version").GetString(), sha256 = digest,
            notice = "Download and signature verified. Installation result is reported separately by the update broker." };
    }
}
