using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MusicPlayer.Audio;
using MusicPlayer.Helpers;
using MusicPlayer.Library;
using MusicPlayer.ViewModels;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace MusicPlayer.Services;

/// <summary>
/// Mengatur antrean lagu dan menghubungkan AudioEngine dengan tampilan.
/// Semua properti publik di sini bisa di-binding dari XAML.
/// </summary>
public sealed class PlaybackService : ObservableObject, IDisposable
{
    private const string IdleTitle = "MusicPlayer";
    private const string IdleSubtitle = "Double-click a song to play it";

    private readonly AudioEngine _engine = new();
    private readonly LibraryDatabase _database;
    private readonly DispatcherQueue _dispatcher;

    // Volume: 0-100 untuk slider. Untuk perangkat dengan volume hardware (exclusive mode), nilainya
    // mengikuti volume perangkat itu sendiri, termasuk saat diubah lewat tombol volume keyboard.
    private double _volume = 100;
    private bool _isMuted;
    private string _volumeDescription = "";
    private AudioEndpointVolume? _deviceVolume;
    private bool _syncingFromDevice;

    // Penanda untuk perubahan volume perangkat yang kita lakukan sendiri. Windows mengirim notifikasi
    // untuk SETIAP perubahan; yang membawa penanda ini diabaikan, supaya slider tidak "melompat balik"
    // saat sedang digeser.
    private static readonly Guid OwnVolumeChange = Guid.NewGuid();
    private readonly DispatcherQueueTimer _timer;

    // Berapa lama DAC tetap dipegang setelah pause, di luar waktu menunggu sisa audio di buffer selesai
    // diputar (AudioEngine.DrainTime). Nol berarti DAC dilepas begitu pause benar-benar hening, supaya
    // aplikasi lain langsung bisa bersuara dan Windows bisa sleep. Naikkan (misalnya ke 5 detik) kalau DAC
    // berbunyi klik setiap lagu dilanjutkan: pause sebentar lalu lanjut tidak perlu membuka DAC lagi.
    private static readonly TimeSpan KeepDeviceAfterPause = TimeSpan.Zero;
    private readonly DispatcherQueueTimer _releaseTimer;

    // HRESULT dari Windows kalau tidak ada perangkat output sama sekali (ERROR_NOT_FOUND)
    private const int NoDeviceFound = unchecked((int)0x80070490);

    // Output pilihan pengguna, diatur dari popup kualitas. _outputDeviceId null = ikut perangkat default Windows.
    private string? _outputDeviceId;
    private string _outputDeviceName = "";
    private bool _useExclusiveMode = true;

    // Output diganti saat lagu diputar: setelah perangkat lama dilepas, lagu langsung dilanjutkan
    private bool _outputChangePending;

    // Lagu berhenti karena perangkat (dicabut, atau perangkat pilihan tidak tersambung), bukan karena pause.
    // Kalau pengguna lalu memilih output lain, lagu langsung dilanjutkan di sana.
    private bool _interruptedByDevice;

    // Untuk riwayat: berapa lama lagu sekarang benar-benar didengarkan (pause dan seek tidak dihitung)
    private double _listenedSeconds;
    private bool _playRecorded;
    // Lagu-lagu yang sudah diputar sebelum lagu sekarang, untuk tombol Previous dan Repeat All
    private readonly List<Track> _previous = [];

    // Urutan Playing Next sebelum diacak, untuk dikembalikan saat shuffle dimatikan
    private List<QueueEntry> _originalOrder = [];
    private bool _isShuffle;
    private RepeatMode _repeatMode = RepeatMode.Off;
    private MMDevice? _device;

    private Track? _currentTrack;
    private ImageSource? _currentCover;
    private string _titleText = IdleTitle;
    private string _subtitleText = IdleSubtitle;
    private string _elapsedText = "0:00";
    private string _remainingText = "0:00";
    private string _statusText = "";
    private double _positionSeconds;
    private double _durationSeconds = 1;
    private Symbol _playPauseSymbol = Symbol.Play;
    private string _qualityBadgeText = "";
    private string _qualityShortText = "";
    private string _qualityDetailText = "";
    private string _outputModeText = "";
    private string _outputDetailText = "";
    private string _deviceText = "";
    private Visibility _qualityVisibility = Visibility.Collapsed;

    public PlaybackService(DispatcherQueue dispatcherQueue, LibraryDatabase database)
    {
        _database = database;
        _dispatcher = dispatcherQueue;
        _engine.PlaybackStopped += OnPlaybackStopped;

        // Perbarui posisi lagu di tampilan 4 kali per detik
        _timer = dispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += (_, _) => OnTimerTick();
        _timer.Start();

        // Melepas DAC setelah pause (lihat KeepDeviceAfterPause). Sekali jalan, dihitung ulang setiap pause.
        _releaseTimer = dispatcherQueue.CreateTimer();
        _releaseTimer.Interval = AudioEngine.DrainTime + KeepDeviceAfterPause;
        _releaseTimer.IsRepeating = false;
        _releaseTimer.Tick += (_, _) => ReleaseDeviceIfPaused();
    }

    /// <summary>Dipicu setiap kali sebuah lagu tercatat di riwayat putar.</summary>
    public event EventHandler? PlayRecorded;

    public Track? CurrentTrack
    {
        get => _currentTrack;
        private set => SetProperty(ref _currentTrack, value);
    }

    public ImageSource? CurrentCover
    {
        get => _currentCover;
        private set => SetProperty(ref _currentCover, value);
    }

    public string TitleText
    {
        get => _titleText;
        private set => SetProperty(ref _titleText, value);
    }

    public string SubtitleText
    {
        get => _subtitleText;
        private set => SetProperty(ref _subtitleText, value);
    }

