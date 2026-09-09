//
// Copyright (C) Microsoft Corporation. All rights reserved.
//
#include "pch.h"
#include "../../../../../include/FrameIpcContract.h"

SimpleFrameGenerator::~SimpleFrameGenerator()
{
    if (m_mappingView != nullptr)
    {
        UnmapViewOfFile(m_mappingView);
    }

    if (m_mapping != nullptr)
    {
        CloseHandle(m_mapping);
    }

    if (m_pipe != INVALID_HANDLE_VALUE)
    {
        CloseHandle(m_pipe);
    }
}

HRESULT SimpleFrameGenerator::Initialize(_In_ IMFMediaType* pMediaType)
{
    RETURN_HR_IF_NULL(E_INVALIDARG, pMediaType);

    RETURN_IF_FAILED(pMediaType->GetGUID(MF_MT_SUBTYPE, &m_subType));
    if (m_subType != MFVideoFormat_RGB32 && m_subType != MFVideoFormat_NV12)
    {
        RETURN_HR_MSG(MF_E_UNSUPPORTED_FORMAT, "Unsupported format: %s", winrt::to_hstring(m_subType).data());
    }
    MFGetAttributeSize(pMediaType, MF_MT_FRAME_SIZE, &m_width, &m_height);

    return S_OK;
}

/*:
   Writes to a buffer representing a 2D image.
   Writes a different constant to each line based on row number and current time.
   Assumes top down image, no negative stride and pBuf points to the begnning of the buffer of length len.
   Param:
   pBuf - pointer to beginning of buffer
   pitch - line length in bytes
   len - length of buffer in bytes
*/
HRESULT SimpleFrameGenerator::CreateFrame(
    _Inout_updates_bytes_(len) BYTE* pBuf,
    _In_ DWORD len,
    _In_ LONG pitch,
    _In_ ULONG rgbMask)
{
    if (m_subType == MFVideoFormat_RGB32)
    {
        DEBUG_MSG(L"RGB32 frames %s\n", winrt::to_hstring(MFVideoFormat_RGB32).data());

        if (TryCopyLocalCamRgb32(pBuf, len, pitch) || TryCopyLocalCamPipe(pBuf, len, pitch))
        {
            return S_OK;
        }

        RETURN_IF_FAILED(_CreateRGB32Frame(pBuf, len, pitch, m_width, m_height, rgbMask));
    }
    else if(m_subType == MFVideoFormat_NV12)
    {
        DEBUG_MSG(L"NV12 frames %s \n", winrt::to_hstring(MFVideoFormat_NV12).data());

        if (TryCopyLocalCamNv12(pBuf, len, pitch) || TryCopyLocalCamPipe(pBuf, len, pitch))
        {
            return S_OK;
        }

        DWORD frameBuffLen = m_width * m_height * 4;
        wil::unique_cotaskmem_ptr<BYTE[]> spBuff = wil::make_unique_cotaskmem_nothrow<BYTE[]>(frameBuffLen);
        RETURN_IF_NULL_ALLOC(spBuff.get());

        RETURN_IF_FAILED(_CreateRGB32Frame(spBuff.get(), frameBuffLen, m_width * 4, m_width, m_height, rgbMask));
        RETURN_IF_FAILED(RGB32ToNV12Frame(spBuff.get(), frameBuffLen, m_width * 4, m_width, m_height, pBuf, len, pitch));
    }
    else
    {
        return MF_E_UNSUPPORTED_FORMAT;
    }

    return S_OK;
}

//////////////////////////////////////////////////
// private

