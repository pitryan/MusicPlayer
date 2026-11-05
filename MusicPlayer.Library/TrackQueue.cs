namespace MusicPlayer.Library;

/// <summary>Satu baris di antrean Playing Next (di aplikasi: QueueEntry).</summary>
public interface IQueueEntry
{
    Track Track { get; }

    /// <summary>Lagu tambahan dari Autoplay, bukan bagian dari antrean sendiri.</summary>
    bool IsAutoplay { get; }

    /// <summary>Data lagu yang baru, misalnya setelah tag-nya diedit atau foldernya dipindah.</summary>
    void UpdateTrack(Track track);
}

/// <summary>
/// Membuka dan memutar lagu untuk <see cref="TrackQueue{TEntry}.Advance"/>. False kalau file-nya tidak bisa dibuka, dengan
/// alasannya di <paramref name="error"/>. Lagu yang berhasil menjadi lagu sekarang lewat MoveTo.
/// </summary>
/// <param name="keepHistory">Lagu sekarang masuk daftar Previous.</param>
public delegate bool TryPlayTrack(Track track, bool keepHistory, out string error);

/// <summary>
/// Hasil <see cref="TrackQueue{TEntry}.Advance"/>. Played false berarti antrean habis dan pemutaran berhenti. Skipped:
/// berapa lagu dilewati karena file-nya tidak bisa dibuka, dengan lagu pertama dan alasannya untuk pesan.
/// </summary>
public sealed record AdvanceResult(bool Played, int Skipped, Track? FirstSkipped, string FirstSkippedReason);

/// <summary>
/// Aturan antrean pemutar, tanpa kode tampilan atau audio supaya bisa diuji: lagu sekarang, daftar Previous, Playing Next,
/// shuffle, repeat, Autoplay, dan batas lagu rusak berturut-turut. PlaybackService yang membuka file dan mengurus tampilan.
/// Playing Next adalah koleksi milik pemanggil (di aplikasi: ObservableCollection yang ditampilkan dan bisa digeser
/// pengguna), jadi urutannya juga bisa berubah dari luar kelas ini.
/// </summary>
/// <typeparam name="TEntry">
/// Satu baris Playing Next. Lagu yang sama bisa masuk antrean dua kali, jadi baris dibedakan dari objeknya, bukan dari isinya.
/// </typeparam>
public sealed class TrackQueue<TEntry> where TEntry : class, IQueueEntry
{
    /// <summary>Jumlah lagu Autoplay yang disiapkan di antrean setelah antrean sendiri habis.</summary>
    public const int AutoplayCount = 5;

    private readonly Func<Track, bool, TEntry> _createEntry;
    private readonly Random _random;
    private readonly List<Track> _previous = [];

    // Urutan Playing Next sebelum diacak, untuk dikembalikan saat shuffle dimatikan
    private List<TEntry> _originalOrder = [];

    // Lagu yang berhenti karena error di tengah file (rusak), berturut-turut tanpa satu pun lagu yang selesai dengan normal,
    // dan batasnya (lihat FailedMidSong)
    private int _failedInARow;
    private int _failedInARowLimit;

    /// <param name="upNext">Koleksi Playing Next; kelas ini mengubah isinya, tapi tidak pernah menggantinya.</param>
    /// <param name="createEntry">Membuat baris baru: (lagu, lagu Autoplay?) → baris.</param>
    /// <param name="random">Untuk shuffle; tes memakai Random dengan seed tetap.</param>
    public TrackQueue(IList<TEntry> upNext, Func<Track, bool, TEntry> createEntry, Random? random = null)
    {
        UpNext = upNext;
        _createEntry = createEntry;
        _random = random ?? Random.Shared;
    }

    /// <summary>Lagu-lagu yang akan diputar setelah lagu sekarang; Advance selalu mengambil yang paling atas.</summary>
    public IList<TEntry> UpNext { get; }

