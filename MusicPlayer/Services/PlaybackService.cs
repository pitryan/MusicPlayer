using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MusicPlayer.Audio;
using MusicPlayer.Helpers;
using MusicPlayer.Library;
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
    private readonly DispatcherQueueTimer _timer;
    private IReadOnlyList<Track> _queue = [];
    private int _index = -1;
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

    public PlaybackService(DispatcherQueue dispatcherQueue)
    {
        _engine.PlaybackStopped += OnPlaybackStopped;

        // Perbarui posisi lagu di tampilan 4 kali per detik
        _timer = dispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += (_, _) => RefreshPosition();
        _timer.Start();
    }

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

    /// <summary>Memutar daftar lagu mulai dari lagu ke-startIndex; daftar itu jadi antreannya.</summary>
    public void PlayQueue(IReadOnlyList<Track> tracks, int startIndex)
    {
        _queue = tracks;
        PlayAt(startIndex);
    }

    public void TogglePlayPause()
    {
        if (CurrentTrack is null)
            return;

        _engine.TogglePause();
        RefreshPosition();
    }

    public void Next()
    {
        if (_index + 1 < _queue.Count)
            PlayAt(_index + 1);
        else
            StopAll();
    }

    public void Previous()
    {
        if (CurrentTrack is null)
            return;

        // Seperti pemutar musik pada umumnya: kalau lagu sudah berjalan lebih dari
        // 3 detik, tombol ini mengulang lagu dari awal, bukan pindah ke lagu sebelumnya
        if (_engine.Position > TimeSpan.FromSeconds(3) || _index <= 0)
            _engine.Seek(TimeSpan.Zero);
        else
            PlayAt(_index - 1);

        RefreshPosition();
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
        _engine.Dispose();
        _device?.Dispose();
    }

    private void PlayAt(int index)
    {
        if (index < 0 || index >= _queue.Count)
            return;

        _index = index;
        var track = _queue[index];
        MMDevice? device = null;

        try
        {
            // Selalu pakai perangkat default saat ini, supaya ganti headset/DAC langsung terpakai
            using (var enumerator = new MMDeviceEnumerator())
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            _engine.Open(track.FilePath, device); // player lama ditutup lebih dulu di sini
            _device?.Dispose();
            _device = device;
            _engine.Play();

            CurrentTrack = track;
            TitleText = track.DisplayTitle;
            SubtitleText = $"{track.DisplayArtist} — {track.DisplayAlbum}";
            StatusText = _engine.IsExclusive
                ? $"EXCLUSIVE · {_engine.OutputDescription}"
                : "SHARED · Windows mixer";
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
        _index = -1;
        CurrentTrack = null;
        CurrentCover = null;
        TitleText = IdleTitle;
        SubtitleText = IdleSubtitle;
        StatusText = "";
        RefreshPosition();
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

        Next(); // lagu selesai dengan normal: lanjut ke lagu berikutnya
    }

    private async Task LoadCoverAsync(Track track)
    {
        CurrentCover = null;
        var cover = await CoverImageLoader.LoadAsync(track.FilePath, decodePixelWidth: 104);

        // Lewati kalau sementara itu pengguna sudah pindah ke lagu lain
        if (ReferenceEquals(CurrentTrack, track))
            CurrentCover = cover;
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
