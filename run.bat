@echo off
setlocal

REM ================================================================
REM  YES24 eBook Dumper - StartupHook launcher (ASCII only)
REM ================================================================

set "YES24_INSTALL_DIR=C:\Program Files\YES24eBook"
set "YES24_EXE_NAME=YES24eBook.exe"

set "YES24_DUMP_PATH=%~dp0dump"

set "HOOK_DLL=%~dp0YES24Dumper\bin\Release\net8.0-windows\YES24Dumper.dll"

if not exist "%HOOK_DLL%" (
    echo [!] Hook DLL not found: %HOOK_DLL%
    echo     Build first:
    echo         dotnet build YES24Dumper\YES24Dumper.csproj -c Release
    pause
    exit /b 1
)

if not exist "%YES24_INSTALL_DIR%\%YES24_EXE_NAME%" (
    echo [!] EXE not found: %YES24_INSTALL_DIR%\%YES24_EXE_NAME%
    echo     Edit YES24_INSTALL_DIR at top of this bat if install path differs.
    pause
    exit /b 1
)

if not exist "%YES24_DUMP_PATH%" mkdir "%YES24_DUMP_PATH%"

set "DOTNET_STARTUP_HOOKS=%HOOK_DLL%"

echo [+] Install dir : %YES24_INSTALL_DIR%
echo [+] EXE         : %YES24_EXE_NAME%
echo [+] Dump path   : %YES24_DUMP_PATH%
echo [+] Hook DLL    : %HOOK_DLL%
echo.
echo [*] Open a book in the viewer. Extraction is automatic.
echo.

pushd "%YES24_INSTALL_DIR%"
start "" "%YES24_EXE_NAME%"
popd

endlocal
