using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FireAlarmAddin.Core
{
    public enum DetectorType { Smoke, Heat }

    public class Pt
    {
        public double X, Y;
        public Pt(double x, double y) { X = x; Y = y; }
    }

    public class CalcResult
    {
        public double ListedSpacing;     // S listed (m)
        public double HeightFactor;      // faktor reduksi ketinggian (tabel heat NFPA 72)
        public double DesignSpacing;     // S efektif (m)
        public int CountX, CountY;       // jumlah kolom × baris grid
        public int AutoCountX, AutoCountY; // hasil otomatis (sebelum diatur user)
        public List<double> LinesX = new List<double>(); // posisi garis kolom (m, lokal)
        public List<double> LinesY = new List<double>(); // posisi garis baris (m, lokal)
        public List<Pt> Points = new List<Pt>();  // titik potong grid yang ada di dalam space
        public List<Pt> Removed = new List<Pt>(); // titik potong grid di luar boundary (dihapus otomatis)
        public int Quantity => Points.Count;
        public double MaxDistance;       // jarak terjauh titik mana pun di space ke detector terdekat
        public bool Adjusted;            // posisi garis digeser dari pembagian rata agar tercover 0.7 S
        public bool Manual;              // jumlah kolom × baris diatur user
        public List<string> Violations = new List<string>();
        public string Warning;
    }

    /// <summary>
    /// Perhitungan jumlah detector berdasarkan NFPA 72. Satu space = satu grid: kolom dan baris lurus menerus
    /// di seluruh ruangan, titik potong di luar boundary dihapus.
    /// </summary>
    public static class NfpaCalculator
    {
        public const double DefaultSmokeSpacing = 9.0;
        public const double DefaultHeatSpacing = 15.0;

        // NFPA 72 Table 17.6.3.5.1 (dulu 2-2.4.5.1): (tinggi plafon s/d [m], faktor)
        private static readonly double[,] HeatTable =
        {
            { 3.05, 1.00 }, { 3.66, 0.91 }, { 4.27, 0.84 }, { 4.88, 0.77 },
            { 5.49, 0.71 }, { 6.10, 0.64 }, { 6.71, 0.58 }, { 7.32, 0.52 },
            { 7.93, 0.46 }, { 8.54, 0.40 }, { 9.14, 0.34 }
        };

        public static double HeatHeightFactor(double ceilingHeight, out string warning)
        {
            warning = null;
            for (int i = 0; i < HeatTable.GetLength(0); i++)
                if (ceilingHeight <= HeatTable[i, 0] + 1e-9) return HeatTable[i, 1];
            warning = "Tinggi > 9.14 m di luar tabel NFPA 72 untuk heat detector. Faktor 0.34 dipakai, mohon evaluasi engineering.";
            return 0.34;
        }

        /// <summary>
        /// length/width dalam meter (sistem lokal space), polygon = boundary dalam sistem lokal
        /// (origin di pojok kiri bawah bounding box). Kolom/baris pertama 1/2 S dari tepi, jarak antar garis &lt;= S,
        /// dan seluruh space harus dalam jangkauan 0.7 S dari detector.
        /// </summary>
        /// <param name="manual">jumlah kolom × baris dari user; null = otomatis (jumlah detector paling sedikit)</param>
        public static CalcResult Calculate(double length, double width, double height, DetectorType type,
            double listedSpacing, bool applyHeightReduction, List<List<Pt>> polygon, (int Nx, int Ny)? manual = null)
        {
            var r = new CalcResult { ListedSpacing = listedSpacing, HeightFactor = 1.0 };
            // applyHeightReduction untuk smoke = opsi konservatif user (NFPA hanya mensyaratkan untuk heat)
            if (applyHeightReduction)
                r.HeightFactor = HeatHeightFactor(height, out r.Warning);
            if (type == DetectorType.Smoke && height > 12.2)
                r.Warning = "Tinggi > 12.2 m: pertimbangkan beam/aspirating smoke detector.";

            r.DesignSpacing = listedSpacing * r.HeightFactor;
            if (r.DesignSpacing <= 0 || length <= 0 || width <= 0) return r;
            if (polygon != null && polygon.Count == 0) polygon = null;

            double S = r.DesignSpacing, R = 0.7 * S;
            r.AutoCountX = Math.Max(1, (int)Math.Ceiling(length / S - 1e-9));
            r.AutoCountY = Math.Max(1, (int)Math.Ceiling(width / S - 1e-9));
            // sampel kasar untuk mencari posisi garis, sampel halus untuk verifikasi akhir
            var samples = Samples(length, width, polygon, R / 12);
            var fine = Samples(length, width, polygon, 0.1);

            Layout best = null;
            if (manual.HasValue && manual.Value.Nx > 0 && manual.Value.Ny > 0)
            {
                best = Solve(manual.Value.Nx, manual.Value.Ny, length, width, S, R, polygon, samples, fine);
                r.Manual = manual.Value.Nx != r.AutoCountX || manual.Value.Ny != r.AutoCountY;
            }
            else
            {
                // coba beberapa jumlah kolom × baris (dari yang terkecil), pilih yang memenuhi dengan detector paling sedikit
                var combos = new List<(int nx, int ny)>();
                for (int nx = r.AutoCountX; nx <= r.AutoCountX + 2; nx++)
                    for (int ny = r.AutoCountY; ny <= r.AutoCountY + 2; ny++) combos.Add((nx, ny));
                int okSize = int.MaxValue;
                foreach (var c in combos.OrderBy(c => c.nx * c.ny))
                {
                    if (c.nx * c.ny > okSize) break;
                    var l = Solve(c.nx, c.ny, length, width, S, R, polygon, samples, fine);
                    if (best == null || Better(l, best)) best = l;
                    if (l.Ok) okSize = Math.Min(okSize, c.nx * c.ny);
                }
            }

            r.LinesX = best.Xs; r.LinesY = best.Ys;
            r.CountX = best.Xs.Count; r.CountY = best.Ys.Count;
            r.Adjusted = best.Adjusted;
            foreach (var x in best.Xs)
                foreach (var y in best.Ys)
                {
                    var p = new Pt(x, y);
                    if (polygon == null || Inside(p, polygon)) r.Points.Add(p); else r.Removed.Add(p);
                }
            if (r.Points.Count == 0) r.Points.Add(InteriorPoint(length, width, polygon));
            r.MaxDistance = MaxDist(fine, best.Xs, best.Ys, polygon, r.Points);

            if (r.Manual || !best.Ok)
            {
                var gx = Gaps(best.Xs, length); var gy = Gaps(best.Ys, width);
                if (gx.Max() > S + 1e-6 || gy.Max() > S + 1e-6)
                    r.Violations.Add("Jarak antar detector " + F(gx.Max()) + " / " + F(gy.Max()) + " m melebihi S = " + F(S) + " m");
                if (r.MaxDistance > R + 1e-6)
                    r.Violations.Add("Ada bagian space " + F(r.MaxDistance) + " m dari detector terdekat (> 0.7 S = " + F(R) + " m)");
            }
            return r;
        }

        private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        private class Layout
        {
            public List<double> Xs, Ys;
            public int Count;
            public double Violation; // 0 = seluruh space tercover 0.7 S
            public bool Adjusted;
            public bool Ok => Violation <= 1e-9;
        }

        private static bool Better(Layout a, Layout b)
        {
            if (a.Ok != b.Ok) return a.Ok;
            if (!a.Ok) return a.Violation < b.Violation;
            if (a.Count != b.Count) return a.Count < b.Count;
            return !a.Adjusted && b.Adjusted; // sama banyak: pilih yang jaraknya rata
        }

        // tepi & jarak garis: [tepi awal, jarak antar garis..., tepi akhir]; tepi dikali 2 agar sebanding dengan S
        private static List<double> Gaps(List<double> lines, double extent)
        {
            var g = new List<double> { lines[0] * 2 };
            for (int i = 1; i < lines.Count; i++) g.Add(lines[i] - lines[i - 1]);
            g.Add((extent - lines[lines.Count - 1]) * 2);
            return g;
        }

        /// <summary>
        /// Grid nx × ny: mulai dari pembagian rata (1/2 S dari tepi), lalu kalau ada bagian space yang di luar 0.7 S
        /// (mis. pojok coakan), garis kolom/baris digeser sedikit demi sedikit sampai tercover. Garis tetap lurus
        /// menerus, jarak antar garis tetap &lt;= S dan garis terluar tetap &lt;= 1/2 S dari tepi.
        /// </summary>
        private static Layout Solve(int nx, int ny, double length, double width, double S, double R,
            List<List<Pt>> polygon, List<Pt> samples, List<Pt> fine)
        {
            var xs = Uniform(nx, length); var ys = Uniform(ny, width);
            bool adjusted = false;
            double v = Violation(xs, ys, polygon, fine, R);
            if (v > 1e-9)
            {
                // cari dengan sampel kasar & radius sedikit dikecilkan (cadangan untuk celah antar sampel)
                double rc = R - 0.1;
                double vc = Violation(xs, ys, polygon, samples, rc);
                double[] steps = { 2, 1, 0.5, 0.25, 0.1, 0.05 };
                for (int pass = 0; pass < 80 && vc > 1e-9; pass++)
                {
                    bool improved = false;
                    foreach (var axis in new[] { xs, ys })
                    {
                        double ext = axis == xs ? length : width;
                        for (int i = 0; i < axis.Count; i++)
                            foreach (var st in steps)
                                foreach (var d in new[] { st, -st })
                                {
                                    var old = axis.ToList();
                                    if (Move(axis, i, d, ext, S))
                                    {
                                        double nv = Violation(xs, ys, polygon, samples, rc);
                                        if (nv < vc - 1e-9) { vc = nv; improved = true; goto nextLine; }
                                    }
                                    for (int k = 0; k < axis.Count; k++) axis[k] = old[k];
                                }
                            nextLine:;
                    }
                    if (!improved) break;
                }
                for (int k = 0; k < xs.Count; k++) xs[k] = Round(xs[k]);
                for (int k = 0; k < ys.Count; k++) ys[k] = Round(ys[k]);
                double nvFine = Violation(xs, ys, polygon, fine, R);
                if (nvFine < v) { v = nvFine; adjusted = true; }
                else { xs = Uniform(nx, length); ys = Uniform(ny, width); }
            }
            int count = 0;
            foreach (var x in xs) foreach (var y in ys) if (polygon == null || Inside(new Pt(x, y), polygon)) count++;
            return new Layout { Xs = xs, Ys = ys, Count = count, Violation = v, Adjusted = adjusted };
        }

        /// <summary>
        /// Geser garis i sejauh d; garis tetangga ikut terseret bila jarak antar garis jadi &gt; S atau &lt; jarak minimum.
        /// </summary>
        private static bool Move(List<double> axis, int i, double d, double extent, double S)
        {
            const double minGap = 0.3;
            axis[i] += d;
            for (int j = i - 1; j >= 0; j--)
            {
                if (axis[j + 1] - axis[j] > S) axis[j] = axis[j + 1] - S;
                if (axis[j + 1] - axis[j] < minGap) axis[j] = axis[j + 1] - minGap;
            }
            for (int j = i + 1; j < axis.Count; j++)
            {
                if (axis[j] - axis[j - 1] > S) axis[j] = axis[j - 1] + S;
                if (axis[j] - axis[j - 1] < minGap) axis[j] = axis[j - 1] + minGap;
            }
            return Valid(axis, extent, S);
        }

        private static double Round(double v) => Math.Round(v, 2);

        private static List<double> Uniform(int n, double extent)
        {
            var l = new List<double>();
            for (int i = 0; i < n; i++) l.Add(extent / n * (i + 0.5));
            return l;
        }

        private static bool Valid(List<double> lines, double extent, double S)
        {
            const double eps = 1e-6, minGap = 0.3;
            if (lines[0] <= 0 || lines[0] > S / 2 + eps) return false;
            if (lines[lines.Count - 1] >= extent || extent - lines[lines.Count - 1] > S / 2 + eps) return false;
            for (int i = 1; i < lines.Count; i++)
            {
                double g = lines[i] - lines[i - 1];
                if (g < minGap || g > S + eps) return false;
            }
            return true;
        }

        // jumlah kuadrat kelebihan jarak (d - R) di titik sampel; 0 = semua dalam jangkauan
        private static double Violation(List<double> xs, List<double> ys, List<List<Pt>> polygon, List<Pt> samples, double R)
        {
            var g = new GridIndex(xs, ys, polygon);
            if (g.Count == 0) return double.MaxValue;
            double sum = 0;
            foreach (var s in samples)
            {
                double d = g.Nearest(s.X, s.Y, R);
                if (d > R + 1e-9) sum += (d - R) * (d - R);
            }
            return sum;
        }

        private static double MaxDist(List<Pt> samples, List<double> xs, List<double> ys, List<List<Pt>> polygon, List<Pt> fallback)
        {
            var g = new GridIndex(xs, ys, polygon);
            if (g.Count == 0) g = new GridIndex(fallback);
            double worst = 0;
            foreach (var s in samples) worst = Math.Max(worst, g.Nearest(s.X, s.Y, double.MaxValue));
            return worst;
        }

        /// <summary>Pencarian detector terdekat cepat: cek titik potong grid di sekitar sampel dulu, baru semua.</summary>
        private class GridIndex
        {
            private readonly double[] _xs, _ys;
            private readonly bool[,] _in;
            private readonly List<Pt> _all = new List<Pt>();
            public int Count => _all.Count;

            public GridIndex(List<double> xs, List<double> ys, List<List<Pt>> polygon)
            {
                _xs = xs.ToArray(); _ys = ys.ToArray();
                _in = new bool[_xs.Length, _ys.Length];
                for (int i = 0; i < _xs.Length; i++)
                    for (int j = 0; j < _ys.Length; j++)
                    {
                        var p = new Pt(_xs[i], _ys[j]);
                        _in[i, j] = polygon == null || Inside(p, polygon);
                        if (_in[i, j]) _all.Add(p);
                    }
            }

            public GridIndex(List<Pt> pts) { _xs = new double[0]; _ys = new double[0]; _in = new bool[0, 0]; _all.AddRange(pts); }

            private static int Closest(double[] a, double v)
            {
                int k = Array.BinarySearch(a, v);
                if (k >= 0) return k;
                k = ~k;
                if (k == 0) return 0;
                if (k >= a.Length) return a.Length - 1;
                return v - a[k - 1] <= a[k] - v ? k - 1 : k;
            }

            // jarak ke detector terdekat; kalau di sekitar sudah ada yang <= enough, langsung dipakai
            public double Nearest(double x, double y, double enough)
            {
                double best = double.MaxValue;
                if (_xs.Length > 0)
                {
                    int ci = Closest(_xs, x), cj = Closest(_ys, y);
                    for (int i = Math.Max(0, ci - 2); i <= Math.Min(_xs.Length - 1, ci + 2); i++)
                        for (int j = Math.Max(0, cj - 2); j <= Math.Min(_ys.Length - 1, cj + 2); j++)
                            if (_in[i, j]) best = Math.Min(best, (_xs[i] - x) * (_xs[i] - x) + (_ys[j] - y) * (_ys[j] - y));
                    if (best <= enough * enough) return Math.Sqrt(best);
                }
                foreach (var d in _all) best = Math.Min(best, (d.X - x) * (d.X - x) + (d.Y - y) * (d.Y - y));
                return Math.Sqrt(best);
            }
        }

        // titik sampel: grid di dalam space + titik di sepanjang boundary (pojok & dinding paling jauh dari detector)
        private static List<Pt> Samples(double length, double width, List<List<Pt>> polygon, double step)
        {
            var res = new List<Pt>();
            int nx = Math.Max(4, (int)Math.Ceiling(length / step)), ny = Math.Max(4, (int)Math.Ceiling(width / step));
            for (int i = 0; i <= nx; i++)
                for (int j = 0; j <= ny; j++)
                {
                    var p = new Pt(length * i / nx, width * j / ny);
                    if (polygon == null || Inside(p, polygon)) res.Add(p);
                }
            if (polygon != null)
                foreach (var loop in polygon)
                    for (int i = 0; i < loop.Count; i++)
                    {
                        var a = loop[i]; var b = loop[(i + 1) % loop.Count];
                        double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                        int n = Math.Max(1, (int)Math.Ceiling(len / step));
                        for (int k = 0; k < n; k++)
                            res.Add(new Pt(a.X + (b.X - a.X) * k / n, a.Y + (b.Y - a.Y) * k / n));
                    }
            return res;
        }

        // titik cadangan yang pasti di dalam boundary (tengah bbox bisa di luar untuk bentuk L)
        private static Pt InteriorPoint(double length, double width, List<List<Pt>> polygon)
        {
            var center = new Pt(length / 2, width / 2);
            if (polygon == null || polygon.Count == 0 || Inside(center, polygon)) return center;
            Pt best = center; double bestD = double.MaxValue;
            const int n = 40;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    var p = new Pt(length * (i + 0.5) / n, width * (j + 0.5) / n);
                    double d = Math.Pow(p.X - center.X, 2) + Math.Pow(p.Y - center.Y, 2);
                    if (d < bestD && Inside(p, polygon)) { best = p; bestD = d; }
                }
            return best;
        }

        // even-odd rule, mendukung lubang (loop dalam)
        public static bool Inside(Pt p, List<List<Pt>> loops)
        {
            bool inside = false;
            foreach (var poly in loops)
                for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
                {
                    var a = poly[i]; var b = poly[j];
                    if ((a.Y > p.Y) != (b.Y > p.Y) &&
                        p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                        inside = !inside;
                }
            return inside;
        }
    }
}
