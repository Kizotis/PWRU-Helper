@echo off
title Build PWRU Helper - Portable EXE
cd /d "%~dp0"
set "DOTNET=C:\Program Files\dotnet\dotnet.exe"

echo Building a single portable PWRU Helper.exe (bundles .NET + all DLLs)...
echo The exe is around 180 MB - deliberately NOT compressed, see below.
echo This can take a couple of minutes the first time.
echo.

REM  NO -p:EnableCompressionInSingleFile - do not add it back.
REM  Compressing the bundle stops Windows memory-MAPPING the assemblies: they have to be
REM  decompressed into private RAM at every launch. Measured on the same machine, same build:
REM      compressed    74 MB exe  ->  267 MB working set / 153 MB private
REM      uncompressed 179 MB exe  ->  155 MB working set /  95 MB private
REM  That is ~110 MB of RAM handed back to the machine that is also running the game, which is
REM  the whole point of this app being small. It also starts ~130 ms faster warm.
REM  The cost is download size, and only for the portable exe: the MSI re-compresses the payload
REM  into its CAB, so the installer download barely moves.
"%DOTNET%" publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
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
