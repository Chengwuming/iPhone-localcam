#include <Windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mfvirtualcamera.h>
#include <wrl/client.h>

#include <cstdlib>
#include <iostream>

using Microsoft::WRL::ComPtr;

namespace
{
bool IsBlackFrame(IMFSample* sample)
{
    ComPtr<IMFMediaBuffer> buffer;
    if (FAILED(sample->ConvertToContiguousBuffer(&buffer)))
    {
        return true;
    }

    BYTE* data = nullptr;
    DWORD currentLength = 0;
    if (FAILED(buffer->Lock(&data, nullptr, &currentLength)))
    {
        return true;
    }

    unsigned long long brightness = 0;
    constexpr DWORD pixelStride = 4;
    constexpr DWORD sampleEveryPixels = 997;
    for (DWORD offset = 0; offset + 2 < currentLength; offset += pixelStride * sampleEveryPixels)
    {
        brightness += data[offset] + data[offset + 1] + data[offset + 2];
    }
    buffer->Unlock();
    return brightness == 0;
}

bool VerifyContinuity(IMFSourceReader* reader, int requestedFrames)
{
    ComPtr<IMFMediaType> mediaType;
    if (FAILED(MFCreateMediaType(&mediaType)) ||
        FAILED(mediaType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video)) ||
        FAILED(mediaType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32)) ||
        FAILED(MFSetAttributeSize(mediaType.Get(), MF_MT_FRAME_SIZE, 1920, 1080)) ||
        FAILED(MFSetAttributeRatio(mediaType.Get(), MF_MT_FRAME_RATE, 30, 1)) ||
        FAILED(reader->SetCurrentMediaType(
            static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM),
            nullptr,
            mediaType.Get())))
    {
        std::wcerr << L"      continuity: unable to select RGB32 1920x1080\n";
        return false;
    }

    int frames = 0;
    int blackFrames = 0;
    int attempts = 0;
    while (frames < requestedFrames && attempts < requestedFrames * 3)
    {
        ++attempts;
        DWORD streamIndex = 0;
        DWORD flags = 0;
        LONGLONG timestamp = 0;
        ComPtr<IMFSample> sample;
        const HRESULT result = reader->ReadSample(
            static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM),
            0,
            &streamIndex,
            &flags,
            &timestamp,
            &sample);
        if (FAILED(result) || (flags & MF_SOURCE_READERF_ENDOFSTREAM) != 0)
        {
            std::wcerr << L"      continuity: ReadSample failed: 0x"
                       << std::hex << static_cast<unsigned long>(result) << L'\n';
            return false;
        }
        if (!sample)
        {
            continue;
        }

        ++frames;
        if (IsBlackFrame(sample.Get()))
        {
            ++blackFrames;
        }
        Sleep(10);
    }

    std::cout << "CONTINUITY frames=" << frames
              << ", fallback-black=" << blackFrames << std::endl;
    return frames == requestedFrames && blackFrames == 0;
}

