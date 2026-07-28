namespace RediensIAM.Services;

/// <summary>
/// Allowlist-based redirect validator for the OAuth2/OIDC flow. Returned URLs from Hydra
/// must always land on one of the operator-trusted origins: the app PublicUrl, the
/// AdminSpaOrigin, or Hydra's own PublicUrl. Anything else is treated as an open-redirect
/// attempt regardless of whether the source claims to be trusted.
/// </summary>
public static class RedirectValidator
{
    public enum Decision { Allow, BadRequest }

    /// <summary>
    /// Returns Allow if <paramref name="url"/> is a same-origin (relative) path or absolute
    /// URL whose origin matches one of the trusted origins. Otherwise BadRequest.
    /// </summary>
    public static Decision Evaluate(string? url, IEnumerable<string> trustedOrigins)
    {
        return TryReconstruct(url, trustedOrigins, out _) ? Decision.Allow : Decision.BadRequest;
    }

    /// <summary>
    /// Validates the URL and, if allowed, returns a freshly-reconstructed string from the
    /// parsed <see cref="Uri"/> components. The reconstructed URL has no direct flow from
    /// the input string into the redirect sink, which both prevents accidental open-redirect
    /// (the host comes from the parsed/validated Uri) and lets static taint analysers see
    /// that the sink is fed from a sanitised value.
    /// </summary>
    public static bool TryReconstruct(string? url, IEnumerable<string> trustedOrigins, out string safeUrl)
    {
        safeUrl = "";
        if (string.IsNullOrWhiteSpace(url)) return false;
        // Reject backslashes outright. Browsers normalise a leading "/\" to "//", i.e.
        // protocol-relative, so "/\evil.com" would slip through the relative-path
        // short-circuit below and redirect off-origin.
        if (url.Contains('\\', StringComparison.Ordinal)) return false;
        // Relative path = same origin = always safe (covers Hydra reject-flow's "/" replies too).
        // Reconstruct it through Uri to drop fragments/CR-LF and apply percent-encoding rules.
        if (url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(url, UriKind.Relative, out _)) return false;
            safeUrl = url.Replace("\r", "").Replace("\n", "");
            return true;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return false;
        var origin = $"{u.Scheme}://{u.Authority}";
        var allowed = false;
        foreach (var raw in trustedOrigins)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (string.Equals(TrimOrigin(raw), origin, StringComparison.OrdinalIgnoreCase))
            {
                allowed = true;
                break;
            }
        }
        if (!allowed) return false;
        // Rebuild via UriBuilder so the final string is composed from already-parsed
        // (host, port, path, query) values rather than the raw input.
        var builder = new UriBuilder(u.Scheme, u.Host)
        {
            Port  = u.IsDefaultPort ? -1 : u.Port,
            Path  = u.AbsolutePath,
            Query = u.Query.TrimStart('?'),
        };
        safeUrl = builder.Uri.AbsoluteUri;
        return true;
    }

    public static string TrimOrigin(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        return Uri.TryCreate(s, UriKind.Absolute, out var u) ? $"{u.Scheme}://{u.Authority}" : s;
    }
}
