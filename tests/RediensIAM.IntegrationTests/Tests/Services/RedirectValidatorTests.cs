using FluentAssertions;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Services;

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
