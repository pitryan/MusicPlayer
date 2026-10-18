using System;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MusicPlayer.Pages;
using MusicPlayer.Services;

namespace MusicPlayer;

public sealed partial class MainWindow : Window
{
    /// <summary>Dipakai oleh x:Bind di MainWindow.xaml.</summary>
    public PlaybackService Playback { get; } = App.Playback;

    public MainWindow()
    {
        InitializeComponent();

        // Bar pemutar menyatu dengan title bar, seperti Apple Music
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = Colors.White;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 900));

        // Buka halaman pertama (Songs)
        NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>().First();

        Closed += (_, _) => Playback.Dispose();
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        Type page = args.IsSettingsSelected
            ? typeof(SettingsPage)
            : (args.SelectedItem as NavigationViewItem)?.Tag switch
            {
                "songs" => typeof(SongsPage),
                _ => typeof(SongsPage),
            };

        if (ContentFrame.CurrentSourcePageType != page)
            ContentFrame.Navigate(page);
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => Playback.Previous();

    private void PlayPause_Click(object sender, RoutedEventArgs e) => Playback.TogglePlayPause();

    private void Next_Click(object sender, RoutedEventArgs e) => Playback.Next();

    private void SeekBar_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Klik di garis progres: lompat ke posisi itu
        var bar = (FrameworkElement)sender;
        if (bar.ActualWidth > 0)
            Playback.SeekToFraction(e.GetCurrentPoint(bar).Position.X / bar.ActualWidth);
    }
}
