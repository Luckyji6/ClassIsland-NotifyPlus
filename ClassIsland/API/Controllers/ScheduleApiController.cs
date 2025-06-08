using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Models;
using ClassIsland.Services;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using ClassIsland.API.Models;
using ClassIsland.Shared.Models.Profile;

namespace ClassIsland.API.Controllers
{
    /// <summary>
    /// 提供课表和时间安排的API控制功能
    /// </summary>
    public class ScheduleApiController
    {
        private readonly ILogger<ScheduleApiController> _logger;
        private readonly ILessonsService _lessonsService;
        private readonly IProfileService _profileService;
        private readonly MessageSecurityService _securityService;
        private readonly HttpListener _httpListener;
        private CancellationTokenSource _cts;
        private Task _serverTask;
        private readonly string _apiKeyHash; // 保存API密钥的哈希值
        private int _retryCount = 0;
        private const int MAX_RETRY_COUNT = 3;
        private const int RETRY_PORT_INCREMENT = 1;
        private const int BASE_PORT = 8089;
        
        // API路径前缀，用于区分WebMessageServer的其他功能
        private const string API_PATH_PREFIX = "schedule-api";

        public bool IsRunning { get; private set; }
        public int Port { get; set; } = BASE_PORT; // 初始端口
        public string ServerAddress => $"http://+:{Port}/{API_PATH_PREFIX}/";

        public ScheduleApiController(
            ILogger<ScheduleApiController> logger,
            ILessonsService lessonsService,
            IProfileService profileService,
            MessageSecurityService securityService,
            string apiKey)
        {
            _logger = logger;
            _lessonsService = lessonsService;
            _profileService = profileService;
            _securityService = securityService;
            
            // 计算API密钥的哈希值用于验证
            using (var sha256 = SHA256.Create())
            {
                var keyBytes = Encoding.UTF8.GetBytes(apiKey);
                var hashBytes = sha256.ComputeHash(keyBytes);
                _apiKeyHash = Convert.ToBase64String(hashBytes);
            }
            
            _httpListener = new HttpListener();
            _logger.LogInformation("课表API控制器已创建");
        }

