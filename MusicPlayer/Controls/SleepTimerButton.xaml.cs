using System;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MusicPlayer.Library;
using MusicPlayer.Services;

namespace MusicPlayer.Controls;

/// <summary>Tombol dan menu sleep timer (lihat <see cref="SleepTimer"/>).</summary>
public sealed partial class SleepTimerButton : UserControl
{
    private readonly SleepTimer _timer = App.Playback.SleepTimer;

    // Durasi yang terakhir dipilih, supaya pilihannya tercentang selama timer berjalan
    private int _minutes;

    public SleepTimerButton()
    {
        InitializeComponent();
        // Hanya mendengarkan selama tampil, seperti QualityBadge
        Loaded += (_, _) =>
        {
            _timer.PropertyChanged += Timer_PropertyChanged;
            ShowState();
        };
        Unloaded += (_, _) => _timer.PropertyChanged -= Timer_PropertyChanged;
    }

    private void Timer_PropertyChanged(object? sender, PropertyChangedEventArgs e) => ShowState();

    private void ShowState()
    {
        bool active = _timer.IsActive;
        if (active)
            MoonIcon.Foreground = (Brush)Application.Current.Resources["AccentRedBrush"];
        else
            MoonIcon.ClearValue(IconElement.ForegroundProperty);

        bool timed = _timer.Mode == SleepTimerMode.Timed;
        RemainingText.Text = timed ? SleepTimer.FormatRemaining(_timer.Remaining) : "";
        RemainingText.Visibility = timed ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(TimerButton, active ? $"Sleep Timer: {_timer.Description}" : "Sleep Timer");
    }

    /// <summary>Pilihan yang sedang berlaku tercentang; sisa waktunya di baris paling atas.</summary>
    private void TimerMenu_Opening(object? sender, object e)
    {
        bool active = _timer.IsActive;
        StatusItem.Text = _timer.Description;
        StatusItem.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        StatusSeparator.Visibility = StatusItem.Visibility;

        foreach (var item in TimerMenu.Items.OfType<RadioMenuFlyoutItem>())
        {
            item.IsChecked = _timer.Mode switch
            {
                SleepTimerMode.Timed => item.Tag is string tag && tag == _minutes.ToString(),
                SleepTimerMode.EndOfSong => item == SongItem,
                SleepTimerMode.EndOfAlbum => item == AlbumItem,
                _ => item == OffItem,
            };
        }
    }

    private void Off_Click(object sender, RoutedEventArgs e) => _timer.Cancel();

    private void Minutes_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag && int.TryParse(tag, out int minutes))
        {
            _minutes = minutes;
            _timer.StartTimed(TimeSpan.FromMinutes(minutes));
        }
    }

    private void Song_Click(object sender, RoutedEventArgs e) => _timer.StopAfterSong();

    private void Album_Click(object sender, RoutedEventArgs e) => _timer.StopAfterAlbum();
}
