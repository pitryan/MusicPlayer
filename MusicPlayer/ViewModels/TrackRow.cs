using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using MusicPlayer.Helpers;
using MusicPlayer.Library;
using MusicPlayer.Services;

namespace MusicPlayer.ViewModels;

/// <summary>
/// Satu baris lagu (halaman album, folder, artis, Songs, dan playlist). Saat lagu ini sedang diputar, nomor track-nya
/// diganti ikon play merah, dan lagu favorit diberi bintang merah di kiri, seperti di Apple Music.
/// </summary>
public sealed class TrackRow : ObservableObject
{
    // Baris ke-1, 3, 5, ... sedikit lebih terang, supaya baris mudah diikuti mata
    private static readonly Brush StripeBrush = new SolidColorBrush(ColorHelper.FromArgb(0x0D, 0xFF, 0xFF, 0xFF));
    private static readonly Brush ClearBrush = new SolidColorBrush(Colors.Transparent);

    // Cover kecil di baris playlist (40 px), dimuat dua kali lebih besar supaya tetap tajam di layar HiDPI
    private const int CoverPixelWidth = 80;

    private bool _isPlaying;
    private bool _isFavourite;
    private ImageSource? _cover;
    private bool _coverRequested;
    private Brush _rowBackground;

    /// <param name="albumArtist">
    /// Diisi halaman album: artis lagu ditampilkan di bawah judul hanya kalau berbeda dari artis albumnya, seperti
    /// Apple Music (misalnya lagu "feat." atau kompilasi).
    /// </param>
    public TrackRow(Track track, int index, string? albumArtist = null)
    {
        Track = track;
        _rowBackground = StripeFor(index);
        NumberText = Formatters.TrackNumber(track.TrackNumber);
        ArtistText = track.DisplayArtist;
        ArtistUnderTitle = albumArtist is not null
            && !string.Equals(track.DisplayArtist, albumArtist, System.StringComparison.OrdinalIgnoreCase)
            ? track.DisplayArtist
            : "";
        DurationText = Formatters.Time(track.Duration);
        _isFavourite = App.Favourites.IsFavourite(track);
    }

    public Track Track { get; }

    public string Title => Track.DisplayTitle;

    public string NumberText { get; }

    /// <summary>
    /// Artis lagu ini. Selalu ditampilkan, supaya album kompilasi (Various Artists)
    /// tetap menunjukkan siapa penyanyi setiap lagunya.
    /// </summary>
    public string ArtistText { get; }

    /// <summary>Artis di bawah judul (halaman album); kosong kalau sama dengan artis album.</summary>
    public string ArtistUnderTitle { get; }

    public Visibility ArtistUnderTitleVisibility => ArtistUnderTitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string DurationText { get; }

    public string AlbumText => Track.DisplayAlbum;

    /// <summary>
    /// Latar belang dipasang di dalam template baris, bukan di kotak ListViewItem-nya.
    /// Latar kotak ListViewItem ikut diubah oleh efek hover, sehingga belangnya bisa hilang.
    /// </summary>
    public Brush RowBackground
    {
        get => _rowBackground;
        private set => SetProperty(ref _rowBackground, value);
    }

    /// <summary>Id baris ini di playlist (lagu yang sama bisa muncul dua kali); null di luar halaman playlist.</summary>
    public long? PlaylistItemId { get; init; }

    /// <summary>Menyamakan warna belang dengan posisi baru, misalnya setelah lagu di playlist diseret.</summary>
    public void SetIndex(int index) => RowBackground = StripeFor(index);

    private static Brush StripeFor(int index) => index % 2 == 0 ? StripeBrush : ClearBrush;

    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (SetProperty(ref _isPlaying, value))
            {
                OnPropertyChanged(nameof(NumberVisibility));
                OnPropertyChanged(nameof(PlayingVisibility));
            }
        }
    }

    public Visibility NumberVisibility => IsPlaying ? Visibility.Collapsed : Visibility.Visible;

    public Visibility PlayingVisibility => IsPlaying ? Visibility.Visible : Visibility.Collapsed;

    public bool IsFavourite
    {
        get => _isFavourite;
        set
        {
            if (SetProperty(ref _isFavourite, value))
                OnPropertyChanged(nameof(FavouriteVisibility));
        }
    }

    public Visibility FavouriteVisibility => IsFavourite ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Cover kecil untuk baris playlist; baru dimuat saat barisnya akan tampil (lihat EnsureCover).</summary>
    public ImageSource? Cover
    {
        get => _cover;
        private set => SetProperty(ref _cover, value);
    }

    public async void EnsureCover()
    {
        if (_coverRequested)
            return;

        _coverRequested = true;
        Cover = await CoverCache.GetAsync(Track.FilePath, CoverPixelWidth);
    }

    /// <summary>
    /// Menyamakan bintang semua baris dengan daftar favorit sekarang. Dipanggil halaman saat favorit berubah dari mana
    /// pun; ribuan baris pun cepat, karena statusnya hanya dicek di memori.
    /// </summary>
    public static void RefreshFavourites(IEnumerable<TrackRow> rows)
    {
        foreach (var row in rows)
            row.IsFavourite = App.Favourites.IsFavourite(row.Track);
    }
}
