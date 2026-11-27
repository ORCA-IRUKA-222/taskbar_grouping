using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using TaskbarGrouping.Core;

namespace TaskbarGrouping.App;

/// <summary>グループのボタンに対する一覧。クリック時、またはカーソルを合わせたとき(ホバー)に表示する。
/// 起動中のアプリは前面へ、未起動なら起動する。複数ウィンドウを持つアプリは、ウィンドウを選べる。</summary>
sealed class GroupPopup : Window
{
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    readonly GroupConfig _config;
    readonly GroupDefinition _group;
    readonly System.Drawing.Point _anchor;            // 表示の基準点(物理ピクセル)。ボタンの中心またはクリック位置
    readonly System.Drawing.Rectangle? _anchorRect;   // ホバー時のボタン領域(物理ピクセル)
    List<NativeWindows.TopWindow> _running = new();
    string? _expanded;                                // ウィンドウ一覧を開いているプロセス名
    bool _closing;
    int _outsideTicks;

    public string GroupId => _group.Id;
    public bool IsClosing => _closing;
    /// <summary>ホバーで開き、まだクリックされていない状態。</summary>
    public bool IsHoverMode { get; private set; }

    public GroupPopup(GroupConfig config, GroupDefinition group, System.Drawing.Point anchor,
                      System.Drawing.Rectangle? anchorRect = null, bool hover = false)
    {
        _config = config; _group = group; _anchor = anchor; _anchorRect = anchorRect; IsHoverMode = hover;
        Program.LogError($"ポップアップ表示: {group.Name} ホバー={hover} 表示={config.PopupView}");
        Title = group.Name;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = !hover;
        SizeToContent = SizeToContent.WidthAndHeight;
        AllowsTransparency = true;                       // 出現アニメーションで背景を透かすため
        Background = System.Windows.Media.Brushes.Transparent;

        _running = NativeWindows.GetTaskbarWindows();
        Build();

        Activated += (_, _) => IsHoverMode = false;
        Deactivated += (_, _) => CloseOnce();
        SizeChanged += (_, _) => Place();
        Loaded += (_, _) => { Place(); PrepareAnimation(); if (!hover) Activate(); };
        ContentRendered += (_, _) => AnimateIn(); // 最初の描画が終わってから動かす(Loaded 直後だと見えないうちに終わるため)

        if (hover)
        {
            // ボタンからもポップアップからもカーソルが離れたら閉じる(通常のタスクバーと同じ挙動)
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            t.Tick += (_, _) =>
            {
                if (!IsHoverMode || _closing) { t.Stop(); return; }
                if (IsCursorInside()) _outsideTicks = 0;
                else if (++_outsideTicks >= 3) { t.Stop(); CloseOnce(); }
            };
            t.Start();
        }
    }

    public void CloseOnce()
    {
        if (_closing) return;
        _closing = true;
        Dispatcher.BeginInvoke(new Action(Close)); // Deactivated 中に直接 Close すると例外になるため遅延
    }

    bool IsCursorInside()
    {
        var c = System.Windows.Forms.Cursor.Position;
        GetWindowRect(new System.Windows.Interop.WindowInteropHelper(this).Handle, out var r);
        var area = System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        if (_anchorRect is { } a) area = System.Drawing.Rectangle.Union(area, a); // 間のすき間も含める
        area.Inflate(4, 4);
        return area.Contains(c);
    }

    bool TaskbarBelow()
    {
        var wa = System.Windows.Forms.Screen.FromPoint(_anchor).WorkingArea;
        return _anchor.Y > wa.Top + wa.Height / 2;
    }

    System.Windows.Media.TranslateTransform? _slide;

    /// <summary>出現前の状態(タスクバー側にずらして透明)にしておく。</summary>
    void PrepareAnimation()
    {
        if (Content is not FrameworkElement fe) return;
        _slide = new System.Windows.Media.TranslateTransform(0, TaskbarBelow() ? 40 : -40);
        fe.RenderTransform = _slide;
        fe.Opacity = 0;
    }

