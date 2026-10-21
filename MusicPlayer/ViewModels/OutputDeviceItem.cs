namespace MusicPlayer.ViewModels;

/// <summary>Satu pilihan di daftar output pada popup kualitas.</summary>
public sealed class OutputDeviceItem
{
    public OutputDeviceItem(string? id, string name, string label)
    {
        Id = id;
        Name = name;
        Label = label;
    }

    /// <summary>ID perangkat dari Windows, atau null untuk pilihan "Windows default".</summary>
    public string? Id { get; }

    /// <summary>Nama perangkat. Disimpan bersama ID-nya, supaya pesan "tidak tersambung" tetap bisa menyebut namanya.</summary>
    public string Name { get; }

    /// <summary>Teks yang tampil di daftar.</summary>
    public string Label { get; }
}