bool SimpleFrameGenerator::EnsureLocalCamMapping()
{
    if (m_mappingView != nullptr)
    {
        return true;
    }

    m_mapping = OpenFileMappingW(FILE_MAP_READ, FALSE, localcam::ipc::kMappingName);
    if (m_mapping == nullptr)
    {
        return false;
    }

    m_mappingView = static_cast<const BYTE*>(MapViewOfFile(m_mapping, FILE_MAP_READ, 0, 0, 0));
    if (m_mappingView == nullptr)
    {
        CloseHandle(m_mapping);
        m_mapping = nullptr;
        return false;
    }

    const auto* ring = reinterpret_cast<const localcam::ipc::FrameRingHeader*>(m_mappingView);
    if (ring->magic != localcam::ipc::kMagic ||
        ring->majorVersion != localcam::ipc::kMajorVersion ||
        ring->slotCount != localcam::ipc::kSlotCount ||
        ring->slotPayloadCapacity < m_width * m_height * 3u / 2u)
    {
        UnmapViewOfFile(m_mappingView);
        CloseHandle(m_mapping);
        m_mappingView = nullptr;
        m_mapping = nullptr;
        return false;
    }

    return true;
}

bool SimpleFrameGenerator::TryCopyLocalCamNv12(BYTE* pBuf, DWORD len, LONG pitch)
{
    const DWORD sourceWidth = m_width;
    const DWORD sourceHeight = m_height;
    const DWORD sourcePayloadLength = sourceWidth * sourceHeight * 3 / 2;

    if (pBuf == nullptr || pitch < static_cast<LONG>(sourceWidth) ||
        m_width != sourceWidth || m_height != sourceHeight ||
        len < static_cast<DWORD>(pitch) * sourceHeight * 3 / 2 ||
        !EnsureLocalCamMapping())
    {
        return false;
    }

    const auto* ring = reinterpret_cast<const localcam::ipc::FrameRingHeader*>(m_mappingView);
    const auto latestSequence = InterlockedCompareExchange64(
        reinterpret_cast<volatile LONG64*>(const_cast<std::int64_t*>(&ring->latestSequence)), 0, 0);
    if (latestSequence <= 0)
    {
        return false;
    }

    const auto slotIndex = static_cast<std::uint64_t>(latestSequence - 1) % ring->slotCount;
    const auto slotBytes = sizeof(localcam::ipc::FrameSlotHeader) + ring->slotPayloadCapacity;
    const auto* slotAddress = m_mappingView + sizeof(localcam::ipc::FrameRingHeader) + (slotIndex * slotBytes);
    const auto* slot = reinterpret_cast<const localcam::ipc::FrameSlotHeader*>(slotAddress);
    const auto sequenceBefore = InterlockedCompareExchange64(
        reinterpret_cast<volatile LONG64*>(const_cast<std::int64_t*>(&slot->sequence)), 0, 0);

    if (sequenceBefore != latestSequence ||
        slot->width != sourceWidth || slot->height != sourceHeight ||
        slot->stride < sourceWidth || slot->pixelFormat != localcam::ipc::kPixelFormatNv12 ||
        slot->payloadLength != sourcePayloadLength || slot->payloadLength > ring->slotPayloadCapacity)
    {
        return false;
    }

    FILETIME currentFileTime{};
    GetSystemTimeAsFileTime(&currentFileTime);
    ULARGE_INTEGER currentTime{};
    currentTime.LowPart = currentFileTime.dwLowDateTime;
    currentTime.HighPart = currentFileTime.dwHighDateTime;
    constexpr ULONGLONG maxFrameAge = 3ull * 10'000'000ull;
    const auto frameTime = static_cast<ULONGLONG>(slot->timestamp100Nanoseconds);
    if (frameTime > currentTime.QuadPart || currentTime.QuadPart - frameTime > maxFrameAge)
    {
        return false;
    }

    const BYTE* source = slotAddress + sizeof(localcam::ipc::FrameSlotHeader);
    for (DWORD row = 0; row < sourceHeight; ++row)
    {
        memcpy(pBuf + (row * pitch), source + (row * slot->stride), sourceWidth);
    }

    const BYTE* sourceUv = source + (slot->stride * sourceHeight);
    BYTE* destinationUv = pBuf + (pitch * sourceHeight);
    for (DWORD row = 0; row < sourceHeight / 2; ++row)
    {
        memcpy(destinationUv + (row * pitch), sourceUv + (row * slot->stride), sourceWidth);
    }

    MemoryBarrier();
    const auto sequenceAfter = InterlockedCompareExchange64(
        reinterpret_cast<volatile LONG64*>(const_cast<std::int64_t*>(&slot->sequence)), 0, 0);
    if (sequenceAfter != sequenceBefore)
    {
        return false;
    }

    m_pipeFrame.resize(sourcePayloadLength);
    for (DWORD row = 0; row < sourceHeight; ++row)
    {
        memcpy(m_pipeFrame.data() + (row * sourceWidth), source + (row * slot->stride), sourceWidth);
    }
    BYTE* cachedUv = m_pipeFrame.data() + (sourceWidth * sourceHeight);
    for (DWORD row = 0; row < sourceHeight / 2; ++row)
    {
        memcpy(cachedUv + (row * sourceWidth), sourceUv + (row * slot->stride), sourceWidth);
    }
    m_pipeTimestamp = slot->timestamp100Nanoseconds;
    return true;
}

