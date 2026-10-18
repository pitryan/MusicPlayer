using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MusicPlayer.Helpers;

/// <summary>
/// Dasar untuk kelas yang propertinya di-binding ke tampilan: setiap kali nilainya berubah,
/// tampilan otomatis diberi tahu lewat event PropertyChanged.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>Memberi tahu tampilan bahwa sebuah properti berubah, misalnya properti yang dihitung dari properti lain.</summary>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
