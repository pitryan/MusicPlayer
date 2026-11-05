using System;
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

        // Daftar diisi saat panel ini tampil, lalu setiap kali perangkat dicolok, dicabut, atau dinyalakan ulang (misalnya DAC
        // yang restart sendiri) selama panel ini masih tampil. OutputDevicesChanged dilepas lagi saat panel ditutup, supaya
        // panelnya tidak tertahan di memori.
        Loaded += (_, _) =>
        {
            FillOutputList();
            Playback.OutputDevicesChanged += OnOutputDevicesChanged;
        };
        Unloaded += (_, _) => Playback.OutputDevicesChanged -= OnOutputDevicesChanged;
    }

    public PlaybackService Playback { get; } = App.Playback;

    private void OnOutputDevicesChanged(object? sender, EventArgs e) => FillOutputList();

    private void FillOutputList()
    {
        _fillingOutputList = true;
        OutputDeviceCombo.SelectedItem = Playback.RefreshOutputDevices();
        _fillingOutputList = false;
    }

    private void OutputDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingOutputList || OutputDeviceCombo.SelectedItem is not OutputDeviceItem item)
            return;

        Playback.SelectOutputDevice(item.Id, item.Name);
    }
}
