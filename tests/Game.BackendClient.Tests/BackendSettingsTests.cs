
namespace RaidZero.BackendApi.Tests;

public class BackendSettingsTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void TheCommandLineWinsOverTheEnvironment()
    {
        var settings = BackendSettings.Resolve(
            ["-batchmode", "--backend-url", "https://args.example/", "--server-token=from-args", "-nographics"],
            Env(("RAIDZERO_BACKEND_URL", "https://env.example"), ("RAIDZERO_SERVER_TOKEN", "from-env")),
            "default-outbox");

        Assert.Equal("https://args.example", settings.BackendUrl);
        Assert.Equal("from-args", settings.ServerToken);
        Assert.Equal("default-outbox", settings.OutboxDirectory);
    }

    [Fact]
    public void TheEnvironmentFillsInWhatTheCommandLineLeavesOut()
    {
        var settings = BackendSettings.Resolve(
            ["--backend-url", "http://localhost:5080"],
            Env(("RAIDZERO_SERVER_TOKEN", "from-env"), ("RAIDZERO_OUTBOX_DIR", "/srv/outbox")),
            "default-outbox");

        Assert.Equal("http://localhost:5080", settings.BackendUrl);
        Assert.Equal("from-env", settings.ServerToken);
        Assert.Equal("/srv/outbox", settings.OutboxDirectory);
    }

    [Fact]
    public void MissingSettings_AreAllNamedAtOnce()
    {
        var error = Assert.Throws<BackendSettingsException>(() => BackendSettings.Resolve([], Env(), "default-outbox"));

        Assert.Contains("--backend-url", error.Message);
        Assert.Contains("RAIDZERO_BACKEND_URL", error.Message);
        Assert.Contains("--server-token", error.Message);
        Assert.Contains("RAIDZERO_SERVER_TOKEN", error.Message);
    }

    [Fact]
    public void EmptyValues_CountAsMissing()
    {
        var error = Assert.Throws<BackendSettingsException>(() =>
            BackendSettings.Resolve(["--server-token", "  "], Env(("RAIDZERO_BACKEND_URL", "http://localhost:5080"), ("RAIDZERO_SERVER_TOKEN", "")), "outbox"));

        Assert.Contains("server token", error.Message);
        Assert.DoesNotContain("backend URL", error.Message);
    }

    [Theory]
    [InlineData("api.example.com")]
    [InlineData("ftp://api.example.com")]
    [InlineData("/v1")]
    public void TheUrlMustBeAbsoluteHttp(string url)
    {
        var error = Assert.Throws<BackendSettingsException>(() =>
            BackendSettings.Resolve(["--backend-url", url, "--server-token", "t"], Env(), "outbox"));

        Assert.Contains(url, error.Message);
    }

    [Fact]
    public void ToString_NeverShowsTheToken()
    {
        var settings = new BackendSettings("https://api.example.com", "super-secret-token", "outbox");

        Assert.DoesNotContain("super-secret", settings.ToString());
    }
}
