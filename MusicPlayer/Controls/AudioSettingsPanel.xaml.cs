using Microsoft.UI.Xaml.Controls;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;

namespace MusicPlayer.Controls;

/// <summary>Pilihan perangkat output dan exclusive mode. Dipakai di halaman Settings.</summary>
public sealed partial class AudioSettingsPanel : UserControl
{
    // True selama daftar output sedang diisi dari kode, bukan dipilih pengguna
    private bool _fillingOutputList;

    public AudioSettingsPanel()
    {
        InitializeComponent();

        // Daftar diisi ulang setiap kali panel ini tampil (setiap Settings dibuka), supaya DAC yang baru dicolok muncul
        Loaded += (_, _) =>
        {
            _fillingOutputList = true;
            OutputDeviceCombo.SelectedItem = Playback.RefreshOutputDevices();
            _fillingOutputList = false;
        };
    }

    public PlaybackService Playback { get; } = App.Playback;

    private void OutputDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingOutputList || OutputDeviceCombo.SelectedItem is not OutputDeviceItem item)
            return;

        Playback.SelectOutputDevice(item.Id, item.Name);
    }
}
