using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskbarGrouping.App;

/// <summary>Win32 / Shell の薄いラッパー。ウィンドウ列挙と AppUserModelID の設定を行う。</summary>
static class NativeWindows
{
    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);

    const uint GW_OWNER = 4;
    const int GWL_EXSTYLE = -20;
    const int WS_EX_TOOLWINDOW = 0x80;

    public sealed record TopWindow(IntPtr Handle, string ProcessName, string Title);

    /// <summary>タスクバーに出るトップレベルウィンドウの一覧。</summary>
    public static List<TopWindow> GetTaskbarWindows()
    {
        var list = new List<TopWindow>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
            if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, sb.Capacity);
            if (sb.Length == 0) return true;
            GetWindowThreadProcessId(h, out var pid);
            try { list.Add(new TopWindow(h, Process.GetProcessById((int)pid).ProcessName, sb.ToString())); }
            catch (ArgumentException) { /* プロセス終了済み */ }
            catch (InvalidOperationException) { }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    // ---- AppUserModelID ----
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint c);
        [PreserveSig] int GetAt(uint i, out PropertyKey k);
        [PreserveSig] int GetValue(ref PropertyKey k, out PropVariant v);
        [PreserveSig] int SetValue(ref PropertyKey k, ref PropVariant v);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct PropertyKey { public Guid FmtId; public uint Pid; }

    [StructLayout(LayoutKind.Sequential)]
    struct PropVariant { public ushort Vt; ushort r1, r2, r3; public IntPtr P; public IntPtr P2; }

    [DllImport("shell32.dll")]
    static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, out IPropertyStore ppv);

    static PropertyKey AppIdKey = new() { FmtId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Pid = 5 };
    const ushort VT_EMPTY = 0, VT_LPWSTR = 31;

    /// <summary>ウィンドウの AppUserModelID を設定。appId=null で既定へ戻す。成功なら true。</summary>
    public static bool SetAppUserModelId(IntPtr hwnd, string? appId)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0) return false;
        var pv = new PropVariant();
        try
        {
            if (appId is not null) { pv.Vt = VT_LPWSTR; pv.P = Marshal.StringToCoTaskMemUni(appId); }
            else pv.Vt = VT_EMPTY;
            return store.SetValue(ref AppIdKey, ref pv) == 0 && store.Commit() == 0;
        }
        finally
        {
            if (pv.P != IntPtr.Zero) Marshal.FreeCoTaskMem(pv.P);
            Marshal.ReleaseComObject(store);
        }
    }
}
