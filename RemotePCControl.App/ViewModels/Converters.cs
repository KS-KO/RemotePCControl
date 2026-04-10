using System;
using System.Globalization;
using System.Windows.Data;

namespace RemotePCControl.App.ViewModels;

public class BooleanToIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isDirectory && isDirectory)
        {
            return "📁";
        }
        return "📄";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
