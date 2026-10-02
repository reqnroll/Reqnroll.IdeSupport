using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

/// <summary>
/// Assigns a child process to a Win32 Job Object configured with
/// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>.  When the last handle to the Job
/// Object is closed (i.e. when the VS process exits — even abnormally), the OS
/// automatically terminates every process in the job.
/// </summary>
internal sealed class ChildProcessJob : IDisposable
{
    private readonly object _lock = new();
    private IntPtr _jobHandle;
    private bool   _disposed;

    /// <summary>Creates a new Win32 Job Object configured to kill all member processes when the job is closed.</summary>
    public ChildProcessJob()
    {
        _jobHandle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (_jobHandle == IntPtr.Zero)
            throw new InvalidOperationException(
                $"CreateJobObject failed: {Marshal.GetLastWin32Error()}");

        var info = new NativeMethods.JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        };
        var extInfo = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = info
        };

        int length = Marshal.SizeOf(typeof(NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
        var extInfoPtr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(extInfo, extInfoPtr, false);
            if (!NativeMethods.SetInformationJobObject(
                    _jobHandle,
                    NativeMethods.JobObjectInfoType.ExtendedLimitInformation,
                    extInfoPtr,
                    (uint)length))
            {
                throw new InvalidOperationException(
                    $"SetInformationJobObject failed: {Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(extInfoPtr);
        }
    }

    /// <summary>
    /// Assigns <paramref name="process"/> to this job.  Must be called before any
    /// child process of <paramref name="process"/> is created.
    /// </summary>
    public void AddProcess(Process process)
    {
        lock (_lock)
        {
            if (_disposed) return;
            if (!NativeMethods.AssignProcessToJobObject(_jobHandle, process.Handle))
                throw new InvalidOperationException(
                    $"AssignProcessToJobObject failed: {Marshal.GetLastWin32Error()}");
        }
    }

    /// <summary>Closes the job handle. Any surviving member processes are terminated by the OS as a result.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            if (_jobHandle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(_jobHandle);
                _jobHandle = IntPtr.Zero;
            }
        }
    }

    internal static class NativeMethods
    {
        public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

        public enum JobObjectInfoType
        {
            ExtendedLimitInformation = 9
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long  PerProcessUserTimeLimit;
            public long  PerJobUserTimeLimit;
            public uint  LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint  ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint  PriorityClass;
            public uint  SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS                       IoInfo;
            public UIntPtr                           ProcessMemoryLimit;
            public UIntPtr                           JobMemoryLimit;
            public UIntPtr                           PeakProcessMemoryUsed;
            public UIntPtr                           PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetInformationJobObject(
            IntPtr hJob,
            JobObjectInfoType infoType,
            IntPtr lpJobObjectInfo,
            uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);
    }
}
