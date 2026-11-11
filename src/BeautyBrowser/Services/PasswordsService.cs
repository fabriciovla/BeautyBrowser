using System.Runtime.InteropServices;
using System.Text;

namespace BeautyBrowser.Services;

/// <summary>A saved account. The password stays encrypted until it is needed.</summary>
public sealed class SavedLogin
{
    public string Origin { get; set; } = "";
    public string User { get; set; } = "";

    /// <summary>The password encrypted with Windows (DPAPI, tied to this Windows user), in base64.</summary>
    public string Secret { get; set; } = "";

    public DateTime Updated { get; set; }

    public string Site => Uri.TryCreate(Origin, UriKind.Absolute, out var uri) ? uri.Host : Origin;
}

/// <summary>
/// Saved passwords, in passwords.json. Each password is encrypted with Windows' DPAPI, so the file is useless on
/// another PC or to another Windows user, as with Chrome on Windows.
/// </summary>
public sealed class PasswordsService
{
    private const string FileName = "passwords.json";

    public sealed class Data
    {
        public List<SavedLogin> Logins { get; set; } = [];

        /// <summary>Sites where the user chose "Never" for saving.</summary>
        public List<string> Never { get; set; } = [];
    }

    private readonly Data _data = JsonStore.Load<Data>(FileName);

    public event EventHandler? Changed;

    public IReadOnlyList<SavedLogin> All => _data.Logins.OrderBy(l => l.Site).ThenBy(l => l.User).ToList();

    public IReadOnlyList<SavedLogin> For(string origin) =>
        _data.Logins.Where(l => l.Origin == origin).OrderByDescending(l => l.Updated).ToList();

    public bool IsNever(string origin) => _data.Never.Contains(origin);

    /// <summary>Whether saving this login would add or change something (a new account or another password).</summary>
    public bool IsNew(string origin, string user, string password) =>
        !For(origin).Any(l => l.User == user && Reveal(l) == password);

    public void Save(string origin, string user, string password)
    {
        var login = _data.Logins.FirstOrDefault(l => l.Origin == origin && l.User == user);
        if (login is null) _data.Logins.Add(login = new SavedLogin { Origin = origin, User = user });
        login.Secret = Protect(password);
        login.Updated = DateTime.UtcNow;
        Store();
    }

    public void NeverFor(string origin)
    {
        if (!_data.Never.Contains(origin)) _data.Never.Add(origin);
        Store();
    }

    public void Remove(SavedLogin login)
    {
        _data.Logins.RemoveAll(l => l.Origin == login.Origin && l.User == login.User);
        Store();
    }

    /// <summary>The password in clear, or "" if Windows cannot decrypt it (another user or PC).</summary>
    public static string Reveal(SavedLogin login)
    {
        try { return Unprotect(login.Secret); }
        catch { return ""; }
    }

    private void Store()
    {
        JsonStore.Save(FileName, _data);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // DPAPI through crypt32, without an extra package.
    private static string Protect(string text) => Convert.ToBase64String(Crypt(Encoding.UTF8.GetBytes(text), protect: true));

    private static string Unprotect(string secret) => Encoding.UTF8.GetString(Crypt(Convert.FromBase64String(secret), protect: false));

    private static byte[] Crypt(byte[] data, bool protect)
    {
        var input = new DataBlob { Size = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            var ok = protect
                ? CryptProtectData(ref input, null, 0, 0, 0, UiForbidden, out var output)
                : CryptUnprotectData(ref input, 0, 0, 0, 0, UiForbidden, out output);
            if (!ok) throw new InvalidOperationException("DPAPI " + Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(input.Data); }
    }

    private const int UiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public nint Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, nint entropy, nint reserved,
        nint prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob input, nint description, nint entropy, nint reserved,
        nint prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
