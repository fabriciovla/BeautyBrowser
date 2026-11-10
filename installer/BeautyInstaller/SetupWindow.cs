#nullable disable
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using Path = System.Windows.Shapes.Path;

namespace BeautyInstaller;

/// <summary>
/// The installer, short like the usual ones: a single screen over the liquid background where the logo floats, the
/// title rises letter by letter, Install turns into the progress bar and BeautyBrowser opens at the end. Setup choices
/// (default browser, favorites, theme) are not here: the browser's own onboarding handles them on first run.
/// </summary>
sealed class SetupWindow : Window
{
    const double LetterMs = 32;
    const double BarWidth = 280;

    /// <summary>Windows animations are on. Also off when rendering the preview, with everything in its final place.</summary>
    public static bool Motion = SystemParameters.ClientAreaAnimation;
    static readonly Brush White = Brushes.White;
    static readonly Brush Ink = Ui.Brush(0xFF141414);

    readonly Grid scene = new Grid();
    readonly Grid root = new Grid();
    readonly Liquid liquid;
    readonly Button close;
    readonly bool update = Installer.IsUpdate;
    Grid actionHost;
    Border note;
    CheckBox desktopShortcut;
    StackPanel shortcutRow;
    bool busy, installed, finishing;
    TimeSpan lastFrame;

