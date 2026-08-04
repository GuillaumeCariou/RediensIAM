using System.Text.RegularExpressions;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Regression;

/// <summary>
/// The audit log has to name a thing the same way whichever route touched it.
///
/// <para>
/// Deleting a SAML provider from the organisation scope recorded <c>target_type =
/// "saml_provider"</c>; deleting the same provider from the system scope recorded
/// <c>"saml_idp_config"</c>. Same event, same action string on both sides
/// (<c>saml_provider.deleted</c>), two different resource types — so an audit query filtered by
/// type saw half the deletions and reported the other half as never having happened. Nothing
/// failed, nothing was logged as wrong; the record was simply incomplete, which is the one thing
/// an audit log may not be.
/// </para>
///
/// <para>
/// <c>saml_provider</c> is the correct value: it is what the action string already says, and what
/// the API calls the resource. <c>saml_idp_config</c> was the database table's name leaking into a
/// record that is read by people.
/// </para>
///
/// <para>
/// This is the shape of defect that duplicated handlers produce — see the note on ProjectUpdate.
/// The test is per-scope on purpose: a single assertion on one route is exactly what let the two
/// drift apart.
/// </para>
/// </summary>
[Collection("RediensIAM")]
public class AuditTargetTypeTests(TestFixture fixture)
{
    /// <summary>Every audit entry this deployment writes for a SAML provider, whatever wrote it.</summary>
    private const string SamlTargetType = "saml_provider";

    [Theory]
    [InlineData("saml_provider.created")]
    [InlineData("saml_provider.updated")]
    [InlineData("saml_provider.deleted")]
    public void EveryScopeNamesTheSamlProviderTheSameWay(string action)
    {
        // Read from the source rather than driving both routes: the divergence is a literal, the
        // two call sites are three lines apart in different files, and a test that has to stand up
        // an IdP on each scope to compare two strings is a test nobody will keep.
        var sources = new[]
        {
            File.ReadAllText(SourcePath("OrgController.cs")),
            File.ReadAllText(SourcePath("SystemAdminController.cs")),
        };

        var targetTypes = sources
            .SelectMany(src => Regex.Matches(
                src, $@"RecordAsync\([^;]*?""{Regex.Escape(action)}"",\s*""(?<type>[a-z_]+)"""))
            .Select(m => m.Groups["type"].Value)
            .ToList();

        targetTypes.Should().NotBeEmpty($"{action} is recorded somewhere");
        targetTypes.Should().AllBe(SamlTargetType,
            "the same event recorded under two resource types makes an audit query miss half of them");
    }

    private static string SourcePath(string file)
    {
        // The test binary runs from bin/Debug/…; walk up to the repository root.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Controllers")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the repository root has to be reachable from the test binary");
        return Path.Combine(dir!.FullName, "src", "Controllers", file);
    }
}
