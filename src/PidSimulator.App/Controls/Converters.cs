using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PidSimulator.App.Controls;

/// <summary>ラジオボタンの選択時だけ値を反映し、グループ内の選択解除では設定を変更しない。</summary>
public sealed class BoolEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool current && current == bool.Parse(parameter.ToString()!);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? bool.Parse(parameter.ToString()!) : Binding.DoNothing;
}

/// <summary>RadioButton.IsChecked と enum の双方向バインド用。ConverterParameter に enum 名を渡す。</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Enum.Parse(targetType, parameter.ToString()!) : Binding.DoNothing;
}

/// <summary>int が ConverterParameter と等しければ Visible（ウィザードのステップ切替用）</summary>
public sealed class IndexToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int i && int.TryParse(parameter?.ToString(), out int p) && i == p ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
