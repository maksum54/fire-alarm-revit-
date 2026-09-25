using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using FireAlarmAddin.Core;

namespace FireAlarmAddin.UI
{
    public partial class FireAlarmWindow : Window
    {
        public enum UserAction { Close, PickAnother, Place }

        public class Settings
        {
            public DetectorType Type;
            public double Height;
            public double Spacing;
            public bool Reduction;
            public Autodesk.Revit.DB.FamilySymbol Symbol;
        }

        private class SymbolItem
        {
            public Autodesk.Revit.DB.FamilySymbol Symbol;
            public string Display => Symbol == null ? "(tidak ada family Fire Alarm Device dimuat)" : Symbol.FamilyName + " : " + Symbol.Name;
        }

        public UserAction Action { get; private set; } = UserAction.Close;
        public CalcResult Result { get; private set; }
        public Settings CurrentSettings { get; private set; }

        private readonly SpaceGeometry _geo;
        private bool _loading = true;
        private double _smokeS = NfpaCalculator.DefaultSmokeSpacing, _heatS = NfpaCalculator.DefaultHeatSpacing;
        // jumlah kolom × baris yang diatur user (null = otomatis)
        private (int Nx, int Ny)? _manual;

        public FireAlarmWindow(SpaceGeometry geo, IList<Autodesk.Revit.DB.FamilySymbol> symbols, Settings previous)
        {
            InitializeComponent();
            _geo = geo;
            Icon = Icons.Detector(64);
            HeaderIcon.Source = Icons.Detector(40);

            TxtSpaceName.Text = geo.Number + " - " + geo.Name;
            TxtSpaceInfo.Text = "Level: " + geo.LevelName;
            TxtL.Text = F(geo.Length) + " m";
            TxtW.Text = F(geo.Width) + " m";
            TxtLW.Text = F(geo.Length * geo.Width) + " m²";
            TxtArea.Text = F(geo.Area) + " m²";

            var items = symbols.Select(s => new SymbolItem { Symbol = s }).ToList();
            if (items.Count == 0) items.Add(new SymbolItem());
            CbFamily.ItemsSource = items;
            CbFamily.SelectedIndex = 0;

            if (previous != null)
            {
                if (previous.Type == DetectorType.Heat) { RbHeat.IsChecked = true; _heatS = previous.Spacing; }
                else _smokeS = previous.Spacing;
                CbReduction.IsChecked = previous.Reduction;
                var sel = items.FirstOrDefault(i => i.Symbol != null && previous.Symbol != null && i.Symbol.Id == previous.Symbol.Id);
                if (sel != null) CbFamily.SelectedItem = sel;
            }
            TbHeight.Text = F(previous?.Height ?? geo.DefaultHeight);
            TbSpacing.Text = F(RbHeat.IsChecked == true ? _heatS : _smokeS);
            _loading = false;
            Loaded += (s, e) => { TbHeight.Focus(); TbHeight.SelectAll(); Recalculate(); };
        }

        private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        private static bool TryNum(string s, out double v) =>
            double.TryParse((s ?? "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

        private DetectorType SelectedType => RbHeat.IsChecked == true ? DetectorType.Heat : DetectorType.Smoke;

        private void Type_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _loading = true;
            TbSpacing.Text = F(SelectedType == DetectorType.Heat ? _heatS : _smokeS);
            _loading = false;
            _manual = null;
            Recalculate();
        }

        private void Input_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            if (sender == TbSpacing && TryNum(TbSpacing.Text, out var s))
            {
                if (SelectedType == DetectorType.Heat) _heatS = s; else _smokeS = s;
            }
            if (sender != CbFamily) _manual = null; // S/tinggi berubah -> jumlah otomatis dihitung ulang
            Recalculate();
        }

