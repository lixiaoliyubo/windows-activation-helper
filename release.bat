@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

:: ============================================================
:: Windows Activation Helper - release script
::   1) bump/verify version, build Release, copy exe + version.txt to publish
::   2) commit all changes and push to every configured remote
::      (gitee + github); if a remote rejects the push because it has
::      newer commits, pull --rebase that remote and retry once
::
:: Usage: release.bat            auto bump patch version (1.1.19 -> 1.1.20)
::        release.bat 1.2.5      use the given version
::
:: Note: pushing to GitHub triggers .github/workflows/release.yml, which
:: builds the same version and publishes the GitHub/Gitee release.
:: ============================================================

set INTERACTIVE=
echo %cmdcmdline% | find /i "%~nx0" >nul && set INTERACTIVE=1

set EXE_NAME=WindowsActivationHelper.exe
set PROJECT=src\MasActivator\MasActivator.csproj
set GITHUB_URL=https://github.com/lixiaoliyubo/windows-activation-helper.git

set VERSION=%~1
if "%VERSION%"=="" (
    for /f "usebackq delims=" %%V in (`powershell -NoProfile -Command "$t=Get-Content -LiteralPath '%PROJECT%' -Raw; $m=[regex]::Match($t,'<Version>([^<]+)</Version>'); $p=$m.Groups[1].Value.Split('.'); $p[$p.Length-1]=[string]([int]$p[$p.Length-1]+1); $p -join '.'"`) do set VERSION=%%V
)

if "%VERSION%"=="" (
    echo [FAIL] cannot resolve version number
    goto :fail
)

echo ============================================
echo  Windows Activation Helper - release v%VERSION%
echo ============================================

echo.
echo [1/4] sync version into csproj ...
powershell -NoProfile -Command "$p='%PROJECT%'; $t=Get-Content -LiteralPath $p -Raw; $t=[regex]::Replace($t,'<Version>[^<]+</Version>','<Version>%VERSION%</Version>'); Set-Content -LiteralPath $p -Value $t -Encoding UTF8 -NoNewline"
if errorlevel 1 goto :fail_build

echo.
echo [2/4] build ...
dotnet build "%PROJECT%" -c Release --no-incremental -p:Version=%VERSION% -v q -nologo
if errorlevel 1 goto :fail_build

if not exist publish mkdir publish
copy /Y "src\MasActivator\bin\Release\net48\%EXE_NAME%" "publish\%EXE_NAME%" >nul
if errorlevel 1 goto :fail_copy
> "publish\version.txt" echo %VERSION%
for %%F in ("publish\%EXE_NAME%") do echo     output: publish\%%~nxF  (%%~zF bytes, v%VERSION%)

echo.
echo [3/4] commit ...
git add -A
git commit -m "chore: release v%VERSION%" --quiet 2>nul
if errorlevel 1 (
    echo     no changes to commit
) else (
    echo     committed
)

echo.
echo [4/4] push remotes ...
if not exist .git (
    echo     [SKIP] not a git repository
    goto :done
)

git remote | findstr /i /x "github" >nul
if errorlevel 1 (
    git remote add github "%GITHUB_URL%"
    echo     added remote github
)

for /f "tokens=1" %%R in ('git remote') do (
    call :push_one %%R
    if errorlevel 1 (
        echo     [FAIL] %%R push failed
        goto :fail_push
    )
)

:done
echo.
echo Done. v%VERSION%
if defined INTERACTIVE pause
endlocal
exit /b 0

:push_one
git push %1 main --quiet
if not errorlevel 1 (
    echo     pushed %1/main
    exit /b 0
)
echo     %1 rejected the push, rebase and retry ...
git pull --rebase %1 main
if errorlevel 1 (
    echo     [FAIL] rebase conflict on %1, resolve manually
    exit /b 1
)
git push %1 main --quiet
if errorlevel 1 exit /b 1
echo     pushed %1/main after rebase
exit /b 0

:fail_build
echo.
echo [FAIL] build failed, release aborted
goto :fail

:fail_copy
echo.
echo [FAIL] copy failed, check bin\Release\net48\%EXE_NAME%
goto :fail

:fail_push
echo.
echo [FAIL] push failed, release aborted (commit is kept locally)
:fail
if defined INTERACTIVE pause
endlocal
exit /b 1
