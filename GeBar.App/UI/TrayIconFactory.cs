using GeBar.Core;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GeBar.App;

internal static partial class TrayIconFactory
{
    private const int DesignSize = 32;
    private const int SmallIconMetric = 49;
    private const double BoltStrip = 22;
    private const int BarCount = 4;
    private const double BarGap = 1.4;
    private const double BoltRatio = 0.62;
    private static readonly Color Missing = Color.FromRgb(128, 128, 128);
    private static readonly Color Broken = Color.FromRgb(255, 69, 0);
    private static readonly Color Charging = Color.FromRgb(34, 197, 94);
    private static readonly Typeface Face = new(new FontFamily("Segoe UI Variable Display"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public static int IconSize()
    {
        int size = 0;
        try { size = GetSystemMetricsForDpi(SmallIconMetric, GetDpiForSystem()); }
        catch (EntryPointNotFoundException) { }
        if (size <= 0) size = GetSystemMetrics(SmallIconMetric);
        return size > 0 ? size : 16;
    }

    public static IntPtr Create(DeviceBatteryState state, AppSettings settings)
    {
        int pixels = IconSize();
        double scale = pixels / (double)DesignSize;
        var visual = new DrawingVisual();
        using (DrawingContext canvas = visual.RenderOpen())
        {
            Color color = state.ConnectionState switch
            {
                DeviceConnectionState.DeviceNotFound or DeviceConnectionState.DeviceOff => Missing,
                DeviceConnectionState.ReadError => Broken,
                _ when state.Percentage <= settings.CriticalThreshold => FromArgb(settings.CriticalColorArgb),
                _ when state.Percentage <= settings.WarningThreshold => FromArgb(settings.WarningColorArgb),
                _ => FromArgb(settings.NormalColorArgb)
            };
            if (settings.IconStyle == TrayIconStyle.Percentage && state.Percentage is int percentage)
            {
                DrawPercentage(canvas, pixels, scale, percentage, color, state, settings);
            }
            else
            {
                canvas.PushTransform(new ScaleTransform(scale, scale));
                DrawBattery(canvas, color, state, settings);
                canvas.Pop();
            }
        }

        var bitmap = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return CreateIcon(bitmap);
    }

    private static void DrawPercentage(DrawingContext canvas, int pixels, double scale, int percentage, Color color, DeviceBatteryState state, AppSettings settings)
    {
        double em = scale * settings.TrayIconScale switch
        {
            1 => percentage == 100 ? 11 : 15,
            3 => percentage == 100 ? 16 : 22,
            _ => percentage == 100 ? 14 : 19
        };
        var text = new FormattedText(percentage.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Face, em, new SolidColorBrush(color), 1);
        double available = (state.IsCharging == true ? BoltStrip : DesignSize) * scale;
        double left = Math.Round(Math.Max(0, (available - text.Width) / 2));
        double top = Math.Round((pixels - text.Height) / 2);
        canvas.DrawText(text, new Point(left, top));
        if (state.IsCharging != true) return;
        canvas.PushTransform(new ScaleTransform(scale, scale));
        canvas.DrawGeometry(new SolidColorBrush(Charging), null, Bolt(new Rect(BoltStrip, 7, DesignSize - BoltStrip, 18)));
        canvas.Pop();
    }

    private static void DrawBattery(DrawingContext canvas, Color color, DeviceBatteryState state, AppSettings settings)
    {
        (Rect body, double thickness, double radius) = settings.TrayIconScale switch
        {
            1 => (new Rect(4, 10, 21, 13), 2d, 3.5d),
            3 => (new Rect(1, 5, 26, 22), 2.5d, 5.5d),
            _ => (new Rect(2, 8, 24, 17), 2.25d, 4.5d)
        };
        var brush = new SolidColorBrush(color);
        var pen = new Pen(brush, thickness);
        canvas.DrawRoundedRectangle(null, pen, body, radius, radius);

        double capHeight = body.Height * 0.4;
        canvas.DrawRoundedRectangle(brush, null,
            new Rect(body.Right + thickness * 0.6, body.Top + ((body.Height - capHeight) / 2), 2.2, capHeight), 1, 1);

        Rect inside = body;
        inside.Inflate(-(thickness + 1.1), -(thickness + 1.1));
        Geometry? bolt = state.IsCharging == true ? Bolt(inside) : null;

        int bars = bolt is null ? Bars(state.Percentage, settings) : 0;
        if (bars > 0)
        {
            double slot = (inside.Width - (BarGap * (BarCount - 1))) / BarCount;
            var filled = new GeometryGroup();
            for (int index = 0; index < bars; index++)
                filled.Children.Add(new RectangleGeometry(new Rect(inside.X + (index * (slot + BarGap)), inside.Y, slot, inside.Height), 0.8, 0.8));
            canvas.DrawGeometry(brush, null, filled);
        }
        if (state.ConnectionState == DeviceConnectionState.DeviceNotFound)
            canvas.DrawLine(pen, new Point(body.Left + 2, body.Top - 2), new Point(body.Right - 1, body.Bottom + 2));
        if (bolt is not null) canvas.DrawGeometry(new SolidColorBrush(Charging), null, bolt);
    }

    private static int Bars(int? percentage, AppSettings settings)
    {
        if (percentage is not int value) return 0;
        if (value <= settings.CriticalThreshold) return 1;
        if (value <= settings.WarningThreshold) return 2;
        return value <= settings.WarningThreshold + ((100 - settings.WarningThreshold) / 2) ? 3 : 4;
    }

    private static Geometry Bolt(Rect slot)
    {
        double height = slot.Height;
        double width = Math.Min(slot.Width, height * BoltRatio);
        double x = slot.X + ((slot.Width - width) / 2);
        double y = slot.Y;
        var bolt = new StreamGeometry();
        using (StreamGeometryContext outline = bolt.Open())
        {
            outline.BeginFigure(new Point(x + (0.56 * width), y), true, true);
            outline.PolyLineTo(
            [
                new Point(x, y + (0.55 * height)),
                new Point(x + (0.44 * width), y + (0.55 * height)),
                new Point(x + (0.44 * width), y + height),
                new Point(x + width, y + (0.45 * height)),
                new Point(x + (0.56 * width), y + (0.45 * height))
            ], true, true);
        }
        bolt.Freeze();
        return bolt;
    }

    private static Color FromArgb(int argb) =>
        Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private static IntPtr CreateIcon(RenderTargetBitmap bitmap)
    {
        int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        IntPtr color = CreateArgbSurface(width, height, pixels);
        if (color == IntPtr.Zero) return IntPtr.Zero;
        IntPtr mask = CreateBitmap(width, height, 1, 1, new byte[(width + 15) / 16 * 2 * height]);
        var description = new IconInfo { fIcon = 1, hbmMask = mask, hbmColor = color };
        IntPtr icon = CreateIconIndirect(ref description);
        DeleteObject(color);
        DeleteObject(mask);
        return icon;
    }

    private static IntPtr CreateArgbSurface(int width, int height, byte[] pixels)
    {
        var header = new BitmapInfoHeader
        {
            biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0
        };
        IntPtr surface = CreateDIBSection(IntPtr.Zero, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (surface == IntPtr.Zero || bits == IntPtr.Zero)
        {
            if (surface != IntPtr.Zero) DeleteObject(surface);
            return IntPtr.Zero;
        }
        Marshal.Copy(pixels, 0, bits, pixels.Length);
        return surface;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, [In] byte[] bits);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateDIBSection")]
    private static partial IntPtr CreateDIBSection(IntPtr device, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr handle);

    [LibraryImport("user32.dll")]
    private static partial IntPtr CreateIconIndirect(ref IconInfo icon);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int metric);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int metric, uint dpi);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForSystem();
}
