@echo off
rem 本文件按 GBK(cp936) 保存 + CRLF 行尾 —— 中文提示依赖控制台默认代码页。
rem 不要改成 UTF-8，也不要用只写 LF 的工具保存：cmd.exe 按字节偏移读批次的，
rem 这两点任意一条都会让脚本读串行（实测会刷一屏 "'xxx' 不是内部或外部命令"）。
setlocal EnableExtensions

rem ============================================================================
rem  NetBpm MySQL 初始化：建库 + 建表 + 索引外键 + 种子数据 + 结构版本校验
rem
rem  做什么：调用 tools\NetBpm.DbInit，先从单一数据源（SchemaCatalog）重新生成
rem          db\mysql\netbpm_init.sql，再按连接串建库（若缺），最后幂等执行脚本。
rem          可以反复跑：新库一次建成，老库只补缺失结构，不重置已有数据。
rem
rem  用法：
rem    init-db-mysql.bat
rem        默认 127.0.0.1:3306 / root 空口令 / 库 netbpm_admin
rem    init-db-mysql.bat netbpm_test
rem        只换库名
rem    init-db-mysql.bat --connection "Server=...;Database=...;User ID=...;Password=..."
rem        整串覆盖（口令里有 & 之类字符时必须加引号）
rem    set NETBPM_MYSQL_CONNECTION=...  然后 init-db-mysql.bat
rem        同上，优先级低于 --connection
rem    init-db-mysql.bat --no-pause
rem        结束不暂停（给 CI / 其它脚本调用）
rem
rem  注意：这里初始化的库就是 5080(Rest) 与 5081(管理控制台) 共用的业务库。
rem        集成测试用的是另一个库 netbpm_ittest —— 千万别把测试指过来，
rem        EngineTableCleaner.Purge 会清空引擎表。
rem ============================================================================

set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"

set "DB=netbpm_admin"
set "NOPAUSE=0"
set "CLI_CONN="

:PARSE
if "%~1"=="" goto PARSED
if /i "%~1"=="--no-pause" (
    set "NOPAUSE=1"
    shift
    goto PARSE
)
if /i "%~1"=="--connection" (
    set "CLI_CONN=%~2"
    shift
    shift
    goto PARSE
)
set "DB=%~1"
shift
goto PARSE
:PARSED

if defined CLI_CONN (
    set "CONN=%CLI_CONN%"
) else if defined NETBPM_MYSQL_CONNECTION (
    set "CONN=%NETBPM_MYSQL_CONNECTION%"
) else (
    set "CONN=Server=127.0.0.1;Port=3306;Database=%DB%;User ID=root;Password=;Allow User Variables=true;"
)

echo.
echo === NetBpm MySQL 初始化 ===
echo   仓库目录   %ROOT%

where dotnet >nul 2>nul
if not "%errorlevel%"=="0" goto NO_DOTNET

for /f "delims=" %%v in ('dotnet --version 2^>nul') do set "DOTNET_VER=%%v"
echo   .NET SDK   %DOTNET_VER%
echo   目标库     %DB%
echo   连接串     %CONN%

echo.
echo [1/1] 生成脚本并初始化数据库（幂等，可重复执行）
rem stderr 单独落文件：失败时 .NET 会打几十行堆栈，摘要只取第一行，细节留日志里看。
dotnet run --project "%ROOT%\tools\NetBpm.DbInit" -v q --nologo -- --provider mysql --connection "%CONN%" --create-database
if not "%errorlevel%"=="0" goto INIT_FAIL

echo.
echo === 初始化完成 ===
echo   库         %DB%   （Rest 5080 与控制台 5081 共用）
echo   脚本       %ROOT%\db\mysql\netbpm_init.sql
echo   下一步     start.bat（起 Rest 与控制台）
goto END_OK

rem ---------------------------------------------------------------- 异常出口
:NO_DOTNET
echo.
echo [错误] 找不到 dotnet 命令，请先安装 .NET 10 SDK：
echo        https://dotnet.microsoft.com/download
goto END_FAIL

:INIT_FAIL
echo.
echo [错误] 初始化失败，常见原因：
echo        - MySQL 没启动或端口不是 3306（可用 --connection 指定别的端口）
echo        - 账号口令不对，或该账号没有 create database 权限
echo        - 库名含非法字符（只允许字母、数字、下划线）
echo        - NuGet 还原失败（首次运行需要联网）
goto END_FAIL

rem ---------------------------------------------------------------- 收尾
:END_OK
set "RC=0"
goto FINISH
:END_FAIL
set "RC=1"
:FINISH
echo.
if "%NOPAUSE%"=="1" goto BYE
pause
:BYE
endlocal & exit /b %RC%
