@echo off
cd /d "%~dp0"
set PATH=C:\msys64\mingw64\bin;%USERPROFILE%\.cargo\bin;%PATH%
cargo build --release -p ffi
exit /b %ERRORLEVEL%