bool SimpleFrameGenerator::TryCopyLocalCamRgb32(BYTE* pBuf, DWORD len, LONG pitch)
{
    const DWORD sourceWidth = m_width;
    const DWORD sourceHeight = m_height;
    const DWORD sourcePayloadLength = sourceWidth * sourceHeight * 3 / 2;

    if (pBuf == nullptr || pitch < static_cast<LONG>(sourceWidth * 4) ||
        m_width != sourceWidth || m_height != sourceHeight ||
        len < static_cast<DWORD>(pitch) * sourceHeight ||
        !EnsureLocalCamMapping())
    {
        return false;
    }

    const auto* ring = reinterpret_cast<const localcam::ipc::FrameRingHeader*>(m_mappingView);
    const auto latestSequence = InterlockedCompareExchange64(
        reinterpret_cast<volatile LONG64*>(const_cast<std::int64_t*>(&ring->latestSequence)), 0, 0);
    if (latestSequence <= 0)
    {
        return false;
    }

    const auto slotIndex = static_cast<std::uint64_t>(latestSequence - 1) % ring->slotCount;
    const auto slotBytes = sizeof(localcam::ipc::FrameSlotHeader) + ring->slotPayloadCapacity;
    const auto* slotAddress = m_mappingView + sizeof(localcam::ipc::FrameRingHeader) + (slotIndex * slotBytes);
    const auto* slot = reinterpret_cast<const localcam::ipc::FrameSlotHeader*>(slotAddress);
    const auto sequenceBefore = InterlockedCompareExchange64(
        reinterpret_cast<volatile LONG64*>(const_cast<std::int64_t*>(&slot->sequence)), 0, 0);

    if (sequenceBefore != latestSequence ||
        slot->width != sourceWidth || slot->height != sourceHeight ||
        slot->stride < sourceWidth || slot->pixelFormat != localcam::ipc::kPixelFormatNv12 ||
        slot->payloadLength != sourcePayloadLength || slot->payloadLength > ring->slotPayloadCapacity)
    {
        return false;
    }

    FILETIME currentFileTime{};
    GetSystemTimeAsFileTime(&currentFileTime);
    ULARGE_INTEGER currentTime{};
    currentTime.LowPart = currentFileTime.dwLowDateTime;
    currentTime.HighPart = currentFileTime.dwHighDateTime;
    constexpr ULONGLONG maxFrameAge = 3ull * 10'000'000ull;
    const auto frameTime = static_cast<ULONGLONG>(slot->timestamp100Nanoseconds);
    if (frameTime > currentTime.QuadPart || currentTime.QuadPart - frameTime > maxFrameAge)
    {
        return false;
    }

    const BYTE* source = slotAddress + sizeof(localcam::ipc::FrameSlotHeader);
    const BYTE* sourceUv = source + (slot->stride * sourceHeight);
    for (DWORD row = 0; row < sourceHeight; ++row)
    {
        BYTE* destination = pBuf + (row * pitch);
        const BYTE* sourceY = source + (row * slot->stride);
        const BYTE* sourceUvRow = sourceUv + ((row / 2) * slot->stride);
        for (DWORD column = 0; column < sourceWidth; ++column)
        {
            const int y = static_cast<int>(sourceY[column]) - 16;
            const int u = static_cast<int>(sourceUvRow[column & ~1u]) - 128;
            const int v = static_cast<int>(sourceUvRow[(column & ~1u) + 1]) - 128;
            const int c = max(0, y);
            const auto clampByte = [](int value) -> BYTE
            {
                return static_cast<BYTE>(min(255, max(0, value)));
            };

            destination[column * 4] = clampByte((298 * c + 516 * u + 128) >> 8);
            destination[column * 4 + 1] = clampByte((298 * c - 100 * u - 208 * v + 128) >> 8);
            destination[column * 4 + 2] = clampByte((298 * c + 409 * v + 128) >> 8);
            destination[column * 4 + 3] = 255;
        }
    }

    MemoryBarrier();
    const auto sequenceAfter = InterlockedCompareExchange64(
        reinterpret_cast<volatile LONG64*>(const_cast<std::int64_t*>(&slot->sequence)), 0, 0);
    if (sequenceAfter != sequenceBefore)
    {
        return false;
    }

    m_pipeFrame.resize(sourcePayloadLength);
    for (DWORD row = 0; row < sourceHeight; ++row)
    {
        memcpy(m_pipeFrame.data() + (row * sourceWidth), source + (row * slot->stride), sourceWidth);
    }
    BYTE* cachedUv = m_pipeFrame.data() + (sourceWidth * sourceHeight);
    for (DWORD row = 0; row < sourceHeight / 2; ++row)
    {
        memcpy(cachedUv + (row * sourceWidth), sourceUv + (row * slot->stride), sourceWidth);
    }
    m_pipeTimestamp = slot->timestamp100Nanoseconds;
    return true;
}

