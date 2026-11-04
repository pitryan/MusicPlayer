namespace MusicPlayer.Library.Tests;

/// <summary>
/// Tes aturan antrean tanpa audio. Pembantu di bawah (TryPlay, PlayQueue, PlayChosen, Next) meniru cara PlaybackService
/// memakai TrackQueue: "memutar" lagu cukup menjadikannya lagu sekarang, dan lagu di _broken dianggap file-nya tidak bisa
/// dibuka.
/// </summary>
public sealed class TrackQueueTests
{
    private static readonly Track A = Song("A"), B = Song("B"), C = Song("C"), D = Song("D"), E = Song("E");

    private readonly List<Entry> _upNext = [];
    private readonly TrackQueue<Entry> _queue;
    private readonly HashSet<Track> _broken = [];

    public TrackQueueTests()
    {
        _queue = new TrackQueue<Entry>(_upNext, (track, autoplay) => new Entry(track, autoplay), new Random(1));
    }

    private IEnumerable<Track> UpNextTracks => _upNext.Select(e => e.Track);

    [Fact]
    public void Start_SongsAfterTheChosenOneAreUpNext_EarlierOnesArePrevious()
    {
        PlayQueue([A, B, C, D], 1);

        Assert.Equal(B, _queue.Current);
        Assert.Equal([A], _queue.Previous);
        Assert.Equal([C, D], UpNextTracks);
    }

    [Fact]
    public void Start_WithShuffle_QueuesEveryOtherSongIncludingEarlierOnes()
    {
        _queue.SetShuffle(true);
        PlayQueue([A, B, C, D, E], 2);

        Assert.Equal(C, _queue.Current);
        Assert.Empty(_queue.Previous);
        Assert.Equal([A, B, D, E], UpNextTracks.OrderBy(t => t.Title));
    }

    [Fact]
    public void Next_PlaysTheTopSong_AndKeepsTheOldOneForPrevious()
    {
        PlayQueue([A, B, C], 0);

        Assert.True(Next().Played);
        Assert.Equal(B, _queue.Current);
        Assert.Equal([A], _queue.Previous);
        Assert.Equal([C], UpNextTracks);
    }

    [Fact]
    public void Next_AtTheEndWithoutRepeat_Stops()
    {
        PlayQueue([A, B], 1);

        Assert.False(Next().Played);
        Assert.Null(_queue.Current);
        Assert.Empty(_queue.Previous);
    }

    [Fact]
    public void Next_AtTheEndWithRepeat_StartsAgainFromTheFirstSong()
    {
        _queue.SetRepeat(true);
        PlayQueue([A, B, C], 2);

        Assert.True(Next().Played);
        Assert.Equal(A, _queue.Current);
        Assert.Empty(_queue.Previous);
        Assert.Equal([B, C], UpNextTracks);
    }

    [Fact]
    public void Next_SkipsSongsThatCantBeOpened()
    {
        _broken.Add(B);
        PlayQueue([A, B, C], 0);

        var result = Next();

        Assert.Equal(C, _queue.Current);
        Assert.Equal([A], _queue.Previous); // lagu yang dilewati tidak masuk riwayat
        Assert.Equal((1, B, "the file wasn't found."), (result.Skipped, result.FirstSkipped, result.FirstSkippedReason));
        Assert.Contains(B.FilePath, _queue.Unplayable);
    }

    [Fact]
    public void Next_EverySongBrokenWithRepeat_StopsAfterTryingEachOnce()
    {
        // Misalnya drive-nya dicabut di tengah lagu A: antrean diulang sekali, lalu berhenti, tidak berputar terus
        _queue.SetRepeat(true);
        PlayQueue([A, B, C], 0);
        _broken.UnionWith([A, B, C]);

        var result = Next();

        Assert.False(result.Played);
        Assert.Equal(3, result.Skipped);
    }

