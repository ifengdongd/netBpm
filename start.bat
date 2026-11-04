@echo off
rem 本文件按 GBK(cp936) 保存 —— 中文提示依赖控制台默认代码页。
rem 不要改成 UTF-8：cmd.exe 读批次的字节偏移按读入时的代码页算，
rem 脚本里再 chcp 65001 会让后续行被错位重读成命令（实测会刷一串
rem "'xx' is not recognized"），而且改了编码只影响提示不影响逻辑。
setlocal EnableExtensions

rem ============================================================================
rem  NetBpm 本地开发一键启动（双击运行，或在终端里执行 start.bat）
rem
rem    5080  NetBpm.Rest    引擎 REST 宿主
rem    5081  NetBpm.Admin   管理控制台 + 平台面 /admin/v1
rem
rem  依赖：.NET 10 SDK + 本机 MySQL（两个宿主共用库 netbpm_admin，AutoMigrate 自动建表）
rem
rem  用法：
rem    start.bat          发布并启动两个服务，各自开一个窗口
rem    start.bat stop     按端口停掉 5080 / 5081
rem
rem  为什么先 publish 再启动：常驻在 src\**\bin 下会锁住 exe，
rem  IDE 里点「重新生成」就报 MSB3021 / MSB3027。发布到 artifacts\ 后
rem  运行时与编译输出彻底分离，服务开着也能随时重新生成。
rem ============================================================================

set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"

set "REST_DIR=%ROOT%\artifacts\rest-api"
set "ADMIN_DIR=%ROOT%\artifacts\admin-console"
set "REST_URL=http://localhost:5080"
set "ADMIN_URL=http://localhost:5081"

if /i "%~1"=="stop" goto STOP

echo.
echo === NetBpm 启动 ===
echo   仓库目录  %ROOT%

rem ---------------------------------------------------------------- 0) 体检
where dotnet >nul 2>nul
if errorlevel 1 goto NO_DOTNET

for /f "delims=" %%v in ('dotnet --version 2^>nul') do set "DOTNET_VER=%%v"
echo   .NET SDK  %DOTNET_VER%

if exist "%ROOT%\src\NetBpm.Admin\wwwroot\index.html" goto FRONTEND_OK
echo.
echo   [提醒] src\NetBpm.Admin\wwwroot 里没有 index.html，管理台页面会 404。
echo          首次拉取代码后先构建前端：
echo            cd web\admin    ^&^& npm install ^&^& npm run build
echo            cd web\designer ^&^& npm install ^&^& npm run build
:FRONTEND_OK

rem ---------------------------------------------------------------- 1) 发布
echo.
echo [1/3] 发布 NetBpm.Rest 到 artifacts\rest-api
dotnet publish "%ROOT%\src\NetBpm.Rest\NetBpm.Rest.csproj" -c Debug -o "%REST_DIR%" --nologo
if errorlevel 1 goto PUBLISH_FAIL

echo.
echo [2/3] 发布 NetBpm.Admin 到 artifacts\admin-console
dotnet publish "%ROOT%\src\NetBpm.Admin\NetBpm.Admin.csproj" -c Debug -o "%ADMIN_DIR%" --nologo
if errorlevel 1 goto PUBLISH_FAIL

rem ---------------------------------------------------------------- 2) 启动
echo.
echo [3/3] 启动服务（每个服务一个窗口）
set "ASPNETCORE_ENVIRONMENT=Development"

call :PORT_BUSY 5080
if "%BUSY%"=="1" goto REST_BUSY
set "ASPNETCORE_URLS=%REST_URL%"
start "NetBpm.Rest (5080)" /D "%REST_DIR%" cmd /k ""%REST_DIR%\NetBpm.Rest.exe""
echo   引擎 REST    %REST_URL%  ^(新窗口^)
goto REST_LAUNCHED
:REST_BUSY
echo   引擎 REST    %REST_URL%  已在运行，跳过
:REST_LAUNCHED

call :PORT_BUSY 5081
if "%BUSY%"=="1" goto ADMIN_BUSY
set "ASPNETCORE_URLS=%ADMIN_URL%"
start "NetBpm.Admin (5081)" /D "%ADMIN_DIR%" cmd /k ""%ADMIN_DIR%\NetBpm.Admin.exe""
echo   管理控制台   %ADMIN_URL%  ^(新窗口^)
goto ADMIN_LAUNCHED
:ADMIN_BUSY
echo   管理控制台   %ADMIN_URL%  已在运行，跳过
:ADMIN_LAUNCHED

rem ------------------------------------------------------------- 3) 等就绪
echo.
echo   等待控制台就绪 ...
set "TRIES=0"
:WAIT_ADMIN
"%SystemRoot%\System32\ping.exe" -n 3 127.0.0.1 >nul 2>&1
set /a TRIES+=1
call :PORT_BUSY 5081
if "%BUSY%"=="1" goto OPEN_ADMIN
if %TRIES% lss 25 goto WAIT_ADMIN
echo   [提醒] 50 秒内控制台没起来，看窗口里的报错（多数是 MySQL 没启动或库连不上）
goto DONE

:OPEN_ADMIN
start "" "%ADMIN_URL%"

:DONE
echo.
echo === 已启动 ===
echo   引擎 REST    %REST_URL%
echo   管理控制台   %ADMIN_URL%
echo   停止服务     start.bat stop
echo.
endlocal
exit /b 0

rem ---------------------------------------------------------------- 子过程
:PORT_BUSY
rem 结果写回 BUSY：1 = 端口已在监听
set "BUSY=0"
for /f "tokens=5" %%p in ('netstat -ano ^| findstr /c:"LISTENING" ^| findstr /c:":%~1"') do set "BUSY=1"
exit /b 0

rem ---------------------------------------------------------------- 异常出口
:NO_DOTNET
echo.
echo [错误] 找不到 dotnet 命令，请先安装 .NET 10 SDK：
echo        https://dotnet.microsoft.com/download
goto FAIL

:PUBLISH_FAIL
echo.
echo [错误] 发布失败，服务未启动。常见原因：
echo        - 有进程正锁着发布目录：先执行 start.bat stop
echo        - NuGet 还原失败：检查网络或 NuGet.config 源
goto FAIL

:FAIL
echo.
endlocal
exit /b 1

rem ---------------------------------------------------------------- 停止服务
:STOP
echo.
echo === 停止 NetBpm 服务（端口 5080 / 5081）===
call :KILL_PORT 5080
call :KILL_PORT 5081
echo.
echo 完成。
echo.
endlocal
exit /b 0

:KILL_PORT
set "FOUND=0"
rem 同一个进程会以 IPv4 / IPv6 各出现一行，按 PID 去重
for /f "tokens=5" %%p in ('netstat -ano ^| findstr /c:"LISTENING" ^| findstr /c:":%~1"') do call :KILL_ONE %%p %~1
if "%FOUND%"=="0" echo   端口 %~1  没有监听进程
exit /b 0

:KILL_ONE
if defined SEEN_%~1 exit /b 0
set "SEEN_%~1=1"
set "FOUND=1"
echo   端口 %~2  停止 PID %~1
taskkill /PID %~1 /F >nul 2>&1
exit /b 0