    /// <summary>Lagu-lagu yang sudah diputar sebelum lagu sekarang, untuk tombol Previous dan Repeat.</summary>
    public IReadOnlyList<Track> Previous => _previous;

    public Track? Current { get; private set; }

    /// <summary>
    /// File lagu yang gagal dibuka (hilang, rusak). Tidak dipilih Autoplay, supaya antrean tidak terus melewatinya.
    /// Pemanggil mengeluarkannya lagi begitu file itu berhasil dibuka.
    /// </summary>
    public HashSet<string> Unplayable { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsShuffle { get; private set; }

    /// <summary>
    /// Repeat menyala (All atau One): antrean yang habis diputar lagi dari awal. Repeat One sendiri (lagu yang selesai
    /// diulang) diurus PlaybackService; tombol Next tetap pindah ke lagu berikutnya.
    /// </summary>
    public bool Repeat { get; private set; }

    public bool AutoplayEnabled { get; private set; }

    /// <summary>
    /// Sumber lagu Autoplay: (lagu terakhir, path yang dikecualikan, jumlah) → lagu. Null berarti Autoplay tidak tersedia.
    /// </summary>
    public Func<Track, ISet<string>, int, IReadOnlyList<Track>>? AutoplaySource { get; set; }

    public bool HasPrevious => _previous.Count > 0;

    /// <summary>
    /// Antrean baru dari daftar lagu, mulai dari lagu ke-startIndex (pemanggil lalu memutar lagu itu tanpa keepHistory).
    /// Lagu sesudahnya masuk Playing Next, dan lagu sebelumnya bisa dicapai dengan tombol Previous.
    /// </summary>
    public void Start(IReadOnlyList<Track> tracks, int startIndex)
    {
        _previous.Clear();
        UpNext.Clear();

        if (IsShuffle)
        {
            // Shuffle aktif: lagu yang dipilih diputar dulu, lalu SEMUA lagu lain di daftar itu diacak,
            // termasuk yang letaknya sebelum lagu yang dipilih (kalau tidak, lagu-lagu itu terlewat)
            for (int i = 0; i < tracks.Count; i++)
            {
                if (i != startIndex)
                    UpNext.Add(_createEntry(tracks[i], false));
            }
            ShuffleUpNext();
        }
        else
        {
            _previous.AddRange(tracks.Take(startIndex));
            foreach (var track in tracks.Skip(startIndex + 1))
                UpNext.Add(_createEntry(track, false));
        }
    }

    /// <summary>
    /// Lagu ini menjadi lagu sekarang (sudah berhasil dibuka, atau gagal tapi tetap ditampilkan). Dengan
    /// <paramref name="keepHistory"/>, lagu sebelumnya masuk daftar Previous, juga kalau file-nya sama (lagu yang sama dua
    /// kali di antrean, dua kali putar).
    /// </summary>
    public void MoveTo(Track track, bool keepHistory)
    {
        if (keepHistory && Current is { } old)
            _previous.Add(old);
        ChangeCurrent(track);
    }

    /// <summary>Pemutaran berhenti: tidak ada lagu sekarang, dan daftar Previous dikosongkan. Playing Next tidak berubah.</summary>
    public void Stop()
    {
        _previous.Clear();
        ChangeCurrent(null);
    }

    /// <summary>Menaruh lagu di urutan paling atas Playing Next.</summary>
    public void PlayNext(Track track) => UpNext.Insert(0, _createEntry(track, false));

    /// <summary>Menaruh lagu di urutan paling bawah antrean sendiri, yang selalu di atas lagu Autoplay.</summary>
    public void Add(Track track)
    {
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
            UpNext.Add(_createEntry(track, false));
        else
            UpNext.Insert(autoplayStart, _createEntry(track, false));
    }

    /// <summary>
    /// Lagu dari Playing Next yang dipilih langsung: baris itu dan semua baris di atasnya dikeluarkan (pemanggil lalu memutar
    /// lagunya). False kalau barisnya sudah tidak ada di antrean.
    /// </summary>
    public bool SkipTo(TEntry entry)
    {
        int index = UpNext.IndexOf(entry);
        if (index < 0)
            return false;

        for (int i = 0; i <= index; i++)
            UpNext.RemoveAt(0);
        return true;
    }

    public void Remove(TEntry entry)
    {
        UpNext.Remove(entry);
        _originalOrder.Remove(entry); // supaya tidak kembali saat shuffle dimatikan

        // Sama seperti Clear: kalau antrean sendiri sudah habis, lagu Autoplay langsung disiapkan (atau dilengkapi lagi kalau
        // yang dihapus lagu Autoplay). Lagu yang baru dihapus tidak dipilih lagi sebagai penggantinya.
        TopUpAutoplay(skip: [entry.Track.FilePath]);
    }

    public void Clear()
    {
        UpNext.Clear();
        _originalOrder.Clear();
        TopUpAutoplay(); // dengan Autoplay menyala, pemutaran tetap berlanjut setelah lagu ini
    }

    /// <summary>Shuffle mengacak urutan Playing Next. Saat dimatikan, sisa antrean kembali ke urutan aslinya.</summary>
    public void SetShuffle(bool on)
    {
        if (IsShuffle == on)
            return;
        IsShuffle = on;

        // Lagu Autoplay bukan bagian dari antrean sendiri: dibuang dulu, lalu disiapkan ulang sesudahnya
        RemoveAutoplay();
        if (on)
            ShuffleUpNext();
        else
            ReplaceUpNext(QueueOrder.Restored(UpNext.ToList(), _originalOrder));
        TopUpAutoplay();
    }

    /// <summary>Repeat dan Autoplay tidak berjalan bersamaan: repeat mengulang antrean, jadi lagu Autoplay dibuang.</summary>
    public void SetRepeat(bool on)
    {
        Repeat = on;
        if (on)
            RemoveAutoplay();
        else
            TopUpAutoplay();
    }

    /// <summary>
    /// Autoplay, seperti Apple Music: saat antrean habis, pemutaran berlanjut dengan lagu yang mirip dari library.
    /// Lagu-lagunya tampil di bawah antrean sendiri; Repeat yang menyala mendahuluinya.
    /// </summary>
    public void SetAutoplay(bool on)
    {
        if (AutoplayEnabled == on)
            return;
        AutoplayEnabled = on;
        if (on)
            TopUpAutoplay();
        else
            RemoveAutoplay();
    }

    /// <summary>
    /// Menyiapkan lagu Autoplay di antrean kalau antrean sendiri sudah habis. Dipanggil setiap lagu baru mulai, dan
    /// saat Autoplay, Repeat, atau isi antrean berubah.
    /// </summary>
    /// <param name="skip">Path lagu yang tidak boleh dipilih, misalnya lagu yang baru saja dihapus dari antrean.</param>
    public void TopUpAutoplay(IEnumerable<string>? skip = null)
    {
        if (!AutoplayEnabled || AutoplaySource is null || Current is not { } current || Repeat)
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
        exclude.UnionWith(Unplayable);
        if (skip is not null)
            exclude.UnionWith(skip);

        foreach (var track in AutoplaySource(current, exclude, missing))
            UpNext.Add(_createEntry(track, true));
    }

    /// <summary>
    /// Pindah ke lagu berikutnya di Playing Next (lagu selesai, atau tombol Next). Lagu yang file-nya tidak bisa dibuka
    /// (hilang atau rusak) dilewati. Percobaannya terbatas pada lagu di antrean saat ini, satu kali tambahan lagu Autoplay,
    /// dan satu putaran ulang untuk Repeat, supaya antrean yang semua file-nya hilang (misalnya drive-nya dicabut) tidak
    /// berputar terus. Played false berarti tidak ada lagi yang bisa diputar: pemanggil menghentikan pemutaran.
    /// </summary>
    public AdvanceResult Advance(TryPlayTrack tryPlay)
    {
        var skippedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Track? firstSkipped = null;
        string firstReason = "";
        bool keepHistory = true, toppedUp = false, restarted = false;

        while (true)
        {
            // Antrean habis tanpa sempat disiapkan lagu Autoplay (misalnya lagu terakhir dihapus dengan "Remove from Queue"),
            // atau semua lagu Autoplay-nya gagal dibuka: disiapkan sekali lagi, tanpa lagu yang gagal dibuka
            if (UpNext.Count == 0 && !Repeat && !toppedUp)
            {
                toppedUp = true;
                TopUpAutoplay();
            }

            if (UpNext.Count == 0)
            {
                if (!Repeat || Current is null || restarted)
                    return new AdvanceResult(false, skippedPaths.Count, firstSkipped, firstReason);

                restarted = true;
                RestartQueue();
                keepHistory = false; // lagu sekarang sudah ikut masuk antrean putaran ulang
                continue;
            }

            var next = UpNext[0];
            UpNext.RemoveAt(0);
            if (tryPlay(next.Track, keepHistory, out string error))
                return new AdvanceResult(true, skippedPaths.Count, firstSkipped, firstReason);

            // File-nya tidak bisa dibuka: dilewati, lagu sekarang dan riwayatnya tidak berubah
            skippedPaths.Add(next.Track.FilePath);
            if (firstSkipped is null)
            {
                firstSkipped = next.Track;
                firstReason = error;
            }
        }
    }

    /// <summary>
    /// Lagu yang akan dicoba lebih dulu oleh <see cref="Advance"/>, tanpa mengubah antrean, untuk disiapkan sebelum lagu
    /// sekarang habis (gapless). Null kalau Playing Next kosong: Advance lalu berhenti, menyiapkan lagu Autoplay, atau mengulang
    /// antrean (yang urutannya bisa diacak), jadi lagunya belum bisa ditebak.
    /// </summary>
    public Track? PeekNext() => UpNext.Count > 0 ? UpNext[0].Track : null;

    /// <summary>
    /// Tombol Previous: lagu terakhir di daftar Previous dikeluarkan untuk diputar (tanpa keepHistory), dan lagu sekarang
    /// kembali ke paling atas Playing Next. Null kalau tidak ada lagu sekarang atau lagu sebelumnya.
    /// </summary>
    public Track? StepBack()
    {
        if (Current is not { } current || _previous.Count == 0)
            return null;

        var previous = _previous[^1];
        _previous.RemoveAt(_previous.Count - 1);
        UpNext.Insert(0, _createEntry(current, false));
        return previous;
    }

    /// <summary>
    /// Lagu berhenti karena error di tengah file (rusak). True kalau sudah sebanyak lagu di antrean saat error pertama
    /// (Playing Next, Previous, dan lagu itu sendiri) yang gagal berturut-turut: pemutaran harus berhenti, supaya Repeat
    /// atau Autoplay dengan file yang semuanya rusak tidak berputar terus.
    /// </summary>
    public bool FailedMidSong()
    {
        if (_failedInARow++ == 0)
            _failedInARowLimit = UpNext.Count + _previous.Count + 1;
        return _failedInARow >= _failedInARowLimit;
    }

    /// <summary>Lagu selesai dengan normal, atau pengguna memilih lagu sendiri: hitungan lagu rusak dimulai dari nol.</summary>
    public void ResetFailures() => _failedInARow = 0;

    /// <summary>
    /// Mengganti data lagu di lagu sekarang, daftar Previous, dan Playing Next (termasuk urutan sebelum diacak), misalnya
    /// setelah tag-nya diedit atau foldernya dipindah. <paramref name="replacement"/> mengembalikan null untuk lagu yang
    /// tidak berubah. Setiap baris diganti sekali saja, walaupun ada di Playing Next dan di urutan aslinya: penggantian yang
    /// dijalankan dua kali bisa salah (folder baru yang berada di dalam folder lama). True kalau lagu sekarang ikut diganti.
    /// </summary>
    public bool ReplaceTracks(Func<Track, Track?> replacement)
    {
        for (int i = 0; i < _previous.Count; i++)
        {
            if (replacement(_previous[i]) is { } updated)
                _previous[i] = updated;
        }

        foreach (var entry in UpNext.Concat(_originalOrder).Distinct<TEntry>(ReferenceEqualityComparer.Instance))
        {
            if (replacement(entry.Track) is { } updated)
                entry.UpdateTrack(updated);
        }

        if (Current is not { } current || replacement(current) is not { } now)
            return false;
        ChangeCurrent(now);
        return true;
    }

    /// <summary>Isi antrean untuk disimpan saat aplikasi ditutup. Null kalau tidak ada lagu sekarang.</summary>
    public PlaybackSession? Snapshot(TimeSpan position)
    {
        if (Current is not { } current)
            return null;

        var original = new Dictionary<TEntry, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < _originalOrder.Count; i++)
            original.TryAdd(_originalOrder[i], i);

        return new PlaybackSession(
            current.FilePath,
            position,
            _previous.Select(t => t.FilePath).ToList(),
            UpNext.Select(e => new SessionEntry(e.Track.FilePath, e.IsAutoplay, original.TryGetValue(e, out int i) ? i : -1)).ToList());
    }