        /// <summary>
        /// 启动API服务器
        /// </summary>
        public async Task StartAsync()
        {
            if (IsRunning)
            {
                _logger.LogInformation("API服务器已经在运行中");
                return;
            }
            
            try
            {
                _cts = new CancellationTokenSource();
                bool bindSuccess = false;
                
                // 首先尝试检测当前端口是否可用
                TcpListener portTest = null;
                try 
                {
                    portTest = new TcpListener(IPAddress.Loopback, Port);
                    portTest.Start();
                    portTest.Stop();
                    _logger.LogInformation("端口 {Port} 可用", Port);
                } 
                catch (SocketException se) 
                {
                    _logger.LogWarning("端口 {Port} 已被占用，错误代码: {ErrorCode}", Port, se.ErrorCode);
                    // 端口已被占用，增加端口号尝试下一个
                    Port = BASE_PORT + _retryCount * RETRY_PORT_INCREMENT;
                    _logger.LogInformation("将尝试使用新端口: {Port}", Port);
                } 
                finally 
                {
                    portTest?.Stop();
                }
                
                // 清空现有前缀
                _httpListener.Prefixes.Clear();
                
                // 多级绑定策略 - 从最宽松到最严格的绑定方式
                var bindingStrategies = new List<(string description, Func<bool> bindingAction)>
                {
                    ("所有网络接口 http://+:{Port}/" + API_PATH_PREFIX + "/", () => {
                        try {
                            _httpListener.Prefixes.Add($"http://+:{Port}/{API_PATH_PREFIX}/");
                            _httpListener.Start();
                            _logger.LogInformation("API服务器已成功绑定到所有网络接口，端口: {Port}", Port);
                            return true;
                        } catch (Exception ex) {
                            _logger.LogWarning(ex, "无法绑定到所有网络接口，将尝试下一个绑定策略");
                            _httpListener.Prefixes.Clear();
                            return false;
                        }
                    }),
                    
                    ("指定IP地址", () => {
                        try {
                            string hostName = Dns.GetHostName();
                            IPAddress[] localIPs = Dns.GetHostAddresses(hostName);
                            bool anyIpBound = false;
                            
                            foreach (var ip in localIPs.Where(ip => ip.AddressFamily == AddressFamily.InterNetwork))
                            {
                                try {
                                    string prefix = $"http://{ip}:{Port}/{API_PATH_PREFIX}/";
                                    _httpListener.Prefixes.Add(prefix);
                                    _logger.LogInformation("添加绑定前缀: {Prefix}", prefix);
                                    anyIpBound = true;
                                } catch (Exception ipEx) {
                                    _logger.LogWarning(ipEx, "无法添加IP绑定前缀: {IP}", ip);
                                }
                            }
                            
                            if (anyIpBound) {
                                _httpListener.Start();
                                _logger.LogInformation("API服务器已成功绑定到特定IP地址，端口: {Port}", Port);
                                return true;
                            }
                            return false;
                        } catch (Exception ex) {
                            _logger.LogWarning(ex, "无法绑定到特定IP地址，将尝试下一个绑定策略");
                            _httpListener.Prefixes.Clear();
                            return false;
                        }
                    }),
                    
                    ("仅本地 http://localhost:{Port}/" + API_PATH_PREFIX + "/", () => {
                        try {
                            _httpListener.Prefixes.Add($"http://localhost:{Port}/{API_PATH_PREFIX}/");
                            _httpListener.Start();
                            _logger.LogInformation("API服务器已成功绑定到localhost，端口: {Port}", Port);
                            return true;
                        } catch (Exception ex) {
                            _logger.LogWarning(ex, "无法绑定到localhost，绑定策略全部失败");
                            _httpListener.Prefixes.Clear();
                            return false;
                        }
                    }),
                    
                    ("回环地址 http://127.0.0.1:{Port}/" + API_PATH_PREFIX + "/", () => {
                        try {
                            _httpListener.Prefixes.Add($"http://127.0.0.1:{Port}/{API_PATH_PREFIX}/");
                            _httpListener.Start();
                            _logger.LogInformation("API服务器已成功绑定到回环地址，端口: {Port}", Port);
                            return true;
                        } catch (Exception ex) {
                            _logger.LogError(ex, "所有绑定策略失败");
                            _httpListener.Prefixes.Clear();
                            return false;
                        }
                    })
                };
                
                // 依次尝试各种绑定策略
                foreach (var (description, bindingAction) in bindingStrategies)
                {
                    _logger.LogInformation("尝试绑定策略: {Description}", description);
                    if (bindingAction())
                    {
                        bindSuccess = true;
                        break;
                    }
                }
                
                if (!bindSuccess)
                {
                    throw new InvalidOperationException($"无法绑定到端口 {Port}，所有绑定策略都失败了");
                }
                
                // 验证服务器是否真的在监听
                if (!_httpListener.IsListening)
                {
                    _logger.LogError("API服务器未能正常监听！");
                    throw new InvalidOperationException("API服务器未能正常监听，尽管没有抛出异常");
                }
                
                IsRunning = true;
                
                // 打印可用的访问地址
                LogAccessUrls();
                
                // 启动请求处理
                _serverTask = HandleRequestsAsync(_cts.Token);
                _logger.LogInformation("API请求处理线程已启动");
            }
            catch (Exception ex)
            {
                IsRunning = false;
                _logger.LogError(ex, "启动API服务器失败。详细错误: {Message}", ex.Message);
                
                // 尝试再次启动，但使用不同的端口
                if (_retryCount < MAX_RETRY_COUNT)
                {
                    _retryCount++;
                    Port = BASE_PORT + _retryCount * RETRY_PORT_INCREMENT;
                    _logger.LogWarning("尝试使用新端口重启API服务器: {Port} (尝试 {RetryCount}/{MaxRetryCount})", 
                        Port, _retryCount, MAX_RETRY_COUNT);
                    await StartAsync();
                    return;
                }
                else
                {
                    _logger.LogError("已达到最大重试次数 ({MaxRetryCount})，API服务器启动失败", MAX_RETRY_COUNT);
                    throw;
                }
            }
        }

