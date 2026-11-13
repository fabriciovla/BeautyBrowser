using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace BeautyBrowser.Services;

/// <summary>
/// Windows shell plumbing for installed web apps: .lnk shortcuts and window properties that carry an
/// AppUserModelID, so each app gets its own taskbar button and icon instead of grouping with the browser.
/// </summary>
internal static class ShellShortcuts
{
    /// <summary>Creates (or replaces) a shortcut that starts <paramref name="exe"/> with <paramref name="arguments"/>.</summary>
    public static void Create(string path, string exe, string arguments, string iconPath, string description, string appId)
    {
        var link = (IShellLinkW)new ShellLink();
        link.SetPath(exe);
        link.SetArguments(arguments);
        link.SetWorkingDirectory(Path.GetDirectoryName(exe) ?? "");
        link.SetIconLocation(iconPath, 0);
        link.SetDescription(description);

        var store = (IPropertyStore)link;
        var key = AppUserModelId;
        using (var value = PropVariant.FromString(appId))
        {
            var v = value.Value;
            store.SetValue(ref key, ref v);
        }
        store.Commit();

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ((IPersistFile)link).Save(path, true);
        Marshal.ReleaseComObject(link);
    }

    /// <summary>
    /// Gives a window its own AppUserModelID, plus what the taskbar needs to pin it (how to relaunch it, its name and
    /// icon), so a pinned app opens the app and not the browser.
    /// </summary>
    public static void SetWindowAppId(nint hwnd, string appId, string relaunchCommand, string name, string iconPath)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store is null) return;
        void Set(PropertyKey key, string text)
        {
            using var value = PropVariant.FromString(text);
            var v = value.Value;
            store.SetValue(ref key, ref v);
        }
        Set(AppUserModelId, appId);
        Set(RelaunchCommand, relaunchCommand);
        Set(RelaunchDisplayName, name);
        Set(RelaunchIcon, iconPath);
        store.Commit();
        Marshal.ReleaseComObject(store);
    }

    // PKEY_AppUserModel_* ({9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}).
    private static readonly Guid AppUserModelFormat = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private static PropertyKey AppUserModelId => new(AppUserModelFormat, 5);
    private static PropertyKey RelaunchCommand => new(AppUserModelFormat, 2);
    private static PropertyKey RelaunchIcon => new(AppUserModelFormat, 3);
    private static PropertyKey RelaunchDisplayName => new(AppUserModelFormat, 4);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct PropertyKey(Guid format, uint id)
    {
        public readonly Guid Format = format;
        public readonly uint Id = id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantValue
    {
        public ushort Type;
        public ushort Reserved1, Reserved2, Reserved3;
        public nint Pointer;
        public nint Pointer2;
    }

    /// <summary>A VT_LPWSTR PROPVARIANT that frees its string.</summary>
    private sealed class PropVariant : IDisposable
    {
        public PropVariantValue Value;

        public static PropVariant FromString(string text) =>
            new() { Value = new PropVariantValue { Type = 31 /* VT_LPWSTR */, Pointer = Marshal.StringToCoTaskMemUni(text) } };

        public void Dispose()
        {
            if (Value.Pointer != 0) Marshal.FreeCoTaskMem(Value.Pointer);
            Value.Pointer = 0;
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(nint hwnd, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? store);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int max, nint findData, uint flags);
        void GetIDList(out nint idl);
        void SetIDList(nint idl);
        void GetDescription([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder dir, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder args, int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder path, int max, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(nint hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariantValue value);
        void SetValue(ref PropertyKey key, ref PropVariantValue value);
        void Commit();
    }
}
