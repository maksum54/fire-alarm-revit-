using System;
using System.Collections.Generic;

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
        public int CountX, CountY;       // grid di bounding box
        public double ActualSpacingX, ActualSpacingY;
        public List<Pt> Points = new List<Pt>();  // koordinat lokal (m) yang ada di dalam space
        public List<Pt> Removed = new List<Pt>(); // titik grid di luar boundary (dihapus otomatis)
        public int Quantity => Points.Count;
        public List<Zone> Zones = new List<Zone>(); // persegi panjang hasil pecahan boundary
        public string Warning;
    }

    /// <summary>Satu area persegi panjang dengan grid detector sendiri (1/2 S dari dinding area).</summary>
    public class Zone
    {
        public double X0, Y0, X1, Y1;
        public int Nx, Ny;
        public double W => X1 - X0;
        public double H => Y1 - Y0;
        public double Sx => W / Nx;
        public double Sy => H / Ny;
        public bool Skipped; // area kecil yang sudah tercover detector area lain (radius 0.7 S)
    }

    /// <summary>Perhitungan jumlah detector berdasarkan NFPA 72.</summary>
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
        /// (origin di pojok kiri bawah bounding box). Detector pertama 1/2 S dari tepi, lalu S antar detector.
        /// </summary>
        public static CalcResult Calculate(double length, double width, double height, DetectorType type,
            double listedSpacing, bool applyHeightReduction, List<List<Pt>> polygon)
        {
            var r = new CalcResult { ListedSpacing = listedSpacing, HeightFactor = 1.0 };
            // applyHeightReduction untuk smoke = opsi konservatif user (NFPA hanya mensyaratkan untuk heat)
            if (applyHeightReduction)
                r.HeightFactor = HeatHeightFactor(height, out r.Warning);
            if (type == DetectorType.Smoke && height > 12.2)
                r.Warning = "Tinggi > 12.2 m: pertimbangkan beam/aspirating smoke detector.";

            r.DesignSpacing = listedSpacing * r.HeightFactor;
            if (r.DesignSpacing <= 0 || length <= 0 || width <= 0) return r;

            if (polygon != null && polygon.Count > 0 && LayoutByZones(r, polygon))
                return r;

            // fallback: grid di bounding box, titik di luar boundary dihapus
            r.CountX = Math.Max(1, (int)Math.Ceiling(length / r.DesignSpacing - 1e-9));
            r.CountY = Math.Max(1, (int)Math.Ceiling(width / r.DesignSpacing - 1e-9));
            r.ActualSpacingX = length / r.CountX;
            r.ActualSpacingY = width / r.CountY;

            for (int i = 0; i < r.CountX; i++)
                for (int j = 0; j < r.CountY; j++)
                {
                    var p = new Pt(r.ActualSpacingX * (i + 0.5), r.ActualSpacingY * (j + 0.5));
                    if (polygon == null || polygon.Count == 0 || Inside(p, polygon))
                        r.Points.Add(p);
                    else
                        r.Removed.Add(p);
                }
            if (r.Points.Count == 0) r.Points.Add(InteriorPoint(length, width, polygon));
            return r;
        }

        /// <summary>
        /// Pecah boundary jadi persegi panjang terbesar secara berurutan. Tiap persegi panjang diberi grid
        /// sendiri: detector 1/2 S dari dinding area, S antar detector (dibagi rata). Area kecil yang seluruhnya
        /// sudah dalam radius 0.7 S dari detector lain dilewati.
        /// </summary>
        private static bool LayoutByZones(CalcResult r, List<List<Pt>> polygon)
        {
            double S = r.DesignSpacing;
            var xs = Coords(polygon, true);
            var ys = Coords(polygon, false);
            int nc = xs.Count - 1, nr = ys.Count - 1;
            if (nc < 1 || nr < 1 || nc > 60 || nr > 60) return false; // terlalu kompleks (mis. lengkung) -> fallback

            // sel grid (dari koordinat vertex) yang berada di dalam boundary
            var free = new bool[nc, nr];
            for (int i = 0; i < nc; i++)
                for (int j = 0; j < nr; j++)
                    free[i, j] = Inside(new Pt((xs[i] + xs[i + 1]) / 2, (ys[j] + ys[j + 1]) / 2), polygon);

            while (true)
            {
                // persegi panjang (luas nyata) terbesar dari sel yang masih bebas
                double best = 0; int bi0 = 0, bi1 = 0, bj0 = 0, bj1 = 0;
                for (int i0 = 0; i0 < nc; i0++)
                    for (int j0 = 0; j0 < nr; j0++)
                    {
                        if (!free[i0, j0]) continue;
                        int jMax = nr - 1;
                        for (int i1 = i0; i1 < nc && free[i1, j0]; i1++)
                        {
                            int j1 = j0;
                            while (j1 + 1 <= jMax && free[i1, j1 + 1]) j1++;
                            jMax = j1;
                            double a = (xs[i1 + 1] - xs[i0]) * (ys[jMax + 1] - ys[j0]);
                            if (a > best) { best = a; bi0 = i0; bi1 = i1; bj0 = j0; bj1 = jMax; }
                        }
                    }
                if (best <= 1e-6) break;
                for (int i = bi0; i <= bi1; i++)
                    for (int j = bj0; j <= bj1; j++) free[i, j] = false;

                var z = new Zone { X0 = xs[bi0], X1 = xs[bi1 + 1], Y0 = ys[bj0], Y1 = ys[bj1 + 1] };
                z.Nx = Math.Max(1, (int)Math.Ceiling(z.W / S - 1e-9));
                z.Ny = Math.Max(1, (int)Math.Ceiling(z.H / S - 1e-9));
                r.Zones.Add(z);

                if (r.Points.Count > 0 && Covered(z, r.Points, 0.7 * S)) { z.Skipped = true; continue; }
                for (int i = 0; i < z.Nx; i++)
                    for (int j = 0; j < z.Ny; j++)
                        r.Points.Add(new Pt(z.X0 + z.Sx * (i + 0.5), z.Y0 + z.Sy * (j + 0.5)));
            }
            if (r.Points.Count == 0) return false;

            var main = r.Zones[0];
            r.CountX = main.Nx; r.CountY = main.Ny;
            r.ActualSpacingX = main.Sx; r.ActualSpacingY = main.Sy;
            return true;
        }

        // semua titik sampel di area berada dalam radius dari salah satu detector
        private static bool Covered(Zone z, List<Pt> dets, double radius)
        {
            const int n = 6;
            for (int i = 0; i <= n; i++)
                for (int j = 0; j <= n; j++)
                {
                    double x = z.X0 + z.W * i / n, y = z.Y0 + z.H * j / n;
                    bool ok = false;
                    foreach (var d in dets)
                        if ((d.X - x) * (d.X - x) + (d.Y - y) * (d.Y - y) <= radius * radius + 1e-9) { ok = true; break; }
                    if (!ok) return false;
                }
            return true;
        }

        // koordinat unik vertex (dibulatkan 1 cm, sliver < 5 cm digabung)
        private static List<double> Coords(List<List<Pt>> polygon, bool x)
        {
            var v = new List<double>();
            foreach (var loop in polygon)
                foreach (var p in loop) v.Add(Math.Round(x ? p.X : p.Y, 2));
            v.Sort();
            var res = new List<double>();
            foreach (var c in v)
                if (res.Count == 0 || c - res[res.Count - 1] > 0.05) res.Add(c);
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