    /// <summary>
    /// Antrean dari sesi yang disimpan (lihat <see cref="Snapshot"/>), dengan lagu dari library. <paramref name="find"/>
    /// mengembalikan null untuk lagu yang sudah tidak ada di library; lagu itu dilewati. False kalau lagu sekarangnya
    /// sendiri sudah tidak ada: antrean tidak diubah. Shuffle, repeat, dan Autoplay tidak diubah.
    /// </summary>
    public bool Restore(PlaybackSession session, Func<string, Track?> find)
    {
        if (find(session.Current) is not { } current)
            return false;

        _previous.Clear();
        foreach (string path in session.Previous)
        {
            if (find(path) is { } track)
                _previous.Add(track);
        }

        var entries = new List<(TEntry Entry, int OriginalIndex)>();
        foreach (var item in session.UpNext)
        {
            if (find(item.Path) is { } track)
                entries.Add((_createEntry(track, item.IsAutoplay), item.OriginalIndex));
        }
        ReplaceUpNext(entries.Select(e => e.Entry).ToList());
        _originalOrder = entries.Where(e => e.OriginalIndex >= 0).OrderBy(e => e.OriginalIndex).Select(e => e.Entry).ToList();

        ChangeCurrent(current);
        return true;
    }

    // Seperti SetProperty: lagu yang isinya sama (Track adalah record) tidak menggantikan objek lamanya. PlaybackService
    // membandingkan objeknya untuk tahu apakah lagu sekarang berubah.
    private void ChangeCurrent(Track? track)
    {
        if (!Equals(Current, track))
            Current = track;
    }

    /// <summary>
    /// Repeat: antrean sudah habis, jadi semua lagu yang tadi diputar (ditambah lagu sekarang) masuk Playing Next lagi
    /// dari awal; Advance lalu memutar yang paling atas. Kalau shuffle aktif, urutannya diacak ulang.
    /// </summary>
    private void RestartQueue()
    {
        var entries = _previous.Append(Current!).Select(t => _createEntry(t, false)).ToList();
        _previous.Clear();

        if (IsShuffle)
        {
            _originalOrder = entries.ToList();
            entries = QueueOrder.Shuffled(entries, _random);
        }

        ReplaceUpNext(entries);
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

    private void ShuffleUpNext()
    {
        _originalOrder = UpNext.ToList();
        ReplaceUpNext(QueueOrder.Shuffled(_originalOrder, _random));
    }

    private void ReplaceUpNext(List<TEntry> entries)
    {
        UpNext.Clear();
        foreach (var entry in entries)
            UpNext.Add(entry);
    }
}
