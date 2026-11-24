namespace MusicPlayer.ViewModels;

/// <summary>
/// Satu file di halaman Library Health: namanya (atau "nama lama → nama baru"), foldernya, dan keterangan di kanan
/// (ukuran atau apa yang akan dilakukan). <see cref="Item"/> adalah data dari LibraryHealth.Scan.
/// </summary>
public sealed class HealthRow
{
    public HealthRow(object item, string title, string folder, string detail)
    {
        Item = item;
        Title = title;
        Folder = folder;
        Detail = detail;
    }

    public object Item { get; }

    public string Title { get; }

    public string Folder { get; }

    public string Detail { get; }
}
