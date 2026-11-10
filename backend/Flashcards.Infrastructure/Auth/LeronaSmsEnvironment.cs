using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Memory;

namespace Flashcards.Infrastructure.Auth;

public static class LeronaSmsEnvironment
{
    // Only SMS/OTP keys are imported. Never mutate process environment or Cent configuration.
    public static void AddLeronaSmsEnvironment(this ConfigurationManager configuration, string contentRoot)
    {
        var directory = new DirectoryInfo(contentRoot);
        string? file = null;
        for (var depth = 0; directory is not null && depth < 4; depth++, directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "backend", "Flashcards.slnx")) || depth == 0)
            {
                var candidate = Path.Combine(directory.FullName, ".env");
                if (File.Exists(candidate)) { file = candidate; break; }
            }
        }
        if (file is null) return; // Production can inject environment variables without a file.
        var values = new Dictionary<string, string?>();
        foreach (var line in File.ReadLines(file))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var separator = trimmed.IndexOf('=');
            if (separator < 1) throw new InvalidOperationException("Invalid Lerona SMS environment file format.");
            var key = trimmed[..separator].Trim();
            if (key != "Sms__Provider" && !key.StartsWith("Kavenegar__", StringComparison.Ordinal) && !key.StartsWith("Otp__", StringComparison.Ordinal)) continue;
            var value = trimmed[(separator + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '\'' || value[0] == '"') && value[^1] == value[0]) value = value[1..^1];
            if (!values.TryAdd(key.Replace("__", ":"), value)) throw new InvalidOperationException("Duplicate Lerona SMS environment setting.");
        }
        // JSON < .env < real environment < command line, using the normal configuration pipeline.
        var index = configuration.Sources.ToList().FindLastIndex(source => source is EnvironmentVariablesConfigurationSource env && string.IsNullOrEmpty(env.Prefix));
        configuration.Sources.Insert(index < 0 ? configuration.Sources.Count : index, new MemoryConfigurationSource { InitialData = values });
    }
}
