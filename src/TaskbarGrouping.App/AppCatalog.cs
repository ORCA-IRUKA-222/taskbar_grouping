using System.IO;
namespace TaskbarGrouping.App;

/// <summary>グループに登録できるアプリ。起動していなくても、ピン留め・スタートメニューから集める。</summary>
sealed record AppEntry(string DisplayName, string ProcessName, bool Running, bool Pinned)
{
    public override string ToString() =>
        $"{(Running ? "●" : "　")}{(Pinned ? "📌" : "　")} {DisplayName} ({ProcessName})";
}

static class AppCatalog
{
    static string Env(Environment.SpecialFolder f) => Environment.GetFolderPath(f);

    public static List<AppEntry> Collect()
    {
        var map = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);

        // 1) スタートメニュー(全ユーザー + 現ユーザー)
        foreach (var root in new[]
        {
            Path.Combine(Env(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\Start Menu\Programs"),
            Path.Combine(Env(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs"),
        })
            AddShortcuts(map, root, pinned: false);

        // 2) タスクバーにピン留めされたアプリ
        AddShortcuts(map,
            Path.Combine(Env(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"),
            pinned: true);

        // 3) 実行中のウィンドウ(ストアアプリ等、ショートカットから取れないものを補う)
        foreach (var w in NativeWindows.GetTaskbarWindows())
        {
            map.TryGetValue(w.ProcessName, out var e);
            map[w.ProcessName] = new AppEntry(e?.DisplayName ?? w.ProcessName, w.ProcessName, true, e?.Pinned ?? false);
        }

        return map.Values
            .OrderByDescending(a => a.Pinned).ThenByDescending(a => a.Running)
            .ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static void AddShortcuts(Dictionary<string, AppEntry> map, string dir, bool pinned)
    {
        if (!Directory.Exists(dir)) return;
        var shell = Type.GetTypeFromProgID("WScript.Shell");
        if (shell is null) return;
        dynamic sh = Activator.CreateInstance(shell)!;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories).ToList(); }
        catch (UnauthorizedAccessException) { return; }

        foreach (var lnk in files)
        {
            try
            {
                string target = sh.CreateShortcut(lnk).TargetPath;
                if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                var proc = Path.GetFileNameWithoutExtension(target);
                var name = Path.GetFileNameWithoutExtension(lnk);
                if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) continue;
                map.TryGetValue(proc, out var old);
                map[proc] = new AppEntry(name, proc, false, pinned || (old?.Pinned ?? false));
            }
            catch (Exception) { /* 壊れたショートカットは無視 */ }
        }
    }
}
