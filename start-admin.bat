@echo off
rem 本文件按 GBK(cp936) 保存 + CRLF 行尾 —— 中文提示依赖控制台默认代码页。
rem 不要改成 UTF-8，也不要用只写 LF 的工具保存：cmd.exe 按字节偏移读批次的，
rem 这两点任意一条都会让脚本读串行（实测会刷一屏 "'xxx' 不是内部或外部命令"）。
setlocal EnableExtensions

rem ============================================================================
rem  启动 NetBpm 管理控制台（5081，只起控制台，不起 Rest）
rem
rem  为什么先 publish 再启动：常驻在 src\**\bin 下会锁住 exe，IDE 里点
rem  「重新生成」就报 MSB3021 / MSB3027「文件正由另一进程使用」。发布到
rem  artifacts\admin-console 后，运行实例与编译输出彻底分离，控制台开着
rem  也能随时重新生成。
rem
rem  先查端口再发布：控制台已经在跑时，发布一定会被它锁住的 dll 挡住
rem  （MSB3021 重试十次），所以命中"已在运行"就直接跳过发布。
rem
rem  数据库：netbpm_admin（与 Rest 5080 同一个库）；开发配置里写死了连接串
rem          和 dev-only JWT 密钥，环境固定 Development。
rem
rem  用法：
rem    start-admin.bat                     发布并启动，默认 5081，自动开浏览器
rem    start-admin.bat 5082                换端口
rem    start-admin.bat 5081 --no-publish   跳过发布（只改了配置/dll 时更快）
rem    start-admin.bat 5081 --no-browser   不自动开浏览器
rem    start-admin.bat stop                按端口停掉控制台
rem    start-admin.bat stop 5082           停指定端口
rem
rem  首次使用前：库要先初始化（init-db-mysql.bat），前端要先构建一次：
rem    cd web\admin   ^&^& npm install ^&^& npm run build
rem    cd web\designer ^&^& npm install ^&^& npm run build
rem ============================================================================

set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"

set "ADMIN_DIR=%ROOT%\artifacts\admin-console"
set "PORT=5081"
set "DO_PUBLISH=1"
set "OPEN_BROWSER=1"
set "MODE=start"

:PARSE
if "%~1"=="" goto PARSED
if /i "%~1"=="stop" (
    set "MODE=stop"
    shift
    goto PARSE
)
if /i "%~1"=="--no-publish" (
    set "DO_PUBLISH=0"
    shift
    goto PARSE
)
if /i "%~1"=="--no-browser" (
    set "OPEN_BROWSER=0"
    shift
    goto PARSE
)
set "PORT=%~1"
shift
goto PARSE
:PARSED

set "ADMIN_URL=http://localhost:%PORT%"

if /i "%MODE%"=="stop" goto STOP

echo.
echo === NetBpm 管理控制台 ===
echo   仓库目录   %ROOT%
echo   端口       %PORT%
echo   库         netbpm_admin（与 Rest 5080 共用）

echo.
echo [1/3] 检查端口
call :PORT_BUSY %PORT%
if "%BUSY%"=="1" goto ALREADY_RUNNING
echo   端口 %PORT% 空闲

if "%DO_PUBLISH%"=="0" goto SKIP_PUBLISH
echo.
echo [2/3] 发布 NetBpm.Admin 到 artifacts\admin-console
dotnet publish "%ROOT%\src\NetBpm.Admin\NetBpm.Admin.csproj" -c Debug -o "%ADMIN_DIR%" --nologo
if not "%errorlevel%"=="0" goto PUBLISH_FAIL
goto PUBLISHED

:SKIP_PUBLISH
echo.
echo [2/3] 跳过发布（--no-publish）
if not exist "%ADMIN_DIR%\NetBpm.Admin.exe" goto NOT_PUBLISHED

:PUBLISHED
if not exist "%ROOT%\src\NetBpm.Admin\wwwroot\index.html" (
    echo.
    echo   [提醒] src\NetBpm.Admin\wwwroot 里没有 index.html，管理台页面会 404。
    echo          首次拉取代码后先构建前端：
    echo            cd web\admin    ^&^& npm install ^&^& npm run build
    echo            cd web\designer ^&^& npm install ^&^& npm run build
)

echo.
echo [3/3] 启动控制台（新窗口）
set "ASPNETCORE_ENVIRONMENT=Development"
set "ASPNETCORE_URLS=%ADMIN_URL%"
start "NetBpm.Admin (%PORT%)" /D "%ADMIN_DIR%" cmd /k ""%ADMIN_DIR%\NetBpm.Admin.exe""
echo   已拉起进程，等待监听 ...

set "TRIES=0"
:WAIT_PORT
"%SystemRoot%\System32\ping.exe" -n 3 127.0.0.1 >nul 2>&1
set /a TRIES+=1
call :PORT_BUSY %PORT%
if "%BUSY%"=="1" goto READY
if %TRIES% lss 25 goto WAIT_PORT
echo.
echo   [提醒] 50 秒内端口没起来，去看新窗口里的报错：
echo          - MySQL 没启动，或 netbpm_admin 库还没初始化（先跑 init-db-mysql.bat）
echo          - 端口被别的程序占用
goto END_FAIL

:READY
echo.
echo === 控制台已启动 ===
echo   地址       %ADMIN_URL%
echo   库         netbpm_admin
echo   停止       start-admin.bat stop
if "%OPEN_BROWSER%"=="1" start "" "%ADMIN_URL%"
goto END_OK

:ALREADY_RUNNING
echo.
echo   端口 %PORT% 已在监听，控制台应该已经在运行：
echo   地址       %ADMIN_URL%
echo   重启       start-admin.bat stop ^&^& start-admin.bat
if "%OPEN_BROWSER%"=="1" start "" "%ADMIN_URL%"
goto END_OK

:STOP
call :PORT_BUSY %PORT%
if "%BUSY%"=="0" goto STOP_NOTHING
for /f "tokens=5" %%p in ('netstat -ano ^| findstr /c:"LISTENING" ^| findstr /c:":%PORT%"') do (
    taskkill /PID %%p /F >nul 2>nul
)
echo.
echo === 已停止 ===
echo   端口 %PORT% 上的进程已结束
goto END_OK

:STOP_NOTHING
echo.
echo   端口 %PORT% 没有监听，无需停止
goto END_OK

rem ---------------------------------------------------------------- 子过程
:PORT_BUSY
rem 结果写回 BUSY：1 = 端口已在监听
set "BUSY=0"
for /f "tokens=5" %%p in ('netstat -ano ^| findstr /c:"LISTENING" ^| findstr /c:":%~1"') do set "BUSY=1"
exit /b 0

rem ---------------------------------------------------------------- 异常出口
:PUBLISH_FAIL
echo.
echo [错误] 发布失败，控制台未启动。常见原因：
echo        - 控制台正在运行、锁着 artifacts\admin-console：先 start-admin.bat stop
echo        - NuGet 还原失败：检查网络或 NuGet.config 源
goto END_FAIL

:NOT_PUBLISHED
echo.
echo [错误] artifacts\admin-console 里没有 NetBpm.Admin.exe（从未发布过）。
echo        去掉 --no-publish 再跑一次。
goto END_FAIL

rem ---------------------------------------------------------------- 收尾
:END_OK
set "RC=0"
goto FINISH
:END_FAIL
set "RC=1"
:FINISH
endlocal & exit /b %RC%
