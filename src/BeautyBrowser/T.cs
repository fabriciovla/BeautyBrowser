using BeautyKit;
using Microsoft.UI.Xaml.Markup;

namespace BeautyBrowser;

/// <summary>Localized text in XAML: <c>Text="{local:T En='New tab', Es='Nueva pestaña'}"</c>.</summary>
[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed partial class T : MarkupExtension
{
    public string En { get; set; } = "";
    public string Es { get; set; } = "";

    protected override object ProvideValue() => L.T(En, Es);
}
