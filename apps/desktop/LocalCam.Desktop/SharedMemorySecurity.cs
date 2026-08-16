using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LocalCam.Desktop;

internal static partial class SharedMemorySecurity
{
    private const uint SddlRevision1 = 1;
    private const uint DaclSecurityInformation = 0x00000004;
    private const string CameraMappingSddl = "D:(A;;GA;;;SY)(A;;GR;;;LS)(A;;GA;;;IU)";

    public static void AllowCameraServiceRead(SafeMemoryMappedFileHandle handle)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                CameraMappingSddl,
                SddlRevision1,
                out var securityDescriptor,
                out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            if (!SetKernelObjectSecurity(handle, DaclSecurityInformation, securityDescriptor))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            _ = LocalFree(securityDescriptor);
        }
    }

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor,
        uint stringSdRevision,
        out nint securityDescriptor,
        out uint securityDescriptorSize);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetKernelObjectSecurity(
        SafeMemoryMappedFileHandle handle,
        uint securityInformation,
        nint securityDescriptor);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
