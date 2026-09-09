using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Drawing.Imaging;
using System.Drawing.Text;
using ZXing;
using ZXing.Common;
using ZXing.Windows.Compatibility;

namespace WarehousePacking.Services;

public static class BarcodeLabel
{
    public static Bitmap RenderCode128(string value, int width, int height)
    {
        var writer = new BarcodeWriter
        {
            Format = BarcodeFormat.CODE_128,
            Options = new EncodingOptions
            {
                Width = Math.Max(80, width),
                Height = Math.Max(24, height),
                Margin = 2,
                PureBarcode = true,
            },
        };
        return writer.Write(value);
    }

    public static byte[] LabelPdf(string barcode, string sku, string name, double widthMm, double heightMm)
    {
        using var bmp = RenderLabelBitmap(barcode, sku, name, widthMm, heightMm);
        return BitmapToPdf(bmp, widthMm, heightMm);
    }

    public static Bitmap RenderLabelBitmap(string barcode, string sku, string name, double widthMm, double heightMm)
    {
        var value = (barcode ?? "").Trim();
        if (value.Length == 0)
            throw new InvalidOperationException("Barcode is empty");

        const int dpi = 300;
        var w = Math.Max(80, (int)Math.Round(widthMm / 25.4 * dpi));
        var h = Math.Max(40, (int)Math.Round(heightMm / 25.4 * dpi));
        var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        bmp.SetResolution(dpi, dpi);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        using var font = new Font("Arial", heightMm <= 26 ? 6.5f : 7.2f, FontStyle.Regular, GraphicsUnit.Point);
        using var fontBold = new Font("Arial", heightMm <= 26 ? 7f : 7.8f, FontStyle.Bold, GraphicsUnit.Point);
        using var brush = new SolidBrush(Color.Black);
        var cx = w / 2f;
        var margin = Mm(1, dpi);
        var textW = w - Mm(2.4, dpi);

        var footer = Truncate(g, value, font, textW);
        var skuLine = string.IsNullOrWhiteSpace(sku) ? "" : Truncate(g, "SKU " + sku.Trim(), font, textW);
        var yValue = h - margin - font.GetHeight(g);
        var ySku = yValue - (heightMm <= 26 ? Mm(2.0, dpi) : Mm(2.4, dpi));
        DrawCentered(g, footer, font, brush, cx, yValue);
        if (skuLine.Length > 0)
            DrawCentered(g, skuLine, fontBold, brush, cx, ySku);

        var zoneTop = margin;
        var zoneBottom = ySku - Mm(0.5, dpi);
        if (!string.IsNullOrWhiteSpace(name))
        {
            var lines = Wrap(g, name.Trim(), fontBold, textW, 2);
            var step = fontBold.GetHeight(g) + 2;
            for (var i = 0; i < lines.Count; i++)
                DrawCentered(g, lines[i], fontBold, brush, cx, zoneTop + i * step);
            zoneTop += lines.Count * step + Mm(0.4, dpi);
        }

        var barMaxW = w - Mm(1.2, dpi);
        var barMaxH = Math.Max(Mm(2.5, dpi), zoneBottom - zoneTop);
        using var bars = RenderCode128(value, (int)barMaxW, (int)Math.Max(24, barMaxH * 0.85));
        var scale = Math.Min(barMaxW / bars.Width, barMaxH / bars.Height);
        var bw = Math.Max(1, (int)Math.Round(bars.Width * scale));
        var bh = Math.Max(1, (int)Math.Round(bars.Height * scale));
        var bx = (w - bw) / 2;
        var by = (int)(zoneTop + (barMaxH - bh) / 2);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.DrawImage(bars, new Rectangle(bx, by, bw, bh));
        return bmp;
    }

    private static byte[] BitmapToPdf(Bitmap bmp, double widthMm, double heightMm)
    {
        // Minimal one-page PDF with an embedded JPEG — enough for GDI reprint path.
        using var jpeg = new MemoryStream();
        bmp.Save(jpeg, ImageFormat.Jpeg);
        var img = jpeg.ToArray();
        var wPt = widthMm * 72.0 / 25.4;
        var hPt = heightMm * 72.0 / 25.4;
        var imgObj = 3;
        var stream = $"q\n{F(wPt)} 0 0 {F(hPt)} 0 0 cm\n/Im0 Do\nQ\n";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            $"<< /Type /XObject /Subtype /Image /Width {bmp.Width} /Height {bmp.Height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {img.Length} >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(wPt)} {F(hPt)}] /Resources << /XObject << /Im0 {imgObj} 0 R >> >> /Contents 5 0 R >>",
            $"<< /Length {stream.Length} >>",
        };

        using var ms = new MemoryStream();
        using var w = new StreamWriter(ms, new System.Text.ASCIIEncoding(), leaveOpen: true);
        w.Write("%PDF-1.4\n");
        w.Flush();
        var offsets = new List<long> { 0 };
        void WriteObj(int id, string dict, byte[]? binary = null, string? extra = null)
        {
            offsets.Add(ms.Position);
            var sw = new StreamWriter(ms, new System.Text.ASCIIEncoding(), leaveOpen: true);
            sw.Write($"{id} 0 obj\n{dict}");
            if (binary != null)
            {
                sw.Write("\nstream\n");
                sw.Flush();
                ms.Write(binary, 0, binary.Length);
                sw = new StreamWriter(ms, new System.Text.ASCIIEncoding(), leaveOpen: true);
                sw.Write("\nendstream\nendobj\n");
            }
            else if (extra != null)
            {
                sw.Write($"\nstream\n{extra}endstream\nendobj\n");
            }
            else
            {
                sw.Write("\nendobj\n");
            }
            sw.Flush();
        }

        WriteObj(1, objects[0]);
        WriteObj(2, objects[1]);
        WriteObj(3, objects[2], img);
        WriteObj(4, objects[3]);
        WriteObj(5, objects[4], extra: stream);
        var xref = ms.Position;
        var xw = new StreamWriter(ms, new System.Text.ASCIIEncoding(), leaveOpen: true);
        xw.Write($"xref\n0 6\n0000000000 65535 f \n");
        for (var i = 1; i <= 5; i++)
            xw.Write($"{offsets[i]:D10} 00000 n \n");
        xw.Write($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        xw.Flush();
        return ms.ToArray();
    }

    private static float Mm(double mm, int dpi) => (float)(mm / 25.4 * dpi);

    private static string F(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static void DrawCentered(Graphics g, string text, Font font, Brush brush, float cx, float y)
    {
        var size = g.MeasureString(text, font);
        g.DrawString(text, font, brush, cx - size.Width / 2f, y);
    }

    private static string Truncate(Graphics g, string text, Font font, float maxWidth)
    {
        if (g.MeasureString(text, font).Width <= maxWidth)
            return text;
        var s = text;
        while (s.Length > 1 && g.MeasureString(s + "…", font).Width > maxWidth)
            s = s[..^1];
        return s + "…";
    }

    private static List<string> Wrap(Graphics g, string text, Font font, float maxWidth, int maxLines)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var current = "";
        foreach (var word in words)
        {
            var trial = current.Length == 0 ? word : current + " " + word;
            if (g.MeasureString(trial, font).Width <= maxWidth)
            {
                current = trial;
                continue;
            }
            if (current.Length > 0)
                lines.Add(current);
            current = word;
            if (lines.Count >= maxLines)
                break;
        }
        if (current.Length > 0 && lines.Count < maxLines)
            lines.Add(current);
        if (lines.Count == maxLines && words.Length > 0)
            lines[^1] = Truncate(g, lines[^1], font, maxWidth);
        return lines;
    }
}
