using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using ClassIsland.Controls.NotificationProviders;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Models.NotificationProviderSettings;
using ClassIsland.Shared.Interfaces;
using ClassIsland.Shared.Models.Notification;
using MaterialDesignThemes.Wpf;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClassIsland.Services.NotificationProviders;

/// <summary>
/// 自定义提醒文字提供方
/// </summary>
public class CustomMessageNotificationProvider : INotificationProvider, IHostedService
{
    public string Name { get; set; } = "自定义提醒";
    public string Description { get; set; } = "显示用户自定义的提醒文字。";
    public Guid ProviderGuid { get; set; } = new Guid("8F3E9D1A-2B5C-4D6E-B8C9-5A7E6F4D3B2A");
    public object? SettingsElement { get; set; }
    public object? IconElement { get; set; } = new PackIcon()
    {
        Kind = PackIconKind.Message,
        Width = 24,
        Height = 24
    };

    private INotificationHostService NotificationHostService { get; }
    private ILogger<CustomMessageNotificationProvider> Logger { get; }
    public CustomMessageNotificationProviderSettings Settings { get; }

    public CustomMessageNotificationProvider(
        INotificationHostService notificationHostService,
        ILogger<CustomMessageNotificationProvider> logger)
    {
        NotificationHostService = notificationHostService;
        Logger = logger;

        // 注册到提醒主机
        NotificationHostService.RegisterNotificationProvider(this);
        
        // 获取或创建设置
        Settings = NotificationHostService.GetNotificationProviderSettings<CustomMessageNotificationProviderSettings>(ProviderGuid);
        
        // 记录当前设置状态
        Logger.LogInformation("自定义提醒初始化，当前设置 - " +
                              "消息: \"{0}\", " +
                              "声音: {1}, " +
                              "特效: {2}, " +
                              "置顶: {3}, " +
                              "语音: {4}, " +
                              "持续: {5}秒",
            Settings.CustomMessage,
            Settings.UseNotificationSound,
            Settings.UseEmphasisEffect,
            Settings.UseTopmost,
            Settings.UseSpeech,
            Settings.DisplayDurationSeconds);
        
        // 设置界面元素
        SettingsElement = new CustomMessageNotificationProviderSettingsControl(Settings);
    }

    /// <summary>
    /// 显示自定义提醒
    /// </summary>
    public void ShowCustomNotification()
    {
        // 如果声音设置是关闭的，记录一个调试信息
        if (!Settings.UseNotificationSound)
        {
            Logger.LogWarning("声音设置当前为关闭状态，请在高级设置中打开\"提醒音效\"选项");
        }
        
        Logger.LogInformation("显示自定义提醒：{0}，声音设置：{1}", 
            Settings.CustomMessage,
            Settings.UseNotificationSound);
        
        // 确保在UI线程上执行
        if (Application.Current != null && Application.Current.Dispatcher != null && 
            !Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.Invoke(() => ShowCustomNotification());
            return;
        }
        
        try
        {
            // 创建通知请求
            var request = new NotificationRequest()
            {
                MaskContent = new StackPanel()
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new PackIcon() { Kind = PackIconKind.MessageOutline, Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock(new Run(Settings.CustomMessage)) { FontSize = 18, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) }
                    }
                },
                // 语音相关设置
                MaskDuration = TimeSpan.FromSeconds(Settings.DisplayDurationSeconds)
            };
            
            // 确保RequestNotificationSettings不为null
            if (request.RequestNotificationSettings == null)
            {
                request.RequestNotificationSettings = new NotificationSettings();
                Logger.LogDebug("创建了新的NotificationSettings实例");
            }
            
            // 设置通知相关设置 - 确保这些设置被应用
            var settings = request.RequestNotificationSettings;
            settings.IsSettingsEnabled = true;
            settings.IsNotificationEffectEnabled = Settings.UseEmphasisEffect;
            settings.IsNotificationTopmostEnabled = Settings.UseTopmost;
            settings.IsNotificationSoundEnabled = Settings.UseNotificationSound;
            
            // 重要：在设置完RequestNotificationSettings后，确保语音设置正确应用
            if (Settings.UseSpeech)
            {
                // 直接设置在Request对象上的语音属性
                request.IsSpeechEnabled = true;
                request.MaskSpeechContent = Settings.CustomMessage;
                request.OverlaySpeechContent = Settings.CustomMessage;
                
                // 同时可能需要在settings中再次确认
                settings.IsSpeechEnabled = true;
                
                Logger.LogDebug("语音设置已启用 - 内容: \"{0}\"", Settings.CustomMessage);
            }
            else
            {
                request.IsSpeechEnabled = false;
                Logger.LogDebug("语音设置已禁用");
            }
            
            // 记录最终设置状态
            Logger.LogDebug("通知最终设置: IsSettingsEnabled={0}, 特效={1}, 声音={2}, 置顶={3}, 语音={4}",
                settings.IsSettingsEnabled,
                settings.IsNotificationEffectEnabled,
                settings.IsNotificationSoundEnabled,
                settings.IsNotificationTopmostEnabled,
                request.IsSpeechEnabled);
            
            // 显示通知
            NotificationHostService.ShowNotification(request);
            
            // 成功显示通知后，确保将设置保存到配置中
            var notificationSettings = NotificationHostService.GetNotificationProviderSettings<CustomMessageNotificationProviderSettings>(ProviderGuid);
            
            // 检查设置是否一致，如果不一致则更新
            if (notificationSettings.UseNotificationSound != Settings.UseNotificationSound)
            {
                notificationSettings.UseNotificationSound = Settings.UseNotificationSound;
                Logger.LogInformation("更新并保存声音设置：{0}", Settings.UseNotificationSound);
            }
            
            Logger.LogInformation("成功显示通知，声音设置为：{0}", Settings.UseNotificationSound);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "显示自定义提醒时发生错误: {0}", ex.Message);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // 启动服务时不需要做特殊处理
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // 停止服务时不需要做特殊处理
        return Task.CompletedTask;
    }
} 