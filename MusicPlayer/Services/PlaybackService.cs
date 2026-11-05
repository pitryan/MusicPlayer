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

    // True selama slider diisi PlaybackService sendiri (lihat ShowVolume), bukan digeser pengguna
    private bool _showingVolume;

    // Volume aplikasi (session volume atau volume digital) pilihan pengguna, yang juga disimpan di pengaturan. Terpisah dari
    // slider: selama slider mengikuti volume perangkat (_volumeFollowsDevice), slider menampilkan volume DAC, dan nilai itu
    // tidak boleh terpakai lagi sebagai volume aplikasi untuk lagu shared mode berikutnya.
    private double _appVolume = 100;
    private bool _appMuted;
    private bool _volumeFollowsDevice;

    // Volume dan mute perangkat yang terakhir tampil di slider sebelum slider berhenti mengikutinya, misalnya DAC yang dicabut
    // di tengah lagu. Lagu berikutnya tidak mulai lebih keras dari itu di perangkat tanpa volume hardware, dan tidak mulai
    // berbunyi kalau DAC tadi di-mute (lihat LimitStartVolume). Tidak disimpan sebagai volume aplikasi: lagu shared mode
    // memakai volume aplikasi DI ATAS volume Windows perangkatnya, jadi volume DAC di situ membuatnya terlalu pelan.
    private double? _lastDeviceLevel;
    private bool _lastDeviceMuted;

    // Perangkat yang terakhir diikuti slider, asal _lastDeviceMuted (lihat UnmuteFollowedDevice)
    private string? _followedDeviceId;

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

    // Pemberitahuan Windows saat perangkat output dicolok, dicabut, atau dinyalakan ulang (lihat OutputDevicesChanged).
    // Null kalau layanan audio Windows tidak bisa memberikannya.
    private readonly MMDeviceEnumerator? _deviceEnumerator;
    private readonly MMDeviceNotificationClient? _deviceNotifications;
    private readonly DispatcherQueueTimer _devicesChangedTimer;

    // HRESULT dari Windows kalau tidak ada perangkat output sama sekali (ERROR_NOT_FOUND)
    private const int NoDeviceFound = unchecked((int)0x80070490);

    // HRESULT untuk file lagu yang tidak bisa dibuka. Media Foundation melaporkannya dengan pesan "Media Foundation
    // error 0x...", jadi diganti dengan penjelasan yang bisa dibaca (lihat DescribeFileError).
    private const int FileNotFound = unchecked((int)0x80070002);
    private const int PathNotFound = unchecked((int)0x80070003);
    private const int AccessDenied = unchecked((int)0x80070005);
    private const int DriveNotReady = unchecked((int)0x80070015);
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int NetworkPathNotFound = unchecked((int)0x80070035);

    // Output pilihan pengguna, diatur dari popup kualitas. _outputDeviceId null = ikut perangkat default Windows.
    private string? _outputDeviceId;
    private string _outputDeviceName = "";
    private bool _useExclusiveMode = true;

    // Output diganti saat lagu diputar: setelah perangkat lama dilepas, lagu langsung dilanjutkan
    private bool _outputChangePending;

    // Lagu berhenti karena perangkat (dicabut, atau perangkat pilihan tidak tersambung), bukan karena pause.
    // Kalau pengguna lalu memilih output lain, lagu langsung dilanjutkan di sana.
    private bool _interruptedByDevice;

    // Lagu sekarang, kalau file-nya sedang dilepas untuk ditulis tag-nya (lihat WithFilesReleasedAsync). Null kalau tidak ada,
    // dan dikosongkan begitu pengguna pindah ke lagu lain selama penyimpanan: lagu yang dilepas lalu tidak dimuat lagi.
    private EditRelease? _editRelease;

    // Semua file yang sedang dilepas, termasuk yang sudah ditinggal pengguna, sampai penyimpanan terakhirnya selesai. Selama
    // itu file-nya tidak dibuka, juga kalau pengguna kembali ke lagu itu (lihat Play).
    private readonly List<EditRelease> _releases = [];

    // Lagu sekarang, Previous, Playing Next, shuffle, repeat, dan Autoplay. Aturannya di Library supaya bisa diuji; di sini
    // hanya membuka file dan mengurus tampilan.
    private readonly TrackQueue<QueueEntry> _queue;

    // Nomor permintaan cover terakhir (lihat LoadCoverAsync)
    private int _coverRequest;

    // Untuk riwayat: berapa lama lagu sekarang benar-benar didengarkan (pause dan seek tidak dihitung)
    private double _listenedSeconds;
    private bool _playRecorded;
    private RepeatMode _repeatMode = RepeatMode.Off;
    private MMDevice? _device;

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
        _queue = new TrackQueue<QueueEntry>(UpNext, (track, autoplay) => new QueueEntry(track) { IsAutoplay = autoplay });
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

        // Satu kejadian (misalnya DAC yang restart sendiri) memicu beberapa notifikasi beruntun: dikabarkan sekali saja
        _devicesChangedTimer = dispatcherQueue.CreateTimer();
        _devicesChangedTimer.Interval = TimeSpan.FromMilliseconds(300);
        _devicesChangedTimer.IsRepeating = false;
        _devicesChangedTimer.Tick += (_, _) => OutputDevicesChanged?.Invoke(this, EventArgs.Empty);

        try
        {
            // Dibuat di UI thread, jadi NAudio mengirim event-nya ke UI thread juga
            _deviceEnumerator = new MMDeviceEnumerator();
            _deviceNotifications = _deviceEnumerator.CreateNotificationClient(useSynchronizationContext: true);
            _deviceNotifications.DeviceAdded += (_, _) => OnDevicesChanged();
            _deviceNotifications.DeviceRemoved += (_, _) => OnDevicesChanged();
            _deviceNotifications.DeviceStateChanged += (_, _) => OnDevicesChanged();
            _deviceNotifications.DefaultDeviceChanged += (_, _) => OnDevicesChanged();
        }
        catch (COMException)
        {
            // Layanan audio Windows bermasalah: daftar output hanya diperbarui saat Settings dibuka
            _deviceNotifications?.Dispose();
            _deviceEnumerator?.Dispose();
            _deviceNotifications = null;
            _deviceEnumerator = null;
        }
    }

    private void OnDevicesChanged()
    {
        _devicesChangedTimer.Stop();
        _devicesChangedTimer.Start();
    }

    /// <summary>Dipicu setiap kali sebuah lagu tercatat di riwayat putar.</summary>
    public event EventHandler? PlayRecorded;

    /// <summary>Lagu sekarang, dari antrean. PropertyChanged-nya dipicu ChangeQueue.</summary>
    public Track? CurrentTrack => _queue.Current;

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
        get => _queue.IsShuffle;
        set
        {
            if (value == _queue.IsShuffle)
                return;
            _queue.SetShuffle(value);
            OnPropertyChanged();
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
                _queue.SetRepeat(value != RepeatMode.Off);
            }
        }
    }

    // ── Autoplay ─────────────────────────────────────────────

    /// <summary>
    /// Sumber lagu Autoplay: (lagu terakhir, path yang dikecualikan, jumlah) → lagu. Disetel App
    /// (AutoplayPicker dengan library, riwayat putar, dan favorit); null berarti Autoplay tidak tersedia.
    /// </summary>
    public Func<Track, ISet<string>, int, IReadOnlyList<Track>>? AutoplaySource
    {
        get => _queue.AutoplaySource;
        set => _queue.AutoplaySource = value;
    }

    /// <summary>
    /// Autoplay, seperti Apple Music: saat antrean habis, pemutaran berlanjut dengan lagu yang mirip dari library.
    /// Lagu-lagunya tampil di bawah antrean sendiri; Repeat yang menyala mendahuluinya.
    /// </summary>
    public bool AutoplayEnabled
    {
        get => _queue.AutoplayEnabled;
        set
        {
            if (value == _queue.AutoplayEnabled)
                return;
            _queue.SetAutoplay(value);
            OnPropertyChanged();
        }
    }

    /// <summary>Volume dari 0 sampai 100.</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            if (!SetProperty(ref _volume, Math.Clamp(Math.Round(value), 0, 100)))
                return;

            OnPropertyChanged(nameof(VolumeGlyph));
            if (!_showingVolume)
            {
                if (!_volumeFollowsDevice)
                {
                    AppVolume = _volume; // slider sedang mengatur volume aplikasi, bukan volume perangkat

                    // Dipilih sambil mendengar volume digital-nya, jadi batas dari perangkat sebelumnya tidak perlu lagi. Saat
                    // tidak ada yang berbunyi, batasnya tetap: pengguna belum mendengar seberapa keras pilihan itu di perangkat
                    // berikutnya.
                    if (_engine.VolumeKind == VolumeKind.Digital && IsDeviceAttached)
                        _lastDeviceLevel = null;
                }
                ApplyVolume();
            }
            OnPropertyChanged(nameof(StartVolumeLimit));
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (!SetProperty(ref _isMuted, value))
                return;

            OnPropertyChanged(nameof(VolumeGlyph));
            if (!_showingVolume)
            {
                if (!_volumeFollowsDevice)
                {
                    // Mute dari perangkat sebelumnya baru dihapus saat pengguna membuka mute. Selama mute aplikasi menyala
                    // setiap lagu memang mulai tanpa suara, dan saat dibuka nanti mute DAC asalnya ikut dilepas.
                    if (!_isMuted && _lastDeviceMuted)
                    {
                        UnmuteFollowedDevice();
                        _lastDeviceMuted = false;
                    }
                    IsAppMuted = _isMuted;
                }
                ApplyVolume();
            }
            OnPropertyChanged(nameof(StartVolumeLimit));
        }
    }

    // Lagu terbuka di perangkat yang benar-benar dipakai (tidak dilepas karena pause, error, atau edit tag)
    private bool IsDeviceAttached => _engine.IsOpen && !_engine.IsDeviceReleased;

    /// <summary>
    /// Batas awal volume (lihat LimitStartVolume) yang berlaku kalau slider berhenti mengikuti perangkat sekarang, juga saat
    /// aplikasi ditutup. App menyimpannya dengan kuncinya sendiri, tidak pernah sebagai volume aplikasi, supaya perangkat tanpa
    /// volume hardware tidak mulai keras setelah aplikasi dibuka lagi. Null kalau tidak ada batas. PropertyChanged untuk
    /// properti ini juga berarti <see cref="StartMutedLimit"/> mungkin berubah.
    /// </summary>
    public double? StartVolumeLimit => _volumeFollowsDevice ? _volume : _lastDeviceLevel;

    /// <summary>Pasangan <see cref="StartVolumeLimit"/>: lagu berikutnya mulai tanpa suara karena DAC tadi di-mute.</summary>
    public bool StartMutedLimit => _volumeFollowsDevice ? _isMuted : _lastDeviceMuted;

    /// <summary>Perangkat asal <see cref="StartMutedLimit"/> (lihat UnmuteFollowedDevice); disimpan App bersama batasnya.</summary>
    public string? StartMutedDeviceId => _followedDeviceId;

    /// <summary>Batas awal dari sesi sebelumnya. Dipanggil App saat aplikasi dibuka, sebelum ada lagu.</summary>
    public void RestoreStartLimit(double? level, bool muted, string? mutedDeviceId)
    {
        _lastDeviceLevel = level;
        _lastDeviceMuted = muted;
        _followedDeviceId = mutedDeviceId;
    }

    /// <summary>
    /// Mute yang tampil berasal dari DAC yang tadi diikuti slider, misalnya dipasang lewat tombol mute selama exclusive mode.
    /// Lagu shared mode di DAC itu tetap diam selama endpoint-nya di-mute, jadi saat pengguna membuka mute, mute DAC itu ikut
    /// dilepas. Hanya mute: volume DAC tidak diubah, dan perangkat lain (atau mute dari Windows) tidak disentuh.
    /// </summary>
    private void UnmuteFollowedDevice()
    {
        try
        {
            // Perangkat tempat lagu ini (akan) berbunyi, yaitu output pilihan sekarang. _device bisa masih perangkat lama
            // setelah output atau mode diganti selama pause, sampai lagunya dibuka lagi.
            using var device = GetOutputDevice();
            if (device.ID == _followedDeviceId && device.AudioEndpointVolume is { Mute: true } endpoint)
                endpoint.Mute = false;
        }
        catch (Exception)
        {
            // Perangkat pilihan tidak tersambung, sudah dicabut, atau menolak perubahan: tidak ada yang bisa dilepas
        }
    }

    /// <summary>
    /// Volume aplikasi (0-100) untuk shared mode dan volume digital, yang disimpan di pengaturan. Hanya berubah saat slider
    /// tidak mengikuti volume perangkat; volume DAC yang tampil di slider dalam exclusive mode tidak pernah masuk ke sini.
    /// </summary>
    public double AppVolume
    {
        get => _appVolume;
        private set => SetProperty(ref _appVolume, value);
    }

    /// <summary>Mute untuk volume aplikasi, pasangan <see cref="AppVolume"/>.</summary>
    public bool IsAppMuted
    {
        get => _appMuted;
        private set => SetProperty(ref _appMuted, value);
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

    /// <summary>
    /// Perangkat output dicolok, dicabut, dinyalakan ulang, atau perangkat default Windows berganti, misalnya DAC yang
    /// restart sendiri. Dipicu di UI thread, sekali untuk beberapa perubahan beruntun. Daftar output yang sedang tampil
    /// mengisi ulang dirinya dengan <see cref="RefreshOutputDevices"/>.
    /// </summary>
    public event EventHandler? OutputDevicesChanged;

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
    /// Mengisi ulang daftar output, lalu mengembalikan pilihan yang sedang aktif. Dipanggil saat daftarnya tampil dan
    /// setiap kali <see cref="OutputDevicesChanged"/> dipicu, supaya DAC yang baru dicolok langsung muncul. Perangkat
    /// pilihan yang sedang tidak tersambung tetap tampil dengan keterangan, supaya jelas apa yang sedang dipilih.
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

        _queue.Start(tracks, startIndex);
        PlayChosen(tracks[startIndex], keepHistory: false);
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

    /// <summary>
    /// Langsung memutar sebuah lagu tanpa mengubah Playing Next (misalnya dari History). Kalau itu lagu sekarang, lagu itu
    /// diputar ulang tanpa masuk daftar Previous.
    /// </summary>
    public void PlayNow(Track track) =>
        PlayChosen(track, keepHistory: CurrentTrack is not { } current || !IsSameFile(current.FilePath, track.FilePath));

    /// <summary>Menaruh lagu di urutan paling atas Playing Next. Kalau belum ada yang diputar, langsung diputar.</summary>
    public void PlayNext(Track track)
    {
        if (CurrentTrack is null)
            PlayChosen(track, keepHistory: false);
        else
            _queue.PlayNext(track);
    }

    /// <summary>Menaruh lagu di urutan paling bawah Playing Next. Kalau belum ada yang diputar, langsung diputar.</summary>
    public void AddToQueue(Track track)
    {
        if (CurrentTrack is null)
            PlayChosen(track, keepHistory: false);
        else
            _queue.Add(track); // antrean sendiri selalu di atas lagu Autoplay
    }

    /// <summary>Langsung memutar sebuah lagu dari Playing Next. Lagu-lagu di atasnya dilewati.</summary>
    public void PlayFromQueue(QueueEntry entry)
    {
        if (_queue.SkipTo(entry))
            PlayChosen(entry.Track, keepHistory: true);
    }

    public void RemoveFromQueue(QueueEntry entry) => _queue.Remove(entry);

    public void ClearQueue() => _queue.Clear();

    public void TogglePlayPause()
    {
        if (CurrentTrack is null)
            return;

        // File lagu ini sedang ditulis (edit tag), jadi tidak dibuka sekarang: yang diubah adalah apakah lagu lanjut
        // berbunyi setelah penyimpanan selesai. Ikon tombol mengikutinya (lihat RefreshPosition).
        if (_editRelease is { } release)
            release.Resume = !release.Resume;
        else if (_engine.State == PlaybackState.Playing)
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

        // Tidak ada file yang terbuka: lagu ini tadi gagal dibuka (file hilang, atau gagal dimuat lagi setelah edit tag).
        // Dicoba lagi dari awal, misalnya karena drive-nya sudah tersambung lagi. Selama file-nya sedang ditulis, tidak.
        if (!_engine.IsOpen)
        {
            if (CurrentTrack is { } track && _editRelease is null)
                PlayChosen(track, keepHistory: false);
            return;
        }

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
            StopFollowingDevice();
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
            else
            {
                StopFollowingDevice();
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
            StopFollowingDevice();
        }

        RefreshPosition();
    }

    /// <summary>
    /// Lagu di-pause dan tidak lagi memakai perangkat lamanya (output atau mode diganti, atau perangkat gagal dibuka lagi):
    /// slider berhenti mengikuti perangkat itu, supaya geserannya tidak mengubah volume hardware perangkat yang tidak dipakai
    /// lagi. Saat Play, volume perangkat yang baru dipasang seperti biasa (AttachVolume).
    /// </summary>
    private void StopFollowingDevice()
    {
        if (_volumeFollowsDevice)
            ShowAppVolume();
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

    /// <summary>Alasan file lagu tidak bisa dibuka atau dibaca, untuk pesan di kanan atas.</summary>
    private static string DescribeFileError(Exception ex) => ex switch
    {
        { HResult: FileNotFound or PathNotFound } => "the file wasn't found.",
        { HResult: DriveNotReady or NetworkPathNotFound } => "the drive it's on isn't available.",
        { HResult: AccessDenied } => "access to the file was denied.",
        { HResult: SharingViolation } => "the file is in use by another app.",
        // Pesan Media Foundation yang hanya berisi kode error, saat membuka file atau di tengah lagu (NAudio)
        { Message: var m } when m.StartsWith("Media Foundation error", StringComparison.Ordinal) =>
            $"the file couldn't be read (error 0x{ex.HResult:X8}).",
        { Message: var m } when m.StartsWith("MediaFoundationReadError", StringComparison.Ordinal) => "the file couldn't be read.",
        _ => ex.Message.EndsWith('.') ? ex.Message : ex.Message + ".",
    };

    /// <summary>Tombol Next. Dalam mode Repeat One pun, tombol ini tetap pindah ke lagu berikutnya.</summary>
    public void Next()
    {
        _queue.ResetFailures();
        Advance();
    }

    /// <summary>
    /// Pindah ke lagu berikutnya di Playing Next (lagu selesai, atau tombol Next). Lagu yang file-nya tidak bisa dibuka
    /// (hilang atau rusak) dilewati dan disebut di kanan atas. Percobaannya terbatas pada lagu di antrean saat ini, satu kali
    /// tambahan lagu Autoplay, dan satu putaran ulang untuk Repeat, supaya antrean yang semua file-nya hilang (misalnya
    /// drive-nya dicabut) tidak berputar terus. Masalah perangkat tidak dilewati, karena lagu lain pasti gagal dengan cara
    /// yang sama: lagunya tampil dan menunggu tombol Play (lihat WaitForDevice).
    /// </summary>
    /// <param name="problem">Pesan yang ditampilkan lebih dulu, misalnya lagu sebelumnya yang berhenti karena error.</param>
    private void Advance(string? problem = null)
    {
        var result = _queue.Advance(Play);
        string? skipped = DescribeSkipped(result.Skipped, result.FirstSkipped, result.FirstSkippedReason);

        if (!result.Played)
        {
            StopAll(); // mengosongkan StatusText, jadi pesannya diisi sesudahnya
            StatusText = JoinMessages(problem, skipped);
        }
        else if (problem is not null || skipped is not null)
        {
            StatusText = JoinMessages(problem, skipped, StatusText);
        }
    }

    /// <summary>Pesan untuk lagu yang dilewati karena file-nya tidak bisa dibuka. Null kalau tidak ada.</summary>
    private static string? DescribeSkipped(int count, Track? first, string reason) => first is null ? null
        : count == 1 ? $"Skipped \"{first.DisplayTitle}\": {reason}"
        : $"Skipped {count} songs that couldn't be played. \"{first.DisplayTitle}\": {reason}";

    private static string JoinMessages(params string?[] messages) =>
        string.Join(" ", messages.Where(m => !string.IsNullOrEmpty(m)));

    public void Previous()
    {
        if (CurrentTrack is not { } current)
            return;

        // Seperti pemutar musik pada umumnya: kalau lagu sudah berjalan lebih dari
        // 3 detik, tombol ini mengulang lagu dari awal, bukan pindah ke lagu sebelumnya
        if (Position > TimeSpan.FromSeconds(3) || !_queue.HasPrevious)
        {
            if (_engine.IsOpen || _editRelease is not null)
                SeekTo(TimeSpan.Zero);
            else
                PlayChosen(current, keepHistory: false); // lagu ini tadi gagal dibuka: dicoba lagi
            return;
        }

        // Lagu sekarang dikembalikan ke paling atas Playing Next
        PlayChosen(_queue.StepBack()!, keepHistory: false);
    }

    /// <summary>
    /// Posisi lagu saat ini, dibaca langsung dari engine. PositionSeconds hanya diperbarui 4 kali per detik, terlalu
    /// jarang untuk menyorot baris lirik tepat waktu.
    /// </summary>
    public TimeSpan Position => CurrentTrack is null ? TimeSpan.Zero : _editRelease?.Position ?? _engine.Position;

    // Durasi lagu sekarang; selama file-nya dilepas untuk edit tag, durasi dari sebelum dilepas
    private TimeSpan TrackDuration => _editRelease?.Duration ?? _engine.Duration;

    /// <summary>Lompat ke posisi tertentu, dari 0 (awal lagu) sampai 1 (akhir lagu).</summary>
    public void SeekToFraction(double fraction) =>
        SeekTo(TimeSpan.FromSeconds(Math.Clamp(fraction, 0, 1) * TrackDuration.TotalSeconds));

    /// <summary>Lompat ke waktu tertentu di lagu ini, misalnya saat baris lirik diklik.</summary>
    public void SeekTo(TimeSpan position)
    {
        if (CurrentTrack is null)
            return;

        var duration = TrackDuration;
        var target = position < TimeSpan.Zero ? TimeSpan.Zero : position > duration ? duration : position;
        if (_editRelease is { } release)
            release.Position = target; // file-nya sedang ditulis: posisi ini dipakai saat lagu dimuat lagi
        else
            _ = _engine.SeekAsync(target); // posisi tujuan langsung tampil; suaranya menyusul (tidak pernah gagal)
        RefreshPosition();
    }

    public void Dispose()
    {
        _timer.Stop();
        _releaseTimer.Stop();
        _devicesChangedTimer.Stop();
        _deviceNotifications?.Dispose();
        _deviceEnumerator?.Dispose();
        DetachDeviceVolume();
        _engine.Dispose();
        _device?.Dispose();
    }

    private void ApplyVolume()
    {
        // Volume aplikasi tidak pernah dikirim ke volume hardware perangkat. Setelah perangkat error (misalnya DAC dicabut)
        // engine masih memegang perangkat lama dengan VolumeKind.Device, sedangkan slider sudah kembali ke volume aplikasi:
        // DAC itu tidak boleh ikut berubah, karena volume-nya dibaca lagi ke slider saat lagu dilanjutkan di sana.
        if (_engine.VolumeKind == VolumeKind.Device && !_volumeFollowsDevice)
            return;

        // Slider mengikuti DAC: diatur lewat endpoint yang dipegang di sini, karena engine tidak memegang perangkatnya lagi
        // setelah Close atau OpenWithoutDevice (misalnya lagu yang di-pause lalu tag-nya disimpan). MMDevice menyimpan satu
        // objek endpoint, jadi ini objek yang sama dengan milik engine, dan perubahannya tetap membawa OwnVolumeChange.
        if (_volumeFollowsDevice && _deviceVolume is { } endpoint)
        {
            try
            {
                endpoint.Mute = IsMuted;
                endpoint.MasterVolumeLevelScalar = (float)(Volume / 100);
            }
            catch (COMException)
            {
                // Perangkat dicabut atau menolak perubahan volume: abaikan, sama seperti AudioEngine.SetVolume
            }
            return;
        }

        _engine.SetVolume((float)(Volume / 100), IsMuted);
    }

    private void AttachVolume(MMDevice device)
    {
        switch (_engine.VolumeKind)
        {
            case VolumeKind.Device:
                // Volume perangkat dipakai apa adanya, TIDAK ditimpa dengan nilai slider,
                // supaya suara tidak tiba-tiba keras saat lagu pertama diputar. Slider hanya menampilkannya
                // (termasuk mute perangkat); volume aplikasi yang tersimpan tidak ikut berubah. Batas dari perangkat
                // sebelumnya tidak berlaku lagi: saat slider berhenti mengikuti perangkat ini, batasnya diambil dari sini.
                _volumeFollowsDevice = true;
                _lastDeviceLevel = null;
                _lastDeviceMuted = false;
                try
                {
                    _followedDeviceId = device.ID;
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
                OnPropertyChanged(nameof(StartVolumeLimit)); // juga kalau nilai slider tidak berubah
                break;

            case VolumeKind.Digital:
                // Di sini volume digital satu-satunya peredam, jadi batas volume dari perangkat sebelumnya ikut berlaku
                RestoreAppVolume();
                LimitStartVolume(limitLevel: true);
                VolumeDescription = "Digital volume: this device has no hardware volume, so playback is not bit-perfect below 100.";
                break;

            default:
                // Shared mode: volume Windows perangkatnya tetap berlaku, jadi volume aplikasi dipakai apa adanya. Hanya mute
                // dari perangkat sebelumnya yang ikut berlaku.
                RestoreAppVolume();
                LimitStartVolume(limitLevel: false);
                VolumeDescription = "App volume, the same as the MusicPlayer slider in the Windows volume mixer.";
                break;
        }
    }

    /// <summary>
    /// Slider kembali menampilkan volume aplikasi, bukan volume perangkat yang tadi disalin ke slider (lagu sebelumnya dalam
    /// exclusive mode), lalu volume itu dipakai untuk lagu yang terbuka sekarang. Volume perangkat yang tadi tampil diingat
    /// sebagai batas awal (lihat LimitStartVolume).
    /// </summary>
    private void RestoreAppVolume()
    {
        if (_volumeFollowsDevice)
        {
            _lastDeviceLevel = Volume;
            _lastDeviceMuted = IsMuted;
        }
        _volumeFollowsDevice = false;
        ShowVolume(_appVolume, _appMuted);
        ApplyVolume(); // juga kalau slider tidak berubah: session atau gain lagu yang baru dibuka belum diatur
        OnPropertyChanged(nameof(StartVolumeLimit));
    }

    /// <summary>
    /// Lagu yang baru dibuka setelah slider berhenti mengikuti DAC lain (misalnya DAC dicabut, lalu Windows pindah ke speaker)
    /// tidak mulai berbunyi kalau DAC itu tadi di-mute, dan di perangkat tanpa volume hardware tidak mulai lebih keras dari
    /// volume DAC tadi. Kira-kira saja: slider DAC memakai kurva volume Windows, volume digital linear. Hanya tampil di
    /// slider, tidak disimpan; batas ini berlaku untuk lagu-lagu berikutnya juga, sampai pengguna mengatur volume sendiri.
    /// </summary>
    private void LimitStartVolume(bool limitLevel)
    {
        double volume = limitLevel && _lastDeviceLevel is { } limit ? Math.Min(Volume, limit) : Volume;
        bool muted = IsMuted || _lastDeviceMuted;
        if (volume == Volume && muted == IsMuted)
            return;

        ShowVolume(volume, muted);
        ApplyVolume();
    }

    /// <summary>
    /// Mengisi slider dari PlaybackService sendiri (volume perangkat, volume aplikasi, atau batas awal), bukan dari pengguna:
    /// tidak disimpan sebagai volume aplikasi dan tidak langsung dikirim ke perangkat.
    /// </summary>
    private void ShowVolume(double volume, bool muted)
    {
        _showingVolume = true;
        Volume = volume;
        IsMuted = muted;
        _showingVolume = false;
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

        // Bisa dipanggil dari thread lain (misalnya saat volume diubah dari Windows), jadi dipindah ke UI thread. Notifikasi
        // yang baru tiba setelah lagu pindah ke shared mode diabaikan: slider sudah kembali ke volume aplikasi.
        _dispatcher.TryEnqueue(() =>
        {
            if (_volumeFollowsDevice)
                SyncFromDevice(data.MasterVolume, data.Muted);
        });
    }

    // Nilai ini datang dari perangkat, jadi tidak perlu dikirim balik, dan bukan volume aplikasi
    private void SyncFromDevice(float volume, bool muted) => ShowVolume(volume * 100, muted);

    /// <summary>
    /// Membuka dan memutar lagu. False kalau file lagunya tidak bisa dibuka (hilang, rusak, atau formatnya tidak didukung),
    /// dengan alasannya di <paramref name="error"/>: lagu sekarang, antrean, dan daftar Previous tidak berubah, tapi lagu
    /// lama sudah ditutup. Masalah perangkat tidak dianggap gagal: lagunya tetap tampil dan menunggu (lihat WaitForDevice).
    /// </summary>
    /// <param name="keepHistory">Lagu sekarang masuk daftar Previous.</param>
    private bool Play(Track track, bool keepHistory, out string error)
    {
        error = "";
        _releaseTimer.Stop(); // lagu baru selalu langsung diputar, jadi DAC tidak perlu dilepas
        _outputChangePending = false;
        _interruptedByDevice = false;

        if (FindRelease(track.FilePath) is { } release)
        {
            PlayAfterEdit(track, keepHistory, release);
            return true;
        }

        _editRelease = null; // pengguna pindah ke lagu lain: lagu yang dilepas tidak dimuat lagi setelah penyimpanan

        MMDevice device;
        try
        {
            // Perangkat pilihan pengguna, atau perangkat default Windows saat ini
            device = GetOutputDevice();
        }
        catch (OutputDeviceUnavailableException ex)
        {
            return WaitForDevice(track, keepHistory,
                $"{ex.DeviceName} isn't connected. Plug it in, or choose another output from the quality badge.", deviceError: null, out error);
        }
        catch (Exception ex)
        {
            // Misalnya tidak ada perangkat output sama sekali, atau layanan audio Windows bermasalah
            string reason = DescribeDeviceError(ex);
            return WaitForDevice(track, keepHistory, $"Couldn't play \"{track.DisplayTitle}\": {reason} Press Play to try again.", reason, out error);
        }

        try
        {
            _engine.Open(track.FilePath, device, UseExclusiveMode); // player lama ditutup lebih dulu di sini
            DetachDeviceVolume();
            _device?.Dispose();
            _device = device;
            AttachVolume(device); // sebelum Play, supaya tidak ada sesaat suara dengan volume yang salah
            _engine.Play();
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(device, _device))
                device.Dispose();

            if (AudioErrors.IsDeviceError(ex))
            {
                // Perangkatnya yang bermasalah (misalnya dipakai aplikasi lain dalam exclusive mode), bukan file-nya
                string reason = DescribeDeviceError(ex);
                return WaitForDevice(track, keepHistory, $"Couldn't play \"{track.DisplayTitle}\": {reason} Press Play to try again.", reason, out error);
            }

            _engine.Close(); // tidak ada yang tertinggal terbuka, juga kalau Open sudah berhasil sebelum error-nya
            return Unplayable(track, ex, out error);
        }

        _queue.Unplayable.Remove(track.FilePath);
        ShowTrack(track, keepHistory);
        StatusText = "";
        UpdateQualityInfo(track, device);
        RefreshPosition();
        return true;
    }

    /// <summary>File lagu tidak bisa dibuka: dicatat supaya Autoplay tidak memilihnya, dan alasannya untuk pesan.</summary>
    private bool Unplayable(Track track, Exception ex, out string error)
    {
        _queue.Unplayable.Add(track.FilePath);
        error = DescribeFileError(ex);
        RefreshPosition();
        return false;
    }

    /// <summary>
    /// Memutar lagu yang dipilih langsung oleh pengguna (atau diulang oleh Repeat One). Kalau file-nya tidak bisa dibuka,
    /// lagu itu tetap tampil dengan pesan error-nya, supaya layar sesuai dengan pilihan pengguna, tanpa ada yang dimuat.
    /// Tombol Play lalu mencobanya lagi (lihat Resume).
    /// </summary>
    private void PlayChosen(Track track, bool keepHistory)
    {
        _queue.ResetFailures();
        if (Play(track, keepHistory, out string error))
            return;

        ShowTrack(track, keepHistory);
        StatusText = $"Couldn't play \"{track.DisplayTitle}\": {error}";
        QualityVisibility = Visibility.Collapsed; // tidak ada yang dimuat, jadi tidak ada kualitas yang bisa ditampilkan
        ShowAppVolume();
        RefreshPosition();
    }

    private static bool IsSameFile(string path, string otherPath) =>
        string.Equals(path, otherPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Lagu yang file-nya dilepas selama tag-nya ditulis: posisi dan durasinya dari sebelum dilepas, dan apakah lagu lanjut
    /// berbunyi setelah dimuat lagi. Beberapa penyimpanan untuk file yang sama bisa berjalan bersamaan; lagu baru dimuat lagi
    /// setelah yang terakhir selesai.
    /// </summary>
    private sealed class EditRelease
    {
        public EditRelease(string filePath, TimeSpan position, TimeSpan duration, bool wasOpen, bool resume)
        {
            FilePath = filePath;
            Position = position;
            Duration = duration;
            WasOpen = wasOpen;
            Resume = resume;
        }

        public string FilePath { get; }
        public TimeSpan Position { get; set; }
        public TimeSpan Duration { get; }

        /// <summary>False kalau lagu ini tadi gagal dibuka: setelah penyimpanan, lagu hanya dibuka kalau Play ditekan.</summary>
        public bool WasOpen { get; }

        /// <summary>Lanjut berbunyi setelah dimuat lagi. Tombol Play/Pause selama penyimpanan mengubah nilai ini.</summary>
        public bool Resume { get; set; }

        /// <summary>Jumlah penyimpanan yang masih berjalan untuk file ini.</summary>
        public int Count { get; set; }
    }

    private EditRelease? FindRelease(string path) => _releases.Find(r => IsSameFile(r.FilePath, path));

    /// <summary>
    /// Menjalankan <paramref name="write"/> (menulis tag) dengan file lagu yang sedang dimuat dilepas lebih dulu, karena
    /// Windows tidak mengizinkan file yang sedang dibuka diganti. Setelah selesai, lagu itu dimuat lagi di posisi yang
    /// sama dan lanjut berbunyi kalau tadinya berbunyi. Lagu lain tidak terpengaruh.
    /// </summary>
    /// <remarks>
    /// Selama penyimpanan, file yang dilepas tidak dibuka: tombol Play/Pause hanya menentukan apakah lagu lanjut berbunyi
    /// setelahnya, dan memilih lagu itu lagi memutarnya dari awal setelahnya. Kalau pengguna pindah ke lagu lain (Next,
    /// Previous, memilih lagu), lagu itu langsung diputar dan lagu yang dilepas tidak dimuat lagi. Penyimpanan lain untuk
    /// file yang sama yang mulai sebelum yang pertama selesai ikut memakai keadaan yang sama.
    /// </remarks>
    public async Task WithFilesReleasedAsync(IReadOnlyCollection<string> paths, Func<Task> write)
    {
        bool Writes(string path) => paths.Contains(path, StringComparer.OrdinalIgnoreCase);

        // File lagu sekarang ikut ditulis dan belum dilepas: dilepas sekarang. Juga kalau lagu itu tadi gagal dibuka,
        // supaya tombol Play selama penyimpanan tidak membukanya.
        if (_editRelease is null && CurrentTrack is { } track && Writes(track.FilePath) && FindRelease(track.FilePath) is null)
        {
            // Output yang sedang diganti mem-pause lagu sebentar (ApplyOutputChange); lagu itu tetap dianggap berbunyi
            var created = new EditRelease(track.FilePath, _engine.Position, _engine.Duration, wasOpen: _engine.IsOpen,
                resume: _engine.State == PlaybackState.Playing || _outputChangePending);
            _releaseTimer.Stop();
            _outputChangePending = false;
            _engine.Close();
            _editRelease = created;
            _releases.Add(created);
        }

        // Penyimpanan ini ikut menahan setiap file yang sedang dilepas dan ikut ditulisnya, juga yang sudah ditinggal pengguna
        var held = _releases.Where(r => Writes(r.FilePath)).ToList();
        if (held.Count == 0)
        {
            await write();
            return;
        }

        foreach (var release in held)
            release.Count++;
        RefreshPosition();
        try
        {
            await write();
        }
        finally
        {
            foreach (var release in held)
                EndRelease(release);
        }
    }

    /// <summary>
    /// Satu penyimpanan untuk file yang dilepas selesai. Setelah yang terakhir, lagunya dimuat lagi, tapi hanya kalau pengguna
    /// tidak pindah lagu selama itu (_editRelease dikosongkan saat lagu lain diputar atau antrean berhenti).
    /// </summary>
    private void EndRelease(EditRelease release)
    {
        if (--release.Count > 0)
            return; // penyimpanan lain untuk file ini masih berjalan

        _releases.Remove(release);
        if (!ReferenceEquals(_editRelease, release))
            return;

        _editRelease = null;
        if (CurrentTrack is not { } track || _engine.IsOpen)
            RefreshPosition();
        else if (release.WasOpen)
            ReopenAfterEdit(track, release.Position, release.Resume);
        else if (release.Resume)
            PlayChosen(track, keepHistory: false); // lagu yang tadi gagal dibuka, dan Play ditekan selama penyimpanan
        else
            RefreshPosition();
    }

    /// <summary>
    /// Lagu yang file-nya sedang ditulis diputar (dipilih lagi, Previous, atau Next ke lagu yang sama). File-nya tidak dibuka
    /// sekarang, karena membukanya bisa membuat penyimpanan gagal: lagu ini menjadi lagu sekarang dan diputar dari awal
    /// begitu penyimpanan terakhirnya selesai (lihat EndRelease).
    /// </summary>
    private void PlayAfterEdit(Track track, bool keepHistory, EditRelease release)
    {
        if (!ReferenceEquals(_editRelease, release))
        {
            // Kembali ke lagu yang tadi ditinggal selama penyimpanannya: lagu lain yang sedang dimuat ditutup
            _engine.Close();
            _editRelease = release;
            QualityVisibility = Visibility.Collapsed; // dimuat lagi (dan badge-nya diisi) setelah penyimpanan
        }

        release.Position = TimeSpan.Zero;
        release.Resume = true;
        ShowTrack(track, keepHistory);
        StatusText = "";
        RefreshPosition();
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
                _ = _engine.SeekAsync(position); // hening sampai posisinya siap, lalu langsung berbunyi
                _engine.Play();
                if (_interruptedByDevice && _engine.State == PlaybackState.Playing)
                {
                    // Lagu tadi menunggu perangkat, dan Play ditekan selama penyimpanan: sekarang sudah berbunyi, jadi pesan
                    // perangkatnya tidak berlaku lagi. Pesan lain (misalnya lagu yang dilewati) tetap tampil.
                    _interruptedByDevice = false;
                    StatusText = "";
                }
                UpdateQualityInfo(track, device);
            }
            else
            {
                // Sedang di-pause: file dimuat tanpa perangkat, jadi DAC tetap bebas; Play membukanya lagi seperti biasa
                _engine.OpenWithoutDevice(track.FilePath);
                _ = _engine.SeekAsync(position);
            }
        }
        catch (Exception ex) when (ex is OutputDeviceUnavailableException || AudioErrors.IsDeviceError(ex))
        {
            // Perangkatnya yang bermasalah, bukan file-nya: lagu dimuat tanpa perangkat di posisinya, dan lanjut begitu
            // Play ditekan atau output lain dipilih
            if (!ReferenceEquals(device, _device))
                device?.Dispose();
            try
            {
                _engine.OpenWithoutDevice(track.FilePath);
                _ = _engine.SeekAsync(position);
                if (ex is OutputDeviceUnavailableException missing)
                {
                    ShowWaitingForDevice(track,
                        $"{missing.DeviceName} isn't connected. Plug it in, or choose another output from the quality badge.", deviceError: null);
                }
                else
                {
                    string reason = DescribeDeviceError(ex);
                    ShowWaitingForDevice(track, $"Couldn't resume \"{track.DisplayTitle}\": {reason} Press Play to try again.", reason);
                }
            }
            catch (Exception fileError)
            {
                ShowResumeFailed(track, fileError);
            }
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(device, _device))
                device?.Dispose();
            _engine.Close();
            ShowResumeFailed(track, ex);
        }

        RefreshPosition();
    }

    /// <summary>Lagu gagal dimuat lagi setelah edit tag. Tidak ada yang dimuat; tombol Play mencobanya lagi dari awal (lihat Resume).</summary>
    private void ShowResumeFailed(Track track, Exception ex)
    {
        StatusText = $"Couldn't resume \"{track.DisplayTitle}\": {DescribeFileError(ex)}";
        QualityVisibility = Visibility.Collapsed;
        ShowAppVolume();
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

        bool currentReplaced = false;
        ChangeQueue(() => currentReplaced = _queue.ReplaceTracks(t => byPath.GetValueOrDefault(t.FilePath)));
        if (currentReplaced && CurrentTrack is { } now)
        {
            TitleText = now.DisplayTitle;
            SubtitleText = $"{now.DisplayArtist} — {now.DisplayAlbum}";
            _ = LoadCoverAsync(now);
        }
    }

    /// <summary>
    /// Folder library dipindah (lihat LibraryDatabase.RelocateFolder): lagu dari folder itu di lagu sekarang, antrean, dan
    /// daftar Previous memakai path barunya, supaya tetap bisa diputar dan status favoritnya tetap terbaca. Kedua folder
    /// sudah dirapikan dengan LibraryPaths.NormalizeFolder.
    /// </summary>
    public void RelocateTracks(string oldFolder, string newFolder)
    {
        Track? Moved(Track track) => LibraryPaths.IsUnder(track.FilePath, oldFolder)
            ? track with
            {
                FilePath = LibraryPaths.Relocate(track.FilePath, oldFolder, newFolder),
                FolderPath = LibraryPaths.Relocate(track.FolderPath, oldFolder, newFolder),
            }
            : null;

        ChangeQueue(() => _queue.ReplaceTracks(Moved));
    }

    /// <summary>
    /// Menjalankan perubahan antrean yang bisa mengganti lagu sekarang, lalu memberi tahu tampilan kalau memang berganti.
    /// Antrean tidak mengganti objek lagu yang isinya sama, jadi membandingkan objeknya sama dengan SetProperty.
    /// </summary>
    private void ChangeQueue(Action change)
    {
        var before = CurrentTrack;
        change();
        if (!ReferenceEquals(before, CurrentTrack))
            OnPropertyChanged(nameof(CurrentTrack));
    }

    /// <summary>Lagu ini menjadi lagu sekarang di tampilan.</summary>
    /// <param name="keepHistory">
    /// Lagu sebelumnya masuk daftar Previous, juga kalau file-nya sama (lagu yang sama dua kali di antrean, dua kali putar).
    /// Lagu sekarang yang diputar ulang (PlayNow, Repeat One) memberi false.
    /// </param>
    private void ShowTrack(Track track, bool keepHistory)
    {
        ChangeQueue(() => _queue.MoveTo(track, keepHistory));
        ShowTrackInfo(track);
        _queue.TopUpAutoplay();
    }

    /// <summary>Judul dan cover lagu yang baru menjadi lagu sekarang; hitungan waktu dengarnya mulai dari nol.</summary>
    private void ShowTrackInfo(Track track)
    {
        _listenedSeconds = 0;
        _playRecorded = false;
        TitleText = track.DisplayTitle;
        SubtitleText = $"{track.DisplayArtist} — {track.DisplayAlbum}";
        _ = LoadCoverAsync(track);
    }

    /// <summary>Antrean dan posisi sekarang untuk disimpan (lihat SessionService). Null kalau tidak ada lagu.</summary>
    public PlaybackSession? CaptureSession() => _queue.Snapshot(Position);

    /// <summary>
    /// Sesi terakhir kembali saat aplikasi dibuka: antrean dan lagu terakhir, di-pause di posisinya. Perangkat audio tidak
    /// dibuka, jadi DAC tetap bebas sampai Play ditekan (Resume membukanya seperti setelah pause). <paramref name="find"/>
    /// mencari lagu di library; lagu yang sudah tidak ada dilewati. False kalau lagu terakhirnya sendiri sudah tidak ada.
    /// </summary>
    public bool RestoreSession(PlaybackSession session, Func<string, Track?> find)
    {
        bool restored = false;
        ChangeQueue(() => restored = _queue.Restore(session, find));
        if (!restored || CurrentTrack is not { } track)
            return false;

        ShowTrackInfo(track);
        try
        {
            _engine.OpenWithoutDevice(track.FilePath);
            if (session.Position > TimeSpan.Zero)
                _ = _engine.SeekAsync(session.Position);
        }
        catch (Exception ex)
        {
            // Misalnya drive-nya belum tersambung: lagunya tetap tampil dengan pesannya, dan Play mencobanya lagi
            _queue.Unplayable.Add(track.FilePath);
            StatusText = $"Couldn't open \"{track.DisplayTitle}\": {DescribeFileError(ex)}";
        }
        RefreshPosition();
        return true;
    }

    /// <summary>
    /// Lagu tidak bisa dibuka di perangkat output: perangkat pilihan tidak tersambung, atau perangkatnya error (misalnya
    /// dipakai aplikasi lain, atau tidak ada perangkat sama sekali). Lagunya tetap dimuat dan di-pause di awal, supaya judul
    /// dan badge kualitas tampil dan output lain bisa dipilih dari popup-nya. Begitu output lain dipilih (atau Play ditekan
    /// setelah perangkatnya beres), lagu langsung diputar. False kalau file-nya sendiri juga tidak bisa dibuka.
    /// </summary>
    /// <param name="deviceError">Alasan perangkat gagal dibuka; null kalau perangkat pilihan tidak tersambung.</param>
    private bool WaitForDevice(Track track, bool keepHistory, string message, string? deviceError, out string error)
    {
        error = "";
        try
        {
            _engine.OpenWithoutDevice(track.FilePath);
        }
        catch (Exception ex)
        {
            return Unplayable(track, ex, out error);
        }

        _queue.Unplayable.Remove(track.FilePath);
        ShowTrack(track, keepHistory);
        ShowWaitingForDevice(track, message, deviceError);
        RefreshPosition();
        return true;
    }

    /// <summary>Lagu sudah dimuat tanpa perangkat (OpenWithoutDevice) dan menunggu perangkatnya; pesannya di kanan atas.</summary>
    private void ShowWaitingForDevice(Track track, string message, string? deviceError)
    {
        ShowAppVolume();
        _device?.Dispose();
        _device = null;
        _interruptedByDevice = true;
        StatusText = message;
        UpdateQualityInfo(track, device: null, deviceError);
    }

    /// <summary>
    /// Tidak ada lagu yang terhubung ke perangkat (antrean berhenti, lagu gagal dibuka, perangkat error di tengah lagu, atau
    /// menunggu perangkat): slider kembali mengatur volume aplikasi. VolumeKind engine masih milik lagu terakhir yang terbuka,
    /// jadi volume perangkat lagu itu tidak boleh terus tampil sebagai nilai slider, dan volume aplikasi tidak dikirim ke
    /// perangkat itu (lihat ApplyVolume).
    /// </summary>
    private void ShowAppVolume()
    {
        DetachDeviceVolume();
        RestoreAppVolume();
        VolumeDescription = "";
    }

    private void StopAll()
    {
        _releaseTimer.Stop();
        _outputChangePending = false;
        _interruptedByDevice = false;
        _editRelease = null; // lagu yang sedang dilepas untuk edit tag tidak dimuat lagi setelah penyimpanan
        _engine.Close();
        ChangeQueue(_queue.Stop);
        _coverRequest++; // cover yang masih dimuat tidak ditampilkan lagi
        CurrentCover = null;
        TitleText = IdleTitle;
        SubtitleText = IdleSubtitle;
        StatusText = "";
        QualityVisibility = Visibility.Collapsed;
        ShowAppVolume(); // tidak ada lagu: slider mengatur volume aplikasi, yang dipakai lagu shared mode berikutnya
        RefreshPosition();
    }

    /// <summary>
    /// Isi popup kualitas. <paramref name="device"/> null berarti lagu tidak terhubung ke perangkat: perangkat pilihan
    /// sedang tidak tersambung, atau perangkatnya gagal dibuka (<paramref name="deviceError"/>).
    /// </summary>
    private void UpdateQualityInfo(Track track, MMDevice? device, string? deviceError = null)
    {
        var quality = AudioQuality.Describe(track);
        QualityBadgeText = quality.Label;
        QualityShortText = quality.ShortBadge;
        QualityDetailText = quality.Detail;
        QualityKind = quality.Kind;
        DeviceText = device?.FriendlyName ?? "";

        if (deviceError is { Length: > 0 })
        {
            OutputModeText = "Couldn't open the device";
            OutputDetailText = $"{char.ToUpperInvariant(deviceError[0])}{deviceError[1..]} Press Play to try again, or choose another output below.";
        }
        else if (device is null)
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
        if (e.Exception is { } deviceError && AudioErrors.IsDeviceError(deviceError))
        {
            // Perangkat bermasalah di tengah lagu, paling sering karena DAC dicabut. Player yang rusak dibuang
            // dan lagu berhenti di posisinya (antrean tidak maju). Lagu sengaja tidak dilanjutkan otomatis di
            // perangkat lain, misalnya speaker laptop, supaya suara tidak tiba-tiba keluar dari tempat yang
            // tidak diharapkan. Tombol Play melanjutkannya di perangkat default saat itu.
            _releaseTimer.Stop();
            _outputChangePending = false;
            _engine.ReleaseDevice();
            ShowAppVolume(); // slider tidak lagi mengikuti perangkat yang mati, tanpa mengubah volume hardware-nya
            _interruptedByDevice = true;
            StatusText = deviceError.HResult != AudioClientErrorCode.DeviceInvalidated
                ? $"Playback stopped: {deviceError.Message}"
                : _outputDeviceId is null
                    ? "Audio device disconnected. Press Play to resume."
                    : $"{_outputDeviceName} disconnected. Plug it back in, or choose another output from the quality badge.";
            RefreshPosition();
            return;
        }

        if (e.Exception is { } readError)
        {
            // File-nya rusak di tengah lagu (gagal di-decode atau dibaca): lagu ini dilewati dan antrean lanjut dengan
            // aturan yang sama seperti Next, juga dalam mode Repeat One (lagu yang rusak akan berhenti di tempat yang sama
            // lagi). Kalau semua lagu di antrean rusak, pemutaran berhenti setelah semuanya dicoba sekali.
            string problem = $"Couldn't continue \"{CurrentTrack?.DisplayTitle}\": {DescribeFileError(readError)}";
            if (_queue.FailedMidSong())
            {
                // Semua lagu di antrean sudah dicoba sekali: berhenti di lagu ini dengan pesannya, seperti PlayChosen yang
                // gagal. Antrean tetap, tidak ada yang dimuat, dan tidak ada yang berjalan sendiri lagi (juga kalau Autoplay
                // terus menambah lagu). Tombol Play mencobanya lagi dari awal (lihat Resume), Next lanjut ke Playing Next.
                _releaseTimer.Stop();
                _outputChangePending = false;
                _interruptedByDevice = false;
                _engine.Close();
                StatusText = problem;
                QualityVisibility = Visibility.Collapsed;
                ShowAppVolume();
                RefreshPosition();
                return;
            }

            Advance(problem);
            return;
        }

        // Lagu selesai dengan normal: ulangi kalau Repeat One, selain itu lanjut ke lagu berikutnya
        _queue.ResetFailures();
        if (RepeatMode == RepeatMode.One && CurrentTrack is { } track)
            PlayChosen(track, keepHistory: false);
        else
            Advance();
    }

    private async Task LoadCoverAsync(Track track)
    {
        // Setiap permintaan diberi nomor, dan hanya yang terbaru yang boleh mengisi cover. Membandingkan CurrentTrack
        // dengan ReferenceEquals tidak bisa dipakai: Track adalah record, jadi lagu yang sama dari daftar lain (sama
        // isinya, beda objek) tidak menggantikan CurrentTrack, dan cover-nya lalu tidak pernah tampil.
        int request = ++_coverRequest;
        CurrentCover = null;
        var cover = await CoverImageLoader.LoadAsync(track.FilePath, decodePixelWidth: 104);

        // Lewati kalau sementara itu pengguna sudah pindah ke lagu lain (atau kembali lagi: A → B → A)
        if (request == _coverRequest)
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
        // Selama file lagu dilepas untuk edit tag, tampilan tetap menunjukkan posisi dari sebelum dilepas, dan tombol
        // Play/Pause menunjukkan apakah lagu lanjut berbunyi setelah penyimpanan selesai
        var release = _editRelease;
        var position = release?.Position ?? _engine.Position;
        var duration = TrackDuration;
        var remaining = duration > position ? duration - position : TimeSpan.Zero;

        PositionSeconds = position.TotalSeconds;
        DurationSeconds = Math.Max(duration.TotalSeconds, 1);
        ElapsedText = Formatters.Time(position);
        RemainingText = "-" + Formatters.Time(remaining);
        IsPlaying = release?.Resume ?? (_engine.State == PlaybackState.Playing);
        PlayPauseSymbol = IsPlaying ? Symbol.Pause : Symbol.Play;
    }
}
