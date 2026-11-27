@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

:: ============================================================
:: 一键发布脚本（Windows 批处理）
::   1) 按指定版本号编译 Release 单文件 exe，产物拷进 publish
::   2) 提交并推送所有远端仓库（gitee 必成功，其余 best-effort）
::
:: 用法：release.bat [版本号]
::   例：release.bat 1.2.5      不带参数则用下方 DEFAULT_VERSION
:: ============================================================

set DEFAULT_VERSION=1.2.4
set EXE_NAME=WindowsActivationHelper.exe
set PROJECT=src\MasActivator\MasActivator.csproj

set VERSION=%1
if "%VERSION%"=="" set VERSION=%DEFAULT_VERSION%

echo ============================================
echo  Windows 一键激活助手 - 发布 v%VERSION%
echo ============================================

echo.
echo [1/2] 编译 ...
dotnet build "%PROJECT%" -c Release -p:Version=%VERSION% -v q -nologo
if errorlevel 1 goto :fail_build

if not exist publish mkdir publish
copy /Y "src\MasActivator\bin\Release\net48\%EXE_NAME%" "publish\%EXE_NAME%" >nul
if errorlevel 1 goto :fail_copy
for %%F in ("publish\%EXE_NAME%") do echo     产物：publish\%%~nxF  ^(%%~zF 字节^)

echo.
echo [2/2] 提交并推送 ...
git add -A
git commit -m "chore: 发布 v%VERSION%" --quiet 2>nul
if errorlevel 1 (
    echo     无新改动，跳过提交
) else (
    echo     已提交本地改动
)

git push gitee main --quiet
if errorlevel 1 goto :fail_push
echo     已推送 gitee/main

for /f "tokens=1" %%R in ('git remote') do (
    if /i not "%%R"=="gitee" (
        git push %%R main --quiet
        if errorlevel 1 (
            echo     [警告] %%R 推送失败，已跳过
        ) else (
            echo     已推送 %%R/main
        )
    )
)

echo.
echo 发布完成：v%VERSION%
endlocal
exit /b 0

:fail_build
echo 编译失败，中止发布
endlocal
exit /b 1

:fail_copy
echo 产物拷贝失败，检查 bin\Release\net48 下是否存在 %EXE_NAME%
endlocal
exit /b 1

:fail_push
echo 推送 gitee 失败，中止发布
endlocal
exit /b 1