bool EnumerateVideoCaptureDevices(int continuityFrames)
{
    ComPtr<IMFAttributes> attributes;
    const HRESULT attributesResult = MFCreateAttributes(&attributes, 1);
    if (FAILED(attributesResult))
    {
        std::wcerr << L"MFCreateAttributes failed: 0x" << std::hex
                   << static_cast<unsigned long>(attributesResult) << L'\n';
        return false;
    }

    const HRESULT typeResult = attributes->SetGUID(
        MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE,
        MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
    if (FAILED(typeResult))
    {
        std::wcerr << L"Set video capture source type failed: 0x" << std::hex
                   << static_cast<unsigned long>(typeResult) << L'\n';
        return false;
    }

    IMFActivate** devices = nullptr;
    UINT32 count = 0;
    const HRESULT enumResult = MFEnumDeviceSources(attributes.Get(), &devices, &count);
    if (FAILED(enumResult))
    {
        std::wcerr << L"MFEnumDeviceSources failed: 0x" << std::hex
                   << static_cast<unsigned long>(enumResult) << L'\n';
        return false;
    }

    std::wcout << L"Video capture devices: " << std::dec << count << L'\n';
    bool localCamFound = false;
    bool continuityPassed = continuityFrames <= 0;
    for (UINT32 index = 0; index < count; ++index)
    {
        wchar_t* friendlyName = nullptr;
        UINT32 friendlyNameLength = 0;
        if (SUCCEEDED(devices[index]->GetAllocatedString(
                MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME,
                &friendlyName,
                &friendlyNameLength)))
        {
            std::wcout << L"  [" << index << L"] " << friendlyName << L'\n';
            if (wcsstr(friendlyName, L"LocalCam Camera") != nullptr)
            {
                localCamFound = true;
                ComPtr<IMFMediaSource> mediaSource;
                const HRESULT activateResult = devices[index]->ActivateObject(IID_PPV_ARGS(&mediaSource));
                if (SUCCEEDED(activateResult))
                {
                    ComPtr<IMFSourceReader> reader;
                    const HRESULT readerResult = MFCreateSourceReaderFromMediaSource(
                        mediaSource.Get(),
                        nullptr,
                        &reader);
                    if (SUCCEEDED(readerResult))
                    {
                        if (continuityFrames > 0)
                        {
                            continuityPassed = VerifyContinuity(reader.Get(), continuityFrames);
                            mediaSource->Shutdown();
                            CoTaskMemFree(friendlyName);
                            devices[index]->Release();
                            continue;
                        }

                        DWORD streamIndex = 0;
                        DWORD flags = 0;
                        LONGLONG timestamp = 0;
                        ComPtr<IMFSample> sample;
                        HRESULT sampleResult = S_OK;
                        for (int attempt = 0; attempt < 30 && !sample; ++attempt)
                        {
                            sampleResult = reader->ReadSample(
                                static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM),
                                0,
                                &streamIndex,
                                &flags,
                                &timestamp,
                                &sample);
                            if (FAILED(sampleResult) || (flags & MF_SOURCE_READERF_ENDOFSTREAM) != 0)
                            {
                                break;
                            }
                        }
                        if (SUCCEEDED(sampleResult) && sample)
                        {
                            DWORD bufferCount = 0;
                            sample->GetBufferCount(&bufferCount);
                            std::wcout << L"      activation: frame received, buffers="
                                       << bufferCount << L", timestamp=" << timestamp << L'\n';
                        }
                        else
                        {
                            std::wcerr << L"      activation: ReadSample failed: 0x"
                                       << std::hex << static_cast<unsigned long>(sampleResult)
                                       << L", flags=0x" << flags << L'\n';
                        }
                    }
                    else
                    {
                        std::wcerr << L"      activation: source reader failed: 0x"
                                   << std::hex << static_cast<unsigned long>(readerResult) << L'\n';
                    }
                    mediaSource->Shutdown();
                }
                else
                {
                    std::wcerr << L"      activation failed: 0x" << std::hex
                               << static_cast<unsigned long>(activateResult) << L'\n';
                }
            }
            CoTaskMemFree(friendlyName);
        }
        devices[index]->Release();
    }
    CoTaskMemFree(devices);
    return localCamFound && continuityPassed;
}
}

int wmain(int argc, wchar_t** argv)
{
    int continuityFrames = 0;
    if (argc == 3 && std::wstring_view(argv[1]) == L"--verify-continuity")
    {
        continuityFrames = _wtoi(argv[2]);
        if (continuityFrames <= 0)
        {
            std::wcerr << L"Frame count must be positive.\n";
            return 64;
        }
    }
    else if (argc != 1)
    {
        std::wcerr << L"Usage: LocalCamVirtualCameraCapabilityProbe [--verify-continuity <frames>]\n";
        return 64;
    }

    const HRESULT startupResult = MFStartup(MF_VERSION);
    if (FAILED(startupResult))
    {
        std::wcerr << L"MFStartup failed: 0x" << std::hex << static_cast<unsigned long>(startupResult) << L'\n';
        return 1;
    }

    BOOL supported = FALSE;
    const HRESULT supportResult = MFIsVirtualCameraTypeSupported(
        MFVirtualCameraType_SoftwareCameraSource,
        &supported);
    if (FAILED(supportResult))
    {
        MFShutdown();
        std::wcerr << L"MFIsVirtualCameraTypeSupported failed: 0x"
                   << std::hex << static_cast<unsigned long>(supportResult) << L'\n';
        return 2;
    }

    std::wcout << L"Software virtual camera support: " << (supported ? L"available" : L"unavailable") << L'\n';
    const bool probePassed = EnumerateVideoCaptureDevices(continuityFrames);
    MFShutdown();
    return supported && (continuityFrames <= 0 || probePassed) ? 0 : 3;
}
