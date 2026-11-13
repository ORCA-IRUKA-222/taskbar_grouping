using TaskbarGrouping.Core;

namespace TaskbarGrouping.App;

/// <summary>定期的にウィンドウを走査し、グループ所属アプリの AppUserModelID を揃える。</summary>
sealed class GroupApplier
{
    readonly HashSet<IntPtr> _applied = new();
    public GroupConfig Config { get; set; }

    public GroupApplier(GroupConfig config) => Config = config;

    public void Tick()
    {
        if (!Config.MergeWindows) { if (_applied.Count > 0) ResetAll(); return; }
        var windows = NativeWindows.GetTaskbarWindows();
        foreach (var w in windows)
        {
            var g = Config.FindGroup(w.ProcessName);
            if (g is null || _applied.Contains(w.Handle)) continue;
            int hr = NativeWindows.SetAppUserModelId(w.Handle, g.AppUserModelId);
            Program.LogError($"適用 {w.ProcessName} '{w.Title}' -> {g.AppUserModelId} HRESULT=0x{hr:X8}");
            if (hr != 0) { _applied.Add(w.Handle); continue; } // 失敗は再試行しない(ログ連発防止)
            _applied.Add(w.Handle);
            NativeWindows.RefreshTaskbarButton(w.Handle);
        }
        _applied.IntersectWith(windows.Select(w => w.Handle));
    }

    /// <summary>全ウィンドウを元の(アプリ既定の)グループ化へ戻す。終了時に呼ぶ。</summary>
    public void ResetAll()
    {
        foreach (var h in _applied) NativeWindows.SetAppUserModelId(h, null);
        _applied.Clear();
    }

    /// <summary>設定変更後: いったん戻して次の Tick で再適用。</summary>
    public void Reapply() => ResetAll();
}
