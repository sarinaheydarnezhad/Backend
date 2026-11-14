using System.Text;
using System.Text.Json;

namespace Flashcards.Infrastructure.Sync;

// Equivalent to frontend card.ts; see docs/card-content-contract.md.
public static class CardContentContract
{
    public const int TextUnits = 4000;
    public const int MaximumExamples = 20;
    public const int MaximumPayloadBytes = 6 * (2 * TextUnits + 500 + 120 + MaximumExamples * 3 * TextUnits) + 2048;
    public const int PushBytes = 2 * 1024 * 1024;
    public const int GuestBytes = 10 * 1024 * 1024;
    // ECMAScript trim set: .NET's default Trim differs for U+0085 and U+FEFF.
    private static readonly char[] TrimCharacters = "\u0009\u000a\u000b\u000c\u000d\u0020\u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000\ufeff".ToCharArray();
    internal static string Trim(string value) => value.Trim(TrimCharacters);

    internal static bool Text(string? value, int maximum, bool required = false)
    {
        if (value is null || value.Length > maximum || (required && Trim(value).Length == 0)) return false;
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (++index >= value.Length || !char.IsLowSurrogate(value[index])) return false;
            }
            else if (char.IsLowSurrogate(value[index])) return false;
        }
        return true;
    }

    internal static bool ExampleText(JsonElement value, bool required)
    {
        if (value.ValueKind != JsonValueKind.String) return false;
        try { return Text(value.GetString(), TextUnits, required); }
        // JsonDocument accepts escaped lone surrogates, but GetString cannot decode them.
        catch (InvalidOperationException) { return false; }
        catch (JsonException) { return false; }
    }

    internal static void Validate(CardPayload card)
    {
        Require(card.DeckId != Guid.Empty, "deckId");
        Require(Text(card.FrontText, TextUnits, true), "frontText");
        Require(Text(card.Meaning, TextUnits, true), "meaning");
        Require(card.Phonetic is null || Text(card.Phonetic, 500), "phonetic");
        Require(card.Category is null || Text(card.Category, 120), "category");
        Require(card.Examples.ValueKind == JsonValueKind.Array && card.Examples.GetArrayLength() <= MaximumExamples, "examples");
        var index = 0;
        foreach (var example in card.Examples.EnumerateArray())
        {
            var path = $"examples[{index++}]";
            Require(example.ValueKind == JsonValueKind.Object, path);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in example.EnumerateObject())
            {
                Require(names.Add(property.Name) && property.Name is "sentence" or "translation" or "notes", path);
                Require(ExampleText(property.Value, property.Name == "sentence"), $"{path}.{property.Name}");
            }
            Require(names.Contains("sentence"), $"{path}.sentence");
        }
    }

    internal static void Require(bool valid, string path)
    {
        if (!valid) throw new SyncException($"Invalid card field {path}. Check its type and length; original data was retained.", code: "card_content_invalid");
    }

    internal static void ValidatePayload(JsonElement payload)
    {
        Require(payload.ValueKind == JsonValueKind.Object && Encoding.UTF8.GetByteCount(payload.GetRawText()) <= MaximumPayloadBytes, "payload");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var property in payload.EnumerateObject()) Require(names.Add(property.Name), "payload");
        }
        catch (InvalidOperationException) { Require(false, "payload"); }
    }
}
