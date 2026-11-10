@echo off
setlocal
cd /d "%~dp0"
echo Building KID Helper...
dotnet publish src\KIDHelper.csproj -c Release -o src\publish --nologo -v q
if errorlevel 1 ( echo. & echo BUILD FAILED & pause & exit /b 1 )

copy /y src\publish\KIDHelper.exe . >nul
rd /s /q src\publish 2>nul

echo.
echo Done:  %~dp0KIDHelper.exe
echo        keywords\  (keyword files - edit freely, build never overwrites them)
endlocal
