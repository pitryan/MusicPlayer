using System;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace MusicPlayer.Helpers;

/// <summary>
/// Membaca pesan Win32 yang dikirim Windows ke jendela WinUI, misalnya klik tombol di thumbnail taskbar atau ikon di
/// notification area. WinUI tidak menyediakan WndProc, jadi jendelanya di-subclass (SetWindowSubclass). Beberapa
/// subclass boleh dipasang di jendela yang sama.
/// </summary>
internal sealed class WindowSubclass : IDisposable
{
    /// <summary>Hasil untuk pesan yang sudah ditangani, atau null supaya pesan itu diteruskan seperti biasa.</summary>
    public delegate IntPtr? Handler(uint message, IntPtr wParam, IntPtr lParam);

    private const uint WM_NCDESTROY = 0x0082;
    private static nuint _lastId;

    private readonly IntPtr _hwnd;
    private readonly nuint _id;
    private readonly Handler _handler;
    // Disimpan di field supaya delegate-nya tidak dibuang GC selama Windows masih memanggilnya
    private readonly SubclassProc _proc;
    private readonly DispatcherQueue _dispatcher;
    private bool _removed;

    public WindowSubclass(IntPtr hwnd, Handler handler)
    {
        _hwnd = hwnd;
        _handler = handler;
        _id = ++_lastId;
        _proc = Proc;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        SetWindowSubclass(hwnd, _proc, _id, 0);
    }

    private IntPtr Proc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data)
    {
        if (message == WM_NCDESTROY)
        {
            Dispose();
            return DefSubclassProc(hwnd, message, wParam, lParam);
        }

        try
        {
            if (_handler(message, wParam, lParam) is { } result)
                return result;
        }
        catch (Exception ex)
        {
            // Exception tidak boleh keluar dari callback Win32 (prosesnya langsung mati tanpa catatan). Dilempar ulang
            // lewat dispatcher supaya tercatat di crash log seperti error lain.
            var captured = ExceptionDispatchInfo.Capture(ex);
            _dispatcher.TryEnqueue(captured.Throw);
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_removed)
            return;
        _removed = true;
        RemoveWindowSubclass(_hwnd, _proc, _id);
    }

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, nuint id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