    [Fact]
    public void Autoplay_StartsAfterTheOwnQueueEnds_WithoutTheSongsJustPlayed()
    {
        var excluded = UseAutoplay();
        PlayQueue([A, B], 0);
        Assert.Equal([B], UpNextTracks); // antrean sendiri masih ada

        Next();

        Assert.Equal(TrackQueue<Entry>.AutoplayCount, _upNext.Count);
        Assert.All(_upNext, e => Assert.True(e.IsAutoplay));
        Assert.Contains(A.FilePath, excluded);
        Assert.Contains(B.FilePath, excluded);
    }

    [Fact]
    public void AddToQueue_GoesBelowTheOwnQueueButAboveAutoplay_PlayNextGoesOnTop()
    {
        UseAutoplay();
        PlayQueue([A], 0);

        _queue.Add(C);
        _queue.Add(D);
        _queue.PlayNext(E);

        Assert.Equal([E, C, D], _upNext.TakeWhile(e => !e.IsAutoplay).Select(e => e.Track));
        Assert.Equal(TrackQueue<Entry>.AutoplayCount, _upNext.Count(e => e.IsAutoplay));
    }

    [Fact]
    public void Repeat_RemovesAutoplaySongs_TurningItOffBringsThemBack()
    {
        UseAutoplay();
        PlayQueue([A], 0);
        Assert.Equal(TrackQueue<Entry>.AutoplayCount, _upNext.Count);

        _queue.SetRepeat(true);
        Assert.Empty(_upNext);

        _queue.SetRepeat(false);
        Assert.Equal(TrackQueue<Entry>.AutoplayCount, _upNext.Count(e => e.IsAutoplay));
    }

    [Fact]
    public void ShuffleOff_RestoresTheOriginalOrder_KeepingSongsAddedMeanwhile()
    {
        var (x, y) = (Song("X"), Song("Y"));
        PlayQueue([A, B, C, D, E], 0);

        _queue.SetShuffle(true);
        _queue.PlayNext(x);
        _queue.Add(y);
        _queue.SetShuffle(false);

        Assert.Equal([x, B, C, D, E, y], UpNextTracks);
    }

    [Fact]
    public void RemovedSong_DoesNotComeBackWhenShuffleIsTurnedOff()
    {
        PlayQueue([A, B, C, D], 0);

        _queue.SetShuffle(true);
        _queue.Remove(_upNext.Single(e => e.Track == C));
        _queue.SetShuffle(false);

        Assert.Equal([B, D], UpNextTracks);
    }

    [Fact]
    public void RemovingTheLastOwnSong_FillsAutoplayWithoutIt()
    {
        var excluded = UseAutoplay();
        PlayQueue([A, B], 0);

        _queue.Remove(_upNext.Single());

        Assert.Equal(TrackQueue<Entry>.AutoplayCount, _upNext.Count(e => e.IsAutoplay));
        Assert.Contains(B.FilePath, excluded);
    }

    [Fact]
    public void StepBack_ReturnsThePreviousSong_AndPutsTheCurrentOneOnTop()
    {
        PlayQueue([A, B, C], 1);

        var previous = _queue.StepBack();
        PlayChosen(previous!);

        Assert.Equal(A, previous);
        Assert.Equal(A, _queue.Current);
        Assert.Empty(_queue.Previous);
        Assert.Equal([B, C], UpNextTracks);
        Assert.Null(_queue.StepBack()); // sudah di lagu pertama
    }

    [Fact]
    public void FailedMidSong_StopsOnceEverySongInTheQueueFailedInARow()
    {
        PlayQueue([A, B, C], 0);

        // Seperti PlaybackService: lagu yang rusak di tengah dilewati dengan Advance, tanpa mengulang hitungannya
        Assert.False(_queue.FailedMidSong());
        _queue.Advance(TryPlay);
        Assert.False(_queue.FailedMidSong());
        _queue.Advance(TryPlay);
        Assert.True(_queue.FailedMidSong());

        // Lagu yang selesai dengan normal (atau dipilih sendiri) memulai hitungan dari nol
        _queue.ResetFailures();
        Assert.False(_queue.FailedMidSong());
    }

