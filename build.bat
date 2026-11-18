@echo off
cd /d "%~dp0"
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set WM=%WINDIR%\System32\WinMetadata
rem taskkill nao alcanca o app escondido na bandeja
powershell -NoProfile -ExecutionPolicy Bypass -File tools\close-app.ps1
powershell -NoProfile -Command "Get-Process TurzxScreen -ErrorAction SilentlyContinue | ForEach-Object { [void]$_.WaitForExit(10000) }"
"%FW%\csc.exe" /nologo /codepage:65001 /target:winexe /win32icon:app.ico /optimize+ /out:TurzxScreen.new.exe ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Management.dll /r:System.Web.Extensions.dll ^
  /r:"%FW%\System.Runtime.dll" /r:"%FW%\System.Threading.Tasks.dll" /r:"%FW%\System.Runtime.WindowsRuntime.dll" ^
  /r:"%FW%\System.Runtime.InteropServices.WindowsRuntime.dll" /r:"%FW%\System.IO.dll" ^
  /r:"%WM%\Windows.Foundation.winmd" /r:"%WM%\Windows.UI.winmd" /r:"%WM%\Windows.ApplicationModel.winmd" ^
  /r:"%WM%\Windows.Storage.winmd" /r:"%WM%\Windows.Media.winmd" ^
  src\*.cs
if errorlevel 1 (echo FALHOU & exit /b 1)
rem Os hooks do Claude Code podem estar usando o .exe: renomear funciona mesmo com ele em execucao
del /q TurzxScreen.old.exe >nul 2>&1
if exist TurzxScreen.exe ren TurzxScreen.exe TurzxScreen.old.exe
ren TurzxScreen.new.exe TurzxScreen.exe
if errorlevel 1 (echo FALHOU ao trocar o executavel & exit /b 1)
del /q TurzxScreen.old.exe >nul 2>&1
echo OK: TurzxScreen.exe
