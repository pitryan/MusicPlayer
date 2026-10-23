using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace MusicPlayer.Services;

/// <summary>
/// Kontrol media Windows (System Media Transport Controls): panel media di Quick Settings dan saat volume diubah, serta
/// tombol media keyboard/headset, yang diteruskan Windows ke sini walaupun jendela aplikasi tidak aktif.
/// </summary>
public sealed class WindowsMediaControls : IMediaControls, IDisposable
{
    // Aplikasi desktop mendapatkan kontrol media lewat MediaPlayer. MediaPlayer ini tidak memutar apa pun (audio tetap
    // lewat WASAPI); CommandManager-nya dimatikan supaya tombol media tidak diproses olehnya sendiri.
    private readonly MediaPlayer _player;
    private readonly SystemMediaTransportControls _controls;
    private readonly DispatcherQueue _dispatcher;
    private InMemoryRandomAccessStream? _coverStream;

    public WindowsMediaControls(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _player = new MediaPlayer();
        _player.CommandManager.IsEnabled = false;
        _controls = _player.SystemMediaTransportControls;
        _controls.IsEnabled = true;
        _controls.IsPlayEnabled = true;
        _controls.IsPauseEnabled = true;
        _controls.IsNextEnabled = true;
        _controls.IsPreviousEnabled = true;
        _controls.IsStopEnabled = false;

        // Event kontrol media datang dari thread lain; diteruskan ke UI thread karena PlaybackService milik UI thread
        _controls.ButtonPressed += (_, e) =>
        {
            MediaButton? button = e.Button switch
            {
                SystemMediaTransportControlsButton.Play => MediaButton.Play,
                SystemMediaTransportControlsButton.Pause => MediaButton.Pause,
                SystemMediaTransportControlsButton.Next => MediaButton.Next,
                SystemMediaTransportControlsButton.Previous => MediaButton.Previous,
                _ => null,
            };
            if (button is { } pressed)
                _dispatcher.TryEnqueue(() => ButtonPressed?.Invoke(this, pressed));
        };
        _controls.PlaybackPositionChangeRequested += (_, e) =>
        {
            var position = e.RequestedPlaybackPosition;
            _dispatcher.TryEnqueue(() => SeekRequested?.Invoke(this, position));
        };
        _controls.ShuffleEnabledChangeRequested += (_, e) =>
        {
            bool enabled = e.RequestedShuffleEnabled;
            _dispatcher.TryEnqueue(() => ShuffleRequested?.Invoke(this, enabled));
        };
        _controls.AutoRepeatModeChangeRequested += (_, e) =>
        {
            var mode = e.RequestedAutoRepeatMode switch
            {
                MediaPlaybackAutoRepeatMode.Track => RepeatMode.One,
                MediaPlaybackAutoRepeatMode.List => RepeatMode.All,
                _ => RepeatMode.Off,
            };
            _dispatcher.TryEnqueue(() => RepeatRequested?.Invoke(this, mode));
        };
    }

    public event EventHandler<MediaButton>? ButtonPressed;
    public event EventHandler<TimeSpan>? SeekRequested;
    public event EventHandler<bool>? ShuffleRequested;
    public event EventHandler<RepeatMode>? RepeatRequested;

    public void SetStatus(MediaStatus status) => _controls.PlaybackStatus = status switch
    {
        MediaStatus.Playing => MediaPlaybackStatus.Playing,
        MediaStatus.Paused => MediaPlaybackStatus.Paused,
        _ => MediaPlaybackStatus.Closed,
    };

    public async Task SetInfoAsync(MediaInfo? info)
    {
        var updater = _controls.DisplayUpdater;
        if (info is null)
        {
            updater.ClearAll();
            updater.Update();
            return;
        }

        // Dikosongkan dulu, supaya cover lagu sebelumnya tidak tertinggal
        updater.ClearAll();
        updater.Type = MediaPlaybackType.Music;
        updater.MusicProperties.Title = info.Title;
        updater.MusicProperties.Artist = info.Artist;
        updater.MusicProperties.AlbumTitle = info.Album;
        updater.MusicProperties.AlbumArtist = info.AlbumArtist;
        updater.MusicProperties.TrackNumber = info.TrackNumber;

        if (info.Cover is { } cover)
        {
            // Stream cover disimpan selama ditampilkan; Windows membacanya kapan saja
            var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(cover.AsBuffer());
            stream.Seek(0);
            _coverStream?.Dispose();
            _coverStream = stream;
            updater.Thumbnail = RandomAccessStreamReference.CreateFromStream(stream);
        }

        updater.Update();
    }

    public void SetTimeline(TimeSpan position, TimeSpan duration) =>
        _controls.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
        {
            StartTime = TimeSpan.Zero,
            MinSeekTime = TimeSpan.Zero,
            Position = position,
            MaxSeekTime = duration,
            EndTime = duration,
        });

    public void SetShuffle(bool enabled) => _controls.ShuffleEnabled = enabled;

    public void SetRepeat(RepeatMode mode) => _controls.AutoRepeatMode = mode switch
    {
        RepeatMode.One => MediaPlaybackAutoRepeatMode.Track,
        RepeatMode.All => MediaPlaybackAutoRepeatMode.List,
        _ => MediaPlaybackAutoRepeatMode.None,
    };

    public void Dispose()
    {
        _coverStream?.Dispose();
        _player.Dispose();
    }
}
