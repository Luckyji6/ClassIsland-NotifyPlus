@echo off
chcp 65001 > nul
setlocal enabledelayedexpansion
title ClassIsland 网络诊断工具 - 高级诊断与修复

echo.
echo =================================
echo    ClassIsland 网络诊断工具
echo =================================
echo.
echo 正在收集系统和网络信息...
echo.

REM 检查管理员权限
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo [!] 警告: 此工具没有以管理员权限运行
    echo     部分功能可能无法正常工作
    echo     请右键点击此批处理文件并选择"以管理员身份运行"
    echo.
    echo 按任意键继续以有限功能运行...
    pause > nul
) else (
    echo [√] 已以管理员权限运行
)

echo.
echo ----------------------------------
echo 1. 系统环境信息
echo ----------------------------------
echo 操作系统版本:
ver
whoami /user
echo.

echo ----------------------------------
echo 2. 网络接口信息
echo ----------------------------------
echo 正在获取IP地址信息...
ipconfig | findstr /i "IPv4.*192\|IPv4.*10\|IPv4.*172"
echo.
echo 本机可用IP地址(这些是其他设备可能用来访问的地址):
for /f "tokens=2 delims=:" %%a in ('ipconfig ^| findstr /i "IPv4.*192\|IPv4.*10\|IPv4.*172"') do (
    echo   - %%a
)
echo.

echo ----------------------------------
echo 3. HTTP.SYS端口保留状态
echo ----------------------------------
echo 检查端口8088的URL保留状态:
netsh http show urlacl | findstr /i :8088
if %errorlevel% neq 0 (
    echo [!] 没有找到端口8088的URL保留
    echo     这可能是连接问题的原因
    echo     解决方法: 以管理员身份运行 fix_netsh.bat 脚本
) else (
    echo [√] 端口8088已正确保留
)
echo.

echo 检查端口8089的URL保留状态:
netsh http show urlacl | findstr /i :8089
if %errorlevel% neq 0 (
    echo [!] 没有找到端口8089的URL保留
    echo     这可能是连接问题的原因
    echo     解决方法: 以管理员身份运行 fix_netsh.bat 脚本
) else (
    echo [√] 端口8089已正确保留
)
echo.

echo ----------------------------------
echo 4. 端口占用检查
echo ----------------------------------
echo 检查端口8088是否被占用:
netstat -ano | findstr /i ":8088"
if %errorlevel% equ 0 (
    echo [!] 端口8088已被占用
    echo     这可能会导致WebMessageServer无法启动
    echo     您可以在ClassIsland设置中尝试更改端口
) else (
    echo [√] 端口8088未被占用
)
echo.

echo 检查端口8089是否被占用:
netstat -ano | findstr /i ":8089"
if %errorlevel% equ 0 (
    echo [!] 端口8089已被占用
    echo     这可能会导致WebMessageServer无法启动
    echo     您可以在ClassIsland设置中尝试更改端口
) else (
    echo [√] 端口8089未被占用
)
echo.

echo ----------------------------------
echo 5. 防火墙检查
echo ----------------------------------
echo 检查ClassIsland防火墙规则:
netsh advfirewall firewall show rule name=all | findstr /i "ClassIsland"
if %errorlevel% neq 0 (
    echo [!] 未找到ClassIsland的防火墙规则
    echo     这可能会阻止其他设备访问
    echo     解决方法: 以管理员身份运行 fix_netsh.bat 脚本
)

echo.
echo ----------------------------------
echo 6. 连通性测试
echo ----------------------------------
echo 测试到默认网关的连接:
for /f "tokens=3" %%g in ('route print ^| findstr "\<0.0.0.0\>"') do (
    echo 正在ping网关 %%g...
    ping -n 3 %%g
)
echo.

echo ----------------------------------
echo 7. 服务器启动与接口绑定方式
echo ----------------------------------
echo [i] 启动步骤诊断:
echo     1. 如果使用管理员权限运行，尝试绑定到 http://+:8088/
echo     2. 如果没有管理员权限，尝试URL保留方式绑定到 http://+:8088/
echo     3. 如果上述都失败，尝试绑定到特定的本地IP
echo     4. 如果所有远程绑定方式都失败，回退到localhost
echo     5. 检查当前WebMessageServer状态信息

echo.
echo ----------------------------------
echo 8. 网络连接问题诊断结果
echo ----------------------------------

set problem_found=false

REM 检查URL保留
netsh http show urlacl | findstr /i :8088 > nul
if %errorlevel% neq 0 (
    echo [!] 问题1: 缺少端口8088的URL保留
    set problem_found=true
)

REM 检查防火墙规则
netsh advfirewall firewall show rule name=all | findstr /i "ClassIsland" > nul
if %errorlevel% neq 0 (
    echo [!] 问题2: 没有为ClassIsland设置Windows防火墙规则
    set problem_found=true
)

REM 检查端口占用
netstat -ano | findstr /i ":8088" > nul
if %errorlevel% equ 0 (
    echo [!] 问题3: 端口8088被其他程序占用
    set problem_found=true
)

if "%problem_found%"=="false" (
    echo [√] 未发现明显的网络配置问题
    echo     如果仍然无法连接，请尝试以下操作:
    echo     1. 临时关闭Windows防火墙进行测试
    echo     2. 确保ClassIsland在尝试连接的设备和电脑在同一个网络中
    echo     3. 尝试在ClassIsland设置中手动重启WebMessageServer服务
    echo     4. 确认连接设备使用的是电脑的内网IP地址，不是localhost或127.0.0.1
)

echo.
echo ----------------------------------
echo 9. 推荐解决方案
echo ----------------------------------
echo [+] 如果诊断发现URL保留或防火墙问题:
echo     1. 以管理员身份运行 fix_netsh.bat 脚本
echo     2. 重启ClassIsland应用程序
echo.
echo [+] 如果端口被占用:
echo     1. 在ClassIsland设置中尝试使用不同的端口
echo     2. 使用"自定义提醒"设置中的"重试启动服务器"按钮
echo.
echo [+] 如果上述方法都不起作用:
echo     1. 尝试以管理员身份运行ClassIsland程序
echo     2. 暂时禁用任何可能阻止网络连接的安全软件
echo     3. 检查连接设备是否与电脑在同一个Wi-Fi或以太网络

echo.
echo =================================
echo  诊断完成! 请将此信息提供给开发者以获取进一步帮助。
echo =================================
echo.
pause 