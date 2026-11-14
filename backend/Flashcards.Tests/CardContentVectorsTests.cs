using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Flashcards.Infrastructure.Persistence;
using Flashcards.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flashcards.Tests;

public sealed class CardContentVectorsTests
{
    [Theory]
    [InlineData("[{\"sentence\":\"\\ud800\"}]")]
    [InlineData("[{\"sentence\":\"valid\",\"notes\":\"\\udfff\"}]")]
    public void Card_contract_unpaired_escaped_surrogates_fail_with_a_controlled_error(string raw)
    {
        using var examples = JsonDocument.Parse(raw);
        var exception = Record.Exception(() => CardContentContract.Validate(new CardPayload(Guid.NewGuid(), "front", "back", null, null, examples.RootElement)));
        Assert.IsType<SyncException>(exception);
    }

    [Fact]
    public void Card_contract_shared_vectors_match_TypeScript()
    {
        using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "CardContentVectors.json")));
        foreach (var vector in vectors.RootElement.EnumerateArray())
        {
            var payload = new CardPayload(Guid.NewGuid(), "front", "back", null, null, vector.GetProperty("examples"));
            var error = Record.Exception(() => CardContentContract.Validate(payload));
            Assert.True((error is null) == vector.GetProperty("valid").GetBoolean(), vector.GetProperty("name").GetString());
            if (error is not null) Assert.IsType<SyncException>(error);
        }
    }

    [Theory]
    [InlineData("frontText", 4000)]
    [InlineData("meaning", 4000)]
    [InlineData("phonetic", 500)]
    [InlineData("category", 120)]
    [InlineData("sentence", 4000)]
    [InlineData("translation", 4000)]
    [InlineData("notes", 4000)]
    public void Card_contract_field_boundaries_are_UTF16_units(string field, int maximum)
    {
        foreach (var length in new[] { maximum - 1, maximum, maximum + 1 })
        {
            // Includes paired supplementary characters, with an optional trailing ASCII unit.
            var text = string.Concat(Enumerable.Repeat("😀", length / 2)) + (length % 2 == 1 ? "x" : "");
            var fields = new Dictionary<string, object?> {
                ["deckId"] = Guid.NewGuid(), ["frontText"] = "front", ["meaning"] = "back", ["phonetic"] = null, ["category"] = null };
            var example = new Dictionary<string, object?> { ["sentence"] = "valid" };
            if (field is "sentence" or "translation" or "notes") example[field] = text;
            else fields[field] = text;
            fields["examples"] = new[] { example };
            var card = JsonSerializer.SerializeToElement(fields).Deserialize<CardPayload>(SyncValidator.JsonOptions)!;
            var error = Record.Exception(() => CardContentContract.Validate(card));
            Assert.Equal(length > maximum, error is SyncException);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void Card_contract_transport_byte_boundary_is_independent_of_logical_lengths(int delta)
    {
        var raw = JsonSerializer.Serialize(new CardPayload(Guid.NewGuid(), "سلام", "😀", null, null, JsonSerializer.SerializeToElement(Array.Empty<object>())), SyncValidator.JsonOptions);
        var length = CardContentContract.MaximumPayloadBytes + delta;
        raw = raw.Insert(1, new string(' ', length - Encoding.UTF8.GetByteCount(raw)));
        using var document = JsonDocument.Parse(raw);
        Assert.Equal(length, Encoding.UTF8.GetByteCount(document.RootElement.GetRawText()));
        var error = Record.Exception(() => CardContentContract.ValidatePayload(document.RootElement));
        Assert.Equal(delta > 0, error is SyncException);
    }

    [Fact]
    public void Card_contract_rejects_duplicate_nested_fields_and_twenty_one_examples()
    {
        foreach (var raw in new[] { "[{\"sentence\":\"first\",\"sentence\":\"second\"}]", JsonSerializer.Serialize(Enumerable.Repeat(new { sentence = "valid" }, 21)) })
        {
            using var examples = JsonDocument.Parse(raw);
            Assert.Throws<SyncException>(() => CardContentContract.Validate(new CardPayload(Guid.NewGuid(), "front", "back", null, null, examples.RootElement)));
        }
    }
}

public sealed partial class SyncTests
{
    [Theory]
    [InlineData("frontText")]
    [InlineData("sentence")]
    [InlineData("notes")]
    public async Task Card_contract_unpaired_escaped_surrogates_are_rejected_over_HTTP_without_writes(string field)
    {
        await LoginAsync("card-surrogate-invalid@example.test");
        var deck = Guid.NewGuid();
        var card = Change("card", Guid.NewGuid(), "upsert", new CardPayload(deck, field == "frontText" ? "_INVALID_" : "term", "meaning", null, null,
            JsonSerializer.SerializeToElement(new[] { new { sentence = field == "sentence" ? "_INVALID_" : "valid", notes = field == "notes" ? "_INVALID_" : "" } })));
        var raw = JsonSerializer.Serialize(new SyncPushRequest([Change("deck", deck, "upsert", new DeckPayload("Words", "", "en", "ltr", "medium")), card]), SyncValidator.JsonOptions)
            .Replace("_INVALID_", "\\ud800");
        using var body = new StringContent(raw, Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync("/api/v1/sync/push", body)).StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Empty(await db.SyncChanges.ToListAsync());
        Assert.Empty(await db.Cards.ToListAsync());
    }

    [Fact]
    public async Task Card_contract_legacy_oversized_feed_is_preserved_without_unbounded_response_or_cursor_skip()
    {
        await LoginAsync("card-feed-oversized@example.test");
        var deck = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await Push(Change("deck", deck, "upsert", new DeckPayload("Words", "", "en", "ltr", "medium")))).StatusCode);
        var raw = JsonSerializer.Serialize(new { notes = new string('x', CardContentContract.PushBytes) });
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            var owner = await db.Users.SingleAsync();
            db.SyncChanges.Add(new Flashcards.Domain.SyncChange { UserId = owner.Id, Version = 2, DeviceId = Guid.NewGuid(),
                ClientChangeId = Guid.NewGuid(), EntityType = "card", EntityId = Guid.NewGuid(), Operation = "upsert", PayloadJson = raw, Fingerprint = "legacy" });
            await db.SaveChangesAsync();
            await db.SyncHeads.ExecuteUpdateAsync(update => update.SetProperty(head => head.Version, 2));
        }
        var before = (await _client.GetFromJsonAsync<SyncPullResponse>("/api/v1/sync/pull?cursor=0"))!;
        Assert.Equal(1, before.Cursor);
        Assert.True(before.HasMore);
        var blocked = await _client.GetAsync("/api/v1/sync/pull?cursor=1");
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("sync_payload_recovery_required", await blocked.Content.ReadAsStringAsync());
        using var inspect = _factory.Services.CreateScope();
        var stored = inspect.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Equal(raw, (await stored.SyncChanges.SingleAsync(change => change.Version == 2)).PayloadJson);
        Assert.Equal(2, (await stored.SyncHeads.SingleAsync()).Version);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Card_contract_HTTP_body_byte_limit_is_enforced_for_unknown_length_streams(int delta)
    {
        await LoginAsync("card-request-boundary@example.test");
        using var content = new StreamContent(new NonSeekableCardStream(Encoding.UTF8.GetBytes("{}" + new string(' ', CardContentContract.PushBytes + delta - 2))));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        Assert.Null(content.Headers.ContentLength);
        Assert.Equal(delta > 0 ? HttpStatusCode.RequestEntityTooLarge : HttpStatusCode.BadRequest,
            (await _client.PostAsync("/api/v1/sync/push", content)).StatusCode);
        using var scope = _factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().SyncChanges.ToListAsync());
    }

    private sealed class NonSeekableCardStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    [Theory]
    [InlineData("\u0085")]
    [InlineData("\ufeffhello\ufeff")]
    public async Task Card_contract_ECMAScript_trim_and_backend_storage_agree(string front)
    {
        await LoginAsync("card-whitespace@example.test");
        var deck = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await Push(Change("deck", deck, "upsert", new DeckPayload("Words", "", "en", "ltr", "medium")),
            Change("card", Guid.NewGuid(), "upsert", new CardPayload(deck, front, "meaning", null, null, JsonSerializer.SerializeToElement(Array.Empty<object>()))))).StatusCode);
        using var scope = _factory.Services.CreateScope();
        Assert.Equal(CardContentContract.Trim(front), (await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().Cards.SingleAsync()).FrontText);
    }

    [Fact]
    public async Task Card_contract_legacy_malformed_bootstrap_retains_source_and_rolls_back_the_new_feed()
    {
        await LoginAsync("card-bootstrap-invalid@example.test");
        const string malformed = "[{\"sentence\":\"valid\",\"translation\":42,\"notes\":[\"array\"]}]";
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            var owner = await db.Users.SingleAsync();
            db.Add(new Flashcards.Domain.Card { Deck = new Flashcards.Domain.Deck { UserId = owner.Id, Name = "Legacy" }, FrontText = "term", Meaning = "meaning", ExamplesJson = malformed });
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Conflict, (await _client.GetAsync("/api/v1/sync/pull?cursor=0")).StatusCode);
        using var inspect = _factory.Services.CreateScope();
        var stored = inspect.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Equal(malformed, (await stored.Cards.SingleAsync()).ExamplesJson);
        Assert.Empty(await stored.SyncChanges.ToListAsync());
        Assert.Equal(0, (await stored.SyncHeads.SingleAsync()).Version);
    }

    [Fact]
    public async Task Card_contract_audit_exact_five_sentences_upload_edit_and_retry_succeed()
    {
        await LoginAsync("card-audit@example.test");
        var deck = Guid.NewGuid(); var card = Guid.NewGuid();
        var examples = JsonSerializer.SerializeToElement(Enumerable.Repeat(new { sentence = new string('a', 4000) }, 5));
        var payload = new CardPayload(deck, "term", "meaning", null, null, examples);
        var operations = new[] { Change("deck", deck, "upsert", new DeckPayload("Words", "", "en", "ltr", "medium")), Change("card", card, "upsert", payload) };
        Assert.Equal(HttpStatusCode.OK, (await Push(operations)).StatusCode);
        var edit = Change("card", card, "upsert", payload with { Meaning = "Persian فارسی \"quoted\" \\ newline\n😀" }, 2);
        Assert.Equal(HttpStatusCode.OK, (await Push(edit)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Push(edit)).StatusCode);
        var pull = await _client.GetFromJsonAsync<SyncPullResponse>("/api/v1/sync/pull?cursor=0");
        Assert.Equal(2, pull!.SchedulingContractVersion);
        Assert.Equal(5, pull.Changes.Last().Payload.GetProperty("examples").GetArrayLength());
        using var scope = _factory.Services.CreateScope();
        var stored = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Equal(edit.Payload.GetProperty("meaning").GetString(), (await stored.Cards.SingleAsync()).Meaning);
        Assert.Equal(3, await stored.SyncChanges.CountAsync());
    }

    [Fact]
    public async Task Card_contract_large_push_batches_are_bounded_and_pull_pages_remain_contiguous()
    {
        await LoginAsync("card-batch@example.test");
        var deck = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await Push(Change("deck", deck, "upsert", new DeckPayload("Words", "", "en", "ltr", "medium")))).StatusCode);
        var examples = JsonSerializer.SerializeToElement(Enumerable.Repeat(new { sentence = new string('ی', 4000), translation = new string('ی', 4000), notes = new string('ی', 4000) }, 20));
        var operations = Enumerable.Range(0, 3).Select(_ => Change("card", Guid.NewGuid(), "upsert", new CardPayload(deck, "front", "back", null, null, examples))).ToArray();
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Push(operations)).StatusCode);
        using (var scope = _factory.Services.CreateScope()) Assert.Empty(await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().Cards.ToListAsync());
        foreach (var operation in operations) Assert.Equal(HttpStatusCode.OK, (await Push(operation)).StatusCode);
        long cursor = 1;
        foreach (var operation in operations)
        {
            var response = await _client.GetAsync($"/api/v1/sync/pull?cursor={cursor}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True((await response.Content.ReadAsByteArrayAsync()).Length <= CardContentContract.PushBytes);
            var page = (await response.Content.ReadFromJsonAsync<SyncPullResponse>())!;
            Assert.Single(page.Changes);
            Assert.Equal(++cursor, page.Cursor);
        }
        Assert.Equal(4, cursor);
    }
}
