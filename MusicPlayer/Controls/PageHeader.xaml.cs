using Microsoft.UI.Xaml.Controls;

namespace MusicPlayer.Controls;

/// <summary>Kepala halaman daftar (lihat XAML-nya). Title dan Detail bisa diubah kapan saja dari halamannya.</summary>
public sealed partial class PageHeader : UserControl
{
    public PageHeader()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => TitleBlock.Text;
        set => TitleBlock.Text = value;
    }

    /// <summary>Keterangan kecil di kanan, misalnya "89 albums".</summary>
    public string Detail
    {
        get => DetailBlock.Text;
        set => DetailBlock.Text = value;
    }
}