    public SetupWindow()
    {
        Title = update ? L.T("Update Beauty Browser", "Actualizar Beauty Browser") : L.T("Install Beauty Browser", "Instalar Beauty Browser");
        Width = 880;
        Height = 600;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.Black;
        FontFamily = Ui.TextFont;
        UseLayoutRounding = true;
        Icon = Logo;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            GlassFrameThickness = new Thickness(0),
            ResizeBorderThickness = new Thickness(0),
            UseAeroCaptionButtons = false,
        });
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

        // Cool blues and lavender like the logo, muted in dark mode.
        Color c1 = Hsl(220, 0.5, 0.58), c2 = Hsl(260, 0.4, 0.75), c3 = Hsl(205, 0.5, 0.48);
        liquid = new Liquid(Width, Height, new[] { c1, c2, c3, Mix(c2, Colors.White, 0.45) }, AppsDark);

        var background = new Image { Source = liquid.Bitmap, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(background, BitmapScalingMode.HighQuality);
        root.Children.Add(background);
        root.Children.Add(new Rectangle { Fill = Liquid.Grain(), Opacity = 0.075, IsHitTestVisible = false });

        // Top left: the brand, small, as in Arc's and Zen's installers.
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        var brandLogo = new Image { Source = Logo, Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(brandLogo, BitmapScalingMode.HighQuality);
        brand.Children.Add(brandLogo);
        var brandName = Text("Beauty Browser", 12.5, 0.85, FontWeights.SemiBold);
        brandName.Margin = new Thickness(8, 0, 0, 0);
        brandName.VerticalAlignment = VerticalAlignment.Center;
        brand.Children.Add(brandName);
        root.Children.Add(brand);

        scene.Margin = new Thickness(40, 56, 40, 64);
        scene.ClipToBounds = false;
        root.Children.Add(scene);

        close = new Button
        {
            Style = (Style)FindResource("Close"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(14),
            ToolTip = update ? L.T("Cancel the update", "Cancelar la actualización") : L.T("Cancel the installation", "Cancelar la instalación"),
            IsCancel = true,
        };
        close.Click += (s, e) => OnClose();
        root.Children.Add(close);
        Content = root;

        MouseLeftButtonDown += (s, e) =>
        {
            try { DragMove(); } catch (InvalidOperationException) { }
        };
        SourceInitialized += (s, e) => RoundCorners();
        CompositionTarget.Rendering += OnFrame;
        Closed += (s, e) => CompositionTarget.Rendering -= OnFrame;
        Loaded += (s, e) => Cover();
    }


    void Cover()
    {
        var logo = new Image { Source = Logo, Width = 76, Height = 76 };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        logo.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 30, ShadowDepth = 6, Direction = 270, Opacity = 0.16 };
        Float(logo, 5, 3200);
        var logoHost = Host(logo);
        logoHost.Margin = new Thickness(0, 0, 0, 24);

        var title = Rising("Beauty Browser", 60, 120, out int letters);
        double after = 120 + letters * LetterMs + 400;

        var sub = Text(update ? L.T("A new version is here.", "Hay una versión nueva.") : L.T("Your browser, your way.", "Tu navegador, a tu manera."), 15.5, 0.92);
        sub.Margin = new Thickness(0, 14, 0, 0);

        actionHost = new Grid { Height = 46, Margin = new Thickness(0, 36, 0, 0) };
        var install = Primary(update ? L.T("Update", "Actualizar") : L.T("Install", "Instalar"), arrow: true);
        install.IsDefault = true;
        install.Click += async (s, e) => await Install();
        actionHost.Children.Add(Host(install));

        note = new Border { Margin = new Thickness(0, 18, 0, 0), MinHeight = 20, HorizontalAlignment = HorizontalAlignment.Center };
        SetNote(update ? L.T("Your data stays as it is", "Tus datos quedan como están") : Installer.InstalledSize + L.T("  ·  No administrator rights", "  ·  Sin permisos de administrador"), animate: false);

        // Desktop shortcut: one line at the bottom (an update keeps whatever was there).
        desktopShortcut = new CheckBox { Style = (Style)FindResource("Toggle"), IsChecked = update ? Installer.HasDesktopShortcut : true };
        var shortcutLabel = Text(L.T("Desktop shortcut", "Acceso directo en el escritorio"), 12.5, 0.85);
        shortcutLabel.VerticalAlignment = VerticalAlignment.Center;
        shortcutLabel.Margin = new Thickness(0, 0, 12, 0);
        shortcutRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 26, 0, 0) };
        shortcutRow.Children.Add(shortcutLabel);
        shortcutRow.Children.Add(desktopShortcut);
        shortcutRow.MouseLeftButtonDown += (s, e) => e.Handled = true;   // not a window drag
        if (update) shortcutRow.Visibility = Visibility.Collapsed;

        var cover = Column(logoHost, title, sub, actionHost, note, shortcutRow);
        Show(cover);
        Enter(logoHost, 0, Pop: true);
        Enter(sub, after);
        Enter(actionHost, after + 150);
        Enter(note, after + 300);
        Enter(shortcutRow, after + 450);
    }

    /// <summary>Install turns into the progress bar on the same line; the note tells each step.</summary>
    async Task Install()
    {
        if (busy) return;
        if (!Installer.HasPayload)
        {
            SetNote(L.T("This installer does not carry the app. Build it with installer\\build-installer.ps1.", "Este instalador no trae la app adentro. Armalo con installer\\build-installer.ps1."));
            return;
        }
        busy = true;
        close.Visibility = Visibility.Hidden;
        liquid.Flow(1);

        Leave(actionHost.Children[0]);
        shortcutRow.IsHitTestVisible = false;
        shortcutRow.BeginAnimation(OpacityProperty, Ui.To(0.45, Dur(200)));
        await Wait(150);
        var fill = new ScaleTransform(0, 1);
        var shine = new TranslateTransform(-80, 0);
        var bar = new Border
        {
            Width = BarWidth,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = Alpha(0.22),
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds = true,
            Child = new Grid
            {
                Children =
                {
                    new Border { Background = White, CornerRadius = new CornerRadius(4), RenderTransform = fill },
                    // A shine runs along the bar while it works.
                    new Border
                    {
                        Width = 70,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        RenderTransform = shine,
                        Background = new LinearGradientBrush(
                            new GradientStopCollection { new GradientStop(Color.FromArgb(0, 255, 255, 255), 0), new GradientStop(Color.FromArgb(0x70, 255, 255, 255), 0.5), new GradientStop(Color.FromArgb(0, 255, 255, 255), 1) },
                            new Point(0, 0.5), new Point(1, 0.5)),
                    },
                },
            },
        };
        if (Motion)
            shine.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-80, BarWidth + 10, new Duration(Ui.Ms(1400))) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Ui.EaseInOut });
        actionHost.Children.Clear();
        actionHost.Children.Add(bar);
        Enter(bar, 0);
        void FillTo(double value, double ms) => fill.BeginAnimation(ScaleTransform.ScaleXProperty, Ui.To(value, Dur(ms), 0, Ui.Glide));

        try
        {
            SetNote(update ? L.T("Closing Beauty Browser…", "Cerrando Beauty Browser…") : L.T("Preparing…", "Preparando…"));
            FillTo(0.04, 450);
            await Task.WhenAll(Installer.CloseRunningAsync(), Wait(450));

            // Copying is almost all the work, so the bar really follows it, from 4% to 90%.
            SetNote(L.T("Copying files…", "Copiando archivos…"));
            var copying = (TextBlock)((StackPanel)note.Child).Children[^1];
            var progress = new Progress<double>(p =>
            {
                fill.BeginAnimation(ScaleTransform.ScaleXProperty, Ui.To(0.04 + p * 0.86, Dur(160)));
                copying.Text = L.T($"Copying files…  {Math.Round(p * 100)}%", $"Copiando archivos…  {Math.Round(p * 100)} %");
            });
            await Task.Run(() => Installer.ExtractAsync(Installer.InstallDir, progress));

            SetNote(L.T("Creating the Start menu shortcut…", "Creando el acceso en Inicio…"));
            FillTo(0.94, 400);
            Installer.CreateStartMenuShortcut();
            await Wait(400);

            SetNote(L.T("Registering Beauty Browser with Windows…", "Registrando Beauty Browser en Windows…"));
            FillTo(1, 400);
            Installer.Register();
            Installer.RegisterBrowser();
            await Wait(450);
        }
        catch (Exception ex)
        {
            shine.BeginAnimation(TranslateTransform.XProperty, null);
            SetNote(L.T("Could not install: ", "No se pudo instalar: ") + ex.Message);
            busy = false;
            close.Visibility = Visibility.Visible;
            return;
        }

        installed = true;
        busy = false;
        shine.BeginAnimation(TranslateTransform.XProperty, null);
        shine.X = -80;
        SetNote(L.T("Installed. Opening Beauty Browser…", "Instalado. Abriendo Beauty Browser…"), check: true);
        await Wait(900);
        Finish(desktopShortcut.IsChecked == true);
    }


    void OnClose()
    {
        if (busy) return;
        if (installed) Finish(desktopShortcut?.IsChecked ?? true);
        else Close();
    }

    /// <summary>Creates the desktop shortcut (or not), opens BeautyBrowser, and the window shrinks and fades out.</summary>
    void Finish(bool desktop)
    {
        if (finishing) return;
        finishing = true;
        try
        {
            Installer.SetDesktopShortcut(desktop);
            Installer.Launch();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, L.T("Beauty Browser is installed, but it could not be opened: ", "Beauty Browser quedó instalado, pero no se pudo abrir: ") + ex.Message, "Beauty Browser");
        }
        var shrink = new ScaleTransform(1, 1);
        root.RenderTransformOrigin = new Point(0.5, 0.5);
        root.RenderTransform = shrink;
        var fade = Ui.To(0, Dur(260), 0, Ui.Launch);
        fade.Completed += (s, e) => Close();
        root.BeginAnimation(OpacityProperty, fade);
        if (Motion)
        {
            shrink.BeginAnimation(ScaleTransform.ScaleXProperty, Ui.To(0.96, 260, 0, Ui.Launch));
            shrink.BeginAnimation(ScaleTransform.ScaleYProperty, Ui.To(0.96, 260, 0, Ui.Launch));
        }
    }


    void Show(Panel panel)
    {
        scene.Children.Clear();
        scene.Children.Add(panel);
    }

    /// <summary>A plain container, so Enter and Leave move it rather than its content (buttons animate their own scale).</summary>
    static Grid Host(UIElement child)
    {
        var host = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
        host.Children.Add(child);
        return host;
    }

    static StackPanel Column(params UIElement[] children)
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    static TextBlock Text(string text, double size, double opacity, FontWeight? weight = null, bool shadow = false)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            Foreground = Alpha(opacity),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 480,
            HorizontalAlignment = HorizontalAlignment.Center,
            LineHeight = size * 1.45,
        };
        if (shadow) block.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 20, ShadowDepth = 2, Direction = 270, Opacity = 0.1 };
        return block;
    }

    /// <summary>A dark circle with a white check mark.</summary>
    static Border CheckBadge(double size) => new Border
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(size / 2),
        Background = Ink,
        Child = new Path
        {
            Data = Geometry.Parse("M 1,5 L 4,8 L 10,1.5"),
            Stroke = White,
            StrokeThickness = 1.7,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = size * 0.5,
            Height = size * 0.45,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    Button Primary(string label, bool arrow)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        if (arrow)
            content.Children.Add(new Path
            {
                Data = Geometry.Parse("M 2,8 L 14,8 M 9,3 L 14,8 L 9,13"),
                Stroke = Ink,
                StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Width = 16,
                Height = 16,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        return new Button { Style = (Style)FindResource("Primary"), Content = content };
    }

    /// <summary>The small line under the action: the size first, each step while installing, "Installed" at the end.</summary>
    void SetNote(string text, bool check = false, bool animate = true)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        if (check)
        {
            var badge = CheckBadge(16);
            badge.Background = Alpha(0.9);
            ((Path)badge.Child).Stroke = Ink;
            badge.Margin = new Thickness(0, 0, 8, 0);
            line.Children.Add(badge);
        }
        var label = Text(text, 12.5, 0.8);
        label.MaxWidth = 540;
        line.Children.Add(label);
        note.Child = line;
        if (animate) Enter(note, 0);
    }

    /// <summary>The title: each letter rises out of a blur, one after another (as in Arc and Beauty Shell).</summary>
    static FrameworkElement Rising(string text, double size, double startDelay, out int count)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        count = 0;
        foreach (char ch in text)
        {
            if (ch == ' ')
            {
                row.Children.Add(new Border { Width = size * 0.26 });
                continue;
            }
            var letter = new TextBlock
            {
                Text = ch.ToString(),
                FontSize = size,
                FontWeight = FontWeights.SemiBold,
                Foreground = White,
                Margin = new Thickness(0, 0, -size * 0.02, 0),
            };
            if (Motion)
            {
                var shift = new TranslateTransform(0, size * 0.45);
                var blur = new BlurEffect { Radius = 18 };
                letter.Opacity = 0;
                letter.RenderTransform = shift;
                letter.Effect = blur;
                double delay = startDelay + count * LetterMs;
                var fade = Ui.Tween(0, 1, 900, delay, Ui.Glide);
                fade.Completed += (s, e) => letter.Effect = null;
                letter.BeginAnimation(OpacityProperty, fade);
                shift.BeginAnimation(TranslateTransform.YProperty, Ui.To(0, 900, delay, Ui.Glide));
                blur.BeginAnimation(BlurEffect.RadiusProperty, Ui.To(0, 900, delay, Ui.Glide));
            }
            row.Children.Add(letter);
            count++;
        }
        return new Border
        {
            Child = row,
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 24, ShadowDepth = 2, Direction = 270, Opacity = 0.12 },
        };
    }

    /// <summary>Floats gently up and down, forever.</summary>
    static void Float(FrameworkElement element, double distance, double periodMs)
    {
        if (!Motion) return;
        var bob = new TranslateTransform();
        element.RenderTransform = bob;
        bob.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-distance / 2, distance / 2, new Duration(Ui.Ms(periodMs / 2)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = Ui.EaseInOut,
        });
    }

    /// <summary>
    /// Comes in out of a blur: from below, or from the right (<paramref name="sideways"/>, when changing steps).
    /// <paramref name="Pop"/> also grows it a little (the cover logo).
    /// </summary>
    static void Enter(UIElement element, double delay, bool sideways = false, bool Pop = false)
    {
        if (!Motion)
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = 1;
            return;
        }
        var shift = new TranslateTransform(sideways ? 40 : 0, sideways ? 0 : 12);
        var transforms = new TransformGroup();
        ScaleTransform scale = null;
        if (Pop)
        {
            scale = new ScaleTransform(0.8, 0.8);
            transforms.Children.Add(scale);
            element.RenderTransformOrigin = new Point(0.5, 0.5);
        }
        transforms.Children.Add(shift);
        element.RenderTransform = transforms;
        element.Opacity = 0;
        var duration = sideways ? 420 : 300;
        var fade = Ui.Tween(0, 1, duration, delay, Ui.Glide);
        if (!(element is Panel) && element.Effect == null)
        {
            // A blur over a whole step would also blur the effects inside, so only simple elements get one.
            var blur = new BlurEffect { Radius = 10 };
            element.Effect = blur;
            blur.BeginAnimation(BlurEffect.RadiusProperty, Ui.To(0, duration, delay, Ui.Glide));
            fade.Completed += (s, e) => { if (element.Effect == blur) element.Effect = null; };
        }
        element.BeginAnimation(OpacityProperty, fade);
        shift.BeginAnimation(sideways ? TranslateTransform.XProperty : TranslateTransform.YProperty, Ui.To(0, duration, delay, Ui.Glide));
        if (scale != null)
        {
            var grow = Ui.To(1, 700, delay, new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut });
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }
    }

    /// <summary>Leaves: rises (or, when changing steps, exits to the left) and fades out.</summary>
    static void Leave(UIElement element, Action then = null, bool sideways = false)
    {
        if (!Motion)
        {
            element.Opacity = 0;
            then?.Invoke();
            return;
        }
        var shift = new TranslateTransform();
        element.RenderTransform = shift;
        var duration = sideways ? 220 : 150;
        var fade = Ui.To(0, duration, 0, Ui.Launch);
        if (then != null) fade.Completed += (s, e) => then();
        element.BeginAnimation(OpacityProperty, fade);
        shift.BeginAnimation(sideways ? TranslateTransform.XProperty : TranslateTransform.YProperty, Ui.To(sideways ? -40 : -12, duration, 0, Ui.Launch));
    }

    static double Dur(double ms) => Motion ? ms : 1;
    static Task Wait(double ms) => Task.Delay(TimeSpan.FromMilliseconds(Motion ? ms : 0));

    static SolidColorBrush Alpha(double opacity)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)Math.Round(opacity * 255), 255, 255, 255));
        b.Freeze();
        return b;
    }

    void OnFrame(object sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == lastFrame) return;
        double dt = lastFrame == TimeSpan.Zero ? 0 : (now - lastFrame).TotalSeconds;
        lastFrame = now;
        if (Motion || dt == 0) liquid.Tick(Motion ? dt : 0);
    }

    static readonly BitmapImage Logo = LoadLogo();

    static BitmapImage LoadLogo()
    {
        var image = new BitmapImage(new Uri("pack://application:,,,/Assets/BeautyBrowser.png"));
        image.Freeze();
        return image;
    }

    static bool AppsDark
    {
        get
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
    }

    static Color Hsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Windows 11 only rounds the corners of a borderless window when asked.</summary>
    void RoundCorners()
    {
        int round = 2;   // DWMWCP_ROUND
        try { DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref round, sizeof(int)); }
        catch (DllNotFoundException) { }
    }


    /// <summary>
    /// Renders an installer screen to a PNG without opening a window, to review the design. Stages: "cover",
    /// "installing".
    /// </summary>
    public static void RenderPreview(string stage, string pngPath)
    {
        Motion = false;
        var window = new SetupWindow();
        var content = window.root;
        window.Content = null;
        content.Width = window.Width;
        content.Height = window.Height;

        window.Cover();
        switch (stage)
        {
            case "installing":
                window.actionHost.Children.Clear();
                window.actionHost.Children.Add(new Border
                {
                    Width = BarWidth, Height = 8, CornerRadius = new CornerRadius(4), Background = Alpha(0.22), VerticalAlignment = VerticalAlignment.Center,
                    Child = new Border { Background = White, CornerRadius = new CornerRadius(4), RenderTransform = new ScaleTransform(0.42, 1) },
                });
                window.close.Visibility = Visibility.Hidden;
                window.SetNote(L.T("Copying files…  42%", "Copiando archivos…  42 %"), animate: false);
                break;
        }

        content.Measure(new Size(window.Width, window.Height));
        content.Arrange(new Rect(0, 0, window.Width, window.Height));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(pngPath);
        encoder.Save(file);
        window.Close();
    }
}
