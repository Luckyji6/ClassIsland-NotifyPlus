using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows.Controls;
using System.Threading;
using System.ComponentModel;
using ClassIsland.Core;
using ClassIsland.Models.NotificationProviderSettings;
using ClassIsland.Services;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace ClassIsland.Controls.NotificationProviders;

/// <summary>
/// CustomMessageNotificationProviderSettingsControl.xaml 的交互逻辑
/// </summary>
public partial class CustomMessageNotificationProviderSettingsControl : UserControl, INotifyPropertyChanged
{
    public CustomMessageNotificationProviderSettings Settings { get; }
    
    private Timer? _refreshTimer;
    private string _cachedWebServerUrl = "服务器尚未初始化";

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public string WebServerUrl
    {
        get => _cachedWebServerUrl;
        private set
        {
            if (_cachedWebServerUrl != value)
            {
                _cachedWebServerUrl = value;
                // 这里需要UI线程更新
                Dispatcher.Invoke(() => 
                {
                    OnPropertyChanged(nameof(WebServerUrl));
                });
            }
        }
    }

    public CustomMessageNotificationProviderSettingsControl(CustomMessageNotificationProviderSettings settings)
    {
        Settings = settings;
        InitializeComponent();
        
        // 设置定期刷新服务器状态
        _refreshTimer = new Timer(CheckServerStatus, null, 0, 3000); // 3秒刷新一次
        
        // 在控件卸载时停止Timer
        Unloaded += (s, e) => {
            _refreshTimer?.Dispose();
            _refreshTimer = null;
        };
    }
    
    private void CheckServerStatus(object? state)
    {
        try
        {
            var webServer = App.GetService<WebMessageServer>();
            if (webServer != null && webServer.IsRunning)
            {
                var port = webServer.Port;
                string ipAddress = GetLocalIPAddress();
                
                _logger?.LogDebug($"服务器状态检查：运行中，端口: {port}，仅本地: {webServer.IsLocalOnly}");
                
                if (webServer.IsLocalOnly)
                {
                    // 如果只能本地访问，显示localhost地址，但同时提供内网IP作为参考
                    WebServerUrl = $"服务器运行中 (端口{port})\n" +
                                  $"本机访问: http://localhost:{port}/\n" +
                                  $"注意: 当前仅限本机访问 - 其他设备无法连接\n" +
                                  $"建议: 以管理员权限运行 network_fix.bat";
                }
                else
                {
                    // 如果可以远程访问，显示局域网IP
                    WebServerUrl = $"服务器运行中 (端口{port})\n" +
                                  $"本机访问: http://localhost:{port}/\n" +
                                  $"内网访问: http://{ipAddress}:{port}/";
                }
            }
            else if (webServer != null)
            {
                // 如果服务存在但未运行，输出更详细的信息
                var reason = webServer.LastErrorMessage ?? "未知原因";
                
                _logger?.LogDebug($"服务器状态检查：未运行，原因: {reason}, 端口: {webServer.Port}");
                
                // 尝试获取服务状态的更多信息
                var statusInfo = new System.Text.StringBuilder();
                statusInfo.AppendLine($"服务器未运行，原因: {reason}");
                
                // 始终显示当前端口，强调使用固定端口8088
                statusInfo.AppendLine($"当前配置端口: {webServer.Port} (已固定为8088)");
                
                // 检查HttpListener状态
                bool portAvailable = false;
                try
                {
                    using (var listener = new System.Net.HttpListener())
                    {
                        var testUrl = $"http://localhost:{webServer.Port}/";
                        listener.Prefixes.Add(testUrl);
                        listener.Start();
                        listener.Stop();
                        portAvailable = true;
                        statusInfo.AppendLine("✓ 端口8088当前可用，但服务未能成功绑定");
                    }
                }
                catch (Exception ex)
                {
                    portAvailable = false;
                    statusInfo.AppendLine($"✗ 端口8088测试失败: {ex.Message}");
                    
                    // 检查是谁占用了端口
                    try
                    {
                        var processInfo = GetProcessUsingPort(webServer.Port);
                        if (!string.IsNullOrEmpty(processInfo))
                        {
                            statusInfo.AppendLine($"端口占用信息: {processInfo}");
                        }
                    }
                    catch (Exception)
                    {
                        // 忽略查找进程错误
                    }
                }
                
                // 添加手动启动提示
                if (portAvailable)
                {
                    statusInfo.AppendLine("✓ 您可以尝试点击下方\"重试启动服务器\"按钮");
                }
                else 
                {
                    statusInfo.AppendLine("! 请确保没有其他程序占用端口8088");
                    statusInfo.AppendLine("  建议: 以管理员权限运行 network_fix.bat");
                }
                
                // 检查管理员权限
                try
                {
                    var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                    var principal = new System.Security.Principal.WindowsPrincipal(identity);
                    bool isAdmin = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                    statusInfo.AppendLine($"当前是否管理员权限: {(isAdmin ? "是" : "否")}");
                    
                    if (!isAdmin && !webServer.IsLocalOnly)
                    {
                        statusInfo.AppendLine("提示: 需要管理员权限才能允许远程访问，否则只能在本机访问");
                        statusInfo.AppendLine("      可以运行fix_netsh.bat脚本添加URL保留或以管理员身份运行程序");
                    }
                }
                catch
                {
                    // 忽略权限检查错误
                }
                
                WebServerUrl = statusInfo.ToString();
                _logger.LogInformation($"WebMessageServer状态检测: {statusInfo}");
            }
            else
            {
                WebServerUrl = "服务器未初始化";
                _logger.LogInformation("WebMessageServer状态检测: 服务未初始化");
            }
        }
        catch (Exception ex)
        {
            WebServerUrl = $"无法获取服务器状态: {ex.Message}";
            _logger.LogError(ex, "WebMessageServer状态检测异常");
        }
    }

