using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace GearHub.Ui;

/// <summary>Half the real height as the corner radius: a true pill (WPF draws a too-big radius as stretched ellipses).</summary>
public sealed class PillRadius : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new CornerRadius(value is double h && h > 0 ? h / 2 : 0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
