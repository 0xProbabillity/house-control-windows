using System.Runtime.InteropServices;

namespace HouseControl.Native;

/// <summary>
/// Just enough DirectComposition to give a window a visual tree WebView2 can draw into: that
/// is what lets the panel, the island and the desktop widget have truly transparent corners
/// and soft shadows, composited by the GPU with no copy of each frame.
/// </summary>
internal sealed class Composition : IDisposable
{
    private readonly IDCompositionDevice _device;
    private readonly IDCompositionTarget _target;
    private IntPtr _visual;

    /// <summary>The visual WebView2 renders into (CoreWebView2CompositionController.RootVisualTarget).</summary>
    public object Visual { get; }

    public Composition(IntPtr hwnd)
    {
        var iid = typeof(IDCompositionDevice).GUID;
        Marshal.ThrowExceptionForHR(DCompositionCreateDevice2(IntPtr.Zero, ref iid, out var device));
        _device = (IDCompositionDevice)Marshal.GetObjectForIUnknown(device);
        Marshal.Release(device);
        Marshal.ThrowExceptionForHR(_device.CreateTargetForHwnd(hwnd, true, out _target));
        // Raw IDCompositionVisual pointer: SetRoot wants exactly that interface, not IUnknown.
        Marshal.ThrowExceptionForHR(_device.CreateVisual(out _visual));
        Marshal.ThrowExceptionForHR(_target.SetRoot(_visual));
        Visual = Marshal.GetObjectForIUnknown(_visual);
        Commit();
    }

    public void Commit() => _device.Commit();

    public void Dispose()
    {
        Marshal.ReleaseComObject(Visual);
        if (_visual != IntPtr.Zero) Marshal.Release(_visual);
        _visual = IntPtr.Zero;
        Marshal.ReleaseComObject(_target);
        Marshal.ReleaseComObject(_device);
    }

    [DllImport("dcomp.dll")]
    private static extern int DCompositionCreateDevice2(IntPtr renderingDevice, ref Guid iid, out IntPtr device);

    // Only the leading methods of each interface are declared; vtable order matters.
    [ComImport, Guid("C37EA93A-E7AA-450D-B16F-9746CB0407F3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDCompositionDevice
    {
        [PreserveSig] int Commit();
        [PreserveSig] int WaitForCommitCompletion();
        [PreserveSig] int GetFrameStatistics(IntPtr statistics);
        [PreserveSig] int CreateTargetForHwnd(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool topmost, out IDCompositionTarget target);
        [PreserveSig] int CreateVisual(out IntPtr visual);
    }

    [ComImport, Guid("eacdd04c-117e-4e17-88f4-d1b12b0e3d89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDCompositionTarget
    {
        [PreserveSig] int SetRoot(IntPtr visual);
    }
}