    /// <summary>
    /// 获取本机内网IP地址
    /// </summary>
    private string GetLocalIPAddress()
    {
        try
        {
            // 尝试获取主机名
            string hostName = Dns.GetHostName();
            
            // 首先尝试通过主机名获取IP地址
            IPAddress[] addresses = Dns.GetHostAddresses(hostName);
            
            // 过滤出IPv4地址，且不是回环地址
            var ipv4 = addresses
                .Where(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Where(ip => !IPAddress.IsLoopback(ip))
                .ToList();
                
            // 如果有可用的IPv4地址，则返回第一个
            if (ipv4.Count > 0)
            {
                return ipv4[0].ToString();
            }
            
            // 如果通过主机名没有找到合适的IP，则尝试通过网络接口获取
            var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(i => i.OperationalStatus == OperationalStatus.Up)
                .Where(i => i.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .ToList();
                
            foreach (var ni in networkInterfaces)
            {
                var props = ni.GetIPProperties();
                foreach (var ip in props.UnicastAddresses)
                {
                    if (ip.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        return ip.Address.ToString();
                    }
                }
            }
            
            // 如果都找不到，则返回localhost
            return "localhost";
        }
        catch
        {
            // 出现任何异常时，返回localhost
            return "localhost";
        }
    }

    /// <summary>
    /// 重试启动服务器按钮点击事件
    /// </summary>
    private async void RetryServerButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var button = sender as System.Windows.Controls.Button;
        if (button != null)
        {
            // 禁用按钮并更改文字
            button.IsEnabled = false;
            var originalContent = button.Content;
            button.Content = "重新启动中...";
            
            try
            {
                // 显示进度状态
                WebServerUrl = "正在尝试重新启动服务器...";
                
                await Task.Run(() => {
                    try
                    {
                        var webServer = App.GetService<WebMessageServer>();
                        if (webServer != null)
                        {
                            _logger?.LogInformation("开始重启WebMessageServer服务...");
                            
                            // 停止当前实例
                            try
                            {
                                webServer.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
                                System.Threading.Thread.Sleep(500); // 等待资源释放
                                _logger?.LogInformation("服务器已停止，准备重新启动");
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogWarning($"停止服务器时出错: {ex.Message}");
                            }
                            
                            // 重置错误状态
                            webServer.ResetError();
                            
                            // 显示当前端口
                            _logger?.LogInformation($"当前WebMessageServer端口: {webServer.Port}");
                            
                            // 手动启动服务器 - 由于我们已修改WebMessageServer.cs使用固定端口8088，不需要再次指定端口
                            _logger?.LogInformation("调用WebMessageServer.ManualStart()方法...");
                            webServer.ManualStart();
                            
                            // 检查启动结果
                            if (webServer.IsRunning)
                            {
                                _logger?.LogInformation($"服务器启动成功! 端口: {webServer.Port}, 是否仅本地访问: {webServer.IsLocalOnly}");
                                WebServerUrl = $"服务器启动成功! 地址：http://{(webServer.IsLocalOnly ? "localhost" : GetLocalIPAddress())}:{webServer.Port}/";
                            }
                            else
                            {
                                _logger?.LogError($"服务器启动失败: {webServer.LastErrorMessage ?? "未知错误"}");
                                WebServerUrl = $"服务器启动失败，原因：{webServer.LastErrorMessage ?? "未知错误"}";
                            }
                        }
                        else
                        {
                            _logger?.LogError("无法获取WebMessageServer服务!");
                            WebServerUrl = "无法获取WebMessageServer服务!";
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, $"重试启动服务器操作失败: {ex.Message}");
                        WebServerUrl = $"重试启动服务器操作失败: {ex.Message}";
                    }
                });
                
                // 立即刷新状态
                CheckServerStatus(null);
                
                // 延迟再次刷新
                _refreshTimer?.Change(2000, 3000);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"重试启动服务器UI操作失败: {ex.Message}");
                WebServerUrl = $"重试启动服务器失败: {ex.Message}";
            }
            finally
            {
                // 恢复按钮状态
                button.IsEnabled = true;
                button.Content = originalContent;
            }
        }
    }

    // 获取正在使用特定端口的进程信息
    private string GetProcessUsingPort(int port)
    {
        try
        {
            System.Diagnostics.Process process = new System.Diagnostics.Process();
            process.StartInfo.FileName = "netstat";
            process.StartInfo.Arguments = "-ano";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.CreateNoWindow = true;
            process.Start();
            
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            
            string[] lines = output.Split(new[] { '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries);
            
            foreach (string line in lines)
            {
                if (line.Contains($":{port}") && line.Contains("LISTENING"))
                {
                    string pidStr = line.Trim().Split(' ', '\t').Last();
                    if (int.TryParse(pidStr, out int pid))
                    {
                        try
                        {
                            var proc = System.Diagnostics.Process.GetProcessById(pid);
                            return $"进程: {proc.ProcessName}，PID: {pid}";
                        }
                        catch
                        {
                            return $"PID: {pid} (无法获取进程名)";
                        }
                    }
                    return $"PID: {pidStr}";
                }
            }
            
            return string.Empty;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "获取端口占用进程信息失败");
            return string.Empty;
        }
    }

    // 使用应用程序的Logger实例
    private readonly Microsoft.Extensions.Logging.ILogger? _logger = 
        App.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()?.CreateLogger("CustomMessageNotificationProviderSettingsControl");
} 