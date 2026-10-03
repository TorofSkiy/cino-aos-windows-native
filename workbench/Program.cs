using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace Cino.Workbench;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try {
            string? Option(string name) { var i = Array.IndexOf(args, name); if (i < 0) return null; if (i + 1 >= args.Length || args[i + 1].StartsWith("--")) throw new ArgumentException("启动参数缺少值：" + name); return args[i + 1]; }
            var port = int.Parse(Option("--port") ?? "8093");
            if (port is < 1024 or > 65535) return 2;
            if (Option("--export-diagnostics") is string output) { Diagnostics.Export(port, Path.GetFullPath(output)).GetAwaiter().GetResult(); return 0; }
            var path = Option("--config") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CINO-AOS", "workbench.json");
            path = LocalPaths.Safe(path);
            var settings = File.Exists(path) ? JsonSerializer.Deserialize<WorkbenchConfig>(File.ReadAllText(path), LocalPaths.Json) ?? new() : new();
            var workspaceRoot = settings.WorkspaceDirectory.Length > 0 ? settings.WorkspaceDirectory : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CINO-AOS", "Workspace");
            using var workspaceLease = WorkspaceLease.Acquire(workspaceRoot);
            // Keep this lightweight desktop UI independent of inference GPU/driver state.
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            new Application().Run(new WorkbenchWindow(port, args.Contains("--management-preview"), path, settings)); return 0;
        } catch (Exception ex) {
            if (!args.Contains("--export-diagnostics")) MessageBox.Show(ex.Message, "CINO-AOS 启动失败");
            return 1;
        }
    }
}

public partial class WorkbenchWindow : Window
{
    readonly int port;
    readonly bool preview;
    readonly string configPath;
    WorkbenchConfig config;
    readonly Workspace workspace;
    LocalModel model;
    CancellationTokenSource? operation;
    WorkRecord? selected;
    bool dirty, loadingText;

