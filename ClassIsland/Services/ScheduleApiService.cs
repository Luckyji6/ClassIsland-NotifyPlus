using System;
using System.Threading;
using System.Threading.Tasks;
using ClassIsland.API.Controllers;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClassIsland.Services
{
    /// <summary>
    /// 提供学校排课系统API的托管服务
    /// </summary>
    public class ScheduleApiService : IHostedService
    {
        private readonly ILogger<ScheduleApiService> _logger;
        private readonly ILessonsService _lessonsService;
        private readonly IProfileService _profileService;
        private readonly MessageSecurityService _securityService;
        private ScheduleApiController _apiController;
        private bool _isStarted = false;

        /// <summary>
        /// 初始化ScheduleApiService
        /// </summary>
        public ScheduleApiService(
            ILogger<ScheduleApiService> logger,
            ILoggerFactory loggerFactory,
            ILessonsService lessonsService,
            IProfileService profileService,
            MessageSecurityService securityService)
        {
            _logger = logger;
            _lessonsService = lessonsService;
            _profileService = profileService;
            _securityService = securityService;
            
            // 默认使用与WebMessageServer相同的密钥作为API密钥
            // 这样用户只需要配置一次密钥即可
            string apiKey = _securityService.IsTokenConfigured 
                ? _securityService.GetCurrentToken() ?? "default-api-key" 
                : "default-api-key";
            
            _logger.LogInformation("获取的API密钥状态: {IsConfigured}", _securityService.IsTokenConfigured ? "已配置" : "使用默认值");
            
            // 使用LoggerFactory创建正确类型的Logger
            var controllerLogger = loggerFactory.CreateLogger<ScheduleApiController>();
            
            _apiController = new ScheduleApiController(
                logger: controllerLogger,
                lessonsService: _lessonsService,
                profileService: _profileService,
                securityService: _securityService,
                apiKey: apiKey
            );
            
            // 使用与WebMessageServer相同的端口8088
            _apiController.Port = 8088;
            
            _logger.LogInformation("Schedule API服务已初始化，将与WebMessageServer共享8088端口");
        }

        /// <summary>
        /// 启动API服务
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_isStarted)
            {
                _logger.LogInformation("Schedule API服务已经在运行中");
                return;
            }
            
            try
            {
                _logger.LogInformation("正在启动Schedule API服务(集成到WebMessageServer)...");
                _logger.LogInformation("由于Schedule API服务与WebMessageServer共享端口，所以无需单独启动");
                _isStarted = true;
                _logger.LogInformation("Schedule API服务已成功集成到WebMessageServer，共享端口: 8088");
                
                // 检查是否有管理员权限
                bool isAdmin = App.IsRunAsAdministrator();
                if (!isAdmin)
                {
                    _logger.LogWarning("当前应用程序不是以管理员身份运行，这可能会限制Web API的网络绑定能力");
                    _logger.LogWarning("如果遇到连接问题，建议以管理员身份重新启动应用程序");
                }
                
                // 提供测试命令示例
                _logger.LogInformation("您可以使用以下命令测试API是否正常工作：");
                _logger.LogInformation("curl -v http://localhost:8088/schedule-api/schedule?apiKey={Token}", 
                    _securityService.GetCurrentToken() ?? "您的访问令牌");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "启动Schedule API服务失败");
                _logger.LogError("详细错误: {Message}", ex.Message);
                _logger.LogError("API服务无法启动，请检查WebMessageServer是否正常运行");
            }
        }

        /// <summary>
        /// 停止API服务
        /// </summary>
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (!_isStarted)
            {
                return;
            }
            
            try
            {
                _logger.LogInformation("正在停止Schedule API服务...");
                _isStarted = false;
                _logger.LogInformation("Schedule API服务已停止");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "停止Schedule API服务时出错");
            }
        }
        
        /// <summary>
        /// 手动重启API服务
        /// </summary>
        public async Task RestartAsync()
        {
            _logger.LogInformation("正在重启Schedule API服务...");
            await StopAsync(CancellationToken.None);
            await Task.Delay(500); // 等待资源释放
            await StartAsync(CancellationToken.None);
            _logger.LogInformation("Schedule API服务重启完成，当前端口: 8088");
        }
        
        /// <summary>
        /// 获取API控制器当前使用的端口
        /// </summary>
        public int GetCurrentPort()
        {
            return 8088; // 固定使用8088端口
        }
        
        /// <summary>
        /// 获取API控制器是否正在运行
        /// </summary>
        public bool IsRunning()
        {
            return _isStarted;
        }
        
        /// <summary>
        /// 获取课程API处理器
        /// </summary>
        public ScheduleApiController GetApiController()
        {
            return _apiController;
        }
    }
} 