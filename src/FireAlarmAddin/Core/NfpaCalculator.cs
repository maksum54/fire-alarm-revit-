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
        public double HeightFactor;      // faktor reduksi ketinggian (heat)
        public double DesignSpacing;     // S efektif (m)
        public int CountX, CountY;       // grid di bounding box
        public double ActualSpacingX, ActualSpacingY;
        public List<Pt> Points = new List<Pt>();  // koordinat lokal (m) yang ada di dalam space
        public List<Pt> Removed = new List<Pt>(); // titik grid di luar boundary (dihapus otomatis)
        public int Quantity => Points.Count;
        public string Warning;
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
            if (type == DetectorType.Heat && applyHeightReduction)
                r.HeightFactor = HeatHeightFactor(height, out r.Warning);
            else if (type == DetectorType.Smoke && height > 12.2)
                r.Warning = "Tinggi > 12.2 m: pertimbangkan beam/aspirating smoke detector.";

            r.DesignSpacing = listedSpacing * r.HeightFactor;
            if (r.DesignSpacing <= 0 || length <= 0 || width <= 0) return r;

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
                        r.Removed.Add(p); // mis. bagian kosong pada space bentuk L
                }
            if (r.Points.Count == 0) r.Points.Add(InteriorPoint(length, width, polygon));
            return r;
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
