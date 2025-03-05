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

namespace ClassIsland.Services
{
    /// <summary>
    /// 提供Web服务器功能，允许通过内网访问和发送自定义提醒
    /// </summary>
    public class WebMessageServer : IHostedService
    {
        private readonly ILogger<WebMessageServer> _logger;
        private readonly CustomMessageNotificationProvider _notificationProvider;
        private HttpListener? _httpListener;
        private CancellationTokenSource? _cts;
        private Task? _serverTask;

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
            CustomMessageNotificationProvider notificationProvider)
        {
            _logger = logger;
            _notificationProvider = notificationProvider;
            
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

        public Task StartAsync(CancellationToken cancellationToken)
        {
            LastErrorMessage = "正在尝试启动服务...";
            _logger.LogInformation("准备启动Web消息服务器...");
            
            try
            {
                if (_notificationProvider == null)
                {
                    LastErrorMessage = "自定义提醒服务不可用，无法启动";
                    _logger.LogError(LastErrorMessage);
                    IsRunning = false;
                    return Task.CompletedTask;
                }
                
                _logger.LogInformation("正在启动Web消息服务器...");
                _cts = new CancellationTokenSource();
                
                // 检查管理员权限并记录
                var isAdmin = IsRunAsAdministrator();
                _logger.LogInformation("当前应用程序是否以管理员身份运行: {IsAdmin}", isAdmin);
                
                // 使用固定端口8088，不再查找可用端口
                _logger.LogInformation("使用固定端口: {Port}", Port);
                
                // 重新创建HttpListener - 非常重要，避免使用可能已被释放的对象
                try
                {
                    // 确保旧的实例被完全释放
                    if (_httpListener != null)
                    {
                        try
                        {
                            if (_httpListener.IsListening)
                            {
                                _httpListener.Stop();
                            }
                        }
                        catch { /* 忽略任何错误 */ }
                        
                        _httpListener = null;
                    }
                    
                    // 创建全新的HttpListener实例
                    _httpListener = new HttpListener();
                }
                catch (Exception ex)
                {
                    LastErrorMessage = $"创建HttpListener实例失败: {ex.Message}";
                    _logger.LogError(ex, LastErrorMessage);
                    IsRunning = false;
                    return Task.CompletedTask;
                }
                
                // 尝试绑定 - 先尝试远程访问（需要管理员权限），失败后回退到本地模式
                bool bindingSuccess = false;
                
                // 1. 如果有管理员权限，优先尝试绑定到所有地址
                if (isAdmin)
                {
                    try
                    {
                        string prefix = $"http://+:{Port}/";
                        _httpListener.Prefixes.Clear();
                        _httpListener.Prefixes.Add(prefix);
                        
                        try
                        {
                            _httpListener.Start();
                            IsRunning = true;
                            IsLocalOnly = false;
                            bindingSuccess = true;
                            LastErrorMessage = null;
                            _logger.LogInformation("服务器已成功绑定到所有地址: {prefix}，管理员权限启用", prefix);
                        }
                        catch (HttpListenerException ex)
                        {
                            _logger.LogWarning(ex, "尽管有管理员权限，绑定到所有地址仍然失败，可能是其他原因导致");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "配置远程绑定失败");
                    }
                }
                else
                {
                    _logger.LogInformation("无管理员权限，将尝试URL保留方式绑定");
                }
                
                // 2. 如果没有管理员权限或者之前的绑定失败，尝试其他方式
                if (!bindingSuccess)
                {
                    // 尝试直接绑定到+，检查是否有URL保留
                    try
                    {
                        // 重新创建HttpListener，避免使用之前可能失败的实例
                        _httpListener = new HttpListener();
                        
                        string prefix = $"http://+:{Port}/";
                        _httpListener.Prefixes.Clear();
                        _httpListener.Prefixes.Add(prefix);
                        
                        try
                        {
                            _httpListener.Start();
                            IsRunning = true;
                            IsLocalOnly = false;
                            bindingSuccess = true;
                            LastErrorMessage = null;
                            _logger.LogInformation("服务器通过URL保留成功绑定到所有地址: {prefix}", prefix);
                        }
                        catch (HttpListenerException ex)
                        {
                            _logger.LogWarning(ex, "绑定到所有地址失败，尝试特定IP绑定或本地模式");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "配置通过URL保留绑定失败");
                    }
                }
                
                // 3. 如果上述方法都失败，尝试绑定到特定的本地IP地址
                if (!bindingSuccess)
                {
                    try
                    {
                        // 重新创建HttpListener
                        _httpListener = new HttpListener();
                        
                        string localIp = GetLocalIPAddress();
                        if (localIp != "localhost")
                        {
                            string prefix = $"http://{localIp}:{Port}/";
                            _httpListener.Prefixes.Clear();
                            _httpListener.Prefixes.Add(prefix);
                            
                            try
                            {
                                _httpListener.Start();
                                IsRunning = true;
                                IsLocalOnly = false;
                                bindingSuccess = true;
                                LastErrorMessage = null;
                                _logger.LogInformation("服务器已成功绑定到特定IP: {prefix}", prefix);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "绑定到特定IP失败，将尝试本地模式");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "配置特定IP绑定失败");
                    }
                }
                
                // 4. 如果所有尝试都失败，使用本地绑定
                if (!bindingSuccess)
                {
                    try
                    {
                        // 重新创建HttpListener，避免使用可能已被部分初始化的实例
                        _httpListener = new HttpListener();
                        
                        string prefix = $"http://localhost:{Port}/";
                        _httpListener.Prefixes.Clear();
                        _httpListener.Prefixes.Add(prefix);
                        
                        try
                        {
                            _httpListener.Start();
                            IsRunning = true;
                            IsLocalOnly = true;
                            bindingSuccess = true;
                            LastErrorMessage = null;
                            _logger.LogInformation("服务器已成功绑定到本地地址: {prefix}", prefix);
                        }
                        catch (Exception ex)
                        {
                            LastErrorMessage = $"启动本地服务器失败: {ex.Message}";
                            _logger.LogError(ex, LastErrorMessage);
                            bindingSuccess = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        LastErrorMessage = $"配置本地绑定失败: {ex.Message}";
                        _logger.LogError(ex, LastErrorMessage);
                        bindingSuccess = false;
                    }
                }
                
                // 如果绑定都失败，返回错误
                if (!bindingSuccess)
                {
                    IsRunning = false;
                    if (string.IsNullOrEmpty(LastErrorMessage))
                    {
                        LastErrorMessage = "服务器绑定失败，请检查端口是否被占用或尝试以管理员身份运行";
                    }
                    return Task.CompletedTask;
                }
                
                // 启动请求处理
                _serverTask = HandleRequestsAsync(_cts.Token);
                _logger.LogInformation("Web消息服务器启动完成，开始处理请求");
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                IsRunning = false;
                LastErrorMessage = $"服务器启动异常: {ex.Message}";
                _logger.LogError(ex, LastErrorMessage);
                return Task.CompletedTask;
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("正在停止Web消息服务器...");
            try
            {
                // 首先取消任务
                if (_cts != null && !_cts.IsCancellationRequested)
                {
                    _cts.Cancel();
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
                            if (request.HttpMethod == "GET" && (request.Url.AbsolutePath == "/" || request.Url.AbsolutePath == "/index.html"))
                            {
                                var html = GenerateHtmlPage();
                                var buffer = Encoding.UTF8.GetBytes(html);
                                response.ContentType = "text/html; charset=utf-8";
                                response.ContentLength64 = buffer.Length;
                                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                                response.Close();
                                continue;
                            }

                            // 处理POST请求（处理消息发送）
                            if (request.HttpMethod == "POST" && (request.Url.AbsolutePath == "/" || request.Url.AbsolutePath == "/api/message"))
                            {
                                await ProcessPostRequest(request, response);
                                continue;
                            }

                            // 其他请求返回404
                            response.StatusCode = 404;
                            response.StatusDescription = "Not Found";
                            var notFoundBuffer = Encoding.UTF8.GetBytes("404 - Not Found");
                            response.ContentType = "text/plain";
                            response.ContentLength64 = notFoundBuffer.Length;
                            await response.OutputStream.WriteAsync(notFoundBuffer, 0, notFoundBuffer.Length);
                            response.Close();
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
                    // 不抛出异常，继续处理响应
                }

                // 发送成功响应
                _logger.LogInformation("已接收到Web消息请求: {Message}", message);
                await WriteJsonResponse(response, new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理POST请求时出错");
                response.StatusCode = 500;
                response.StatusDescription = "Internal Server Error";
                await WriteJsonResponse(response, new { success = false, error = $"服务器内部错误: {ex.Message}" });
            }
        }

        private string GenerateHtmlPage()
        {
            // 使用标准HTML结构而不是原始字符串
            return @"<!DOCTYPE html>
<html lang='zh-CN'>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <title>ClassIsland自定义提醒发送</title>
    <style>
        body {
            font-family: 'Microsoft YaHei', 'Segoe UI', sans-serif;
            max-width: 600px;
            margin: 0 auto;
            padding: 20px;
            background-color: #f5f5f5;
        }
        .container {
            background-color: white;
            border-radius: 8px;
            padding: 20px;
            box-shadow: 0 2px 10px rgba(0,0,0,0.1);
        }
        h1 {
            color: #2196F3;
            margin-top: 0;
        }
        label {
            display: block;
            margin-top: 15px;
            font-weight: bold;
        }
        input, textarea {
            width: 100%;
            padding: 8px;
            margin-top: 5px;
            border: 1px solid #ddd;
            border-radius: 4px;
            box-sizing: border-box;
        }
        textarea {
            min-height: 100px;
            resize: vertical;
        }
        .checkbox-group {
            margin-top: 15px;
        }
        button {
            background-color: #2196F3;
            color: white;
            border: none;
            padding: 10px 15px;
            border-radius: 4px;
            margin-top: 20px;
            cursor: pointer;
            font-size: 16px;
        }
        button:hover {
            background-color: #0b7dda;
        }
        #status {
            margin-top: 20px;
            padding: 10px;
            border-radius: 4px;
            display: none;
        }
        .success {
            background-color: #dff0d8;
            color: #3c763d;
        }
        .error {
            background-color: #f2dede;
            color: #a94442;
        }
    </style>
</head>
<body>
    <div class='container'>
        <h1>ClassIsland自定义提醒</h1>
        <form id='reminderForm'>
            <label for='message'>提醒内容:</label>
            <textarea id='message' name='message' required></textarea>
            
            <div class='checkbox-group'>
                <input type='checkbox' id='speech' name='speech'>
                <label for='speech' style='display: inline'>启用语音朗读</label>
            </div>
            
            <label for='duration'>显示时长(秒):</label>
            <input type='number' id='duration' name='duration' value='10' min='1' max='60'>
            
            <button type='submit'>发送提醒</button>
        </form>
        
        <div id='status'></div>
    </div>

    <script>
        document.getElementById('reminderForm').addEventListener('submit', function(e) {
            e.preventDefault();
            
            var message = document.getElementById('message').value;
            var speech = document.getElementById('speech').checked;
            var duration = document.getElementById('duration').value;
            
            var data = {
                message: message,
                speech: speech,
                duration: parseInt(duration)
            };
            
            var statusDiv = document.getElementById('status');
            statusDiv.className = '';
            statusDiv.style.display = 'block';
            statusDiv.textContent = '正在发送...';
            
            fetch('/', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify(data)
            })
            .then(response => {
                if (response.ok) {
                    return response.json();
                }
                throw new Error('网络请求失败');
            })
            .then(data => {
                statusDiv.className = 'success';
                statusDiv.textContent = '提醒已发送成功！';
                document.getElementById('message').value = '';
            })
            .catch(error => {
                statusDiv.className = 'error';
                statusDiv.textContent = '发送失败: ' + error.message;
            });
        });
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
        /// 尝试多种方式绑定服务器地址
        /// </summary>
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
            
            // 2. 尝试绑定到0.0.0.0 (另一种方式绑定所有接口)
            if (!bindingSuccess)
            {
                try
                {
                    bindingAttempts++;
                    _logger.LogInformation("尝试绑定到0.0.0.0 (方法2): http://0.0.0.0:{Port}/", Port);
                    
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
                    _httpListener.Prefixes.Add($"http://0.0.0.0:{Port}/");
                    
                    try
                    {
                        _httpListener.Start();
                        IsRunning = true;
                        IsLocalOnly = false;
                        bindingSuccess = true;
                        LastErrorMessage = null;
                        _logger.LogInformation("服务器已成功绑定到0.0.0.0，应用程序可以通过内网访问");
                        
                        // 启动请求处理
                        _cts = new CancellationTokenSource();
                        _serverTask = HandleRequestsAsync(_cts.Token);
                        return;
                    }
                    catch (HttpListenerException ex)
                    {
                        _logger.LogWarning(ex, "无法绑定到0.0.0.0, 错误代码: {Code}, 错误信息: {Message}", ex.ErrorCode, ex.Message);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "创建HTTP监听器(方法2)时出错: {Message}", ex.Message);
                }
            }
            
            // 3. 尝试绑定到特定IP (不需要管理员权限)
            if (!bindingSuccess)
            {
                try
                {
                    bindingAttempts++;
                    _logger.LogInformation("尝试绑定到特定内网IP(方法3)...");
                    
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
                    _logger.LogWarning(ex, "配置特定IP绑定失败(方法3): {Message}", ex.Message);
                }
            }
            
            // 4. 尝试使用备用端口绑定localhost
            if (!bindingSuccess)
            {
                int[] alternativePorts = { 8089, 8090, 5000, 3000, 9000, 8000 };
                
                foreach (int altPort in alternativePorts)
                {
                    try
                    {
                        bindingAttempts++;
                        _logger.LogInformation("尝试使用备用端口绑定到localhost(方法4): http://localhost:{Port}/", altPort);
                        
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
                        _httpListener.Prefixes.Add($"http://localhost:{altPort}/");
                        
                        try
                        {
                            _httpListener.Start();
                            IsRunning = true;
                            IsLocalOnly = true;
                            bindingSuccess = true;
                            LastErrorMessage = null;
                            Port = altPort; // 更新端口
                            _logger.LogInformation("服务器已成功绑定到localhost，使用备用端口: {Port}", altPort);
                            _logger.LogWarning("服务器当前仅限本机访问。要启用内网访问，请运行fix_netsh.bat脚本");
                            
                            // 启动请求处理
                            _cts = new CancellationTokenSource();
                            _serverTask = HandleRequestsAsync(_cts.Token);
                            return;
                        }
                        catch (HttpListenerException ex)
                        {
                            _logger.LogWarning(ex, "无法绑定到localhost:{Port}, 错误代码: {Code}", altPort, ex.ErrorCode);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "尝试绑定到localhost:{Port}时出错", altPort);
                    }
                }
            }
            
            // 5. 最后的尝试：本地回环地址 127.0.0.1
            if (!bindingSuccess)
            {
                try
                {
                    bindingAttempts++;
                    _logger.LogInformation("尝试绑定到127.0.0.1 (方法5): http://127.0.0.1:{Port}/", Port);
                    
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
                    _logger.LogWarning(ex, "创建HTTP监听器(方法5)时出错");
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

        // 获取所有本地IP地址的方法
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
    }
} 