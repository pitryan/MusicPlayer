using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MusicPlayer.Helpers;

/// <summary>Dialog kecil yang dipakai bersama: nama playlist, konfirmasi hapus, dan lagu yang sudah ada di playlist.</summary>
public static class Dialogs
{
    /// <summary>Menanyakan nama (playlist baru atau ganti nama). Null kalau dibatalkan.</summary>
    public static async Task<string?> AskNameAsync(string title, string primaryButton, string initialName = "")
    {
        var box = new TextBox { Text = initialName, PlaceholderText = "Playlist name", MaxLength = 200 };
        var dialog = Create(title, box, primaryButton);
        dialog.IsPrimaryButtonEnabled = initialName.Trim().Length > 0;

        // Tombol utama baru aktif kalau namanya tidak kosong
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = box.Text.Trim().Length > 0;
        box.Loaded += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }

    /// <summary>Konfirmasi untuk tindakan yang tidak bisa dibatalkan, misalnya menghapus playlist.</summary>
    public static async Task<bool> ConfirmAsync(string title, string message, string primaryButton)
    {
        var dialog = Create(title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, primaryButton);
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>
    /// Sebagian lagu sudah ada di playlist: true = tetap tambahkan semuanya, false = lewati yang sudah ada (seperti
    /// Apple Music yang juga menanyakan ini).
    /// </summary>
    public static async Task<bool> AskAddDuplicatesAsync(int existing, int total, string playlistName)
    {
        string message = total == 1
            ? $"This song is already in \"{playlistName}\"."
            : $"{existing} of these {total} songs are already in \"{playlistName}\".";
        var dialog = Create("Already in Playlist", new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, "Add Anyway");
        dialog.CloseButtonText = total == 1 ? "Don't Add" : "Skip Them";
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static ContentDialog Create(string title, object content, string primaryButton) => new()
    {
        Title = title,
        Content = content,
        PrimaryButtonText = primaryButton,
        CloseButtonText = "Cancel",
        DefaultButton = ContentDialogButton.Primary,
        // Dialog WinUI 3 harus tahu jendela tempatnya tampil
        XamlRoot = App.MainWindow.Content?.XamlRoot,
    };
}
