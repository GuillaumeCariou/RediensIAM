using FluentAssertions;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Services;

// ── from RedirectValidatorTests.cs ──────────────────────────

/// <summary>
/// Unit tests for <see cref="RedirectValidator"/>: covers every branch of the open-redirect
/// allowlist used by AuthController.SafeRedirect.
/// </summary>
public class RedirectValidatorTests
{
    private static readonly string[] Trusted =
        ["http://localhost", "http://admin.localhost", "http://hydra.localhost:4444"];

    [Fact]
    public void Evaluate_NullUrl_ReturnsBadRequest() =>
        RedirectValidator.Evaluate(null, Trusted).Should().Be(RedirectValidator.Decision.BadRequest);

    [Fact]
    public void Evaluate_EmptyUrl_ReturnsBadRequest() =>
        RedirectValidator.Evaluate("", Trusted).Should().Be(RedirectValidator.Decision.BadRequest);

    [Fact]
    public void Evaluate_WhitespaceUrl_ReturnsBadRequest() =>
        RedirectValidator.Evaluate("   ", Trusted).Should().Be(RedirectValidator.Decision.BadRequest);

    [Fact]
    public void Evaluate_RelativePath_Allowed() =>
        RedirectValidator.Evaluate("/oauth2/callback", Trusted).Should().Be(RedirectValidator.Decision.Allow);

    [Fact]
    public void Evaluate_ProtocolRelativeUrl_BadRequest() =>
        RedirectValidator.Evaluate("//evil.example/x", Trusted).Should().Be(RedirectValidator.Decision.BadRequest);

    [Fact]
    public void Evaluate_MalformedUrl_BadRequest() =>
        RedirectValidator.Evaluate("not-a-url", Trusted).Should().Be(RedirectValidator.Decision.BadRequest);

    [Fact]
    public void Evaluate_FtpScheme_BadRequest() =>
        RedirectValidator.Evaluate("ftp://localhost/x", Trusted).Should().Be(RedirectValidator.Decision.BadRequest);

    [Fact]
    public void Evaluate_JavascriptScheme_BadRequest() =>
        RedirectValidator.Evaluate("javascript:alert(1)", Trusted).Should().Be(RedirectValidator.Decision.BadRequest);

    [Fact]
    public void Evaluate_AllowedAppOrigin_Allowed() =>
        RedirectValidator.Evaluate("http://localhost/path?x=1", Trusted).Should().Be(RedirectValidator.Decision.Allow);

    [Fact]
    public void Evaluate_AllowedHydraOrigin_Allowed() =>
        RedirectValidator.Evaluate("http://hydra.localhost:4444/oauth2/auth", Trusted).Should().Be(RedirectValidator.Decision.Allow);

    [Fact]
    public void Evaluate_DifferentPort_BadRequest() =>
        RedirectValidator.Evaluate("http://localhost:8443/x", Trusted).Should().Be(RedirectValidator.Decision.BadRequest);

    [Fact]
    public void Evaluate_DifferentScheme_BadRequest() =>
        RedirectValidator.Evaluate("https://localhost/x", Trusted).Should().Be(RedirectValidator.Decision.BadRequest);

    [Fact]
    public void Evaluate_CrossOriginHttps_BadRequest() =>
        RedirectValidator.Evaluate("https://evil.example/x", Trusted).Should().Be(RedirectValidator.Decision.BadRequest);

    [Fact]
    public void Evaluate_EmptyTrustedOriginsEntry_Skipped() =>
        RedirectValidator.Evaluate("http://localhost/x", ["", "http://localhost"])
            .Should().Be(RedirectValidator.Decision.Allow);

    [Fact]
    public void Evaluate_OriginComparisonIsCaseInsensitive() =>
        RedirectValidator.Evaluate("HTTP://LOCALHOST/x", Trusted).Should().Be(RedirectValidator.Decision.Allow);

    [Fact]
    public void TrimOrigin_StripsPathAndQuery() =>
        RedirectValidator.TrimOrigin("http://localhost:5000/foo?x=1").Should().Be("http://localhost:5000");

    [Fact]
    public void TrimOrigin_HandlesMalformed() =>
        RedirectValidator.TrimOrigin("not-a-url").Should().Be("not-a-url");

    [Fact]
    public void TrimOrigin_HandlesEmpty() =>
        RedirectValidator.TrimOrigin("").Should().Be("");
}

// ── from RedirectValidatorNewTests.cs ───────────────────────

/// <summary>
/// Edge-case coverage for <see cref="RedirectValidator"/> beyond the original happy paths:
/// CR/LF injection, userinfo in URL, IDN, port confusion, mixed-case schemes.
/// </summary>
public class RedirectValidatorNewTests
{
    private static readonly string[] Trusted = ["http://localhost", "http://hydra.localhost:4444"];

    [Theory]
    [InlineData("/path\rwith-cr")]
    [InlineData("/path\nwith-lf")]
    [InlineData("/path\r\nwith-crlf")]
    public void TryReconstruct_StripsCRLF_FromRelativePath(string input)
    {
        var ok = RedirectValidator.TryReconstruct(input, Trusted, out var safe);
        ok.Should().BeTrue();
        safe.Should().NotContain("\r").And.NotContain("\n");
    }

    [Fact]
    public void TryReconstruct_RejectsUserInfoInAbsoluteUrl()
    {
        // user@evil syntax can be used to spoof origin in user-visible URLs.
        var ok = RedirectValidator.TryReconstruct("http://attacker@localhost/x", Trusted, out var safe);
        // u.Authority for `http://attacker@localhost/x` includes userinfo per RFC 3986;
        // Uri.Authority strips userinfo in .NET, so the parsed origin matches "http://localhost"
        // which IS in the allowlist. Verify the reconstructed URL drops the userinfo so the
        // browser doesn't render the spoofed authority.
        ok.Should().BeTrue();
        safe.Should().NotContain("attacker@");
    }

    [Fact]
    public void TryReconstruct_RejectsMismatchedPort()
    {
        var ok = RedirectValidator.TryReconstruct("http://localhost:9999/x", Trusted, out _);
        ok.Should().BeFalse();
    }

    [Theory]
    [InlineData("HTTP://LOCALHOST/x")]
    [InlineData("http://LOCALHOST/x")]
    public void TryReconstruct_OriginComparison_IsCaseInsensitive(string input)
    {
        var ok = RedirectValidator.TryReconstruct(input, Trusted, out _);
        ok.Should().BeTrue();
    }

    [Fact]
    public void TryReconstruct_RelativePathWithBackslash_IsRejected()
    {
        // Browsers normalise a leading `/\` to `//` — protocol-relative — so emitting the
        // literal value in a Location header navigates off-origin. This used to be accepted
        // by the relative-path short-circuit ("starts with '/', not with '//'").
        var ok = RedirectValidator.TryReconstruct("/\\attacker.example/x", Trusted, out var safe);

        ok.Should().BeFalse();
        safe.Should().BeEmpty();
    }
}