        /// <summary>
        /// 记录可用的访问URL
        /// </summary>
        private void LogAccessUrls()
        {
            _logger.LogInformation("API服务器已启动。您可以通过以下地址访问API服务：");
            
            if (_httpListener.Prefixes.Any(p => p.Contains("://+:")))
            {
                _logger.LogInformation(" - 所有网络接口: http://[您的IP地址]:{Port}/{Prefix}/schedule", Port, API_PATH_PREFIX);
                
                try
                {
                    string hostName = Dns.GetHostName();
                    IPAddress[] localIPs = Dns.GetHostAddresses(hostName);
                    
                    foreach (var ip in localIPs.Where(ip => ip.AddressFamily == AddressFamily.InterNetwork))
                    {
                        _logger.LogInformation(" - 网络接口: http://{IP}:{Port}/{Prefix}/schedule", ip, Port, API_PATH_PREFIX);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "获取本地IP地址时出错");
                }
            }
            
            if (_httpListener.Prefixes.Any(p => p.Contains("://localhost:")))
            {
                _logger.LogInformation(" - 本地访问: http://localhost:{Port}/{Prefix}/schedule", Port, API_PATH_PREFIX);
            }
            
            if (_httpListener.Prefixes.Any(p => p.Contains("://127.0.0.1:")))
            {
                _logger.LogInformation(" - 回环地址: http://127.0.0.1:{Port}/{Prefix}/schedule", Port, API_PATH_PREFIX);
            }
            
            _logger.LogInformation("使用API需要访问令牌，可通过查询参数提供: ?apiKey=您的令牌");
            _logger.LogInformation("或者通过Authorization头提供: Bearer 您的令牌");
            _logger.LogInformation("API提供以下端点: {Prefix}/schedule, {Prefix}/timeLayout, {Prefix}/subjects", API_PATH_PREFIX);
        }

        /// <summary>
        /// 停止API服务器
        /// </summary>
        public async Task StopAsync()
        {
            if (!IsRunning)
            {
                return;
            }
            
            try
            {
                _cts?.Cancel();
                
                if (_httpListener.IsListening)
                {
                    _httpListener.Stop();
                }
                
                if (_serverTask != null)
                {
                    // 设置超时，避免无限等待
                    var timeoutTask = Task.Delay(3000);
                    await Task.WhenAny(_serverTask, timeoutTask);
                }
                
                IsRunning = false;
                _logger.LogInformation("API服务器已停止");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "停止API服务器时出错");
            }
        }

