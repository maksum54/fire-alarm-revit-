# Fire Alarm Detector – Add-in Revit 2025

Add-in Revit 2025 untuk menghitung jumlah **smoke / heat detector** per **Space** berdasarkan NFPA 72, lengkap dengan preview layout dan penempatan otomatis family detector.

## Cara pakai
1. Tab **Fire Alarm** → tombol **Hitung Detector**.
2. **Klik Space** di view denah (pastikan kategori *Spaces* terlihat / Interior Fill aktif).
3. Jendela muncul: nama & nomor space, **panjang × lebar**, L×W, dan luas aktual.
4. Isi **tinggi pemasangan** (default = tinggi space), pilih **Smoke** atau **Heat**.
5. Jumlah detector, perhitungan, dan preview langsung update.
6. **Tempatkan Detector di Model** (pilih family kategori *Fire Alarm Devices*) atau **Pilih Space Lain**.

## Aturan perhitungan
| Detector | S listed | Reduksi tinggi |
|---|---|---|
| Smoke | 9 m | Tabel yang sama (satu checkbox untuk smoke & heat) |
| Heat | 15 m | NFPA 72 Tabel 17.6.3.5.1 (3.05 m → 1.00, 3.66 → 0.91 … 9.14 → 0.34) |

- Jarak detector ke dinding = **½ S**, antar detector = **S**.
- Space dipecah jadi persegi panjang terbesar (bentuk L, tonjolan, dll). Tiap area punya grid sendiri: `n = ⌈panjang area / S⌉ × ⌈lebar area / S⌉`, ½ S dari dinding area. Area kecil yang sudah dalam radius 0,7 S dari detector lain tidak diberi detector.
- Jarak aktual dibagi rata: `L / n` (tepi = setengahnya), sehingga selalu ≤ S.
- Sumbu panjang mengikuti dinding terlurus terpanjang, jadi space yang miring tetap benar.
- Nilai S bisa diubah di jendela sesuai data pabrikan.

## Download DLL
Setiap push dibangun oleh GitHub Actions (**Actions → Build Revit 2025 Add-in → Artifacts**). Push tag `v*` (mis. `v1.0.0`) membuat Release berisi `FireAlarmAddin-Revit2025.zip`.

## Instalasi
Ekstrak zip lalu jalankan `install.bat`, atau salin manual:
```
%APPDATA%\Autodesk\Revit\Addins\2025\FireAlarmAddin.addin
%APPDATA%\Autodesk\Revit\Addins\2025\FireAlarmAddin\FireAlarmAddin.dll
```

## Build lokal
Windows + .NET 8 SDK: `dotnet build src/FireAlarmAddin/FireAlarmAddin.csproj -c Release`
(referensi Revit API via NuGet `Nice3point.Revit.Api.*` 2025, Revit tidak perlu terinstal).