    public string ElapsedText
    {
        get => _elapsedText;
        private set => SetProperty(ref _elapsedText, value);
    }

    public string RemainingText
    {
        get => _remainingText;
        private set => SetProperty(ref _remainingText, value);
    }

    /// <summary>Mode output (EXCLUSIVE/SHARED), atau pesan error kalau ada masalah.</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public double PositionSeconds
    {
        get => _positionSeconds;
        private set => SetProperty(ref _positionSeconds, value);
    }

    public double DurationSeconds
    {
        get => _durationSeconds;
        private set => SetProperty(ref _durationSeconds, value);
    }

    public Symbol PlayPauseSymbol
    {
        get => _playPauseSymbol;
        private set => SetProperty(ref _playPauseSymbol, value);
    }

    /// <summary>Golongan kualitas di popup: "Hi-Res", "Lossless", atau "Lossy".</summary>
    public string QualityBadgeText
    {
        get => _qualityBadgeText;
        private set => SetProperty(ref _qualityBadgeText, value);
    }

    /// <summary>Versi pendek untuk LCD: "Hi-Res", "Lossless", "MP3", atau "AAC".</summary>
    public string QualityShortText
    {
        get => _qualityShortText;
        private set => SetProperty(ref _qualityShortText, value);
    }

    /// <summary>Format file, misalnya "24-bit 96 kHz FLAC".</summary>
    public string QualityDetailText
    {
        get => _qualityDetailText;
        private set => SetProperty(ref _qualityDetailText, value);
    }

    /// <summary>"Exclusive mode · bit-perfect" atau "Shared mode".</summary>
    public string OutputModeText
    {
        get => _outputModeText;
        private set => SetProperty(ref _outputModeText, value);
    }

    /// <summary>Format yang benar-benar dikirim ke perangkat, atau alasan memakai shared mode.</summary>
    public string OutputDetailText
    {
        get => _outputDetailText;
        private set => SetProperty(ref _outputDetailText, value);
    }

    /// <summary>Nama perangkat output, misalnya "Headset (HiBy FC5)".</summary>
    public string DeviceText
    {
        get => _deviceText;
        private set => SetProperty(ref _deviceText, value);
    }

    /// <summary>Badge kualitas hanya tampil saat ada lagu yang diputar.</summary>
    public Visibility QualityVisibility
    {
        get => _qualityVisibility;
        private set => SetProperty(ref _qualityVisibility, value);
    }

    /// <summary>
    /// Shuffle mengacak urutan Playing Next. Saat dimatikan, sisa antrean kembali ke urutan aslinya.
    /// </summary>
    public bool IsShuffle
    {
        get => _isShuffle;
        set
        {
            if (!SetProperty(ref _isShuffle, value))
                return;

            // Lagu Autoplay bukan bagian dari antrean sendiri: dibuang dulu, lalu disiapkan ulang sesudahnya
            RemoveAutoplay();
            if (value)
                ShuffleUpNext();
            else
                ReplaceUpNext(QueueOrder.Restored(UpNext.ToList(), _originalOrder));
            TopUpAutoplay();
        }
    }

    public RepeatMode RepeatMode
    {
        get => _repeatMode;
        set
        {
            if (SetProperty(ref _repeatMode, value))
            {
                OnPropertyChanged(nameof(IsRepeatOn));
                OnPropertyChanged(nameof(IsRepeatOne));
                OnPropertyChanged(nameof(RepeatSymbol));

                // Repeat dan Autoplay tidak berjalan bersamaan: repeat mengulang antrean, jadi lagu Autoplay dibuang
                if (value == RepeatMode.Off)
                    TopUpAutoplay();
                else
                    RemoveAutoplay();
            }
        }
    }

    // ── Autoplay ─────────────────────────────────────────────

    // Jumlah lagu Autoplay yang disiapkan di antrean setelah antrean sendiri habis
    private const int AutoplayCount = 5;
    private bool _autoplayEnabled;

    /// <summary>
    /// Sumber lagu Autoplay: (lagu terakhir, path yang dikecualikan, jumlah) → lagu. Disetel App
    /// (AutoplayPicker dengan library, riwayat putar, dan favorit); null berarti Autoplay tidak tersedia.
    /// </summary>
    public Func<Track, ISet<string>, int, IReadOnlyList<Track>>? AutoplaySource { get; set; }

    /// <summary>
    /// Autoplay, seperti Apple Music: saat antrean habis, pemutaran berlanjut dengan lagu yang mirip dari library.
    /// Lagu-lagunya tampil di bawah antrean sendiri; Repeat yang menyala mendahuluinya.
    /// </summary>
    public bool AutoplayEnabled
    {
        get => _autoplayEnabled;
        set
        {
            if (!SetProperty(ref _autoplayEnabled, value))
                return;
            if (value)
                TopUpAutoplay();
            else
                RemoveAutoplay();
        }
    }

    /// <summary>
    /// Menyiapkan lagu Autoplay di antrean kalau antrean sendiri sudah habis. Dipanggil setiap lagu baru mulai, dan
    /// saat Autoplay atau Repeat berubah.
    /// </summary>
    private void TopUpAutoplay()
    {
        if (!_autoplayEnabled || AutoplaySource is null || CurrentTrack is not { } current || RepeatMode != RepeatMode.Off)
            return;
        if (UpNext.Any(e => !e.IsAutoplay))
            return; // antrean sendiri masih ada: Autoplay baru setelah itu
        int missing = AutoplayCount - UpNext.Count;
        if (missing <= 0)
            return;

        var exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { current.FilePath };
        foreach (var entry in UpNext)
            exclude.Add(entry.Track.FilePath);
        foreach (var track in _previous)
            exclude.Add(track.FilePath);

        foreach (var track in AutoplaySource(current, exclude, missing))
            UpNext.Add(new QueueEntry(track) { IsAutoplay = true });
    }

