# lyrics-align: timing per kata (Enhanced LRC) untuk file .lrc

Alat batch yang menambahkan timing per kata ke file `.lrc` yang baru punya timing per baris.
Caranya: menyelaraskan (*forced alignment*) teks lirik yang sudah ada dengan audio lagunya.
Hasilnya dipakai MusicPlayer untuk efek karaoke ala Apple Music, supaya isian (fill) di dalam
satu baris mengikuti suara penyanyi, bukan sekadar tebakan.

Alat ini **tidak mentranskripsi** lagu. Teks lirik tidak pernah diubah; yang ditambahkan
hanya tanda waktu `<mm:ss.xx>`.

## Cara kerja

1. Untuk setiap `.lrc`, dicari file audio di folder yang sama dengan nama yang sama
   (dibandingkan setelah normalisasi Unicode NFC, jadi `シャルル` NFD/NFC tetap cocok).
   Ekstensi yang dikenali: flac, mp3, m4a, aac, ogg, opus, wav, wma, ape, wv.
2. Vokal dipisahkan dari musik dengan **Demucs (htdemucs)** di GPU (otomatis pindah ke CPU kalau
   VRAM tidak cukup). Hasilnya disimpan sebagai WAV 16 kHz mono di cache
   `%LOCALAPPDATA%\lyrics-align\cache` (di luar library musik), jadi lagu yang sama tidak
   dipisahkan dua kali.
3. Bahasa dipilih per file dari jenis huruf: kana/kanji → `ja`, Hangul → `ko`, selain itu `en`
   (bisa dipaksa dengan `--lang`).
4. Penyelarasan dengan **stable-ts** (Whisper, model `medium` fp16; kalau tidak muat di GPU turun ke
   `small`), dalam dua tahap:
   - **Tahap 1, satu lagu sekaligus:** semua baris lirik asli diselaraskan ke seluruh track vokal.
     Baris-baris yang berdekatan "berebut" audio, sehingga batas antarbaris jadi rapi.
   - **Tahap 2, per baris:** baris yang gagal dicek di tahap 1 dicoba lagi sendirian, pada potongan
     vokal dari (waktu baris − 0,3 dtk) sampai (waktu baris berikutnya + 0,3 dtk). Tahap ini kurang
     andal, jadi ambang keyakinannya lebih tinggi (`--retry-min-prob`, default 0,35).
5. Setiap baris dicek. Kalau meragukan, baris itu **dibiarkan apa adanya** (tetap timing per baris):
   - semua karakter teks kebagian kata hasil align, dan aligner tidak "menyerah" pada kata mana pun;
   - waktu kata tidak mundur dan berada di dalam jendela baris;
   - kata pertama tidak terlambat jauh dari waktu baris (> maks(2 dtk, setengah jarak ke baris berikut));
   - kata terakhir tidak mulai lebih dari 0,5 dtk setelah waktu baris berikutnya;
   - rentang waktu kata tidak terlalu pendek dibanding panjang baris;
   - tidak ada 3+ segmen berdurasi nol berturut-turut, dan tidak lebih dari separuh segmen berdurasi nol;
   - rata-rata probabilitas kata ≥ `--min-prob` (default 0,20).
   Satu atau dua segmen berdurasi nol yang terselip dibagi rata (menurut jumlah huruf) dengan segmen
   tetangganya, supaya isian karaoke tidak melompat.
6. Sebelum ditulis, setiap baris hasil diverifikasi lagi: kalau tag dibuang, teksnya harus persis
   sama dengan aslinya.

## Setup (sekali saja)

Kebutuhan: Windows, Python 3.x (dipakai hanya untuk menjalankan `uv`), ffmpeg di PATH,
GPU NVIDIA (opsional, tanpa GPU jalan di CPU tetapi jauh lebih lambat).
Perintah di bawah dijalankan dari root repo (`C:\Users\pit\source\repos\MusicPlayer`), di PowerShell.

