using System.Collections.ObjectModel;
using BeautyKit;

namespace BeautyBrowser.Shell;

/// <summary>A group of tabs, like Arc's Spaces. Each space remembers its active tab.</summary>
public sealed class Space(string name, string? icon = null)
{
    public string Name { get; set; } = name;

    /// <summary>A Segoe Fluent Icons glyph, never an emoji.</summary>
    public string Icon { get; set; } = SpaceIcons.IsValid(icon) ? icon! : SpaceIcons.Default;

    public ObservableCollection<BrowserTab> Tabs { get; } = [];
    public BrowserTab? ActiveTab { get; set; }
}

public static class SpaceIcons
{
    public const string Default = ""; // house

    public static readonly (string Glyph, string Name)[] All =
    [
        ("", L.T("Home", "Inicio")),
        ("", L.T("Work", "Trabajo")),
        ("", L.T("Study", "Estudio")),
        ("", L.T("Code", "Código")),
        ("", L.T("Games", "Juegos")),
        ("", L.T("Music", "Música")),
        ("", L.T("Video", "Video")),
        ("", L.T("Photos", "Fotos")),
        ("", L.T("Shopping", "Compras")),
        ("", L.T("Finance", "Finanzas")),
        ("", L.T("Mail", "Correo")),
        ("", L.T("Calendar", "Calendario")),
        ("", L.T("People", "Personas")),
        ("", L.T("World", "Mundo")),
        ("", L.T("Travel", "Viajes")),
        ("", L.T("Places", "Lugares")),
        ("", L.T("Favorite", "Favorito")),
        ("", L.T("Star", "Estrella")),
        ("", L.T("Lightning", "Rayo")),
        ("", L.T("Sun", "Sol")),
        ("", L.T("Moon", "Luna")),
        ("", L.T("Cloud", "Nube")),
        ("", L.T("Design", "Diseño")),
        ("", L.T("Reading", "Lectura")),
    ];

    public static bool IsValid(string? glyph) => glyph is not null && All.Any(i => i.Glyph == glyph);

    /// <summary>For a new space: the first icon no other space uses.</summary>
    public static string FirstUnused(IEnumerable<Space> spaces)
    {
        var used = spaces.Select(s => s.Icon).ToHashSet();
        return All.Select(i => i.Glyph).FirstOrDefault(g => !used.Contains(g)) ?? Default;
    }
}
