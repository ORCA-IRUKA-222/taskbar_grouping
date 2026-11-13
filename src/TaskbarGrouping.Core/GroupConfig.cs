using System.Text.Json;

namespace TaskbarGrouping.Core;

/// <summary>ユーザー定義のグループ。ProcessNames は拡張子なしのプロセス名(例: "Discord")。</summary>
public sealed class GroupDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新しいグループ";
    public List<string> ProcessNames { get; set; } = new();

    /// <summary>同じ AppUserModelID を持つウィンドウは 1 つのタスクバーボタンにまとまる。</summary>
    public string AppUserModelId => "TaskbarGrouping." + Id;
}

public sealed class GroupConfig
{
    public List<GroupDefinition> Groups { get; set; } = new();

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TaskbarGrouping", "groups.json");

    public static GroupConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new GroupConfig();
        try { return JsonSerializer.Deserialize<GroupConfig>(File.ReadAllText(path)) ?? new GroupConfig(); }
        catch (JsonException) { return new GroupConfig(); }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
    }

    /// <summary>プロセス名に対応するグループ。未所属なら null。</summary>
    public GroupDefinition? FindGroup(string processName) =>
        Groups.FirstOrDefault(g => g.ProcessNames.Any(
            p => string.Equals(p, processName, StringComparison.OrdinalIgnoreCase)));
}