bool SimpleFrameGenerator::EnsureLocalCamPipe()
{
    if (m_pipe != INVALID_HANDLE_VALUE)
    {
        return true;
    }

    m_pipe = CreateFileW(
        localcam::ipc::kFramePipePath,
        GENERIC_READ | GENERIC_WRITE,
        0,
        nullptr,
        OPEN_EXISTING,
        0,
        nullptr);
    return m_pipe != INVALID_HANDLE_VALUE;
}

bool SimpleFrameGenerator::ReadPipeExact(BYTE* destination, DWORD length)
{
    DWORD totalRead = 0;
    while (totalRead < length)
    {
        DWORD bytesRead = 0;
        if (!ReadFile(m_pipe, destination + totalRead, length - totalRead, &bytesRead, nullptr) || bytesRead == 0)
        {
            CloseHandle(m_pipe);
            m_pipe = INVALID_HANDLE_VALUE;
            return false;
        }
        totalRead += bytesRead;
    }
    return true;
}

bool SimpleFrameGenerator::ReadLocalCamPipeFrame()
{
    constexpr DWORD responseMagic = 0x3143504C;
    const DWORD expectedPayloadLength = m_width * m_height * 3u / 2u;

    if (!EnsureLocalCamPipe())
    {
        return false;
    }

    const BYTE request = 1;
    DWORD bytesWritten = 0;
    if (!WriteFile(m_pipe, &request, sizeof(request), &bytesWritten, nullptr) || bytesWritten != sizeof(request))
    {
        CloseHandle(m_pipe);
        m_pipe = INVALID_HANDLE_VALUE;
        return false;
    }

    BYTE header[16]{};
    if (!ReadPipeExact(header, sizeof(header)))
    {
        return false;
    }

    DWORD magic = 0;
    DWORD payloadLength = 0;
    memcpy(&magic, header, sizeof(magic));
    memcpy(&payloadLength, header + 4, sizeof(payloadLength));
    LONGLONG responseTimestamp = 0;
    memcpy(&responseTimestamp, header + 8, sizeof(responseTimestamp));
    if (magic != responseMagic || (payloadLength != 0 && payloadLength != expectedPayloadLength))
    {
        CloseHandle(m_pipe);
        m_pipe = INVALID_HANDLE_VALUE;
        return false;
    }
    if (payloadLength == 0)
    {
        m_pipeFrame.clear();
        m_pipeTimestamp = 0;
        return false;
    }

    std::vector<BYTE> candidate(payloadLength);
    if (!ReadPipeExact(candidate.data(), payloadLength))
    {
        return false;
    }

    FILETIME currentFileTime{};
    GetSystemTimeAsFileTime(&currentFileTime);
    ULARGE_INTEGER currentTime{};
    currentTime.LowPart = currentFileTime.dwLowDateTime;
    currentTime.HighPart = currentFileTime.dwHighDateTime;
    constexpr ULONGLONG maxFrameAge = 3ull * 10'000'000ull;
    const auto frameTime = static_cast<ULONGLONG>(responseTimestamp);
    if (frameTime > currentTime.QuadPart || currentTime.QuadPart - frameTime > maxFrameAge)
    {
        return false;
    }

    m_pipeFrame = std::move(candidate);
    m_pipeTimestamp = responseTimestamp;
    return true;
}

