@echo off
echo [Admin] Checking inpoutx64 driver...
sc query inpoutx64 | find "RUNNING" >nul
if %errorlevel%==0 (
    echo [Admin] inpoutx64 driver is already running
) else (
    echo [Admin] Starting inpoutx64 driver...
    sc start inpoutx64
)
echo [Admin] Starting C# HAL backend on port 3100...
cd /d d:\DOUZHANZHE-Control\server\api
dotnet run --urls http://0.0.0.0:3100
pause
