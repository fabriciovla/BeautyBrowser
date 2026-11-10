using Microsoft.UI.Xaml;

namespace BeautyBrowser;

/// <summary>Helpers for {x:Bind}.</summary>
public static class BindHelpers
{
    public static Visibility VisibleIfNull(object? value) => value is null ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility VisibleIfNotNull(object? value) => value is not null ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility VisibleIf(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
}
