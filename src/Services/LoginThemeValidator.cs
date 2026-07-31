using System.Text.Json;

namespace RediensIAM.Services;

/// <summary>
/// Server-side validation of the tenant-supplied <c>login_theme</c>.
///
/// The theme is written by a tenant admin and rendered on every user's login page — the page
/// where passwords are typed. The client-side sanitiser
/// (<c>frontend/login/src/lib/sanitizeCss.ts</c>) says in its own header that it cannot be the
/// guard; this is the guard. Both the org route and the project route must go through it, which
/// is why it lives here rather than in either controller.
/// </summary>
public static class LoginThemeValidator
{
    private const string CustomCssKey = "custom_css";
    private const string LogoUrlKey   = "logo_url";
    private const int MaxCustomCssLength = 20_000;
    private const int MaxThemeValueLength = 120;

    /// <summary>
    /// Characters refused in every theme value other than <c>custom_css</c> and <c>logo_url</c>.
    /// <c>(</c> is the one that matters — it is what makes <c>url(https://attacker/?</c> a legal
    /// value — and the backslash is what would rebuild it from <c>\28</c>. The rest match the
    /// guard the preview page already applies.
    /// </summary>
    private static readonly System.Buffers.SearchValues<char> ForbiddenValueChars =
        System.Buffers.SearchValues.Create(";{}()<>\"'`\\");

    /// <summary>
    /// Constructs refused outright rather than stripped. Each is either an exfiltration
    /// primitive (<c>url()</c>, <c>@import</c>, <c>attr()</c>, <c>image-set()</c> — the CSS
    /// keylogger's only way off the page) or a way to hide one from inspection (comments, hex
    /// and backslash escapes). Theming a login page needs none of them, and refusing with a
    /// named reason is sound in a way a sanitiser that has to be right every time is not.
    /// </summary>
    private static readonly (string Token, string Error)[] ForbiddenCss =
    [
        ("/*",          "css_comments_not_allowed"),
        ("\\",          "css_escapes_not_allowed"),
        ("@",           "css_at_rules_not_allowed"),
        ("url(",        "css_url_not_allowed"),
        ("image-set(",  "css_url_not_allowed"),
        ("attr(",       "css_attr_not_allowed"),
        ("expression(", "css_expression_not_allowed"),
        ("<",           "css_markup_not_allowed"),
    ];

    /// <summary>Returns an error code, or null when the theme is acceptable.</summary>
    public static string? Validate(Dictionary<string, object>? theme)
    {
        if (theme == null) return null;

        if (theme.TryGetValue(LogoUrlKey, out var logoRaw) && AsString(logoRaw) is { Length: > 0 } logoUrl
            && (!Uri.TryCreate(logoUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
            return "logo_url_must_be_https";

        if (theme.TryGetValue(CustomCssKey, out var cssRaw) && AsString(cssRaw) is { Length: > 0 } css
            && ValidateCss(css) is { } cssErr)
            return cssErr;

        // The theme is a free-form dictionary and the login page pushes every string value it
        // recognises into a CSS custom property (Login.tsx `setProperty`); index.css then uses
        // several of them in `background: var(--surface)`, which accepts `url()`. So validating
        // custom_css alone left the exfiltration primitive in the colour keys, to be reassembled
        // by selectors that custom_css is perfectly entitled to contain. Checked key-agnostically
        // on purpose: a colour key added tomorrow is covered without editing a list here.
        foreach (var (key, raw) in theme)
        {
            if (key is CustomCssKey or LogoUrlKey) continue;
            if (AsString(raw) is { } value && IsUnsafeThemeValue(value))
                return "theme_value_invalid_character";
        }

        return null;
    }

    private static bool IsUnsafeThemeValue(string value) =>
        value.Length > MaxThemeValueLength
        || value.AsSpan().IndexOfAny(ForbiddenValueChars) >= 0;

    private static string? ValidateCss(string css)
    {
        if (css.Length > MaxCustomCssLength) return "custom_css_too_long";

        // Whitespace-insensitive and case-insensitive so `URL (` and `u r l(` cannot slip past a
        // literal match.
        var normalised = string.Concat(css.Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();
        foreach (var (token, error) in ForbiddenCss)
            if (normalised.Contains(token, StringComparison.Ordinal))
                return error;

        return null;
    }

    // Request bodies bind Dictionary<string, object> values as JsonElement; values read back
    // from an already-persisted theme are plain strings.
    private static string? AsString(object? value) => value switch
    {
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } el => el.GetString(),
        _ => null,
    };
}
