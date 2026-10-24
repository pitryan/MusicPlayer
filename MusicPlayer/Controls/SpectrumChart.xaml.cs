using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using MusicPlayer.Helpers;
using MusicPlayer.Library;
using Windows.Foundation;

namespace MusicPlayer.Controls;

/// <summary>Grafik spektrum dari QualityReport (lihat XAML-nya).</summary>
public sealed partial class SpectrumChart : UserControl
{
    // Rentang tegak, relatif terhadap level tengah lagu (0 dB)
    private const double TopDb = 10;
    private const double BottomDb = -110;
    private const double CdLimitHz = 22050;

    public SpectrumChart()
    {
        InitializeComponent();
    }

    public double ChartWidth
    {
        get => ChartCanvas.Width;
        set
        {
            ChartCanvas.Width = value;
            LabelRow.Width = value;
        }
    }

    public double ChartHeight
    {
        get => ChartCanvas.Height;
        set
        {
            ChartCanvas.Height = value;
            ChartArea.Height = value;
        }
    }

    public void Show(QualityReport report)
    {
        double width = ChartCanvas.Width, height = ChartCanvas.Height, nyquist = report.SampleRate / 2.0;
        double X(double hz) => Math.Clamp(hz / nyquist, 0, 1) * width;
        double Y(double db) => Math.Clamp((TopDb - db) / (TopDb - BottomDb), 0, 1) * height;

        var points = new PointCollection();
        foreach (var point in report.Spectrum)
            points.Add(new Point(X(point.Hz), Y(point.Db)));
        SpectrumLine.Points = points;

        PlaceLine(CutoffLine, report.CutoffHz is { } cutoff ? X(cutoff) : null, height);
        // Batas CD hanya berarti di file hi-res: isi yang berhenti tepat di garis ini menandakan sumber 44,1 kHz
        PlaceLine(CdLimitLine, nyquist > 24000 ? X(CdLimitHz) : null, height);
        LegendText.Text = nyquist > 24000 ? "dashed: CD limit" : "";
        MaxText.Text = Formatters.Kilohertz(nyquist);
    }

    private static void PlaceLine(Line line, double? x, double height)
    {
        line.Visibility = x is null ? Visibility.Collapsed : Visibility.Visible;
        if (x is { } position)
        {
            line.X1 = position;
            line.X2 = position;
            line.Y1 = 0;
            line.Y2 = height;
        }
    }
}
