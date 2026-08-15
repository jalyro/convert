using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Jalyro.Convert.Host;

/// <summary>
/// A Windows Job Object holding every Worker and every ffmpeg process.
///
/// Two guarantees, both of which the product needed and did not have:
///
/// 1. **No orphans, ever.** With KILL_ON_JOB_CLOSE, everything in the job dies
///    when the Host's handle closes — including if the Host is killed outright.
///    Testing found two abandoned ffmpeg processes still encoding for jobs
///    whose Host was long gone, one with 94 CPU-minutes accumulated. A user
///    experiences that as their machine running hot for no reason. The startup
///    sweep added in 0.5.2 cleaned up after the fact; this prevents it.
///
/// 2. **Bounded memory.** A malformed image that makes a decoder allocate
///    without limit hits a per-process ceiling and dies, rather than pushing
///    the machine into swap.
///
/// Note on what this deliberately does NOT do: run Workers at low integrity.
/// A low-IL process cannot write to ordinary user folders, and writing the
/// output next to the source file is the product's entire interaction model.
/// See docs/sandboxing.md.
/// </summary>
internal sealed class WorkerJobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;

    [Flags]
    private enum LimitFlags : uint
    {
        ProcessMemory      = 0x00000100,
        JobMemory          = 0x00000200,
        KillOnJobClose     = 0x00002000,
        DieOnUnhandledException = 0x00000400,
        BreakawayOk        = 0x00000800
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr security, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr job, int infoClass, ref ExtendedLimitInformation info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private IntPtr _handle = IntPtr.Zero;

    public bool IsAvailable => _handle != IntPtr.Zero;

    /// <param name="perProcessMegabytes">
    /// Ceiling for a single Worker or ffmpeg. Generous enough for a 100-megapixel
    /// TIFF, tight enough that a runaway allocation dies rather than swapping.
    /// </param>
    /// <param name="totalMegabytes">Ceiling across every process in the job.</param>
    public WorkerJobObject(int perProcessMegabytes = 4096, int totalMegabytes = 12288)
    {
        try
        {
            _handle = CreateJobObject(IntPtr.Zero, null);
            if (_handle == IntPtr.Zero)
            {
                Storage.Log($"JobObject: CreateJobObject failed ({Marshal.GetLastWin32Error()})");
                return;
            }

            var info = new ExtendedLimitInformation
            {
                BasicLimitInformation = new BasicLimitInformation
                {
                    LimitFlags = (uint)(LimitFlags.KillOnJobClose
                                      | LimitFlags.ProcessMemory
                                      | LimitFlags.JobMemory
                                      | LimitFlags.DieOnUnhandledException)
                },
                ProcessMemoryLimit = (UIntPtr)((ulong)perProcessMegabytes * 1024 * 1024),
                JobMemoryLimit     = (UIntPtr)((ulong)totalMegabytes * 1024 * 1024)
            };

            int size = Marshal.SizeOf<ExtendedLimitInformation>();
            if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, ref info, size))
            {
                Storage.Log($"JobObject: SetInformationJobObject failed ({Marshal.GetLastWin32Error()})");
                Dispose();
                return;
            }

            Storage.Log($"JobObject: created, {perProcessMegabytes} MB per process, "
                      + $"{totalMegabytes} MB total, kill-on-close");
        }
        catch (Exception ex)
        {
            Storage.Log($"JobObject: {ex.GetType().Name}: {ex.Message}");
            _handle = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Adds a process to the job. Failure is logged and tolerated — a
    /// conversion running without a memory cap is worse than one with a cap,
    /// but far better than one that does not run.
    /// </summary>
    public bool Assign(Process process)
    {
        if (_handle == IntPtr.Zero)
            return false;

        try
        {
            if (AssignProcessToJobObject(_handle, process.Handle))
                return true;

            int error = Marshal.GetLastWin32Error();

            // 5 = access denied, which happens when the process is already in a
            // job that does not permit breakaway - under some CI and container
            // configurations, for instance.
            Storage.Log($"JobObject: could not assign pid {process.Id} ({error})");
            return false;
        }
        catch (Exception ex)
        {
            Storage.Log($"JobObject: assign threw {ex.GetType().Name}");
            return false;
        }
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero)
            return;

        // Closing the last handle kills everything in the job. That is the
        // point: an abruptly-terminated Host cannot leave encoders running.
        CloseHandle(_handle);
        _handle = IntPtr.Zero;
    }
}