    public WorkbenchWindow(int port, bool preview, string configPath, WorkbenchConfig settings)
    {
        this.port = port; this.preview = preview; this.configPath = LocalPaths.Safe(configPath);
        config = settings;
        if (config.RuntimeDirectory.Length == 0) config = config with { RuntimeDirectory = Path.Combine(AppContext.BaseDirectory, "runtime") };
        if (config.WorkspaceDirectory.Length == 0) config = config with { WorkspaceDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CINO-AOS", "Workspace") };
        workspace = new Workspace(config.WorkspaceDirectory); model = new LocalModel(config);
        InitializeComponent();
        if (!Uri.TryCreate(config.ManagementConsoleUrl, UriKind.Absolute, out var console) ||
            (console.Scheme != "https" && !(console.Scheme == "http" && console.IsLoopback))) {
            ManagementButton.IsEnabled = true; ManagementButton.Content = "查看管理连接";
        }
        Title += preview ? " · 管理电脑" : "";
        MachineScope.Text = preview ? "当前：管理电脑\n此窗口不代表目标机状态" : "当前：" + Environment.MachineName + "\nWindows 原生用户会话";
        ModelState.Text = config.ModelPath.Length == 0 ? "请先选择本地模型" : model.ModelName + "\n生成时自动加载";
        Loaded += async (_, _) => { RefreshHistory(); RefreshActions(); await RefreshHost(); StartNetworkBridge(); };
        Closing += (_, e) => {
            if (operation != null && MessageBox.Show(this, "任务仍在运行。关闭会停止任务，是否继续？", "关闭工作台", MessageBoxButton.YesNo) != MessageBoxResult.Yes) { e.Cancel = true; return; }
            if (!LeaveDraft()) { e.Cancel = true; return; } operation?.Cancel();
        };
        Closed += (_, _) => { StopNetworkBridge(); model.Dispose(); };
    }
    void Launch(ProcessStartInfo start) { try { Process.Start(start); } catch { ProgressText.Text = "应用未能打开；已保存成果仍在本地目录中。"; } }
    void FolderClick(object sender, RoutedEventArgs e) { var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = false }; start.ArgumentList.Add(LocalPaths.Safe(workspace.Root)); Launch(start); }
    async void ManagementClick(object sender, RoutedEventArgs e) {
        if (Uri.TryCreate(config.ManagementConsoleUrl, UriKind.Absolute, out var console) &&
            (console.Scheme == "https" || (console.Scheme == "http" && console.IsLoopback)))
            Launch(new ProcessStartInfo(console.AbsoluteUri) { UseShellExecute = true });
        else { await RefreshHost(); MessageBox.Show(this, "管理操作在管理电脑的 CINO 管理入口完成。\n本工作台在当前用户会话执行本地生成任务；设备连接状态显示在右下角。", "管理连接"); }
    }
    void NewTask(object sender, RoutedEventArgs e) { if (operation != null || !LeaveDraft()) return; selected = null; SetResult(""); TaskInput.Clear(); ResultState.Text = "等待你的第一个任务"; RefreshActions(); TaskInput.Focus(); }
    void PlanExample(object sender, RoutedEventArgs e) { if (operation == null) { TaskInput.Text = "请为一个小型软件项目起草一周实施计划，包含每日目标、交付物和验收条件。"; TaskInput.Focus(); } }
    void DocumentExample(object sender, RoutedEventArgs e) { if (operation == null) { TaskInput.Text = "请写一份简明的本地文件归档规范，包含命名规则、目录结构和每周检查步骤。"; TaskInput.Focus(); } }
    void CancelClick(object sender, RoutedEventArgs e) => operation?.Cancel();
    void ResultChanged(object sender, TextChangedEventArgs e) { if (!loadingText && SaveButton != null) { dirty = true; ResultState.Text = "有未保存的修改 · 保存为新副本"; RefreshActions(); } }
    void SetResult(string text) { loadingText = true; ResultEditor.Text = text; loadingText = false; dirty = false; }
    bool LeaveDraft() {
        if (!dirty) return true;
        var answer = MessageBox.Show(this, "成果有未保存的修改。是否保存为新副本？", "保存修改", MessageBoxButton.YesNoCancel);
        if (answer == MessageBoxResult.Cancel) return false;
        return answer == MessageBoxResult.No || SaveCopy();
    }
    void RefreshActions() { SaveButton.IsEnabled = operation == null && !string.IsNullOrWhiteSpace(ResultEditor.Text); OpenButton.IsEnabled = operation == null && selected?.State == "completed"; }
    void Busy(bool busy) {
        GenerateButton.IsEnabled = ConfigureButton.IsEnabled = InventoryButton.IsEnabled = LoadButton.IsEnabled = HistoryList.IsEnabled = !busy;
        TaskInput.IsReadOnly = ResultEditor.IsReadOnly = busy; CancelButton.IsEnabled = busy; Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed; RefreshActions();
    }
    static string StateLabel(WorkRecord record) => record.State switch {
        "completed" => record.Truncated ? "已保存 · 达到长度上限，请检查结尾" : "已保存 · 文件校验通过",
        "cancelled" => "已停止 · 未生成成果", "interrupted" => "上次运行中断 · 未确认完成", _ => "未完成 · " + record.Error
    };
    void RefreshHistory() {
        HistoryList.Items.Clear();
        foreach (var record in workspace.History()) {
            var panel = new StackPanel { Margin = new Thickness(5, 10, 5, 10) };
            panel.Children.Add(new TextBlock { Text = record.Title, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Honeydew });
            panel.Children.Add(new TextBlock { Text = record.CreatedAt.LocalDateTime.ToString("MM-dd HH:mm") + " · " + (record.State == "completed" ? "已保存" : record.State == "cancelled" ? "已停止" : record.State == "interrupted" ? "已中断" : "未完成"), FontSize = 10, Foreground = Brushes.DarkSeaGreen });
            HistoryList.Items.Add(new ListBoxItem { Content = panel, Tag = record, ToolTip = record.Title, Padding = new Thickness(2) });
        }
    }
    void HistoryChanged(object sender, SelectionChangedEventArgs e) {
        if (operation != null || HistoryList.SelectedItem is not ListBoxItem { Tag: WorkRecord record } || !LeaveDraft()) return;
        selected = record; TaskInput.Text = record.Prompt.Length <= 1800 ? record.Prompt : "";
        try { SetResult(record.State == "completed" ? workspace.ReadVerified(record) : ""); ResultState.Text = StateLabel(record); }
        catch (Exception ex) { SetResult(""); ResultState.Text = ex.Message; } RefreshActions();
    }
    async Task EnsureModel(CancellationToken cancellation) {
        await model.Start(new Progress<string>(s => { ModelState.Text = s; ProgressText.Text = s; }), cancellation);
        ModelState.Text = model.ModelName + "\n本机模型已就绪";
    }
    async void LoadClick(object sender, RoutedEventArgs e) {
        if (operation != null) return; operation = new CancellationTokenSource(); Busy(true);
        try { await EnsureModel(operation.Token); ProgressText.Text = "模型已就绪，可以生成成果。"; }
        catch (OperationCanceledException) { ProgressText.Text = "模型加载已停止。"; ModelState.Text = "模型未载入"; }
        catch (Exception ex) { ProgressText.Text = ex is System.Net.Sockets.SocketException ? "模型端口被占用；未连接或停止其他程序。" : ex.Message; ModelState.Text = "模型未载入"; }
        finally { operation.Dispose(); operation = null; Busy(false); }
    }
    async void GenerateClick(object sender, RoutedEventArgs e) {
        if (operation != null || !LeaveDraft()) return;
        var input = TaskInput.Text.Trim(); if (input.Length == 0) { ProgressText.Text = "请先描述需要完成的任务。"; TaskInput.Focus(); return; }
        operation = new CancellationTokenSource(); Busy(true); WorkRecord? record = null;
        try {
            record = workspace.Begin(input.Length > 28 ? input[..28] + "…" : input, "local_model_text", input, model.ModelName);
            selected = null; SetResult(""); ResultState.Text = "正在处理…";
            await EnsureModel(operation.Token); ProgressText.Text = "正在本机生成内容…";
            var generated = await model.Generate(input, operation.Token); operation.Token.ThrowIfCancellationRequested();
            selected = workspace.Complete(record, generated.Text, generated.Tokens, generated.Seconds, generated.Truncated);
            SetResult(generated.Text); ResultState.Text = StateLabel(selected);
            ProgressText.Text = $"已完成并保存 · {generated.Seconds:F1} 秒 · {generated.Tokens} 个输出 token";
        } catch (OperationCanceledException) {
            if (record != null) selected = workspace.Fail(record, "cancelled", "用户停止或请求超时"); ProgressText.Text = "任务已停止或超时，未保存为成功成果。"; ResultState.Text = "未完成";
        } catch (Exception ex) {
            if (record != null) selected = workspace.Fail(record, "failed", ex.Message); ProgressText.Text = ex.Message; ResultState.Text = "未完成";
        } finally { operation.Dispose(); operation = null; Busy(false); RefreshHistory(); }
    }
    async void InventoryClick(object sender, RoutedEventArgs e) {
        if (operation != null || !LeaveDraft()) return;
        var dialog = new OpenFolderDialog { Title = "选择需要生成文件清单的本地目录", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        operation = new CancellationTokenSource(); Busy(true); WorkRecord? record = null;
        try {
            record = workspace.Begin("文件清单 · " + Path.GetFileName(dialog.FolderName), "local_file_inventory", dialog.FolderName);
            ProgressText.Text = "正在读取目录…";
            var body = await Task.Run(() => Workspace.Inventory(dialog.FolderName, operation.Token), operation.Token);
            operation.Token.ThrowIfCancellationRequested(); selected = workspace.Complete(record, body); SetResult(body);
            ResultState.Text = StateLabel(selected); ProgressText.Text = "文件清单已保存；源目录没有被修改。";
        } catch (OperationCanceledException) { if (record != null) selected = workspace.Fail(record, "cancelled", "用户停止"); ProgressText.Text = "文件扫描已停止。"; }
        catch (Exception ex) { if (record != null) selected = workspace.Fail(record, "failed", ex.Message); ProgressText.Text = ex.Message; }
        finally { operation.Dispose(); operation = null; Busy(false); RefreshHistory(); }
    }
    void SaveClick(object sender, RoutedEventArgs e) => SaveCopy();
    bool SaveCopy() {
        WorkRecord? record = null;
        try {
            record = workspace.Begin("编辑副本 · " + (selected?.Title ?? "手写成果"), "edited_text", TaskInput.Text);
            selected = workspace.Complete(record, ResultEditor.Text); dirty = false; ResultState.Text = StateLabel(selected); ProgressText.Text = "已另存为新成果，原文件保留。"; RefreshHistory(); RefreshActions(); return true;
        } catch (Exception ex) { if (record != null) workspace.Fail(record, "failed", ex.Message); ProgressText.Text = ex.Message; return false; }
    }
    void OpenClick(object sender, RoutedEventArgs e) {
        if (selected == null || !LeaveDraft()) return;
        try { workspace.ReadVerified(selected); var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe")) { UseShellExecute = false }; start.ArgumentList.Add(workspace.ArtifactPath(selected)); Launch(start); }
        catch (Exception ex) { ProgressText.Text = ex.Message; }
    }
    async void ConfigureClick(object sender, RoutedEventArgs e) {
        if (operation != null) return;
        var picker = new OpenFileDialog { Title = "选择可信的本地 GGUF 模型", Filter = "GGUF 模型|*.gguf", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        operation = new CancellationTokenSource(); Busy(true);
        try {
            ProgressText.Text = "正在记录模型文件的校验值…";
            var hash = await LocalPaths.HashFile(picker.FileName, operation.Token);
            var next = config with { ModelPath = LocalPaths.Safe(picker.FileName), ModelSha256 = hash };
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            var temporary = configPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            LocalPaths.CreateNew(temporary, JsonSerializer.SerializeToUtf8Bytes(next, LocalPaths.Json)); File.Move(temporary, configPath, true);
            model.Dispose(); config = next; model = new LocalModel(config); ModelState.Text = model.ModelName + "\n已选择，尚未载入";
            ProgressText.Text = "选择已保存。校验值用于检测文件变化，不代表来源认证。";
        } catch (OperationCanceledException) { ProgressText.Text = "模型选择已停止。"; }
        catch (Exception ex) { ProgressText.Text = ex.Message; }
        finally { operation.Dispose(); operation = null; Busy(false); }
    }
    async void RefreshClick(object sender, RoutedEventArgs e) => await RefreshHost();
    async void DiagnosticsClick(object sender, RoutedEventArgs e) {
        try { var path = await Diagnostics.Export(port, Path.Combine(workspace.Root, "diagnostics")); ProgressText.Text = "诊断已保存：" + path; }
        catch { ProgressText.Text = "本机 Host 未响应，未生成诊断；本地任务仍可使用。"; }
    }
    async Task RefreshHost() {
        try { var state = await Diagnostics.Read(port); ServiceState.Text = "本机 Host 已响应\n" + (state.GetProperty("link_state").GetString() == "online" ? "管理心跳已连接" : "管理连接尚未建立") + (preview ? "\n来源：管理电脑" : ""); }
        catch { ServiceState.Text = "本机 Host 未连接\n本地任务可独立使用"; }
    }
}
