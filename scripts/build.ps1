param([string]$NsisCompiler = $env:MAKENSIS)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
$workspace = (Get-Location).Path
New-Item -Force -ItemType Directory -Path build, dist | Out-Null

if (!(Test-Path 'build\wil\include\wil\com.h')) {
    git clone --quiet --depth 1 --branch v1.0.250325.1 https://github.com/microsoft/wil build/wil
    if ($LASTEXITCODE) { throw 'WIL indisponible' }
}
$modelPath = Join-Path $workspace 'build\pose-lightning.onnx'
$modelSha256 = '3C841C042A998DC102A13532DEAFCA5A0554D2FAAAB4511FE977C63C26CC5538'
if (!(Test-Path -LiteralPath $modelPath)) {
    Invoke-WebRequest 'https://huggingface.co/onnx-community/movenet-lightning-web/resolve/b3bef58ab3f8c766f9b6b5310493e623160c2998/onnx/model.onnx' -OutFile "$modelPath.tmp"
    if ((Get-FileHash -LiteralPath "$modelPath.tmp").Hash -ne $modelSha256) { throw 'Empreinte MoveNet invalide' }
    Move-Item -LiteralPath "$modelPath.tmp" -Destination $modelPath
}
if ((Get-FileHash -LiteralPath $modelPath).Hash -ne $modelSha256) { throw 'Empreinte MoveNet invalide' }

& .\scripts\android-build.ps1
cmd /d /c scripts\native-build.cmd
if ($LASTEXITCODE) { throw 'Compilation Media Foundation échouée' }
dotnet publish windows\CadreLink.csproj -c Release -r win-x64 --self-contained true -o build\studio --nologo
if ($LASTEXITCODE) { throw 'Compilation du studio échouée' }
$nugetDirectory = & dotnet nuget locals global-packages --list
$nugetDirectory = $nugetDirectory.Substring($nugetDirectory.IndexOf(':') + 1).Trim()
$ortRoot = Join-Path $nugetDirectory 'microsoft.ml.onnxruntime\1.32.0'
$ortNotice = Get-ChildItem -LiteralPath $ortRoot -File | Where-Object {$_.Name -match '^ThirdPartyNotices'} | Select-Object -First 1
if (!$ortNotice) { throw 'Notices ONNX Runtime manquantes' }
$ortLicense = [IO.File]::ReadAllText((Join-Path $ortRoot 'LICENSE'))
$ortNotices = [IO.File]::ReadAllText($ortNotice.FullName)
[IO.File]::WriteAllText((Join-Path $workspace 'build\ONNX-THIRD-PARTY-NOTICES.txt'),
    $ortLicense + [Environment]::NewLine + $ortNotices, [Text.UTF8Encoding]::new($false))

if (!$NsisCompiler) { $NsisCompiler = (Get-Command makensis.exe -ErrorAction SilentlyContinue).Source }
if (!$NsisCompiler) {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'tauri\NSIS\makensis.exe'),
        (Join-Path $env:ProgramFiles 'NSIS\makensis.exe')
    )
    if (${env:ProgramFiles(x86)}) { $candidates += (Join-Path ${env:ProgramFiles(x86)} 'NSIS\makensis.exe') }
    $NsisCompiler = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (!$NsisCompiler -or !(Test-Path -LiteralPath $NsisCompiler)) {
    throw 'NSIS introuvable. Passe -NsisCompiler avec le chemin de makensis.exe (NSIS 3.11 ou ultérieur).'
}
$pluginDir = Join-Path $workspace 'build\nsis-plugins'
New-Item -Force -ItemType Directory -Path $pluginDir | Out-Null
$plugin = Join-Path $pluginDir 'nsis_tauri_utils.dll'
$pluginSha1 = '6532DA4545864C6EC95F62F27F2199BFD668560B'
if (!(Test-Path -LiteralPath $plugin)) {
    Invoke-WebRequest 'https://github.com/tauri-apps/nsis-tauri-utils/releases/download/nsis_tauri_utils-v0.4.2/nsis_tauri_utils.dll' -OutFile $plugin
}
if ((Get-FileHash -LiteralPath $plugin -Algorithm SHA1).Hash -ne $pluginSha1) { throw 'Empreinte du module NSIS Tauri invalide' }

$project = [xml][IO.File]::ReadAllText((Join-Path $workspace 'windows\CadreLink.csproj'))
$version = $project.Project.PropertyGroup.Version
$setup = Join-Path $workspace 'dist\CadreLink-setup.exe'
$staging = Join-Path $workspace ('build\setup-' + [Guid]::NewGuid().ToString('N') + '.exe')
& $NsisCompiler /INPUTCHARSET UTF8 "/DVERSION=$version" "/DWORKSPACE=$workspace" "/DARTIFACT=$staging" "/DTAURI_PLUGIN_DIR=$pluginDir" (Join-Path $workspace 'scripts\setup.nsi')
if ($LASTEXITCODE) { throw "Création de l’installeur NSIS échouée" }
if (Test-Path -LiteralPath $setup) {
    [IO.File]::Replace($staging, $setup, [NullString]::Value)
} else {
    [IO.File]::Move($staging, $setup)
}
Get-Item -LiteralPath $setup
