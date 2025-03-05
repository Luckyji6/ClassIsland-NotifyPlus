using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ClassIsland.Converters
{
    /// <summary>
    /// 检查字符串是否包含指定子字符串的转换器
    /// </summary>
    public class StringContainsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null || parameter == null)
                return Visibility.Collapsed;
            
            string str = value?.ToString() ?? string.Empty;
            string searchText = parameter?.ToString() ?? string.Empty;
            
            if (string.IsNullOrEmpty(searchText))
                return Visibility.Collapsed;
                
            return str.Contains(searchText) ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
} 