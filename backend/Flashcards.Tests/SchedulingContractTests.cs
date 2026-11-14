using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flashcards.Infrastructure.Sync;
using Flashcards.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flashcards.Tests;

public sealed partial class SyncTests
{
    [Theory]
    [InlineData("leitner", true)]
    [InlineData("sm2", false)]
    public async Task Scheduling_legacy_Leitner_wire_retries_are_duplicates_only_when_the_accepted_scheduler_was_Leitner(string originalSystem, bool equivalent)
    {
        await LoginAsync("legacy-wire-retry@example.test");
        var deck = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await Push(Change("deck", deck, "upsert", new DeckPayload("Legacy retry", "", "en", "ltr", "medium", originalSystem)))).StatusCode);
        // Older clients omitted explicit Leitner when serializing this already accepted operation.
        var omitted = Change("deck", deck, "upsert", new DeckPayload("Legacy retry", "", "en", "ltr", "medium"), 1);
        Assert.Equal(HttpStatusCode.OK, (await Push(omitted)).StatusCode);
        var retry = omitted with { Payload = JsonSerializer.SerializeToElement(new DeckPayload("Legacy retry", "", "en", "ltr", "medium", "leitner"), SyncValidator.JsonOptions) };
        var response = await Push(retry);
        Assert.Equal(equivalent ? HttpStatusCode.OK : HttpStatusCode.Conflict, response.StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            Assert.Equal(2, await database.SyncChanges.CountAsync());
            Assert.Equal(originalSystem, (await database.Decks.SingleAsync()).ReviewSystem);
        }
        if (equivalent) Assert.Equal("duplicate", Assert.Single((await response.Content.ReadFromJsonAsync<SyncPushResponse>())!.Changes).Status);
        else Assert.Equal(HttpStatusCode.OK, (await Push(Change("deck", deck, "upsert", new DeckPayload("Legacy retry", "", "en", "ltr", "medium", "leitner"), 2))).StatusCode);
    }
    [Theory]
    [InlineData("leitner", false)]
    [InlineData("sm2", false)]
    [InlineData("leitner", true)]
    [InlineData("sm2", true)]
    public async Task Scheduling_bootstrap_retains_legacy_calendar_and_clock_progress_before_a_real_corrective_review(string system, bool futureClock)
    {
        await LoginAsync("legacy-projection-contract@example.test");
        var originalTime = futureClock ? new DateTimeOffset(2076, 11, 13, 8, 0, 0, TimeSpan.Zero)
            : new DateTimeOffset(2026, 11, 12, 21, 30, 0, TimeSpan.Zero);
        var savedDue = futureClock ? new DateOnly(2076, 11, system == "leitner" ? 13 : 14)
            : new DateOnly(2026, 11, system == "leitner" ? 15 : 14);
        var deck = new Flashcards.Domain.Deck { Name = "Accepted legacy projection", ReviewSystem = system };
        var card = new Flashcards.Domain.Card { Deck = deck, FrontText = "term", Meaning = "meaning" };
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            deck.UserId = await database.Users.Select(item => item.Id).SingleAsync();
            database.AddRange(card, new Flashcards.Domain.CardReviewState
            {
                Card = card, Box = futureClock || system == "sm2" ? 1 : 2, DueDate = savedDue,
                LastReviewedAtUtc = originalTime, TotalReviews = futureClock ? 2 : 1, TotalSuccesses = 1,
                ConsecutiveSuccesses = futureClock ? 0 : 1,
                SchedulerStateJson = system == "sm2" ? JsonSerializer.Serialize(new SchedulerStatePayload("sm2", "learning",
                    new Sm2Data(futureClock ? 0 : 1, 1, futureClock ? 2.4 : 2.6)), SyncValidator.JsonOptions) : null
            }, new Flashcards.Domain.ReviewEvent
            {
                UserId = deck.UserId, Deck = deck, Card = card, PreviousBox = 1, NewBox = system == "leitner" ? 2 : 1,
                Result = "success", ReviewedAtUtc = originalTime,
                SchedulerJson = system == "sm2" ? JsonSerializer.Serialize(new SchedulerEventPayload("sm2", "new", "learning"), SyncValidator.JsonOptions) : null
            });
            if (futureClock) database.Add(new Flashcards.Domain.ReviewEvent
            {
                UserId = deck.UserId, Deck = deck, Card = card, PreviousBox = system == "leitner" ? 2 : 1, NewBox = 1,
                Result = "failure", ReviewedAtUtc = new DateTimeOffset(2026, 11, 13, 8, 0, 0, TimeSpan.Zero),
                SchedulerJson = system == "sm2" ? JsonSerializer.Serialize(new SchedulerEventPayload("sm2", "learning", "learning"), SyncValidator.JsonOptions) : null
            });
            await database.SaveChangesAsync();
        }
        var response = await _client.GetAsync("/api/v1/sync/pull?cursor=0");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var checkpoint = Assert.Single((await response.Content.ReadFromJsonAsync<SyncPullResponse>())!.ReviewCheckpoints!);
        Assert.Equal(savedDue, checkpoint.State.DueDate);
        Assert.Equal(futureClock ? 2 : 1, checkpoint.State.TotalReviews);
        var corrected = new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal(HttpStatusCode.OK, (await Push(Change("reviewEvent", Guid.NewGuid(), "create",
            new ReviewEventPayload(deck.Id, card.Id, checkpoint.State.Box, 1, "failure", corrected, null,
                system == "sm2" ? new SchedulerEventPayload("sm2", "learning", "learning") : null,
                new ReviewCalendarPayload(new DateOnly(2026, 11, 13), 0))))).StatusCode);
        using var inspect = _factory.Services.CreateScope();
        var stored = inspect.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Equal(futureClock ? 3 : 2, (await stored.CardReviewStates.SingleAsync()).TotalReviews);
        Assert.Equal(new DateOnly(2026, 11, system == "leitner" ? 13 : 14), (await stored.CardReviewStates.SingleAsync()).DueDate);
        Assert.True(await stored.ReviewEvents.AnyAsync(item => item.ReviewedAtUtc == originalTime && item.ReviewCalendarJson == null));
    }
    [Fact]
    public async Task Scheduling_an_accepted_nonzero_baseline_cannot_be_replaced_by_a_conflicting_snapshot()
    {
        await LoginAsync("baseline-conflict@example.test");var deck=Guid.NewGuid();var card=Guid.NewGuid();
        await Push(Change("deck",deck,"upsert",new DeckPayload("Baseline","","en","ltr","medium")),
            Change("card",card,"upsert",new CardPayload(deck,"term","meaning",null,null,JsonSerializer.SerializeToElement(Array.Empty<object>()))),
            Change("reviewState",card,"upsert",new ReviewStatePayload(4,new DateOnly(2026,11,13),new DateTimeOffset(2026,11,12,8,0,0,TimeSpan.Zero),3,9,8)));
        Assert.Equal(HttpStatusCode.OK, (await Push(Change("reviewState", card, "upsert",
            new ReviewStatePayload(4, new DateOnly(2026, 11, 13), new DateTimeOffset(2026, 11, 12, 8, 0, 0, TimeSpan.Zero), 3, 9, 8), 3))).StatusCode);
        var overwritten=await Push(Change("reviewState",card,"upsert",new ReviewStatePayload(1,new DateOnly(2026,11,13),null,0,0,0),4));
        Assert.Equal(HttpStatusCode.Conflict,overwritten.StatusCode);
        using var scope=_factory.Services.CreateScope();Assert.Equal(9,(await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().CardReviewStates.SingleAsync()).TotalReviews);
    }
    [Fact]
    public async Task Scheduling_guest_transfer_preserves_explicit_calendar_without_UTC_reinterpretation()
    {
        await LoginAsync("guest-calendar-contract@example.test");
        var deck=Guid.NewGuid();var card=Guid.NewGuid();
        var changes=new[]{ Change("deck",deck,"upsert",new DeckPayload("Guest calendar","","en","ltr","medium")),
            Change("card",card,"upsert",new CardPayload(deck,"term","meaning",null,null,JsonSerializer.SerializeToElement(Array.Empty<object>()))),
            Change("reviewEvent",Guid.NewGuid(),"create",new ReviewEventPayload(deck,card,1,2,"success",new DateTimeOffset(2026,11,12,21,30,0,TimeSpan.Zero),null,null,new ReviewCalendarPayload(new DateOnly(2026,11,13),210))) };
        Assert.Equal(HttpStatusCode.OK,(await MigrateAsync(changes)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await MigrateAsync(changes)).StatusCode);
        var state=await _client.GetFromJsonAsync<JsonElement>($"/api/v1/me/cards/{card}/review-state");
        Assert.Equal("2026-11-15",state.GetProperty("dueDate").GetString());
        Assert.Equal(1,state.GetProperty("totalReviews").GetInt32());
    }
    [Theory]
    [InlineData("leitner")]
    [InlineData("sm2")]
    public async Task Scheduling_initial_baseline_is_retained_in_fresh_install_checkpoints(string system)
    {
        await LoginAsync("baseline-contract@example.test");
        var deck=Guid.NewGuid();var card=Guid.NewGuid();
        await Push(Change("deck",deck,"upsert",new DeckPayload("Baseline","","en","ltr","medium",system)),
            Change("card",card,"upsert",new CardPayload(deck,"term","meaning",null,null,JsonSerializer.SerializeToElement(Array.Empty<object>()))));
        var before=new ReviewStatePayload(system=="leitner"?4:1,new DateOnly(2026,11,13),new DateTimeOffset(2026,11,12,8,0,0,TimeSpan.Zero),3,9,8,
            system=="sm2"?new SchedulerStatePayload("sm2","review",new Sm2Data(3,16,2.8)):null);
        Assert.Equal(HttpStatusCode.OK,(await Push(Change("reviewState",card,"upsert",before))).StatusCode);
        var review=Change("reviewEvent",Guid.NewGuid(),"create",new ReviewEventPayload(deck,card,system=="leitner"?4:1,system=="leitner"?5:1,"success",
            new DateTimeOffset(2026,11,13,8,0,0,TimeSpan.Zero),null,system=="sm2"?new SchedulerEventPayload("sm2","review","review"):null,new ReviewCalendarPayload(new DateOnly(2026,11,13),0)));
        Assert.Equal(HttpStatusCode.OK,(await Push(review)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await Push(review)).StatusCode);
        var pull=await _client.GetFromJsonAsync<SyncPullResponse>("/api/v1/sync/pull?cursor=0&limit=2");
        Assert.Equal(2,pull!.SchedulingContractVersion);
        var checkpoint=Assert.Single(pull.ReviewCheckpoints!);
        Assert.Equal(4,checkpoint.ThroughVersion);
        Assert.Equal(10,checkpoint.State.TotalReviews);Assert.Equal(9,checkpoint.State.TotalSuccesses);
        if(system=="leitner")Assert.Equal(5,checkpoint.State.Box);
        else Assert.Equal(4,checkpoint.State.SchedulerState!.Data.Repetitions);
        var remaining=await _client.GetFromJsonAsync<SyncPullResponse>("/api/v1/sync/pull?cursor=2");
        Assert.Equal(10,Assert.Single(remaining!.ReviewCheckpoints!).State.TotalReviews);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scheduling_retained_cards_block_changes_even_when_archived(bool archived)
    {
        await LoginAsync("scheduler-retained@example.test");
        var deck=Guid.NewGuid();var card=Guid.NewGuid();
        await Push(Change("deck",deck,"upsert",new DeckPayload("Retained","","en","ltr","medium","leitner")),
            Change("card",card,"upsert",new CardPayload(deck,"term","meaning",null,null,JsonSerializer.SerializeToElement(Array.Empty<object>()))));
        if(archived)Assert.Equal(HttpStatusCode.OK,(await Push(Change<object?>("card",card,"archive",null,2))).StatusCode);
        var rejected=await Push(Change("deck",deck,"upsert",new DeckPayload("Retained","","en","ltr","medium","sm2"),1));
        Assert.Equal(HttpStatusCode.Conflict,rejected.StatusCode);
        Assert.True((await rejected.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("conflict",out _));
    }

    [Fact]
    public async Task Scheduling_rejects_inconsistent_calendar_metadata_without_history_changes()
    {
        await LoginAsync("calendar-invalid@example.test");
        var deck=Guid.NewGuid();var card=Guid.NewGuid();
        await Push(Change("deck",deck,"upsert",new DeckPayload("Calendar","","en","ltr","medium")),
            Change("card",card,"upsert",new CardPayload(deck,"term","meaning",null,null,JsonSerializer.SerializeToElement(Array.Empty<object>()))));
        var invalid=new ReviewEventPayload(deck,card,1,2,"success",new DateTimeOffset(2026,11,12,21,30,0,TimeSpan.Zero),null,null,new ReviewCalendarPayload(new DateOnly(2026,11,13),0));
        Assert.Equal(HttpStatusCode.BadRequest,(await Push(Change("reviewEvent",Guid.NewGuid(),"create",invalid))).StatusCode);
        using var scope=_factory.Services.CreateScope();Assert.Empty(await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().ReviewEvents.ToListAsync());
    }
    [Theory]
    [InlineData("leitner", "sm2")]
    [InlineData("sm2", "leitner")]
    public async Task Scheduling_empty_deck_can_change_system_in_both_directions(string from, string to)
    {
        await LoginAsync("scheduler-change@example.test");
        var id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await Push(Change("deck", id, "upsert", new DeckPayload("Empty", "", "en", "ltr", "medium", from)))).StatusCode);
        var changed = await Push(Change("deck", id, "upsert", new DeckPayload("Empty", "", "en", "ltr", "medium", to), 1));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        using var scope = _factory.Services.CreateScope();
        Assert.Equal(to, (await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().Decks.SingleAsync()).ReviewSystem);
    }

    [Theory]
    [InlineData("leitner", "2026-11-15")]
    [InlineData("sm2", "2026-11-14")]
    public async Task Scheduling_preserves_Tehran_calendar_day_over_HTTP(string system, string due)
    {
        await LoginAsync("calendar@example.test");
        var deck = Guid.NewGuid(); var card = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await Push(Change("deck", deck, "upsert", new DeckPayload("Calendar", "", "en", "ltr", "medium", system)),
            Change("card", card, "upsert", new CardPayload(deck,"term","meaning",null,null,JsonSerializer.SerializeToElement(Array.Empty<object>()))))).StatusCode);
        var body = new { deckId=deck, cardId=card, previousBox=1, newBox=system=="leitner"?2:1, result="success",
            reviewedAtUtc="2026-11-12T21:30:00.000Z", studySessionId=(Guid?)null,
            scheduler=system=="sm2"?new SchedulerEventPayload("sm2","new","learning"):null,
            reviewCalendar=new { reviewDate="2026-11-13", utcOffsetMinutes=210 } };
        var response=await Push(Change("reviewEvent",Guid.NewGuid(),"create",body));
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var state=await _client.GetFromJsonAsync<JsonElement>($"/api/v1/me/cards/{card}/review-state");
        Assert.Equal(due,state.GetProperty("dueDate").GetString());
    }

    [Fact]
    public async Task Scheduling_rejects_a_fifty_year_future_review_without_mutation()
    {
        await LoginAsync("future-clock@example.test");
        var deck=Guid.NewGuid();var card=Guid.NewGuid();
        await Push(Change("deck",deck,"upsert",new DeckPayload("Clock","","en","ltr","medium")),
            Change("card",card,"upsert",new CardPayload(deck,"term","meaning",null,null,JsonSerializer.SerializeToElement(Array.Empty<object>()))));
        var invalid=Change("reviewEvent",Guid.NewGuid(),"create",new ReviewEventPayload(deck,card,1,2,"success",DateTimeOffset.UtcNow.AddYears(50),null));
        var response=await Push(invalid);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,(await Push(invalid)).StatusCode);
        using var scope=_factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().ReviewEvents.ToListAsync());
    }
}
