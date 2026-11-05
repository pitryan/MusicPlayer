# MusicPlayer

Pemutar musik lokal untuk Windows (WinUI 3) dengan tampilan ala Apple Music, yang mengutamakan kualitas audio: pemutaran
bit-perfect lewat WASAPI exclusive mode, pemeriksaan kualitas sumber, dan lirik tersinkron lengkap dengan cara baca dan
terjemahannya.

## Fitur

- **Library**: scan folder musik (FLAC, MP3, WAV, AAC, M4A), lalu jelajahi Albums, Artists, Songs, Genres, dan Folders,
  ditambah Recently Added, Recently Played, Most Played, dan pencarian. Folder yang dipindah bisa diarahkan ke lokasi
  barunya (Settings → Change location) tanpa kehilangan riwayat putar, favorit, dan playlist.
- **Pemutaran**: WASAPI exclusive mode yang bit-perfect, gapless (lagu berikutnya menyambung tanpa jeda), dan otomatis
  pindah ke shared mode kalau perangkatnya tidak bisa dipakai. Tersedia juga pilihan perangkat output, volume aplikasi yang terpisah dari volume DAC, shuffle, repeat, Autoplay,
  antrean Playing Next dan History, MiniPlayer, layar penuh, serta tombol media Windows.
- **Lirik**: dari tag, file `.lrc`, atau [LRCLIB](https://lrclib.net). Ada editor lirik, dan lapisan cara baca
  (romaji, pinyin) serta terjemahan yang bisa dinyalakan atau dimatikan.
- **Tag dan artwork**: editor tag, pencarian di MusicBrainz, cover dari Cover Art Archive, dan keterangan artis dari
  MusicBrainz, Wikidata, dan Wikipedia.
- **Quality Check**: analisis bit depth dan spektrum untuk mendeteksi file hi-res palsu (hasil upsampling) atau file yang
  berasal dari sumber lossy.
- **Playlist dan favorit.**

## Kebutuhan

- Windows 10 versi 1809 (build 17763) atau lebih baru. Dikembangkan di Windows 11.
- Visual Studio dengan .NET 10 SDK dan workload pengembangan WinUI (Windows App SDK).

## Menjalankan

1. Buka `MusicPlayer.slnx` di Visual Studio.
2. Jadikan proyek **MusicPlayer** sebagai startup project, pilih profil **MusicPlayer (Package)** dan platform **x64**.
3. Tekan F5.

Aplikasi harus berjalan sebagai package (MSIX), karena database dan pengaturannya disimpan di folder data package. Profil
Unpackaged tidak bisa dipakai.

Tes bisa dijalankan dari Visual Studio atau terminal:

```bash
dotnet test MusicPlayer.Library.Tests
```

```bash
dotnet test MusicPlayer.Audio.Tests
```

## Struktur proyek

| Proyek | Isi |
|---|---|
| `MusicPlayer` | Aplikasi WinUI 3: halaman, kontrol, service (pemutaran, lirik, kontrol media Windows), dan view model. |
| `MusicPlayer.Audio` | Engine audio: decode lewat Media Foundation, output lewat WASAPI (NAudio), negosiasi format exclusive mode, seek, dan gapless. |
| `MusicPlayer.Library` | Library SQLite, scanner, tag (TagLib#), lirik (LRC, LRCLIB), MusicBrainz, Quality Check, dan aturan antrean (`TrackQueue`). |
| `MusicPlayer.AudioLab`, `MusicPlayer.LibraryLab` | Program konsol untuk mencoba engine dan library tanpa UI. |
| `*.Tests` | Tes xUnit untuk Audio dan Library. |

## Data aplikasi

Semua data disimpan di folder data package (`%LOCALAPPDATA%\Packages\<nama package>\LocalState`):

- `library.db`: daftar folder dan lagu, riwayat putar, favorit, playlist, dan hasil Quality Check.
- `Lyrics\`: lirik dari LRCLIB yang sudah pernah diunduh.
- `Logs\crash.log`: error yang membuat aplikasi tertutup.

Lokasi database dan crash log juga ditampilkan di Settings → About.

## Catatan teknis

- **Volume di exclusive mode.** Kalau DAC punya volume hardware, slider mengatur volume DAC itu sendiri, jadi output tetap
  bit-perfect. Kalau tidak, sampelnya dikecilkan secara digital.
- **Seek di file FLAC.** Media Foundation tidak bisa seek dengan tepat di tengah pemutaran untuk FLAC tanpa SEEKTABLE.
  Karena itu setiap seek membuka reader baru di posisi tujuan, sehingga ada jeda hening singkat sebelum suaranya menyusul.
  Jedanya paling lama sekitar 1,5 detik, di dekat akhir file hi-res.
- **Gapless.** Sekitar 10 detik sebelum lagu habis, lagu berikutnya dibuka lebih dulu, lalu menyambung di buffer yang sama
  tanpa perangkat dibuka ulang. Ini hanya berlaku kalau formatnya (sample rate, bit depth, jumlah channel) sama persis
  dengan lagu sekarang, supaya sampelnya tidak diubah. Lagu dengan format lain diputar seperti biasa: perangkat dibuka
  ulang untuk formatnya, jadi ada jeda singkat. Repeat One juga menyambung tanpa jeda.
- **Layanan online** (LRCLIB, MusicBrainz, Cover Art Archive, Wikidata, Wikipedia) hanya dihubungi saat fiturnya dipakai.
  Tanpa internet, pemutaran tetap berjalan normal.