bool SimpleFrameGenerator::TryCopyLocalCamPipe(BYTE* pBuf, DWORD len, LONG pitch)
{
    const DWORD sourceWidth = m_width;
    const DWORD sourceHeight = m_height;
    const DWORD expectedPayloadLength = sourceWidth * sourceHeight * 3 / 2;
    if (pBuf == nullptr || m_width != sourceWidth || m_height != sourceHeight)
    {
        return false;
    }

    // A single short pipe read must not flash the animated fallback pattern into
    // an otherwise healthy stream. Keep using the last complete frame while it
    // is still within the same three-second freshness window. A deliberate
    // Clear() response sets the timestamp to zero, so a real disconnect still
    // switches to the fallback immediately.
    if (!ReadLocalCamPipeFrame())
    {
        if (m_pipeFrame.size() != expectedPayloadLength || m_pipeTimestamp <= 0)
        {
            return false;
        }

        FILETIME currentFileTime{};
        GetSystemTimeAsFileTime(&currentFileTime);
        ULARGE_INTEGER currentTime{};
        currentTime.LowPart = currentFileTime.dwLowDateTime;
        currentTime.HighPart = currentFileTime.dwHighDateTime;
        constexpr ULONGLONG maxFrameAge = 3ull * 10'000'000ull;
        const auto frameTime = static_cast<ULONGLONG>(m_pipeTimestamp);
        if (frameTime > currentTime.QuadPart || currentTime.QuadPart - frameTime > maxFrameAge)
        {
            return false;
        }
    }

    if (m_subType == MFVideoFormat_NV12)
    {
        if (pitch < static_cast<LONG>(sourceWidth) ||
            len < static_cast<DWORD>(pitch) * sourceHeight * 3 / 2)
        {
            return false;
        }
        for (DWORD row = 0; row < sourceHeight; ++row)
        {
            memcpy(pBuf + (row * pitch), m_pipeFrame.data() + (row * sourceWidth), sourceWidth);
        }
        const BYTE* sourceUv = m_pipeFrame.data() + (sourceWidth * sourceHeight);
        BYTE* destinationUv = pBuf + (pitch * sourceHeight);
        for (DWORD row = 0; row < sourceHeight / 2; ++row)
        {
            memcpy(destinationUv + (row * pitch), sourceUv + (row * sourceWidth), sourceWidth);
        }
        return true;
    }

    if (m_subType != MFVideoFormat_RGB32 || pitch < static_cast<LONG>(sourceWidth * 4) ||
        len < static_cast<DWORD>(pitch) * sourceHeight)
    {
        return false;
    }

    const BYTE* sourceUv = m_pipeFrame.data() + (sourceWidth * sourceHeight);
    for (DWORD row = 0; row < sourceHeight; ++row)
    {
        BYTE* destination = pBuf + (row * pitch);
        const BYTE* sourceY = m_pipeFrame.data() + (row * sourceWidth);
        const BYTE* sourceUvRow = sourceUv + ((row / 2) * sourceWidth);
        for (DWORD column = 0; column < sourceWidth; ++column)
        {
            const int y = static_cast<int>(sourceY[column]) - 16;
            const int u = static_cast<int>(sourceUvRow[column & ~1u]) - 128;
            const int v = static_cast<int>(sourceUvRow[(column & ~1u) + 1]) - 128;
            const int c = max(0, y);
            const auto clampByte = [](int value) -> BYTE
            {
                return static_cast<BYTE>(min(255, max(0, value)));
            };
            destination[column * 4] = clampByte((298 * c + 516 * u + 128) >> 8);
            destination[column * 4 + 1] = clampByte((298 * c - 100 * u - 208 * v + 128) >> 8);
            destination[column * 4 + 2] = clampByte((298 * c + 409 * v + 128) >> 8);
            destination[column * 4 + 3] = 255;
        }
    }
    return true;
}

