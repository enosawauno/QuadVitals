# Install the QuadVitals icon assets from the provided source files.
#
# The icon is not drawn by this script: it copies the supplied .ico/.icns and explodes every
# embedded size to assets/icon/png/ so the WPF window / store art / design handoff can use them.
# Run it again if the source icon changes.
#
#   powershell -File scripts\make-icons.ps1 -SourceIco <path.ico> -SourceIcns <path.icns>
#
# ASCII only in code; PS 5.1 misreads BOM-less UTF-8 as GBK.
param(
    [string]$SourceIco = '',
    [string]$SourceIcns = ''
)
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($SourceIco)) { throw 'missing -SourceIco <path.ico>' }

$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root 'assets\icon'
$pngDir = Join-Path $outDir 'png'
New-Item -ItemType Directory -Path $pngDir -Force | Out-Null

if (-not (Test-Path -LiteralPath $SourceIco)) { throw "missing source ico: $SourceIco" }
Copy-Item -LiteralPath $SourceIco -Destination (Join-Path $outDir 'quadvitals.ico') -Force
"ico  -> assets\icon\quadvitals.ico"

if (Test-Path -LiteralPath $SourceIcns) {
    Copy-Item -LiteralPath $SourceIcns -Destination (Join-Path $outDir 'quadvitals.icns') -Force
    "icns -> assets\icon\quadvitals.icns"
}

# Explode the embedded PNG frames (the .ico is PNG-compressed, the .icns carries bigger ones).
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

public static class IconFrames
{
    public static string Extract(string icoPath, string icnsPath, string outDir)
    {
        StringBuilder log = new StringBuilder();
        Dictionary<int, byte[]> frames = new Dictionary<int, byte[]>();

        byte[] ico = File.ReadAllBytes(icoPath);
        int count = BitConverter.ToUInt16(ico, 4);
        for (int i = 0; i < count; i++)
        {
            int entry = 6 + i * 16;
            int size = BitConverter.ToInt32(ico, entry + 8);
            int offset = BitConverter.ToInt32(ico, entry + 12);
            byte[] payload = new byte[size];
            Buffer.BlockCopy(ico, offset, payload, 0, size);
            int w = ico[entry] == 0 ? 256 : ico[entry];
            if (IsPng(payload)) frames[w] = payload;
        }

        if (icnsPath != null && File.Exists(icnsPath))
        {
            byte[] icns = File.ReadAllBytes(icnsPath);
            int pos = 8;
            while (pos + 8 <= icns.Length)
            {
                int len = (icns[pos + 4] << 24) | (icns[pos + 5] << 16) | (icns[pos + 6] << 8) | icns[pos + 7];
                if (len <= 8 || pos + len > icns.Length) break;
                byte[] payload = new byte[len - 8];
                Buffer.BlockCopy(icns, pos + 8, payload, 0, payload.Length);
                if (IsPng(payload) && payload.Length > 24)
                {
                    int w = (payload[16] << 24) | (payload[17] << 16) | (payload[18] << 8) | payload[19];
                    if (w >= 16 && w <= 1024 && (!frames.ContainsKey(w) || frames[w].Length < payload.Length))
                        frames[w] = payload;
                }
                pos += len;
            }
        }

        foreach (KeyValuePair<int, byte[]> pair in frames)
        {
            string file = Path.Combine(outDir, "icon-" + pair.Key + ".png");
            File.WriteAllBytes(file, pair.Value);
            log.AppendLine("png  -> " + pair.Key + "x" + pair.Key + " (" + pair.Value.Length + " bytes)");
        }
        return log.ToString();
    }

    static bool IsPng(byte[] data)
    {
        return data.Length > 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47;
    }
}
'@

