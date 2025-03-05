@echo off
chcp 65001 > nul
setlocal enabledelayedexpansion
title ClassIsland 连接测试工具 - 端口8088

echo.
echo ==========================================
echo      ClassIsland 连接测试工具
echo ==========================================
echo.
echo 此工具将测试ClassIsland WebMessageServer在端口8088上的连接状态
echo.

REM 获取本地IP地址
echo 正在获取本地IP地址...
set "local_ip="

REM 尝试获取IPv4地址
for /f "tokens=2 delims=:" %%a in ('ipconfig ^| findstr /i "IPv4.*192\|IPv4.*10\|IPv4.*172"') do (
    if not defined local_ip (
        set "local_ip=%%a"
        set "local_ip=!local_ip: =!"
    )
)

if not defined local_ip (
    echo [!] 警告: 无法获取本地IP地址
    set "local_ip=127.0.0.1"
) else (
    echo [√] 本地IP地址: !local_ip!
)

REM 直接设置端口为8088
set "port=8088"
echo.
echo 固定测试端口: !port!
echo.
echo 测试本地WebMessageServer连接...
echo.

REM 测试localhost连接
echo 1. 测试localhost连接(http://localhost:!port!/)...
powershell -Command "try { $response = Invoke-WebRequest -Uri \"http://localhost:!port!/\" -UseBasicParsing -TimeoutSec 5; Write-Host ('状态: ' + $response.StatusCode); } catch { Write-Host ('错误: ' + $_.Exception.Message) }"

echo.
echo 2. 测试本地IP连接(http://!local_ip!:!port!/)...
powershell -Command "try { $response = Invoke-WebRequest -Uri \"http://!local_ip!:!port!/\" -UseBasicParsing -TimeoutSec 5; Write-Host ('状态: ' + $response.StatusCode); } catch { Write-Host ('错误: ' + $_.Exception.Message) }"

echo.
echo 3. 测试端口是否已打开并监听...
powershell -Command "try { $tcp = New-Object Net.Sockets.TcpClient; $tcp.ConnectAsync('localhost', !port!).Wait(2000); if($tcp.Connected) { Write-Host '本地端口!port!已打开并可连接' } else { Write-Host '本地端口!port!未打开或无法连接' }; $tcp.Close(); } catch { Write-Host ('错误: ' + $_.Exception.Message) }"

echo.
echo 4. 检查本地端口监听状态...
netstat -ano | findstr /i ":!port!" | findstr /i "LISTENING"
if %errorlevel% neq 0 (
    echo [!] 警告: 未检测到端口!port!的监听
    echo     WebMessageServer可能未正确启动
    
    REM 检查端口是否被占用
    netstat -ano | findstr /i ":!port!" > nul
    if %errorlevel% equ 0 (
        echo [!] 发现端口!port!的其他连接，但不是处于监听状态
    )
) else (
    echo [√] 端口!port!已在监听
    
    REM 显示监听的进程
    for /f "tokens=5" %%a in ('netstat -ano ^| findstr /i ":!port!" ^| findstr /i "LISTENING"') do (
        echo     监听进程PID: %%a
        for /f "tokens=1" %%b in ('tasklist /fi "pid eq %%a" ^| findstr "%%a"') do (
            echo     进程名称: %%b
        )
    )
)

echo.
echo 5. 检查URL保留状态...
netsh http show urlacl | findstr /i :!port!
if %errorlevel% neq 0 (
    echo [!] 警告: 端口!port!未设置URL保留
    echo     这可能导致其他设备无法访问
    echo     解决方法: 以管理员身份运行 network_fix.bat 脚本
) else (
    echo [√] 端口!port!已设置URL保留
)

echo.
echo 6. 检查防火墙设置...
netsh advfirewall firewall show rule name="ClassIsland Web Server (Port 8088)" > nul
if %errorlevel% neq 0 (
    echo [!] 警告: 未找到端口8088的专用防火墙规则
    echo     检查通用ClassIsland防火墙规则:
    netsh advfirewall firewall show rule name="ClassIsland Web Server" | findstr /i "8088" > nul
    if %errorlevel% neq 0 (
        echo [!] 警告: 通用防火墙规则中也未找到端口8088
        echo     这可能导致其他设备无法访问
        echo     解决方法: 以管理员身份运行 network_fix.bat 脚本
    ) else (
        echo [√] 端口8088包含在通用防火墙规则中
    )
) else (
    echo [√] 已找到端口8088的专用防火墙规则
)

echo.
echo 7. 外部访问信息...
echo.
echo WebMessageServer访问地址:
echo   - 本机访问: http://localhost:!port!/
echo   - 内网访问: http://!local_ip!:!port!/
echo.

echo ==========================================
echo               连接测试结果
echo ==========================================
echo.
echo 如果您无法从其他设备访问WebMessageServer:
echo.
echo 1. 确认您已运行network_fix.bat脚本(以管理员身份)
echo 2. 确认端口8088未被其他应用占用
echo 3. 在ClassIsland的"自定义提醒"设置中点击"重试启动服务器"按钮
echo 4. 确保其他设备与此电脑在同一网络中
echo 5. 尝试临时关闭Windows防火墙
echo 6. 确保使用的是内网IP地址(!local_ip!)而不是localhost
echo.
echo 其他设备访问地址: http://!local_ip!:!port!/
echo.
echo 请按任意键退出...
pause > nul 