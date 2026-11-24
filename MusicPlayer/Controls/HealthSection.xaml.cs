using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MusicPlayer.ViewModels;

namespace MusicPlayer.Controls;

/// <summary>Satu bagian di halaman Library Health. Tombolnya hanya membersihkan file yang masih tercentang.</summary>
public sealed partial class HealthSection : UserControl
{
    private bool _busy;

    public HealthSection()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }

    public string Description
    {
        get => DescriptionText.Text;
        set => DescriptionText.Text = value;
    }

    public string ActionText
    {
        get => ActionButton.Content as string ?? "";
        set => ActionButton.Content = value;
    }

    /// <summary>Tombol diklik; file yang dipilih ada di <see cref="SelectedRows"/>.</summary>
    public event EventHandler? ActionClick;

    public IReadOnlyList<HealthRow> SelectedRows => FileList.SelectedItems.OfType<HealthRow>().ToList();

    /// <summary>Selama pembersihan (di bagian mana pun), semua tombol dimatikan.</summary>
    public bool IsBusy
    {
        get => _busy;
        set
        {
            _busy = value;
            UpdateButton();
        }
    }

    /// <summary>Hasil scan: semua file langsung tercentang. Daftar kosong menampilkan <paramref name="emptyText"/>.</summary>
    public void Show(IReadOnlyList<HealthRow> rows, string summary, string emptyText)
    {
        FileList.ItemsSource = rows;
        FileList.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SummaryText.Text = rows.Count > 0 ? summary : emptyText;
        if (rows.Count > 0)
            FileList.SelectAll();
        UpdateButton();
    }

    /// <summary>Kosong menyembunyikan barisnya.</summary>
    public void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateButton() => ActionButton.IsEnabled = !_busy && FileList.SelectedItems.Count > 0;

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButton();

    private void Action_Click(object sender, RoutedEventArgs e) => ActionClick?.Invoke(this, EventArgs.Empty);
}
