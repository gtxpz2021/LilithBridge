using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LilithBridge;

/// <summary>
/// Binds out-of-process worker trees (Python + audiocpp_server) to a Windows NT Job Object
/// with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE so no child or grandchild process can ever orphan
/// holding VRAM/RAM if the worker or game exits abruptly.
/// </summary>
internal sealed class ChildProcessJobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x00000800;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    private IntPtr _handle;
    private bool _disposed;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob,
        int infoType,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    internal static ChildProcessJobObject? TryAttach(Process? process)
    {
        if (process == null || !OperatingSystem.IsWindows())
            return null;

        IntPtr job = IntPtr.Zero;
        try
        {
            job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
                return null;

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_BREAKAWAY_OK;

            if (!SetInformationJobObject(
                    job,
                    JobObjectExtendedLimitInformation,
                    ref info,
                    (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            {
                CloseHandle(job);
                return null;
            }

            if (!AssignProcessToJobObject(job, process.Handle))
            {
                CloseHandle(job);
                return null;
            }

            return new ChildProcessJobObject { _handle = job };
        }
        catch
        {
            if (job != IntPtr.Zero)
            {
                try { CloseHandle(job); } catch { }
            }
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        IntPtr h = _handle;
        _handle = IntPtr.Zero;
        if (h != IntPtr.Zero)
        {
            try { CloseHandle(h); } catch { }
        }
    }
}
