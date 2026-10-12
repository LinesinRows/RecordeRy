using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RecordeRy.Core;

/// <summary>
/// Alt süreçleri (ffmpeg) "kill on close" bir Windows job object'ine ekler.
/// Uygulama nasıl kapanırsa kapansın (çökme, Görev Yöneticisi'nden
/// sonlandırma) job handle'ı kapanır ve Windows alt süreçleri de sonlandırır.
/// Aksi halde ffmpeg arka planda sahipsiz kalıp yazmaya devam ediyordu.
/// </summary>
public static class ChildProcessTracker
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
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
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationStruct
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(
        IntPtr job,
        int infoClass,
        ref JobObjectExtendedLimitInformationStruct info,
        uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    // Uygulama ömrü boyunca açık kalır; süreç bitince Windows kapatır.
    private static readonly Lazy<IntPtr> Job = new(CreateKillOnCloseJob);

    private static IntPtr CreateKillOnCloseJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);

        if (job == IntPtr.Zero)
            return IntPtr.Zero;

        var info = new JobObjectExtendedLimitInformationStruct
        {
            BasicLimitInformation =
            {
                LimitFlags = JobObjectLimitKillOnJobClose
            }
        };

        SetInformationJobObject(
            job,
            JobObjectExtendedLimitInformation,
            ref info,
            (uint)Marshal.SizeOf<JobObjectExtendedLimitInformationStruct>());

        return job;
    }

    public static void Track(Process process)
    {
        try
        {
            var job = Job.Value;

            if (job != IntPtr.Zero &&
                !AssignProcessToJobObject(job, process.Handle))
            {
                DiagnosticsLog.Write(
                    $"Alt süreç job'a eklenemedi: {Marshal.GetLastWin32Error()}");
            }
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write($"Alt süreç job'a eklenemedi: {ex.Message}");
        }
    }
}
