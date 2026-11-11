using BeautyBrowser.Services;
using BeautyKit;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace BeautyBrowser;

// The password manager: Chrome's own does not exist in our Chromium mode, so the script in the page
// (PageScripts.Passwords) reports logins and login fields, and the app saves accounts (PasswordsService) and lists them
// under the field, filling the form only when the user picks one.
public sealed partial class MainWindow
{
    private SubmittedLogin? _loginToSave;
    private (BrowserTab Tab, LoginField Field)? _loginField;
    private int _loginHideVersion;
    private bool _pointerOnLoginList;
    private bool _hideLoginListOnLeave;

    private void SetupPasswords()
    {
        _shell.LoginFieldChanged += (_, e) =>
        {
            if (e.Field is { } field) ShowLoginList(e.Tab, field);
            else if (_loginField?.Tab == e.Tab) HideLoginListSoon();
        };
        _shell.LoginSubmitted += (_, e) => OfferToSave(e.Tab, e.Login);
        _shell.CurrentTabChanged += (_, _) =>
        {
            HideLoginList();
            HideSaveLogin();
        };
        LoginList.PointerEntered += (_, _) => _pointerOnLoginList = true;
        LoginList.PointerExited += (_, _) =>
        {
            _pointerOnLoginList = false;
            if (_hideLoginListOnLeave) HideLoginList();
        };
    }

    private object Resource(string key) =>
        LoginList.Resources.TryGetValue(key, out var value) ? value : Application.Current.Resources[key];

    private BrowserView? ViewOf(BrowserTab tab) =>
        tab == _shell.CurrentTab ? WebView : tab == _peekTab ? PeekView : null;

    private void ShowLoginList(BrowserTab tab, LoginField field)
    {
        _loginHideVersion++;
        var logins = _shell.Passwords.For(field.Origin);
        if (logins.Count == 0 || ViewOf(tab) is not { } view)
        {
            HideLoginList();
            return;
        }
        _loginField = (tab, field);
        _hideLoginListOnLeave = false;

        LoginRows.Children.Clear();
        foreach (var login in logins.Take(6)) LoginRows.Children.Add(LoginRow(login));

        // The script measures in physical pixels from the page's corner; the layer works in view pixels.
        var scale = view.XamlRoot?.RasterizationScale ?? 1;
        var at = view.TransformToVisual(LoginLayer).TransformPoint(new Point(field.X / scale, field.Bottom / scale + 4));
        Canvas.SetLeft(LoginList, at.X);
        Canvas.SetTop(LoginList, at.Y);
        LoginList.Width = Math.Max(240, field.Width / scale);
        LoginList.Visibility = Visibility.Visible;
    }

    private Button LoginRow(SavedLogin login)
    {
        var text = new StackPanel { Spacing = 1 };
        text.Children.Add(new TextBlock
        {
            FontSize = 13,
            Text = login.User.Length > 0 ? login.User : L.T("(no user name)", "(sin usuario)"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            FontSize = 11,
            Text = "••••••••",
            Foreground = (Brush)Resource("BeautyTextSecondaryBrush"),
        });
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(new FontIcon { FontSize = 14, Glyph = "" }); // contact
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var button = new Button
        {
            Content = row,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 6, 10, 6),
            Style = (Style)Resource("BeautyListRowButton"),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, login.User);
        button.Click += (_, _) =>
        {
            if (_loginField is not { } current) return;
            current.Tab.FillLogin(current.Field.Origin, login.User, PasswordsService.Reveal(login));
            HideLoginList();
            ViewOf(current.Tab)?.Focus(FocusState.Programmatic);
        };
        return button;
    }

    // The field loses focus also when the user clicks the list itself (the page loses focus): wait a moment and keep
    // the list while the pointer is on it.
    private void HideLoginListSoon()
    {
        var version = ++_loginHideVersion;
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(200);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            if (version != _loginHideVersion) return;
            if (_pointerOnLoginList) _hideLoginListOnLeave = true; // the user is picking an account
            else HideLoginList();
        };
        timer.Start();
    }

    private void HideLoginList()
    {
        _loginHideVersion++;
        _loginField = null;
        _pointerOnLoginList = false;
        LoginList.Visibility = Visibility.Collapsed;
    }

    private void OfferToSave(BrowserTab tab, SubmittedLogin login)
    {
        if (tab != _shell.CurrentTab && tab != _peekTab) return;
        if (_shell.Passwords.IsNever(login.Origin) || !_shell.Passwords.IsNew(login.Origin, login.User, login.Password)) return;
        _loginToSave = login;
        var site = Uri.TryCreate(login.Origin, UriKind.Absolute, out var uri) ? HostName.WithoutWww(uri.Host) : login.Origin;
        var updating = _shell.Passwords.For(login.Origin).Any(l => l.User == login.User);
        SaveLoginText.Text = updating
            ? L.T($"Update the saved password for {login.User} on {site}?", $"¿Actualizar la contraseña guardada de {login.User} en {site}?")
            : login.User.Length > 0
                ? L.T($"Save the password for {login.User} on {site}?", $"¿Guardar la contraseña de {login.User} en {site}?")
                : L.T($"Save the password for {site}?", $"¿Guardar la contraseña de {site}?");
        SaveLoginBar.Visibility = Visibility.Visible;
    }

    private void HideSaveLogin()
    {
        _loginToSave = null;
        SaveLoginBar.Visibility = Visibility.Collapsed;
    }

    private void SaveLogin_Click(object sender, RoutedEventArgs e)
    {
        if (_loginToSave is { } login) _shell.Passwords.Save(login.Origin, login.User, login.Password);
        HideSaveLogin();
    }

    private void SaveLoginNever_Click(object sender, RoutedEventArgs e)
    {
        if (_loginToSave is { } login) _shell.Passwords.NeverFor(login.Origin);
        HideSaveLogin();
    }

    private void SaveLoginDismiss_Click(object sender, RoutedEventArgs e) => HideSaveLogin();
}
