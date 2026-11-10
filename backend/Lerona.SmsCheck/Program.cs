using System.Text.Json;
using Flashcards.Infrastructure.Auth;
using Microsoft.Extensions.Configuration;

// Read the same process environment provider used by WebApplication.CreateBuilder.
// Output booleans only. Never serialize configuration values or raw exceptions.
var configuration = new ConfigurationManager();
configuration.AddEnvironmentVariables();
configuration.AddLeronaSmsEnvironment(Directory.GetCurrentDirectory());
using var handler = new HttpClientHandler { AllowAutoRedirect = false };
using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
var sender = new KavenegarOtpSender(client, configuration);
var selected = configuration["Sms:Provider"] == "Kavenegar";
var valid = selected && sender.IsConfigured;
var providerChecked = false;
var providerLive = false;
if (args.Contains("--provider-check") && valid)
{
    providerChecked = true;
    try
    {
        await sender.ValidateLiveConfigurationAsync(CancellationToken.None);
        providerLive = true;
    }
    catch (PhoneAuthException) { }
}
Console.WriteLine(JsonSerializer.Serialize(new
{
    environmentLoaded = selected,
    kavenegarSelected = selected,
    credentialsAndTemplateConfigured = valid,
    providerChecked,
    providerDebugModeDisabled = providerLive,
    smsSent = false,
    realDeliveryVerified = false
}));
return valid && (!providerChecked || providerLive) ? 0 : 2;
