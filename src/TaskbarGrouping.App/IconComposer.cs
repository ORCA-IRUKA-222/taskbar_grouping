using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TaskbarGrouping.App;

/// <summary>複数アプリのアイコンを 2x2 に並べた 1 つの .ico を作る。</summary>
static class IconComposer
{
    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(long size, int flags, out IntPtr hbitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellItemImageFactory ppv);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

    /// <summary>.lnk / exe / shell:AppsFolder\... からアイコン画像を取得。</summary>
    static BitmapSource? GetIcon(string parsingName, int px)
    {
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var f);
            long size = ((long)px << 32) | (uint)px; // SIZE{cx,cy} を 64bit にパック(cx が下位)
            size = (long)(uint)px | ((long)px << 32);
            if (f.GetImage(size, 0, out var hbm) != 0) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally { DeleteObject(hbm); }
        }
        catch (Exception ex) { Program.LogError($"アイコン取得失敗 {parsingName}: {ex.Message}"); return null; }
    }

    /// <summary>最大 4 個のアイコンを 2x2 に並べて .ico を書き出す。成功なら true。</summary>
    public static bool Compose(IEnumerable<string> sources, string icoPath)
    {
        var icons = sources.Select(s => GetIcon(s, 128)).Where(i => i is not null).Take(4).ToList();
        if (icons.Count == 0) return false;

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            for (int i = 0; i < icons.Count; i++)
                dc.DrawImage(icons[i], new Rect((i % 2) * 128, (i / 2) * 128, 128, 128));
        }
        var rtb = new RenderTargetBitmap(256, 256, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        enc.Save(ms);
        var png = ms.ToArray();

        using var fs = File.Create(icoPath);
        using var w = new BinaryWriter(fs);
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)1);          // ICONDIR
        w.Write((byte)0); w.Write((byte)0); w.Write((byte)0); w.Write((byte)0); // 256x256
        w.Write((ushort)1); w.Write((ushort)32);
        w.Write(png.Length); w.Write(22);
        w.Write(png);
        return true;
    }
}
