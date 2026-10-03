using System.IO;

namespace Cino.Workbench;

// A live OS file handle excludes other sessions/processes using the same workspace.
// Crashes release the handle automatically; the empty lock file is safe to retain.
public sealed class WorkspaceLease : IDisposable
{
    readonly FileStream handle;
    WorkspaceLease(FileStream handle) => this.handle = handle;
    public static WorkspaceLease Acquire(string root)
    {
        var safe = LocalPaths.Safe(root);
        Directory.CreateDirectory(safe);
        var path = LocalPaths.Safe(Path.Combine(safe, ".workbench.lock"));
        try { return new WorkspaceLease(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
        catch (IOException ex) { throw new IOException("此成果目录已有工作台在运行。请切换到已打开的窗口，或稍后重试。", ex); }
    }
    public void Dispose() => handle.Dispose();
}

