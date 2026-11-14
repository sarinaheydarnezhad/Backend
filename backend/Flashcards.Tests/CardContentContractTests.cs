using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flashcards.Infrastructure.Persistence;
using Flashcards.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flashcards.Tests;

public sealed partial class SyncTests
{
    [Theory]
    [InlineData("{\"sentence\":\"valid\",\"translation\":42,\"notes\":[\"array\"]}")]
    [InlineData("{\"sentence\":\"valid\",\"translation\":42}")]
    [InlineData("{\"sentence\":\"valid\",\"notes\":[\"array\"]}")]
    [InlineData("{\"sentence\":\"valid\",\"translation\":false}")]
    [InlineData("{\"sentence\":\"valid\",\"notes\":{}}")]
    [InlineData("{\"sentence\":\"valid\",\"notes\":null}")]
    [InlineData("{\"sentence\":\"valid\",\"unknown\":\"value\"}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    public async Task Card_contract_malformed_nested_examples_are_rejected_atomically_over_HTTP(string example)
    {
        await LoginAsync("card-invalid@example.test");
        var deck = Guid.NewGuid();
        var card = Guid.NewGuid();
        var operation = Change("card", card, "upsert", new CardPayload(deck, "hello", "meaning", null, null,
            JsonDocument.Parse("[" + example + "]").RootElement.Clone()));
        var response = await Push(Change("deck", deck, "upsert", new DeckPayload("Words", "", "en", "ltr", "medium")), operation);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("example", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Empty(await db.Cards.ToListAsync());
        Assert.Empty(await db.Decks.ToListAsync());
        Assert.Empty(await db.SyncChanges.ToListAsync());
        Assert.Empty(await db.ReviewEvents.ToListAsync());
        Assert.Empty(await db.CardReviewStates.ToListAsync());
    }

    [Theory]
    [InlineData(5)]
    [InlineData(20)]
    public async Task Card_contract_large_valid_cards_upload_and_duplicate_without_loss(int count)
    {
        await LoginAsync("card-large@example.test");
        var deck = Guid.NewGuid();
        var card = Guid.NewGuid();
        var examples = JsonSerializer.SerializeToElement(Enumerable.Range(0, count).Select(_ => new {
            sentence = new string('x', 4000), translation = new string('ی', 4000), notes = string.Concat(Enumerable.Repeat("😀", 2000)) }));
        var operations = new[] {
            Change("deck", deck, "upsert", new DeckPayload("Words", "", "en", "ltr", "medium")),
            Change("card", card, "upsert", new CardPayload(deck, new string('ی',4000), new string('x',4000), new string('p',500), new string('c',120), examples)) };
        Assert.Equal(HttpStatusCode.OK, (await Push(operations)).StatusCode);
        var duplicate = await Push(operations);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.All((await duplicate.Content.ReadFromJsonAsync<SyncPushResponse>())!.Changes, item => Assert.Equal("duplicate", item.Status));
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Equal(examples.GetRawText(), (await db.Cards.SingleAsync()).ExamplesJson);
        Assert.Equal(2, await db.SyncChanges.CountAsync());
    }
}
