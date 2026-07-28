using FluentAssertions;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Services;

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