```powershell
# 1. uv (pengelola environment Python)
python -m pip install --user uv

# 2. environment terisolasi dengan Python 3.12 (uv mengunduh Python 3.12 sendiri)
python -m uv venv --python 3.12 tools/lyrics-align/.venv

# 3. PyTorch build CUDA 12.8 (jalan dengan driver NVIDIA 610)
python -m uv pip install --python tools/lyrics-align/.venv/Scripts/python.exe torch==2.8.0+cu128 torchaudio==2.8.0+cu128 --index-url https://download.pytorch.org/whl/cu128

# 4. sisanya (stable-ts, demucs, dst.) dengan versi persis yang sudah dites
python -m uv pip install --python tools/lyrics-align/.venv/Scripts/python.exe -r tools/lyrics-align/requirements.txt

# cek GPU terdeteksi
tools/lyrics-align/.venv/Scripts/python.exe -c "import torch; print(torch.cuda.is_available())"
```

Saat pertama dijalankan, model Whisper `medium` (~1,5 GB, ke `%USERPROFILE%\.cache\whisper`) dan
model htdemucs (~80 MB, dari Hugging Face) akan diunduh otomatis.

Versi yang dites: Python 3.12.16, torch 2.8.0+cu128, torchaudio 2.8.0+cu128, stable-ts 2.19.1,
openai-whisper 20250625, demucs 4.1.0, numpy 2.5.4 (daftar lengkap di `requirements.txt`).

## Pemakaian

```powershell
$py = "tools/lyrics-align/.venv/Scripts/python.exe"

# Dry run (default): hasil ditulis ke folder --out, library TIDAK disentuh
& $py tools/lyrics-align/align.py "D:\music\Japan\25時、ナイトコードで。" --out C:\temp\aligned

# Beberapa file/folder sekaligus, dengan log per baris
& $py tools/lyrics-align/align.py "D:\music\English" "D:\music\Japan\Ado\x.lrc" -v

# Tulis langsung ke library (salinan asli disimpan sebagai <nama>.lrc.bak)
& $py tools/lyrics-align/align.py "D:\music\Japan" --write
```

Opsi:

| Opsi | Arti |
|---|---|
| `paths...` | file `.lrc` atau folder (dicari rekursif) |
| `--out DIR` | folder hasil dry run (default `tools/lyrics-align/out`). Path lengkap dicerminkan tanpa huruf drive, mis. `D:\music\Japan\a.lrc` → `DIR\music\Japan\a.lrc` |
| `--write` | timpa `.lrc` di tempat. Asli disalin dulu ke `<nama>.lrc.bak`; `.bak` yang sudah ada **tidak pernah** ditimpa. File tanpa satu pun baris yang berhasil tidak diubah |
| `--force` | proses juga file yang sudah punya tag kata (tag lama dibuang dulu untuk baris yang diproses ulang) |
| `--model NAME` | model Whisper (default `medium`, otomatis turun ke `small` kalau VRAM kurang) |
| `--lang ja/ko/en/...` | paksa bahasa (default: deteksi per file) |
| `--device cuda/cpu` | default `cuda` kalau tersedia |
| `--cache-dir DIR` | lokasi cache vokal (default `%LOCALAPPDATA%\lyrics-align\cache`) |
| `--min-prob X` | ambang keyakinan tahap 1 (default 0,20) |
| `--retry-min-prob X` | ambang keyakinan tahap 2 / per baris (default 0,35) |
| `--clamp-next` | jangan biarkan tag kata melewati waktu baris berikutnya (lihat Format) |
| `-v` | tampilkan hasil setiap baris (ok / skip + alasan) |

Tanpa `--force`, file yang sudah berisi tag `<mm:ss.xx>` dilewati, jadi aman dijalankan ulang
pada folder yang sama.

Ringkasan per file berisi: jumlah baris yang diberi tag / total baris lirik, baris yang dilewati
beserta alasannya, dan lama proses (termasuk pemisahan vokal kalau belum ada di cache).

## Format keluaran

