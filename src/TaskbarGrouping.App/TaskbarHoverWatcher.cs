using System.Threading;
using System.Runtime.InteropServices;
using TaskbarGrouping.Core;

namespace TaskbarGrouping.App;

/// <summary>カーソルがタスクバー上のグループのボタンに乗ったことを UI オートメーションで検出する。
/// 一定時間とどまったら onHover を呼ぶ(呼び出しはバックグラウンドスレッドから)。</summary>
sealed class TaskbarHoverWatcher
{
    readonly GroupConfig _config;
    readonly Action<GroupDefinition, System.Drawing.Rectangle> _onHover;
    public volatile bool Enabled = true;
    const int DelayMs = 300;
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

    /// <summary>カーソル下のタスクバー要素の名前と領域。子要素(画像など)に当たったら親をたどる。
    /// Windows 11 のタスクバーは XAML のため、.NET の UI オートメーションでは中のボタンが見えない。ネイティブの UIA (COM) を使う。</summary>
    (string? Name, System.Drawing.Rectangle Rect) NameAt(System.Drawing.Point pt)
    {
        _uia ??= (Uia.IUIAutomation)new Uia.CUIAutomation8();
        _walker ??= _uia.GetControlViewWalker(out var w) == 0 ? w : null;
        if (_uia.ElementFromPoint(new Uia.POINT { X = pt.X, Y = pt.Y }, out var el) != 0) return (null, System.Drawing.Rectangle.Empty);
        var diag = new System.Text.StringBuilder();
        for (int i = 0; i < 4 && el is not null; i++)
        {
            el.GetCurrentName(out var name);
            el.GetCurrentClassName(out var cls);
            diag.Append($" [{i}] name='{name}' class='{cls}'");
            if (!string.IsNullOrWhiteSpace(name))
            {
                el.GetCurrentBoundingRectangle(out var r);
                return (name, System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom));
            }
            if (_walker is null || _walker.GetParentElement(el, out var parent) != 0) break;
            el = parent;
        }
        // 名前が取れなかったときは、要素の情報を記録(変化したときだけ)
        var d = diag.ToString();
        if (d != _lastDiag) { _lastDiag = d; Program.LogError("タスクバー上の要素(名前なし):" + d); }
        return (null, System.Drawing.Rectangle.Empty);
    }

    Uia.IUIAutomation? _uia;
    Uia.IUIAutomationTreeWalker? _walker;

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

/// <summary>ネイティブ UI オートメーション(UIAutomationCore.dll)の最小限の COM 定義。
/// 未使用のメソッドは vtable の順番を合わせるためだけに置いている。</summary>
static class Uia
{
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [ComImport, Guid("e22ad333-b25f-460c-83d0-0581107395c9")]
    public class CUIAutomation8 { }

    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IUIAutomation
    {
        [PreserveSig] int CompareElements();            // 1
        [PreserveSig] int CompareRuntimeIds();          // 2
        [PreserveSig] int GetRootElement();             // 3
        [PreserveSig] int ElementFromHandle();          // 4
        [PreserveSig] int ElementFromPoint(POINT pt, out IUIAutomationElement element); // 5
        [PreserveSig] int GetFocusedElement();          // 6
        [PreserveSig] int GetRootElementBuildCache();   // 7
        [PreserveSig] int ElementFromHandleBuildCache();// 8
        [PreserveSig] int ElementFromPointBuildCache(); // 9
        [PreserveSig] int GetFocusedElementBuildCache();// 10
        [PreserveSig] int CreateTreeWalker();           // 11
        [PreserveSig] int GetControlViewWalker(out IUIAutomationTreeWalker walker); // 12
    }

    [ComImport, Guid("4042c624-389c-4afc-a630-9df854a541fc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IUIAutomationTreeWalker
    {
        [PreserveSig] int GetParentElement(IUIAutomationElement element, out IUIAutomationElement parent); // 1
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IUIAutomationElement
    {
        [PreserveSig] int SetFocus();                   // 1
        [PreserveSig] int GetRuntimeId();               // 2
        [PreserveSig] int FindFirst();                  // 3
        [PreserveSig] int FindAll();                    // 4
        [PreserveSig] int FindFirstBuildCache();        // 5
        [PreserveSig] int FindAllBuildCache();          // 6
        [PreserveSig] int BuildUpdatedCache();          // 7
        [PreserveSig] int GetCurrentPropertyValue();    // 8
        [PreserveSig] int GetCurrentPropertyValueEx();  // 9
        [PreserveSig] int GetCachedPropertyValue();     // 10
        [PreserveSig] int GetCachedPropertyValueEx();   // 11
        [PreserveSig] int GetCurrentPatternAs();        // 12
        [PreserveSig] int GetCachedPatternAs();         // 13
        [PreserveSig] int GetCurrentPattern();          // 14
        [PreserveSig] int GetCachedPattern();           // 15
        [PreserveSig] int GetCachedParent();            // 16
        [PreserveSig] int GetCachedChildren();          // 17
        [PreserveSig] int GetCurrentProcessId(out int pid);                 // 18
        [PreserveSig] int GetCurrentControlType(out int type);              // 19
        [PreserveSig] int GetCurrentLocalizedControlType([MarshalAs(UnmanagedType.BStr)] out string s); // 20
        [PreserveSig] int GetCurrentName([MarshalAs(UnmanagedType.BStr)] out string name);              // 21
        [PreserveSig] int GetCurrentAcceleratorKey([MarshalAs(UnmanagedType.BStr)] out string s);      // 22
        [PreserveSig] int GetCurrentAccessKey([MarshalAs(UnmanagedType.BStr)] out string s);           // 23
        [PreserveSig] int GetCurrentHasKeyboardFocus(out int b);            // 24
        [PreserveSig] int GetCurrentIsKeyboardFocusable(out int b);         // 25
        [PreserveSig] int GetCurrentIsEnabled(out int b);                   // 26
        [PreserveSig] int GetCurrentAutomationId([MarshalAs(UnmanagedType.BStr)] out string s);        // 27
        [PreserveSig] int GetCurrentClassName([MarshalAs(UnmanagedType.BStr)] out string s);           // 28
        [PreserveSig] int GetCurrentHelpText([MarshalAs(UnmanagedType.BStr)] out string s);            // 29
        [PreserveSig] int GetCurrentCulture(out int c);                     // 30
        [PreserveSig] int GetCurrentIsControlElement(out int b);            // 31
        [PreserveSig] int GetCurrentIsContentElement(out int b);            // 32
        [PreserveSig] int GetCurrentIsPassword(out int b);                  // 33
        [PreserveSig] int GetCurrentNativeWindowHandle(out IntPtr h);       // 34
        [PreserveSig] int GetCurrentItemType([MarshalAs(UnmanagedType.BStr)] out string s);            // 35
        [PreserveSig] int GetCurrentIsOffscreen(out int b);                 // 36
        [PreserveSig] int GetCurrentOrientation(out int o);                 // 37
        [PreserveSig] int GetCurrentFrameworkId([MarshalAs(UnmanagedType.BStr)] out string s);         // 38
        [PreserveSig] int GetCurrentIsRequiredForForm(out int b);           // 39
        [PreserveSig] int GetCurrentItemStatus([MarshalAs(UnmanagedType.BStr)] out string s);          // 40
        [PreserveSig] int GetCurrentBoundingRectangle(out RECT r);          // 41
    }
}
