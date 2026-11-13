using System.IO;
namespace TaskbarGrouping.App;

/// <summary>グループに登録できるアプリ。起動していなくても、ピン留め・スタートメニューから集める。</summary>
sealed record AppEntry(string DisplayName, string ProcessName, bool Running, bool Pinned, string? LaunchPath = null)
{
    public override string ToString() =>
        $"{(Running ? "●" : "　")}{(Pinned ? "📌" : "　")} {DisplayName} ({ProcessName})";
}

static class AppCatalog
{
    static string Env(Environment.SpecialFolder f) => Environment.GetFolderPath(f);

    static Dictionary<string, AppEntry>? _cache;

    /// <summary>プロセス名からアプリ情報(起動パス等)を引く。初回のみ全体を走査する。</summary>
    public static AppEntry? Lookup(string processName)
    {
        if (_cache is null) Collect();
        return _cache!.TryGetValue(processName, out var e) ? e : null;
    }

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

        // 2.5) ストアアプリ(新しい Teams など)。PowerShell で列挙する
        AddStoreApps(map);

        // 3) 実行中のウィンドウ(ストアアプリ等、ショートカットから取れないものを補う)
        foreach (var w in NativeWindows.GetTaskbarWindows())
        {
            map.TryGetValue(w.ProcessName, out var e);
            map[w.ProcessName] = new AppEntry(e?.DisplayName ?? w.ProcessName, w.ProcessName, true, e?.Pinned ?? false, e?.LaunchPath);
        }

        _cache = map;
        return map.Values
            .OrderByDescending(a => a.Pinned).ThenByDescending(a => a.Running)
            .ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    const string StoreAppsScript = @"
$pk=@{}; Get-AppxPackage | ForEach-Object { $pk[$_.PackageFamilyName]=$_.InstallLocation }
Get-StartApps | ForEach-Object {
  $id=$_.AppID; $exe=''
  if ($id -match '^(.+)!(.+)$') {
    $loc=$pk[$matches[1]]; $aid=$matches[2]
    if ($loc -and (Test-Path ""$loc\AppxManifest.xml"")) {
      try { [xml]$m=Get-Content ""$loc\AppxManifest.xml""
        $a=@($m.Package.Applications.Application) | Where-Object { $_.Id -eq $aid } | Select-Object -First 1
        $exe=$a.Executable } catch {}
    }
    if ($exe) { ""$($_.Name)`t$id`t$exe"" }
  }
}";

    static void AddStoreApps(Dictionary<string, AppEntry> map)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " +
                Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(
                    "[Console]::OutputEncoding=[Text.Encoding]::UTF8;" + StoreAppsScript)))
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.TrimEnd('\r').Split('\t');
                if (parts.Length < 3) continue;
                var proc = Path.GetFileNameWithoutExtension(parts[2]);
                if (proc.Length == 0 || map.ContainsKey(proc)) continue;
                map[proc] = new AppEntry(parts[0], proc, false, false, @"shell:AppsFolder\" + parts[1]);
            }
        }
        catch (Exception ex) { Program.LogError("ストアアプリ列挙に失敗: " + ex.Message); }
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
                map[proc] = new AppEntry(name, proc, false, pinned || (old?.Pinned ?? false), pinned ? lnk : (old?.LaunchPath ?? lnk));
            }
            catch (Exception) { /* 壊れたショートカットは無視 */ }
        }
    }
}
