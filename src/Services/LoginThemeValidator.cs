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
    public const int MaxThemeValueLength = 120;   // mirrored by THEME_VALUE_MAX_LENGTH in sanitizeCss.ts

    /// <summary>
    /// Characters refused in every theme value other than <c>custom_css</c> and <c>logo_url</c>.
    /// <c>(</c> is the one that matters — it is what makes <c>url(https://attacker/?</c> a legal
    /// value — and the backslash is what would rebuild it from <c>\28</c>. The rest match the
    /// guard the preview page already applies.
    /// </summary>
    /// <remarks>
    /// Mirrored by <c>THEME_VALUE_FORBIDDEN</c> in <c>frontend/login/src/lib/sanitizeCss.ts</c>.
    /// Two languages cannot share one literal, so both sides pin the exact string in a test:
    /// widening either guard alone fails the paired test naming the other file.
    /// </remarks>
    public const string ForbiddenValueCharacters = ";{}()<>\"'`\\";

    private static readonly System.Buffers.SearchValues<char> ForbiddenValueChars =
        System.Buffers.SearchValues.Create(ForbiddenValueCharacters);

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
            if (ValidateNested(key, raw) is { } nestedErr) return nestedErr;
        }

        return null;
    }

    /// <summary>
    /// Walks a theme value of any shape.
    ///
    /// <para>
    /// The loop above used to call <c>AsString</c> and move on, and <c>AsString</c> answers null
    /// for an array — so <c>providers</c>, the one part of the theme that is a list, was never
    /// examined. Each entry carries a <c>logo_url</c> that the login page renders as an
    /// <c>&lt;img src&gt;</c> on an unauthenticated page, and it got none of the checks the
    /// top-level <c>logo_url</c> gets: a tenant admin could point it anywhere and be told who
    /// signed in, when, and from what address.
    /// </para>
    /// </summary>
    private static string? ValidateNested(string key, object? raw)
    {
        if (raw is JsonElement { ValueKind: JsonValueKind.Array } array)
        {
            foreach (var item in array.EnumerateArray())
                if (ValidateNested(key, item) is { } err) return err;
            return null;
        }

        if (raw is JsonElement { ValueKind: JsonValueKind.Object } obj)
        {
            foreach (var property in obj.EnumerateObject())
            {
                // A nested logo_url ends up in the same <img src> as the top-level one, so it is
                // held to the same rule rather than to the generic character check.
                if (property.NameEquals(LogoUrlKey))
                {
                    if (AsString(property.Value) is { Length: > 0 } url
                        && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
                        return "logo_url_must_be_https";
                    continue;
                }
                if (property.NameEquals(CustomCssKey))
                {
                    if (AsString(property.Value) is { Length: > 0 } css && ValidateCss(css) is { } cssErr)
                        return cssErr;
                    continue;
                }
                if (ValidateNested(property.Name, property.Value) is { } err) return err;
            }
            return null;
        }

        return AsString(raw) is { } value && IsUnsafeThemeValue(value)
            ? "theme_value_invalid_character"
            : null;
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
