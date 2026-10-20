using System;
using System.Runtime.InteropServices;
using System.Text;

namespace EverythingToolbar.Launcher;

// Inline interop for shell shortcuts. Replaces the previous COMReference to
// IWshRuntimeLibrary/Shell32 so the solution builds with the plain .NET SDK, without
// Visual Studio's COM reference resolution.
internal static class ShellLinkInterop
{
    private const uint SLGP_RAWPATH = 0x4;
    private const uint STGM_READ = 0x0;

    public static string? GetTargetPath(string shortcutPath)
    {
        var shellLink = (IShellLinkW)new ShellLinkCoClass();
        ((IPersistFile)shellLink).Load(shortcutPath, STGM_READ);

        var buffer = new StringBuilder(1024);
        shellLink.GetPath(buffer, buffer.Capacity, IntPtr.Zero, SLGP_RAWPATH);

        var path = buffer.ToString();
        return path.Length > 0 ? path : null;
    }

    public static void CreateShortcut(string shortcutPath, string targetPath, string? iconPath)
    {
        var shellLink = (IShellLinkW)new ShellLinkCoClass();
        shellLink.SetPath(targetPath);

        if (iconPath != null)
            shellLink.SetIconLocation(iconPath, 0);

        ((IPersistFile)shellLink).Save(shortcutPath, true);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010B-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig]
        int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
