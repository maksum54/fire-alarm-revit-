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
- Satu space = satu grid kolom × baris. Detector terluar **≤ ½ S dari setiap dinding** (termasuk dinding coakan/bentuk L), jarak antar detector **≤ S** (boleh lebih rapat, tidak boleh lebih jauh).
- Sumbu dibagi di posisi dinding jadi ruas; tiap ruas `n = ⌈panjang ruas / S⌉`, jarak = panjang / n, tepi = setengahnya. Ruas digabung selama aturan ½ S / S tetap terpenuhi, jadi ruangan persegi tetap `⌈L/S⌉ × ⌈W/S⌉`. Titik potong di luar boundary dihapus.
- Contoh ER1 35,95 × 30,05 m dengan coakan kanan atas, S = 9: kolom 4 × 8,99 m; baris 3 × 8,35 m (½ S dari dinding bawah & dinding coakan) + 1 baris untuk bagian kiri atas → 15 detector.
- Jumlah kolom × baris bisa diatur manual (tombol − / +); pelanggaran ½ S / S ditampilkan merah.
- Sumbu panjang mengikuti dinding terlurus terpanjang, jadi space yang miring tetap benar.
- Nilai S bisa diubah di jendela sesuai data pabrikan.

## Download DLL
Setiap push dibangun oleh GitHub Actions (**Actions → Build Revit 2025 Add-in → Artifacts**). Push tag `v*` (mis. `v1.0.0`) membuat Release berisi `FireAlarmAddin-Revit2025.zip`.

## Instalasi
Zip hanya berisi DLL dan `.addin` (tanpa `.bat`, supaya tidak diblokir browser). Ekstrak, lalu salin ke folder add-in Revit 2025
(ketik `%APPDATA%\Autodesk\Revit\Addins\2025` di address bar Explorer):
```
%APPDATA%\Autodesk\Revit\Addins\2025\FireAlarmAddin.addin
%APPDATA%\Autodesk\Revit\Addins\2025\FireAlarmAddin\FireAlarmAddin.dll
```
Sebelum disalin: klik kanan `FireAlarmAddin.dll` → Properties → centang **Unblock** (kalau ada). Buka ulang Revit.
`install.bat` di repo ini tetap bisa dipakai: taruh di sebelah folder hasil ekstrak lalu jalankan.

## Build lokal
Windows + .NET 8 SDK: `dotnet build src/FireAlarmAddin/FireAlarmAddin.csproj -c Release`
(referensi Revit API via NuGet `Nice3point.Revit.Api.*` 2025, Revit tidak perlu terinstal).
