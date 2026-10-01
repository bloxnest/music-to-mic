@echo off
rem Builds and runs the self-tests. The routing tests play quiet tones on the digital output (S/PDIF), which
rem normally has nothing plugged in, so nothing is heard. They never touch Roblox.
rem No arguments = every test except the long run. Or name parts: mixer drift settings devices routing window long
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist bin mkdir bin
set "FW=%CSC:\csc.exe=%"

"%CSC%" /nologo /target:exe /optimize+ /unsafe /out:bin\ToneApp.exe ^
    ..\src\Audio\CoreAudio.cs ..\src\Audio\Streams.cs ..\src\Audio\Devices.cs ToneApp.cs || exit /b 1

"%CSC%" /nologo /target:exe /optimize+ /unsafe /codepage:65001 /main:MusicToMic.Tests.SelfTest /out:bin\SelfTest.exe ^
    /win32manifest:..\app.manifest /win32icon:..\assets\icon.ico ^
    /resource:..\src\MainWindow.xaml,MainWindow.xaml ^
    /resource:..\assets\tray-on.ico,MusicToMic.tray-on.ico /resource:..\assets\tray-off.ico,MusicToMic.tray-off.ico ^
    /resource:..\assets\tray-warn.ico,MusicToMic.tray-warn.ico ^
    /r:System.dll /r:System.Core.dll /r:System.Xaml.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
    /r:"%FW%\WPF\WindowsBase.dll" /r:"%FW%\WPF\PresentationCore.dll" /r:"%FW%\WPF\PresentationFramework.dll" ^
    /recurse:..\src\*.cs SelfTest.cs || exit /b 1

bin\SelfTest.exe %*
