using System;
using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SaveVault.Converters
{
    /// <summary>Collapses an element when the bound string is empty.</summary>
    public class StringToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var text = value as string;
            return string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>Inverts a boolean, for "nothing here yet" placeholders.</summary>
    public class InvertBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return !(value is bool && (bool)value);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return !(value is bool && (bool)value);
        }
    }

    /// <summary>Shows an element only when the bound boolean is false.</summary>
    public class InvertedBooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is bool && (bool)value ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    /// Collapses an element when the bound collection is empty. Binding to Count would work for a
    /// list but breaks the moment the property is null, which it is before the first load.
    ///
    /// Pass "invert" as the converter parameter for the mirrored case, which is how an "there is
    /// nothing here yet" placeholder shares one binding with the list it replaces.
    /// </summary>
    public class CountToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var invert = string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase);
            return Any(value) != invert ? Visibility.Visible : Visibility.Collapsed;
        }

        private static bool Any(object value)
        {
            if (value is int)
            {
                return (int)value > 0;
            }

            var list = value as ICollection;
            if (list != null)
            {
                return list.Count > 0;
            }

            var any = value as IEnumerable;
            if (any != null)
            {
                foreach (var item in any)
                {
                    return true;
                }
            }

            return false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
