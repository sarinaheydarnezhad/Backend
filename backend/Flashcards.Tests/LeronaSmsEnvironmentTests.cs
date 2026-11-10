using Flashcards.Infrastructure.Auth;
using Microsoft.Extensions.Configuration;

namespace Flashcards.Tests;

public sealed class LeronaSmsEnvironmentTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lerona-sms-config-" + Guid.NewGuid().ToString("N"));
    public LeronaSmsEnvironmentTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public void Runtime_file_loads_sms_settings_without_importing_cent_or_auth_settings()
    {
        File.WriteAllText(Path.Combine(root, ".env"), "Sms__Provider=Kavenegar\nKavenegar__ApiKey='test-key'\nKavenegar__OtpTemplate=test-template\nCent__Auth__SigningKey=must-not-load\nAuth__SigningKey=must-not-load\n");
        var config = new ConfigurationManager();
        config.AddLeronaSmsEnvironment(root);
        Assert.Equal("Kavenegar", config["Sms:Provider"]); Assert.Equal("test-key", config["Kavenegar:ApiKey"]);
        Assert.Null(config["Cent:Auth:SigningKey"]); Assert.Null(config["Auth:SigningKey"]);
        Assert.Null(Environment.GetEnvironmentVariable("Cent__Auth__SigningKey"));
    }

    [Fact]
    public void Host_configuration_overrides_runtime_file_without_mutating_process_environment()
    {
        File.WriteAllText(Path.Combine(root, ".env"), "Sms__Provider=Kavenegar\nKavenegar__ApiKey=file-key\n");
        var config = new ConfigurationManager();
        config.AddEnvironmentVariables();
        config.AddInMemoryCollection(new Dictionary<string, string?> { ["Kavenegar:ApiKey"] = "host-key" });
        config.AddLeronaSmsEnvironment(root);
        Assert.Equal("host-key", config["Kavenegar:ApiKey"]);
    }

    [Fact]
    public void Repository_root_file_loads_when_api_starts_from_project_directory()
    {
        Directory.CreateDirectory(Path.Combine(root, "backend", "Flashcards.Api"));
        File.WriteAllText(Path.Combine(root, "backend", "Flashcards.slnx"), "");
        File.WriteAllText(Path.Combine(root, ".env"), "Sms__Provider=Kavenegar\nKavenegar__ApiKey=test-key\n");
        var config = new ConfigurationManager();
        config.AddLeronaSmsEnvironment(Path.Combine(root, "backend", "Flashcards.Api"));
        Assert.Equal("test-key", config["Kavenegar:ApiKey"]);
    }

    [Fact]
    public void Missing_runtime_file_keeps_host_configuration_and_duplicate_settings_fail_safely()
    {
        var config = new ConfigurationManager(); config.AddLeronaSmsEnvironment(root);
        Assert.Null(config["Sms:Provider"]);
        File.WriteAllText(Path.Combine(root, ".env"), "Kavenegar__ApiKey=private-one\nKavenegar__ApiKey=private-two\n");
        var error = Assert.Throws<InvalidOperationException>(() => config.AddLeronaSmsEnvironment(root));
        Assert.DoesNotContain("private", error.Message);
    }
}
