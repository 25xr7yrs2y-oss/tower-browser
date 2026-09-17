namespace PrivacyBrowser.App;

public static class PaymentUriValidator
{
    public static Uri ParseAbsoluteHttps(string value)
    {
        return Parse(value, exactHost: null, exactPath: null);
    }

    public static Uri ParseHostedCheckout(string value, string exactHost, string? exactPath = null)
    {
        return Parse(value, exactHost, exactPath);
    }

    private static Uri Parse(string value, string? exactHost, string? exactPath)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException("The payment response contained an empty payment URL.");
        }
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                throw new InvalidOperationException("The payment URL contained whitespace or control characters.");
            }
        }
        if (value.Contains('#') || value.Contains('\\'))
        {
            throw new InvalidOperationException("The payment URL contained a forbidden fragment or path separator.");
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(uri.Host))
        {
            throw new InvalidOperationException("The payment URL must be an absolute HTTPS URL.");
        }
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException("The payment URL must not contain user information.");
        }
        if (HasExplicitEmptyPort(value) || !uri.IsDefaultPort)
        {
            throw new InvalidOperationException("The payment URL must use the default HTTPS port.");
        }
        if (exactHost is not null &&
            !uri.DnsSafeHost.Equals(exactHost, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The payment URL host is not approved for this gateway.");
        }
        if (exactPath is not null &&
            !uri.AbsolutePath.Equals(exactPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The payment URL path is not approved for this gateway.");
        }

        return uri;
    }

    // System.Uri normalizes an explicit empty port ("https://host:") to the default port,
    // so inspect the raw authority before relying on IsDefaultPort.
    private static bool HasExplicitEmptyPort(string value)
    {
        var authorityStart = value.IndexOf("://", StringComparison.Ordinal);
        if (authorityStart < 0)
        {
            return true;
        }

        authorityStart += 3;
        var authorityEnd = value.Length;
        foreach (var separator in new[] { '/', '?', '#' })
        {
            var index = value.IndexOf(separator, authorityStart);
            if (index >= 0 && index < authorityEnd)
            {
                authorityEnd = index;
            }
        }

        return authorityEnd > authorityStart && value[authorityEnd - 1] == ':';
    }
}
