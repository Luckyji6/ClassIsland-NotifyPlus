using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace ClassIsland.Services
{
    public class WebMessageServer
    {
        private readonly ILogger<WebMessageServer> _logger;
        private HttpListener _httpListener;
        private bool IsLocalOnly;
        private string LastErrorMessage;
        private bool IsRunning;
        private readonly int Port;

        public WebMessageServer(ILogger<WebMessageServer> logger, int port)
        {
            _logger = logger;
            Port = port;
        }

        public async Task HandleRequestsAsync(CancellationToken cancellationToken)
        {
            IsRunning = true;
            _logger.LogInformation("Web服务器已启动。正在监听: {0}, 仅本地访问: {1}, 端口: {2}", 
                _httpListener.Prefixes.FirstOrDefault(), IsLocalOnly, Port);
            _logger.LogInformation("您可以通过以下地址访问自定义提醒服务：");
            
            if (!IsLocalOnly)
            {
                var localIp = GetLocalIPAddress();
                _logger.LogInformation(" - 内网: http://{0}:{1}/", localIp, Port);
            }
            _logger.LogInformation(" - 本地: http://localhost:{0}/", Port);
            
            try
            {
                // ... existing code ...
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Web服务器处理请求时发生错误");
                LastErrorMessage = $"处理请求时发生错误: {ex.Message}";
            }
            finally
            {
                IsRunning = false;
            }
        }

        private void TryBindToAddress()
        {
            _logger.LogInformation("尝试绑定Web服务器...");
            
            var isAdmin = IsRunAsAdministrator();
            _logger.LogInformation("当前是否以管理员身份运行: {0}", isAdmin);
            
            try
            {
                if (isAdmin)
                {
                    _logger.LogInformation("尝试绑定到所有网络接口: http://+:{0}/", Port);
                    _httpListener = new HttpListener();
                    _httpListener.Prefixes.Add($"http://+:{Port}/");
                    _httpListener.Start();
                    
                    IsLocalOnly = false;
                    _logger.LogInformation("成功绑定到所有网络接口，Web服务器可通过内网访问");
                    return;
                }
                
                _logger.LogInformation("尝试使用URL保留方式绑定: http://+:{0}/", Port);
                try
                {
                    _httpListener = new HttpListener();
                    _httpListener.Prefixes.Add($"http://+:{Port}/");
                    _httpListener.Start();
                    
                    IsLocalOnly = false;
                    _logger.LogInformation("成功使用URL保留方式绑定到所有网络接口，Web服务器可通过内网访问");
                    return;
                }
                catch (HttpListenerException ex)
                {
                    _logger.LogWarning(ex, "URL保留方式绑定失败，这通常意味着需要运行fix_netsh.bat脚本，错误: {0}", ex.Message);
                }
                
                try
                {
                    var localIp = GetLocalIPAddress();
                    if (!string.IsNullOrEmpty(localIp))
                    {
                        _logger.LogInformation("尝试直接绑定到内网IP地址: http://{0}:{1}/", localIp, Port);
                        _httpListener = new HttpListener();
                        _httpListener.Prefixes.Add($"http://{localIp}:{Port}/");
                        _httpListener.Start();
                        
                        IsLocalOnly = false;
                        _logger.LogInformation("成功绑定到内网IP: {0}, Web服务器可通过内网访问", localIp);
                        return;
                    }
                }
                catch (HttpListenerException ex)
                {
                    _logger.LogWarning(ex, "内网IP绑定失败，错误: {0}", ex.Message);
                }
                
                _logger.LogWarning("无法绑定到内网接口，回退到本地模式，只能通过localhost访问。要允许内网访问，请：");
                _logger.LogWarning("1. 以管理员身份运行ClassIsland，或");
                _logger.LogWarning("2. 以管理员身份运行fix_netsh.bat脚本（一次性操作）");
                
                _httpListener = new HttpListener();
                _httpListener.Prefixes.Add($"http://localhost:{Port}/");
                _httpListener.Start();
                IsLocalOnly = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "绑定Web服务器失败: {0}", ex.Message);
                LastErrorMessage = $"绑定Web服务器失败: {ex.Message}";
                throw;
            }
        }

        private string GetLocalIPAddress()
        {
            // Implementation of GetLocalIPAddress method
            return null; // Placeholder return, actual implementation needed
        }

        private bool IsRunAsAdministrator()
        {
            // Implementation of IsRunAsAdministrator method
            return false; // Placeholder return, actual implementation needed
        }
    }
} 