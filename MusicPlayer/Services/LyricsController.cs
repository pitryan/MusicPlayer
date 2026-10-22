using System;
using System.Threading;
using System.Threading.Tasks;
using MusicPlayer.Library;

namespace MusicPlayer.Services;

/// <summary>Keadaan lirik lagu yang sedang diputar.</summary>
public enum LyricsState
{
    /// <summary>Tidak ada lagu yang diputar.</summary>
    Idle,

    Loading,

    /// <summary>Lirik ada di <see cref="LyricsController.Current"/>.</summary>
    Ready,

    NotFound,
    Instrumental,

    /// <summary>Tidak ada lirik lokal, dan LRCLIB tidak bisa dihubungi. Alasannya di ErrorMessage.</summary>
    Unavailable,
}

/// <summary>
/// Lirik lagu yang sedang diputar, dipakai bersama oleh panel lirik di jendela utama dan layar penuh. Lirik baru dicari
/// selama ada tampilan lirik yang terbuka (lihat <see cref="AddViewer"/>), supaya LRCLIB tidak dihubungi untuk setiap
/// lagu yang diputar. Semua event dipicu di UI thread.
/// </summary>
public sealed class LyricsController
{
    // Jeda instrumental yang lebih pendek dari ini tidak diberi tanda "• • •"
    private static readonly TimeSpan MinimumInterlude = TimeSpan.FromSeconds(5);

    private readonly PlaybackService _playback;
    private readonly LyricsFinder _finder;
    private int _viewers;

    // Lagu yang liriknya sedang atau sudah dicari. Null = perlu dicari begitu ada tampilan yang terbuka.
    private string? _trackPath;
    private CancellationTokenSource? _cts;

    public LyricsController(PlaybackService playback, LyricsFinder finder)
    {
        _playback = playback;
        _finder = finder;
        playback.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaybackService.CurrentTrack))
                Refresh();
        };
    }

    /// <summary>Dipicu setiap kali State, Current, IsCheckingOnline, atau ErrorMessage berubah.</summary>
    public event EventHandler? Changed;

    public LyricsState State { get; private set; } = LyricsState.Idle;

    /// <summary>Lirik yang ditampilkan saat State = Ready, sudah lengkap dengan baris jeda instrumental.</summary>
    public FoundLyrics? Current { get; private set; }

    /// <summary>True selama lirik teks biasa dari file sudah tampil dan LRCLIB sedang dicek untuk versi bertanda waktu.</summary>
    public bool IsCheckingOnline { get; private set; }

    public string ErrorMessage { get; private set; } = "";

    /// <summary>Pencarian yang sedang berjalan. Hanya untuk tes.</summary>
    internal Task LoadTask { get; private set; } = Task.CompletedTask;

    /// <summary>Tampilan lirik dibuka: lirik lagu sekarang dicari kalau belum.</summary>
    public void AddViewer()
    {
        _viewers++;
        Refresh();
    }

    public void RemoveViewer()
    {
        if (_viewers > 0)
            _viewers--;
    }

    /// <summary>Tombol "Try Again" saat LRCLIB tidak bisa dihubungi.</summary>
    public void Retry()
    {
        _trackPath = null;
        Refresh();
    }

    private void Refresh()
    {
        var track = _playback.CurrentTrack;
        if (track is null)
        {
            Cancel();
            _trackPath = null;
            IsCheckingOnline = false;
            Publish(LyricsState.Idle, null);
            return;
        }

        if (string.Equals(track.FilePath, _trackPath, StringComparison.OrdinalIgnoreCase))
            return; // lirik lagu ini sudah ada atau sedang dicari (misalnya Repeat One)

        Cancel();
        _trackPath = null;
        if (_viewers > 0)
            LoadTask = LoadAsync(track);
        // Tanpa tampilan lirik yang terbuka, pencarian ditunda sampai panel lirik dibuka
    }

    private async Task LoadAsync(Track track)
    {
        var cts = new CancellationTokenSource();
        _cts = cts;
        var token = cts.Token;
        _trackPath = track.FilePath;
        IsCheckingOnline = false;
        ErrorMessage = "";
        Publish(LyricsState.Loading, null);

        FoundLyrics? local = null;
        OnlineLookup online;
        try
        {
            // Tag dan file .lrc dibaca di luar UI thread; drive eksternal atau jaringan bisa lambat
            local = await Task.Run(() => _finder.FindLocal(track), token);
            if (token.IsCancellationRequested)
                return;

            if (local is { Lyrics.IsSynced: true })
            {
                Publish(LyricsState.Ready, Prepare(local));
                return;
            }

            // Lirik teks biasa dari file tampil dulu, sambil mencari versi bertanda waktu di LRCLIB
            if (local is not null)
            {
                IsCheckingOnline = true;
                Publish(LyricsState.Ready, Prepare(local));
            }

            online = await Task.Run(() => _finder.FindOnlineAsync(track, token), token);
        }
        catch (OperationCanceledException)
        {
            return; // lagu sudah berganti
        }
        catch (Exception ex)
        {
            // Jaga-jaga: kesalahan tak terduga tidak boleh membuat panel lirik terus "memuat". Lirik lokal yang
            // sudah ketemu tetap tampil.
            online = new OnlineLookup(OnlineStatus.Failed, Error: ex.Message);
        }

        if (token.IsCancellationRequested)
            return;

        IsCheckingOnline = false;
        var choice = LyricsFinder.Choose(local, online);
        ErrorMessage = choice.Error ?? "";
        switch (choice.Outcome)
        {
            case LyricsOutcome.Found:
                // Lirik dari file yang sudah tampil tidak diganti objek baru, supaya tampilan tidak dibangun ulang
                Publish(LyricsState.Ready,
                    ReferenceEquals(choice.Lyrics, local) && Current is not null ? Current : Prepare(choice.Lyrics!));
                break;
            case LyricsOutcome.Instrumental:
                Publish(LyricsState.Instrumental, null);
                break;
            case LyricsOutcome.Unavailable:
                Publish(LyricsState.Unavailable, null);
                break;
            default:
                Publish(LyricsState.NotFound, null);
                break;
        }
    }

    private static FoundLyrics Prepare(FoundLyrics found) =>
        found with { Lyrics = found.Lyrics.WithInterludes(MinimumInterlude) };

    // Token sengaja tidak di-Dispose: pencarian lama mungkin masih memegangnya, dan tanpa timer tidak ada yang bocor
    private void Cancel()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private void Publish(LyricsState state, FoundLyrics? lyrics)
    {
        State = state;
        Current = lyrics;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Keterangan kecil di bawah lirik: dari mana lirik ini diambil.</summary>
    public static string DescribeSource(FoundLyrics lyrics) => lyrics.Source switch
    {
        LyricsSource.Tag => "Lyrics from the song file",
        LyricsSource.LrcFile => $"Lyrics from {lyrics.FileName}",
        _ => "Lyrics from LRCLIB",
    };
}
