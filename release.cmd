@echo off
setlocal
cd /d "%~dp0"

:: ============================================================
:: 一键发布脚本：编译 -> 提交推送 -> 上传到运行中的测试虚拟机
:: 用法：release.cmd [版本号]  例：release.cmd 1.2.4
:: 不带参数时自动使用默认版本号 DEFAULT_VERSION
:: ============================================================

set DEFAULT_VERSION=1.2.4
set VERSION=%1
if "%VERSION%"=="" set VERSION=%DEFAULT_VERSION%

set VM_NAME=WB-Master-Test
set VM_DEST=C:\va\latest
set EXE_NAME=WindowsActivationHelper.exe

echo [1/3] 编译 v%VERSION% ...
dotnet build "src\MasActivator\MasActivator.csproj" -c Release -p:Version=%VERSION% -v q -nologo
if errorlevel 1 (
    echo 编译失败，中止发布
    exit /b 1
)

if not exist publish mkdir publish
copy /Y "src\MasActivator\bin\Release\net48\%EXE_NAME%" "publish\%EXE_NAME%" >nul
echo     产物：publish\%EXE_NAME%

echo [2/3] 提交并推送 Gitee ...
git add -A
git commit -m "chore: 发布 v%VERSION%" --quiet 2>nul
if errorlevel 1 echo     无新改动，跳过提交
git push gitee main --quiet
if errorlevel 1 (
    echo 推送 Gitee 失败，中止发布
    exit /b 1
)
echo     已推送 Gitee main

echo [3/3] 上传到虚拟机 %VM_NAME% ...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Copy-VMFile -Name '%VM_NAME%' -SourcePath '%~dp0publish\%EXE_NAME%' -DestinationPath '%VM_DEST%\%EXE_NAME%' -FileSource Host -CreateFullPath -Force"
if errorlevel 1 (
    echo 上传虚拟机失败，检查虚拟机是否运行、集成服务是否开启
    exit /b 1
)
echo     已上传：虚拟机 %VM_DEST%\%EXE_NAME%

echo 发布完成：v%VERSION%
endlocal
