#pragma once

#include <cstdint>

// Keep this ABI in lockstep with LocalCam.Contracts/FrameIpcContract.cs.
namespace localcam::ipc {

constexpr std::uint32_t kMagic = 0x4D41434C;
constexpr std::uint16_t kMajorVersion = 1;
constexpr std::uint32_t kSlotCount = 4;
constexpr wchar_t kMappingName[] = L"Local\\LocalCam.FrameRing.v1";
constexpr wchar_t kFrameAvailableEventName[] = L"Local\\LocalCam.FrameAvailable.v1";
constexpr wchar_t kFramePipePath[] = L"\\\\.\\pipe\\LocalCam.FramePipe.v1";
constexpr std::uint32_t kPixelFormatNv12 = 0x3231564E;

#pragma pack(push, 1)
struct FrameRingHeader final {
    std::uint32_t magic;
    std::uint16_t majorVersion;
    std::uint16_t minorVersion;
    std::uint32_t slotCount;
    std::uint32_t slotPayloadCapacity;
    std::int64_t latestSequence;
    std::int64_t producerEpoch;
    std::int64_t reserved[4];
};

struct FrameSlotHeader final {
    std::int64_t sequence;
    std::int64_t timestamp100Nanoseconds;
    std::uint32_t width;
    std::uint32_t height;
    std::uint32_t stride;
    std::uint32_t pixelFormat;
    std::uint32_t payloadLength;
    std::uint32_t flags;
    std::int64_t reserved[3];
};
#pragma pack(pop)

static_assert(sizeof(FrameRingHeader) == 64);
static_assert(sizeof(FrameSlotHeader) == 64);

} // namespace localcam::ipc
