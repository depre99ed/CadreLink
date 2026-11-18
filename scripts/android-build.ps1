$ErrorActionPreference='Stop'
Set-Location "$PSScriptRoot\.."
$sdk=if($env:ANDROID_HOME){$env:ANDROID_HOME}else{"$env:USERPROFILE\hub-android-tools\sdk"}
$bt="$sdk\build-tools\37.0.0"; $refs="$sdk\platforms\android-36\android.jar"
$stage='build\android'; New-Item -Force -ItemType Directory -Path "$stage\classes","$stage\r","$stage\dex" | Out-Null
function Run($tool,[string[]]$arguments){ & $tool @arguments; if($LASTEXITCODE){throw "$tool failed ($LASTEXITCODE)"} }
if(!(Test-Path build\zxing.jar)){Invoke-WebRequest https://repo.maven.apache.org/maven2/com/google/zxing/core/3.5.3/core-3.5.3.jar -OutFile build\zxing.jar}
if((Get-FileHash build\zxing.jar).Hash -ne '8D8064C1636FDAEF7189DD9055C7D59950A8940A12F2293956446EC3C109FD82'){throw 'ZXing checksum'}
Run "$bt\aapt2.exe" @('compile','--dir','android\res','-o',"$stage\res.zip")
Run "$bt\aapt2.exe" @('link','--manifest','android\AndroidManifest.xml','-I',$refs,'-A','android\assets','--java',"$stage\r",'-o',"$stage\unsigned.apk","$stage\res.zip")
$src=Get-ChildItem android\src,"$stage\r" -Filter *.java -Recurse | ForEach-Object FullName
Run javac (@('-encoding','UTF-8','-source','8','-target','8','-classpath',"$refs;build\zxing.jar",'-d',"$stage\classes")+$src)
Run jar @('cf',"$stage\app.jar",'-C',"$stage\classes",'.')
Run "$bt\d8.bat" @('--min-api','30','--lib',$refs,'--output',"$stage\dex","$stage\app.jar",'build\zxing.jar')
Run jar @('uf',"$stage\unsigned.apk",'-C',"$stage\dex",'classes.dex')
Run "$bt\zipalign.exe" @('-f','4',"$stage\unsigned.apk","$stage\aligned.apk")
$keys=Join-Path $env:LOCALAPPDATA 'Focale-Build'; New-Item -Force -ItemType Directory -Path $keys | Out-Null
$keyfile=Join-Path $keys 'android.p12';$pwfile=Join-Path $keys 'android.credential'
if(!(Test-Path $keyfile)){
 $keypass=[guid]::NewGuid().ToString('N')+[guid]::NewGuid().ToString('N'); ConvertTo-SecureString $keypass -AsPlainText -Force | ConvertFrom-SecureString | Set-Content $pwfile
 $env:FOCALE_SIGN=$keypass
 Run keytool @('-genkeypair','-keystore',$keyfile,'-storetype','PKCS12','-storepass:env','FOCALE_SIGN','-keypass:env','FOCALE_SIGN','-alias','focale','-keyalg','RSA','-keysize','3072','-validity','10000','-dname','CN=Focale Local')
}else{$env:FOCALE_SIGN=[System.Net.NetworkCredential]::new('',(Get-Content $pwfile | ConvertTo-SecureString)).Password}
try { Run "$bt\apksigner.bat" @('sign','--ks',$keyfile,'--ks-pass','env:FOCALE_SIGN','--out','dist\CadreLink-Android.apk',"$stage\aligned.apk") }finally{Remove-Item Env:\FOCALE_SIGN}
Run "$bt\apksigner.bat" @('verify','--verbose','dist\CadreLink-Android.apk')
