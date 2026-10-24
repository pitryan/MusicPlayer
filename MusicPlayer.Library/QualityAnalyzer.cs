using System.Numerics;

namespace MusicPlayer.Library;

/// <summary>Kesimpulan tentang sumber sebuah file, dari isinya (bukan dari format wadahnya).</summary>
public enum SourceVerdict
{
    /// <summary>Terlalu pendek atau terlalu sunyi untuk dinilai.</summary>
    Unknown,

    /// <summary>Isi frekuensinya penuh sampai batas format: lossless asli (atau hi-res asli untuk wadah di atas 48 kHz).</summary>
    Genuine,

    /// <summary>Wadah hi-res, tapi isinya terpotong di sekitar 22 kHz (sumber 44,1 kHz) atau 24 kHz (sumber 48 kHz).</summary>
    Upsampled,

    /// <summary>Terpotong tajam di 14–20 kHz, ciri khas encoder MP3/AAC.</summary>
    LikelyLossy,

    /// <summary>Terpotong di bawah 14 kHz: rekaman lama, atau sumber lossy berbitrate rendah.</summary>
    LimitedBandwidth,
}

/// <summary>Satu titik grafik spektrum: frekuensi dan level dalam dB relatif terhadap level tengah lagu (1–8 kHz = 0 dB).</summary>
public sealed record SpectrumPoint(double Hz, double Db);

public sealed record QualityReport(
    int SampleRate,
    int ContainerBits,
    bool IsFloat,
    int? EffectiveBits,
    double? CutoffHz,
    SourceVerdict Verdict,
    int? LikelySourceRate,
    IReadOnlyList<SpectrumPoint> Spectrum)
{
    /// <summary>Wadah 24-bit (atau lebih) yang hanya memakai 16 bit: 8 bit bawahnya selalu nol.</summary>
    public bool BitDepthPadded => !IsFloat && ContainerBits >= 24 && EffectiveBits is <= 16;
}

/// <summary>
/// Menganalisis isi audio seperti Spek atau Lossless Audio Checker. Dua pemeriksaan:
/// (1) bit yang benar-benar terpakai: semua sampel digabung dengan OR, bit bawah yang selalu nol berarti tambalan;
/// (2) spektrum rata-rata (FFT berjendela Hann) untuk menemukan potongan tajam ("brickwall") tempat isi lagu berhenti.
/// Hasil (2) adalah dugaan kuat, bukan kepastian: master yang memang terpotong bisa terlihat sama, jadi spektrumnya
/// ikut dilaporkan supaya bisa dinilai sendiri.
/// </summary>
public sealed class QualityAnalyzer
{
    /// <summary>
    /// Versi algoritma. Naikkan setiap kali aturan atau ambangnya berubah: hasil lama di database jadi tidak berlaku
    /// dan lagunya diperiksa ulang.
    /// </summary>
    public const int Version = 2; // 2: penghalusan tanpa jumlah berjalan (NaN), batas sumber dari ujung isi

    // Bagian atas spektrum yang tidak sejauh ini di bawah level tengah lagu berarti masih berisi: isinya penuh
    private const double FullBandBelowReferenceDb = 40;

    // Isi lagu dianggap berakhir di frekuensi terakhir yang masih setinggi ini di atas lantai noise. Lantai noise
    // diukur per file, karena file 16-bit (misalnya MP3 yang disimpan ulang sebagai FLAC) punya noise kuantisasi
    // di seluruh frekuensi, dan lagu yang pelan lebih dekat ke lantainya.
    private const double AboveFloorDb = 20;

    // Tepi buatan (filter encoder atau resampler): turun minimal sebanyak ini dalam 2 kHz di sekitar ujung isi.
    // Peluruhan alami frekuensi tinggi pada rekaman sungguhan jauh lebih landai.
    private const double EdgeDropDb = 25;


    // Lebar pita untuk mencari ujung isi; median per pita, supaya nada sempit (dengung, pilot tone) tidak ikut
    private const double BandHz = 200;

    // Lagu yang level tengahnya serendah ini dianggap sunyi
    private const double SilenceDbfs = -110;

    private const int SpectrumPoints = 300;

    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly int _containerBits;
    private readonly bool _isFloat;
    private readonly int _fftSize;
    private readonly double[] _window;
    private readonly double[] _power;
    private int _windows;
    private uint _orBits;
    private bool _anyNonZero;

