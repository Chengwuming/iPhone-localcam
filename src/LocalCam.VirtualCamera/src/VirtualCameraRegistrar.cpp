#include <Windows.h>
#include <mfapi.h>
#include <mfvirtualcamera.h>
#include <wrl/client.h>

#include <filesystem>
#include <iostream>
#include <string>

using Microsoft::WRL::ComPtr;

namespace
{
constexpr wchar_t kSourceClsid[] = L"{7B89B92E-FE71-42D0-8A41-E137D06EA184}";
constexpr wchar_t kFriendlyName[] = L"LocalCam Camera";
constexpr wchar_t kRegistryPath[] =
    L"Software\\Classes\\CLSID\\{7B89B92E-FE71-42D0-8A41-E137D06EA184}\\InProcServer32";

void ThrowIfFailed(HRESULT result, const wchar_t* operation)
{
    if (FAILED(result))
    {
        std::wcerr << operation << L" failed: 0x" << std::hex
                   << static_cast<unsigned long>(result) << L'\n';
        throw result;
    }
}

void SetRegistryString(HKEY key, const wchar_t* name, const std::wstring& value)
{
    const auto bytes = static_cast<DWORD>((value.size() + 1) * sizeof(wchar_t));
    const LSTATUS result = RegSetValueExW(
        key,
        name,
        0,
        REG_SZ,
        reinterpret_cast<const BYTE*>(value.c_str()),
        bytes);
    if (result != ERROR_SUCCESS)
    {
        ThrowIfFailed(HRESULT_FROM_WIN32(result), L"RegSetValueExW");
    }
}

void RegisterMediaSource(const std::filesystem::path& dllPath)
{
    if (!std::filesystem::is_regular_file(dllPath))
    {
        std::wcerr << L"Media Source DLL not found: " << dllPath.c_str() << L'\n';
        throw HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
    }

    HKEY rawKey = nullptr;
    const LSTATUS result = RegCreateKeyExW(
        HKEY_LOCAL_MACHINE,
        kRegistryPath,
        0,
        nullptr,
        REG_OPTION_NON_VOLATILE,
        KEY_WRITE,
        nullptr,
        &rawKey,
        nullptr);
    if (result != ERROR_SUCCESS)
    {
        ThrowIfFailed(HRESULT_FROM_WIN32(result), L"Register Media Source (run elevated)");
    }

    try
    {
        SetRegistryString(rawKey, nullptr, std::filesystem::absolute(dllPath).wstring());
        SetRegistryString(rawKey, L"ThreadingModel", L"Both");
    }
    catch (...)
    {
        RegCloseKey(rawKey);
        throw;
    }
    RegCloseKey(rawKey);
}

void UnregisterMediaSource()
{
    const LSTATUS result = RegDeleteTreeW(HKEY_LOCAL_MACHINE, kRegistryPath);
    if (result != ERROR_SUCCESS && result != ERROR_FILE_NOT_FOUND)
    {
        ThrowIfFailed(HRESULT_FROM_WIN32(result), L"Unregister Media Source (run elevated)");
    }
}

ComPtr<IMFVirtualCamera> CreateCamera(MFVirtualCameraLifetime lifetime)
{
    ComPtr<IMFVirtualCamera> camera;
    ThrowIfFailed(
        MFCreateVirtualCamera(
            MFVirtualCameraType_SoftwareCameraSource,
            lifetime,
            MFVirtualCameraAccess_CurrentUser,
            kFriendlyName,
            kSourceClsid,
            nullptr,
            0,
            &camera),
        L"MFCreateVirtualCamera");
    return camera;
}

int Install(const std::filesystem::path& dllPath)
{
    RegisterMediaSource(dllPath);
    std::wcout << L"LocalCam media source registered successfully.\n";
    return 0;
}

int RemoveCamera(MFVirtualCameraLifetime lifetime)
{
    auto camera = CreateCamera(lifetime);
    const HRESULT removeResult = camera->Remove();
    if (FAILED(removeResult) && removeResult != HRESULT_FROM_WIN32(ERROR_NOT_FOUND))
    {
        ThrowIfFailed(removeResult, L"IMFVirtualCamera::Remove");
    }
    ThrowIfFailed(camera->Shutdown(), L"IMFVirtualCamera::Shutdown");
    return 0;
}

int RunSession()
{
    auto camera = CreateCamera(MFVirtualCameraLifetime_Session);
    ThrowIfFailed(camera->Start(nullptr), L"IMFVirtualCamera::Start session");
    std::cout << "READY" << std::endl;
    std::string command;
    std::getline(std::cin, command);
    ThrowIfFailed(camera->Shutdown(), L"IMFVirtualCamera::Shutdown session");
    return 0;
}

int Remove()
{
    RemoveCamera(MFVirtualCameraLifetime_System);
    UnregisterMediaSource();
    std::wcout << kFriendlyName << L" removed successfully.\n";
    return 0;
}
}

int wmain(int argc, wchar_t** argv)
{
    if (argc < 2)
    {
        std::wcerr << L"Usage:\n"
                   << L"  LocalCamVirtualCameraRegistrar --install <MediaSource.dll>\n"
                   << L"  LocalCamVirtualCameraRegistrar --run-session\n"
                   << L"  LocalCamVirtualCameraRegistrar --remove-legacy-system\n"
                   << L"  LocalCamVirtualCameraRegistrar --remove\n";
        return 64;
    }

    const HRESULT comResult = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(comResult))
    {
        std::wcerr << L"CoInitializeEx failed: 0x" << std::hex
                   << static_cast<unsigned long>(comResult) << L'\n';
        return 1;
    }

    int exitCode = 1;
    try
    {
        ThrowIfFailed(MFStartup(MF_VERSION), L"MFStartup");
        const std::wstring command = argv[1];
        if (command == L"--install" && argc == 3)
        {
            exitCode = Install(argv[2]);
        }
        else if (command == L"--run-session" && argc == 2)
        {
            exitCode = RunSession();
        }
        else if (command == L"--remove-legacy-system" && argc == 2)
        {
            exitCode = RemoveCamera(MFVirtualCameraLifetime_System);
        }
        else if (command == L"--remove" && argc == 2)
        {
            exitCode = Remove();
        }
        else
        {
            std::wcerr << L"Invalid arguments.\n";
            exitCode = 64;
        }
        MFShutdown();
    }
    catch (HRESULT)
    {
        MFShutdown();
    }

    CoUninitialize();
    return exitCode;
}
