@echo off
title Build PWRU Helper - MSI Installer
cd /d "%~dp0"
set "DOTNET=C:\Program Files\dotnet\dotnet.exe"
set "WIX=%USERPROFILE%\.dotnet\tools\wix.exe"

REM ---- version: keep in sync with <Version> in PWRUHelper.csproj ----
set "VERSION=0.7.0"

echo ============================================================
echo  Building the PWRU Helper MSI installer (v%VERSION%)
echo ============================================================
echo.

REM 1) One-time: install the free WiX v5 toolset if it's missing.
if not exist "%WIX%" (
    echo WiX toolset not found - installing it once...
    "%DOTNET%" tool install --global wix --version 5.0.2
    "%WIX%" extension add -g WixToolset.UI.wixext/5.0.2
)

REM 2) Publish the single-file exe (bundles .NET + all DLLs).
echo Publishing the portable exe...
"%DOTNET%" publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=none
if errorlevel 1 goto :fail

set "EXE=bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\PWRUHelper.exe"
if not exist "%EXE%" goto :fail

REM 3) Build the MSI from installer\Product.wxs.
echo.
echo Building the MSI...
"%WIX%" build "installer\Product.wxs" -ext WixToolset.UI.wixext -arch x64 ^
  -d Version=%VERSION%.0 ^
  -d ExeFile="%EXE%" ^
  -d IconFile="assets\icon.ico" ^
  -d LicenseFile="installer\license.rtf" ^
  -o "installer\PWRUHelper-%VERSION%-setup.msi"
if errorlevel 1 goto :fail

echo.
echo ============================================================
echo  DONE.  Your installer is here:
echo    %CD%\installer\PWRUHelper-%VERSION%-setup.msi
echo ============================================================
explorer "installer"
pause
goto :eof

:fail
echo.
echo Build FAILED - see the messages above.
pause
