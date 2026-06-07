@echo off
echo === inpoutx64 驱动修复 ===
echo.
echo [1/4] 查询当前状态...
sc query inpoutx64
echo.
echo [2/4] 尝试停止卡住的服务...
sc stop inpoutx64
timeout /t 3 /nobreak >nul
echo.
echo [3/4] 重新启动驱动...
sc start inpoutx64
timeout /t 2 /nobreak >nul
echo.
echo [4/4] 验证状态...
sc query inpoutx64
echo.
echo === 驱动修复完成 ===
echo.
echo 现在启动后端...
cd /d d:\DOUZHANZHE-Control\server\api
dotnet run --urls http://0.0.0.0:3100
pause
