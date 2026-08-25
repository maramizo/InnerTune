using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace InnerTune;

public sealed class ArtworkImageConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string url || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var width = int.TryParse(parameter?.ToString(), out var requested) ? Math.Clamp(requested, 32, 320) : 128;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = uri;
            image.DecodePixelWidth = width;
            image.CacheOption = BitmapCacheOption.OnDemand;
            image.CreateOptions = BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile;
            image.EndInit();
            return image;
        }
        catch { return null; }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}
