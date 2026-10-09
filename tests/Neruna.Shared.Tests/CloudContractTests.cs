using System.Text.Json;
using Neruna.Contracts;
using Neruna.Contracts.Cloud;

namespace Neruna.Shared.Tests;

/// <summary>The server asserts it answers like contract/fixtures; here the desktop app reads those same documents.</summary>
public class CloudContractTests
{
    [Fact]
    public void Client_reads_the_enrollment_response()
    {
        var response = JsonSerializer.Deserialize<EnrollmentResponse>(ContractFixtures.Read("enrollment-response-example.json"), NerunaJson.Options)!;

        Assert.Equal(("Example AG", "Anna Muster"), (response.OrganizationName, response.MemberName));
        Assert.False(string.IsNullOrEmpty(response.DeviceId));
    }

    [Fact]
    public void Client_reads_the_profile()
    {
        var me = JsonSerializer.Deserialize<MeResponse>(ContractFixtures.Read("me-example.json"), NerunaJson.Options)!;

        Assert.Equal(("Anna", "Muster", "Projektleiterin", "+41 79 123 45 67"), (me.Member.FirstName, me.Member.LastName, me.Member.Position, me.Member.PhoneMobile));
        Assert.False(me.Member.HasPhoto);
        Assert.Equal(("Example AG", "Zürich", "https://example.com"), (me.Organization.Name, me.Organization.City, me.Organization.Website));
        Assert.Equal(["signatures"], me.Addons);
        Assert.Equal("Anna – Notebook", me.Device.Name);
    }

    [Fact]
    public void Enrollment_request_uses_the_server_field_names()
    {
        var json = JsonSerializer.Serialize(new EnrollmentRequest("CODE", "123456", "PEM", "Notebook", "0.1.2", "Windows", "11 (Build 26100)", @"AD\anna"), NerunaJson.Options);
        var names = JsonDocument.Parse(json).RootElement.EnumerateObject().Select(p => p.Name).ToList();

        Assert.Equal(["code", "pin", "publicKey", "deviceName", "appVersion", "osName", "osVersion", "osUser"], names);
    }

    [Fact]
    public void Client_reads_the_signatures()
    {
        var response = JsonSerializer.Deserialize<SignaturesResponse>(ContractFixtures.Read("signatures-example.json"), NerunaJson.Options)!;

        Assert.True(response.Available);
        var signature = Assert.Single(response.Signatures);
        Assert.Equal("Example AG", signature.Name);
        Assert.Contains("<strong>Anna Muster</strong>", signature.Html, StringComparison.Ordinal);
        Assert.StartsWith("Freundliche Grüsse", signature.Text, StringComparison.Ordinal);
        Assert.Equal(2026, signature.UpdatedAt.Year);
    }

    [Fact]
    public void Client_reads_the_text_templates()
    {
        var response = JsonSerializer.Deserialize<TextTemplatesResponse>(ContractFixtures.Read("text-templates-example.json"), NerunaJson.Options)!;

        var template = Assert.Single(response.Templates);
        Assert.Equal(("Anrufnotiz", "tel"), (template.Name, template.Shortcut));
        Assert.Contains("<table", template.Html, StringComparison.Ordinal);
    }
}
