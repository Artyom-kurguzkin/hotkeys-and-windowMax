using System.Drawing;
using System.Drawing.Imaging;
using System.Text;

namespace WindowManager;

// Writes .cur / .ani bytes, so animated cursors can be built from rendered frames (Windows only animates .ani).
public static class CursorFile
{
    // A .cur file holding one 32bpp image with alpha: ICONDIR, one ICONDIRENTRY (with the hotspot), then a DIB
    // (BITMAPINFOHEADER with doubled height, BGRA pixels bottom-up, all-zero AND mask).
    public static byte[] Cur(Bitmap bmp, int hotX, int hotY)
    {
        var dib = Dib(bmp);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)0); w.Write((ushort)2); w.Write((ushort)1);            // reserved, type = cursor, count
        w.Write((byte)(bmp.Width >= 256 ? 0 : bmp.Width)); w.Write((byte)(bmp.Height >= 256 ? 0 : bmp.Height));
        w.Write((byte)0); w.Write((byte)0);                                       // colours, reserved
        w.Write((ushort)hotX); w.Write((ushort)hotY);
        w.Write(dib.Length); w.Write(6 + 16);                                     // size, offset of the image
        w.Write(dib);
        return ms.ToArray();
    }

    // RIFF "ACON": an "anih" header, then a LIST "fram" of "icon" chunks, each a whole .cur file.
    public static byte[] Ani(IReadOnlyList<Bitmap> frames, int hotX, int hotY, int jiffies)
    {
        using var body = new MemoryStream();
        using var w = new BinaryWriter(body);
        w.Write(Encoding.ASCII.GetBytes("ACON"));
        Chunk(w, "anih", h =>
        {
            h.Write(36); h.Write(frames.Count); h.Write(frames.Count);           // cbSize, frames, steps
            h.Write(0); h.Write(0); h.Write(0); h.Write(0);                      // size/bits/planes: taken from frames
            h.Write(jiffies); h.Write(1);                                         // rate (1/60 s), flags: AF_ICON
        });
        using var list = new MemoryStream();
        using (var l = new BinaryWriter(list, Encoding.ASCII, leaveOpen: true))
        {
            l.Write(Encoding.ASCII.GetBytes("fram"));
            foreach (var f in frames) Chunk(l, "icon", c => c.Write(Cur(f, hotX, hotY)));
        }
        Chunk(w, "LIST", c => c.Write(list.ToArray()));

        using var file = new MemoryStream();
        using var fw = new BinaryWriter(file);
        fw.Write(Encoding.ASCII.GetBytes("RIFF"));
        fw.Write((int)body.Length);
        fw.Write(body.ToArray());
        return file.ToArray();
    }

    static void Chunk(BinaryWriter w, string id, Action<BinaryWriter> write)
    {
        using var ms = new MemoryStream();
        using (var c = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true)) write(c);
        w.Write(Encoding.ASCII.GetBytes(id));
        w.Write((int)ms.Length);
        w.Write(ms.ToArray());
        if (ms.Length % 2 == 1) w.Write((byte)0); // RIFF chunks are word-aligned
    }

    static byte[] Dib(Bitmap bmp)
    {
        int width = bmp.Width, height = bmp.Height;
        var maskStride = (width + 31) / 32 * 4;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(40); w.Write(width); w.Write(height * 2);                         // header size, width, XOR+AND height
        w.Write((ushort)1); w.Write((ushort)32); w.Write(0);                      // planes, bits, BI_RGB
        w.Write(width * height * 4 + maskStride * height); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[width * 4];
            for (var y = height - 1; y >= 0; y--) // bottom-up; Format32bppArgb is BGRA in memory, straight alpha
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                w.Write(row);
            }
        }
        finally { bmp.UnlockBits(data); }
        w.Write(new byte[maskStride * height]); // AND mask unused when the image has alpha
        return ms.ToArray();
    }
}
