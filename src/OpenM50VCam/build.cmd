@echo off
rem Builds OpenM50VCam.dll into ..\..\dist
rem Usage: build.cmd "<path to vcvars64.bat>"   (build.ps1 finds it via vswhere)
setlocal
set "HERE=%~dp0"
set "DIST=%HERE%..\..\dist"
set "OBJ=%HERE%obj"
if not exist "%OBJ%" mkdir "%OBJ%"
if not exist "%DIST%" mkdir "%DIST%"

rem A stray quote in PATH breaks vcvars64.bat, so strip quotes for this build only.
set PATH=%PATH:"=%
call %1 >nul || exit /b 1

rc /nologo /fo "%OBJ%\OpenM50VCam.res" "%HERE%OpenM50VCam.rc" || exit /b 1
cl /nologo /utf-8 /O2 /EHsc /std:c++17 /W3 /MT /DUNICODE /D_UNICODE /Fo"%OBJ%\\" "%HERE%OpenM50VCam.cpp" "%OBJ%\OpenM50VCam.res" /LD /link /DEF:"%HERE%OpenM50VCam.def" /OUT:"%DIST%\OpenM50VCam.dll" /IMPLIB:"%OBJ%\OpenM50VCam.lib"
exit /b %ERRORLEVEL%
