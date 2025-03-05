@echo off
chcp 65001 > nul
setlocal enabledelayedexpansion
title ClassIsland 网络修复工具 - 固定端口8088

REM 检查管理员权限
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo 需要管理员权限来运行此脚本！请右键点击并选择"以管理员身份运行"
    echo.
    pause
    exit /b 1
)

echo.
echo ===================================
echo     ClassIsland 网络修复工具
echo ===================================
echo.
echo 此工具将修复ClassIsland网络连接问题，使其能够在端口8088上
echo 正常工作，并允许其他设备通过内网访问。
echo.
echo [正在执行] 添加URL保留中...

REM 添加URL保留 - 着重于8088端口
echo 1. 设置URL保留权限...

echo 为端口8088设置URL保留:
netsh http delete urlacl url=http://+:8088/ >nul 2>&1
netsh http add urlacl url=http://+:8088/ user=Everyone
if %errorlevel% neq 0 (
    echo [错误] 添加端口8088的URL保留失败
    echo        这可能导致WebMessageServer无法正常工作
) else (
    echo [成功] 已添加端口8088的URL保留
)

REM 添加其他可能使用的端口
echo.
echo 为其他可能使用的端口设置URL保留:
for %%p in (8089 7000 7001 9000) do (
    netsh http delete urlacl url=http://+:%%p/ >nul 2>&1
    netsh http add urlacl url=http://+:%%p/ user=Everyone >nul 2>&1
)
echo [完成] 已为备用端口添加URL保留

echo.
echo 2. 配置Windows防火墙规则...

REM 为固定端口8088创建专用规则
netsh advfirewall firewall delete rule name="ClassIsland Web Server (Port 8088)" >nul 2>&1
netsh advfirewall firewall add rule name="ClassIsland Web Server (Port 8088)" dir=in action=allow protocol=TCP localport=8088 enable=yes
if %errorlevel% neq 0 (
    echo [错误] 添加端口8088的防火墙规则失败
) else (
    echo [成功] 已添加防火墙规则允许端口8088的入站流量
)

REM 删除可能存在的旧规则并添加通用规则
netsh advfirewall firewall delete rule name="ClassIsland Web Server" >nul 2>&1
netsh advfirewall firewall add rule name="ClassIsland Web Server" dir=in action=allow protocol=TCP localport=8088,8089,7000,7001,9000 enable=yes >nul 2>&1

echo.
echo 3. 重点检查端口8088占用情况...
echo 检查端口8088:
netstat -ano | findstr /i ":8088" | findstr /i "LISTENING"
if %errorlevel% equ 0 (
    echo [警告] 端口8088已被占用！
    
    REM 获取占用进程信息
    for /f "tokens=5" %%a in ('netstat -ano ^| findstr /i ":8088" ^| findstr /i "LISTENING"') do set pid=%%a
    echo         占用进程PID: !pid!
    
    REM 尝试获取进程名称
    for /f "tokens=1" %%b in ('tasklist /fi "pid eq !pid!" ^| findstr "!pid!"') do set procname=%%b
    if defined procname (
        echo         进程名称: !procname!
    )
    
    echo.
    echo [重要] ClassIsland已被配置为使用固定端口8088。
    echo        请关闭上述占用此端口的进程，或修改这些应用的配置
    echo        以使用其他端口。
) else (
    echo [正常] 端口8088未被占用，可以供ClassIsland使用
)

echo.
echo 4. 尝试解决可能的网络问题...
ipconfig /flushdns
echo [完成] DNS缓存已刷新

echo 重置Winsock目录...
netsh winsock reset >nul 2>&1
echo [完成] Winsock重置成功

echo.
echo 5. 获取本地IP地址信息...
echo 可用的内网IP地址(其他设备应使用这些地址访问):
for /f "tokens=2 delims=:" %%a in ('ipconfig ^| findstr /i "IPv4.*192\|IPv4.*10\|IPv4.*172"') do (
    echo   - %%a:8088
)

echo.
echo ===================================
echo            修复完成!
echo ===================================
echo.
if "%has_port_conflict%"=="true" (
    echo [重要警告] 检测到端口8088被占用！
    echo           由于ClassIsland现在使用固定端口8088，
    echo           您必须先关闭占用此端口的应用程序，再启动ClassIsland。
    echo.
)

echo 请按照以下步骤操作:
echo 1. 确保端口8088未被占用
echo 2. 重启ClassIsland应用程序
echo 3. 在"自定义提醒"设置面板中点击"重试启动服务器"按钮
echo 4. 使用显示的IP地址(不是localhost)和端口8088从其他设备访问
echo.
echo 从其他设备访问的地址格式：http://[您电脑的IP地址]:8088/
echo.
pause 