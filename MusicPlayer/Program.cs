using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace MusicPlayer;

/// <summary>
/// Titik masuk aplikasi, menggantikan Main buatan XAML (DISABLE_XAML_GENERATED_MAIN di .csproj). Aplikasi hanya boleh
/// terbuka sekali: dua jendela akan berebut DAC dalam exclusive mode dan menulis ke database yang sama. Kalau aplikasi
/// sudah berjalan, peluncuran baru dialihkan ke sana (jendelanya dimunculkan), lalu proses baru langsung keluar.
/// </summary>
public static class Program
{
    // Cukup lama untuk aplikasi yang sedang sibuk; kalau lewat, proses baru tetap keluar tanpa membuka jendela kedua
    private const uint RedirectTimeoutMs = 10_000;

    [STAThread]
    private static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (RedirectedToRunningInstance())
            return;

        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }

    private static bool RedirectedToRunningInstance()
    {
        AppInstance main = AppInstance.FindOrRegisterForKey("main");
        if (main.IsCurrent)
        {
            // Dipicu di thread lain setiap kali aplikasi dibuka lagi, termasuk dengan file dari Explorer
            main.Activated += (_, args) => App.OnActivatedAgain(args);
            return false;
        }

        // Proses yang sedang di depan (yang ini) mengizinkan aplikasi yang sudah berjalan memunculkan jendelanya di depan
        AllowSetForegroundWindow(main.ProcessId);

        // RedirectActivationToAsync tidak boleh ditunggu dengan memblokir thread STA ini, jadi ditunggu di thread lain
        // sambil pesan COM tetap diproses (seperti contoh single-instance Windows App SDK)
        AppActivationArguments args = AppInstance.GetCurrent().GetActivatedEventArgs();
        var done = new ManualResetEvent(false); // tidak di-dispose: kalau waktunya habis, thread lain masih memakainya
        _ = Task.Run(() =>
        {
            try
            {
                main.RedirectActivationToAsync(args).AsTask().Wait();
            }
            catch (Exception)
            {
                // Aplikasi yang sudah berjalan sedang menutup: proses ini tetap keluar, pengguna bisa membukanya lagi
            }
            done.Set();
        });
        _ = CoWaitForMultipleObjects(0, RedirectTimeoutMs, 1, [done.SafeWaitHandle.DangerousGetHandle()], out _);
        GC.KeepAlive(done); // handle-nya dipakai sebagai angka biasa selama menunggu, jadi objeknya dijaga tetap hidup
        return true;
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("ole32.dll")]
    private static extern int CoWaitForMultipleObjects(uint flags, uint timeoutMs, uint handleCount, IntPtr[] handles, out uint index);
}
