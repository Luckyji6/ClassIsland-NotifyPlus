using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using ClassIsland.Core.Helpers.Native;
using ClassIsland.Core.Models;
using Microsoft.Extensions.Logging;
using Windows.Win32;

namespace ClassIsland.Services
{
    /// <summary>
    /// 屏幕截图服务
    /// </summary>
    public class ScreenshotService
    {
        private readonly ILogger<ScreenshotService> _logger;

        public ScreenshotService(ILogger<ScreenshotService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// 获取全屏截图
        /// </summary>
        /// <returns>截图的字节数组</returns>
        public async Task<byte[]?> CaptureFullScreenAsync()
        {
            try
            {
                _logger.LogInformation("开始进行全屏截图...");
                
                var screen = Screen.PrimaryScreen;
                if (screen == null)
                {
                    _logger.LogError("无法获取主屏幕信息");
                    return null;
                }

                _logger.LogInformation("开始全屏截图，分辨率: {Width}x{Height}", 
                    screen.Bounds.Width, screen.Bounds.Height);

                var bounds = screen.Bounds;
                using var bitmap = new Bitmap(bounds.Width, bounds.Height);
                using var graphics = Graphics.FromImage(bitmap);
                
                // 从屏幕复制图像
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                
                // 添加时间水印
                AddTimestampWatermark(graphics, bitmap.Width, bitmap.Height);
                
                var result = BitmapToByteArray(bitmap);
                _logger.LogInformation("全屏截图完成，大小: {Size} bytes", result.Length);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "全屏截图失败");
                return null;
            }
        }

        /// <summary>
        /// 获取指定窗口的截图
        /// </summary>
        /// <param name="windowHandle">窗口句柄</param>
        /// <returns>截图的字节数组</returns>
        public async Task<byte[]?> CaptureWindowAsync(IntPtr windowHandle)
        {
            try
            {
                _logger.LogInformation("开始进行窗口截图...");
                
                if (windowHandle == IntPtr.Zero)
                {
                    _logger.LogError("无效的窗口句柄");
                    return null;
                }

                _logger.LogInformation("开始窗口截图，句柄: {Handle}", windowHandle);

                using var bitmap = WindowCaptureHelper.CaptureWindowBitBlt(windowHandle);
                if (bitmap == null)
                {
                    _logger.LogError("窗口截图失败，无法获取窗口图像");
                    return null;
                }
                
                // 添加时间水印
                using var graphics = Graphics.FromImage(bitmap);
                AddTimestampWatermark(graphics, bitmap.Width, bitmap.Height);
                
                var result = BitmapToByteArray(bitmap);
                _logger.LogInformation("窗口截图完成，大小: {Size} bytes", result.Length);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "窗口截图失败，句柄: {Handle}", windowHandle);
                return null;
            }
        }

        /// <summary>
        /// 获取所有可截图的窗口列表
        /// </summary>
        /// <returns>窗口信息列表</returns>
        public List<WindowInfo> GetAvailableWindows()
        {
            try
            {
                _logger.LogInformation("开始获取可用窗口列表...");
                
                // 使用非详细模式获取窗口列表，避免性能问题
                var windows = NativeWindowHelper.GetAllWindows(false);
                var availableWindows = new List<WindowInfo>();

                foreach (var window in windows)
                {
                    try
                    {
                        // 检查窗口是否可见
                        if (!window.IsVisible)
                            continue;

                        // 获取窗口详细信息
                        var detailedWindow = DesktopWindow.GetWindowByHWndDetailed(window.HWnd);
                        
                        // 过滤掉没有标题或标题太短的窗口
                        if (string.IsNullOrWhiteSpace(detailedWindow.WindowText) || detailedWindow.WindowText.Length < 2)
                            continue;

                        // 过滤掉一些系统窗口
                        if (IsSystemWindow(detailedWindow.WindowText))
                            continue;

                        // 过滤掉程序管理器等系统窗口
                        if (IsSystemWindow(detailedWindow.ClassName))
                            continue;

                        availableWindows.Add(new WindowInfo
                        {
                            Handle = detailedWindow.HWnd,
                            Title = detailedWindow.WindowText,
                            ProcessName = detailedWindow.OwnerProcess?.ProcessName ?? "未知"
                        });

                        _logger.LogDebug("找到窗口: {Title} ({ProcessName})", detailedWindow.WindowText, detailedWindow.OwnerProcess?.ProcessName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "获取窗口详细信息失败，句柄: {Handle}", window.HWnd);
                        continue;
                    }
                }

                _logger.LogInformation("获取到 {Count} 个可截图窗口", availableWindows.Count);
                return availableWindows.OrderBy(w => w.Title).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取可用窗口列表失败");
                return new List<WindowInfo>();
            }
        }