        private void Recalculate()
        {
            if (TxtQty == null) return;
            bool okH = TryNum(TbHeight.Text, out var h) && h > 0;
            bool okS = TryNum(TbSpacing.Text, out var s) && s > 0;
            TbHeight.BorderBrush = okH ? Brushes.LightGray : Brushes.Red;
            TbSpacing.BorderBrush = okS ? Brushes.LightGray : Brushes.Red;
            bool heat = SelectedType == DetectorType.Heat;
            bool reduce = CbReduction.IsChecked == true; // satu opsi untuk smoke & heat agar hasil konsisten
            BtnPlace.IsEnabled = okH && okS;
            if (!okH || !okS)
            {
                TxtQty.Text = "-";
                TxtDetail.Text = "Masukkan tinggi dan S listed dengan angka yang valid (contoh 3.5).";
                TxtWarning.Text = "";
                Result = null;
                Preview.Children.Clear();
                ManualBox.Visibility = Visibility.Collapsed;
                return;
            }

            var r = NfpaCalculator.Calculate(_geo.Length, _geo.Width, h, SelectedType, s,
                reduce, _geo.LocalLoops, _manual);
            Result = r;
            CurrentSettings = new Settings
            {
                Type = SelectedType, Height = h, Spacing = s, Reduction = CbReduction.IsChecked == true,
                Symbol = (CbFamily.SelectedItem as SymbolItem)?.Symbol
            };

            TxtQty.Text = r.Quantity.ToString();
            TxtQtyUnit.Text = SelectedType == DetectorType.Smoke ? " smoke detector" : " heat detector";
            TxtDetail.Text =
                "S listed = " + F(r.ListedSpacing) + " m" +
                (reduce ? "  × " + F(r.HeightFactor) + " (tabel reduksi tinggi " + F(h) + " m" + ")" : "") + "\n" +
                "S desain = " + F(r.DesignSpacing) + " m\n" +
                GridDetail(r);
            var warn = r.Warning ?? "";
            if (r.Violations.Count > 0)
                warn = (warn.Length > 0 ? warn + "\n" : "") + "⚠ Tidak memenuhi NFPA 72:\n• " + string.Join("\n• ", r.Violations);
            TxtWarning.Text = warn;
            TxtWarning.Foreground = r.Violations.Count > 0 ? Brushes.Firebrick : new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09));
            BuildGridEditor(r);
            DrawPreview();
        }

        private string GridDetail(CalcResult r)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("Satu grid untuk seluruh space (kolom & baris lurus menerus):\n");
            sb.Append("Arah panjang: ⌈" + F(_geo.Length) + " / " + F(r.DesignSpacing) + "⌉ = " + r.AutoCountX + " kolom\n");
            sb.Append("Arah lebar: ⌈" + F(_geo.Width) + " / " + F(r.DesignSpacing) + "⌉ = " + r.AutoCountY + " baris\n");
            if (r.Manual)
                sb.Append("Diatur manual: " + r.CountX + " × " + r.CountY + " (otomatis " + r.AutoCountX + " × " + r.AutoCountY + ")\n");
            else if (r.CountX != r.AutoCountX || r.CountY != r.AutoCountY)
                sb.Append("Dipakai " + r.CountX + " × " + r.CountY + " agar seluruh space tercover 0.7 S\n");
            sb.Append("Kolom (x): " + Spacing(r.LinesX, _geo.Length) + "\n");
            sb.Append("Baris (y): " + Spacing(r.LinesY, _geo.Width) + "\n");
            if (r.Adjusted) sb.Append("Garis digeser dari pembagian rata agar pojok/coakan tercover 0.7 S\n");
            sb.Append("Grid " + r.CountX + " × " + r.CountY + " = " + (r.CountX * r.CountY));
            if (r.Removed.Count > 0) sb.Append(", " + r.Removed.Count + " titik di luar boundary dihapus");
            sb.Append("\nJarak terjauh ke detector = " + F(r.MaxDistance) + " m (maks 0.7 S = " + F(0.7 * r.DesignSpacing) + " m)");
            sb.Append("\nTotal = " + r.Quantity + " unit");
            return sb.ToString();
        }

        // "tepi 4.49 | jarak 8.99, 8.99, 8.99 | tepi 4.49"
        private static string Spacing(List<double> lines, double extent)
        {
            if (lines.Count == 0) return "-";
            var gaps = new List<string>();
            for (int i = 1; i < lines.Count; i++) gaps.Add(F(lines[i] - lines[i - 1]));
            return "tepi " + F(lines[0]) + (gaps.Count > 0 ? " | jarak " + string.Join(", ", gaps) : "") +
                   " | tepi " + F(extent - lines[lines.Count - 1]) + " m";
        }

        // editor jumlah kolom × baris (tombol − / +, dibangun ulang tiap hitung)
        private void BuildGridEditor(CalcResult r)
        {
            GridEditor.Children.Clear();
            ManualBox.Visibility = r.CountX > 0 ? Visibility.Visible : Visibility.Collapsed;
            BtnResetManual.IsEnabled = _manual.HasValue;
            if (r.CountX == 0) return;

            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
            sp.Children.Add(new TextBlock { Text = "Grid", Width = 40, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
            AddStepper(sp, r.CountX, r.CountY, true);
            sp.Children.Add(new TextBlock { Text = "×", Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            AddStepper(sp, r.CountX, r.CountY, false);
            sp.Children.Add(new TextBlock
            {
                Text = "  → " + r.Quantity + " unit" + (r.Manual ? "  (auto " + r.AutoCountX + "×" + r.AutoCountY + ")" : "  (auto)"),
                VerticalAlignment = VerticalAlignment.Center, FontSize = 11,
                Foreground = r.Manual ? Brushes.Firebrick : Brushes.DimGray
            });
            GridEditor.Children.Add(sp);
        }

        private void AddStepper(Panel host, int nx, int ny, bool isX)
        {
            int v = isX ? nx : ny;
            Button B(string t, int delta) => new Button
            {
                Content = t, Width = 22, Height = 22, Padding = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
                Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(0xCB, 0xD2, 0xDB)),
                IsEnabled = v + delta >= 1 && v + delta <= 50,
                Tag = delta
            };
            var minus = B("−", -1); var plus = B("+", 1);
            RoutedEventHandler click = (s, e) =>
            {
                int d = (int)((Button)s).Tag;
                _manual = isX ? (nx + d, ny) : (nx, ny + d);
                Recalculate();
            };
            minus.Click += click; plus.Click += click;
            host.Children.Add(minus);
            host.Children.Add(new TextBlock { Text = v.ToString(), Width = 26, TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
            host.Children.Add(plus);
        }

        private void ResetManual_Click(object sender, RoutedEventArgs e)
        {
            _manual = null;
            Recalculate();
        }

        private void Preview_SizeChanged(object sender, SizeChangedEventArgs e) => DrawPreview();

        private void DrawPreview()
        {
            Preview.Children.Clear();
            var r = Result;
            if (r == null || Preview.ActualWidth < 10 || _geo.Length <= 0) return;
            double pad = 30;
            double scale = Math.Min((Preview.ActualWidth - 2 * pad) / _geo.Length, (Preview.ActualHeight - 2 * pad) / _geo.Width);
            if (scale <= 0) return;
            double ox = (Preview.ActualWidth - _geo.Length * scale) / 2, oy = (Preview.ActualHeight + _geo.Width * scale) / 2;
            System.Windows.Point P(double x, double y) => new System.Windows.Point(ox + x * scale, oy - y * scale);

            // boundary
            foreach (var loop in _geo.LocalLoops)
            {
                var poly = new Polygon { Stroke = new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51)), StrokeThickness = 2,
                    Fill = new SolidColorBrush(Color.FromArgb(0x18, 0x3B, 0x82, 0xF6)) };
                foreach (var p in loop) poly.Points.Add(P(p.X, p.Y));
                Preview.Children.Add(poly);
            }
            // grid lines
            var gridBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x9A, 0x9A));
            // garis kolom & baris grid (lurus menerus di seluruh space)
            foreach (var x in r.LinesX) Line(P(x, 0), P(x, _geo.Width), gridBrush, 0.8, true);
            foreach (var y in r.LinesY) Line(P(0, y), P(_geo.Length, y), gridBrush, 0.8, true);

            // coverage + detectors
            double cov = r.DesignSpacing * 0.7 * scale; // radius 0.7S (NFPA)
            foreach (var d in r.Points)
            {
                var c = P(d.X, d.Y);
                var circle = new Ellipse { Width = cov * 2, Height = cov * 2, Stroke = new SolidColorBrush(Color.FromArgb(0x60, 0xD3, 0x2F, 0x2F)),
                    StrokeDashArray = new DoubleCollection { 3, 3 }, Fill = new SolidColorBrush(Color.FromArgb(0x10, 0xD3, 0x2F, 0x2F)) };
                Canvas.SetLeft(circle, c.X - cov); Canvas.SetTop(circle, c.Y - cov);
                Preview.Children.Add(circle);
            }
            foreach (var d in r.Points)
            {
                var c = P(d.X, d.Y);
                var dot = new Ellipse { Width = 12, Height = 12, Fill = new SolidColorBrush(Color.FromRgb(0xD3, 0x2F, 0x2F)), Stroke = Brushes.White, StrokeThickness = 2 };
                Canvas.SetLeft(dot, c.X - 6); Canvas.SetTop(dot, c.Y - 6);
                Preview.Children.Add(dot);
            }
            // titik yang dihapus (di luar boundary)
            var grey = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
            foreach (var d in r.Removed)
            {
                var c = P(d.X, d.Y);
                Line(new System.Windows.Point(c.X - 5, c.Y - 5), new System.Windows.Point(c.X + 5, c.Y + 5), grey, 2, false);
                Line(new System.Windows.Point(c.X - 5, c.Y + 5), new System.Windows.Point(c.X + 5, c.Y - 5), grey, 2, false);
            }
            // legenda jangkauan
            Label("S desain = " + F(r.DesignSpacing) + " m  |  lingkaran R = 0.7 S = " + F(r.DesignSpacing * 0.7) + " m", 6, 4);
            // dimension labels
            Label(F(_geo.Length) + " m", P(_geo.Length / 2, 0).X - 20, P(0, 0).Y + 6);
            Label(F(_geo.Width) + " m", P(0, 0).X - 28, P(0, _geo.Width / 2).Y - 8, -90);
        }

        private void Line(System.Windows.Point a, System.Windows.Point b, Brush br, double th, bool dash)
        {
            var l = new System.Windows.Shapes.Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = br, StrokeThickness = th };
            if (dash) l.StrokeDashArray = new DoubleCollection { 4, 4 };
            Preview.Children.Add(l);
        }

        private void Label(string text, double x, double y, double angle = 0)
        {
            var t = new TextBlock { Text = text, Foreground = Brushes.DimGray, FontSize = 11 };
            if (angle != 0) t.RenderTransform = new RotateTransform(angle);
            Canvas.SetLeft(t, x); Canvas.SetTop(t, y);
            Preview.Children.Add(t);
        }

        private void Place_Click(object sender, RoutedEventArgs e)
        {
            Recalculate();
            if (Result == null) return;
            if (CurrentSettings.Symbol == null)
            {
                MessageBox.Show(this, "Load dulu family detector (kategori Fire Alarm Devices) ke project.", "Fire Alarm",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Action = UserAction.Place; Close();
        }

        private void Pick_Click(object sender, RoutedEventArgs e) { Recalculate(); Action = UserAction.PickAnother; Close(); }
        private void Close_Click(object sender, RoutedEventArgs e) { Action = UserAction.Close; Close(); }
    }
}
