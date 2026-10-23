using System.Runtime.InteropServices;

namespace HouseControl.Shell;

/// <summary>
/// The menu Windows shows when House Control's taskbar button or Start entry is right-clicked:
/// the home's scenes, All lights off, and the windows. Each entry starts House Control with an
/// argument, which the running copy carries out (see <see cref="SingleInstance"/>).
/// </summary>
internal static class JumpList
{
    public const string AppId = "0xProbability.HouseControl";

    public sealed record Entry(string Title, string Arguments, string? Description = null, int IconIndex = 0);

    /// <summary>Replaces the jump list. Failures are logged and otherwise ignored: it is a convenience.</summary>
    public static void Apply(string category, IReadOnlyList<Entry> categoryItems, IReadOnlyList<Entry?> tasks)
    {
        try
        {
            var list = (ICustomDestinationList)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("77f10cf0-3db5-4966-b520-b7c54fd35ed6"))!)!;
            list.SetAppID(AppId);
            var arrayId = typeof(IObjectArray).GUID;
            list.BeginList(out _, ref arrayId, out _);
            try
            {
                if (categoryItems.Count > 0)
                {
                    var items = Collection(categoryItems);
                    // Fails when the user switched off "recently opened items in Jump Lists"; the tasks still show.
                    try { list.AppendCategory(category, (IObjectArray)items); }
                    catch (COMException) { }
                }
                list.AddUserTasks((IObjectArray)Collection(tasks));
                list.CommitList();
            }
            catch
            {
                list.AbortList();
                throw;
            }
        }
        catch (Exception e)
        {
            Log.Error("jump list", e);
        }
    }

    private static IObjectCollection Collection(IEnumerable<Entry?> entries)
    {
        var collection = (IObjectCollection)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("2d3468c1-36a7-43b6-ac24-d3f02fd9607a"))!)!;
        foreach (var entry in entries) collection.AddObject(entry is null ? Separator() : Link(entry));
        return collection;
    }

    private static IShellLinkW Link(Entry entry)
    {
        var exe = Environment.ProcessPath!;
        var link = (IShellLinkW)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))!)!;
        link.SetPath(exe);
        link.SetArguments(entry.Arguments);
        link.SetIconLocation(exe, entry.IconIndex);
        link.SetWorkingDirectory(Path.GetDirectoryName(exe)!);
        if (entry.Description != null) link.SetDescription(entry.Description);
        var store = (IPropertyStore)link;
        var title = new PropertyKey(new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), 2);
        var value = PropVariant.FromString(entry.Title);
        try
        {
            store.SetValue(ref title, ref value);
            store.Commit();
        }
        finally
        {
            value.Clear();
        }
        return link;
    }

    private static IShellLinkW Separator()
    {
        var link = (IShellLinkW)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))!)!;
        var store = (IPropertyStore)link;
        var key = new PropertyKey(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 6);
        var value = new PropVariant { vt = 11, boolVal = -1 }; // VT_BOOL true
        store.SetValue(ref key, ref value);
        store.Commit();
        return link;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey(Guid format, int id)
    {
        public Guid fmtid = format;
        public int pid = id;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointer;
        [FieldOffset(8)] public short boolVal;

        public static PropVariant FromString(string s) => new() { vt = 31, pointer = Marshal.StringToCoTaskMemUni(s) }; // VT_LPWSTR

        public void Clear()
        {
            if (vt == 31 && pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer);
            pointer = IntPtr.Zero;
        }
    }

    [ComImport, Guid("6332debf-87b5-4670-90c0-5e57b408a49e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICustomDestinationList
    {
        void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
        void BeginList(out uint minSlots, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object removed);
        void AppendCategory([MarshalAs(UnmanagedType.LPWStr)] string category, IObjectArray items);
        void AppendKnownCategory(int category);
        void AddUserTasks(IObjectArray tasks);
        void CommitList();
        void GetRemovedDestinations(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object removed);
        void DeleteList([MarshalAs(UnmanagedType.LPWStr)] string appId);
        void AbortList();
    }

    [ComImport, Guid("92ca9dcd-5622-4bba-a805-5e9f541bd8c9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArray
    {
        void GetCount(out uint count);
        void GetAt(uint index, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object item);
    }

    [ComImport, Guid("5632b1a4-e38a-400a-928a-d4cd63230295"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectCollection
    {
        void GetCount(out uint count);
        void GetAt(uint index, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object item);
        void AddObject([MarshalAs(UnmanagedType.Interface)] object item);
        void AddFromArray(IObjectArray source);
        void RemoveObjectAt(uint index);
        void Clear();
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(IntPtr file, int max, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription(IntPtr name, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory(IntPtr dir, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments(IntPtr args, int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int show);
        void SetShowCmd(int show);
        void GetIconLocation(IntPtr path, int max, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }
}
