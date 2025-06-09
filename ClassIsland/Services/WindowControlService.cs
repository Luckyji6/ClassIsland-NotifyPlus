using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using ClassIsland.Core.Helpers.Native;
using ClassIsland.Core.Models;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace ClassIsland.Services
{
    /// <summary>
    /// 窗口控制服务
    /// </summary>
    public class WindowControlService
    {
        private readonly ILogger<WindowControlService> _logger;
        private readonly ScreenshotService _screenshotService;

        public WindowControlService(ILogger<WindowControlService> logger, ScreenshotService screenshotService)
        {
            _logger = logger;
            _screenshotService = screenshotService;
        }

        /// <summary>
        /// 关闭指定窗口
        /// </summary>
        /// <param name="windowHandle">窗口句柄</param>
        /// <param name="forceClose">是否强制关闭</param>
        /// <returns>关闭结果</returns>
        public WindowCloseResult CloseWindow(IntPtr windowHandle, bool forceClose = false)
        {
            try
            {
                if (windowHandle == IntPtr.Zero)
                {
                    _logger.LogError("无效的窗口句柄");
                    return new WindowCloseResult
                    {
                        Success = false,
                        ErrorMessage = "无效的窗口句柄"
                    };
                }

                // 获取窗口信息用于日志
                var windowInfo = GetWindowInfo(windowHandle);
                _logger.LogInformation("尝试关闭窗口: {Title} (句柄: {Handle}, 进程: {ProcessName})", 
                    windowInfo.Title, windowHandle, windowInfo.ProcessName);

                // 检查是否为系统关键窗口
                if (IsSystemCriticalWindow(windowInfo))
                {
                    var errorMsg = $"拒绝关闭系统关键窗口: {windowInfo.Title} ({windowInfo.ProcessName})";
                    _logger.LogWarning(errorMsg);
                    return new WindowCloseResult
                    {
                        Success = false,
                        ErrorMessage = errorMsg
                    };
                }

                bool success;
                string method;

                if (forceClose)
                {
                    // 强制关闭：终止进程
                    success = ForceCloseWindow(windowHandle, windowInfo);
                    method = "强制终止进程";
                }
                else
                {
                    // 优雅关闭：发送关闭消息
                    success = GracefulCloseWindow(windowHandle);
                    method = "发送关闭消息";
                }

                if (success)
                {
                    _logger.LogInformation("成功关闭窗口: {Title} (方法: {Method})", windowInfo.Title, method);
                    return new WindowCloseResult
                    {
                        Success = true,
                        WindowTitle = windowInfo.Title,
                        ProcessName = windowInfo.ProcessName,
                        Method = method
                    };
                }
                else
                {
                    var errorMsg = $"关闭窗口失败: {windowInfo.Title} (方法: {method})";
                    _logger.LogError(errorMsg);
                    return new WindowCloseResult
                    {
                        Success = false,
                        ErrorMessage = errorMsg,
                        WindowTitle = windowInfo.Title,
                        ProcessName = windowInfo.ProcessName
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "关闭窗口时发生异常，句柄: {Handle}", windowHandle);
                return new WindowCloseResult
                {
                    Success = false,
                    ErrorMessage = $"关闭窗口时发生异常: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// 获取所有可关闭的窗口列表
        /// </summary>
        /// <returns>窗口信息列表</returns>
        public List<CloseableWindowInfo> GetCloseableWindows()
        {
            try
            {
                _logger.LogInformation("开始获取可关闭窗口列表...");
                
                var allWindows = _screenshotService.GetAvailableWindows();
                var closeableWindows = new List<CloseableWindowInfo>();

                foreach (var window in allWindows)
                {
                    try
                    {
                        var windowInfo = GetWindowInfo(window.Handle);
                        bool isCloseable = !IsSystemCriticalWindow(windowInfo);
                        bool isCurrentProcess = IsCurrentProcess(window.Handle);

                        closeableWindows.Add(new CloseableWindowInfo
                        {
                            Handle = window.Handle,
                            Title = window.Title,
                            ProcessName = window.ProcessName,
                            IsCloseable = isCloseable,
                            IsCurrentProcess = isCurrentProcess,
                            CloseableReason = isCloseable ? "可以关闭" : "系统关键窗口，不建议关闭"
                        });

                        _logger.LogDebug("窗口: {Title} ({ProcessName}) - 可关闭: {IsCloseable}", 
                            window.Title, window.ProcessName, isCloseable);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "获取窗口信息失败，句柄: {Handle}", window.Handle);
                        continue;
                    }
                }

                _logger.LogInformation("获取到 {Count} 个窗口，其中 {CloseableCount} 个可关闭", 
                    closeableWindows.Count, closeableWindows.Count(w => w.IsCloseable));
                
                return closeableWindows.OrderBy(w => w.Title).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取可关闭窗口列表失败");
                return new List<CloseableWindowInfo>();
            }
        }

        /// <summary>
        /// 优雅关闭窗口（发送WM_CLOSE消息）
        /// </summary>
        private bool GracefulCloseWindow(IntPtr windowHandle)
        {
            try
            {
                // 发送WM_CLOSE消息
                const int WM_CLOSE = 0x0010;
                var result = PInvoke.SendMessage((HWND)windowHandle, WM_CLOSE, 0, 0);
                
                // 等待一段时间检查窗口是否关闭
                System.Threading.Thread.Sleep(1000);
                
                // 检查窗口是否仍然存在
                return !PInvoke.IsWindowVisible((HWND)windowHandle);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "优雅关闭窗口失败，句柄: {Handle}", windowHandle);
                return false;
            }
        }

        /// <summary>
        /// 强制关闭窗口（终止进程）
        /// </summary>
        private bool ForceCloseWindow(IntPtr windowHandle, WindowControlInfo windowInfo)
        {
            try
            {
                uint processId = 0;
                unsafe
                {
                    PInvoke.GetWindowThreadProcessId((HWND)windowHandle, &processId);
                }

                if (processId == 0)
                {
                    _logger.LogError("无法获取窗口的进程ID，句柄: {Handle}", windowHandle);
                    return false;
                }

                // 不允许终止当前进程
                if (processId == Environment.ProcessId)
                {
                    _logger.LogWarning("拒绝终止当前进程");
                    return false;
                }

                var process = Process.GetProcessById((int)processId);
                if (process == null)
                {
                    _logger.LogError("无法找到进程，ID: {ProcessId}", processId);
                    return false;
                }

                process.Kill();
                process.WaitForExit(5000); // 等待最多5秒
                
                _logger.LogInformation("已强制终止进程: {ProcessName} (PID: {ProcessId})", 
                    windowInfo.ProcessName, processId);
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "强制关闭窗口失败，句柄: {Handle}", windowHandle);
                return false;
            }
        }

        /// <summary>
        /// 获取窗口信息
        /// </summary>
        private WindowControlInfo GetWindowInfo(IntPtr windowHandle)
        {
            try
            {
                var detailedWindow = DesktopWindow.GetWindowByHWndDetailed((HWND)windowHandle);
                return new WindowControlInfo
                {
                    Title = detailedWindow.WindowText,
                    ProcessName = detailedWindow.OwnerProcess?.ProcessName ?? "未知",
                    ClassName = detailedWindow.ClassName
                };
            }
            catch
            {
                return new WindowControlInfo
                {
                    Title = "未知窗口",
                    ProcessName = "未知",
                    ClassName = "未知"
                };
            }
        }

        /// <summary>
        /// 检查是否为系统关键窗口
        /// </summary>
        private bool IsSystemCriticalWindow(WindowControlInfo windowInfo)
        {
            var criticalProcesses = new[]
            {
                "explorer", "dwm", "winlogon", "csrss", "lsass", "services", "smss",
                "wininit", "svchost", "lsm", "audiodg", "spoolsv", "SearchUI", "ShellExperienceHost"
            };

            var criticalClassNames = new[]
            {
                "Shell_TrayWnd", "Progman", "WorkerW", "DV2ControlHost", "Windows.UI.Core.CoreWindow"
            };

            return criticalProcesses.Any(p => 
                string.Equals(windowInfo.ProcessName, p, StringComparison.OrdinalIgnoreCase)) ||
                criticalClassNames.Any(c => 
                string.Equals(windowInfo.ClassName, c, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 检查是否为当前进程的窗口
        /// </summary>
        private bool IsCurrentProcess(IntPtr windowHandle)
        {
            try
            {
                uint processId = 0;
                unsafe
                {
                    PInvoke.GetWindowThreadProcessId((HWND)windowHandle, &processId);
                }
                return processId == Environment.ProcessId;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 窗口控制信息
    /// </summary>
    public class WindowControlInfo
    {
        public string Title { get; set; } = string.Empty;
        public string ProcessName { get; set; } = string.Empty;
        public string ClassName { get; set; } = string.Empty;
    }

    /// <summary>
    /// 可关闭窗口信息
    /// </summary>
    public class CloseableWindowInfo
    {
        public IntPtr Handle { get; set; }
        public string Title { get; set; } = string.Empty;
        public string ProcessName { get; set; } = string.Empty;
        public bool IsCloseable { get; set; }
        public bool IsCurrentProcess { get; set; }
        public string CloseableReason { get; set; } = string.Empty;
    }

    /// <summary>
    /// 窗口关闭结果
    /// </summary>
    public class WindowCloseResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public string WindowTitle { get; set; } = string.Empty;
        public string ProcessName { get; set; } = string.Empty;
        public string Method { get; set; } = string.Empty;
    }
} 