        /// <summary>
        /// 处理所有API请求
        /// </summary>
        private async Task HandleRequestsAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && _httpListener.IsListening)
            {
                try
                {
                    var context = await _httpListener.GetContextAsync();
                    var request = context.Request;
                    var response = context.Response;
                    
                    // 添加CORS头
                    response.AddHeader("Access-Control-Allow-Origin", "*");
                    response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                    response.AddHeader("Access-Control-Allow-Headers", "Content-Type, Authorization");
                    
                    // 处理OPTIONS请求（预检请求）
                    if (request.HttpMethod == "OPTIONS")
                    {
                        response.StatusCode = 200;
                        response.Close();
                        continue;
                    }
                    
                    // 验证API密钥
                    if (!ValidateApiKey(request))
                    {
                        response.StatusCode = 401;
                        await WriteJsonResponse(response, new { error = "未授权访问：无效的API密钥" });
                        continue;
                    }
                    
                    // 根据请求路径和方法分发处理
                    var path = request.Url.AbsolutePath.TrimEnd('/');
                    
                    try
                    {
                        if (path.EndsWith($"/{API_PATH_PREFIX}/schedule"))
                        {
                            if (request.HttpMethod == "GET")
                            {
                                await HandleGetScheduleRequest(request, response);
                            }
                            else if (request.HttpMethod == "POST")
                            {
                                await HandleUpdateScheduleRequest(request, response);
                            }
                            else
                            {
                                response.StatusCode = 405; // Method Not Allowed
                                await WriteJsonResponse(response, new { error = "不支持的HTTP方法" });
                            }
                        }
                        else if (path.EndsWith($"/{API_PATH_PREFIX}/timeLayout"))
                        {
                            if (request.HttpMethod == "GET")
                            {
                                await HandleGetTimeLayoutRequest(request, response);
                            }
                            else if (request.HttpMethod == "POST")
                            {
                                await HandleUpdateTimeLayoutRequest(request, response);
                            }
                            else
                            {
                                response.StatusCode = 405; // Method Not Allowed
                                await WriteJsonResponse(response, new { error = "不支持的HTTP方法" });
                            }
                        }
                        else if (path.EndsWith($"/{API_PATH_PREFIX}/subjects"))
                        {
                            if (request.HttpMethod == "GET")
                            {
                                await HandleGetSubjectsRequest(request, response);
                            }
                            else if (request.HttpMethod == "POST")
                            {
                                await HandleUpdateSubjectsRequest(request, response);
                            }
                            else
                            {
                                response.StatusCode = 405; // Method Not Allowed
                                await WriteJsonResponse(response, new { error = "不支持的HTTP方法" });
                            }
                        }
                        else
                        {
                            // 路径不匹配任何已知API
                            response.StatusCode = 404;
                            await WriteJsonResponse(response, new { 
                                error = "API端点不存在", 
                                availableEndpoints = new[] { 
                                    $"/{API_PATH_PREFIX}/schedule", 
                                    $"/{API_PATH_PREFIX}/timeLayout", 
                                    $"/{API_PATH_PREFIX}/subjects" 
                                } 
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "处理API请求时出错");
                        response.StatusCode = 500;
                        await WriteJsonResponse(response, new { error = "内部服务器错误", message = ex.Message });
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException)
                {
                    // 预期的取消异常，可以安全退出
                    _logger.LogInformation("API处理线程已取消");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "API处理线程异常");
                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                }
            }
            
            _logger.LogInformation("API处理线程已结束");
        }

        /// <summary>
        /// 验证API密钥
        /// </summary>
        private bool ValidateApiKey(HttpListenerRequest request)
        {
            try
            {
                // 从Authorization头中获取API密钥
                if (request.Headers["Authorization"] != null)
                {
                    var authHeader = request.Headers["Authorization"];
                    if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    {
                        var apiKey = authHeader.Substring("Bearer ".Length).Trim();
                        
                        // 计算提供的API密钥的哈希值并与存储的哈希值比较
                        using (var sha256 = SHA256.Create())
                        {
                            var keyBytes = Encoding.UTF8.GetBytes(apiKey);
                            var hashBytes = sha256.ComputeHash(keyBytes);
                            var providedKeyHash = Convert.ToBase64String(hashBytes);
                            
                            return string.Equals(_apiKeyHash, providedKeyHash);
                        }
                    }
                }
                
                // 如果没有Authorization头或格式不正确，尝试从查询参数获取
                if (request.QueryString["apiKey"] != null)
                {
                    var apiKey = request.QueryString["apiKey"];
                    
                    // 计算提供的API密钥的哈希值并与存储的哈希值比较
                    using (var sha256 = SHA256.Create())
                    {
                        var keyBytes = Encoding.UTF8.GetBytes(apiKey);
                        var hashBytes = sha256.ComputeHash(keyBytes);
                        var providedKeyHash = Convert.ToBase64String(hashBytes);
                        
                        return string.Equals(_apiKeyHash, providedKeyHash);
                    }
                }
                
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "验证API密钥时出错");
                return false;
            }
        }

        private async Task WriteJsonResponse(HttpListenerResponse response, object data)
        {
            var json = JsonConvert.SerializeObject(data, Formatting.Indented);
            var buffer = Encoding.UTF8.GetBytes(json);
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            response.Close();
        }

        #region API处理程序

        /// <summary>
        /// 处理获取课表请求
        /// </summary>
        private async Task HandleGetScheduleRequest(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (_lessonsService == null)
            {
                response.StatusCode = 503; // Service Unavailable
                await WriteJsonResponse(response, new { error = "课表服务不可用" });
                return;
            }

            if (!_lessonsService.IsClassPlanLoaded)
            {
                response.StatusCode = 404; // Not Found
                await WriteJsonResponse(response, new { error = "未加载课表" });
                return;
            }

            try
            {
                var currentPlan = _lessonsService.CurrentClassPlan;
                if (currentPlan == null)
                {
                    response.StatusCode = 404;
                    await WriteJsonResponse(response, new { error = "当前没有课表" });
                    return;
                }

                // 将ClassPlan转换为JSON友好格式
                var planData = new
                {
                    name = currentPlan.Name,
                    classes = currentPlan.Classes.Select((c, index) => new {
                        index = index,
                        subjectId = c.SubjectId,
                        subjectName = _profileService.Profile.Subjects.TryGetValue(c.SubjectId, out var subject) ? subject.Name : "未知科目"
                    }).ToList(),
                    timeLayoutName = currentPlan.TimeLayout?.Name ?? "未指定"
                };

                await WriteJsonResponse(response, new { success = true, plan = planData });
                _logger.LogInformation("已处理获取课表请求");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取课表信息时出错");
                response.StatusCode = 500;
                await WriteJsonResponse(response, new { error = "获取课表失败", message = ex.Message });
            }
        }

        /// <summary>
        /// 处理更新课表请求
        /// </summary>
        private async Task HandleUpdateScheduleRequest(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (_lessonsService == null || _profileService == null)
            {
                response.StatusCode = 503; // Service Unavailable
                await WriteJsonResponse(response, new { error = "课表服务不可用" });
                return;
            }

            try
            {
                // 读取请求体
                string requestBody;
                using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
                {
                    requestBody = await reader.ReadToEndAsync();
                }

                // 解析JSON数据
                var schedulePlanData = JsonConvert.DeserializeObject<SchedulePlanDTO>(requestBody);
                
                if (schedulePlanData == null)
                {
                    response.StatusCode = 400; // Bad Request
                    await WriteJsonResponse(response, new { error = "无效的课表数据格式" });
                    return;
                }

                // 获取当前课表并更新
                var profile = _profileService.Profile;
                var targetPlan = profile.ClassPlans.Values
                    .FirstOrDefault(cp => cp.Name == schedulePlanData.Name);
                
                if (targetPlan == null)
                {
                    // 如果找不到匹配的课表，创建新的
                    targetPlan = new ClassPlan { 
                        Name = schedulePlanData.Name
                    };
                    
                    // 生成唯一标识符
                    string planId = Guid.NewGuid().ToString();
                    
                    // 添加到课表集合中
                    profile.ClassPlans[planId] = targetPlan;
                }
                
                // 更新课表信息
                UpdateClassPlanFromDTO(targetPlan, schedulePlanData);
                
                // 保存更新后的档案
                _profileService.SaveProfile();
                
                await WriteJsonResponse(response, new { success = true, message = "课表更新成功" });
                _logger.LogInformation("已处理更新课表请求");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新课表时出错");
                response.StatusCode = 500;
                await WriteJsonResponse(response, new { error = "更新课表失败", message = ex.Message });
            }
        }

        /// <summary>
        /// 处理获取时间表请求
        /// </summary>
        private async Task HandleGetTimeLayoutRequest(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (_lessonsService == null || _profileService == null)
            {
                response.StatusCode = 503; // Service Unavailable
                await WriteJsonResponse(response, new { error = "课表服务不可用" });
                return;
            }

            try
            {
                // 获取所有时间表
                var timeLayouts = _profileService.Profile.TimeLayouts;
                
                if (timeLayouts == null || timeLayouts.Count == 0)
                {
                    response.StatusCode = 404;
                    await WriteJsonResponse(response, new { error = "未找到时间表" });
                    return;
                }

                // 转换为适合API返回的格式
                var timeLayoutsData = timeLayouts.Select(pair => new {
                    id = pair.Key,
                    name = pair.Value.Name,
                    layouts = pair.Value.Layouts.Select((l, idx) => new {
                        startTime = l.StartSecond.TimeOfDay.TotalSeconds,
                        endTime = l.EndSecond.TimeOfDay.TotalSeconds,
                        name = $"第{idx+1}节课",
                        type = l.TimeType
                    }).ToList()
                }).ToList();

                await WriteJsonResponse(response, new { success = true, timeLayouts = timeLayoutsData });
                _logger.LogInformation("已处理获取时间表请求");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取时间表时出错");
                response.StatusCode = 500;
                await WriteJsonResponse(response, new { error = "获取时间表失败", message = ex.Message });
            }
        }

        /// <summary>
        /// 处理更新时间表请求
        /// </summary>
        private async Task HandleUpdateTimeLayoutRequest(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (_lessonsService == null || _profileService == null)
            {
                response.StatusCode = 503; // Service Unavailable
                await WriteJsonResponse(response, new { error = "课表服务不可用" });
                return;
            }

            try
            {
                // 读取请求体
                string requestBody;
                using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
                {
                    requestBody = await reader.ReadToEndAsync();
                }

                // 解析JSON数据
                var timeLayoutData = JsonConvert.DeserializeObject<TimeLayoutDTO>(requestBody);
                
                if (timeLayoutData == null || timeLayoutData.Items == null || timeLayoutData.Items.Count == 0)
                {
                    response.StatusCode = 400; // Bad Request
                    await WriteJsonResponse(response, new { error = "无效的时间表数据格式" });
                    return;
                }

                // 查找或创建时间表
                var profile = _profileService.Profile;
                var timeLayoutKey = profile.TimeLayouts.FirstOrDefault(tl => tl.Value.Name == timeLayoutData.Name).Key;
                TimeLayout timeLayout;
                
                if (string.IsNullOrEmpty(timeLayoutKey))
                {
                    // 创建新的时间表
                    timeLayout = new TimeLayout { 
                        Name = timeLayoutData.Name
                    };
                    
                    // 生成唯一标识符
                    timeLayoutKey = Guid.NewGuid().ToString();
                    
                    // 添加到时间表集合
                    profile.TimeLayouts[timeLayoutKey] = timeLayout;
                }
                else
                {
                    timeLayout = profile.TimeLayouts[timeLayoutKey];
                }
                
                // 更新时间表项目
                UpdateTimeLayoutFromDTO(timeLayout, timeLayoutData);
                
                // 保存更新后的档案
                _profileService.SaveProfile();
                
                await WriteJsonResponse(response, new { success = true, message = "时间表更新成功" });
                _logger.LogInformation("已处理更新时间表请求");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新时间表时出错");
                response.StatusCode = 500;
                await WriteJsonResponse(response, new { error = "更新时间表失败", message = ex.Message });
            }
        }

        /// <summary>
        /// 处理获取科目列表请求
        /// </summary>
        private async Task HandleGetSubjectsRequest(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (_lessonsService == null || _profileService == null)
            {
                response.StatusCode = 503; // Service Unavailable
                await WriteJsonResponse(response, new { error = "课表服务不可用" });
                return;
            }

            try
            {
                // 获取所有科目
                var subjects = _profileService.Profile.Subjects;
                
                if (subjects == null || subjects.Count == 0)
                {
                    response.StatusCode = 404;
                    await WriteJsonResponse(response, new { error = "未找到科目列表" });
                    return;
                }

                // 转换为适合API返回的格式
                var subjectsData = subjects.Select(pair => new {
                    id = pair.Key,
                    name = pair.Value.Name,
                    color = "#FFFFFF", // 使用默认颜色
                    initial = pair.Value.Initial,
                    teacherName = pair.Value.TeacherName
                }).ToList();

                await WriteJsonResponse(response, new { success = true, subjects = subjectsData });
                _logger.LogInformation("已处理获取科目列表请求");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取科目列表时出错");
                response.StatusCode = 500;
                await WriteJsonResponse(response, new { error = "获取科目列表失败", message = ex.Message });
            }
        }

        /// <summary>
        /// 处理更新科目列表请求
        /// </summary>
        private async Task HandleUpdateSubjectsRequest(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (_lessonsService == null || _profileService == null)
            {
                response.StatusCode = 503; // Service Unavailable
                await WriteJsonResponse(response, new { error = "课表服务不可用" });
                return;
            }

            try
            {
                // 读取请求体
                string requestBody;
                using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
                {
                    requestBody = await reader.ReadToEndAsync();
                }

                // 解析JSON数据
                var subjectsData = JsonConvert.DeserializeObject<List<SubjectDTO>>(requestBody);
                
                if (subjectsData == null || subjectsData.Count == 0)
                {
                    response.StatusCode = 400; // Bad Request
                    await WriteJsonResponse(response, new { error = "无效的科目数据格式" });
                    return;
                }

                // 更新科目
                var profile = _profileService.Profile;
                
                foreach (var subjectDto in subjectsData)
                {
                    string subjectKey = profile.Subjects.FirstOrDefault(s => 
                        s.Value.Name == subjectDto.Name || s.Key == subjectDto.Id).Key;
                    
                    Subject subject;
                    
                    if (string.IsNullOrEmpty(subjectKey))
                    {
                        // 创建新科目
                        subject = new Subject
                        {
                            Name = subjectDto.Name
                        };
                        
                        // 生成唯一标识符
                        subjectKey = !string.IsNullOrEmpty(subjectDto.Id) ? subjectDto.Id : Guid.NewGuid().ToString();
                        
                        profile.Subjects[subjectKey] = subject;
                    }
                    else
                    {
                        subject = profile.Subjects[subjectKey];
                    }
                    
                    // 更新科目信息
                    UpdateSubjectFromDTO(subject, subjectDto);
                }
                
                // 保存更新后的档案
                _profileService.SaveProfile();
                
                await WriteJsonResponse(response, new { success = true, message = "科目列表更新成功" });
                _logger.LogInformation("已处理更新科目列表请求");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新科目列表时出错");
                response.StatusCode = 500;
                await WriteJsonResponse(response, new { error = "更新科目列表失败", message = ex.Message });
            }
        }

        #endregion

        #region 辅助方法

        /// <summary>
        /// 将DTO转换为领域模型（这些方法需要根据实际的领域模型进行实现）
        /// </summary>
        private void UpdateClassPlanFromDTO(ClassPlan classPlan, SchedulePlanDTO dto)
        {
            classPlan.Name = dto.Name;
            
            // 由于ClassPlan没有直接的Days属性，我们需要使用其他方法更新课程信息
            // 首先清空当前课程列表
            classPlan.Classes.Clear();
            
            // 根据DTO添加新的课程
            foreach (var daySchedule in dto.Days)
            {
                // 设置课程的星期几和时间等信息
                int dayOfWeek = daySchedule.DayOfWeek;
                
                foreach (var classDto in daySchedule.Classes)
                {
                    // 创建新的课程信息
                    var classInfo = new ClassInfo
                    {
                        SubjectId = classDto.SubjectId
                    };
                    
                    // 添加到课程列表
                    classPlan.Classes.Add(classInfo);
                }
            }
        }

        private void UpdateTimeLayoutFromDTO(TimeLayout timeLayout, TimeLayoutDTO dto)
        {
            timeLayout.Name = dto.Name;
            timeLayout.Layouts.Clear();
            
            foreach (var item in dto.Items)
            {
                var startTime = TimeSpan.FromSeconds(item.StartSecond);
                var endTime = TimeSpan.FromSeconds(item.EndSecond);
                
                // 创建时间表项目并设置属性
                var layoutItem = new TimeLayoutItem
                {
                    StartSecond = DateTime.Today.Add(startTime),
                    EndSecond = DateTime.Today.Add(endTime)
                };
                
                // 设置其他属性（如果在TimeLayoutItem中可用）
                // layoutItem.Index = item.Index;
                // layoutItem.Name = item.Name;
                layoutItem.TimeType = item.Type;
                
                timeLayout.Layouts.Add(layoutItem);
            }
        }

        private void UpdateSubjectFromDTO(Subject subject, SubjectDTO dto)
        {
            subject.Name = dto.Name;
            subject.Initial = dto.Icon ?? dto.Name?.Substring(0, 1);
            subject.TeacherName = dto.DefaultTeacher;
        }

        #endregion
    }
} 