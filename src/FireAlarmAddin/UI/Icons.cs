using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FireAlarmAddin.UI
{
    /// <summary>Ikon vektor (digambar di kode) untuk ribbon dan jendela — tajam di semua ukuran/DPI.</summary>
    internal static class Icons
    {
        private static readonly Color Red = Color.FromRgb(0xD3, 0x2F, 0x2F);
        private static readonly Color DarkRed = Color.FromRgb(0x9A, 0x1B, 0x1B);

        /// <summary>Detector plafon (tampak samping) dengan gelombang alarm, di atas latar merah.</summary>
        public static ImageSource Detector(int size)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                double s = size;
                // latar rounded square gradasi merah
                var bg = new LinearGradientBrush(Red, DarkRed, 90);
                dc.DrawRoundedRectangle(bg, null, new Rect(0, 0, s, s), s * 0.2, s * 0.2);

                // plafon
                dc.DrawRectangle(Brushes.White, null, new Rect(s * 0.12, s * 0.14, s * 0.76, Math.Max(1, s * 0.07)));
                // badan detector (setengah lingkaran menggantung di plafon)
                var body = new StreamGeometry();
                using (var g = body.Open())
                {
                    g.BeginFigure(new Point(s * 0.26, s * 0.21), true, true);
                    g.ArcTo(new Point(s * 0.74, s * 0.21), new Size(s * 0.24, s * 0.22), 0, false, SweepDirection.Clockwise, true, false);
                }
                dc.DrawGeometry(Brushes.White, null, body);
                // LED
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07)), null, new Point(s * 0.5, s * 0.33), s * 0.05, s * 0.05);

                // gelombang alarm
                var pen = new Pen(Brushes.White, Math.Max(1, s * 0.06)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                for (int i = 1; i <= 2; i++)
                {
                    double r = s * (0.2 + 0.14 * i);
                    var arc = new StreamGeometry();
                    using (var g = arc.Open())
                    {
                        var c = new Point(s * 0.5, s * 0.3);
                        g.BeginFigure(new Point(c.X - r * Math.Sin(0.7), c.Y + r * Math.Cos(0.7)), false, false);
                        g.ArcTo(new Point(c.X + r * Math.Sin(0.7), c.Y + r * Math.Cos(0.7)), new Size(r, r), 0, false,
                            SweepDirection.Counterclockwise, true, false);
                    }
                    dc.DrawGeometry(null, pen, arc);
                }
            }
            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);
            bmp.Freeze();
            return bmp;
        }
    }
}
