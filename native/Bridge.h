#pragma once
#include <windows.h>
#include <cstdint>

constexpr int FrameWidth = 1920, FrameHeight = 1080;
constexpr int FrameBytes = FrameWidth * FrameHeight * 3 / 2;
// The service owns the paging-file-backed section; the desktop only opens it.
constexpr wchar_t FrameSection[] = L"Global\\CadreLinkVideo-14e7c1d2-a787-4c88-9817-f2f49f633b8e";
struct FramePage {
    volatile LONG sequence;
    ULONGLONG updated;
    LONG width;
    volatile LONG hidden;
    BYTE pixels[FrameBytes * 4];
};
