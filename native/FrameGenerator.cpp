#include "pch.h"
#include "FrameGenerator.h"
#include <sddl.h>

void FrameGenerator::Pace() {
    const auto now = MFGetSystemTime();
    if (next > now) Sleep(static_cast<DWORD>((next - now + 9999) / 10000));
    const auto actual = MFGetSystemTime();
    next = next && actual < next + interval ? next + interval : actual + interval;
}

const BYTE* FrameGenerator::ReadFrame(const FramePage* source, ULONGLONG now) {
    const UINT bytes = width * height * 3 / 2;
    frame.resize(bytes);
    if (source) {
        for (int attempt = 0; attempt < 3; ++attempt) {
            const LONG sequence = source->sequence;
            if (sequence & 1) { SwitchToThread(); continue; }
            if (lastFrameAt && sequence == lastSequence) break;
            MemoryBarrier();
            const auto stamp = source->updated;
            const UINT sw = source->width;
            if (!stamp || (sw != FrameWidth && sw != 2 * FrameWidth)) break;
            const UINT sh = sw * 9 / 16;
            snapshot.resize(sw * sh * 3 / 2);
            memcpy(snapshot.data(), source->pixels, snapshot.size());
            MemoryBarrier();
            if (sequence != source->sequence) continue;

            if (sw == width) {
                frame.swap(snapshot);
            } else {
                for (UINT plane = 0; plane < 2; ++plane) {
                    const UINT unit = plane ? 2 : 1;
                    const BYTE* src = snapshot.data() + (plane ? sw * sh : 0);
                    BYTE* dest = frame.data() + (plane ? width * height : 0);
                    for (UINT y = 0; y < height / unit; ++y) {
                        const auto row = src + (y * sh / height) * sw;
                        for (UINT x = 0; x < width / unit; ++x) {
                            const auto pixel = row + (x * sw / width) * unit;
                            *dest++ = *pixel;
                            if (plane) *dest++ = pixel[1];
                        }
                    }
                }
            }
            lastSequence = sequence;
            lastFrameAt = stamp;
            break;
        }
    }
    if ((source && source->hidden) || !lastFrameAt || (now >= lastFrameAt && now - lastFrameAt >= 2500)) {
        lastFrameAt = 0;
        memset(frame.data(), 16, width * height);
        memset(frame.data() + width * height, 128, bytes - width * height);
    }
    return frame.data();
}

HRESULT FrameGenerator::Generate(IMFSample* sample, REFGUID, IMFSample** output) {
    if (!page) {
        PSECURITY_DESCRIPTOR sd = nullptr;
        if (ConvertStringSecurityDescriptorToSecurityDescriptorW(
            L"D:P(A;;GA;;;SY)(A;;GA;;;LS)(A;;GA;;;NS)(A;;GRGW;;;IU)", SDDL_REVISION_1, &sd, nullptr)) {
            SECURITY_ATTRIBUTES attributes{sizeof(attributes), sd, FALSE};
            mapping.reset(CreateFileMappingW(INVALID_HANDLE_VALUE, &attributes, PAGE_READWRITE, 0, sizeof(FramePage), FrameSection));
            LocalFree(sd);
            if (mapping) page = static_cast<FramePage*>(MapViewOfFile(mapping.get(), FILE_MAP_ALL_ACCESS, 0, 0, sizeof(FramePage)));
        }
    }
    const UINT bytes = width * height * 3 / 2;
    const BYTE* pixels = ReadFrame(page, GetTickCount64());
    wil::com_ptr_nothrow<IMFMediaBuffer> buffer;
    RETURN_IF_FAILED(sample->GetBufferByIndex(0, &buffer));
    wil::com_ptr_nothrow<IMF2DBuffer> planar;
    if (SUCCEEDED(buffer->QueryInterface(IID_PPV_ARGS(&planar)))) {
        RETURN_IF_FAILED(planar->ContiguousCopyFrom(pixels, bytes));
    } else {
        BYTE* dest;
        DWORD capacity;
        RETURN_IF_FAILED(buffer->Lock(&dest, &capacity, nullptr));
        if (capacity >= bytes) memcpy(dest, pixels, bytes);
        buffer->Unlock();
        RETURN_HR_IF(MF_E_BUFFERTOOSMALL, capacity < bytes);
    }
    RETURN_IF_FAILED(buffer->SetCurrentLength(bytes));
    *output = sample;
    sample->AddRef();
    return S_OK;
}