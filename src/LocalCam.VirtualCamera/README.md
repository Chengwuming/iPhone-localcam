# LocalCam.VirtualCamera

This native Windows Media Foundation module exposes `LocalCam Camera` to third-party
applications. It is intentionally built separately from the .NET solution with MSVC.

The current development POC contains:

1. A user-mode Media Foundation media-source DLL registered through `IMFVirtualCamera`.
2. An NV12 producer/consumer reader for the versioned shared-memory contract in `include/`.
3. A per-user install/remove helper that asks for elevation only when Windows requires it.
4. A static black fallback used only when no complete LocalCam frame is available.

`src/VirtualCameraCapabilityProbe.cpp` is a read-only probe for the Windows 11
`MFVirtualCameraType_SoftwareCameraSource` capability. It does not register a device.

`src/VirtualCameraRegistrar.cpp` is the LocalCam-owned install/remove and session utility.
`--install` registers the Media Source in HKLM. Desktop uses `--run-session` to hold a
current-user, session-lifetime camera named `LocalCam Camera`; it disappears when the
session process exits. Commands that write or remove HKLM registration still require
elevation and explicit install/uninstall confirmation.

The Media Source baseline comes from Microsoft's official
`Windows-Camera/Samples/VirtualCamera` repository under `vendor/Windows-Camera`.
Its NV12 generator now reads the newest stable frame from
`Local\\LocalCam.FrameRing.v1`. It accepts fresh 640×480 NV12 slots only and falls
back to the blue generator when the mapping is absent, the ABI is invalid, the phone
disconnects, or the newest timestamp is more than three seconds old.

Windows Camera CaptureService may isolate named kernel objects created by the desktop
user. The source therefore also supports a local request/response pipe at
`\\.\\pipe\\LocalCam.FramePipe.v1`. Shared memory remains the first attempt; the pipe
returns only the newest NV12 frame and never queues historical frames. The pipe ACL
allows the current user and SYSTEM full access and grants LocalService read/write only.

Required build environment: Visual Studio Build Tools C++ workload, Windows 11 SDK with
`mfvirtualcamera.h`, and Media Foundation libraries including `mfsensorgroup.lib`.