    /// <summary>通常のタスクバーと同じように、タスクバー側からせり上がって表示する。</summary>
    void AnimateIn()
    {
        if (Content is not FrameworkElement fe || _slide is null) return;
        var dur = TimeSpan.FromMilliseconds(250);
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        _slide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, dur) { EasingFunction = ease });
        fe.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, dur));
    }

    void Place()
    {
        if (!IsLoaded) return;
        // ボタンの真上(タスクバーが上なら真下)に表示。物理ピクセルで直接配置する
        double scale = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
        var wa = System.Windows.Forms.Screen.FromPoint(_anchor).WorkingArea;
        double w = ActualWidth * scale, h = ActualHeight * scale, gap = 8 * scale;
        double left = Math.Max(wa.Left + gap, Math.Min(_anchor.X - w / 2, wa.Right - w - gap));
        bool taskbarBelow = _anchor.Y > wa.Top + wa.Height / 2;
        double top = taskbarBelow ? wa.Bottom - h - gap : wa.Top + gap;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, IntPtr.Zero, (int)left, (int)top, 0, 0, 0x0001 | 0x0004 | 0x0010); // NOSIZE|NOZORDER|NOACTIVATE
    }

    List<NativeWindows.TopWindow> WindowsOf(string proc) =>
        _running.Where(w => string.Equals(w.ProcessName, proc, StringComparison.OrdinalIgnoreCase)).ToList();

    System.Windows.Media.ImageSource? IconOf(string proc) =>
        _group.LaunchPaths.TryGetValue(proc, out var lp) && AppCatalog.IsUsable(lp) ? IconComposer.GetIconCached(lp, 64) : null;

    void Build()
    {
        bool iconsOnly = _config.PopupView == PopupViewMode.IconsOnly;
        var root = new StackPanel { Margin = new Thickness(6) };
        root.Children.Add(new TextBlock { Text = _group.Name, FontWeight = FontWeights.Bold, Margin = new Thickness(6, 2, 6, 6) });

        System.Windows.Controls.Panel items = iconsOnly ? new WrapPanel { MaxWidth = 296 } : new StackPanel { MinWidth = 260 };
        foreach (var proc in _group.ProcessNames)
        {
            var wins = WindowsOf(proc);
            var name = _group.DisplayNames.GetValueOrDefault(proc) ?? proc;
            var icon = IconOf(proc);
            bool expanded = _expanded == proc && wins.Count > 1;

            System.Windows.Controls.Button b;
            if (iconsOnly)
            {
                // アイコンのみ: 起動中は右下に ●、複数ウィンドウなら数字を重ねる
                var g = new Grid { Width = 40, Height = 40 };
                g.Children.Add(new System.Windows.Controls.Image { Source = icon, Width = 36, Height = 36 });
                if (wins.Count > 0)
                    g.Children.Add(new Border
                    {
                        MinWidth = 12, Height = 12, CornerRadius = new CornerRadius(6),
                        Background = System.Windows.Media.Brushes.Black,
                        BorderBrush = System.Windows.Media.Brushes.White, BorderThickness = new Thickness(1.5),
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                        VerticalAlignment = System.Windows.VerticalAlignment.Bottom,
                        Child = wins.Count > 1
                            ? new TextBlock { Text = wins.Count.ToString(), FontSize = 8, Foreground = System.Windows.Media.Brushes.White,
                                              HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Margin = new Thickness(2, -1, 2, 0) }
                            : null,
                    });
                b = new System.Windows.Controls.Button { Content = g, Padding = new Thickness(6), Margin = new Thickness(2), ToolTip = name };
                if (expanded) b.BorderBrush = System.Windows.Media.Brushes.DodgerBlue;
            }
            else
            {
                var label = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                label.Children.Add(new TextBlock
                {
                    Text = wins.Count == 0 ? "" : wins.Count == 1 ? "●" : $"●{wins.Count}",
                    Width = 26, VerticalAlignment = System.Windows.VerticalAlignment.Center
                });
                label.Children.Add(new System.Windows.Controls.Image { Source = icon, Width = 24, Height = 24, Margin = new Thickness(0, 0, 8, 0) });
                label.Children.Add(new TextBlock { Text = name, VerticalAlignment = System.Windows.VerticalAlignment.Center });
                if (wins.Count > 1)
                    label.Children.Add(new TextBlock { Text = expanded ? "  ▲" : "  ▼", Foreground = System.Windows.Media.Brushes.Gray, VerticalAlignment = System.Windows.VerticalAlignment.Center });
                b = new System.Windows.Controls.Button
                {
                    Content = label, HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
                    Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 1, 0, 1)
                };
            }
            b.Click += (_, _) => OnAppClick(proc, wins);
            items.Children.Add(b);

            // リスト表示: 展開中のアプリの直下にウィンドウ一覧
            if (expanded && !iconsOnly) foreach (var w in wins) items.Children.Add(WindowButton(w, indent: 34));
        }
        root.Children.Add(items);

        // アイコン表示: 展開中のアプリのウィンドウ一覧を下にまとめて出す
        if (iconsOnly && _expanded is { } ep && WindowsOf(ep) is { Count: > 1 } ew)
        {
            root.Children.Add(new TextBlock
            {
                Text = (_group.DisplayNames.GetValueOrDefault(ep) ?? ep) + " のウィンドウ",
                Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(6, 8, 6, 2)
            });
            foreach (var w in ew) root.Children.Add(WindowButton(w, indent: 0));
        }

        Content = new Border { BorderBrush = System.Windows.Media.Brushes.Gray, BorderThickness = new Thickness(1), Background = System.Windows.Media.Brushes.White, Child = root };
    }

    System.Windows.Controls.Button WindowButton(NativeWindows.TopWindow w, double indent)
    {
        var title = w.Title.Length > 60 ? w.Title[..57] + "..." : w.Title;
        var b = new System.Windows.Controls.Button
        {
            Content = new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 320 },
            ToolTip = w.Title,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
            Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(indent, 1, 0, 1),
            Background = System.Windows.Media.Brushes.WhiteSmoke,
        };
        b.Click += (_, _) =>
        {
            Program.LogError($"ウィンドウ選択: {w.ProcessName} '{w.Title}'");
            CloseOnce();
            NativeWindows.Activate(w.Handle);
        };
        return b;
    }

    void OnAppClick(string proc, List<NativeWindows.TopWindow> wins)
    {
        Program.LogError($"ポップアップ押下: {proc} 起動中ウィンドウ数={wins.Count}");
        if (wins.Count > 1)
        {
            // 複数ウィンドウ: どれを開くか選べるように一覧を開閉する
            _expanded = _expanded == proc ? null : proc;
            _running = NativeWindows.GetTaskbarWindows();
            Build();
            return;
        }
        CloseOnce();
        if (wins.Count == 1) { NativeWindows.Activate(wins[0].Handle); return; }
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
