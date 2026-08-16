cmake_minimum_required(VERSION 3.24)
project(LocalCamVirtualCamera LANGUAGES CXX)

add_executable(LocalCamVirtualCameraCapabilityProbe
    src/VirtualCameraCapabilityProbe.cpp)

target_compile_features(LocalCamVirtualCameraCapabilityProbe PRIVATE cxx_std_20)
target_compile_definitions(LocalCamVirtualCameraCapabilityProbe PRIVATE
    WINVER=0x0A00
    _WIN32_WINNT=0x0A00
    UNICODE
    _UNICODE)
target_link_libraries(LocalCamVirtualCameraCapabilityProbe PRIVATE
    mf
    mfplat
    mfreadwrite
    mfuuid
    mfsensorgroup
    ole32)

add_executable(LocalCamVirtualCameraRegistrar
    src/VirtualCameraRegistrar.cpp)

target_compile_features(LocalCamVirtualCameraRegistrar PRIVATE cxx_std_20)
target_compile_definitions(LocalCamVirtualCameraRegistrar PRIVATE
    WINVER=0x0A00
    _WIN32_WINNT=0x0A00
    UNICODE
    _UNICODE)
target_link_libraries(LocalCamVirtualCameraRegistrar PRIVATE
    advapi32
    mfplat
    mfsensorgroup
    ole32)