Hanya baris **pertama** dari setiap grup timestamp (lirik asli) yang diberi tag. Baris bacaan
(romaji), terjemahan, tag info (`[ar:...]`, `[offset:...]`) dan semua baris lain tetap sama persis
sampai ke byte (encoding, BOM, dan jenis akhir baris CRLF/LF dipertahankan).

```
[00:02.35]<00:03.56>もう<00:03.79>ちょっと<00:04.26>だから<00:05.20>
[00:02.35]Mou chotto dakara
[00:02.35]Karena tinggal sedikit lagi
[00:04.56]<00:04.58>All <00:04.84>of <00:05.16>my <00:05.46>change <00:06.02>I <00:06.50>spent <00:07.24>on <00:07.64>you<00:08.26>
```

- Setiap `<mm:ss.xx>` adalah waktu **absolut** lagu (format sama dengan stempel baris, sentidetik)
  saat segmen sesudahnya mulai. Tag terakhir = saat segmen terakhir selesai.
- Waktu tidak pernah mundur dan tidak pernah lebih awal dari waktu baris.
- Kalau semua `<…>` dibuang (dan spasi beruntun diringkas), hasilnya persis teks asli.
- Segmen: kata untuk bahasa berspasi (spasi pemisah ikut di akhir segmen, mis. `All `), dan potongan
  token Whisper (1–3 karakter) untuk Jepang/Cina.
- Bagian dalam kurung (vokal latar yang dinyanyikan bersamaan, mis.
  `最低だって言われても (light, light 照らして)`) **tidak diselaraskan sendiri**; teksnya digabung ke
  segmen sebelumnya tanpa tag sendiri. Baris yang seluruhnya dalam kurung diselaraskan tanpa tanda kurungnya.
- Baris dengan beberapa stempel `[t1][t2]teks` (reff yang diulang) tidak disentuh, tetapi teksnya tetap
  dipakai sebagai konteks saat penyelarasan.
- `[offset:±ms]` diperhitungkan: tag ditulis dalam "waktu file" yang sama dengan stempel baris.
- **Tag boleh sedikit melewati waktu baris berikutnya.** Stempel baris di banyak file ternyata
  0,2–0,5 dtk lebih awal dari suara sebenarnya, sehingga kata terakhir sering baru selesai setelah baris
  berikutnya mulai (maks. +1,0 dtk, kata terakhir paling lambat mulai +0,5 dtk). Pakai `--clamp-next`
  kalau aplikasi butuh semua tag ≤ waktu baris berikutnya (konsekuensinya kata terakhir bisa
  "dipadatkan" jadi berdurasi hampir nol).

## Keterbatasan

- Kualitas tergantung pemisahan vokal dan model Whisper. Untuk lagu yang sudah dites, sebagian
  besar baris tampak masuk akal, tetapi **tidak ada jaminan per kata**; pengecekan hanya menyaring
  hasil yang jelas rusak.
- Seruan pendek atau bisikan di latar (mis. `(大丈夫)`) hampir selalu dilewati karena keyakinannya rendah.
- Baris yang sangat rapat/cepat (mis. `どうして どうして どうして どうして`) kadang hasilnya terlalu
  padat di ujung baris.
- Vokal latar dalam kurung tidak punya timing sendiri.
- Kata yang dinyanyikan panjang (nada ditahan) kadang durasinya terbagi kurang tepat dengan kata
  sesudahnya.
- Teks lirik yang tidak sesuai dengan yang dinyanyikan (lirik salah, bagian yang tidak tertulis)
  membuat baris sekitarnya ikut gagal atau bergeser.
- Bahasa campuran per baris (mis. bahasa Inggris di lagu Jepang) diselaraskan dengan model bahasa
  file tersebut; biasanya tetap jalan.
- Kira-kira 35–60 detik per lagu di RTX 3050 Ti (±15 dtk pemisahan vokal + 20–45 dtk penyelarasan),
  ditambah ±10 dtk sekali saat memuat model.
- Cache vokal (~7 MB per lagu) tidak dihapus otomatis; hapus saja folder cache-nya kalau perlu ruang.
