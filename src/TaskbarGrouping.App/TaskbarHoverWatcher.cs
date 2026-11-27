using System.Threading;
using System.Windows.Automation;
using TaskbarGrouping.Core;

namespace TaskbarGrouping.App;

/// <summary>カーソルがタスクバー上のグループのボタンに乗ったことを UI オートメーションで検出する。
/// 一定時間とどまったら onHover を呼ぶ(呼び出しはバックグラウンドスレッドから)。</summary>
sealed class TaskbarHoverWatcher
{
    readonly GroupConfig _config;
    readonly Action<GroupDefinition, System.Drawing.Rectangle> _onHover;
    public volatile bool Enabled = true;
    const int DelayMs = 400;
    string? _lastError;
    string? _lastDiag;

    public TaskbarHoverWatcher(GroupConfig config, Action<GroupDefinition, System.Drawing.Rectangle> onHover)
    {
        _config = config; _onHover = onHover;
    }

    public void Start()
    {
        Program.LogError("ホバー検出を開始");
        new Thread(Loop) { IsBackground = true, Name = "TaskbarHoverWatcher" }.Start();
    }

    void Loop()
    {
        string? lastName = null;
        string? candidate = null;
        DateTime since = default;
        bool fired = false;

        while (true)
        {
            Thread.Sleep(120);
            try
            {
                GroupDefinition? hit = null;
                var rect = System.Drawing.Rectangle.Empty;
                var pt = System.Windows.Forms.Cursor.Position;
                // 作業領域の外 = タスクバー上のときだけ調べる(負荷対策)
                if (Enabled && !System.Windows.Forms.Screen.FromPoint(pt).WorkingArea.Contains(pt))
                {
                    var (name, r) = NameAt(pt);
                    if (name != lastName)
                    {
                        lastName = name;
                        if (!string.IsNullOrEmpty(name)) Program.LogError($"タスクバー上の要素: '{name}'");
                    }
                    hit = Match(name);
                    rect = r;
                }
                else lastName = null;

                if (hit is null) { candidate = null; fired = false; continue; }
                if (candidate != hit.Id) { candidate = hit.Id; since = DateTime.Now; fired = false; continue; }
                if (!fired && (DateTime.Now - since).TotalMilliseconds >= DelayMs)
                {
                    fired = true;
                    _onHover(hit, rect);
                }
            }
            catch (Exception ex)
            {
                // 同じエラーは 1 回だけ記録(Explorer 再起動中などは続行)
                var msg = ex.GetType().Name + ": " + ex.Message;
                if (msg != _lastError) { _lastError = msg; Program.LogError("ホバー検出エラー: " + ex); }
            }
        }
    }

    /// <summary>カーソル下のタスクバー要素の名前と領域。子要素(画像など)に当たったら親をたどる。</summary>
    (string? Name, System.Drawing.Rectangle Rect) NameAt(System.Drawing.Point pt)
    {
        var el = AutomationElement.FromPoint(new System.Windows.Point(pt.X, pt.Y));
        var diag = new System.Text.StringBuilder();
        for (int i = 0; i < 4 && el is not null; i++)
        {
            var name = el.Current.Name;
            diag.Append($" [{i}] name='{name}' class='{el.Current.ClassName}' id='{el.Current.AutomationId}' type={el.Current.ControlType.ProgrammaticName}");
            if (!string.IsNullOrWhiteSpace(name))
            {
                var b = el.Current.BoundingRectangle;
                return (name, new System.Drawing.Rectangle((int)b.X, (int)b.Y, (int)b.Width, (int)b.Height));
            }
            el = TreeWalker.ControlViewWalker.GetParent(el);
        }
        // 名前が取れなかったときは、要素の情報を記録(変化したときだけ)
        var d = diag.ToString();
        if (d != _lastDiag) { _lastDiag = d; Program.LogError("タスクバー上の要素(名前なし):" + d); }
        return (null, System.Drawing.Rectangle.Empty);
    }

    /// <summary>ボタン名がグループ名と一致するか。起動中は「名前 - 1 個の実行中ウィンドウ」のように後ろに付くことがある。</summary>
    GroupDefinition? Match(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        foreach (var g in _config.Groups.ToArray())
        {
            if (string.Equals(name, g.Name, StringComparison.CurrentCultureIgnoreCase) ||
                name.StartsWith(g.Name + " ", StringComparison.CurrentCultureIgnoreCase))
                return g;
        }
        return null;
    }
}
