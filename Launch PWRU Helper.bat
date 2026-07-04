@echo off
title PWRU Helper launcher
cd /d "%~dp0"

set "DOTNET=C:\Program Files\dotnet\dotnet.exe"
set "EXE=bin\Debug\net8.0-windows10.0.19041.0\PWRUHelper.exe"

rem Build only if the exe is missing (first run), otherwise just launch fast.
if not exist "%EXE%" (
    echo Building PWRU Helper for the first time...
    "%DOTNET%" build -c Debug -nologo
)

if exist "%EXE%" (
    start "" "%EXE%"
) else (
    echo Build failed - see messages above.
    pause
)
