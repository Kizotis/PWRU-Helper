@echo off
title Build PWRU Helper - Portable EXE
cd /d "%~dp0"
set "DOTNET=C:\Program Files\dotnet\dotnet.exe"

echo Building a single portable PWRU Helper.exe (bundles .NET + all DLLs)...
echo This can take a couple of minutes the first time.
echo.

"%DOTNET%" publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=none

set "OUT=bin\Release\net8.0-windows10.0.19041.0\win-x64\publish"
echo.
if exist "%OUT%\PWRUHelper.exe" (
    echo ============================================================
    echo  DONE.  Your portable app is here:
    echo    %CD%\%OUT%\PWRUHelper.exe
    echo.
    echo  Copy that single .exe anywhere - it needs no install.
    echo ============================================================
    explorer "%OUT%"
) else (
    echo Build failed - see messages above.
)
pause
