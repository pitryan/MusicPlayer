using System;
using System.IO;
using System.Runtime.InteropServices;

namespace MusicPlayer.Helpers;

/// <summary>
/// Memindah file ke Recycle Bin, supaya file yang dibersihkan Library Health masih bisa dikembalikan dari Explorer.
/// </summary>
public static class RecycleBin
{
    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    // Drive tanpa Recycle Bin (misalnya flashdisk) akan menghapus permanen: Windows menanyakannya dulu, tidak diam-diam
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    /// <summary>Melempar exception kalau file-nya tidak bisa dipindah, atau pengguna membatalkannya.</summary>
    public static void Send(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("The file no longer exists.", path);

        var operation = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            // Daftar path diakhiri dua karakter null
            pFrom = Path.GetFullPath(path) + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT | FOF_WANTNUKEWARNING,
        };
        int result = SHFileOperation(ref operation);
        if (operation.fAnyOperationsAborted)
            throw new OperationCanceledException("Cancelled.");
        if (result != 0 || File.Exists(path))
            throw new IOException($"Couldn't move the file to the Recycle Bin (error 0x{result:X}).");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT operation);
}
