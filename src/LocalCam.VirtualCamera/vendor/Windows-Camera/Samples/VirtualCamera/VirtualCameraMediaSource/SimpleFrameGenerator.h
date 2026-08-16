//
// Copyright (C) Microsoft Corporation. All rights reserved.
//

#pragma once
#ifndef SIMPLE_FRAME_GENERATOR_H
#define SIMPLE_FRAME_GENERATOR_H

#include <vector>

class SimpleFrameGenerator
{
public:
    SimpleFrameGenerator() = default;
    ~SimpleFrameGenerator();

    HRESULT Initialize(_In_ IMFMediaType* pMediaType);

    HRESULT CreateFrame(
        _Inout_updates_bytes_(len) BYTE* pBuf,
        _In_ DWORD len,
        _In_ LONG pitch,
        _In_ ULONG rgbMask);

    // pixel format converter
    static void RGB24ToYUY2(int R, int G, int B, BYTE* pY, BYTE* pU, BYTE* pV);
    static void RGB24ToY(int R, int G, int B, BYTE* pY);
    static void RGB32ToNV12(BYTE RGB1[8], BYTE RGB2[8], BYTE* pY1, BYTE* pY2, BYTE* pUV);

    static HRESULT RGB32ToNV12Frame(_Inout_updates_bytes_(len) BYTE* pbBuff, ULONG cbBuff, long stride, UINT width, UINT height, BYTE* pbBuffOut, ULONG cbBuffOut, long strideOut);

private: 
    bool TryCopyLocalCamNv12(BYTE* pBuf, DWORD len, LONG pitch);
    bool TryCopyLocalCamRgb32(BYTE* pBuf, DWORD len, LONG pitch);
    bool TryCopyLocalCamPipe(BYTE* pBuf, DWORD len, LONG pitch);
    bool EnsureLocalCamPipe();
    bool ReadLocalCamPipeFrame();
    bool ReadPipeExact(BYTE* destination, DWORD length);
    bool EnsureLocalCamMapping();

    HRESULT _CreateRGB32Frame(
        _Inout_updates_bytes_(len) BYTE* pBuf,
        _In_ DWORD len,
        _In_ LONG pitch,
        _In_ DWORD width,
        _In_ DWORD height,
        _In_ ULONG rgbMask);

    UINT32 m_width = 0;
    UINT32 m_height = 0;
    GUID m_subType = GUID_NULL;
    HANDLE m_mapping = nullptr;
    const BYTE* m_mappingView = nullptr;
    HANDLE m_pipe = INVALID_HANDLE_VALUE;
    std::vector<BYTE> m_pipeFrame;
    LONGLONG m_pipeTimestamp = 0;

};

#endif