# ---------------------------------------------------------------- tray variant ----
# The shell renders tray icons at ~16 px, so the macOS plate only shrinks the glyph. The tray
# variant keeps the glyph alone, cropped tight and scaled to fill the frame (about 2x bigger).
$masterPng = Join-Path $pngDir 'icon-1024.png'
$trayPath = Join-Path $outDir 'tray.ico'
if (Test-Path -LiteralPath $masterPng) {
    Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public static class TrayIcon2
{
    public static string Build(string masterPath, string icoPath, int[] sizes, double keepRatio)
    {
        Bitmap plate;
        using (Bitmap loaded = new Bitmap(masterPath)) plate = new Bitmap(loaded);
        int w = plate.Width, h = plate.Height;

        // 1. build an alpha mask of the light glyph, ignoring the plate's rim highlight
        BitmapData d = plate.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        byte[] px = new byte[d.Stride * h];
        Marshal.Copy(d.Scan0, px, 0, px.Length);

        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = y * d.Stride + x * 4;
                double lum = 0.2126 * px[o + 2] + 0.7152 * px[o + 1] + 0.0722 * px[o];
                double a = (lum - 150.0) / 45.0;      // the plate rim (~90-120) is excluded
                if (a < 0) a = 0; if (a > 1) a = 1;
                px[o + 3] = (byte)(a * 255);
                if (a > 0.6 && x > 60 && x < w - 60 && y > 60 && y < h - 60)
                {
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                }
            }
        }
        Marshal.Copy(px, 0, d.Scan0, px.Length);
        plate.UnlockBits(d);
        if (maxX < 0) throw new InvalidOperationException("glyph not found");

        int cw = maxX - minX + 1, ch = maxY - minY + 1;

        List<byte[]> frames = new List<byte[]>();
        foreach (int size in sizes)
        {
            using (Bitmap frame = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(frame))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    g.Clear(Color.Transparent);

                    double target = size * keepRatio;
                    double scale = Math.Min(target / cw, target / ch);
                    int dw = Math.Max(1, (int)(cw * scale));
                    int dh = Math.Max(1, (int)(ch * scale));
                    int dx = (size - dw) / 2, dy = (size - dh) / 2;

                    // 2. dark halo first (keeps it readable on light taskbars), then the glyph
                    Bitmap glyph = new Bitmap(dw, dh, PixelFormat.Format32bppArgb);
                    using (Graphics gg = Graphics.FromImage(glyph))
                    {
                        gg.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        gg.Clear(Color.Transparent);
                        gg.DrawImage(plate, new Rectangle(0, 0, dw, dh), new Rectangle(minX, minY, cw, ch), GraphicsUnit.Pixel);
                    }
                    if (size >= 24)   // a halo helps on light taskbars, but eats the shape at 16 px
                    {
                        Recolor(glyph, Color.FromArgb(200, 6, 10, 8));
                        for (int ox = -1; ox <= 1; ox++)
                            for (int oy = -1; oy <= 1; oy++)
                                if (ox != 0 || oy != 0) g.DrawImage(glyph, dx + ox, dy + oy, dw, dh);
                    }

                    Recolor(glyph, Color.FromArgb(255, 245, 248, 252));
                    g.DrawImage(glyph, dx, dy, dw, dh);
                    glyph.Dispose();
                }
                frames.Add(ToDib(frame));
            }
        }
        plate.Dispose();

        using (FileStream fs = new FileStream(icoPath, FileMode.Create, FileAccess.Write))
        using (BinaryWriter bw = new BinaryWriter(fs))
        {
            bw.Write((ushort)0); bw.Write((ushort)1); bw.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int s = sizes[i];
                bw.Write((byte)(s >= 256 ? 0 : s)); bw.Write((byte)(s >= 256 ? 0 : s));
                bw.Write((byte)0); bw.Write((byte)0);
                bw.Write((ushort)1); bw.Write((ushort)32);
                bw.Write((uint)frames[i].Length); bw.Write((uint)offset);
                offset += frames[i].Length;
            }
            foreach (byte[] f in frames) bw.Write(f);
        }
        return "tray.ico: glyph " + cw + "x" + ch + " -> fill " + (keepRatio * 100) + "% of " + sizes[0] + ".." + sizes[sizes.Length - 1] + " px";
    }

    static void Recolor(Bitmap bmp, Color color)
    {
        BitmapData d = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        byte[] px = new byte[d.Stride * bmp.Height];
        Marshal.Copy(d.Scan0, px, 0, px.Length);
        for (int i = 0; i < px.Length; i += 4)
        {
            byte a = px[i + 3];
            px[i] = color.B; px[i + 1] = color.G; px[i + 2] = color.R;
            px[i + 3] = (byte)(a * color.A / 255);
        }
        Marshal.Copy(px, 0, d.Scan0, px.Length);
        bmp.UnlockBits(d);
    }

    static byte[] ToDib(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        MemoryStream ms = new MemoryStream();
        BinaryWriter bw = new BinaryWriter(ms);
        bw.Write(40); bw.Write(w); bw.Write(h * 2); bw.Write((ushort)1); bw.Write((ushort)32);
        bw.Write(0); bw.Write(w * h * 4); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
        BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] row = new byte[w * 4];
            for (int y = h - 1; y >= 0; y--)
            {
                Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + y * data.Stride), row, 0, w * 4);
                bw.Write(row);
            }
        }
        finally { bmp.UnlockBits(data); }
        bw.Write(new byte[(((w + 31) / 32) * 4) * h]);
        return ms.ToArray();
    }
}
'@
    [TrayIcon2]::Build($masterPng, $trayPath, @(16, 20, 24, 32, 48, 64), 1.0)
    "tray -> $trayPath ($((Get-Item $trayPath).Length) bytes)"
}

$icns = Join-Path $outDir 'quadvitals.icns'
$icnsArg = if (Test-Path -LiteralPath $icns) { $icns } else { $null }
[IconFrames]::Extract((Join-Path $outDir 'quadvitals.ico'), $icnsArg, $pngDir)
