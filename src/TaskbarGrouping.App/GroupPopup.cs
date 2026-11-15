using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using TaskbarGrouping.Core;

namespace TaskbarGrouping.App;

/// <summary>グループ用ピン留めボタンを押したときに出る一覧。起動中なら前面へ、未起動なら起動する。</summary>
sealed class GroupPopup : Window
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    bool _closing;
    void CloseOnce()
    {
        if (_closing) return;
        _closing = true;
        Dispatcher.BeginInvoke(new Action(Close)); // Deactivated 中に直接 Close すると例外になるため遅延
    }

    readonly GroupDefinition _group;

    void OpenApp(string proc, List<NativeWindows.TopWindow> wins)
    {
        Program.LogError($"ポップアップ押下: {proc} 起動中ウィンドウ数={wins.Count}");
        CloseOnce();
        if (wins.Count > 0) { NativeWindows.Activate(wins[0].Handle); return; }
        _group.LaunchPaths.TryGetValue(proc, out var path);
        if (!AppCatalog.IsUsable(path))
            path = AppCatalog.Resolve(proc, _group.DisplayNames.GetValueOrDefault(proc));
        if (path is null)
        {
            System.Windows.MessageBox.Show($"{proc} の起動方法が見つかりません。設定画面で一度グループから外し、一覧から追加し直してください。", "Taskbar Grouping");
            return;
        }
        Program.LogError($"起動: {proc} -> {path} (存在={File.Exists(path)})");
        try
        {
            if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Program.LogError("起動失敗: " + ex);
            System.Windows.MessageBox.Show($"{proc} を起動できませんでした。\n{ex.Message}", "Taskbar Grouping");
        }
    }

    public GroupPopup(GroupConfig config, GroupDefinition group, System.Drawing.Point? clickPx = null)
    {
        _group = group;
        Program.LogError($"ポップアップ表示: {group.Name} メンバー={group.ProcessNames.Count} 表示={config.PopupView}");
        Title = group.Name;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;

        bool iconsOnly = config.PopupView == PopupViewMode.IconsOnly;
        var running = NativeWindows.GetTaskbarWindows();
        var root = new StackPanel { Margin = new Thickness(6) };
        root.Children.Add(new TextBlock { Text = group.Name, FontWeight = FontWeights.Bold, Margin = new Thickness(6, 2, 6, 6) });

        System.Windows.Controls.Panel items = iconsOnly
            ? new WrapPanel { MaxWidth = 296 }
            : new StackPanel { MinWidth = 240 };
        foreach (var proc in group.ProcessNames)
        {
            var wins = running.Where(w => string.Equals(w.ProcessName, proc, StringComparison.OrdinalIgnoreCase)).ToList();
            group.DisplayNames.TryGetValue(proc, out var disp);
            var name = disp ?? proc;
            group.LaunchPaths.TryGetValue(proc, out var lp);
            var icon = lp is null ? null : IconComposer.GetIconCached(lp, 64);

            System.Windows.Controls.Button b;
            if (iconsOnly)
            {
                // アイコンのみ: 起動中は右下に ● を重ねる
                var g = new Grid { Width = 40, Height = 40 };
                g.Children.Add(new System.Windows.Controls.Image { Source = icon, Width = 36, Height = 36 });
                if (wins.Count > 0)
                    g.Children.Add(new System.Windows.Shapes.Ellipse
                    {
                        Width = 10, Height = 10, Fill = System.Windows.Media.Brushes.Black,
                        Stroke = System.Windows.Media.Brushes.White, StrokeThickness = 1.5,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                        VerticalAlignment = System.Windows.VerticalAlignment.Bottom,
                    });
                b = new System.Windows.Controls.Button { Content = g, Padding = new Thickness(6), Margin = new Thickness(2), ToolTip = name };
            }
            else
            {
                var label = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                label.Children.Add(new TextBlock { Text = wins.Count > 0 ? "●" : "", Width = 18, VerticalAlignment = System.Windows.VerticalAlignment.Center });
                label.Children.Add(new System.Windows.Controls.Image { Source = icon, Width = 24, Height = 24, Margin = new Thickness(0, 0, 8, 0) });
                label.Children.Add(new TextBlock { Text = name, VerticalAlignment = System.Windows.VerticalAlignment.Center });
                b = new System.Windows.Controls.Button
                {
                    Content = label, HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
                    Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 1, 0, 1)
                };
            }
            b.Click += (_, _) => OpenApp(proc, wins);
            items.Children.Add(b);
        }
        root.Children.Add(items);
        Content = new Border { BorderBrush = System.Windows.Media.Brushes.Gray, BorderThickness = new Thickness(1), Child = root };

        Deactivated += (_, _) => CloseOnce();
        Loaded += (_, _) =>
        {
            // クリックされた位置(タスクバーのボタン)の真上に表示。座標は物理ピクセルで計算し DIP へ変換する
            double scale = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
            var click = clickPx ?? System.Windows.Forms.Cursor.Position;
            var wa = System.Windows.Forms.Screen.FromPoint(click).WorkingArea;
            double w = ActualWidth * scale, h = ActualHeight * scale, gap = 8 * scale;
            double left = Math.Max(wa.Left + gap, Math.Min(click.X - w / 2, wa.Right - w - gap));
            bool taskbarBelow = click.Y > wa.Top + wa.Height / 2;
            double top = taskbarBelow ? wa.Bottom - h - gap : wa.Top + gap;
            // 物理ピクセルで直接配置する(DIP 変換による二重スケールを避ける)
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            SetWindowPos(hwnd, IntPtr.Zero, (int)left, (int)top, 0, 0, 0x0001 | 0x0004 | 0x0010); // NOSIZE|NOZORDER|NOACTIVATE
            Program.LogError($"ポップアップ位置: click=({click.X},{click.Y}) scale={scale} -> ({(int)left},{(int)top})");
            Activate();
        };
    }
}

static class ShortcutMaker
{
    public static string FolderPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskbarGrouping", "Shortcuts");

    /// <summary>グループ用ショートカットを作る。タスクバーへのピン留めは、作成後にユーザーが右クリックで行う。</summary>
    public static string Create(GroupDefinition g)
    {
        Directory.CreateDirectory(FolderPath);
        var safe = string.Concat(g.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var lnk = Path.Combine(FolderPath, safe + ".lnk");
        var shell = Type.GetTypeFromProgID("WScript.Shell")!;
        dynamic sh = Activator.CreateInstance(shell)!;
        var sc = sh.CreateShortcut(lnk);
        sc.TargetPath = Environment.ProcessPath;
        sc.Arguments = $"--group {g.Id}";
        var sources = g.ProcessNames
            .Select(pn => g.LaunchPaths.TryGetValue(pn, out var lp) ? lp : AppCatalog.Lookup(pn)?.LaunchPath)
            .Where(x => x is not null).Select(x => x!).ToList();
        var ico = Path.Combine(FolderPath, g.Id + ".ico");
        bool ok = IconComposer.Compose(sources, ico);
        sc.IconLocation = (ok ? ico : Environment.ProcessPath) + ",0";
        sc.Description = g.Name;
        sc.Save();
        int hr = NativeWindows.SetShortcutAppId(lnk, g.AppUserModelId);
        Program.LogError($"ショートカット作成 {lnk} AUMID HRESULT=0x{hr:X8}");
        return lnk;
    }
}
