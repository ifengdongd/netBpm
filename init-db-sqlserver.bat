@echo off
rem 本文件按 GBK(cp936) 保存 + CRLF 行尾 —— 中文提示依赖控制台默认代码页。
rem 不要改成 UTF-8，也不要用只写 LF 的工具保存：cmd.exe 按字节偏移读批次的，
rem 这两点任意一条都会让脚本读串行（实测会刷一屏 "'xxx' 不是内部或外部命令"）。
setlocal EnableExtensions

rem ============================================================================
rem  NetBpm SQL Server 初始化：建库 + 建表 + 索引外键 + 种子数据 + 结构版本校验
rem
rem  做什么：调用 tools\NetBpm.DbInit，先从单一数据源（SchemaCatalog）重新生成
rem          db\sqlserver\netbpm_init.sql，再按连接串建库（若缺），最后幂等执行脚本。
rem          可以反复跑：新库一次建成，老库只补缺失结构，不重置已有数据。
rem
rem  前置条件：
rem    - SQL Server 2016 及以上（引擎用的 T-SQL 语法），实例必须支持 TLS 1.2+
rem    - 登录账号要有 create database 权限（要建库）
rem
rem  用法：
rem    init-db-sqlserver.bat
rem        不带参数时逐项询问（主机 / 库名 / 账号 / 口令，回车取默认值）
rem    init-db-sqlserver.bat --connection "Server=localhost;Database=netbpm_admin;User ID=sa;Password=***;TrustServerCertificate=True"
rem        整串覆盖，不询问
rem    set NETBPM_SQLSERVER_CONNECTION=...  然后 init-db-sqlserver.bat
rem        同上，优先级低于 --connection
rem    init-db-sqlserver.bat --no-pause
rem        结束不暂停（给 CI / 其它脚本调用）
rem
rem  口令里有 ; 或 % 这类字符时，一律走 --connection 或环境变量传整串，
rem  不要用逐项询问（形如 Set=Val; 的串会被拆断）。
rem
rem  注意：这个库是业务库（与控制台 / Rest 共用）；集成测试走独立库，
rem        别把测试指过来。
rem ============================================================================

set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"

set "DB=netbpm_admin"
set "SERVER=localhost"
set "DBUSER=sa"
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

echo.
echo === NetBpm SQL Server 初始化 ===
echo   仓库目录   %ROOT%

where dotnet >nul 2>nul
if not "%errorlevel%"=="0" goto NO_DOTNET

if defined CLI_CONN (
    set "CONN=%CLI_CONN%"
    goto HAVE_CONN
)
if defined NETBPM_SQLSERVER_CONNECTION (
    set "CONN=%NETBPM_SQLSERVER_CONNECTION%"
    goto HAVE_CONN
)

echo.
echo 未提供连接串，按回车取方括号里的默认值：
set /p "SERVER=SQL Server 主机 [localhost]: "
if "%SERVER%"=="" set "SERVER=localhost"
set /p "DB=数据库名 [%DB%]: "
if "%DB%"=="" set "DB=netbpm_admin"
set /p "DBUSER=登录账号 [sa]: "
if "%DBUSER%"=="" set "DBUSER=sa"
set /p "DBPASS=口    令（直接回车 = 空口令）: "
echo.
set "CONN=Server=%SERVER%;Database=%DB%;User ID=%DBUSER%;Password=%DBPASS%;TrustServerCertificate=True;"

:HAVE_CONN

for /f "delims=" %%v in ('dotnet --version 2^>nul') do set "DOTNET_VER=%%v"
echo   .NET SDK   %DOTNET_VER%
echo   目标库     %DB%
echo   连接串     %CONN%

echo.
echo [1/1] 生成脚本并初始化数据库（幂等，可重复执行）
rem stderr 单独落文件：失败时 .NET 会打几十行堆栈，摘要只取第一行，细节留日志里看。
dotnet run --project "%ROOT%\tools\NetBpm.DbInit" -v q --nologo -- --provider sqlserver --connection "%CONN%" --create-database
if not "%errorlevel%"=="0" goto INIT_FAIL

echo.
echo === 初始化完成 ===
echo   库         %DB%
echo   脚本       %ROOT%\db\sqlserver\netbpm_init.sql
echo   切到 SQL Server 跑宿主：把配置里的 DatabaseType 改成 SqlServer
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
echo        - 实例名 / 端口不对，或 SQL Server 服务没启动
echo        - 只启用 Windows 身份认证，sa 登录被禁用（改用 Integrated Security 的连接串）
echo        - 账号没有 create database 权限
echo        - 老实例只协商 TLS 1.0，客户端会直接拒绝连接（这是刻意的，不降级）
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