    private void RemoveAutoplay()
    {
        for (int i = UpNext.Count - 1; i >= 0; i--)
        {
            if (UpNext[i].IsAutoplay)
                UpNext.RemoveAt(i);
        }
        _originalOrder.RemoveAll(e => e.IsAutoplay);
    }

    /// <summary>Volume dari 0 sampai 100.</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            if (SetProperty(ref _volume, Math.Clamp(Math.Round(value), 0, 100)))
            {
                OnPropertyChanged(nameof(VolumeGlyph));
                ApplyVolume();
            }
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (SetProperty(ref _isMuted, value))
            {
                OnPropertyChanged(nameof(VolumeGlyph));
                ApplyVolume();
            }
        }
    }

    /// <summary>Ikon tombol volume: speaker dicoret saat mute atau volume 0.</summary>
    public string VolumeGlyph => IsMuted || Volume == 0 ? "\uE74F" : "\uE767";

    /// <summary>Penjelasan volume mana yang sedang diatur, ditampilkan di bawah slider.</summary>
    public string VolumeDescription
    {
        get => _volumeDescription;
        private set => SetProperty(ref _volumeDescription, value);
    }

    /// <summary>Tombol repeat menyala untuk mode All dan One.</summary>
    public bool IsRepeatOn => RepeatMode != RepeatMode.Off;

    /// <summary>Ikon tombol repeat: ada angka 1 untuk mode One.</summary>
    public Symbol RepeatSymbol => RepeatMode == RepeatMode.One ? Symbol.RepeatOne : Symbol.RepeatAll;

    /// <summary>True untuk Repeat One. Menentukan ikon tombol repeat: "ulangi semua" atau "ulangi satu".</summary>
    public bool IsRepeatOne => RepeatMode == RepeatMode.One;

    public void CycleRepeat() => RepeatMode = RepeatMode switch
    {
        RepeatMode.Off => RepeatMode.All,
        RepeatMode.All => RepeatMode.One,
        _ => RepeatMode.Off,
    };

    private bool _isPlaying;
    private QualityKind _qualityKind = QualityKind.Lossless;

    /// <summary>Golongan kualitas lagu sekarang. Menentukan ikon di badge dan popup kualitas.</summary>
    public QualityKind QualityKind
    {
        get => _qualityKind;
        private set
        {
            if (!SetProperty(ref _qualityKind, value))
                return;

            OnPropertyChanged(nameof(IsHiResQuality));
            OnPropertyChanged(nameof(IsLosslessQuality));
            OnPropertyChanged(nameof(IsLossyQuality));
        }
    }

    public bool IsHiResQuality => QualityKind == QualityKind.HiRes;
    public bool IsLosslessQuality => QualityKind == QualityKind.Lossless;
    public bool IsLossyQuality => QualityKind == QualityKind.Lossy;

    /// <summary>True saat lagu benar-benar berbunyi. Menentukan ikon Play atau Pause di tombol transport.</summary>
    public bool IsPlaying
    {
        get => _isPlaying;
        private set => SetProperty(ref _isPlaying, value);
    }

    /// <summary>Pilihan di daftar output popup kualitas. Diisi ulang oleh <see cref="RefreshOutputDevices"/>.</summary>
    public ObservableCollection<OutputDeviceItem> OutputDevices { get; } = [];

    /// <summary>ID perangkat output pilihan, atau null untuk perangkat default Windows.</summary>
    public string? OutputDeviceId => _outputDeviceId;

    /// <summary>Nama perangkat pilihan, untuk pesan saat perangkat itu tidak tersambung.</summary>
    public string OutputDeviceName => _outputDeviceName;

    /// <summary>
    /// Exclusive mode (bit-perfect) atau shared mode (lewat mixer Windows, bersama aplikasi lain).
    /// Lagu yang sedang diputar langsung pindah ke mode yang baru.
    /// </summary>
    public bool UseExclusiveMode
    {
        get => _useExclusiveMode;
        set
        {
            if (!SetProperty(ref _useExclusiveMode, value))
                return;

            OnPropertyChanged(nameof(OutputModeDescription));
            ApplyOutputChange();
        }
    }

    /// <summary>Penjelasan di bawah tombol exclusive mode.</summary>
    public string OutputModeDescription => UseExclusiveMode
        ? "Bit-perfect. While a song plays, other apps can't play sound on this device."
        : "Plays through the Windows mixer, together with other apps. Not bit-perfect.";

    /// <summary>
    /// Memilih perangkat output (null = perangkat default Windows). Lagu yang sedang diputar langsung pindah
    /// ke perangkat itu di posisi yang sama; lagu yang sedang di-pause memakai perangkat itu saat Play.
    /// </summary>
    public void SelectOutputDevice(string? deviceId, string deviceName)
    {
        if (deviceId == _outputDeviceId)
            return;

        _outputDeviceId = deviceId;
        _outputDeviceName = deviceId is null ? "" : deviceName;
        OnPropertyChanged(nameof(OutputDeviceId));
        ApplyOutputChange();
    }

    /// <summary>
    /// Mengisi ulang daftar output, lalu mengembalikan pilihan yang sedang aktif. Dipanggil setiap kali popup
    /// kualitas dibuka, supaya DAC yang baru dicolok langsung muncul. Perangkat pilihan yang sedang tidak
    /// tersambung tetap tampil dengan keterangan, supaya jelas apa yang sedang dipilih.
    /// </summary>
    public OutputDeviceItem RefreshOutputDevices()
    {
        string defaultName = "";
        var devices = new List<OutputDeviceItem>();

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            try
            {
                using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                defaultName = defaultDevice.FriendlyName;
            }
            catch (COMException)
            {
                // Tidak ada perangkat default, misalnya semua perangkat sedang dicabut
            }

            using var active = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var device in active)
            {
                using (device)
                    devices.Add(new OutputDeviceItem(device.ID, device.FriendlyName, device.FriendlyName));
            }
        }
        catch (COMException)
        {
            // Layanan audio Windows bermasalah: daftar hanya berisi pilihan default
        }

        OutputDevices.Clear();
        var defaultItem = new OutputDeviceItem(null, "", defaultName.Length > 0 ? $"Windows default ({defaultName})" : "Windows default");
        OutputDevices.Add(defaultItem);
        foreach (var item in devices.OrderBy(d => d.Label, StringComparer.CurrentCultureIgnoreCase))
            OutputDevices.Add(item);

        if (_outputDeviceId is null)
            return defaultItem;

        var selected = OutputDevices.FirstOrDefault(d => d.Id == _outputDeviceId);
        if (selected is null)
        {
            selected = new OutputDeviceItem(_outputDeviceId, _outputDeviceName, $"{_outputDeviceName} (not connected)");
            OutputDevices.Add(selected);
        }
        return selected;
    }

    /// <summary>
    /// Lagu-lagu yang akan diputar setelah lagu sekarang (panel Playing Next). Urutannya bisa diubah
    /// dengan digeser langsung di panel itu; Next selalu mengambil lagu paling atas.
    /// </summary>
    public ObservableCollection<QueueEntry> UpNext { get; } = [];

    /// <summary>
    /// Memutar daftar lagu mulai dari lagu ke-startIndex. Lagu sesudahnya masuk Playing Next,
    /// dan lagu sebelumnya bisa dicapai dengan tombol Previous.
    /// </summary>
    public void PlayQueue(IReadOnlyList<Track> tracks, int startIndex)
    {
        if (startIndex < 0 || startIndex >= tracks.Count)
            return;

        _previous.Clear();
        UpNext.Clear();

        if (IsShuffle)
        {
            // Shuffle aktif: lagu yang dipilih diputar dulu, lalu SEMUA lagu lain di daftar itu diacak,
            // termasuk yang letaknya sebelum lagu yang dipilih (kalau tidak, lagu-lagu itu terlewat)
            for (int i = 0; i < tracks.Count; i++)
            {
                if (i != startIndex)
                    UpNext.Add(new QueueEntry(tracks[i]));
            }
            ShuffleUpNext();
        }
        else
        {
            _previous.AddRange(tracks.Take(startIndex));
            foreach (var track in tracks.Skip(startIndex + 1))
                UpNext.Add(new QueueEntry(track));
        }

        Play(tracks[startIndex]);
    }

    /// <summary>
    /// Tombol Shuffle di halaman album, artis, dan folder: menyalakan shuffle, lalu memutar
    /// daftar itu mulai dari lagu acak, seperti di Apple Music.
    /// </summary>
    public void PlayShuffled(IReadOnlyList<Track> tracks)
    {
        if (tracks.Count == 0)
            return;

        IsShuffle = true;
        PlayQueue(tracks, Random.Shared.Next(tracks.Count));
    }

    /// <summary>Langsung memutar sebuah lagu tanpa mengubah Playing Next (misalnya dari History).</summary>
    public void PlayNow(Track track)
    {
        if (CurrentTrack is not null)
            _previous.Add(CurrentTrack);
        Play(track);
    }

    /// <summary>Menaruh lagu di urutan paling atas Playing Next. Kalau belum ada yang diputar, langsung diputar.</summary>
    public void PlayNext(Track track)
    {
        if (CurrentTrack is null)
            Play(track);
        else
            UpNext.Insert(0, new QueueEntry(track));
    }

    /// <summary>Menaruh lagu di urutan paling bawah Playing Next. Kalau belum ada yang diputar, langsung diputar.</summary>
    public void AddToQueue(Track track)
    {
        if (CurrentTrack is null)
        {
            Play(track);
            return;
        }

        // Antrean sendiri selalu di atas lagu Autoplay
        int autoplayStart = -1;
        for (int i = 0; i < UpNext.Count; i++)
        {
            if (UpNext[i].IsAutoplay)
            {
                autoplayStart = i;
                break;
            }
        }
        if (autoplayStart < 0)
            UpNext.Add(new QueueEntry(track));
        else
            UpNext.Insert(autoplayStart, new QueueEntry(track));
    }

    /// <summary>Langsung memutar sebuah lagu dari Playing Next. Lagu-lagu di atasnya dilewati.</summary>
    public void PlayFromQueue(QueueEntry entry)
    {
        int index = UpNext.IndexOf(entry);
        if (index < 0)
            return;

        for (int i = 0; i <= index; i++)
            UpNext.RemoveAt(0);

        if (CurrentTrack is not null)
            _previous.Add(CurrentTrack);
        Play(entry.Track);
    }

    public void RemoveFromQueue(QueueEntry entry) => UpNext.Remove(entry);

    public void ClearQueue()
    {
        UpNext.Clear();
        _originalOrder.Clear();
        TopUpAutoplay(); // dengan Autoplay menyala, pemutaran tetap berlanjut setelah lagu ini
    }

    public void TogglePlayPause()
    {
        if (CurrentTrack is null)
            return;

        if (_engine.State == PlaybackState.Playing)
            Pause();
        else
            Resume();

        RefreshPosition();
    }

    private void Pause()
    {
        _interruptedByDevice = false;
        _engine.Pause();

        // Hitungan dimulai ulang setiap pause, supaya pause-lanjut-pause yang cepat tidak melepas DAC
        // sebelum sisa audio di buffer selesai diputar
        _releaseTimer.Stop();
        _releaseTimer.Start();
    }

    private void Resume()
    {
        _releaseTimer.Stop();

        // Output sedang diganti dan perangkat lama belum sempat dilepas: lepas sekarang, supaya Play memakai
        // pengaturan yang baru
        if (_outputChangePending)
        {
            _outputChangePending = false;
            _engine.ReleaseDevice();
        }

        // DAC sudah dilepas selama pause: buka lagi dulu. Kalau gagal, lagu tetap di-pause di posisinya dan
        // pesannya tampil di kanan atas. Pengguna ingin memutar lagu, jadi begitu output lain dipilih, lagu lanjut.
        if (_engine.IsDeviceReleased && !ReopenDevice())
        {
            _interruptedByDevice = true;
            return;
        }

        _engine.Play();
        if (_engine.State == PlaybackState.Playing)
        {
            _interruptedByDevice = false;
            StatusText = "";
        }
    }

    /// <summary>Dipanggil timer setelah pause. Kalau lagu sudah dilanjutkan atau diganti, tidak ada yang dilepas.</summary>
    private void ReleaseDeviceIfPaused()
    {
        _releaseTimer.Stop();
        if (_engine.State == PlaybackState.Paused && !_engine.IsDeviceReleased)
            _engine.ReleaseDevice();

        // Output diganti saat lagu diputar: sisa audio sudah habis diputar dan perangkat lama sudah dilepas,
        // jadi lanjutkan dengan pengaturan yang baru
        if (_outputChangePending)
        {
            Resume();
            RefreshPosition();
        }
    }

    /// <summary>
    /// Perangkat atau mode output diganti. Lagu yang sedang diputar langsung pindah: di-pause, ditunggu sampai
    /// sisa audio di buffer habis, perangkat lama dilepas, lalu dibuka lagi dengan pengaturan baru di posisi
    /// yang sama. Lagu yang sedang di-pause hanya dilepas dari perangkat lama; pengaturan baru dipakai saat Play.
    /// </summary>
    private void ApplyOutputChange()
    {
        if (CurrentTrack is null)
            return; // belum ada lagu: pengaturan baru dipakai saat lagu berikutnya diputar

        if (_engine.IsDeviceReleased)
        {
            // Lagu berhenti karena perangkatnya bermasalah, lalu pengguna memilih output lain: lanjutkan di sana
            if (_interruptedByDevice)
            {
                Resume();
                RefreshPosition();
            }
            return;
        }

        if (_engine.State == PlaybackState.Playing)
        {
            Pause();
            _outputChangePending = true;
        }
        else
        {
            _releaseTimer.Stop();
            _engine.ReleaseDevice();
        }

        RefreshPosition();
    }

    /// <summary>
    /// Perangkat untuk membuka lagu: pilihan pengguna, atau perangkat default Windows. Perangkat pilihan yang
    /// sedang tidak tersambung sengaja TIDAK diganti diam-diam dengan perangkat lain, supaya suara tidak
    /// tiba-tiba keluar dari tempat yang tidak diharapkan (misalnya speaker laptop).
    /// </summary>
    private MMDevice GetOutputDevice()
    {
        using var enumerator = new MMDeviceEnumerator();
        if (_outputDeviceId is null)
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        MMDevice device;
        try
        {
            device = enumerator.GetDevice(_outputDeviceId);
        }
        catch (COMException)
        {
            throw new OutputDeviceUnavailableException(_outputDeviceName);
        }

        if (device.State != DeviceState.Active)
        {
            device.Dispose();
            throw new OutputDeviceUnavailableException(_outputDeviceName);
        }
        return device;
    }

    /// <summary>Perangkat output pilihan pengguna sedang tidak tersambung.</summary>
    private sealed class OutputDeviceUnavailableException : Exception
    {
        public OutputDeviceUnavailableException(string deviceName) : base($"{deviceName} isn't connected.")
        {
            DeviceName = deviceName;
        }

        public string DeviceName { get; }
    }

    /// <summary>
    /// Membuka DAC lagi untuk lagu yang sedang di-pause, di posisi terakhir. Selalu memakai perangkat default
    /// saat ini, sama seperti saat memutar lagu baru. Jadi kalau DAC dicabut selama pause, lagu lanjut di
    /// perangkat yang dipilih Windows, tapi hanya setelah pengguna menekan Play.
    /// </summary>
    private bool ReopenDevice()
    {
        MMDevice? device = null;

        try
        {
            device = GetOutputDevice();
            _engine.ReopenDevice(device, UseExclusiveMode);
            DetachDeviceVolume();
            _device?.Dispose();
            _device = device;
            AttachVolume(device); // sebelum Play, supaya tidak ada sesaat suara dengan volume yang salah
            UpdateQualityInfo(CurrentTrack!, device);
            return true;
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(device, _device))
                device?.Dispose();
            StatusText = $"Couldn't resume: {DescribeDeviceError(ex)}";
            return false;
        }
    }

    private static string DescribeDeviceError(Exception ex) => ex switch
    {
        OutputDeviceUnavailableException missing => $"{missing.DeviceName} isn't connected.",
        { HResult: AudioClientErrorCode.DeviceInUse } => "the audio device is in use by another app.",
        { HResult: AudioClientErrorCode.DeviceInvalidated } => "the audio device was disconnected.",
        { HResult: NoDeviceFound } => "no audio output device was found.",
        _ => ex.Message,
    };

    /// <summary>Tombol Next. Dalam mode Repeat One pun, tombol ini tetap pindah ke lagu berikutnya.</summary>
    public void Next() => Advance();

    private void Advance()
    {
        if (UpNext.Count == 0)
        {
            if (RepeatMode == RepeatMode.Off || CurrentTrack is null)
            {
                StopAll();
                return;
            }

            RestartQueue();
            return;
        }

        var next = UpNext[0];
        UpNext.RemoveAt(0);
        if (CurrentTrack is not null)
            _previous.Add(CurrentTrack);
        Play(next.Track);
    }

    /// <summary>
    /// Repeat: antrean sudah habis, jadi semua lagu yang tadi diputar (ditambah lagu sekarang) diputar
    /// lagi dari awal. Kalau shuffle aktif, urutannya diacak ulang.
    /// </summary>
    private void RestartQueue()
    {
        var entries = _previous.Append(CurrentTrack!).Select(t => new QueueEntry(t)).ToList();
        _previous.Clear();

        if (IsShuffle)
        {
            _originalOrder = entries.ToList();
            entries = QueueOrder.Shuffled(entries);
        }

        var first = entries[0];
        entries.RemoveAt(0);
        ReplaceUpNext(entries);
        Play(first.Track);
    }

    private void ShuffleUpNext()
    {
        _originalOrder = UpNext.ToList();
        ReplaceUpNext(QueueOrder.Shuffled(_originalOrder));
    }

    private void ReplaceUpNext(List<QueueEntry> entries)
    {
        UpNext.Clear();
        foreach (var entry in entries)
            UpNext.Add(entry);
    }

    public void Previous()
    {
        if (CurrentTrack is null)
            return;

        // Seperti pemutar musik pada umumnya: kalau lagu sudah berjalan lebih dari
        // 3 detik, tombol ini mengulang lagu dari awal, bukan pindah ke lagu sebelumnya
        if (_engine.Position > TimeSpan.FromSeconds(3) || _previous.Count == 0)
        {
            _engine.Seek(TimeSpan.Zero);
            RefreshPosition();
            return;
        }

        // Lagu sekarang dikembalikan ke paling atas Playing Next
        var previous = _previous[^1];
        _previous.RemoveAt(_previous.Count - 1);
        UpNext.Insert(0, new QueueEntry(CurrentTrack));
        Play(previous);
    }

    /// <summary>
    /// Posisi lagu saat ini, dibaca langsung dari engine. PositionSeconds hanya diperbarui 4 kali per detik, terlalu
    /// jarang untuk menyorot baris lirik tepat waktu.
    /// </summary>
    public TimeSpan Position => CurrentTrack is null ? TimeSpan.Zero : _engine.Position;

    /// <summary>Lompat ke posisi tertentu, dari 0 (awal lagu) sampai 1 (akhir lagu).</summary>
    public void SeekToFraction(double fraction) =>
        SeekTo(TimeSpan.FromSeconds(Math.Clamp(fraction, 0, 1) * _engine.Duration.TotalSeconds));

    /// <summary>Lompat ke waktu tertentu di lagu ini, misalnya saat baris lirik diklik.</summary>
    public void SeekTo(TimeSpan position)
    {
        if (CurrentTrack is null)
            return;

        var duration = _engine.Duration;
        _engine.Seek(position < TimeSpan.Zero ? TimeSpan.Zero : position > duration ? duration : position);
        RefreshPosition();
    }

    public void Dispose()
    {
        _timer.Stop();
        _releaseTimer.Stop();
        DetachDeviceVolume();
        _engine.Dispose();
        _device?.Dispose();
    }

    private void ApplyVolume()
    {
        if (!_syncingFromDevice)
            _engine.SetVolume((float)(Volume / 100), IsMuted);
    }

    private void AttachVolume(MMDevice device)
    {
        switch (_engine.VolumeKind)
        {
            case VolumeKind.Device:
                // Volume perangkat dipakai apa adanya, TIDAK ditimpa dengan nilai slider,
                // supaya suara tidak tiba-tiba keras saat lagu pertama diputar
                try
                {
                    var endpoint = device.AudioEndpointVolume;
                    endpoint.NotificationGuid = OwnVolumeChange;
                    SyncFromDevice(endpoint.MasterVolumeLevelScalar, endpoint.Mute);
                    endpoint.OnVolumeNotification += OnDeviceVolumeChanged;
                    _deviceVolume = endpoint;
                }
                catch (Exception)
                {
                    // Gagal membaca volume perangkat: slider tetap bisa dipakai untuk mengubahnya
                }
                VolumeDescription = "Device volume: adjusts your DAC or speakers directly, so playback stays bit-perfect.";
                break;

            case VolumeKind.Digital:
                ApplyVolume();
                VolumeDescription = "Digital volume: this device has no hardware volume, so playback is not bit-perfect below 100.";
                break;

            default:
                ApplyVolume();
                VolumeDescription = "App volume, the same as the MusicPlayer slider in the Windows volume mixer.";
                break;
        }
    }

    private void DetachDeviceVolume()
    {
        if (_deviceVolume is null)
            return;

        _deviceVolume.OnVolumeNotification -= OnDeviceVolumeChanged;
        _deviceVolume = null;
    }

    private void OnDeviceVolumeChanged(AudioVolumeNotificationData data)
    {
        if (data.EventContext == OwnVolumeChange)
            return; // perubahan dari slider kita sendiri

        // Bisa dipanggil dari thread lain (misalnya saat volume diubah dari Windows), jadi dipindah ke UI thread
        _dispatcher.TryEnqueue(() => SyncFromDevice(data.MasterVolume, data.Muted));
    }

    private void SyncFromDevice(float volume, bool muted)
    {
        _syncingFromDevice = true; // nilai ini datang dari perangkat, jadi tidak perlu dikirim balik
        Volume = volume * 100;
        IsMuted = muted;
        _syncingFromDevice = false;
    }

    private void Play(Track track)
    {
        _releaseTimer.Stop(); // lagu baru selalu langsung diputar, jadi DAC tidak perlu dilepas
        _outputChangePending = false;
        _interruptedByDevice = false;
        MMDevice? device = null;

        try
        {
            // Perangkat pilihan pengguna, atau perangkat default Windows saat ini
            device = GetOutputDevice();

            _engine.Open(track.FilePath, device, UseExclusiveMode); // player lama ditutup lebih dulu di sini
            DetachDeviceVolume();
            _device?.Dispose();
            _device = device;
            AttachVolume(device); // sebelum Play, supaya tidak ada sesaat suara dengan volume yang salah
            _engine.Play();

            ShowTrack(track);
            StatusText = "";
            UpdateQualityInfo(track, device);
        }
        catch (OutputDeviceUnavailableException ex)
        {
            WaitForDevice(track, ex.DeviceName);
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(device, _device))
                device?.Dispose();
            StatusText = $"Couldn't play \"{track.DisplayTitle}\": {ex.Message}";
        }

        RefreshPosition();
    }

    /// <summary>
    /// Menjalankan <paramref name="write"/> (menulis tag) dengan file lagu yang sedang dimuat dilepas lebih dulu, karena
    /// Windows tidak mengizinkan file yang sedang dibuka diganti. Setelah selesai, lagu itu dimuat lagi di posisi yang
    /// sama dan lanjut berbunyi kalau tadinya berbunyi. Lagu lain tidak terpengaruh.
    /// </summary>
    public async Task WithFilesReleasedAsync(IReadOnlyCollection<string> paths, Func<Task> write)
    {
        var track = CurrentTrack;
        if (track is null || !paths.Contains(track.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            await write();
            return;
        }

        var position = _engine.Position;
        bool wasPlaying = _engine.State == PlaybackState.Playing;
        _releaseTimer.Stop();
        _engine.Close();
        try
        {
            await write();
        }
        finally
        {
            ReopenAfterEdit(track, position, wasPlaying);
        }
    }

    private void ReopenAfterEdit(Track track, TimeSpan position, bool wasPlaying)
    {
        MMDevice? device = null;
        try
        {
            if (wasPlaying)
            {
                device = GetOutputDevice();
                _engine.Open(track.FilePath, device, UseExclusiveMode);
                DetachDeviceVolume();
                _device?.Dispose();
                _device = device;
                AttachVolume(device);
                _engine.Seek(position);
                _engine.Play();
                UpdateQualityInfo(track, device);
            }
            else
            {
                // Sedang di-pause: file dimuat tanpa perangkat, jadi DAC tetap bebas; Play membukanya lagi seperti biasa
                _engine.OpenWithoutDevice(track.FilePath);
                _engine.Seek(position);
            }
        }
        catch (OutputDeviceUnavailableException ex)
        {
            if (!ReferenceEquals(device, _device))
                device?.Dispose();
            _engine.OpenWithoutDevice(track.FilePath);
            _engine.Seek(position);
            _interruptedByDevice = true;
            StatusText = $"{ex.DeviceName} isn't connected. Plug it in, or choose another output from the quality badge.";
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(device, _device))
                device?.Dispose();
            StatusText = $"Couldn't resume \"{track.DisplayTitle}\": {ex.Message}";
        }

        RefreshPosition();
    }

    /// <summary>
    /// Data lagu yang baru (setelah tag-nya diedit) menggantikan data lama di lagu yang sedang diputar, antrean, dan
    /// daftar Previous, supaya LCD dan panel antrean langsung menampilkan judul yang baru.
    /// </summary>
    public void UpdateTrackInfo(IReadOnlyList<Track> tracks)
    {
        var byPath = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tracks)
            byPath[t.FilePath] = t;

        for (int i = 0; i < _previous.Count; i++)
        {
            if (byPath.TryGetValue(_previous[i].FilePath, out var updated))
                _previous[i] = updated;
        }
        foreach (var entry in UpNext.Concat(_originalOrder))
        {
            if (byPath.TryGetValue(entry.Track.FilePath, out var updated))
                entry.UpdateTrack(updated);
        }

        if (CurrentTrack is { } current && byPath.TryGetValue(current.FilePath, out var now))
        {
            CurrentTrack = now;
            TitleText = now.DisplayTitle;
            SubtitleText = $"{now.DisplayArtist} — {now.DisplayAlbum}";
            _ = LoadCoverAsync(now);
        }
    }

    private void ShowTrack(Track track)
    {
        CurrentTrack = track;
        _listenedSeconds = 0;
        _playRecorded = false;
        TitleText = track.DisplayTitle;
        SubtitleText = $"{track.DisplayArtist} — {track.DisplayAlbum}";
        _ = LoadCoverAsync(track);
        TopUpAutoplay();
    }

    /// <summary>
    /// Perangkat pilihan sedang tidak tersambung. Lagunya tetap dimuat dan di-pause di awal, supaya judul dan badge
    /// kualitas tampil dan output lain bisa dipilih dari popup-nya. Begitu output lain dipilih (atau Play ditekan
    /// setelah perangkatnya dicolok), lagu langsung diputar.
    /// </summary>
    private void WaitForDevice(Track track, string deviceName)
    {
        try
        {
            _engine.OpenWithoutDevice(track.FilePath);
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't play \"{track.DisplayTitle}\": {ex.Message}";
            return;
        }

        DetachDeviceVolume();
        _device?.Dispose();
        _device = null;
        ShowTrack(track);
        _interruptedByDevice = true;
        StatusText = $"{deviceName} isn't connected. Plug it in, or choose another output from the quality badge.";
        UpdateQualityInfo(track, device: null);
    }

    private void StopAll()
    {
        _releaseTimer.Stop();
        _outputChangePending = false;
        _interruptedByDevice = false;
        _engine.Close();
        DetachDeviceVolume();
        _previous.Clear();
        CurrentTrack = null;
        CurrentCover = null;
        TitleText = IdleTitle;
        SubtitleText = IdleSubtitle;
        StatusText = "";
        QualityVisibility = Visibility.Collapsed;
        RefreshPosition();
    }

    /// <summary>Isi popup kualitas. <paramref name="device"/> null berarti perangkat pilihan sedang tidak tersambung.</summary>
    private void UpdateQualityInfo(Track track, MMDevice? device)
    {
        var quality = AudioQuality.Describe(track);
        QualityBadgeText = quality.Label;
        QualityShortText = quality.ShortBadge;
        QualityDetailText = quality.Detail;
        QualityKind = quality.Kind;
        DeviceText = device?.FriendlyName ?? "";

        if (device is null)
        {
            OutputModeText = "Not connected";
            OutputDetailText = $"{_outputDeviceName} isn't connected. Plug it in, or choose another output below.";
        }
        else if (_engine.ExclusiveFormat is { } format)
        {
            string rate = AudioQuality.FormatSampleRate(format.SampleRate);
            OutputModeText = "Exclusive mode · bit-perfect";
            OutputDetailText = format.ContainerBits == format.ValidBits
                ? $"Sent to the device as {format.ValidBits}-bit {rate}"
                : $"Sent to the device as {format.ValidBits}-bit {rate} in a {format.ContainerBits}-bit container";
        }
        else
        {
            OutputModeText = "Shared mode";
            string mixed = $"Windows mixes the audio at {DescribeMixRate(device)}.";
            OutputDetailText = !UseExclusiveMode ? $"Exclusive mode is turned off. {mixed}"
                : _engine.FallbackReason is { } reason ? $"{reason} {mixed}"
                : mixed;
        }

        QualityVisibility = Visibility.Visible;
    }

    /// <summary>Sample rate mixer Windows untuk perangkat ini (diatur di Sound settings).</summary>
    private static string DescribeMixRate(MMDevice device)
    {
        try
        {
            using var client = device.CreateAudioClient();
            return AudioQuality.FormatSampleRate(client.MixFormat.SampleRate);
        }
        catch (Exception)
        {
            return "its default sample rate";
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // WasapiPlayer mengirim event ini lewat UI thread, jadi aman mengubah tampilan di sini
        if (e.Exception is not null)
        {
            // Perangkat bermasalah di tengah lagu, paling sering karena DAC dicabut. Player yang rusak dibuang
            // dan lagu berhenti di posisinya (antrean tidak maju). Lagu sengaja tidak dilanjutkan otomatis di
            // perangkat lain, misalnya speaker laptop, supaya suara tidak tiba-tiba keluar dari tempat yang
            // tidak diharapkan. Tombol Play melanjutkannya di perangkat default saat itu.
            _releaseTimer.Stop();
            _outputChangePending = false;
            _engine.ReleaseDevice();
            _interruptedByDevice = true;
            StatusText = e.Exception.HResult != AudioClientErrorCode.DeviceInvalidated
                ? $"Playback stopped: {e.Exception.Message}"
                : _outputDeviceId is null
                    ? "Audio device disconnected. Press Play to resume."
                    : $"{_outputDeviceName} disconnected. Plug it back in, or choose another output from the quality badge.";
            RefreshPosition();
            return;
        }

        // Lagu selesai dengan normal: ulangi kalau Repeat One, selain itu lanjut ke lagu berikutnya
        if (RepeatMode == RepeatMode.One && CurrentTrack is { } track)
            Play(track);
        else
            Advance();
    }

    private async Task LoadCoverAsync(Track track)
    {
        CurrentCover = null;
        var cover = await CoverImageLoader.LoadAsync(track.FilePath, decodePixelWidth: 104);

        // Lewati kalau sementara itu pengguna sudah pindah ke lagu lain
        if (ReferenceEquals(CurrentTrack, track))
            CurrentCover = cover;
    }

    private void OnTimerTick()
    {
        TrackListening();
        RefreshPosition();
    }

    /// <summary>
    /// Sebuah lagu masuk riwayat setelah benar-benar didengarkan separuh durasinya, atau 4 menit untuk
    /// lagu yang panjang (aturan yang juga dipakai layanan seperti Last.fm). Lagu yang dilewati di awal
    /// tidak dihitung, dan satu kali putar hanya dicatat sekali.
    /// </summary>
    private void TrackListening()
    {
        if (CurrentTrack is not { } track || _playRecorded || _engine.State != PlaybackState.Playing)
            return;

        _listenedSeconds += _timer.Interval.TotalSeconds;
        double needed = Math.Min(240, Math.Max(1, _engine.Duration.TotalSeconds / 2));
        if (_listenedSeconds < needed)
            return;

        _playRecorded = true;
        try
        {
            _database.RecordPlay(track.FilePath, DateTime.UtcNow);
            PlayRecorded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            // Gagal menulis riwayat (misalnya database terkunci) tidak boleh mengganggu pemutaran
        }
    }

    private void RefreshPosition()
    {
        var position = _engine.Position;
        var duration = _engine.Duration;
        var remaining = duration > position ? duration - position : TimeSpan.Zero;

        PositionSeconds = position.TotalSeconds;
        DurationSeconds = Math.Max(duration.TotalSeconds, 1);
        ElapsedText = Formatters.Time(position);
        RemainingText = "-" + Formatters.Time(remaining);
        IsPlaying = _engine.State == PlaybackState.Playing;
        PlayPauseSymbol = IsPlaying ? Symbol.Pause : Symbol.Play;
    }
}