HRESULT SimpleFrameGenerator::_CreateRGB32Frame(
    _Inout_updates_bytes_(len) BYTE* pBuf,
    _In_ DWORD len,
    _In_ LONG pitch,
    _In_ DWORD width,
    _In_ DWORD height,
    _In_ ULONG rgbMask )
{
    RETURN_HR_IF_NULL(E_INVALIDARG, pBuf);
    if (len < (abs(pitch) * height ))
    {
        return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
    }

    for (unsigned int r = 0; r < height; r++)
    {
        uint32_t* p = (uint32_t*)(pBuf + (r * pitch));
        for (unsigned int c = 0; c < width; c++)
        {
            // Keep the no-signal fallback static. The upstream sample animated
            // this gradient once per second, which looked like a camera fault.
            *p = 0 & rgbMask;
            p++;
        }
    }

    return S_OK;
}

//////////////////////////////////////////////////
// pixelFormatConverter

void SimpleFrameGenerator::RGB24ToYUY2(int R, int G, int B, BYTE* pY, BYTE* pU, BYTE* pV)
{
    *pY = ((66 * R + 129 * G + 25 * B + 128) >> 8) + 16;
    *pU = ((-38 * R - 74 * G + 112 * B + 128) >> 8) + 128;
    *pV = ((112 * R - 94 * G - 18 * B + 128) >> 8) + 128;
}

void SimpleFrameGenerator::RGB24ToY(int R, int G, int B, BYTE* pY)
{
    *pY = ((66 * R + 129 * G + 25 * B + 128) >> 8) + 16;
}

void SimpleFrameGenerator::RGB32ToNV12(BYTE RGB1[8], BYTE RGB2[8], BYTE* pY1, BYTE* pY2, BYTE* pUV)
{
    RGB24ToYUY2(RGB1[2], RGB1[1], RGB1[0], pY1, pUV, pUV + 1);
    RGB24ToY(RGB1[6], RGB1[5], RGB1[4], pY1 + 1);
    RGB24ToYUY2(RGB2[2], RGB2[1], RGB2[0], pY2, pUV, pUV + 1);
    RGB24ToY(RGB2[6], RGB2[5], RGB2[4], pY2 + 1);
};

//////////////////////////////////////////////////
// FrameFormatConverter

HRESULT SimpleFrameGenerator::RGB32ToNV12Frame(_Inout_updates_bytes_(len) BYTE* pbBuff, ULONG cbBuff, long stride, UINT width, UINT height, BYTE* pbBuffOut, ULONG cbBuffOut, long strideOut)
{
    do
    {
        RETURN_HR_IF(E_UNEXPECTED, width * 4 * height > cbBuff);
        RETURN_HR_IF(E_UNEXPECTED, width * 1.5 * height > cbBuffOut);
        RETURN_HR_IF_NULL(E_INVALIDARG, pbBuff);

        RETURN_HR_IF_NULL(E_INVALIDARG, pbBuffOut);
        for (DWORD h = 0; h < height - 1; h += 2)
        {
            BYTE* pRGB1 = h * stride + pbBuff;
            BYTE* pRGB2 = (h + 1) * stride + pbBuff;
            BYTE* pY1 = h * strideOut + pbBuffOut;
            BYTE* pY2 = (h + 1) * strideOut + pbBuffOut;
            BYTE* pUV = (h / 2 + height) * strideOut + pbBuffOut;

            for (DWORD w = 0; w < width; w += 2)
            {
                RGB32ToNV12(pRGB1, pRGB2, pY1, pY2, pUV);
                pRGB1 += 8;
                pRGB2 += 8;
                pY1 += 2;
                pY2 += 2;
                pUV += 2;
            }
        }
    } while (FALSE);

    return S_OK;
}
