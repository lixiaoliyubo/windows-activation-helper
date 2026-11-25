@echo off
setlocal
cd /d "%~dp0"

dotnet build "src\MasActivator\MasActivator.csproj" -c Release --nologo
if errorlevel 1 (
    echo 编译失败
    exit /b 1
)

if not exist publish mkdir publish
copy /Y "src\MasActivator\bin\Release\net48\WindowsActivationHelper.exe" "publish\WindowsActivationHelper.exe" >nul
echo 编译完成：publish\WindowsActivationHelper.exe
