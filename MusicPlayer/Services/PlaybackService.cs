using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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

    /// <summary>Contoh: "Hi-Res Lossless", "Lossless", atau "MP3 320 kbps".</summary>
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

            if (value)
                ShuffleUpNext();
            else
                ReplaceUpNext(QueueOrder.Restored(UpNext.ToList(), _originalOrder));
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
                OnPropertyChanged(nameof(RepeatSymbol));
            }
        }
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

    public void CycleRepeat() => RepeatMode = RepeatMode switch
    {
        RepeatMode.Off => RepeatMode.All,
        RepeatMode.All => RepeatMode.One,
        _ => RepeatMode.Off,
    };

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
            Play(track);
        else
            UpNext.Add(new QueueEntry(track));
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
    }

    public void TogglePlayPause()
    {
        if (CurrentTrack is null)
            return;

        _engine.TogglePause();
        RefreshPosition();
    }

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

    /// <summary>Lompat ke posisi tertentu, dari 0 (awal lagu) sampai 1 (akhir lagu).</summary>
    public void SeekToFraction(double fraction)
    {
        if (CurrentTrack is null)
            return;

        _engine.Seek(TimeSpan.FromSeconds(Math.Clamp(fraction, 0, 1) * _engine.Duration.TotalSeconds));
        RefreshPosition();
    }

    public void Dispose()
    {
        _timer.Stop();
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
        MMDevice? device = null;

        try
        {
            // Selalu pakai perangkat default saat ini, supaya ganti headset/DAC langsung terpakai
            using (var enumerator = new MMDeviceEnumerator())
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            _engine.Open(track.FilePath, device); // player lama ditutup lebih dulu di sini
            DetachDeviceVolume();
            _device?.Dispose();
            _device = device;
            AttachVolume(device); // sebelum Play, supaya tidak ada sesaat suara dengan volume yang salah
            _engine.Play();

            CurrentTrack = track;
            _listenedSeconds = 0;
            _playRecorded = false;
            TitleText = track.DisplayTitle;
            SubtitleText = $"{track.DisplayArtist} — {track.DisplayAlbum}";
            StatusText = "";
            UpdateQualityInfo(track, device);
            _ = LoadCoverAsync(track);
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(device, _device))
                device?.Dispose();
            StatusText = $"Couldn't play \"{track.DisplayTitle}\": {ex.Message}";
        }

        RefreshPosition();
    }

    private void StopAll()
    {
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

    private void UpdateQualityInfo(Track track, MMDevice device)
    {
        var quality = AudioQuality.Describe(track);
        QualityBadgeText = quality.Badge;
        QualityShortText = quality.ShortBadge;
        QualityDetailText = quality.Detail;
        DeviceText = device.FriendlyName;

        if (_engine.ExclusiveFormat is { } format)
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
            OutputDetailText = _engine.FallbackReason is { } reason ? $"{reason} {mixed}" : mixed;
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
            StatusText = $"Playback stopped: {e.Exception.Message}";
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
        PlayPauseSymbol = _engine.State == PlaybackState.Playing ? Symbol.Pause : Symbol.Play;
    }
}
