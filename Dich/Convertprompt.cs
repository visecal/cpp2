using System;
using System.Globalization;
using System.Windows.Data;

namespace subphimv1.Converters
{
    public class BoolToPromptBehaviorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool useDefault)
            {
                // Nếu checkbox "Dùng prompt hệ thống" được chọn, prompt của người dùng sẽ "thêm vào sau".
                // Nếu không, prompt của người dùng sẽ "sử dụng làm" prompt chính.
                return useDefault ? "sẽ được thêm vào sau prompt hệ thống:" : "sẽ được sử dụng làm prompt chính:";
            }
            return "sẽ được thêm vào sau prompt hệ thống:";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}