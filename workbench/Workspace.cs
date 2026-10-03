using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cino.Workbench;

public sealed record WorkRecord(string Id, string Title, string Kind, string State,
    DateTimeOffset CreatedAt, string Prompt, string? Model = null, string? Artifact = null,
    string? Sha256 = null, string? Error = null, int? Tokens = null, double? Seconds = null,
    bool Truncated = false, string ExecutionMachine = "", bool TargetIdentityVerified = false);

public static class LocalPaths
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    // Walk every existing ancestor: a junction above the final directory is also a redirect.
    public static string Safe(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("请选择本机磁盘目录。");
        for (var current = full; current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("所选位置经过目录链接，请改用实际目录。");
        return full;
    }
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static async Task<string> HashFile(string path, CancellationToken cancellation)
    {
        await using var stream = new FileStream(Safe(path), FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation)).ToLowerInvariant();
    }
    public static void CreateNew(string path, byte[] bytes)
    {
        using var stream = new FileStream(Safe(path), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes); stream.Flush(true);
    }
}

public sealed class Workspace
{
    public string Root { get; }
    public Workspace(string root) { Root = LocalPaths.Safe(root); Directory.CreateDirectory(Root); }
    string RecordPath(string id)
    {
        if (!Regex.IsMatch(id, "^[a-f0-9]{32}$")) throw new InvalidDataException("任务标识无效。");
        return LocalPaths.Safe(Path.Combine(Root, id, "task.json"));
    }
    public WorkRecord Begin(string title, string kind, string prompt, string? model = null)
    {
        var record = new WorkRecord(Guid.NewGuid().ToString("N"), title, kind, "running", DateTimeOffset.UtcNow,
            prompt, model, ExecutionMachine: Environment.MachineName);
        Directory.CreateDirectory(Path.GetDirectoryName(RecordPath(record.Id))!);
        Write(record); return record;
    }
    void Write(WorkRecord record)
    {
        var path = RecordPath(record.Id);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        LocalPaths.CreateNew(temporary, JsonSerializer.SerializeToUtf8Bytes(record, LocalPaths.Json));
        File.Move(temporary, path, true);
    }
    public WorkRecord Fail(WorkRecord record, string state, string error)
    {
        if (state is not ("failed" or "cancelled")) throw new ArgumentException(nameof(state));
        var result = record with { State = state, Error = error }; Write(result); return result;
    }
    public WorkRecord Complete(WorkRecord record, string content, int? tokens = null, double? seconds = null, bool truncated = false)
    {
        if (string.IsNullOrWhiteSpace(content)) throw new InvalidDataException("没有可保存的内容。");
        if (Encoding.UTF8.GetByteCount(content) > 2 * 1024 * 1024) throw new InvalidDataException("成果超过 2 MB。");
        var path = LocalPaths.Safe(Path.Combine(Path.GetDirectoryName(RecordPath(record.Id))!, "result.md"));
        var bytes = Encoding.UTF8.GetBytes(content);
        LocalPaths.CreateNew(path, bytes);
        var result = record with { State = "completed", Artifact = "result.md", Sha256 = LocalPaths.Hash(bytes),
            Tokens = tokens, Seconds = seconds, Truncated = truncated };
        LocalPaths.CreateNew(path + ".receipt.json", JsonSerializer.SerializeToUtf8Bytes(new {
            schema = "cino.workbench.artifact.v1", task_id = record.Id, file = "result.md", size_bytes = bytes.Length,
            sha256 = result.Sha256, execution_machine = Environment.MachineName,
            target_machine_identity_verified = false, saved_at = DateTimeOffset.UtcNow
        }, LocalPaths.Json));
        Write(result); return result;
    }
    public IReadOnlyList<WorkRecord> History()
    {
        var records = new List<WorkRecord>();
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            var id = Path.GetFileName(dir);
            if (!Regex.IsMatch(id, "^[a-f0-9]{32}$")) continue;
            try {
                var path = RecordPath(id);
                if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) continue;
                var record = JsonSerializer.Deserialize<WorkRecord>(File.ReadAllText(path), LocalPaths.Json);
                if (record?.Id == id) records.Add(record.State == "running" ? record with { State = "interrupted" } : record);
            } catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return records.OrderByDescending(r => r.CreatedAt).Take(100).ToList();
    }
    public string ArtifactPath(WorkRecord record)
    {
        if (record.State != "completed" || record.Artifact != "result.md") throw new IOException("此任务没有已完成成果。");
        return LocalPaths.Safe(Path.Combine(Path.GetDirectoryName(RecordPath(record.Id))!, "result.md"));
    }
    public string ReadVerified(WorkRecord record)
    {
        var path = ArtifactPath(record);
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new IOException("成果大小异常。");
        var bytes = File.ReadAllBytes(path);
        if (LocalPaths.Hash(bytes) != record.Sha256) throw new IOException("成果已被外部修改，校验不一致；请从成果目录检查原文件。");
        return Encoding.UTF8.GetString(bytes);
    }

    public static string Inventory(string directory, CancellationToken cancellation)
    {
        var root = LocalPaths.Safe(directory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("目录不存在。");
        var lines = new List<string> { "# 本地文件清单", "", $"目录：{root}", $"时间：{DateTimeOffset.Now:O}", "",
            "只列文件名与大小，不读取内容；跳过隐藏文件、系统文件和目录链接。最多 2,000 个文件、30 层目录。", "" };
        var pending = new Stack<(string Path, int Depth)>(); pending.Push((root, 0));
        int count = 0, skipped = 0; bool limited = false;
        while (pending.Count > 0 && count < 2000)
        {
            cancellation.ThrowIfCancellationRequested();
            var (current, depth) = pending.Pop();
            try {
                foreach (var entry in new DirectoryInfo(LocalPaths.Safe(current)).EnumerateFileSystemInfos())
                {
                    cancellation.ThrowIfCancellationRequested();
                    if ((entry.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) != 0) { skipped++; continue; }
                    if ((entry.Attributes & FileAttributes.Directory) != 0) {
                        if (depth < 30) pending.Push((entry.FullName, depth + 1)); else { skipped++; limited = true; }
                    } else {
                        lines.Add($"- {Path.GetRelativePath(root, entry.FullName).Replace('\n', ' ').Replace('\r', ' ')} — {((FileInfo)entry).Length:N0} bytes");
                        count++; if (count >= 2000) { limited = true; break; }
                    }
                }
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
        }
        lines.Add($"\n列出 {count} 个文件；跳过 {skipped} 项。" + (limited ? "已达到扫描上限，此清单不完整。" : "扫描结束。"));
        return string.Join(Environment.NewLine, lines);
    }
}
