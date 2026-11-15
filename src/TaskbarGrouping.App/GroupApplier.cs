using TaskbarGrouping.Core;

namespace TaskbarGrouping.App;

/// <summary>定期的にウィンドウを走査し、グループ所属アプリの個別タスクバーボタンを隠す。</summary>
sealed class GroupApplier
{
    readonly HashSet<IntPtr> _hidden = new();
    public GroupConfig Config { get; set; }

    public GroupApplier(GroupConfig config) => Config = config;

    public void Tick()
    {
        if (!Config.HideMemberButtons) { if (_hidden.Count > 0) ResetAll(); return; }
        var windows = NativeWindows.GetTaskbarWindows();
        foreach (var w in windows)
        {
            if (Config.FindGroup(w.ProcessName) is null) continue;
            // 最小化/復元でボタンが戻ることがあるため、毎回かけ直す
            int hr = NativeWindows.SetTaskbarButtonVisible(w.Handle, false);
            if (_hidden.Add(w.Handle))
                Program.LogError($"ボタンを隠す {w.ProcessName} '{w.Title}' HRESULT=0x{hr:X8}");
        }
        _hidden.IntersectWith(windows.Select(w => w.Handle));
    }

    /// <summary>隠したボタンをすべて元に戻す。終了時・設定変更時に呼ぶ。</summary>
    public void ResetAll()
    {
        foreach (var h in _hidden) NativeWindows.SetTaskbarButtonVisible(h, true);
        _hidden.Clear();
    }

    public void Reapply() => ResetAll();
}
