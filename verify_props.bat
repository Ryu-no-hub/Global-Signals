@echo off
setlocal

set "SOLUTION=%~dp0"
set "HAS_ERROR="

:nextArg
if "%~1"=="" goto done

if not exist "%~1" (
    echo ERROR: Invalid path "%~1" in "%SOLUTION%Directory.Build.props" 1>&2
    set "HAS_ERROR=1"
)

shift
goto nextArg

:done
if defined HAS_ERROR (
    echo.
    echo Press any key to close...
    pause >nul
    exit /b 1
)

echo.
echo Done. Press any key to close...
pause >nul
exit /b 0