    [Fact]
    public void ReplaceTracks_UpdatesEachSongOnce_EvenWhenItIsInBothUpNextAndTheUnshuffledOrder()
    {
        // Lokasi baru di dalam folder lama: penggantian yang dijalankan dua kali akan menjadi "Moved\Moved"
        static Track? Moved(Track t) => t with { FilePath = t.FilePath.Replace(@"C:\Music\", @"C:\Music\Moved\") };
        static string MovedPath(Track t) => t.FilePath.Replace(@"C:\Music\", @"C:\Music\Moved\");

        PlayQueue([A, B, C, D], 1);
        _queue.SetShuffle(true);

        Assert.True(_queue.ReplaceTracks(Moved));

        Assert.Equal(MovedPath(B), _queue.Current!.FilePath);
        Assert.Equal([MovedPath(A)], _queue.Previous.Select(t => t.FilePath));
        Assert.All(_upNext, e => Assert.Equal(1, e.Updates));
        _queue.SetShuffle(false);
        Assert.Equal([MovedPath(C), MovedPath(D)], UpNextTracks.Select(t => t.FilePath));
    }

    [Fact]
    public void ReplaceTracks_SameContent_KeepsTheCurrentObject()
    {
        // PlaybackService membandingkan objek lagu sekarang untuk tahu apakah lagunya berganti (seperti SetProperty)
        PlayQueue([A, B], 0);
        var before = _queue.Current;

        Assert.True(_queue.ReplaceTracks(t => t with { }));
        Assert.Same(before, _queue.Current);
    }

    [Fact]
    public void SnapshotAndRestore_BringBackTheQueue_IncludingTheUnshuffledOrder()
    {
        UseAutoplay();
        PlayQueue([A, B, C, D, E], 1);
        _queue.SetShuffle(true);
        var shuffled = UpNextTracks.ToList();
        Assert.NotEqual([C, D, E], shuffled); // kalau acakannya kebetulan sama, tes ini tidak membuktikan apa-apa

        var session = _queue.Snapshot(TimeSpan.FromSeconds(42))!;
        var (restored, upNext) = NewQueue(shuffle: true);
        Assert.True(restored.Restore(session, Library(A, B, C, D, E)));

        Assert.Equal(B, restored.Current);
        Assert.Equal([A], restored.Previous);
        Assert.Equal(shuffled, upNext.Select(e => e.Track));
        Assert.Equal(TimeSpan.FromSeconds(42), session.Position);

        // Shuffle dimatikan setelah aplikasi dibuka lagi: urutan aslinya tetap kembali
        restored.SetShuffle(false);
        Assert.Equal([C, D, E], upNext.Select(e => e.Track));
    }

    [Fact]
    public void SnapshotAndRestore_KeepAutoplaySongsBelowTheOwnQueue()
    {
        UseAutoplay();
        PlayQueue([A], 0);
        _queue.Add(B);

        var (restored, upNext) = NewQueue();
        restored.Restore(_queue.Snapshot(TimeSpan.Zero)!, path => Library(A, B)(path) ?? Song(Path.GetFileNameWithoutExtension(path)));

        Assert.False(upNext[0].IsAutoplay);
        Assert.Equal(B, upNext[0].Track);
        Assert.Equal(TrackQueue<Entry>.AutoplayCount, upNext.Count(e => e.IsAutoplay));
    }

    [Fact]
    public void Restore_SkipsSongsNoLongerInTheLibrary()
    {
        PlayQueue([A, B, C, D], 2);
        var session = _queue.Snapshot(TimeSpan.Zero)!;

        var (restored, upNext) = NewQueue();
        Assert.True(restored.Restore(session, Library(B, C)));

        Assert.Equal(C, restored.Current);
        Assert.Equal([B], restored.Previous);
        Assert.Empty(upNext);
    }

    [Fact]
    public void Restore_CurrentSongNoLongerInTheLibrary_ChangesNothing()
    {
        PlayQueue([A, B], 0);
        var session = _queue.Snapshot(TimeSpan.Zero)!;

        var (restored, upNext) = NewQueue();
        Assert.False(restored.Restore(session, Library(B)));

        Assert.Null(restored.Current);
        Assert.Empty(upNext);
    }

    [Fact]
    public void Snapshot_NothingPlaying_IsNull() => Assert.Null(_queue.Snapshot(TimeSpan.Zero));

    // ── Meniru PlaybackService ───────────────────────────────

    // Play: file yang tidak bisa dibuka dicatat, yang lain menjadi lagu sekarang (ShowTrack)
    private bool TryPlay(Track track, bool keepHistory, out string error)
    {
        if (_broken.Contains(track))
        {
            _queue.Unplayable.Add(track.FilePath);
            error = "the file wasn't found.";
            return false;
        }

        _queue.Unplayable.Remove(track.FilePath);
        Show(track, keepHistory);
        error = "";
        return true;
    }

    private void Show(Track track, bool keepHistory)
    {
        _queue.MoveTo(track, keepHistory);
        _queue.TopUpAutoplay();
    }

    private void PlayQueue(IReadOnlyList<Track> tracks, int startIndex)
    {
        _queue.Start(tracks, startIndex);
        PlayChosen(tracks[startIndex]);
    }

    // Lagu yang dipilih sendiri tetap tampil sebagai lagu sekarang, walaupun file-nya tidak bisa dibuka
    private void PlayChosen(Track track, bool keepHistory = false)
    {
        _queue.ResetFailures();
        if (!TryPlay(track, keepHistory, out _))
            Show(track, keepHistory);
    }

    private AdvanceResult Next()
    {
        _queue.ResetFailures();
        var result = _queue.Advance(TryPlay);
        if (!result.Played)
            _queue.Stop(); // StopAll
        return result;
    }

    /// <summary>Autoplay menyala dengan lagu "Auto1", "Auto2", …; path yang dikecualikan pada panggilan terakhir dicatat.</summary>
    private HashSet<string> UseAutoplay()
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _queue.AutoplaySource = (_, exclude, count) =>
        {
            excluded.Clear();
            excluded.UnionWith(exclude);
            return Enumerable.Range(1, count).Select(i => Song($"Auto{i}")).ToList();
        };
        _queue.SetAutoplay(true);
        return excluded;
    }

    private static Track Song(string name) => new() { FilePath = $@"C:\Music\{name}.flac", FolderPath = @"C:\Music", Title = name };

    /// <summary>Antrean baru, seperti saat aplikasi dibuka lagi; shuffle sudah dipulihkan dari pengaturan.</summary>
    private static (TrackQueue<Entry> Queue, List<Entry> UpNext) NewQueue(bool shuffle = false)
    {
        var upNext = new List<Entry>();
        var queue = new TrackQueue<Entry>(upNext, (track, autoplay) => new Entry(track, autoplay), new Random(2));
        queue.SetShuffle(shuffle);
        return (queue, upNext);
    }

    /// <summary>Library yang hanya berisi lagu-lagu ini, seperti LibraryDatabase.GetTracksByPath.</summary>
    private static Func<string, Track?> Library(params Track[] tracks)
    {
        var byPath = tracks.ToDictionary(t => t.FilePath, StringComparer.OrdinalIgnoreCase);
        return path => byPath.GetValueOrDefault(path);
    }

    private sealed class Entry(Track initial, bool autoplay) : IQueueEntry
    {
        public Track Track { get; private set; } = initial;

        public bool IsAutoplay { get; } = autoplay;

        /// <summary>Berapa kali UpdateTrack dipanggil.</summary>
        public int Updates { get; private set; }

        public void UpdateTrack(Track track)
        {
            Track = track;
            Updates++;
        }
    }
}
