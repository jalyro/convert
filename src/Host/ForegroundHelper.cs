using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Jalyro.Convert.Host;

/// <summary>
/// Brings a window to the front from a background process.
///
/// The Host has been idle since login, so Windows treats it as having no right
/// to steal focus and quietly opens its windows behind whatever is in front.
/// Attaching to the foreground window's input thread for the duration of the
/// call grants that right; detaching immediately gives it back.
/// </summary>
internal static class ForegroundHelper
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool fAttach);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    public static void Bring(Window window)
    {
        try
        {
            window.Show();
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;

            IntPtr self = new WindowInteropHelper(window).Handle;
            if (self == IntPtr.Zero)
            {
                window.Activate();
                return;
            }

            IntPtr front = GetForegroundWindow();
            uint frontThread = GetWindowThreadProcessId(front, IntPtr.Zero);
            uint ourThread = GetCurrentThreadId();

            if (frontThread != 0 && frontThread != ourThread)
            {
                AttachThreadInput(ourThread, frontThread, true);
                BringWindowToTop(self);
                SetForegroundWindow(self);
                AttachThreadInput(ourThread, frontThread, false);
            }
            else
            {
                BringWindowToTop(self);
                SetForegroundWindow(self);
            }

            window.Activate();
        }
        catch
        {
            // Focus is a nicety. Never let it take the Host down.
            try { window.Activate(); } catch { }
        }
    }
}
