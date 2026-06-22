@echo off
REM Reassemble the chunked Photo Album installer, verify it, then offer to run it.
setlocal enabledelayedexpansion
cd /d "%~dp0"

if exist PhotoAlbum-Setup.exe del /q PhotoAlbum-Setup.exe

REM Build an ordered "+"-joined list of the numbered parts (excludes .sha256).
set "LIST="
for /f "delims=" %%F in ('dir /b /on "PhotoAlbum-Setup.exe.0*"') do (
    if defined LIST ( set "LIST=!LIST!+%%F" ) else ( set "LIST=%%F" )
)
if not defined LIST (
    echo ERROR: no PhotoAlbum-Setup.exe.### parts found here.
    pause
    exit /b 1
)

echo Reassembling PhotoAlbum-Setup.exe ...
copy /b !LIST! PhotoAlbum-Setup.exe >nul
if errorlevel 1 (
    echo ERROR: could not concatenate parts.
    pause
    exit /b 1
)

REM Integrity check via PowerShell (SHA-256)
for /f "tokens=1" %%H in (PhotoAlbum-Setup.exe.sha256) do set "EXPECTED=%%H"
for /f %%A in ('powershell -NoProfile -Command "(Get-FileHash PhotoAlbum-Setup.exe -Algorithm SHA256).Hash.ToLower()"') do set "ACTUAL=%%A"
if /i "%EXPECTED%"=="%ACTUAL%" (
    echo SHA-256 OK - installer is intact.
) else (
    echo SHA-256 MISMATCH - the file may be corrupt. Aborting.
    del /q PhotoAlbum-Setup.exe
    pause
    exit /b 1
)

echo.
set /p RUN="Run the installer now? [Y/N] "
if /i "%RUN%"=="Y" start "" PhotoAlbum-Setup.exe
endlocal
