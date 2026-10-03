using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cino.Native;

public sealed class ProtocolException(string code) : Exception(code);
public sealed class ProtocolClient : IDisposable
{
    readonly HttpClient client;
    readonly X509Certificate2? managementRoot;
    public Uri BaseUri { get; }
    public static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    public static Uri ValidateBase(string input, bool allowLoopback)
    {
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri)) throw new ProtocolException("invalid_management_url");
        bool loopback = uri.Host == "localhost" || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
        if (uri.Scheme != "https" && !(allowLoopback && loopback && uri.Scheme == "http"))
            throw new ProtocolException("management_https_required");
        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
            (uri.AbsolutePath.TrimEnd('/') is not ("" or "/api/host/v1")))
            throw new ProtocolException("invalid_management_url");
        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/api/host/v1");
    }

    public ProtocolClient(HostConfig config)
    {
        BaseUri = ValidateBase(config.ManagementUrl, config.AllowLoopbackForTesting);
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false };
        handler.SslOptions.EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
        if (config.ManagementRootCertificateFile.Length > 0 || config.ManagementRootCertificateSha256.Length > 0)
        {
            if (BaseUri.Scheme != "https" || !Path.IsPathFullyQualified(config.ManagementRootCertificateFile) ||
                !Regex.IsMatch(config.ManagementRootCertificateSha256, "^[a-f0-9]{64}$"))
                throw new ProtocolException("invalid_management_trust_config");
            var bytes = File.ReadAllBytes(config.ManagementRootCertificateFile);
            if (bytes.Length > 16384 || Hex(SHA256.HashData(bytes)) != config.ManagementRootCertificateSha256)
                throw new ProtocolException("management_root_integrity_mismatch");
            managementRoot = new X509Certificate2(bytes);
            if (!managementRoot.Extensions.OfType<X509BasicConstraintsExtension>().Any(e => e.CertificateAuthority))
                throw new ProtocolException("management_root_not_ca");
            // Scope private trust to this client. Normal hostname, validity and server EKU checks remain active.
            var policy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = X509RevocationMode.NoCheck, DisableCertificateDownloads = true };
            policy.CustomTrustStore.Add(managementRoot);
            policy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
            handler.SslOptions.CertificateChainPolicy = policy;
        }
        client = new HttpClient(handler)
        { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<JsonElement> Send(PendingRequest request, string token, string? hostId, CancellationToken ct)
    {
        var uri = new Uri(BaseUri + request.Path);
        var body = Encoding.UTF8.GetBytes(request.Body);
        if (body.Length > 256 * 1024) throw new ProtocolException("request_too_large");
        var digest = Hex(SHA256.HashData(body));
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var nonce = Hex(RandomNumberGenerator.GetBytes(24));
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), uri);
        message.Content = new ByteArrayContent(body);
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        message.Headers.Add("X-CINO-Protocol-Version", "1.0");
        message.Headers.Add("X-CINO-Timestamp", time);
        message.Headers.Add("X-CINO-Nonce", nonce);
        message.Headers.Add("Idempotency-Key", request.Key);
        message.Headers.Add("X-CINO-Content-SHA256", digest);
        if (hostId != null)
        {
            message.Headers.Add("X-CINO-Host-ID", hostId);
            var canonical = string.Join("\n", request.Method, uri.PathAndQuery, time, nonce, request.Key, digest);
            message.Headers.Add("X-CINO-Signature", Hex(HMACSHA256.HashData(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(canonical))));
        }
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new ProtocolException("management_http_" + (int)response.StatusCode);
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (memory.Length + read > 1024 * 1024) throw new ProtocolException("response_too_large");
            memory.Write(buffer, 0, read);
        }
        using var doc = JsonDocument.Parse(memory.ToArray());
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || Get(root, "schema") != "cino.api.response.v1")
            throw new ProtocolException("invalid_response_schema");
        return root.Clone();
    }

    public static string Get(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";

    public static void ValidateRegistration(JsonElement root)
    {
        if (Get(root, "state") is not ("host_registered" or "host_reenrolled") ||
            Get(root, "protocol_version") != "1.0" ||
            Get(root, "token_type") != "Bearer+HMAC-SHA256" ||
            !Regex.IsMatch(Get(root, "host_id"), "^host_[a-f0-9]{32}$") ||
            !Regex.IsMatch(Get(root, "credential_id"), "^cred_[a-f0-9]{32}$") ||
            !Regex.IsMatch(Get(root, "host_token"), "^cino_host_v1_[A-Za-z0-9_-]{16,}$") ||
            !DateTimeOffset.TryParse(Get(root, "expires_at"), out var expiry) || expiry <= DateTimeOffset.UtcNow)
            throw new ProtocolException("invalid_registration_receipt");
    }
    public void Dispose() { client.Dispose(); managementRoot?.Dispose(); }
}
