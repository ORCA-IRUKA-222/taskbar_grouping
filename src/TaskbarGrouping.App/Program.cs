using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using TaskbarGrouping.Core;

namespace TaskbarGrouping.App;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogError(e.ExceptionObject);
        try { Run(args); }
        catch (Exception ex)
        {
            LogError(ex);
            System.Windows.MessageBox.Show(ex.ToString(), "Taskbar Grouping エラー");
        }
    }

    internal static void LogError(object? ex)
    {
        try
        {
            var dir = Path.GetDirectoryName(GroupConfig.DefaultPath)!;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"), $"{DateTime.Now:s} {ex}\n");
        }
        catch (Exception) { }
    }

    static void Run(string[] args)
    {
        LogError("起動開始 args=" + string.Join(" ", args));
        // 多重起動防止: 2 つ目は既存プロセスに「設定を開け」と通知して終了
        using var mutex = new Mutex(true, @"Local\TaskbarGrouping.SingleInstance", out var first);
        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\TaskbarGrouping.Show");
        if (!first) { showEvent.Set(); return; }
        var config = GroupConfig.Load();
        var applier = new GroupApplier(config);
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) => applier.Tick();
        timer.Start();

        SettingsWindow? win = null;
        void ShowSettings()
        {
            if (win is { IsLoaded: true }) { win.Activate(); return; }
            win = new SettingsWindow(config, () => { config.Save(); applier.Reapply(); });
            win.Show();
        }

        var waiter = new Thread(() =>
        {
            while (showEvent.WaitOne()) app.Dispatcher.Invoke(ShowSettings);
        }) { IsBackground = true };
        waiter.Start();

        var tray = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Taskbar Grouping",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip()
        };
        tray.ContextMenuStrip.Items.Add("設定を開く", null, (_, _) => ShowSettings());
        var auto = new ToolStripMenuItem("Windows 起動時に自動実行") { Checked = AutoStart.IsEnabled };
        auto.Click += (_, _) => { AutoStart.Set(!AutoStart.IsEnabled); auto.Checked = AutoStart.IsEnabled; };
        tray.ContextMenuStrip.Items.Add(auto);
        tray.ContextMenuStrip.Items.Add("終了(グループ化を解除)", null, (_, _) =>
        {
            timer.Stop(); applier.ResetAll(); tray.Visible = false; app.Shutdown();
        });
        tray.DoubleClick += (_, _) => ShowSettings();

        if (!args.Contains("--minimized")) ShowSettings();
        app.Run();
    }
}

static class AutoStart
{
    const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "TaskbarGrouping";

    public static bool IsEnabled
    {
        get
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath);
            return k?.GetValue(ValueName) is not null;
        }
    }

    public static void Set(bool on)
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath, writable: true)!;
        if (on) k.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --minimized");
        else k.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

/// <summary>グループの作成・削除と、実行中アプリのグループ割り当てを行う設定画面(コードのみで構築)。</summary>
sealed class SettingsWindow : Window
{
    readonly GroupConfig _config;
    readonly Action _changed;
    readonly System.Windows.Controls.ListBox _groups = new System.Windows.Controls.ListBox { Width = 180, DisplayMemberPath = nameof(GroupDefinition.Name) };
    readonly System.Windows.Controls.ListBox _members = new();
    readonly System.Windows.Controls.ListBox _running = new();

    public SettingsWindow(GroupConfig config, Action changed)
    {
        _config = config; _changed = changed;
        Title = "Taskbar Grouping"; Width = 720; Height = 420;

        var newName = new System.Windows.Controls.TextBox { Margin = new Thickness(0, 0, 0, 4) };
        var add = new System.Windows.Controls.Button { Content = "グループ追加" };
        add.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(newName.Text)) return;
            _config.Groups.Add(new GroupDefinition { Name = newName.Text.Trim() });
            newName.Clear(); RefreshGroups(); _changed();
        };
        var del = new System.Windows.Controls.Button { Content = "選択グループ削除", Margin = new Thickness(0, 4, 0, 0) };
        del.Click += (_, _) =>
        {
            if (_groups.SelectedItem is GroupDefinition g) { _config.Groups.Remove(g); RefreshGroups(); _changed(); }
        };
        var left = new DockPanel { Margin = new Thickness(8) };
        var leftTop = new StackPanel(); leftTop.Children.Add(newName); leftTop.Children.Add(add);
        DockPanel.SetDock(leftTop, Dock.Top); left.Children.Add(leftTop);
        var leftBottom = new StackPanel(); leftBottom.Children.Add(del);
        DockPanel.SetDock(leftBottom, Dock.Bottom); left.Children.Add(leftBottom);
        left.Children.Add(_groups);

        var toGroup = new System.Windows.Controls.Button { Content = "◀ 選択グループへ追加" };
        toGroup.Click += (_, _) =>
        {
            if (_groups.SelectedItem is GroupDefinition g && _running.SelectedItem is AppEntry w)
            {
                foreach (var other in _config.Groups) other.ProcessNames.RemoveAll(
                    p => string.Equals(p, w.ProcessName, StringComparison.OrdinalIgnoreCase));
                g.ProcessNames.Add(w.ProcessName); RefreshMembers(); _changed();
            }
        };
        var remove = new System.Windows.Controls.Button { Content = "選択アプリを外す" };
        remove.Click += (_, _) =>
        {
            if (_groups.SelectedItem is GroupDefinition g && _members.SelectedItem is string p)
            { g.ProcessNames.Remove(p); RefreshMembers(); _changed(); }
        };
        var manual = new System.Windows.Controls.TextBox { Margin = new Thickness(0, 12, 0, 2), ToolTip = "例: ms-teams" };
        var manualAdd = new System.Windows.Controls.Button { Content = "プロセス名を手入力で追加" };
        manualAdd.Click += (_, _) =>
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(manual.Text.Trim());
            if (_groups.SelectedItem is GroupDefinition g && name.Length > 0 && !g.ProcessNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            { g.ProcessNames.Add(name); manual.Clear(); RefreshMembers(); _changed(); }
        };
        var refresh = new System.Windows.Controls.Button { Content = "アプリ一覧を更新" };
        refresh.Click += (_, _) => RefreshRunning();

        var grid = new Grid { Margin = new Thickness(8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8) };
        mid.Children.Add(toGroup); mid.Children.Add(remove); mid.Children.Add(refresh); mid.Children.Add(manual); mid.Children.Add(manualAdd);
        Grid.SetColumn(_members, 0); Grid.SetColumn(mid, 1); Grid.SetColumn(_running, 2);
        grid.Children.Add(_members); grid.Children.Add(mid); grid.Children.Add(_running);

        var root = new DockPanel();
        DockPanel.SetDock(left, Dock.Left); root.Children.Add(left); root.Children.Add(grid);
        Content = root;

        _groups.SelectionChanged += (_, _) => RefreshMembers();
        RefreshGroups(); RefreshRunning();
    }

    void RefreshGroups() { _groups.ItemsSource = null; _groups.ItemsSource = _config.Groups; }
    void RefreshMembers() => _members.ItemsSource =
        (_groups.SelectedItem as GroupDefinition)?.ProcessNames.ToList();
    void RefreshRunning() => _running.ItemsSource = AppCatalog.Collect();
}
