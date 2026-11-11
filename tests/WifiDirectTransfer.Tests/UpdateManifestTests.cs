using WifiDirectTransfer.Core;
using Xunit;

namespace WifiDirectTransfer.Tests;

public sealed class UpdateManifestTests
{
    private const string Sha256 =
        "0123456789abcdef0123456789abcdef" +
        "0123456789abcdef0123456789abcdef";

    private static string CreateJson(
        string version = "1.6.0",
        string setup = "https://github.com/ChenzhongJi/WifiDirectTransfer/releases/download/v1.6.0/WifiDirectTransfer-1.6.0-setup.exe",
        string sha256 = Sha256,
        long size = 72_000_000,
        string? notes = "https://github.com/ChenzhongJi/WifiDirectTransfer/releases/tag/v1.6.0") =>
        $$"""
        {
          "version": "{{version}}",
          "setup": "{{setup}}",
          "sha256": "{{sha256}}",
          "size": {{size}},
          "notes": {{(notes is null ? "null" : $"\"{notes}\"")}}
        }
        """;

    [Fact]
    public void Parse_AcceptsValidManifest()
    {
        var manifest = UpdateManifest.Parse(CreateJson());

        Assert.NotNull(manifest);
        Assert.Equal("1.6.0", manifest!.Version.ToString());
        Assert.Equal(72_000_000, manifest.Size);
        Assert.Equal(Sha256, manifest.Sha256);
        Assert.Equal(Uri.UriSchemeHttps, manifest.SetupUri.Scheme);
        Assert.NotNull(manifest.NotesUri);
    }

    [Fact]
    public void Parse_AllowsMissingNotes()
    {
        var manifest = UpdateManifest.Parse(CreateJson(notes: null));

        Assert.NotNull(manifest);
        Assert.Null(manifest!.NotesUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void Parse_RejectsMalformedJson(string json)
    {
        Assert.Null(UpdateManifest.Parse(json));
    }

    [Fact]
    public void Parse_RejectsMissingVersion()
    {
        var json = CreateJson(version: "");

        Assert.Null(UpdateManifest.Parse(json));
    }

    [Fact]
    public void Parse_RejectsPlainHttpSetup()
    {
        var json = CreateJson(setup: "http://example.com/setup.exe");

        Assert.Null(UpdateManifest.Parse(json));
    }

    [Fact]
    public void Parse_RejectsRelativeSetupPath()
    {
        var json = CreateJson(setup: "setup.exe");

        Assert.Null(UpdateManifest.Parse(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg")]
    public void Parse_RejectsInvalidSha256(string sha256)
    {
        var json = CreateJson(sha256: sha256);

        Assert.Null(UpdateManifest.Parse(json));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(UpdateManifest.MaxSetupBytes + 1)]
    public void Parse_RejectsOutOfRangeSize(long size)
    {
        var json = CreateJson(size: size);

        Assert.Null(UpdateManifest.Parse(json));
    }

    [Fact]
    public void Parse_RejectsNonHttpNotes()
    {
        var json = CreateJson(notes: "file:///C:/notes.txt");

        Assert.Null(UpdateManifest.Parse(json));
    }

    [Fact]
    public void IsValidSha256_AcceptsUppercaseHex()
    {
        Assert.True(UpdateManifest.IsValidSha256(Sha256.ToUpperInvariant()));
    }
}
