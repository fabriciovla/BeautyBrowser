namespace BeautyKit;

public static class HostName
{
    /// <summary>
    /// The host without a leading "www." ("www.google.com" → "google.com"). Only the prefix goes: a host such as
    /// "paypal.cwww.om" must not read as "paypal.com".
    /// </summary>
    public static string WithoutWww(string host) =>
        host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
}