        /// <summary>
        /// 添加时间水印到图像
        /// </summary>
        /// <param name="graphics">图形对象</param>
        /// <param name="imageWidth">图像宽度</param>
        /// <param name="imageHeight">图像高度</param>
        private void AddTimestampWatermark(Graphics graphics, int imageWidth, int imageHeight)
        {
            try
            {
                var now = DateTime.Now;
                var timeText = $"ClassIsland 截图时间: {now:yyyy-MM-dd HH:mm:ss}";
                
                // 设置文本渲染质量
                graphics.TextRenderingHint = TextRenderingHint.AntiAlias;
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                
                // 动态计算字体大小（基于图像尺寸）
                var baseFontSize = Math.Max(12, Math.Min(imageWidth, imageHeight) / 60);
                using var font = new Font("Microsoft YaHei", baseFontSize, FontStyle.Bold);
                
                // 测量文本尺寸
                var textSize = graphics.MeasureString(timeText, font);
                
                // 计算水印位置（右下角，留出边距）
                var margin = Math.Max(10, baseFontSize);
                var x = imageWidth - textSize.Width - margin;
                var y = imageHeight - textSize.Height - margin;
                
                // 创建半透明背景
                var backgroundRect = new RectangleF(x - 8, y - 4, textSize.Width + 16, textSize.Height + 8);
                using var backgroundBrush = new SolidBrush(Color.FromArgb(160, 0, 0, 0)); // 60% 透明黑色
                graphics.FillRectangle(backgroundBrush, backgroundRect);
                
                // 绘制文本阴影
                using var shadowBrush = new SolidBrush(Color.FromArgb(100, 0, 0, 0));
                graphics.DrawString(timeText, font, shadowBrush, x + 1, y + 1);
                
                // 绘制主文本
                using var textBrush = new SolidBrush(Color.White);
                graphics.DrawString(timeText, font, textBrush, x, y);
                
                _logger.LogDebug("已添加时间水印: {TimeText}", timeText);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "添加时间水印失败");
            }
        }
        
        /// <summary>
        /// 将Bitmap转换为字节数组
        /// </summary>
        /// <param name="bitmap">位图对象</param>
        /// <param name="format">图像格式，默认为PNG</param>
        /// <returns>字节数组</returns>
        private byte[] BitmapToByteArray(Bitmap bitmap, ImageFormat? format = null)
        {
            format ??= ImageFormat.Png;
            
            using var memoryStream = new MemoryStream();
            bitmap.Save(memoryStream, format);
            return memoryStream.ToArray();
        }

        /// <summary>
        /// 判断是否为系统窗口
        /// </summary>
        /// <param name="windowTitleOrClassName">窗口标题或类名</param>
        /// <returns>是否为系统窗口</returns>
        private bool IsSystemWindow(string windowTitleOrClassName)
        {
            if (string.IsNullOrWhiteSpace(windowTitleOrClassName))
                return true;

            var systemWindows = new[]
            {
                "Program Manager", "Desktop Window Manager", "Windows Input Experience",
                "Microsoft Text Input Application", "Windows Shell Experience Host",
                "Windows Security Health Service", "Cortana", "SearchUI", "WorkerW", "Progman",
                "Shell_TrayWnd", "NotifyIconOverflowWindow", "Windows.UI.Core.CoreWindow"
            };

            return systemWindows.Any(sw => windowTitleOrClassName.Contains(sw, StringComparison.OrdinalIgnoreCase));
        }


    }

    /// <summary>
    /// 窗口信息
    /// </summary>
    public class WindowInfo
    {
        public IntPtr Handle { get; set; }
        public string Title { get; set; } = string.Empty;
        public string ProcessName { get; set; } = string.Empty;
    }
} 