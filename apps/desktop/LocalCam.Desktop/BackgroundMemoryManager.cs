using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LocalCam.Desktop;

internal static partial class BackgroundMemoryManager
{
    public static void TrimAfterUiRelease()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

            using var process = Process.GetCurrentProcess();
            _ = EmptyWorkingSet(process.Handle);
        }
        catch
        {
            // Memory trimming is an optimization. It must never affect background camera availability.
        }
    }

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyWorkingSet(nint process);
}
