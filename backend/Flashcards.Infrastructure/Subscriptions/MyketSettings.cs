using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;

namespace Flashcards.Infrastructure.Subscriptions;

public sealed class MyketSettings
{
    public bool Enabled { get; init; }
    public string PackageName { get; init; } = "";
    public string PublicKey { get; init; } = "";
    public string AccessToken { get; init; } = "";
    public Dictionary<int, string> Products { get; init; } = [];

    public bool IsConfigured
    {
        get
        {
            if (!Enabled || string.IsNullOrWhiteSpace(PackageName) || string.IsNullOrWhiteSpace(AccessToken) ||
                !new[] { 1, 6, 12 }.All(months => Products.TryGetValue(months, out var sku) &&
                    !string.IsNullOrWhiteSpace(sku) && sku.Length <= 200) ||
                Products.Values.Distinct(StringComparer.Ordinal).Count() != Products.Count) return false;
            try
            {
                using var rsa = RSA.Create();
                var bytes = Convert.FromBase64String(PublicKey);
                rsa.ImportSubjectPublicKeyInfo(bytes, out var read);
                return read == bytes.Length && rsa.KeySize >= 1024;
            }
            catch (Exception exception) when (exception is CryptographicException or FormatException or ArgumentException)
            { return false; }
        }
    }

    public static MyketSettings FromConfiguration(IConfiguration configuration) => new()
    {
        Enabled = configuration.GetValue<bool>("Payments:Myket:Enabled"),
        PackageName = configuration["Payments:Myket:PackageName"]?.Trim() ?? "",
        PublicKey = configuration["Payments:Myket:PublicKey"]?.Trim() ?? "",
        AccessToken = configuration["Payments:Myket:AccessToken"]?.Trim() ?? "",
        Products = new[] { 1, 6, 12 }.ToDictionary(months => months,
            months => configuration[$"Payments:Myket:Products:{months}"]?.Trim() ?? "")
    };
}
