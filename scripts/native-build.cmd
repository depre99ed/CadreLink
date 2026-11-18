@echo off
setlocal
cd /d "%~dp0.."
for /f "usebackq tokens=*" %%v in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "vsInstall=%%v"
call "%vsInstall%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
if not exist build\native mkdir build\native
"%WindowsSdkDir%bin\%WindowsSDKVersion%x64\fxc.exe" /nologo /T ps_2_0 /E main /Fo windows\Assets\Glass.ps windows\Assets\Glass.hlsl || exit /b 1
pushd build\native
cl /MP4 /nologo /utf-8 /EHsc /std:c++20 /O2 /MT /LD /DUNICODE /D_UNICODE /DNDEBUG /I"..\wil\include" ..\..\native\*.cpp /link /OUT:CadreLinkCamera.dll /DEF:..\..\native\VCamSampleSource.def mfplat.lib mf.lib mfuuid.lib mfreadwrite.lib ole32.lib oleaut32.lib advapi32.lib propsys.lib shlwapi.lib strmiids.lib windowsapp.lib user32.lib wmcodecdspuuid.lib uuid.lib
exit /b %errorlevel%
