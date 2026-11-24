@echo off
rem Builds vcamtest.exe (diagnostic tool). Usage: build-test.cmd "<path to vcvars64.bat>"
setlocal
set "HERE=%~dp0"
set "OBJ=%HERE%obj"
if not exist "%OBJ%" mkdir "%OBJ%"
set PATH=%PATH:"=%
call %1 >nul || exit /b 1
cl /nologo /O2 /EHsc /std:c++17 /W3 /MT /DUNICODE /D_UNICODE /Fo"%OBJ%\\" "%HERE%vcamtest.cpp" /Fe"%OBJ%\vcamtest.exe"
exit /b %ERRORLEVEL%
