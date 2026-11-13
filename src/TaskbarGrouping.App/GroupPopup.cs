using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using TaskbarGrouping.Core;

namespace TaskbarGrouping.App;

/// <summary>グループ用ピン留めボタンを押したときに出る一覧。起動中なら前面へ、未起動なら起動する。</summary>
sealed class GroupPopup : Window
{
    bool _closing;
    void CloseOnce()
    {
        if (_closing) return;
        _closing = true;
        Dispatcher.BeginInvoke(new Action(Close)); // Deactivated 中に直接 Close すると例外になるため遅延
    }

    public GroupPopup(GroupDefinition group, System.Drawing.Point? clickPx = null)
    {
        Program.LogError($"ポップアップ表示: {group.Name} メンバー={group.ProcessNames.Count}");
        Title = group.Name;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        MinWidth = 220;

        var running = NativeWindows.GetTaskbarWindows();
        var panel = new StackPanel { Margin = new Thickness(6) };
        panel.Children.Add(new TextBlock { Text = group.Name, FontWeight = FontWeights.Bold, Margin = new Thickness(6, 2, 6, 6) });

        foreach (var proc in group.ProcessNames)
        {
            var wins = running.Where(w => string.Equals(w.ProcessName, proc, StringComparison.OrdinalIgnoreCase)).ToList();
            group.DisplayNames.TryGetValue(proc, out var disp);
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(new TextBlock { Text = wins.Count > 0 ? "●" : "", Width = 18 }); // 起動中だけ印を付ける
            label.Children.Add(new TextBlock { Text = disp ?? proc });
            var b = new System.Windows.Controls.Button
            {
                Content = label, HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 1, 0, 1)
            };
            b.Click += (_, _) =>
            {
                Program.LogError($"ポップアップ押下: {proc} 起動中ウィンドウ数={wins.Count}");
                CloseOnce();
                if (wins.Count > 0) { NativeWindows.Activate(wins[0].Handle); return; }
                if (!group.LaunchPaths.TryGetValue(proc, out var path))
                    path = AppCatalog.Lookup(proc)?.LaunchPath;
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
            };
            panel.Children.Add(b);
        }
        Content = new Border { BorderBrush = System.Windows.Media.Brushes.Gray, BorderThickness = new Thickness(1), Child = panel };

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
            Left = left / scale; Top = top / scale;
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
