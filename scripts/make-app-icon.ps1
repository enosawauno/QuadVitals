# Build assets\icon\app.ico: the product icon cropped to its glyph and blown up to fill
# the frame, so a desktop shortcut reads as large as other apps' icons (the source art carries
# macOS-style padding that makes it look small at 48/96 px).
#
#   powershell -File scripts\make-app-icon.ps1
#
# Source: assets\icon\png\icon-1024.png (already in the repo). ASCII only: PS 5.1 decodes
# BOM-less .ps1 as GBK.
param(
    [string]$Master = '',
    [string]$Output = '',
    [double]$Fill = 1.12,
    # Frames to embed. A tray icon never draws above 48 px, so shipping the 256 px frame there
    # only inflates the payload.
    [string]$Sizes = ''
)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrEmpty($Master)) { $Master = Join-Path $root 'assets\icon\png\icon-1024.png' }
if ([string]::IsNullOrEmpty($Output)) { $Output = Join-Path $root 'assets\icon\app.ico' }
if (-not (Test-Path -LiteralPath $Master)) { throw "missing master png: $Master" }

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public static class ShortcutIcon
{
    public static string Build(string masterPath, string icoPath, int[] sizes, double fill)
    {
        using (Bitmap master = new Bitmap(masterPath))
        using (Bitmap art = Crop(master))
        {
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
                        double target = size * fill;
                        double scale = Math.Min(target / art.Width, target / art.Height);
                        int dw = Math.Max(1, (int)(art.Width * scale));
                        int dh = Math.Max(1, (int)(art.Height * scale));
                        g.DrawImage(art, (size - dw) / 2, (size - dh) / 2, dw, dh);
                    }
                    frames.Add(ToDib(frame));
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(icoPath));
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
            return "app.ico: cropped " + art.Width + "x" + art.Height + " -> fill " +
                   (int)(fill * 100) + "% of " + sizes[0] + ".." + sizes[sizes.Length - 1] + " px";
        }
    }

    private static Bitmap Crop(Bitmap source)
    {
        BitmapData data = source.LockBits(new Rectangle(0, 0, source.Width, source.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] px = new byte[data.Stride * source.Height];
        Marshal.Copy(data.Scan0, px, 0, px.Length);
        source.UnlockBits(data);

        int minX = source.Width, minY = source.Height, maxX = -1, maxY = -1;
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                if (px[y * data.Stride + x * 4 + 3] > 8)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }
        if (maxX < 0) throw new InvalidOperationException("master png has no visible pixels");

        Rectangle bounds = Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
        Bitmap cropped = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(cropped))
        {
            g.DrawImage(source, new Rectangle(0, 0, bounds.Width, bounds.Height), bounds, GraphicsUnit.Pixel);
        }
        return cropped;
    }

    private static byte[] ToDib(Bitmap bmp)
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

if ([string]::IsNullOrWhiteSpace($Sizes)) { $Sizes = '16,20,24,32,40,48,64,96,128,256' }
$frameSizes = @($Sizes -split ',' | ForEach-Object { [int]$_.Trim() })
$result = [ShortcutIcon]::Build($Master, $Output, $frameSizes, $Fill)
$result
"ico  -> $Output ($((Get-Item $Output).Length) bytes)"
