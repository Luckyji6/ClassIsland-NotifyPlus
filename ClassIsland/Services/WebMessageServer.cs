using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClassIsland.Models.NotificationProviderSettings;
using ClassIsland.Services.NotificationProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Linq;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core;

namespace ClassIsland.Services
{
    /// <summary>
    /// 提供Web服务器功能，允许通过内网访问和发送自定义提醒
    /// </summary>
    public class WebMessageServer : IHostedService
    {
        private readonly ILogger<WebMessageServer> _logger;
        private readonly CustomMessageNotificationProvider _notificationProvider;
        private readonly ILessonsService _lessonsService;
        private readonly MessageSecurityService _securityService;
        private HttpListener? _httpListener;
        private CancellationTokenSource? _cts;
        private Task? _serverTask;
        private Task? _monitorTask;
        private const int MAX_RETRY_ATTEMPTS = 3;
        private const int RETRY_DELAY_MS = 5000;
        private int _retryCount = 0;
        private DateTime _lastErrorTime = DateTime.MinValue;
        private bool _isAppStarted = false;

        public bool IsRunning { get; private set; }
        
        /// <summary>
        /// 服务器端口，固定为8088
        /// </summary>
        public int Port { get; private set; } = 8088;
        
        /// <summary>
        /// 服务器是否仅限本地访问
        /// </summary>
        public bool IsLocalOnly { get; private set; } = true;
        
        /// <summary>
        /// 最后一次错误信息
        /// </summary>
        public string? LastErrorMessage { get; private set; }
        
        public string ServerAddress => $"http://+:{Port}/";
        public string LocalUrl => $"http://localhost:{Port}/";

        public WebMessageServer(
            ILogger<WebMessageServer> logger,
            CustomMessageNotificationProvider notificationProvider,
            ILessonsService lessonsService,
            MessageSecurityService securityService)
        {
            _logger = logger;
            _notificationProvider = notificationProvider;
            _lessonsService = lessonsService;
            _securityService = securityService;
            
            // 在构造函数中记录初始化信息
            _logger.LogInformation("WebMessageServer服务已创建，等待启动...");
            LastErrorMessage = "服务已创建但尚未启动";
            
            // 确保依赖项可用
            if (_notificationProvider == null)
            {
                string error = "依赖项CustomMessageNotificationProvider不可用";
                _logger.LogError(error);
                LastErrorMessage = error;
            }
            
            if (_lessonsService == null)
            {
                string error = "依赖项ILessonsService不可用";
                _logger.LogError(error);
                LastErrorMessage = error;
            }

            // 订阅应用启动完成事件
            var app = AppBase.Current;
            if (app != null)
            {
                app.AppStarted += (_, _) =>
                {
                    _logger.LogInformation("收到应用启动完成事件");
                    _isAppStarted = true;
                    // 应用启动完成后，尝试启动服务器
                    if (!IsRunning)
                    {
                        _logger.LogInformation("应用启动完成，开始启动Web服务器");
                        try
                        {
                            ManualStart();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "在应用启动完成事件中启动Web服务器失败");
                        }
                    }
                };
            }
            else
            {
                _logger.LogWarning("无法获取应用实例，服务器可能需要手动启动");
            }
        }

