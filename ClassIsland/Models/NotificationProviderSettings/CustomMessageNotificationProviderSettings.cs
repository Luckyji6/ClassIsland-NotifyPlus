using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassIsland.Models.NotificationProviderSettings;

/// <summary>
/// 自定义提醒文字设置类
/// </summary>
public class CustomMessageNotificationProviderSettings : ObservableRecipient
{
    private string _customMessage = "这是一条自定义提醒";
    private int _displayDurationSeconds = 5;
    private bool _useSpeech = false;
    private bool _useEmphasisEffect = true;
    private bool _useNotificationSound = true;
    private bool _useTopmost = false;

    /// <summary>
    /// 自定义提醒文字
    /// </summary>
    public string CustomMessage
    {
        get => _customMessage;
        set
        {
            if (value == _customMessage) return;
            _customMessage = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 显示持续时间（秒）
    /// </summary>
    public int DisplayDurationSeconds
    {
        get => _displayDurationSeconds;
        set
        {
            if (value == _displayDurationSeconds) return;
            _displayDurationSeconds = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 是否启用语音朗读
    /// </summary>
    public bool UseSpeech
    {
        get => _useSpeech;
        set
        {
            if (value == _useSpeech) return;
            _useSpeech = value;
            OnPropertyChanged();
        }
    }
    
    /// <summary>
    /// 是否使用强调特效
    /// </summary>
    public bool UseEmphasisEffect
    {
        get => _useEmphasisEffect;
        set
        {
            if (value == _useEmphasisEffect) return;
            _useEmphasisEffect = value;
            OnPropertyChanged();
        }
    }
    
    /// <summary>
    /// 是否使用提醒音效
    /// </summary>
    public bool UseNotificationSound
    {
        get => _useNotificationSound;
        set
        {
            if (value == _useNotificationSound) return;
            _useNotificationSound = value;
            OnPropertyChanged();
        }
    }
    
    /// <summary>
    /// 是否置顶显示
    /// </summary>
    public bool UseTopmost
    {
        get => _useTopmost;
        set
        {
            if (value == _useTopmost) return;
            _useTopmost = value;
            OnPropertyChanged();
        }
    }
} 