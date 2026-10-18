using System.Text;
using MusicPlayer.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

// UTF-16 supaya nama file berhuruf Jepang/Korea/Mandarin terbaca dengan benar
Console.InputEncoding = Encoding.Unicode;
Console.OutputEncoding = Encoding.Unicode;
Console.Title = "MusicPlayer AudioLab";

// ── 1. Pilih perangkat output ─────────────────────────────
using var enumerator = new MMDeviceEnumerator();
var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();

string defaultId;
using (var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
    defaultId = defaultDevice.ID;

Console.WriteLine("Perangkat output:");
for (int i = 0; i < devices.Count; i++)
{
    string mark = devices[i].ID == defaultId ? "  (default)" : "";
    Console.WriteLine($"  [{i}] {devices[i].FriendlyName}{mark}");
}

Console.Write("\nPilih nomor perangkat (Enter = default): ");
var device = int.TryParse(Console.ReadLine(), out int index) && index >= 0 && index < devices.Count
    ? devices[index]
    : devices.First(d => d.ID == defaultId);

// ── 2. Pilih file ─────────────────────────────────────────
Console.Write("Seret file MP3/FLAC ke jendela ini, lalu tekan Enter: ");
string path = (Console.ReadLine() ?? "").Trim().Trim('"');

if (!File.Exists(path))
{
    Console.WriteLine("File tidak ditemukan.");
    return;
}

// ── 3. Buka dan putar ─────────────────────────────────────
using var engine = new AudioEngine();
var finished = new ManualResetEventSlim();

engine.PlaybackStopped += (_, e) =>
{
    if (e.Exception is not null)
        Console.WriteLine($"\nPemutaran berhenti karena error: {e.Exception.Message}");
    finished.Set();
};

try
{
    engine.Open(path, device);
}
catch (Exception ex)
{
    Console.WriteLine($"Gagal membuka file: {ex.Message}");
    return;
}

Console.WriteLine();
Console.WriteLine($"Perangkat : {device.FriendlyName}");
Console.WriteLine($"Sumber    : {Describe(engine.SourceFormat!)}");
Console.WriteLine($"Output    : {engine.OutputDescription}");
Console.WriteLine($"Mode      : {(engine.IsExclusive ? "EXCLUSIVE (bit-perfect)" : "SHARED")}");
if (engine.FallbackReason is not null)
    Console.WriteLine($"Alasan    : {engine.FallbackReason}");
Console.WriteLine("\n[Spasi] pause/lanjut   [←] mundur 10 dtk   [→] maju 10 dtk   [Q] keluar\n");

engine.Play();

while (!finished.IsSet)
{
    while (Console.KeyAvailable)
    {
        switch (Console.ReadKey(intercept: true).Key)
        {
            case ConsoleKey.Spacebar: engine.TogglePause(); break;
            case ConsoleKey.LeftArrow: engine.Seek(engine.Position - TimeSpan.FromSeconds(10)); break;
            case ConsoleKey.RightArrow: engine.Seek(engine.Position + TimeSpan.FromSeconds(10)); break;
            case ConsoleKey.Q: finished.Set(); break;
        }
    }

    Console.Write($"\r{engine.State,-8} {Format(engine.Position)} / {Format(engine.Duration)}   ");
    Thread.Sleep(100);
}

Console.WriteLine("\nSelesai.");

static string Format(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";

static string Describe(WaveFormat f)
{
    string type = f.Encoding == WaveFormatEncoding.IeeeFloat ? "float" : "PCM";
    return $"{f.SampleRate / 1000.0:0.#} kHz / {f.BitsPerSample}-bit {type} / {f.Channels} ch";
}
