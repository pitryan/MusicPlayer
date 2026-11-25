using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using MusicPlayer.Helpers;

namespace MusicPlayer.Services;

/// <summary>
/// Ikon MusicPlayer di notification area (di samping jam). Klik untuk membuka jendela; klik kanan untuk Play/Pause,
/// Next, Previous, membuka jendela, atau keluar. Tooltip-nya lagu yang sedang diputar.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint IconId = 1;
    private const uint CallbackMessage = 0x8000 + 1; // WM_APP + 1

    private const uint MenuPlayPause = 1;
    private const uint MenuNext = 2;
    private const uint MenuPrevious = 3;
    private const uint MenuShow = 4;
    private const uint MenuExit = 5;

    private readonly IntPtr _hwnd;
    private readonly PlaybackService _playback;
    private readonly WindowSubclass _subclass;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();

    // Dikirim ke semua jendela setelah Explorer dimulai ulang; ikonnya harus ditambahkan lagi
    private readonly uint _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

    private IntPtr _icon;
    private bool _visible;

    public TrayIcon(IntPtr hwnd, PlaybackService playback)
    {
        _hwnd = hwnd;
        _playback = playback;
        _subclass = new WindowSubclass(hwnd, OnMessage);
        _playback.PropertyChanged += Playback_PropertyChanged;
        UseSystemMenuTheme();
    }

    /// <summary>Klik ikonnya, atau Open MusicPlayer di menunya.</summary>
    public event EventHandler? ShowRequested;

    /// <summary>Exit di menunya.</summary>
    public event EventHandler? ExitRequested;

    public bool IsVisible
    {
        get => _visible;
        set
        {
            if (value == _visible)
                return;
            _visible = value;
            if (value)
                Add();
            else
                Remove();
        }
    }

    /// <summary>Pemberitahuan singkat dari ikonnya (di Windows 11 tampil sebagai notifikasi).</summary>
    public void ShowNotice(string title, string text)
    {
        if (!_visible)
            return;
        var data = NewData(NIF_INFO);
        data.szInfoTitle = title;
        data.szInfo = text;
        data.dwInfoFlags = NIIF_NONE | NIIF_RESPECT_QUIET_TIME;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private void Add()
    {
        if (_icon == IntPtr.Zero)
            _icon = NativeIcons.AppLogo(Math.Max(16, GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForSystem())));

        var data = NewData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _icon;
        data.szTip = TooltipText();
        Shell_NotifyIcon(NIM_ADD, ref data);

        // Versi 4: klik kiri dilaporkan sebagai NIN_SELECT dan klik kanan sebagai WM_CONTEXTMENU, beserta posisinya
        data.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, ref data);
    }

    private void Remove()
    {
        var data = NewData(0);
        Shell_NotifyIcon(NIM_DELETE, ref data);
    }

    private NOTIFYICONDATA NewData(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = IconId,
        uFlags = flags,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    /// <summary>"Judul — Artis" atau nama aplikasi; tooltip paling panjang 127 karakter.</summary>
    private string TooltipText()
    {
        string text = _playback.CurrentTrack is { } track ? $"{track.DisplayTitle} — {track.DisplayArtist}" : "MusicPlayer";
        return text.Length > 127 ? text[..126] + "…" : text;
    }

    private void Playback_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_visible || e.PropertyName != nameof(PlaybackService.CurrentTrack))
            return;
        var data = NewData(NIF_TIP | NIF_SHOWTIP);
        data.szTip = TooltipText();
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private IntPtr? OnMessage(uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == _taskbarCreatedMessage && _visible)
        {
            Add();
            return null;
        }
        if (message != CallbackMessage)
            return null;

        switch ((uint)((nuint)lParam & 0xFFFF))
        {
            case NIN_SELECT:
            case NIN_KEYSELECT:
                Later(() => ShowRequested?.Invoke(this, EventArgs.Empty));
                break;
            case WM_CONTEXTMENU:
                // Posisi menu (koordinat layar) ada di wParam
                ShowMenu((short)((nuint)wParam & 0xFFFF), (short)((nuint)wParam >> 16 & 0xFFFF));
                break;
        }
        return 0;
    }

    private void ShowMenu(int x, int y)
    {
        IntPtr menu = CreatePopupMenu();
        try
        {
            if (_playback.CurrentTrack is { } track)
            {
                // Lagu yang sedang diputar sebagai judul menu; "&" ditulis dua kali supaya tidak dianggap tombol pintas
                string title = $"{track.DisplayTitle} — {track.DisplayArtist}".Replace("&", "&&");
                AppendMenu(menu, MF_STRING | MF_GRAYED, 0, title.Length > 60 ? title[..59] + "…" : title);
                AppendMenu(menu, MF_SEPARATOR, 0, null);
            }
            uint playback = _playback.CurrentTrack is null ? MF_GRAYED : 0;
            AppendMenu(menu, MF_STRING | playback, MenuPlayPause, _playback.IsPlaying ? "Pause" : "Play");
            AppendMenu(menu, MF_STRING | playback, MenuNext, "Next");
            AppendMenu(menu, MF_STRING | playback, MenuPrevious, "Previous");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, MenuShow, "Open MusicPlayer");
            AppendMenu(menu, MF_STRING, MenuExit, "Exit");
            SetMenuDefaultItem(menu, MenuShow, 0);

            // Tanpa ini menunya tidak tertutup saat pengguna mengklik di luar menu
            SetForegroundWindow(_hwnd);
            uint chosen = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY, x, y, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, 0, IntPtr.Zero, IntPtr.Zero); // WM_NULL, juga supaya menunya tertutup dengan benar

            switch (chosen)
            {
                case MenuPlayPause: Later(_playback.TogglePlayPause); break;
                case MenuNext: Later(_playback.Next); break;
                case MenuPrevious: Later(_playback.Previous); break;
                case MenuShow: Later(() => ShowRequested?.Invoke(this, EventArgs.Empty)); break;
                case MenuExit: Later(() => ExitRequested?.Invoke(this, EventArgs.Empty)); break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    /// <summary>
    /// Dijalankan setelah pesan Win32 ini selesai diproses, bukan di dalamnya: Exit menutup jendela yang pesannya
    /// sedang ditangani.
    /// </summary>
    private void Later(Action action) => _dispatcher.TryEnqueue(() => action());

    /// <summary>
    /// Menu klik kanan mengikuti mode gelap atau terang Windows, seperti menu ikon lain di notification area. Fungsi ini
    /// tidak terdokumentasi (uxtheme ordinal 135 dan 136); kalau tidak ada, menunya tetap tampil dengan warna terang.
    /// </summary>
    private static void UseSystemMenuTheme()
    {
        try
        {
            SetPreferredAppMode(1); // AllowDark
            FlushMenuThemes();
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
        }
    }

    public void Dispose()
    {
        _playback.PropertyChanged -= Playback_PropertyChanged;
        IsVisible = false;
        _subclass.Dispose();
        NativeIcons.Destroy(_icon);
        _icon = IntPtr.Zero;
    }

    // ── Win32 ────────────────────────────────────────────────

    private const uint NIM_ADD = 0;
    private const uint NIM_MODIFY = 1;
    private const uint NIM_DELETE = 2;
    private const uint NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 0x01;
    private const uint NIF_ICON = 0x02;
    private const uint NIF_TIP = 0x04;
    private const uint NIF_INFO = 0x10;
    private const uint NIF_SHOWTIP = 0x80;
    private const uint NIIF_NONE = 0x00;
    private const uint NIIF_RESPECT_QUIET_TIME = 0x80;
    private const uint NOTIFYICON_VERSION_4 = 4;
    private const uint NIN_SELECT = 0x0400;
    private const uint NIN_KEYSELECT = 0x0401;
    private const uint WM_CONTEXTMENU = 0x007B;
    private const uint MF_STRING = 0x0000;
    private const uint MF_GRAYED = 0x0001;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_NONOTIFY = 0x0080;
    private const uint TPM_RETURNCMD = 0x0100;
    private const int SM_CXSMICON = 49;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion; // juga uTimeout (union)
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, nuint id, string? text);

    [DllImport("user32.dll")]
    private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPosition);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr parameters);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    private static extern int SetPreferredAppMode(int mode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    private static extern void FlushMenuThemes();
}
