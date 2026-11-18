#include "pch.h"
extern "C" __declspec(dllexport) HRESULT CameraSetup(BOOL remove) {
    HRESULT init = CoInitializeEx(nullptr,COINIT_MULTITHREADED);
    if(FAILED(init) && init!=RPC_E_CHANGED_MODE) return init;
    HRESULT hr = MFStartup(MF_VERSION);
    if(SUCCEEDED(hr)) {
        wil::com_ptr_nothrow<IMFVirtualCamera> camera;
        wchar_t sourceId[39];
        StringFromGUID2(CLSID_VCam, sourceId, ARRAYSIZE(sourceId));
        hr = MFCreateVirtualCamera(MFVirtualCameraType_SoftwareCameraSource,MFVirtualCameraLifetime_System,
            MFVirtualCameraAccess_AllUsers,L"CadreLink",sourceId,nullptr,0,&camera);
        if(SUCCEEDED(hr)) hr = remove ? camera->Remove() : camera->Start(nullptr);
        if(camera) camera->Shutdown();
        camera.reset();
        MFShutdown();
    }
    if(SUCCEEDED(init)) CoUninitialize();
    return hr;
}