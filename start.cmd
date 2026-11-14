@echo off
setlocal
cd /d "%~dp0"

if exist "dist\WinOcrApi.exe" (
    "dist\WinOcrApi.exe" %*
    goto :eof
)

echo [INFO] dist\WinOcrApi.exe not found, falling back to dotnet run
dotnet run --project "%~dp0WinOcrApi.csproj" -c Release -- %*