        /// <summary>
        /// 检查当前应用程序是否以管理员身份运行
        /// </summary>
        /// <returns>如果以管理员身份运行，则返回true</returns>
        private bool IsRunAsAdministrator()
        {
            try
            {
                var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "检查管理员权限时出错");
                return false;
            }
        }

        private async Task MonitorServerHealthAsync()
        {
            while (!_cts?.IsCancellationRequested ?? false)
            {
                try 
                {
                    if (!IsRunning && (DateTime.Now - _lastErrorTime).TotalSeconds > 30)
                    {
                        _logger?.LogWarning("检测到服务器未运行，尝试自动重启...");
                        _lastErrorTime = DateTime.Now;
                        
                        if (_retryCount < MAX_RETRY_ATTEMPTS)
                        {
                            _retryCount++;
                            _logger?.LogInformation($"正在进行第 {_retryCount} 次重试...");
                            
                            try
                            {
                                await StopAsync(CancellationToken.None);
                                await Task.Delay(RETRY_DELAY_MS);
                                await StartAsync(CancellationToken.None);
                                
                                if (IsRunning)
                                {
                                    _logger?.LogInformation("服务器自动重启成功");
                                    _retryCount = 0;
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogError(ex, "自动重启过程中发生错误");
                            }
                        }
                        else
                        {
                            _logger?.LogError($"已达到最大重试次数({MAX_RETRY_ATTEMPTS})，请手动检查服务器状态");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "监控服务器状态时发生错误");
                }
                
                await Task.Delay(10000); // 每10秒检查一次
            }
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            LastErrorMessage = "正在尝试启动服务...";
            _logger.LogInformation("准备启动Web消息服务器...");
            
            try
            {
                // 如果应用还没有完全启动，等待应用启动事件
                if (!_isAppStarted)
                {
                    _logger.LogInformation("应用尚未完全启动，等待应用启动事件...");
                    return Task.CompletedTask;
                }

                // 如果已经在运行，不做任何操作
                if (IsRunning)
                {
                    _logger.LogInformation("服务器已经在运行中，无需重启");
                    return Task.CompletedTask;
                }
                
                // 如果有正在运行的任务，先停止
                if (_serverTask != null || _httpListener != null)
                {
                    _logger.LogInformation("检测到之前的服务器实例，先尝试停止...");
                    try
                    {
                        StopAsync(CancellationToken.None).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "停止之前的服务器实例时出错，这可能不影响新服务器的启动");
                    }
                    
                    // 等待一小段时间确保资源释放
                    Task.Delay(500).GetAwaiter().GetResult();
                }
                
                // 创建新的取消令牌源
                _cts = new CancellationTokenSource();
                
                // 启动监控任务
                _monitorTask = MonitorServerHealthAsync();
                
                if (_notificationProvider == null)
                {
                    LastErrorMessage = "自定义提醒服务不可用，无法启动";
                    _logger.LogError(LastErrorMessage);
                    IsRunning = false;
                    return Task.CompletedTask;
                }
                
                // 重置对象状态
                _httpListener = null;
                _serverTask = null;
                IsRunning = false;
                
                // 确保使用固定端口8088
                Port = 8088;
                _logger.LogInformation($"使用固定端口: {Port}");
                
                // 尝试多种方式绑定地址
                TryBindToAddress();
                
                if (IsRunning)
                {
                    _logger.LogInformation("服务器自动启动成功，端口: {Port}", Port);
                }
                else
                {
                    _logger.LogWarning("服务器自动启动过程完成，但状态检查显示未运行，可能端口 {Port} 被占用", Port);
                }
                
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                LastErrorMessage = $"自动启动服务器失败: {ex.Message}";
                _logger.LogError(ex, LastErrorMessage);
                IsRunning = false;
                return Task.CompletedTask;
            }
        }

        private void TryBindToAddress()
        {
            bool bindingSuccess = false;
            int bindingAttempts = 0;
            
            _logger.LogInformation("开始尝试绑定Web服务器...");
            
            // 检查管理员权限
            var isAdmin = IsRunAsAdministrator();
            _logger.LogInformation("当前应用程序是否以管理员身份运行: {IsAdmin}", isAdmin);
            
            // 记录系统信息
            try
            {
                string osVersion = Environment.OSVersion.ToString();
                string httpListenerSupported = HttpListener.IsSupported.ToString();
                _logger.LogInformation("操作系统版本: {OSVersion}", osVersion);
                _logger.LogInformation("系统是否支持HttpListener: {Supported}", httpListenerSupported);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "获取系统信息时出错");
            }
            
            // 0. 检查HttpListener是否受支持
            if (!HttpListener.IsSupported)
            {
                LastErrorMessage = "当前操作系统不支持HttpListener，无法启动Web服务器";
                _logger.LogError(LastErrorMessage);
                return;
            }
            
            // 1. 首先尝试绑定到所有网络接口 (需要管理员权限或URL保留)
            try
            {
                bindingAttempts++;
                _logger.LogInformation("尝试绑定到所有网络接口(方法1): http://+:{Port}/", Port);
                
                // 创建新的HttpListener实例
                _httpListener = new HttpListener();
                _httpListener.Prefixes.Add($"http://+:{Port}/");
                
                try
                {
                    _httpListener.Start();
                    IsRunning = true;
                    IsLocalOnly = false;
                    bindingSuccess = true;
                    LastErrorMessage = null;
                    _logger.LogInformation("服务器已成功绑定到所有网络接口，应用程序可以通过内网访问");
                    _logger.LogInformation("内网设备可以通过 http://{LocalIP}:{Port}/ 访问服务", GetLocalIPAddress(), Port);
                    
                    // 启动请求处理
                    _cts = new CancellationTokenSource();
                    _serverTask = HandleRequestsAsync(_cts.Token);
                    return;
                }
                catch (HttpListenerException ex)
                {
                    _logger.LogWarning(ex, "无法绑定到所有网络接口，错误代码: {Code}, 错误信息: {Message}", ex.ErrorCode, ex.Message);
                    _logger.LogWarning("这通常是因为没有管理员权限或未配置URL保留");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "创建HTTP监听器(方法1)时出错: {Message}", ex.Message);
            }
            
            // 2. 尝试绑定到特定IP (不需要管理员权限)
            if (!bindingSuccess)
            {
                try
                {
                    bindingAttempts++;
                    _logger.LogInformation("尝试绑定到特定内网IP(方法2)...");
                    
                    // 释放之前的实例
                    try
                    {
                        if (_httpListener != null)
                        {
                            _httpListener.Close();
                            _httpListener = null;
                        }
                    }
                    catch { }
                    
                    // 创建新的HttpListener实例
                    _httpListener = new HttpListener();
                    
                    // 获取所有可能的本地IP地址
                    var localIPs = GetAllLocalIPAddresses();
                    _logger.LogInformation("检测到的所有本地IP: {IPs}", string.Join(", ", localIPs));
                    
                    if (localIPs.Count > 0)
                    {
                        bool anyIpSuccess = false;
                        
                        // 尝试每个IP地址
                        foreach (var ip in localIPs)
                        {
                            try
                            {
                                string prefix = $"http://{ip}:{Port}/";
                                _logger.LogInformation("尝试绑定到IP: {IP}", prefix);
                                
                                // 释放之前的实例
                                try
                                {
                                    if (_httpListener != null)
                                    {
                                        _httpListener.Close();
                                        _httpListener = null;
                                    }
                                }
                                catch { }
                                
                                _httpListener = new HttpListener();
                                _httpListener.Prefixes.Clear();
                                _httpListener.Prefixes.Add(prefix);
                                
                                _httpListener.Start();
                                IsRunning = true;
                                IsLocalOnly = false;
                                bindingSuccess = true;
                                anyIpSuccess = true;
                                LastErrorMessage = null;
                                _logger.LogInformation("服务器已成功绑定到IP: {Prefix}", prefix);
                                
                                // 启动请求处理
                                _cts = new CancellationTokenSource();
                                _serverTask = HandleRequestsAsync(_cts.Token);
                                break;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "绑定到IP {IP} 失败: {Message}", ip, ex.Message);
                            }
                        }
                        
                        if (anyIpSuccess)
                        {
                            return;
                        }
                    }
                    else
                    {
                        _logger.LogWarning("未找到有效的本地IP地址，将尝试本地模式");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "配置特定IP绑定失败(方法2): {Message}", ex.Message);
                }
            }
            
            // 3. 尝试本地回环地址 127.0.0.1
            if (!bindingSuccess)
            {
                try
                {
                    bindingAttempts++;
                    _logger.LogInformation("尝试绑定到127.0.0.1 (方法3): http://127.0.0.1:{Port}/", Port);
                    
                    // 释放之前的实例
                    try
                    {
                        if (_httpListener != null)
                        {
                            _httpListener.Close();
                            _httpListener = null;
                        }
                    }
                    catch { }
                    
                    // 创建新的HttpListener实例
                    _httpListener = new HttpListener();
                    _httpListener.Prefixes.Clear();
                    _httpListener.Prefixes.Add($"http://127.0.0.1:{Port}/");
                    
                    try
                    {
                        _httpListener.Start();
                        IsRunning = true;
                        IsLocalOnly = true;
                        bindingSuccess = true;
                        LastErrorMessage = null;
                        _logger.LogInformation("服务器已成功绑定到127.0.0.1");
                        
                        // 启动请求处理
                        _cts = new CancellationTokenSource();
                        _serverTask = HandleRequestsAsync(_cts.Token);
                        return;
                    }
                    catch (HttpListenerException ex)
                    {
                        _logger.LogWarning(ex, "无法绑定到127.0.0.1, 错误代码: {Code}", ex.ErrorCode);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "创建HTTP监听器(方法3)时出错");
                }
            }
            
            // 所有方法都失败
            if (!bindingSuccess)
            {
                IsRunning = false;
                LastErrorMessage = $"在尝试了{bindingAttempts}种绑定方法后，服务器仍无法启动。可能的原因：1) 防火墙阻止 2) 端口被占用 3) 网络接口不可用";
                _logger.LogError(LastErrorMessage);
                _logger.LogError("请尝试：1) 暂时关闭防火墙 2) 运行fix_netsh.bat 3) 重启计算机");
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("正在停止Web消息服务器...");
            try
            {
                // 首先取消所有任务
                if (_cts != null && !_cts.IsCancellationRequested)
                {
                    _cts.Cancel();
                }
                
                // 等待监控任务完成
                if (_monitorTask != null)
                {
                    try
                    {
                        await Task.WhenAny(_monitorTask, Task.Delay(3000));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "等待监控任务完成时出错");
                    }
                }
                
                // 然后停止监听器
                if (_httpListener != null)
                {
                    try
                    {
                        if (_httpListener.IsListening)
                        {
                            _httpListener.Stop();
                            _logger.LogInformation("HTTP监听器已停止");
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        // 监听器已经被释放，忽略此异常
                        _logger.LogInformation("HTTP监听器已被释放");
                    }
                    catch (Exception ex)
                    {
                        LastErrorMessage = $"停止HTTP监听器时出错: {ex.Message}";
                        _logger.LogError(ex, LastErrorMessage);
                    }
                }
                
                // 等待服务器任务完成
                if (_serverTask != null)
                {
                    try
                    {
                        // 设置超时，避免无限等待
                        var timeoutTask = Task.Delay(3000);
                        var completedTask = await Task.WhenAny(_serverTask, timeoutTask);
                        
                        if (completedTask == timeoutTask)
                        {
                            _logger.LogWarning("等待Web服务器任务完成超时");
                        }
                        else
                        {
                            _logger.LogInformation("Web服务器任务已完成");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // 预期的取消异常，可以忽略
                        _logger.LogInformation("Web服务器任务已取消");
                    }
                    catch (Exception ex)
                    {
                        LastErrorMessage = $"等待Web服务器任务完成时出错: {ex.Message}";
                        _logger.LogError(ex, LastErrorMessage);
                    }
                }
                
                // 更新状态
                IsRunning = false;
                _logger.LogInformation("Web消息服务器已停止");
            }
            catch (Exception ex)
            {
                LastErrorMessage = $"停止Web服务器异常: {ex.Message}";
                _logger.LogError(ex, LastErrorMessage);
                IsRunning = false;
            }
        }

        private async Task HandleRequestsAsync(CancellationToken cancellationToken)
        {
            IsRunning = true;
            // 添加更详细的日志记录
            _logger.LogInformation("Web服务器已启动。正在监听: {Prefix}, 仅本地访问: {IsLocalOnly}, 端口: {Port}", 
                _httpListener?.Prefixes.FirstOrDefault(), IsLocalOnly, Port);
            _logger.LogInformation("您可以通过以下地址访问自定义提醒服务：");
            
            if (!IsLocalOnly)
            {
                var localIp = GetLocalIPAddress();
                _logger.LogInformation(" - 内网: http://{LocalIP}:{Port}/", localIp, Port);
            }
            _logger.LogInformation(" - 本地: http://localhost:{Port}/", Port);
            
            try
            {
                _logger.LogInformation("开始处理Web请求");
                while (!cancellationToken.IsCancellationRequested && _httpListener != null && _httpListener.IsListening)
                {
                    try
                    {
                        // 获取请求上下文
                        var context = await _httpListener.GetContextAsync();
                        var request = context.Request;
                        var response = context.Response;

                        try
                        {
                            // 添加CORS头
                            response.AddHeader("Access-Control-Allow-Origin", "*");
                            response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                            response.AddHeader("Access-Control-Allow-Headers", "Content-Type");

                            // 处理OPTIONS请求（预检请求）
                            if (request.HttpMethod == "OPTIONS")
                            {
                                response.StatusCode = 200;
                                response.Close();
                                continue;
                            }

                            // 处理GET请求（返回HTML页面）
                            if (request.HttpMethod == "GET")
                            {
                                if (request.Url.AbsolutePath == "/" || request.Url.AbsolutePath == "/index.html")
                                {
                                    // 检查是否设置了令牌，如果未设置，重定向到设置页面
                                    if (!_securityService.IsTokenConfigured)
                                    {
                                        response.StatusCode = 302; // 重定向
                                        response.Headers.Add("Location", "/setup");
                                        response.Close();
                                        continue;
                                    }
                                    
                                    // 检查是否已登录（通过cookie）
                                    bool isAuthenticated = IsAuthenticated(request);
                                    if (!isAuthenticated)
                                    {
                                        response.StatusCode = 302; // 重定向
                                        response.Headers.Add("Location", "/login");
                                        response.Close();
                                        continue;
                                    }
                                    
                                    var html = GenerateHtmlPage();
                                    var buffer = Encoding.UTF8.GetBytes(html);
                                    response.ContentType = "text/html; charset=utf-8";
                                    response.ContentLength64 = buffer.Length;
                                    await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                                    response.Close();
                                    continue;
                                }
                                else if (request.Url.AbsolutePath == "/login")
                                {
                                    var html = GenerateLoginPage();
                                    var buffer = Encoding.UTF8.GetBytes(html);
                                    response.ContentType = "text/html; charset=utf-8";
                                    response.ContentLength64 = buffer.Length;
                                    await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                                    response.Close();
                                    continue;
                                }
                                else if (request.Url.AbsolutePath == "/setup")
                                {
                                    // 如果令牌已配置，重定向到登录页面
                                    if (_securityService.IsTokenConfigured)
                                    {
                                        response.StatusCode = 302; // 重定向
                                        response.Headers.Add("Location", "/login");
                                        response.Close();
                                        continue;
                                    }
                                    
                                    var html = GenerateSetupPage();
                                    var buffer = Encoding.UTF8.GetBytes(html);
                                    response.ContentType = "text/html; charset=utf-8";
                                    response.ContentLength64 = buffer.Length;
                                    await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                                    response.Close();
                                    continue;
                                }
                                else if (request.Url.AbsolutePath == "/api/schedule")
                                {
                                    // 检查是否已登录
                                    if (!IsAuthenticated(request))
                                    {
                                        await WriteJsonResponse(response, new { error = "未授权访问", requireAuth = true });
                                        continue;
                                    }
                                    
                                    await HandleScheduleRequest(response);
                                    continue;
                                }
                            }

                            // 处理POST请求
                            if (request.HttpMethod == "POST")
                            {
                                if (request.Url.AbsolutePath == "/api/login")
                                {
                                    await HandleLoginRequest(request, response);
                                    continue;
                                }
                                else if (request.Url.AbsolutePath == "/api/setup")
                                {
                                    await HandleSetupRequest(request, response);
                                    continue;
                                }
                                else if (request.Url.AbsolutePath == "/" || request.Url.AbsolutePath == "/api/message")
                                {
                                    // 获取客户端IP地址
                                    string clientIp = request.RemoteEndPoint.ToString();
                                    
                                    // 验证Token
                                    bool isAuthorized = await ValidateRequestToken(request);
                                    if (!isAuthorized)
                                    {
                                        response.StatusCode = 401; // 未授权
                                        await WriteJsonResponse(response, new { success = false, error = "未授权访问，请先登录并提供正确的访问令牌" });
                                        
                                        // 记录未授权访问
                                        await _securityService.LogMessageHistoryAsync("未授权访问尝试", false, clientIp);
                                        continue;
                                    }
                                    
                                    await ProcessPostRequest(request, response);
                                    continue;
                                }
                            }

                            // 特殊API路径处理
                            if (request.Url.AbsolutePath == "/api/cnm")
                            {
                                response.StatusCode = 200;
                                var specialBuffer = Encoding.UTF8.GetBytes("我也cnm");
                                response.ContentType = "text/plain; charset=utf-8";
                                response.ContentLength64 = specialBuffer.Length;
                                await response.OutputStream.WriteAsync(specialBuffer, 0, specialBuffer.Length);
                                response.Close();
                                continue;
                            }

                            // 其他请求返回404，但附带API说明
                            response.StatusCode = 404;
                            response.StatusDescription = "Not Found";
                            
                            // 提供API信息而不是简单的404
                            var apiDescription = new
                            {
                                error = "请求的端点不存在",
                                message = "ClassIsland支持以下API请求：",
                                api = new object[]
                                {
                                    new {
                                        endpoint = "/api/message",
                                        method = "POST",
                                        description = "发送自定义消息",
                                        example = new {
                                            message = "要显示的消息内容",
                                            speech = true,
                                            duration = 10
                                        }
                                    },
                                    new {
                                        endpoint = "/api/schedule",
                                        method = "GET",
                                        description = "获取当前课表信息"
                                    },
                                    new {
                                        endpoint = "/",
                                        method = "GET",
                                        description = "访问Web界面"
                                    }
                                }
                            };
                            
                            // 返回JSON格式的API信息
                            await WriteJsonResponse(response, apiDescription);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "处理请求时发生错误");
                            LastErrorMessage = $"处理Web请求错误: {ex.Message}";
                            try
                            {
                                if (response != null && !response.OutputStream.CanWrite) continue;
                                
                                response.StatusCode = 500;
                                response.StatusDescription = "Internal Server Error";
                                var errorBuffer = Encoding.UTF8.GetBytes("500 - Internal Server Error");
                                response.ContentType = "text/plain";
                                response.ContentLength64 = errorBuffer.Length;
                                await response.OutputStream.WriteAsync(errorBuffer, 0, errorBuffer.Length);
                                response.Close();
                            }
                            catch (Exception innerEx)
                            {
                                _logger.LogError(innerEx, "无法发送错误响应");
                            }
                        }
                    }
                    catch (Exception ex) when (ex is OperationCanceledException)
                    {
                        // 预期的取消异常，可以安全退出
                        _logger.LogInformation("Web服务器处理线程已取消");
                        break;
                    }
                    catch (Exception ex)
                    {
                        // 处理其他异常但继续循环
                        LastErrorMessage = $"Web服务器处理请求异常: {ex.Message}";
                        _logger.LogError(ex, LastErrorMessage);
                        
                        // 短暂等待后继续
                        await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (HttpListenerException ex)
            {
                LastErrorMessage = $"HTTP监听器错误: {ex.Message}";
                _logger.LogError(ex, LastErrorMessage);
            }
            catch (ObjectDisposedException)
            {
                // 监听器被关闭，忽略此异常
                _logger.LogInformation("HTTP监听器已关闭");
            }
            catch (Exception ex)
            {
                LastErrorMessage = $"Web服务器处理线程致命错误: {ex.Message}";
                _logger.LogError(ex, LastErrorMessage);
            }
            finally
            {
                IsRunning = false;
                _logger.LogInformation("Web请求处理线程已结束");
            }
        }

        private async Task ProcessPostRequest(HttpListenerRequest request, HttpListenerResponse response)
        {
            try
            {
                // 获取客户端IP地址
                string clientIp = request.RemoteEndPoint.ToString();
                
                // 读取请求体
                string requestBody;
                using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
                {
                    requestBody = await reader.ReadToEndAsync();
                }

                // 解析JSON数据
                var data = JsonConvert.DeserializeObject<Dictionary<string, object>>(requestBody);
                
                if (data == null || !data.ContainsKey("message"))
                {
                    response.StatusCode = 400;
                    response.StatusDescription = "Bad Request";
                    await WriteJsonResponse(response, new { success = false, error = "缺少必要的消息内容" });
                    
                    // 记录失败日志
                    await _securityService.LogMessageHistoryAsync("请求格式错误：缺少消息内容", false, clientIp);
                    return;
                }

                // 获取消息内容
                string message = data["message"].ToString() ?? "";
                bool useSpeech = data.ContainsKey("speech") && Convert.ToBoolean(data["speech"]);
                int displayDuration = data.ContainsKey("duration") ? Convert.ToInt32(data["duration"]) : 10;

                // 设置消息内容
                _notificationProvider.Settings.CustomMessage = message;
                _notificationProvider.Settings.UseSpeech = useSpeech;
                _notificationProvider.Settings.DisplayDurationSeconds = displayDuration;

                bool success = true;
                try
                {
                    // 触发消息显示
                    _logger.LogInformation("尝试显示自定义提醒: {Message}", message);
                    _notificationProvider.ShowCustomNotification();
                    _logger.LogInformation("自定义提醒显示成功");
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("STA"))
                {
                    // 捕获特定的线程异常并提供详细日志
                    _logger.LogWarning(ex, "UI线程访问错误，请确保在主线程上创建UI元素");
                    // 即使有这个错误，也返回成功，因为消息已经设置
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "显示自定义提醒时发生错误");
                    success = false;
                    // 不抛出异常，继续处理响应
                }

                // 记录消息历史
                await _securityService.LogMessageHistoryAsync(message, success, clientIp);

                // 发送成功响应
                _logger.LogInformation("已接收到Web消息请求: {Message}", message);
                await WriteJsonResponse(response, new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理POST请求时出错");
                response.StatusCode = 500;
                response.StatusDescription = "Internal Server Error";
                
                // 记录错误日志
                await _securityService.LogMessageHistoryAsync("处理消息请求时服务器内部错误", false, request.RemoteEndPoint.ToString());
                
                await WriteJsonResponse(response, new { success = false, error = $"服务器内部错误: {ex.Message}" });
            }
        }

        private string GenerateHtmlPage()
        {
            return @"<!DOCTYPE html>
<html lang='zh-CN'>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <title>ClassIsland课程助手</title>
    <style>
        :root {
            --primary-color: #1976D2;
            --secondary-color: #2196F3;
            --background-color: #f8f9fa;
            --card-background: #ffffff;
            --text-color: #333333;
            --border-radius: 12px;
            --shadow: 0 4px 6px rgba(0, 0, 0, 0.1);
            --transition: all 0.3s ease;
        }

        * {
            margin: 0;
            padding: 0;
            box-sizing: border-box;
        }

        body {
            font-family: 'Microsoft YaHei', -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
            background-color: var(--background-color);
            color: var(--text-color);
            line-height: 1.6;
            padding: 20px;
            min-height: 100vh;
        }

        .container {
            max-width: 1200px;
            margin: 0 auto;
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(350px, 1fr));
            gap: 24px;
        }

        .header {
            grid-column: 1 / -1;
            text-align: center;
            margin-bottom: 32px;
            padding: 24px;
            background: linear-gradient(135deg, var(--primary-color), var(--secondary-color));
            border-radius: var(--border-radius);
            color: white;
            box-shadow: var(--shadow);
        }

        .header h1 {
            font-size: 32px;
            margin-bottom: 8px;
            font-weight: 600;
        }

        .header p {
            font-size: 16px;
            opacity: 0.9;
        }

        .card {
            background: var(--card-background);
            border-radius: var(--border-radius);
            padding: 24px;
            box-shadow: var(--shadow);
            transition: var(--transition);
        }

        .card:hover {
            transform: translateY(-4px);
            box-shadow: 0 6px 12px rgba(0, 0, 0, 0.15);
        }

        .card h2 {
            color: var(--primary-color);
            margin-bottom: 20px;
            font-size: 24px;
            border-bottom: 2px solid #e0e0e0;
            padding-bottom: 10px;
        }

        .form-group {
            margin-bottom: 20px;
        }

        label {
            display: block;
            margin-bottom: 8px;
            color: #555;
            font-weight: 500;
        }

        input[type='text'],
        input[type='number'],
        textarea {
            width: 100%;
            padding: 12px;
            border: 2px solid #e0e0e0;
            border-radius: 8px;
            font-size: 16px;
            transition: var(--transition);
        }

        input[type='text']:focus,
        input[type='number']:focus,
        textarea:focus {
            border-color: var(--secondary-color);
            outline: none;
            box-shadow: 0 0 0 3px rgba(33, 150, 243, 0.1);
        }

        textarea {
            min-height: 120px;
            resize: vertical;
        }

        .checkbox-group {
            display: flex;
            align-items: center;
            gap: 8px;
            margin: 16px 0;
        }

        button {
            background-color: var(--primary-color);
            color: white;
            border: none;
            padding: 12px 24px;
            border-radius: 8px;
            font-size: 16px;
            cursor: pointer;
            transition: var(--transition);
            width: 100%;
            font-weight: 500;
        }

        button:hover {
            background-color: var(--secondary-color);
            transform: translateY(-2px);
        }

        #status {
            margin-top: 16px;
            padding: 12px;
            border-radius: 8px;
            font-weight: 500;
            display: none;
        }

        .success {
            background-color: #e8f5e9;
            color: #2e7d32;
            border-left: 4px solid #2e7d32;
        }

        .error {
            background-color: #ffebee;
            color: #c62828;
            border-left: 4px solid #c62828;
        }

        .schedule {
            display: grid;
            grid-template-columns: auto 1fr;
            gap: 16px;
            padding: 12px;
            margin: 8px 0;
            border-radius: 8px;
            transition: var(--transition);
        }

        .schedule:hover {
            background-color: #f5f5f5;
        }

        .schedule-time {
            color: #666;
            font-size: 14px;
            font-weight: 500;
            min-width: 120px;
        }

        .schedule-subject {
            font-weight: 600;
            color: var(--text-color);
        }

        .current-class {
            background-color: #e3f2fd;
            border-left: 4px solid var(--primary-color);
        }

        @media (max-width: 768px) {
            .container {
                grid-template-columns: 1fr;
            }
            
            .card {
                padding: 20px;
            }
            
            .header {
                padding: 20px;
            }
            
            .header h1 {
                font-size: 24px;
            }
        }

        .loading {
            display: inline-block;
            width: 20px;
            height: 20px;
            border: 3px solid #f3f3f3;
            border-top: 3px solid var(--primary-color);
            border-radius: 50%;
            animation: spin 1s linear infinite;
            margin-right: 8px;
        }

        @keyframes spin {
            0% { transform: rotate(0deg); }
            100% { transform: rotate(360deg); }
        }

        .empty-schedule {
            text-align: center;
            padding: 24px;
            color: #666;
            font-style: italic;
            background-color: #f5f5f5;
            border-radius: 8px;
            margin: 16px 0;
        }
    </style>
</head>
<body>
    <div class='container'>
        <div class='header'>
            <h1>ClassIsland 课程助手</h1>
            <p>发送自定义提醒 & 实时课表查看</p>
        </div>
        
        <div class='card'>
            <h2>📝 发送提醒</h2>
            <form id='reminderForm'>
                <div class='form-group'>
                    <label for='message'>提醒内容</label>
                    <textarea id='message' name='message' required placeholder='请输入要发送的提醒内容...'></textarea>
                </div>
                
                <div class='checkbox-group'>
                    <input type='checkbox' id='speech' name='speech'>
                    <label for='speech'>启用语音朗读</label>
                </div>
                
                <div class='form-group'>
                    <label for='duration'>显示时长（秒）</label>
                    <input type='number' id='duration' name='duration' value='10' min='1' max='60'>
                </div>
                
                <button type='submit'>发送提醒</button>
            </form>
            <div id='status'></div>
        </div>

        <div class='card'>
            <h2>📅 今日课表</h2>
            <div id='schedule'></div>
        </div>
    </div>

    <script>
        document.getElementById('reminderForm').addEventListener('submit', async function(e) {
            e.preventDefault();
            
            const message = document.getElementById('message').value;
            const speech = document.getElementById('speech').checked;
            const duration = document.getElementById('duration').value;
            
            const statusDiv = document.getElementById('status');
            statusDiv.innerHTML = '<div class=""loading""></div>正在发送...';
            statusDiv.className = '';
            statusDiv.style.display = 'block';
            
            try {
                const response = await fetch('/', {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json'
                    },
                    body: JSON.stringify({
                        message,
                        speech,
                        duration: parseInt(duration)
                    })
                });
                
                const data = await response.json();
                
                if (response.ok) {
                    statusDiv.className = 'success';
                    statusDiv.textContent = '✅ 提醒已发送成功！';
                    document.getElementById('message').value = '';
                } else {
                    throw new Error(data.error || '发送失败');
                }
            } catch (error) {
                statusDiv.className = 'error';
                statusDiv.textContent = '❌ ' + (error.message || '网络请求失败');
            }
        });

        async function fetchSchedule() {
            const scheduleDiv = document.getElementById('schedule');
            
            try {
                const response = await fetch('/api/schedule');
                const data = await response.json();
                
                if (data.error) {
                    scheduleDiv.innerHTML = `<div class=""empty-schedule"">⚠️ ${data.error}</div>`;
                    return;
                }
                
                if (data.classes && data.classes.length > 0) {
                    const scheduleHtml = data.classes.map(lesson => `
                        <div class='schedule ${lesson.isCurrent ? 'current-class' : ''}'>
                            <div class='schedule-time'>${lesson.startTime} - ${lesson.endTime}</div>
                            <div class='schedule-subject'>${lesson.subject}</div>
                        </div>
                    `).join('');
                    scheduleDiv.innerHTML = scheduleHtml;
                } else {
                    scheduleDiv.innerHTML = '<div class=""empty-schedule"">📚 今日没有课程安排</div>';
                }
            } catch (error) {
                scheduleDiv.innerHTML = '<div class=""empty-schedule"">❌ 获取课表失败</div>';
            }
        }

        // 页面加载时获取课表
        fetchSchedule();
        // 每分钟刷新一次课表
        setInterval(fetchSchedule, 60000);
    </script>
</body>
</html>";
        }

        private async Task WriteJsonResponse(HttpListenerResponse response, object data)
        {
            var json = JsonConvert.SerializeObject(data);
            var buffer = Encoding.UTF8.GetBytes(json);
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            response.Close();
        }

        /// <summary>
        /// 重置错误状态
        /// </summary>
        public void ResetError()
        {
            LastErrorMessage = null;
        }

        /// <summary>
        /// 手动启动服务器
        /// </summary>
        public void ManualStart()
        {
            // 如果已经在运行，不做任何操作
            if (IsRunning)
            {
                _logger.LogInformation("服务器已经在运行中，无需重启");
                return;
            }
            
            // 记录手动启动尝试
            _logger.LogInformation("正在手动启动Web消息服务器...");
            
            try
            {
                // 如果有正在运行的任务，先停止
                if (_serverTask != null || _httpListener != null)
                {
                    _logger.LogInformation("检测到之前的服务器实例，先尝试停止...");
                    try
                    {
                        StopAsync(CancellationToken.None).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "停止之前的服务器实例时出错，这可能不影响新服务器的启动");
                    }
                    
                    // 等待一小段时间确保资源释放
                    Task.Delay(500).GetAwaiter().GetResult();
                }
                
                // 重置对象状态
                _httpListener = null;
                _serverTask = null;
                _cts = null;
                IsRunning = false;
                
                // 确保使用固定端口8088
                Port = 8088;
                _logger.LogInformation($"使用固定端口: {Port}");
                
                // 尝试多种方式绑定地址
                TryBindToAddress();
                
                if (IsRunning)
                {
                    _logger.LogInformation("服务器手动启动成功，端口: {Port}", Port);
                }
                else
                {
                    _logger.LogWarning("服务器手动启动过程完成，但状态检查显示未运行，可能端口 {Port} 被占用", Port);
                }
            }
            catch (Exception ex)
            {
                LastErrorMessage = $"手动启动服务器失败: {ex.Message}";
                _logger.LogError(ex, LastErrorMessage);
                IsRunning = false;
                throw;
            }
        }

        /// <summary>
        /// 获取所有本地IP地址的方法
        /// </summary>
        private List<string> GetAllLocalIPAddresses()
        {
            var result = new List<string>();
            try
            {
                // 1. 使用DNS方法
                try
                {
                    var hostName = System.Net.Dns.GetHostName();
                    var hostEntry = System.Net.Dns.GetHostEntry(hostName);
                    var addresses = hostEntry.AddressList
                        .Where(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        .Where(ip => !ip.ToString().StartsWith("127.") && !ip.ToString().StartsWith("169.254"))
                        .Select(ip => ip.ToString())
                        .ToList();
                    
                    if (addresses.Count > 0)
                    {
                        _logger.LogDebug("通过DNS方法找到IP: {IPs}", string.Join(", ", addresses));
                        result.AddRange(addresses);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "通过DNS获取IP地址时出错");
                }
                
                // 2. 使用NetworkInterface方法
                try
                {
                    var networkInterfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                        .Where(i => i.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                        .Where(i => i.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback);
                        
                    foreach (var ni in networkInterfaces)
                    {
                        var props = ni.GetIPProperties();
                        var addresses = props.UnicastAddresses
                            .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            .Where(a => !a.Address.ToString().StartsWith("127.") && !a.Address.ToString().StartsWith("169.254"))
                            .Select(a => a.Address.ToString())
                            .ToList();
                        
                        if (addresses.Count > 0)
                        {
                            _logger.LogDebug("通过网络接口{Name}找到IP: {IPs}", ni.Name, string.Join(", ", addresses));
                            result.AddRange(addresses);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "通过NetworkInterface获取IP地址时出错");
                }
                
                // 去重
                result = result.Distinct().ToList();
                
                // 如果没有找到任何地址，添加localhost
                if (result.Count == 0)
                {
                    result.Add("localhost");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取所有本地IP地址时出错");
                result.Add("localhost"); // 发生错误时至少返回localhost
            }
            
            return result;
        }

        /// <summary>
        /// 获取本地IP地址
        /// </summary>
        private string GetLocalIPAddress()
        {
            try
            {
                _logger.LogDebug("正在尝试获取本地IP地址...");
                // 首先尝试获取所有网络接口的IP地址
                var hostEntry = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                var localIPs = hostEntry.AddressList
                    .Where(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) // 仅IPv4
                    .Select(ip => ip.ToString())
                    .Where(ip => !ip.StartsWith("127.") && !ip.StartsWith("169.254")) // 排除回环和自动IP
                    .ToList();
                
                if (localIPs.Count > 0)
                {
                    string selectedIP = localIPs.First();
                    _logger.LogDebug("找到本地IP: {IP} (总共检测到 {Count} 个地址)", selectedIP, localIPs.Count);
                    if (localIPs.Count > 1)
                    {
                        _logger.LogDebug("所有检测到的IP: {IPs}", string.Join(", ", localIPs));
                    }
                    return selectedIP;
                }
                
                _logger.LogWarning("未找到有效的本地IP地址");
                return "localhost";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取本地IP地址时出错");
                return "localhost";
            }
        }

        /// <summary>
        /// 查找可用端口 (已弃用，现在使用固定端口8088)
        /// </summary>
        private int FindAvailablePort()
        {
            // 只使用固定的8088端口
            int portToUse = 8088;
            
            try
            {
                // 使用独立的HttpListener实例进行测试
                using (var testListener = new HttpListener())
                {
                    try
                    {
                        string testUrl = $"http://localhost:{portToUse}/";
                        testListener.Prefixes.Add(testUrl);
                        testListener.Start();
                        testListener.Stop();
                        
                        _logger.LogInformation($"端口 {portToUse} 可用");
                        return portToUse;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"指定端口 {portToUse} 不可用！请检查是否被其他应用占用");
                        LastErrorMessage = $"端口 {portToUse} 不可用，请检查是否被其他应用占用";
                        return -1;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"测试端口 {portToUse} 时出错");
                return -1;
            }
        }

        private async Task HandleScheduleRequest(HttpListenerResponse response)
        {
            try
            {
                if (_lessonsService == null)
                {
                    await WriteJsonResponse(response, new { error = "课表服务不可用" });
                    return;
                }

                if (!_lessonsService.IsClassPlanLoaded)
                {
                    await WriteJsonResponse(response, new { error = "未加载课表" });
                    return;
                }

                var currentPlan = _lessonsService.CurrentClassPlan;
                if (currentPlan == null)
                {
                    await WriteJsonResponse(response, new { error = "当前没有课表" });
                    return;
                }

                var currentTime = DateTime.Now.TimeOfDay;
                var currentIndex = _lessonsService.CurrentSelectedIndex;
                var currentTimeLayoutItem = _lessonsService.CurrentTimeLayoutItem;
                var currentSubject = _lessonsService.CurrentSubject;

                // 获取所有课程时间段
                var classes = new List<object>();

                // 添加当前课程
                if (currentTimeLayoutItem != null && currentSubject != null)
                {
                    classes.Add(new
                    {
                        startTime = currentTimeLayoutItem.StartSecond.ToString(@"hh\:mm"),
                        endTime = currentTimeLayoutItem.EndSecond.ToString(@"hh\:mm"),
                        subject = currentSubject.Name ?? "未安排课程",
                        isCurrent = true
                    });
                }

                // 添加下一节课
                var nextClassTimeLayoutItem = _lessonsService.NextClassTimeLayoutItem;
                var nextClassSubject = _lessonsService.NextClassSubject;
                if (nextClassTimeLayoutItem != null && nextClassSubject != null)
                {
                    classes.Add(new
                    {
                        startTime = nextClassTimeLayoutItem.StartSecond.ToString(@"hh\:mm"),
                        endTime = nextClassTimeLayoutItem.EndSecond.ToString(@"hh\:mm"),
                        subject = nextClassSubject.Name ?? "未安排课程",
                        isCurrent = false
                    });
                }

                await WriteJsonResponse(response, new { classes });
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "获取课表信息时出错");
                await WriteJsonResponse(response, new { error = "获取课表失败" });
            }
        }

        private bool IsAuthenticated(HttpListenerRequest request)
        {
            try
            {
                // 检查Cookie中是否有认证令牌
                var cookieCollection = request.Cookies;
                if (cookieCollection != null)
                {
                    var authCookie = cookieCollection["ClassIslandAuth"];
                    if (authCookie != null && !string.IsNullOrEmpty(authCookie.Value))
                    {
                        // 验证Cookie中的令牌
                        return _securityService.ValidateToken(authCookie.Value);
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "验证认证状态时出错");
                return false;
            }
        }

        private async Task HandleLoginRequest(HttpListenerRequest request, HttpListenerResponse response)
        {
            try
            {
                // 读取请求体
                string requestBody;
                using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
                {
                    requestBody = await reader.ReadToEndAsync();
                }

                // 解析JSON数据
                var data = JsonConvert.DeserializeObject<Dictionary<string, string>>(requestBody);
                
                if (data == null || !data.ContainsKey("token"))
                {
                    response.StatusCode = 400;
                    await WriteJsonResponse(response, new { success = false, error = "请提供访问令牌" });
                    return;
                }

                string token = data["token"];
                
                // 验证令牌
                if (_securityService.ValidateToken(token))
                {
                    // 设置认证Cookie
                    var authCookie = new Cookie("ClassIslandAuth", token)
                    {
                        Path = "/",
                        Expires = DateTime.Now.AddDays(7) // 7天过期
                    };
                    response.SetCookie(authCookie);
                    
                    await WriteJsonResponse(response, new { success = true });
                    _logger.LogInformation("用户登录成功");
                }
                else
                {
                    response.StatusCode = 401;
                    await WriteJsonResponse(response, new { success = false, error = "访问令牌无效" });
                    _logger.LogWarning("用户登录失败：令牌无效");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理登录请求时出错");
                response.StatusCode = 500;
                await WriteJsonResponse(response, new { success = false, error = "服务器内部错误" });
            }
        }

        private async Task HandleSetupRequest(HttpListenerRequest request, HttpListenerResponse response)
        {
            try
            {
                // 检查是否已配置令牌
                if (_securityService.IsTokenConfigured)
                {
                    response.StatusCode = 400;
                    await WriteJsonResponse(response, new { success = false, error = "访问令牌已配置，无法重新设置" });
                    return;
                }
                
                // 读取请求体
                string requestBody;
                using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
                {
                    requestBody = await reader.ReadToEndAsync();
                }

                // 解析JSON数据
                var data = JsonConvert.DeserializeObject<Dictionary<string, string>>(requestBody);
                
                if (data == null)
                {
                    // 如果没有提供令牌，则生成一个新的
                    string newToken = _securityService.GenerateToken();
                    bool result = await _securityService.SetToken(newToken);
                    
                    if (result)
                    {
                        await WriteJsonResponse(response, new { success = true, token = newToken });
                        _logger.LogInformation("已生成并设置新的访问令牌");
                    }
                    else
                    {
                        response.StatusCode = 500;
                        await WriteJsonResponse(response, new { success = false, error = "设置访问令牌失败" });
                    }
                }
                else if (data.ContainsKey("token") && !string.IsNullOrEmpty(data["token"]))
                {
                    // 使用提供的令牌
                    string token = data["token"];
                    bool result = await _securityService.SetToken(token);
                    
                    if (result)
                    {
                        await WriteJsonResponse(response, new { success = true });
                        _logger.LogInformation("已设置用户提供的访问令牌");
                    }
                    else
                    {
                        response.StatusCode = 500;
                        await WriteJsonResponse(response, new { success = false, error = "设置访问令牌失败" });
                    }
                }
                else
                {
                    response.StatusCode = 400;
                    await WriteJsonResponse(response, new { success = false, error = "请求格式错误" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理设置请求时出错");
                response.StatusCode = 500;
                await WriteJsonResponse(response, new { success = false, error = "服务器内部错误" });
            }
        }

        private async Task<bool> ValidateRequestToken(HttpListenerRequest request)
        {
            try
            {
                // 首先检查认证Cookie
                if (IsAuthenticated(request))
                {
                    return true;
                }
                
                // 如果Cookie验证失败，检查请求体中的令牌
                if (request.HasEntityBody)
                {
                    string requestBody;
                    using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
                    {
                        requestBody = await reader.ReadToEndAsync();
                        // 重置输入流，以便后续处理可以再次读取
                        request.InputStream.Position = 0;
                    }

                    var data = JsonConvert.DeserializeObject<Dictionary<string, object>>(requestBody);
                    if (data != null && data.ContainsKey("token") && data["token"] != null)
                    {
                        string token = data["token"].ToString();
                        return _securityService.ValidateToken(token);
                    }
                }
                
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "验证请求令牌时出错");
                return false;
            }
        }

        private string GenerateLoginPage()
        {
            return @"<!DOCTYPE html>
<html lang='zh-CN'>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <title>登录 - ClassIsland课程助手</title>
    <style>
        :root {
            --primary-color: #1976D2;
            --secondary-color: #2196F3;
            --background-color: #f8f9fa;
            --card-background: #ffffff;
            --text-color: #333333;
            --border-radius: 12px;
            --shadow: 0 4px 6px rgba(0, 0, 0, 0.1);
            --transition: all 0.3s ease;
        }

        * {
            margin: 0;
            padding: 0;
            box-sizing: border-box;
        }

        body {
            font-family: 'Microsoft YaHei', -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
            background-color: var(--background-color);
            color: var(--text-color);
            line-height: 1.6;
            padding: 20px;
            min-height: 100vh;
            display: flex;
            justify-content: center;
            align-items: center;
        }

        .container {
            max-width: 500px;
            width: 100%;
            background-color: var(--card-background);
            border-radius: var(--border-radius);
            box-shadow: var(--shadow);
            padding: 30px;
        }

        .header {
            text-align: center;
            margin-bottom: 30px;
        }

        h1 {
            color: var(--primary-color);
            font-size: 2em;
            margin-bottom: 10px;
        }

        p {
            margin-bottom: 20px;
        }

        .form-group {
            margin-bottom: 20px;
        }

        label {
            display: block;
            margin-bottom: 8px;
            font-weight: 500;
        }

        input[type='text'], input[type='password'] {
            width: 100%;
            padding: 12px;
            border: 1px solid #ddd;
            border-radius: 6px;
            font-size: 16px;
        }

        button {
            background-color: var(--primary-color);
            color: white;
            border: none;
            padding: 12px 20px;
            border-radius: 6px;
            cursor: pointer;
            font-size: 16px;
            width: 100%;
            transition: var(--transition);
        }

        button:hover {
            background-color: var(--secondary-color);
        }

        .alert {
            padding: 12px;
            border-radius: 6px;
            margin-bottom: 20px;
            display: none;
        }

        .alert-error {
            background-color: #f8d7da;
            color: #721c24;
            border: 1px solid #f5c6cb;
        }

        .alert-success {
            background-color: #d4edda;
            color: #155724;
            border: 1px solid #c3e6cb;
        }
    </style>
</head>
<body>
    <div class='container'>
        <div class='header'>
            <h1>登录</h1>
            <p>请输入您的访问令牌以继续访问</p>
        </div>
        
        <div id='loginError' class='alert alert-error'></div>
        <div id='loginSuccess' class='alert alert-success'></div>
        
        <div class='form-group'>
            <label for='tokenInput'>访问令牌</label>
            <input type='text' id='tokenInput' placeholder='请输入访问令牌，格式：XXX-XXX'>
        </div>
        
        <button id='loginButton'>登录</button>
    </div>

    <script>
        document.addEventListener('DOMContentLoaded', function() {
            const loginButton = document.getElementById('loginButton');
            const tokenInput = document.getElementById('tokenInput');
            const loginError = document.getElementById('loginError');
            const loginSuccess = document.getElementById('loginSuccess');
            
            loginButton.addEventListener('click', async function() {
                const token = tokenInput.value.trim();
                
                if (!token) {
                    showError('请输入访问令牌');
                    return;
                }
                
                try {
                    loginButton.disabled = true;
                    loginButton.textContent = '登录中...';
                    
                    const response = await fetch('/api/login', {
                        method: 'POST',
                        headers: {
                            'Content-Type': 'application/json'
                        },
                        body: JSON.stringify({ token })
                    });
                    
                    const data = await response.json();
                    
                    if (data.success) {
                        showSuccess('登录成功，正在跳转...');
                        setTimeout(() => {
                            window.location.href = '/';
                        }, 1500);
                    } else {
                        showError(data.error || '登录失败，请检查您的访问令牌');
                        loginButton.disabled = false;
                        loginButton.textContent = '登录';
                    }
                } catch (error) {
                    showError('登录请求失败，请稍后再试');
                    loginButton.disabled = false;
                    loginButton.textContent = '登录';
                }
            });
            
            // 移除自动格式化功能
            
            function showError(message) {
                loginError.textContent = message;
                loginError.style.display = 'block';
                loginSuccess.style.display = 'none';
            }
            
            function showSuccess(message) {
                loginSuccess.textContent = message;
                loginSuccess.style.display = 'block';
                loginError.style.display = 'none';
            }
        });
    </script>
</body>
</html>";
        }

        private string GenerateSetupPage()
        {
            return @"<!DOCTYPE html>
<html lang='zh-CN'>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <title>设置访问令牌 - ClassIsland课程助手</title>
    <style>
        :root {
            --primary-color: #1976D2;
            --secondary-color: #2196F3;
            --background-color: #f8f9fa;
            --card-background: #ffffff;
            --text-color: #333333;
            --border-radius: 12px;
            --shadow: 0 4px 6px rgba(0, 0, 0, 0.1);
            --transition: all 0.3s ease;
        }

        * {
            margin: 0;
            padding: 0;
            box-sizing: border-box;
        }

        body {
            font-family: 'Microsoft YaHei', -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
            background-color: var(--background-color);
            color: var(--text-color);
            line-height: 1.6;
            padding: 20px;
            min-height: 100vh;
            display: flex;
            justify-content: center;
            align-items: center;
        }

        .container {
            max-width: 600px;
            width: 100%;
            background-color: var(--card-background);
            border-radius: var(--border-radius);
            box-shadow: var(--shadow);
            padding: 30px;
        }

        .header {
            text-align: center;
            margin-bottom: 30px;
        }

        h1 {
            color: var(--primary-color);
            font-size: 2em;
            margin-bottom: 10px;
        }

        p {
            margin-bottom: 20px;
        }

        .form-group {
            margin-bottom: 20px;
        }

        label {
            display: block;
            margin-bottom: 8px;
            font-weight: 500;
        }

        input[type='text'] {
            width: 100%;
            padding: 12px;
            border: 1px solid #ddd;
            border-radius: 6px;
            font-size: 16px;
        }

        button {
            background-color: var(--primary-color);
            color: white;
            border: none;
            padding: 12px 20px;
            border-radius: 6px;
            cursor: pointer;
            font-size: 16px;
            width: 100%;
            transition: var(--transition);
            margin-bottom: 10px;
        }

        button:hover {
            background-color: var(--secondary-color);
        }

        .token-display {
            margin-top: 30px;
            padding: 20px;
            background-color: #f0f8ff;
            border-radius: var(--border-radius);
            border: 1px solid #c3e6cb;
            display: none;
        }

        .token-value {
            font-size: 1.2em;
            font-weight: 600;
            text-align: center;
            margin-bottom: 15px;
            word-break: break-all;
        }

        .alert {
            padding: 12px;
            border-radius: 6px;
            margin-bottom: 20px;
            display: none;
        }

        .alert-error {
            background-color: #f8d7da;
            color: #721c24;
            border: 1px solid #f5c6cb;
        }

        .alert-success {
            background-color: #d4edda;
            color: #155724;
            border: 1px solid #c3e6cb;
        }

        .options {
            display: flex;
            gap: 10px;
        }

        .options button {
            flex: 1;
        }

        .warning {
            color: #856404;
            background-color: #fff3cd;
            border: 1px solid #ffeeba;
            padding: 10px;
            border-radius: 6px;
            margin-top: 20px;
        }
    </style>
</head>
<body>
    <div class='container'>
        <div class='header'>
            <h1>设置访问令牌</h1>
            <p>首次使用前，请设置用于保护您消息服务的访问令牌</p>
        </div>
        
        <div id='setupError' class='alert alert-error'></div>
        <div id='setupSuccess' class='alert alert-success'></div>
        
        <div class='options'>
            <button id='generateButton'>生成随机令牌（推荐）</button>
            <button id='customButton'>使用自定义令牌</button>
        </div>
        
        <div id='customTokenForm' class='form-group' style='display: none; margin-top: 20px;'>
            <label for='tokenInput'>自定义令牌</label>
            <input type='text' id='tokenInput' placeholder='请输入访问令牌，格式：XXX-XXX'>
            <button id='setCustomTokenButton' style='margin-top: 10px;'>设置自定义令牌</button>
        </div>
        
        <div id='tokenDisplay' class='token-display'>
            <p>您的访问令牌为：</p>
            <div id='tokenValue' class='token-value'></div>
            <p style='text-align: center;'>请妥善保管此令牌，并确保只有授权人员知晓</p>
            <button id='continueButton'>继续登录</button>
        </div>
        
        <div class='warning'>
            <p><strong>重要提示：</strong>令牌一旦设置将无法通过此界面更改。如需更改，请删除程序目录下的message文件夹中的security.dat文件。</p>
        </div>
    </div>

    <script>
        document.addEventListener('DOMContentLoaded', function() {
            const generateButton = document.getElementById('generateButton');
            const customButton = document.getElementById('customButton');
            const customTokenForm = document.getElementById('customTokenForm');
            const tokenInput = document.getElementById('tokenInput');
            const setCustomTokenButton = document.getElementById('setCustomTokenButton');
            const tokenDisplay = document.getElementById('tokenDisplay');
            const tokenValue = document.getElementById('tokenValue');
            const continueButton = document.getElementById('continueButton');
            const setupError = document.getElementById('setupError');
            const setupSuccess = document.getElementById('setupSuccess');
            
            generateButton.addEventListener('click', async function() {
                try {
                    generateButton.disabled = true;
                    generateButton.textContent = '生成中...';
                    
                    const response = await fetch('/api/setup', {
                        method: 'POST',
                        headers: {
                            'Content-Type': 'application/json'
                        }
                    });
                    
                    const data = await response.json();
                    
                    if (data.success) {
                        tokenValue.textContent = data.token;
                        tokenDisplay.style.display = 'block';
                        showSuccess('令牌生成成功');
                        generateButton.style.display = 'none';
                        customButton.style.display = 'none';
                    } else {
                        showError(data.error || '生成令牌失败');
                        generateButton.disabled = false;
                        generateButton.textContent = '生成随机令牌';
                    }
                } catch (error) {
                    showError('请求失败，请稍后再试');
                    generateButton.disabled = false;
                    generateButton.textContent = '生成随机令牌';
                }
            });
            
            customButton.addEventListener('click', function() {
                customTokenForm.style.display = 'block';
                generateButton.style.display = 'none';
                customButton.style.display = 'none';
            });
            
            setCustomTokenButton.addEventListener('click', async function() {
                const token = tokenInput.value.trim();
                
                if (!token) {
                    showError('请输入自定义令牌');
                    return;
                }
                
                try {
                    setCustomTokenButton.disabled = true;
                    setCustomTokenButton.textContent = '设置中...';
                    
                    const response = await fetch('/api/setup', {
                        method: 'POST',
                        headers: {
                            'Content-Type': 'application/json'
                        },
                        body: JSON.stringify({ token })
                    });
                    
                    const data = await response.json();
                    
                    if (data.success) {
                        tokenValue.textContent = token;
                        tokenDisplay.style.display = 'block';
                        customTokenForm.style.display = 'none';
                        showSuccess('自定义令牌设置成功');
                    }
                    else {
                        showError(data.error || '设置令牌失败');
                        setCustomTokenButton.disabled = false;
                        setCustomTokenButton.textContent = '设置自定义令牌';
                    }
                } catch (error) {
                    showError('请求失败，请稍后再试');
                    setCustomTokenButton.disabled = false;
                    setCustomTokenButton.textContent = '设置自定义令牌';
                }
            });
            
            // 移除自动格式化功能
            
            continueButton.addEventListener('click', function() {
                window.location.href = '/login';
            });
            
            function showError(message) {
                setupError.textContent = message;
                setupError.style.display = 'block';
                setupSuccess.style.display = 'none';
            }
            
            function showSuccess(message) {
                setupSuccess.textContent = message;
                setupSuccess.style.display = 'block';
                setupError.style.display = 'none';
            }
        });
    </script>
</body>
</html>";
        }
    }
} 