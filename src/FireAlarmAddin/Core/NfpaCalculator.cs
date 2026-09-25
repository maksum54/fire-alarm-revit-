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

    /// <summary>Satu ruas di sumbu kolom/baris yang garisnya dibagi rata (tepi = 1/2 jarak).</summary>
    public class Segment
    {
        public double From, To;
        public int Count;
        public double Gap => (To - From) / Count;
    }

    public class CalcResult
    {
        public double ListedSpacing;     // S listed (m)
        public double HeightFactor;      // faktor reduksi ketinggian (tabel heat NFPA 72)
        public double DesignSpacing;     // S efektif (m)
        public int CountX, CountY;       // jumlah garis kolom × baris
        public int AutoCountX, AutoCountY; // hasil otomatis (sebelum diatur user)
        public List<double> LinesX = new List<double>(); // posisi garis kolom (m, lokal)
        public List<double> LinesY = new List<double>(); // posisi garis baris (m, lokal)
        public List<Segment> SegmentsX = new List<Segment>(), SegmentsY = new List<Segment>();
        public List<Pt> Points = new List<Pt>();  // titik potong grid yang ada di dalam space
        public List<Pt> Removed = new List<Pt>(); // titik potong grid di luar boundary (dihapus otomatis)
        public int Quantity => Points.Count;
        public double MaxDistance;       // jarak terjauh titik mana pun di space ke detector terdekat
        public bool Manual;              // jumlah kolom × baris diatur user
        public List<string> Violations = new List<string>();
        public string Warning;
    }

    /// <summary>
    /// Perhitungan jumlah detector berdasarkan NFPA 72. Satu space = satu grid kolom × baris.
    /// Detector terluar &lt;= 1/2 S dari SETIAP dinding (termasuk dinding coakan), jarak antar detector &lt;= S.
    /// </summary>
    public static class NfpaCalculator
    {
        public const double DefaultSmokeSpacing = 9.0;
        public const double DefaultHeatSpacing = 15.0;
        private const double Eps = 1e-6;

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
        /// (origin di pojok kiri bawah bounding box).
        /// Sumbu dibagi di posisi dinding (mis. dinding coakan) jadi ruas; tiap ruas diisi garis rata:
        /// n = ⌈panjang ruas / S⌉, tepi = 1/2 jarak. Ruas digabung bila setelah digabung semua dinding tetap
        /// &lt;= 1/2 S dan jarak &lt;= S, supaya grid tetap satu dan serata mungkin.
        /// </summary>
        /// <param name="manual">jumlah kolom × baris dari user; null = otomatis</param>
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
            double S = r.DesignSpacing;

            // kolom awal: rata di lebar total, lalu baris <- kolom, kolom <- baris sampai stabil
            var segX = new List<Segment> { Seg(0, length, S) };
            var xs = Lines(segX);
            List<Segment> segY = null;
            List<double> ys = null;
            for (int it = 0; it < 6; it++)
            {
                segY = AxisSegments(xs.Select(x => Crossings(x, true, polygon, length, width)).ToList(), 0, width, S);
                var nys = Lines(segY);
                segX = AxisSegments(nys.Select(y => Crossings(y, false, polygon, length, width)).ToList(), 0, length, S);
                var nxs = Lines(segX);
                bool same = ys != null && Same(nxs, xs) && Same(nys, ys);
                xs = nxs; ys = nys;
                if (same) break;
            }
            r.AutoCountX = xs.Count; r.AutoCountY = ys.Count;

            if (manual.HasValue && manual.Value.Nx > 0 && manual.Value.Ny > 0 &&
                (manual.Value.Nx != xs.Count || manual.Value.Ny != ys.Count))
            {
                r.Manual = true;
                segX = Distribute(segX, manual.Value.Nx, length);
                segY = Distribute(segY, manual.Value.Ny, width);
                xs = Lines(segX); ys = Lines(segY);
            }

            r.SegmentsX = segX; r.SegmentsY = segY;
            r.LinesX = xs; r.LinesY = ys;
            r.CountX = xs.Count; r.CountY = ys.Count;
            foreach (var x in xs)
                foreach (var y in ys)
                {
                    var p = new Pt(x, y);
                    if (polygon == null || Inside(p, polygon)) r.Points.Add(p); else r.Removed.Add(p);
                }
            if (r.Points.Count == 0) r.Points.Add(InteriorPoint(length, width, polygon));

            // verifikasi: tiap garis, jarak ke dinding <= 1/2 S dan antar detector <= S; lalu cakupan keseluruhan
            var msgs = new List<string>();
            foreach (var x in xs) CheckLine(Crossings(x, true, polygon, length, width), ys, S, "Kolom x = " + F(x), msgs);
            foreach (var y in ys) CheckLine(Crossings(y, false, polygon, length, width), xs, S, "Baris y = " + F(y), msgs);
            r.Violations.AddRange(msgs.Distinct().Take(6));
            var fine = Samples(length, width, polygon, 0.1);
            r.MaxDistance = MaxDist(fine, xs, ys, polygon, r.Points);
            // grid persegi S × S punya titik terjauh S/√2; lebih dari itu berarti ada bagian yang tidak terjangkau
            if (r.MaxDistance > S / Math.Sqrt(2) + 0.01)
                r.Violations.Add("Ada bagian space " + F(r.MaxDistance) + " m dari detector terdekat (> 0.7 S = " + F(0.7 * S) + " m)");
            return r;
        }

        private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        private static bool Same(List<double> a, List<double> b) =>
            a.Count == b.Count && a.Zip(b, (p, q) => Math.Abs(p - q) < 1e-4).All(t => t);

        private static Segment Seg(double from, double to, double S) =>
            new Segment { From = from, To = to, Count = Math.Max(1, (int)Math.Ceiling((to - from) / S - 1e-9)) };

        private static List<double> Lines(List<Segment> segs)
        {
            var l = new List<double>();
            foreach (var s in segs)
                for (int i = 0; i < s.Count; i++) l.Add(Math.Round(s.From + s.Gap * (i + 0.5), 3));
            return l;
        }

        /// <summary>
        /// Ruang di sepanjang garis (x = c bila vertical, y = c bila tidak): daftar [a, b] bagian yang di dalam space.
        /// </summary>
        public static List<(double A, double B)> Crossings(double c, bool vertical, List<List<Pt>> polygon, double length, double width)
        {
            var res = new List<(double, double)>();
            if (polygon == null || polygon.Count == 0) { res.Add((0, vertical ? width : length)); return res; }
            double cc = c + 1e-7; // hindari tepat di vertex
            var hits = new List<double>();
            foreach (var loop in polygon)
                for (int i = 0; i < loop.Count; i++)
                {
                    var a = loop[i]; var b = loop[(i + 1) % loop.Count];
                    double a1 = vertical ? a.X : a.Y, b1 = vertical ? b.X : b.Y;
                    if ((a1 > cc) == (b1 > cc)) continue;
                    double t = (cc - a1) / (b1 - a1);
                    double a2 = vertical ? a.Y : a.X, b2 = vertical ? b.Y : b.X;
                    hits.Add(a2 + t * (b2 - a2));
                }
            hits.Sort();
            for (int i = 0; i + 1 < hits.Count; i += 2)
                if (hits[i + 1] - hits[i] > 0.05) res.Add((hits[i], hits[i + 1]));
            return res;
        }

        /// <summary>
        /// Garis di satu sumbu yang memenuhi semua ruang (interval) yang dilalui garis sumbu lain: tiap interval punya
        /// garis di dalamnya, garis pertama/terakhir &lt;= 1/2 S dari ujung interval (dinding), jarak antar garis &lt;= S.
        /// Mulai dari ruas terpecah di setiap ujung interval (pasti memenuhi), lalu ruas digabung selama tetap memenuhi.
        /// </summary>
        private static List<Segment> AxisSegments(List<List<(double A, double B)>> perLine, double min, double max, double S)
        {
            var intervals = perLine.SelectMany(l => l).ToList();
            if (intervals.Count == 0) intervals.Add((min, max));
            var cuts = new List<double> { min, max };
            foreach (var iv in intervals) { cuts.Add(iv.A); cuts.Add(iv.B); }
            cuts.Sort();
            var bp = new List<double>();
            foreach (var c in cuts)
                if (bp.Count == 0 || c - bp[bp.Count - 1] > 0.05) bp.Add(c); else bp[bp.Count - 1] = Math.Max(bp[bp.Count - 1], c);
            bp[0] = min; bp[bp.Count - 1] = max;

            List<Segment> Build(List<double> b)
            {
                var s = new List<Segment>();
                for (int i = 0; i + 1 < b.Count; i++) s.Add(Seg(b[i], b[i + 1], S));
                return s;
            }

            var cur = Build(bp);
            while (bp.Count > 2)
            {
                List<double> bestBp = null; List<Segment> bestSeg = null;
                for (int k = 1; k < bp.Count - 1; k++)
                {
                    var tryBp = new List<double>(bp); tryBp.RemoveAt(k);
                    var seg = Build(tryBp);
                    if (!Satisfies(Lines(seg), intervals, S)) continue;
                    int n = seg.Sum(s => s.Count);
                    if (bestSeg == null || n < bestSeg.Sum(s => s.Count)) { bestBp = tryBp; bestSeg = seg; }
                }
                // gabung hanya bila jumlah garis tidak bertambah
                if (bestSeg == null || bestSeg.Sum(s => s.Count) > cur.Sum(s => s.Count)) break;
                bp = bestBp; cur = bestSeg;
            }
            return cur;
        }

        private static bool Satisfies(List<double> lines, List<(double A, double B)> intervals, double S)
        {
            foreach (var iv in intervals)
            {
                var inside = lines.Where(l => l > iv.A + Eps && l < iv.B - Eps).ToList();
                if (inside.Count == 0) return false;
                if (inside[0] - iv.A > S / 2 + Eps || iv.B - inside[inside.Count - 1] > S / 2 + Eps) return false;
                for (int i = 1; i < inside.Count; i++) if (inside[i] - inside[i - 1] > S + Eps) return false;
            }
            return true;
        }

        // detector di satu garis: jarak ke dinding (ujung interval) <= 1/2 S, antar detector <= S
        private static void CheckLine(List<(double A, double B)> intervals, List<double> cross, double S, string name, List<string> msgs)
        {
            foreach (var iv in intervals)
            {
                var inside = cross.Where(l => l > iv.A + Eps && l < iv.B - Eps).ToList();
                if (inside.Count == 0) { msgs.Add(name + ": tidak ada detector sepanjang " + F(iv.B - iv.A) + " m"); continue; }
                double e1 = inside[0] - iv.A, e2 = iv.B - inside[inside.Count - 1];
                if (Math.Max(e1, e2) > S / 2 + 0.005)
                    msgs.Add(name + ": jarak ke dinding " + F(Math.Max(e1, e2)) + " m > 1/2 S = " + F(S / 2) + " m");
                for (int i = 1; i < inside.Count; i++)
                    if (inside[i] - inside[i - 1] > S + 0.005)
                    { msgs.Add(name + ": jarak antar detector " + F(inside[i] - inside[i - 1]) + " m > S = " + F(S) + " m"); break; }
            }
        }

        /// <summary>Jumlah garis dari user dibagi ke ruas (tiap ruas minimal 1, sisanya ke ruas yang jaraknya paling lebar).</summary>
        private static List<Segment> Distribute(List<Segment> segs, int n, double extent)
        {
            if (n < segs.Count) return new List<Segment> { new Segment { From = 0, To = extent, Count = n } };
            var res = segs.Select(s => new Segment { From = s.From, To = s.To, Count = 1 }).ToList();
            for (int k = segs.Count; k < n; k++)
                res.OrderByDescending(s => (s.To - s.From) / s.Count).First().Count++;
            return res;
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
