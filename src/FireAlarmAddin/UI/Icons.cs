using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FireAlarmAddin.UI
{
    /// <summary>Ikon ribbon digambar di kode agar tidak perlu file gambar.</summary>
    internal static class Icons
    {
        public static ImageSource Detector(int size)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                double c = size / 2.0;
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xD3, 0x2F, 0x2F)), null, new Point(c, c), c - 0.5, c - 0.5);
                dc.DrawEllipse(Brushes.White, null, new Point(c, c), c * 0.62, c * 0.62);
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xD3, 0x2F, 0x2F)), null, new Point(c, c), c * 0.25, c * 0.25);
            }
            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);
            bmp.Freeze();
            return bmp;
        }
    }
}
