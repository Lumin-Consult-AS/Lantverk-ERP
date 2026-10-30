using System.IO.Compression;
using System.Text;
using Erp.Core.Domain.Apps;
using Erp.Core.Domain.Data;
using Erp.Core.Domain.Metadata;
using Erp.Core.Infrastructure.Apps;
using Erp.Core.Infrastructure.Metadata;
using Xunit;

namespace Erp.Core.Tests;

public sealed class AppManifestAndRuleUnitTests
{
    [Fact]
    public void SemverRange_EvaluatesVersionConstraintsCorrectly()
    {
        var range = SemverRange.Parse(">=1.0 <2.0");
        Assert.True(range.IsSatisfiedBy("1.0.0"));
        Assert.True(range.IsSatisfiedBy("1.5.2"));
        Assert.False(range.IsSatisfiedBy("0.9.9"));
        Assert.False(range.IsSatisfiedBy("2.0.0"));

        var caret = SemverRange.Parse("^1.2.0");
        Assert.True(caret.IsSatisfiedBy("1.2.0"));
        Assert.True(caret.IsSatisfiedBy("1.9.0"));
        Assert.False(caret.IsSatisfiedBy("1.1.9"));
        Assert.False(caret.IsSatisfiedBy("2.0.0"));
    }

    [Fact]
    public void WhenRuleEvaluator_EvaluatesYamlConditionalRules()
    {
        const string yaml = """
            id: core.parties
            version: 1.0.0
            objects:
              core.party:
                fields:
                  partyType:
                    type: string
                    required: true
                  organizationNumber:
                    type: string
                    requiredWhen:
                      field: partyType
                      eq: organization
                  creditLimit:
                    type: decimal
                    readOnlyWhen:
                      and:
                        - field: partyType
                          eq: person
                        - field: isLocked
                          eq: true
            """;

        var bundle = YamlMetadataParser.Parse(yaml);
        var partyObj = Assert.Single(bundle.Objects);
        var orgField = partyObj.Fields.Single(f => f.Name == "organizationNumber");
        var creditField = partyObj.Fields.Single(f => f.Name == "creditLimit");

        Assert.NotNull(orgField.RequiredWhen);
        Assert.True(WhenRuleEvaluator.Evaluate(
            orgField.RequiredWhen,
            new Dictionary<string, object?> { ["partyType"] = "organization" }));
        Assert.False(WhenRuleEvaluator.Evaluate(
            orgField.RequiredWhen,
            new Dictionary<string, object?> { ["partyType"] = "person" }));

        Assert.NotNull(creditField.ReadOnlyWhen);
        Assert.True(WhenRuleEvaluator.Evaluate(
            creditField.ReadOnlyWhen,
            new Dictionary<string, object?> { ["partyType"] = "person", ["isLocked"] = true }));
        Assert.False(WhenRuleEvaluator.Evaluate(
            creditField.ReadOnlyWhen,
            new Dictionary<string, object?> { ["partyType"] = "person", ["isLocked"] = false }));
    }

    [Fact]
    public void AppManifestValidator_EnforcesPrefixSealedDependenciesEventsAndEgressRules()
    {
        const string invalidManifestYaml = """
            id: demo.contacts
            version: 1.0.0
            name: Kontakter
            dependencies:
              core.parties: ">=2.0.0 <3.0.0"
            runtime:
              image: ghcr.io/example/demo-contacts:1.0.0
              egress:
                - "*"
                - "http://insecure.example.com"
            objects:
              sales.order:
                label: Ulovlig prefiks
                fields:
                  number: { type: string }
            extensions:
              core.sealedobject:
                fields:
                  customField: { type: string }
            events:
              subscribes:
                - unknown.app.happened@1: OnHappened
            """;

        var manifest = AppManifestParser.ParseManifestYaml(invalidManifestYaml);

        var installedApps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["core.parties"] = "1.0.0"
        };

        var existingObjects = new Dictionary<string, ObjectDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["core.sealedobject"] = new ObjectDefinition
            {
                Id = "core.sealedobject",
                OwnerApp = "core.parties",
                IsSealed = true
            }
        };

        var existingEvents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var ex = Assert.Throws<AppManifestValidationException>(() =>
            AppManifestValidator.ValidateForInstallation(
                manifest,
                installedApps,
                existingObjects,
                existingEvents));

        Assert.Contains(ex.Errors, e => e.Contains("does not satisfy required range", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(ex.Errors, e => e.Contains("sales.order", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(ex.Errors, e => e.Contains("sealed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(ex.Errors, e => e.Contains("unknown.app.happened@1", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(ex.Errors, e => e.Contains("egress", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AppPackageFormat_SignsVerifiesAndRejectsTamperedOrUnsignedZip()
    {
        var files = new Dictionary<string, string>
        {
            ["app.yaml"] = """
                id: core.parties
                version: 1.0.0
                name: Parter
                """,
            ["i18n/nb.yaml"] = """
                parties:
                  title: Parter
                """
        };

        var signedZip = AppPackageFormat.CreateSignedZipFromFiles(files, "unit-test-secret-key");
        var verified = AppPackageFormat.ReadAndVerifyPackage(signedZip, "unit-test-secret-key");
        Assert.Equal("core.parties", verified.Manifest.Id);
        Assert.Equal("1.0.0", verified.Manifest.Version);
        Assert.True(verified.Manifest.IsDeclarative);

        // Wrong signing key must fail signature verification
        Assert.Throws<AppPackageSignatureException>(() =>
            AppPackageFormat.ReadAndVerifyPackage(signedZip, "wrong-secret-key"));

        // Unsigned zip must fail verification
        using var unsignedMs = new MemoryStream();
        using (var archive = new ZipArchive(unsignedMs, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("app.yaml");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(files["app.yaml"]);
        }

        Assert.Throws<AppPackageSignatureException>(() =>
            AppPackageFormat.ReadAndVerifyPackage(unsignedMs.ToArray(), "unit-test-secret-key"));
    }
}

