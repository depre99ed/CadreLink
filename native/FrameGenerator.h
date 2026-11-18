#pragma once
#include "Bridge.h"
#include <vector>

class FrameGenerator {
    wil::unique_handle mapping;
    FramePage* page = nullptr;
    std::vector<BYTE> frame, snapshot;
    LONG lastSequence = 0;
    ULONGLONG lastFrameAt = 0;
public:
    UINT32 width=FrameWidth, height=FrameHeight;
    LONGLONG interval = 333333, next = 0;
    ~FrameGenerator() { if (page) UnmapViewOfFile(page); }
    void Reset() { next = 0; lastFrameAt = 0; }
    void Pace();
    const BYTE* ReadFrame(const FramePage* source, ULONGLONG now);
    HRESULT Generate(IMFSample*, REFGUID, IMFSample**);
};