#include "pch.h"
#include "Bridge.h"
#undef INITGUID
#include <guiddef.h>
EXTERN_C const CLSID CLSID_CMSH264DecoderMFT;
#include <strmif.h>
#include <codecapi.h>
#include <vector>
#include <algorithm>
#include <cmath>

struct Decoder {
    wil::com_ptr_nothrow<IMFTransform> transform;
    wil::unique_handle mapping;
    FramePage* page = nullptr;
    std::vector<BYTE> inputPixels, visiblePixels, canvas, cropped;
    float framingX=.5f, framingY=.5f, framingZoom=1.f;
    const std::vector<BYTE>& Output() const { return framingZoom > 1.002f ? cropped : canvas; }

    void FrameSubject() {
        if (framingZoom <= 1.002f) return;
        const int cropW = (static_cast<int>(canvasWidth / framingZoom)) & ~1;
        const int cropH = (static_cast<int>(canvasHeight / framingZoom)) & ~1;
        const int left = std::clamp(static_cast<int>(framingX * canvasWidth) - cropW / 2, 0, canvasWidth - cropW) & ~1;
        const int top = std::clamp(static_cast<int>(framingY * canvasHeight) - cropH / 2, 0, canvasHeight - cropH) & ~1;
        cropped.resize(canvas.size());
        // Reuse horizontal coordinates for both channels of NV12 chroma.
        for (int plane = 0; plane < 2; ++plane) {
            const int unit = plane ? 2 : 1;
            const int cols = canvasWidth / unit, rows = canvasHeight / unit;
            const auto offset = plane ? canvasWidth * canvasHeight : 0;
            const BYTE* source = canvas.data() + offset;
            BYTE* destination = cropped.data() + offset;
            for (int y = 0; y < rows; ++y) {
                const BYTE* row = source + (top / unit + y * (cropH / unit) / rows) * canvasWidth + left;
                for (int x = 0, sx = 0, phase = 0; x < cols; ++x) {
                    *destination++ = row[sx * unit];
                    if (plane) *destination++ = row[sx * unit + 1];
                    phase += cropW / unit;
                    if (phase >= cols) { ++sx; phase -= cols; }
                }
            }
        }
    }
    UINT width=0,height=0;
    UINT requestedWidth=0, requestedHeight=0, visibleWidth=0, visibleHeight=0, visibleX=0, visibleY=0;
    bool hidden=false;
    UINT32 stride=0, range=0;
    int rotation=0, canvasWidth=FrameWidth, canvasHeight=FrameHeight;
    LONG64 timestamp=0;
    ~Decoder() { if(page) UnmapViewOfFile(page); }
    void SetAperture(IMFMediaType* type) {
        visibleX = visibleY = 0;
        visibleWidth = width;
        visibleHeight = height;
        MFVideoArea aperture{};
        for (const GUID* key : {&MF_MT_MINIMUM_DISPLAY_APERTURE, &MF_MT_GEOMETRIC_APERTURE}) {
            if (FAILED(type->GetBlob(*key, reinterpret_cast<BYTE*>(&aperture), sizeof(aperture), nullptr))) continue;
            if (aperture.OffsetX.value < 0 || aperture.OffsetY.value < 0 ||
                aperture.Area.cx <= 0 || aperture.Area.cy <= 0) continue;
            UINT x = aperture.OffsetX.value, y = aperture.OffsetY.value;
            UINT w = aperture.Area.cx, h = aperture.Area.cy;
            if ((x | y | w | h) & 1 || x + w > width || y + h > height) continue;
            visibleX = x; visibleY = y; visibleWidth = w; visibleHeight = h;
            return;
        }
        // H.264 can pad the camera's 1080 lines to the coded height of 1088.
        if (requestedWidth <= width && width - requestedWidth < 16 &&
            requestedHeight <= height && height - requestedHeight < 16) {
            visibleWidth = requestedWidth;
            visibleHeight = requestedHeight;
        }
    }
    HRESULT SelectOutput() {
        for(DWORD i=0;;++i) {
            wil::com_ptr_nothrow<IMFMediaType> type;
            RETURN_IF_FAILED(transform->GetOutputAvailableType(0,i,&type));
            GUID subtype{};
            type->GetGUID(MF_MT_SUBTYPE,&subtype);
            if(subtype!=MFVideoFormat_NV12) continue;
            RETURN_IF_FAILED(transform->SetOutputType(0,type.get(),0));
            RETURN_IF_FAILED(MFGetAttributeSize(type.get(),MF_MT_FRAME_SIZE,&width,&height));
            RETURN_HR_IF(MF_E_INVALIDMEDIATYPE,!width || !height || width>4096 || height>4096 || (width & 1) || (height & 1));
            stride = width;
            type->GetUINT32(MF_MT_DEFAULT_STRIDE, &stride);
            range = MFNominalRange_Unknown;
            type->GetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, &range);
            SetAperture(type.get());
            return S_OK;
        }
    }
    HRESULT Init(int w,int h,int fps) {
        requestedWidth=w; requestedHeight=h;
        RETURN_IF_FAILED(CoCreateInstance(CLSID_CMSH264DecoderMFT,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&transform)));
        wil::com_ptr_nothrow<IMFAttributes> attr;
        if(SUCCEEDED(transform->GetAttributes(&attr))) attr->SetUINT32(MF_LOW_LATENCY,TRUE);
        wil::com_ptr_nothrow<ICodecAPI> codec;
        if(SUCCEEDED(transform->QueryInterface(IID_PPV_ARGS(&codec)))) {
            VARIANT low{}; low.vt=VT_BOOL; low.boolVal=VARIANT_TRUE;
            codec->SetValue(&CODECAPI_AVLowLatencyMode,&low);
        }
        wil::com_ptr_nothrow<IMFMediaType> type;
        RETURN_IF_FAILED(MFCreateMediaType(&type));
        type->SetGUID(MF_MT_MAJOR_TYPE,MFMediaType_Video);
        type->SetGUID(MF_MT_SUBTYPE,MFVideoFormat_H264);
        MFSetAttributeSize(type.get(),MF_MT_FRAME_SIZE,w,h);
        MFSetAttributeRatio(type.get(),MF_MT_FRAME_RATE,fps,1);
        type->SetUINT32(MF_MT_INTERLACE_MODE,MFVideoInterlace_Progressive);
        RETURN_IF_FAILED(transform->SetInputType(0,type.get(),0));
        RETURN_IF_FAILED(SelectOutput());
        RETURN_IF_FAILED(transform->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING,0));
        return transform->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM,0);
    }
    void Publish(const BYTE* src,UINT w,UINT h) {
        canvasWidth=w>FrameWidth?FrameWidth*2:FrameWidth;
        canvasHeight=canvasWidth*9/16;
        int byteCount=canvasWidth*canvasHeight*3/2;
        canvas.resize(byteCount);
        if(w==canvasWidth && h==canvasHeight && !rotation) memcpy(canvas.data(),src,byteCount);
        else {
            int rw=rotation%180?h:w, rh=rotation%180?w:h;
            int dw=(std::min)(canvasWidth,canvasHeight*rw/rh)&~1;
            int dh=(std::min)(canvasHeight,canvasWidth*rh/rw)&~1;
            for(int plane=0;plane<2;++plane) {
                int unit=plane?2:1, sw=w/unit, sh=h/unit, tw=dw/unit, th=dh/unit;
                int left=((canvasWidth-dw)/2)&~1, top=(((canvasHeight-dh)/2)&~1)/unit;
                BYTE* dst=canvas.data()+(plane?canvasWidth*canvasHeight:0);
                const BYTE* in=src+(plane?w*h:0);
                memset(dst,plane?128:16,plane?canvasWidth*canvasHeight/2:canvasWidth*canvasHeight);
                for(int y=0;y<th;++y) for(int x=0;x<tw;++x) {
                    int u=x*(rw/unit)/tw, v=y*(rh/unit)/th;
                    int sx=u, sy=v;
                    if(rotation==90){sx=v;sy=sh-1-u;}
                    if(rotation==180){sx=sw-1-u;sy=sh-1-v;}
                    if(rotation==270){sx=sw-1-v;sy=u;}
                    auto to=dst+(top+y)*canvasWidth+left+x*unit;
                    auto from=in+(sy*sw+sx)*unit;
                    to[0]=from[0]; if(plane) to[1]=from[1];
                }
            }
        }
        FrameSubject();
        if(!page) {
            mapping.reset(OpenFileMappingW(FILE_MAP_WRITE,FALSE,FrameSection));
            if(mapping) page=static_cast<FramePage*>(MapViewOfFile(mapping.get(),FILE_MAP_WRITE,0,0,sizeof(FramePage)));
        }
        if(page) {
            LONG seq=page->sequence;
            InterlockedExchange(&page->sequence,seq | 1);
            page->width=canvasWidth;
            if (hidden) {
                memset(page->pixels, 16, canvasWidth * canvasHeight);
                memset(page->pixels + canvasWidth * canvasHeight, 128, byteCount - canvasWidth * canvasHeight);
            } else {
                memcpy(page->pixels, Output().data(), byteCount);
            }
            page->updated=GetTickCount64();
            InterlockedIncrement(&page->sequence);
        }
    }
    HRESULT Drain(int& frames) {
        for(;;) {
            MFT_OUTPUT_STREAM_INFO info{};
            RETURN_IF_FAILED(transform->GetOutputStreamInfo(0,&info));
            wil::com_ptr_nothrow<IMFSample> sample;
            if(!(info.dwFlags&MFT_OUTPUT_STREAM_PROVIDES_SAMPLES)) {
                RETURN_IF_FAILED(MFCreateSample(&sample));
                wil::com_ptr_nothrow<IMFMediaBuffer> mem;
                RETURN_IF_FAILED(MFCreateAlignedMemoryBuffer((std::max)(info.cbSize,static_cast<DWORD>(stride*height*3/2)),info.cbAlignment?info.cbAlignment-1:0,&mem));
                RETURN_IF_FAILED(sample->AddBuffer(mem.get()));
            }
            MFT_OUTPUT_DATA_BUFFER out{0,sample.get(),0,nullptr}; DWORD flags=0;
            HRESULT hr=transform->ProcessOutput(0,1,&out,&flags);
            if(out.pEvents) out.pEvents->Release();
            if(!sample && out.pSample) sample.attach(out.pSample);
            if(hr==MF_E_TRANSFORM_NEED_MORE_INPUT) return S_OK;
            if(hr==MF_E_TRANSFORM_STREAM_CHANGE) {RETURN_IF_FAILED(SelectOutput());continue;}
            RETURN_IF_FAILED(hr);
            RETURN_HR_IF_NULL(E_UNEXPECTED,sample);
            wil::com_ptr_nothrow<IMFMediaBuffer> mem;
            RETURN_IF_FAILED(sample->ConvertToContiguousBuffer(&mem));
            inputPixels.resize(width * height * 3 / 2);
            wil::com_ptr_nothrow<IMF2DBuffer> planar;
            if (SUCCEEDED(mem->QueryInterface(IID_PPV_ARGS(&planar)))) {
                RETURN_IF_FAILED(planar->ContiguousCopyTo(inputPixels.data(), static_cast<DWORD>(inputPixels.size())));
            } else {
                BYTE* data = nullptr;
                DWORD length = 0;
                RETURN_IF_FAILED(mem->Lock(&data, nullptr, &length));
                const bool complete = stride >= width && length >= stride * height * 3 / 2;
                if (complete) {
                    for (UINT row = 0; row < height * 3 / 2; ++row)
                        memcpy(inputPixels.data() + row * width, data + row * stride, width);
                }
                mem->Unlock();
                RETURN_HR_IF(MF_E_BUFFERTOOSMALL, !complete);
            }
            if (range == MFNominalRange_0_255) {
                const UINT luma = width * height;
                for (UINT i = 0; i < luma; ++i) inputPixels[i] = 16 + (219 * inputPixels[i] + 127) / 255;
                for (size_t i = luma; i < inputPixels.size(); ++i) inputPixels[i] = 16 + (224 * inputPixels[i] + 127) / 255;
            }
            if (visibleWidth == width && visibleHeight == height) {
                Publish(inputPixels.data(), width, height);
            } else {
                visiblePixels.resize(visibleWidth * visibleHeight * 3 / 2);
                for (UINT row = 0; row < visibleHeight; ++row) {
                    memcpy(visiblePixels.data() + row * visibleWidth,
                        inputPixels.data() + (row + visibleY) * width + visibleX, visibleWidth);
                }
                const BYTE* chroma = inputPixels.data() + width * height;
                BYTE* visibleChroma = visiblePixels.data() + visibleWidth * visibleHeight;
                for (UINT row = 0; row < visibleHeight / 2; ++row) {
                    memcpy(visibleChroma + row * visibleWidth,
                        chroma + (row + visibleY / 2) * width + visibleX, visibleWidth);
                }
                Publish(visiblePixels.data(), visibleWidth, visibleHeight);
            }
            ++frames;
        }
    }
};
extern "C" __declspec(dllexport) HRESULT DecoderOpen(int w,int h,int fps,Decoder** value) {
    *value=nullptr;
    RETURN_IF_FAILED(MFStartup(MF_VERSION));
    auto d=std::make_unique<Decoder>();
    HRESULT hr=d->Init(w,h,fps);
    if(FAILED(hr)){MFShutdown();return hr;}
    *value=d.release(); return S_OK;
}
extern "C" __declspec(dllexport) void DecoderClose(Decoder* d){if(d){delete d;MFShutdown();}}
extern "C" __declspec(dllexport) HRESULT DecodeFrame(Decoder* d,const BYTE* data,int length,int fps,int rotation,int* frames) {
    *frames=0;
    if(rotation%90 || rotation<0 || rotation>270) return E_INVALIDARG;
    d->rotation=rotation;
    wil::com_ptr_nothrow<IMFMediaBuffer> mem;
    RETURN_IF_FAILED(MFCreateMemoryBuffer(length,&mem));
    BYTE* dst; RETURN_IF_FAILED(mem->Lock(&dst,nullptr,nullptr));
    memcpy(dst,data,length); mem->Unlock(); mem->SetCurrentLength(length);
    wil::com_ptr_nothrow<IMFSample> sample;
    RETURN_IF_FAILED(MFCreateSample(&sample)); sample->AddBuffer(mem.get());
    auto duration=10000000/(std::max)(1,fps);
    sample->SetSampleTime(d->timestamp); sample->SetSampleDuration(duration); d->timestamp+=duration;
    RETURN_IF_FAILED(d->transform->ProcessInput(0,sample.get(),0));
    return d->Drain(*frames);
}
extern "C" __declspec(dllexport) void SetWebcamHidden(BOOL hidden) {
    wil::unique_handle mapping(OpenFileMappingW(FILE_MAP_WRITE, FALSE, FrameSection));
    if (!mapping) return;
    auto page = static_cast<FramePage*>(MapViewOfFile(mapping.get(), FILE_MAP_WRITE, 0, 0, sizeof(FramePage)));
    if (!page) return;
    InterlockedExchange(&page->hidden, hidden != FALSE);
    UnmapViewOfFile(page);
}
extern "C" __declspec(dllexport) void DecoderHide(Decoder* d, BOOL hidden) { d->hidden = hidden != FALSE; }
extern "C" __declspec(dllexport) void DecoderFraming(Decoder* d, float x, float y, float zoom) {
    d->framingX = x; d->framingY = y; d->framingZoom = zoom;
}
extern "C" __declspec(dllexport) BOOL TrackingPreview(Decoder* d, BYTE* gray, int w, int h) {
    if (d->canvas.empty()) return FALSE;
    for (int row = 0; row < h; ++row) {
        const auto source = d->canvas.data() + (row * d->canvasHeight / h) * d->canvasWidth;
        for (int col = 0; col < w; ++col) *gray++ = source[col * d->canvasWidth / w];
    }
    return TRUE;
}
extern "C" __declspec(dllexport) BOOL DecoderOutputSize(Decoder* d, int* width, int* height) {
    if (d->canvas.empty()) return FALSE;
    *width = d->canvasWidth;
    *height = d->canvasHeight;
    return TRUE;
}
extern "C" __declspec(dllexport) BOOL PosePreview(Decoder* d, BYTE* rgb, int size, int pictureHeight) {
    if (d->canvas.empty() || size < 1 || pictureHeight < 1 || pictureHeight > size) return FALSE;
    const BYTE* yPlane = d->canvas.data();
    const BYTE* uv = yPlane + d->canvasWidth * d->canvasHeight;
    const int top = (size - pictureHeight) / 2;
    memset(rgb, 0, static_cast<size_t>(size) * size * 4);
    rgb += top * size * 4;
    auto clip = [](int value) { return static_cast<BYTE>(std::clamp(value >> 8, 0, 255)); };
    for (int row = 0; row < pictureHeight; ++row) {
        const int y = row * d->canvasHeight / pictureHeight;
        const BYTE* luma = yPlane + y * d->canvasWidth;
        const BYTE* chroma = uv + (y / 2) * d->canvasWidth;
        for (int column = 0; column < size; ++column) {
            const int x = column * d->canvasWidth / size;
            const int c = x & ~1;
            const int l = 298 * (luma[x] - 16), u = chroma[c] - 128, v = chroma[c + 1] - 128;
            *rgb++ = clip(l + 459 * v);
            *rgb++ = clip(l - 55 * u - 136 * v);
            *rgb++ = clip(l + 541 * u);
            *rgb++ = 255;
        }
    }
    return TRUE;
}
extern "C" __declspec(dllexport) BOOL Preview(Decoder* d,BYTE* bgra,int w,int h) {
    if(d->canvas.empty()) return FALSE;
    const BYTE* y=d->Output().data(); const BYTE* uv=y+d->canvasWidth*d->canvasHeight;
    auto clamp=[](int c){return (BYTE)std::clamp(c>>8,0,255);};
    for(int j=0;j<h;++j) for(int i=0;i<w;++i) {
        int x=i*d->canvasWidth/w, row=j*d->canvasHeight/h, offset=(row/2)*d->canvasWidth+(x&~1);
        int l=298*(y[row*d->canvasWidth+x]-16), u=uv[offset]-128,v=uv[offset+1]-128;
        *bgra++=clamp(l+541*u); *bgra++=clamp(l-55*u-136*v); *bgra++=clamp(l+459*v); *bgra++=255;
    }
    return TRUE;
}
