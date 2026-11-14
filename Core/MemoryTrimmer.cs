using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace DynamicIsland.Core;

/// <summary>Returns unused working-set pages to the OS after garbage collection.
/// Windows transparently reloads them when they are needed again.</summary>
public static class MemoryTrimmer
{
    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    private static DispatcherTimer? _firstTimer;
    private static DispatcherTimer? _periodicTimer;

    public static void Start()
    {
        // Allow startup and JIT compilation to settle before the first cleanup.
        if (_firstTimer != null || _periodicTimer != null) return;
        _firstTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _firstTimer.Tick += (_, _) =>
        {
            _firstTimer?.Stop();
            _firstTimer = null;
            TrimIfNeeded();
            StartPeriodic();
        };
        _firstTimer.Start();
    }

    private static void StartPeriodic()
    {
        _periodicTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(3) };
        _periodicTimer.Tick += (_, _) => TrimIfNeeded();
        _periodicTimer.Start();
    }

    public static void Stop()
    {
        _firstTimer?.Stop();
        _periodicTimer?.Stop();
        _firstTimer = null;
        _periodicTimer = null;
    }

    public static void TrimIfNeeded()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            if (process.WorkingSet64 < 180L * 1024 * 1024) return;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: false);
            EmptyWorkingSet(Process.GetCurrentProcess().Handle);
        }
        catch { }
    }
}
