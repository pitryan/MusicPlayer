using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MusicPlayer.Library;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MusicPlayer.Helpers;

/// <summary>
/// Impor dan ekspor playlist M3U/M3U8 lewat pemilih file Windows, untuk bertukar playlist dengan aplikasi lain
/// (lihat M3uPlaylist). Playlist yang diimpor menjadi playlist biasa, berisi lagu library yang ditemukan.
/// </summary>
public static class PlaylistFiles
{
    // Nama file lagu yang tidak ditemukan yang disebut di ringkasan impor; sisanya cukup jumlahnya
    private const int MissingShown = 5;

    private sealed record Result(string File, M3uImport? Import, string? Error, long? PlaylistId);

    /// <summary>
    /// Memilih satu atau beberapa file M3U/M3U8 lalu menjadikan masing-masing playlist. Kalau satu playlist diimpor dan semua
    /// lagunya ditemukan, playlist itu langsung dibuka; selain itu ringkasannya ditampilkan lebih dulu.
    /// </summary>
    public static async Task ImportAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".m3u8");
        picker.FileTypeFilter.Add(".m3u");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        var files = await picker.PickMultipleFilesAsync();
        if (files is not { Count: > 0 })
            return;

        // Membaca file dan mencocokkan ribuan path dengan library dikerjakan di luar UI thread
        var library = App.Database.GetTracks();
        var paths = files.Select(f => f.Path).ToList();
        var imports = await Task.Run(() => paths.Select(path => ReadPlaylist(path, library)).ToList());

        var results = imports.Select(r => r.Import is { Tracks.Count: > 0 } import
            ? r with { PlaylistId = App.Playlists.CreateWith(import.Name, import.Tracks) }
            : r).ToList();

        var created = results.Where(r => r.PlaylistId is not null).ToList();
        bool complete = results.Count == 1 && created.Count == 1 && created[0].Import!.Missing.Count == 0;
        if (!complete)
            await Dialogs.ShowMessageAsync(created.Count > 0 ? "Playlists Imported" : "Nothing Imported", Describe(results));
        if (created.Count == 1)
            App.MainWindow.ShowPlaylist(created[0].PlaylistId!.Value);
    }

    private static Result ReadPlaylist(string path, IReadOnlyList<Track> library)
    {
        try
        {
            return new Result(path, M3uPlaylist.Import(path, library), null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Result(path, null, ex.Message, null);
        }
    }

    private static string Describe(IReadOnlyList<Result> results)
    {
        var text = new StringBuilder();
        foreach (var result in results)
        {
            if (text.Length > 0)
                text.AppendLine();

            if (result.Import is not { } import)
            {
                text.AppendLine($"{Path.GetFileName(result.File)}: couldn't be read. {result.Error}");
                continue;
            }
            if (import.Tracks.Count == 0)
            {
                text.AppendLine(import.Missing.Count == 0
                    ? $"{import.Name}: the playlist has no songs, so it wasn't imported."
                    : $"{import.Name}: none of its songs are in your library, so it wasn't imported.");
                continue;
            }

            text.Append($"{import.Name}: {Formatters.Count(import.Tracks.Count, "song")} imported.");
            if (import.FoundElsewhere > 0)
            {
                text.Append(import.FoundElsewhere == 1
                    ? " 1 song was found in a different folder than the playlist says."
                    : $" {import.FoundElsewhere} songs were found in a different folder than the playlist says.");
            }
            text.AppendLine();

            if (import.Missing.Count > 0)
            {
                text.AppendLine(import.Missing.Count == 1
                    ? "1 song isn't in your library and was skipped:"
                    : $"{import.Missing.Count} songs aren't in your library and were skipped:");
                foreach (string missing in import.Missing.Take(MissingShown))
                    text.AppendLine($"   {Path.GetFileName(missing)}");
                if (import.Missing.Count > MissingShown)
                    text.AppendLine($"   and {import.Missing.Count - MissingShown} more");
            }
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// Menyimpan lagu-lagu ini sebagai file M3U8, atau M3U untuk pemutar portabel (banyak yang hanya mengenali ekstensi .m3u),
    /// di tempat yang dipilih pengguna. Isinya selalu UTF-8, supaya judul dan folder Jepang tetap terbaca. Path ditulis
    /// relatif terhadap folder tempat file disimpan kalau lagunya ada di sana (lihat M3uPlaylist.Export), jadi playlist yang
    /// disimpan di folder musik, atau di kartu SD yang berisi salinannya, tetap jalan di pemutar portabel.
    /// </summary>
    public static async Task ExportAsync(string name, IReadOnlyList<Track> tracks)
    {
        var picker = new FileSavePicker { SuggestedFileName = SafeFileName(name) };
        picker.FileTypeChoices.Add("M3U8 Playlist", [".m3u8"]);
        picker.FileTypeChoices.Add("M3U Playlist (portable players)", [".m3u"]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        if (await picker.PickSaveFileAsync() is not { } file)
            return;

        string folder = Path.GetDirectoryName(file.Path) ?? "";
        M3uExport export;
        try
        {
            // Mencari salinan lagu di folder tujuan (misalnya kartu SD yang lambat) dikerjakan di luar UI thread.
            // UTF-8 tanpa BOM, seperti yang diharapkan dari file .m3u8.
            export = await Task.Run(() => M3uPlaylist.Export(tracks, name, folder));
            await File.WriteAllTextAsync(file.Path, export.Text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowMessageAsync("Couldn't Export Playlist", ex.Message);
            return;
        }

        // Sebagian lagu ada di folder tujuan, sebagian tidak: biasanya kartu SD yang belum berisi semua lagunya. Kalau tidak ada
        // satu pun (misalnya disimpan di Documents untuk aplikasi lain di PC ini), path lengkap memang yang diharapkan.
        if (export.Relative > 0 && export.Absolute > 0)
        {
            await Dialogs.ShowMessageAsync("Playlist Exported",
                (export.Absolute == 1 ? "1 song wasn't" : $"{export.Absolute} songs weren't") +
                $" found in \"{folder}\", so the playlist uses their full path on this PC. A portable player won't be able to " +
                "play them until they're copied there, into the same folders as on this PC.");
        }
    }

    // "AC/DC: Best?" menjadi "AC_DC_ Best_", karena nama file tidak boleh memuat karakter itu
    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string safe = new(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return safe.Trim().Length > 0 ? safe.Trim() : "Playlist";
    }
}
