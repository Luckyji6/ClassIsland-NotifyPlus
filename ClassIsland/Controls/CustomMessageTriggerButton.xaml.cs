using System.Windows;
using System.Windows.Controls;
using ClassIsland.Core;
using ClassIsland.Services.NotificationProviders;
using Microsoft.Extensions.DependencyInjection;

namespace ClassIsland.Controls;

/// <summary>
/// CustomMessageTriggerButton.xaml 的交互逻辑
/// </summary>
public partial class CustomMessageTriggerButton : UserControl
{
    private static CustomMessageNotificationProvider? _cachedProvider;
    
    public CustomMessageTriggerButton()
    {
        InitializeComponent();
    }

    private void ShowNotificationButton_Click(object sender, RoutedEventArgs e)
    {
        // 使用缓存的提供者或从AppHost获取新的
        if (_cachedProvider == null)
        {
            _cachedProvider = App.GetService<CustomMessageNotificationProvider>();
        }
        
        if (_cachedProvider != null)
        {
            _cachedProvider.ShowCustomNotification();
        }
        else
        {
            MessageBox.Show("未找到自定义提醒服务，请确保服务已正确注册。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
} 