    public QualityAnalyzer(int sampleRate, int channels, int containerBits, bool isFloat)
    {
        _sampleRate = sampleRate;
        _channels = Math.Max(1, channels);
        _containerBits = containerBits;
        _isFloat = isFloat;

        // Resolusi frekuensi sekitar 5–6 Hz di semua sample rate
        _fftSize = sampleRate <= 48000 ? 8192 : sampleRate <= 96000 ? 16384 : 32768;
        _window = new double[_fftSize];
        for (int i = 0; i < _fftSize; i++)
            _window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (_fftSize - 1));
        _power = new double[_fftSize / 2 + 1];
    }

    /// <summary>
    /// Titik-titik awal potongan yang dianalisis: tersebar di antara 10% dan 90% durasi (intro dan outro sering sunyi).
    /// Beberapa potongan pendek sudah cukup mewakili satu lagu, dan jauh lebih cepat daripada membaca seluruhnya.
    /// </summary>
    public static IReadOnlyList<TimeSpan> SegmentStarts(TimeSpan duration, TimeSpan segmentLength, int count = 6)
    {
        if (duration <= segmentLength * 2)
            return [TimeSpan.Zero];

        var first = duration * 0.1;
        var last = duration * 0.9 - segmentLength;
        if (last <= first)
            return [TimeSpan.Zero];

        return Enumerable.Range(0, count)
            .Select(i => first + (last - first) * (count == 1 ? 0 : (double)i / (count - 1)))
            .ToList();
    }

    /// <summary>Satu potongan sampel integer berselang-seling antar kanal, dalam skala wadahnya.</summary>
    public void AddSegment(ReadOnlySpan<int> interleaved)
    {
        double scale = 1.0 / (1L << (Math.Max(_containerBits, 8) - 1));
        var mono = new float[interleaved.Length / _channels];
        for (int frame = 0; frame < mono.Length; frame++)
        {
            double sum = 0;
            for (int c = 0; c < _channels; c++)
            {
                int sample = interleaved[frame * _channels + c];
                _orBits |= (uint)sample;
                sum += sample;
            }
            mono[frame] = (float)(sum / _channels * scale);
        }
        if (_orBits != 0)
            _anyNonZero = true;
        AddMono(mono);
    }

    /// <summary>Satu potongan sampel float (-1..1) berselang-seling antar kanal.</summary>
    public void AddSegment(ReadOnlySpan<float> interleaved)
    {
        var mono = new float[interleaved.Length / _channels];
        for (int frame = 0; frame < mono.Length; frame++)
        {
            double sum = 0;
            for (int c = 0; c < _channels; c++)
                sum += interleaved[frame * _channels + c];
            mono[frame] = (float)(sum / _channels);
            if (mono[frame] != 0)
                _anyNonZero = true;
        }
        AddMono(mono);
    }

    // Setiap potongan dianalisis sendiri (jendela 50% tumpang tindih), tanpa menyambung ke potongan berikutnya
    private void AddMono(float[] mono)
    {
        var re = new double[_fftSize];
        var im = new double[_fftSize];
        for (int start = 0; start + _fftSize <= mono.Length; start += _fftSize / 2)
        {
            for (int i = 0; i < _fftSize; i++)
            {
                re[i] = mono[start + i] * _window[i];
                im[i] = 0;
            }
            Fft(re, im);
            for (int k = 0; k < _power.Length; k++)
                _power[k] += re[k] * re[k] + im[k] * im[k];
            _windows++;
        }
    }

    public QualityReport Finish()
    {
        int? effectiveBits = _isFloat || _orBits == 0 ? null : _containerBits - Math.Min(BitOperations.TrailingZeroCount(_orBits), _containerBits);
        if (_windows == 0)
            return new QualityReport(_sampleRate, _containerBits, _isFloat, effectiveBits, null, SourceVerdict.Unknown, null, []);

        double binHz = (double)_sampleRate / _fftSize;
        double nyquist = _sampleRate / 2.0;

        // Level per bin dalam dBFS (dinormalkan terhadap jendela Hann), dihaluskan sekitar ±50 Hz. Dihitung langsung
        // per titik: jumlah berjalan (tambah-kurang) kehilangan presisi di bagian atas yang nyaris kosong, karena sisa
        // pembulatan dari frekuensi rendah yang jauh lebih keras bisa membuatnya negatif (log-nya NaN).
        double norm = 1.0 / (_windows * Math.Pow(_window.Sum(), 2) / 4);
        int half = Math.Max(1, (int)Math.Round(50 / binHz));
        var smoothed = new double[_power.Length];
        for (int center = 0; center < _power.Length; center++)
        {
            int a = Math.Max(0, center - half), b = Math.Min(_power.Length - 1, center + half);
            double sum = 0;
            for (int k = a; k <= b; k++)
                sum += _power[k];
            smoothed[center] = 10 * Math.Log10(Math.Max(sum / (b - a + 1) * norm, 1e-30));
        }

        // Level tengah lagu: median 1–8 kHz, bagian spektrum yang selalu berisi di musik apa pun
        var mid = smoothed.Skip((int)(1000 / binHz)).Take((int)(7000 / binHz)).OrderBy(x => x).ToList();
        double reference = mid.Count > 0 ? mid[mid.Count / 2] : -200;

        var spectrum = BuildSpectrum(smoothed, binHz, nyquist, reference);
        if (!_anyNonZero || reference < SilenceDbfs)
            return new QualityReport(_sampleRate, _containerBits, _isFloat, effectiveBits, null, SourceVerdict.Unknown, null, spectrum);

        double? cutoff = FindEdge(smoothed, binHz, nyquist, reference);

        var (verdict, sourceRate) = Classify(cutoff, nyquist);
        return new QualityReport(_sampleRate, _containerBits, _isFloat, effectiveBits, cutoff is { } x ? Math.Round(x / 100) * 100 : null,
            verdict, sourceRate, spectrum);
    }

    /// <summary>
    /// Mencari tepi buatan tempat isi lagu berhenti (filter encoder atau resampler) dan mengembalikan ujung isinya,
    /// yaitu perkiraan batas sumber. Null kalau isinya penuh sampai batas format, atau meluruh alami.
    /// </summary>
    private static double? FindEdge(double[] smoothed, double binHz, double nyquist, double reference)
    {
        int bandCount = (int)(nyquist / BandHz);
        var bands = new double[bandCount];
        for (int j = 0; j < bandCount; j++)
        {
            int a = (int)(j * BandHz / binHz), b = Math.Min(smoothed.Length - 1, (int)((j + 1) * BandHz / binHz));
            var values = smoothed[a..(b + 1)];
            System.Array.Sort(values);
            bands[j] = values[values.Length / 2];
        }
        double At(double hz) => bands[Math.Clamp((int)(hz / BandHz), 0, bandCount - 1)];
        double MeanBands(double fromHz, double toHz)
        {
            int a = Math.Clamp((int)(fromHz / BandHz), 0, bandCount - 1), b = Math.Clamp((int)(toHz / BandHz), a, bandCount - 1);
            double sum = 0;
            for (int j = a; j <= b; j++)
                sum += bands[j];
            return sum / (b - a + 1);
        }

        // 1. Lantai noise: persentil ke-25 di 10% teratas spektrum (setengah kilohertz terakhir diabaikan, karena
        //    filter ADC di dekat batas format itu wajar). Kalau bagian atas masih berisi, isinya penuh.
        int top = bandCount - 1 - (int)Math.Ceiling(500 / BandHz);
        if (top < 10)
            return null;
        var upper = bands[(int)(top * 0.9)..(top + 1)].OrderBy(x => x).ToList();
        double floor = upper[upper.Count / 4];
        if (floor >= reference - FullBandBelowReferenceDb)
            return null;

        // 2. Tepi, dicari dari bawah (mulai 12 kHz): titik pertama tempat level jatuh curam ke dekat lantai noise.
        //    Dicari dari bawah, bukan dari atas, karena resampler yang landai membocorkan bayangan (image) di atas
        //    tepinya; misalnya MP3 yang di-upsample punya celah kosong di atas 20 kHz, lalu bayangan di 24–26 kHz.
        double topHz = (top + 1) * BandHz;
        double? end = null;
        for (double f = 12000; f + 1200 <= topHz; f += BandHz)
        {
            double next = MeanBands(f + 200, f + 1200);
            if (next > floor + AboveFloorDb)
                continue; // masih ada isi tepat di atasnya
            double before = At(f - 1500);
            if (before - next < EdgeDropDb)
                continue; // turunnya landai: peluruhan alami, bukan filter
            if (MeanBands(f + 1200, Math.Min(topHz, f + 5000)) > before - EdgeDropDb)
                continue; // isinya kembali ke level musik: celah sempit, bukan tepi
            end = f + BandHz;
            break;
        }
        if (end is null)
            return null;

        // Ujung isi inilah perkiraan batas sumbernya. (Bukan "lutut" tempat level mulai turun: musik sungguhan sudah
        // meluruh alami sebelum tepi filter, jadi lututnya bisa jauh di bawah batas sumber.)
        return end;
    }

    // Sample rate sumber yang umum dan batas frekuensinya (setengahnya)
    private static readonly int[] SourceRates = [44100, 48000, 88200, 96000];

    /// <summary>
    /// Penggolongan dari ujung isi. Upsampling dikenali kalau ujung isi cocok dengan setengah sample rate yang lebih
    /// rendah dari wadahnya, dengan salah satu dari dua pola filter: tegak (resampler studio, berhenti tepat di
    /// batasnya) atau landai (bocor 12–22% di atas batasnya). Selain itu: 14 kHz sampai 95% batas format khas MP3/AAC;
    /// di bawah 14 kHz bandwidth terbatas.
    /// </summary>
    private (SourceVerdict, int?) Classify(double? cutoff, double nyquist)
    {
        if (cutoff is not { } c)
            return (SourceVerdict.Genuine, null);
        if (c < 14000)
            return (SourceVerdict.LimitedBandwidth, null);

        int? best = null;
        double bestScore = double.MaxValue;
        foreach (int rate in SourceRates.Where(r => r < _sampleRate))
        {
            double sourceNyquist = rate / 2.0, offset = (c - sourceNyquist) / sourceNyquist;
            // Pola tegak diutamakan (skor lebih kecil) daripada pola landai
            double score = offset is >= -0.04 and <= 0.03 ? Math.Abs(offset)
                : offset is >= 0.12 and <= 0.22 ? 1 + Math.Abs(offset - 0.17)
                : double.MaxValue;
            if (score < bestScore)
            {
                bestScore = score;
                best = rate;
            }
        }
        if (best is not null)
            return (SourceVerdict.Upsampled, best);

        if (c >= nyquist * 0.95)
            return (SourceVerdict.Genuine, null);
        return nyquist <= 24000 || c < 20850 ? (SourceVerdict.LikelyLossy, null) : (SourceVerdict.Genuine, null);
    }

    private static List<SpectrumPoint> BuildSpectrum(double[] smoothed, double binHz, double nyquist, double reference)
    {
        var points = new List<SpectrumPoint>(SpectrumPoints);
        for (int p = 0; p < SpectrumPoints; p++)
        {
            int a = (int)(p * nyquist / SpectrumPoints / binHz);
            int b = Math.Max(a, Math.Min(smoothed.Length - 1, (int)((p + 1) * nyquist / SpectrumPoints / binHz)));
            double peak = double.MinValue;
            for (int k = a; k <= b; k++)
                peak = Math.Max(peak, smoothed[k]);
            double level = double.IsFinite(peak) ? Math.Clamp(peak - reference, -150, 30) : -150;
            points.Add(new SpectrumPoint((a + b) / 2.0 * binHz, level));
        }
        return points;
    }

    /// <summary>FFT radix-2 di tempat (Cooley–Tukey iteratif). Ukurannya selalu pangkat dua.</summary>
    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int length = 2; length <= n; length <<= 1)
        {
            double angle = -2 * Math.PI / length;
            double wRe = Math.Cos(angle), wIm = Math.Sin(angle);
            for (int start = 0; start < n; start += length)
            {
                double curRe = 1, curIm = 0;
                for (int k = 0; k < length / 2; k++)
                {
                    int a = start + k, b = a + length / 2;
                    double tRe = re[b] * curRe - im[b] * curIm;
                    double tIm = re[b] * curIm + im[b] * curRe;
                    re[b] = re[a] - tRe;
                    im[b] = im[a] - tIm;
                    re[a] += tRe;
                    im[a] += tIm;
                    double nextRe = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = nextRe;
                }
            }
        }
    }
}
