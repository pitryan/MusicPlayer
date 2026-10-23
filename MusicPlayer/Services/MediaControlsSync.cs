using System;
using System.ComponentModel;
using System.Threading.Tasks;
using MusicPlayer.Library;

namespace MusicPlayer.Services;

public enum MediaButton
{
    Play,
    Pause,
    Next,
    Previous,
}

public enum MediaStatus
{
    /// <summary>Tidak ada lagu yang dimuat.</summary>
    Closed,
    Playing,
    Paused,
}

/// <summary>Data lagu untuk panel media Windows. <see cref="Cover"/> berisi file gambar (JPEG/PNG) dari tag.</summary>
public sealed record MediaInfo(string Title, string Artist, string Album, string AlbumArtist, uint TrackNumber, byte[]? Cover);

/// <summary>
/// Kontrol media sistem: panel media Windows dan tombol media keyboard/headset. Implementasi aslinya
/// <see cref="WindowsMediaControls"/>; event-nya selalu dipicu di UI thread.
/// </summary>
public interface IMediaControls
{
    event EventHandler<MediaButton>? ButtonPressed;
    event EventHandler<TimeSpan>? SeekRequested;
    event EventHandler<bool>? ShuffleRequested;
    event EventHandler<RepeatMode>? RepeatRequested;

    void SetStatus(MediaStatus status);

    /// <summary>Null mengosongkan panel media (tidak ada lagu).</summary>
    Task SetInfoAsync(MediaInfo? info);

    void SetTimeline(TimeSpan position, TimeSpan duration);

    void SetShuffle(bool enabled);

    void SetRepeat(RepeatMode mode);
}

/// <summary>
/// Menyamakan kontrol media sistem dengan PlaybackService, dua arah: lagu, status, posisi, shuffle, dan repeat ke panel
/// media Windows; tombol media dan perintah dari panel itu ke PlaybackService. Terpisah dari API Windows supaya bisa
/// diuji dengan kontrol tiruan.
/// </summary>
public sealed class MediaControlsSync
{
    // Panel media Windows tidak menghitung posisi sendiri dengan tepat; diperbarui berkala dan setiap kali melompat
    private static readonly TimeSpan TimelineInterval = TimeSpan.FromSeconds(5);
    private const double JumpThresholdSeconds = 2;

    private readonly PlaybackService _playback;
    private readonly IMediaControls _controls;
    private readonly Func<string, byte[]?> _readCover;
    private double _reportedPosition = -1;
    private DateTime _reportedAtUtc;
    private string? _infoPath;

    /// <param name="readCover">Membaca cover dari file lagu (CoverArt.TryRead); dipanggil di luar UI thread.</param>
    public MediaControlsSync(PlaybackService playback, IMediaControls controls, Func<string, byte[]?> readCover)
    {
        _playback = playback;
        _controls = controls;
        _readCover = readCover;

        controls.ButtonPressed += (_, button) => OnButton(button);
        controls.SeekRequested += (_, position) =>
        {
            _playback.SeekTo(position);
            ReportTimeline(force: true);
        };
        controls.ShuffleRequested += (_, enabled) => _playback.IsShuffle = enabled;
        controls.RepeatRequested += (_, mode) => _playback.RepeatMode = mode;
        playback.PropertyChanged += OnPlaybackChanged;

        LoadTask = UpdateInfoAsync();
        UpdateStatus();
        ReportTimeline(force: true);
        controls.SetShuffle(playback.IsShuffle);
        controls.SetRepeat(playback.RepeatMode);
    }

    /// <summary>Pemuatan info lagu yang terakhir (termasuk cover). Hanya untuk tes.</summary>
    internal Task LoadTask { get; private set; }

    private void OnButton(MediaButton button)
    {
        switch (button)
        {
            // Tombol Play/Pause di keyboard dikirim Windows sebagai Play atau Pause sesuai status yang kita laporkan;
            // dicek lagi di sini supaya tekanan ganda tidak membalik keadaan
            case MediaButton.Play when !_playback.IsPlaying:
            case MediaButton.Pause when _playback.IsPlaying:
                _playback.TogglePlayPause();
                break;
            case MediaButton.Next:
                _playback.Next();
                break;
            case MediaButton.Previous:
                _playback.Previous();
                break;
        }
    }

    private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlaybackService.CurrentTrack):
                LoadTask = UpdateInfoAsync();
                UpdateStatus();
                ReportTimeline(force: true);
                break;
            case nameof(PlaybackService.IsPlaying):
                UpdateStatus();
                ReportTimeline(force: true);
                break;
            case nameof(PlaybackService.PositionSeconds):
                ReportTimeline(force: false);
                break;
            case nameof(PlaybackService.IsShuffle):
                _controls.SetShuffle(_playback.IsShuffle);
                break;
            case nameof(PlaybackService.RepeatMode):
                _controls.SetRepeat(_playback.RepeatMode);
                break;
        }
    }

    private void UpdateStatus() => _controls.SetStatus(
        _playback.CurrentTrack is null ? MediaStatus.Closed : _playback.IsPlaying ? MediaStatus.Playing : MediaStatus.Paused);

    /// <summary>Info tampil langsung tanpa cover, lalu sekali lagi setelah cover terbaca (membaca tag bisa lambat).</summary>
    private async Task UpdateInfoAsync()
    {
        var track = _playback.CurrentTrack;
        _infoPath = track?.FilePath;
        if (track is null)
        {
            await _controls.SetInfoAsync(null);
            return;
        }

        var info = new MediaInfo(track.DisplayTitle, track.DisplayArtist, track.DisplayAlbum,
            track.AlbumArtist ?? track.DisplayArtist, (uint)Math.Max(track.TrackNumber ?? 0, 0), null);
        await _controls.SetInfoAsync(info);

        byte[]? cover = await Task.Run(() => _readCover(track.FilePath));
        if (cover is not null && _infoPath == track.FilePath)
            await _controls.SetInfoAsync(info with { Cover = cover });
    }

    private void ReportTimeline(bool force)
    {
        double position = _playback.PositionSeconds;
        var now = DateTime.UtcNow;
        if (!force)
        {
            // Saat berbunyi, posisi yang diharapkan maju sesuai waktu; selisih besar berarti lompat (seek)
            double expected = _reportedPosition + (_playback.IsPlaying ? (now - _reportedAtUtc).TotalSeconds : 0);
            bool jumped = Math.Abs(position - expected) > JumpThresholdSeconds;
            if (!jumped && now - _reportedAtUtc < TimelineInterval)
                return;
        }

        _reportedPosition = position;
        _reportedAtUtc = now;
        _controls.SetTimeline(TimeSpan.FromSeconds(position), TimeSpan.FromSeconds(_playback.DurationSeconds));
    }
}
