using System;
using System.Runtime.InteropServices;

namespace Jalyro.Convert.Host;

/// <summary>
/// Taskbar button progress, via ITaskbarList3.
///
/// Deliberately P/Invoke rather than a package: this is about thirty lines and
/// pulling a dependency in for it would be worse. Every call is guarded -
/// taskbar progress is a nicety and must never take a conversion down.
/// </summary>
internal static class TaskbarProgress
{
    private enum State
    {
        NoProgress = 0,
        Indeterminate = 0x1,
        Normal = 0x2,
        Error = 0x4,
        Paused = 0x8
    }

    [ComImport]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        // ITaskbarList2
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        // ITaskbarList3
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, State state);
        // Remaining ITaskbarList3 members are unused and intentionally omitted;
        // the vtable order above is all that matters for the calls we make.
    }

    [ComImport]
    [Guid("56fdf344-fd6d-11d0-958a-006097c9a090")]
    [ClassInterface(ClassInterfaceType.None)]
    private class TaskbarInstance { }

    private static ITaskbarList3? _taskbar;
    private static bool _failed;

    private static ITaskbarList3? Instance
    {
        get
        {
            if (_failed) return null;
            if (_taskbar is not null) return _taskbar;

            try
            {
                _taskbar = (ITaskbarList3)new TaskbarInstance();
                _taskbar.HrInit();
                return _taskbar;
            }
            catch
            {
                _failed = true;
                return null;
            }
        }
    }

    public static void SetProgress(IntPtr hwnd, int completed, int total)
    {
        if (hwnd == IntPtr.Zero || total <= 0) return;
        try
        {
            Instance?.SetProgressState(hwnd, State.Normal);
            Instance?.SetProgressValue(hwnd, (ulong)completed, (ulong)total);
        }
        catch { /* nicety only */ }
    }

    public static void SetIndeterminate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try { Instance?.SetProgressState(hwnd, State.Indeterminate); }
        catch { /* nicety only */ }
    }

    public static void SetError(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try { Instance?.SetProgressState(hwnd, State.Error); }
        catch { /* nicety only */ }
    }

    public static void Clear(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try { Instance?.SetProgressState(hwnd, State.NoProgress); }
        catch { /* nicety only */ }
    }
}
