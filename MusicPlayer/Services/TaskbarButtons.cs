using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using MusicPlayer.Helpers;

namespace MusicPlayer.Services;

/// <summary>
/// Tombol Previous, Play/Pause, dan Next di thumbnail taskbar: muncul saat kursor berada di atas tombol aplikasi di
/// taskbar, jadi lagu bisa diatur selagi jendelanya di-minimize atau tertutup jendela lain.
/// </summary>
public sealed class TaskbarButtons : IDisposable
{
    private const uint PreviousId = 1;
    private const uint PlayPauseId = 2;
    private const uint NextId = 3;

    private const uint WM_COMMAND = 0x0111;
    private const uint WM_DPICHANGED = 0x02E0;
    private const int THBN_CLICKED = 0x1800;

    private readonly IntPtr _hwnd;
    private readonly PlaybackService _playback;
    private readonly WindowSubclass _subclass;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();

    // Dikirim Windows setiap kali tombol aplikasi di taskbar dibuat: saat jendela pertama kali tampil, saat muncul lagi
    // setelah disembunyikan, dan setelah Explorer dimulai ulang. Tombol thumbnail baru bisa ditambahkan setelah itu.
    private readonly uint _buttonCreatedMessage = RegisterWindowMessage("TaskbarButtonCreated");

    private ITaskbarList3? _taskbar;
    private bool _added;
    private int _iconSize;
    private IntPtr _previousIcon, _playIcon, _pauseIcon, _nextIcon;

    public TaskbarButtons(IntPtr hwnd, PlaybackService playback)
    {
        _hwnd = hwnd;
        _playback = playback;
        _subclass = new WindowSubclass(hwnd, OnMessage);
        _playback.PropertyChanged += Playback_PropertyChanged;
    }

    private IntPtr? OnMessage(uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == _buttonCreatedMessage)
        {
            AddButtons();
            return null;
        }

        if (message == WM_COMMAND && (int)((nuint)wParam >> 16 & 0xFFFF) == THBN_CLICKED)
        {
            // Dijalankan setelah pesan ini selesai diproses, seperti klik tombol biasa
            Action? action = (uint)((nuint)wParam & 0xFFFF) switch
            {
                PreviousId => _playback.Previous,
                PlayPauseId => _playback.TogglePlayPause,
                NextId => _playback.Next,
                _ => null,
            };
            if (action is not null)
            {
                _dispatcher.TryEnqueue(() => action());
                return 0;
            }
        }

        // Pindah ke layar dengan skala lain: ikonnya digambar ulang di ukuran barunya
        if (message == WM_DPICHANGED && _added)
            UpdateButtons();
        return null;
    }

    private void Playback_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_added && e.PropertyName is nameof(PlaybackService.IsPlaying) or nameof(PlaybackService.CurrentTrack))
            UpdateButtons();
    }

    private void AddButtons()
    {
        try
        {
            if (_taskbar is null)
            {
                var taskbar = (ITaskbarList3)new TaskbarList();
                if (taskbar.HrInit() < 0)
                    return;
                _taskbar = taskbar;
            }
            var buttons = CreateButtons();
            _added = _taskbar.ThumbBarAddButtons(_hwnd, (uint)buttons.Length, buttons) >= 0;
        }
        catch (COMException)
        {
            // Taskbar tidak tersedia (misalnya Explorer sedang dimulai ulang): dicoba lagi pada pesan berikutnya
        }
    }

    private void UpdateButtons()
    {
        if (_taskbar is null)
            return;
        var buttons = CreateButtons();
        _taskbar.ThumbBarUpdateButtons(_hwnd, (uint)buttons.Length, buttons);
    }

    private THUMBBUTTON[] CreateButtons()
    {
        EnsureIcons();
        bool hasTrack = _playback.CurrentTrack is not null;
        bool playing = _playback.IsPlaying;
        return
        [
            Button(PreviousId, _previousIcon, "Previous", hasTrack),
            Button(PlayPauseId, playing ? _pauseIcon : _playIcon, playing ? "Pause" : "Play", hasTrack),
            Button(NextId, _nextIcon, "Next", hasTrack),
        ];
    }

    private static THUMBBUTTON Button(uint id, IntPtr icon, string tooltip, bool enabled) => new()
    {
        dwMask = THB_ICON | THB_TOOLTIP | THB_FLAGS,
        iId = id,
        hIcon = icon,
        szTip = tooltip,
        dwFlags = enabled ? THBF_ENABLED : THBF_DISABLED,
    };

    /// <summary>Ikon kecil sesuai skala layar tempat jendela berada (16 px di 100%).</summary>
    private void EnsureIcons()
    {
        int size = GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForWindow(_hwnd));
        if (size <= 0)
            size = 16;
        if (size == _iconSize && _playIcon != IntPtr.Zero)
            return;

        DestroyIcons();
        _iconSize = size;
        _previousIcon = NativeIcons.Transport(TransportIcon.Previous, size);
        _playIcon = NativeIcons.Transport(TransportIcon.Play, size);
        _pauseIcon = NativeIcons.Transport(TransportIcon.Pause, size);
        _nextIcon = NativeIcons.Transport(TransportIcon.Next, size);
    }

    private void DestroyIcons()
    {
        NativeIcons.Destroy(_previousIcon);
        NativeIcons.Destroy(_playIcon);
        NativeIcons.Destroy(_pauseIcon);
        NativeIcons.Destroy(_nextIcon);
        _previousIcon = _playIcon = _pauseIcon = _nextIcon = IntPtr.Zero;
    }

    public void Dispose()
    {
        _playback.PropertyChanged -= Playback_PropertyChanged;
        _subclass.Dispose();
        DestroyIcons();
        if (_taskbar is not null)
        {
            Marshal.FinalReleaseComObject(_taskbar);
            _taskbar = null;
        }
    }

    // ── Win32 ────────────────────────────────────────────────

    private const int SM_CXSMICON = 49;
    private const uint THB_ICON = 0x2;
    private const uint THB_TOOLTIP = 0x4;
    private const uint THB_FLAGS = 0x8;
    private const uint THBF_ENABLED = 0x0;
    private const uint THBF_DISABLED = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct THUMBBUTTON
    {
        public uint dwMask;
        public uint iId;
        public uint iBitmap;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szTip;
        public uint dwFlags;
    }

    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    [ClassInterface(ClassInterfaceType.None)]
    private class TaskbarList
    {
    }

    /// <summary>ITaskbarList3 dengan semua method induknya (ITaskbarList, ITaskbarList2) berurutan seperti di vtable.</summary>
    [ComImport]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(IntPtr hwnd);
        [PreserveSig] int DeleteTab(IntPtr hwnd);
        [PreserveSig] int ActivateTab(IntPtr hwnd);
        [PreserveSig] int SetActiveAlt(IntPtr hwnd);
        [PreserveSig] int MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        [PreserveSig] int SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        [PreserveSig] int SetProgressState(IntPtr hwnd, int flags);
        [PreserveSig] int RegisterTab(IntPtr tab, IntPtr mdi);
        [PreserveSig] int UnregisterTab(IntPtr tab);
        [PreserveSig] int SetTabOrder(IntPtr tab, IntPtr insertBefore);
        [PreserveSig] int SetTabActive(IntPtr tab, IntPtr mdi, uint reserved);
        [PreserveSig] int ThumbBarAddButtons(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] buttons);
        [PreserveSig] int ThumbBarUpdateButtons(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] buttons);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
