using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flashcards.Infrastructure.Backups;
using Flashcards.Infrastructure.Persistence;
using Flashcards.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flashcards.Tests;

public sealed partial class BackupTests
{
    [Theory]
    [InlineData("leitner")]
    [InlineData("sm2")]
    public async Task Scheduling_future_legacy_evidence_survives_restore_and_a_real_review_recovers_the_schedule(string system)
    {
        await LoginAsync("legacy-clock-backup@example.test");
        Guid owner;var deck=new Flashcards.Domain.Deck{Name="Legacy clock",ReviewSystem=system};
        var card=new Flashcards.Domain.Card{Deck=deck,FrontText="term",Meaning="meaning"};
        var future=new DateTimeOffset(2076,11,13,8,0,0,TimeSpan.Zero);
        using(var scope=_factory.Services.CreateScope()){
            var database=scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();owner=await database.Users.Select(u=>u.Id).SingleAsync();deck.UserId=owner;
            database.AddRange(card,new Flashcards.Domain.CardReviewState{Card=card,Box=system=="leitner"?2:1,DueDate=new DateOnly(2076,11,15),LastReviewedAtUtc=future,
                TotalReviews=1,TotalSuccesses=1,ConsecutiveSuccesses=1,SchedulerStateJson=system=="sm2"?JsonSerializer.Serialize(new SchedulerStatePayload("sm2","learning",new Sm2Data(1,1,2.6)),SyncValidator.JsonOptions):null},
                new Flashcards.Domain.ReviewEvent{UserId=owner,Deck=deck,Card=card,PreviousBox=1,NewBox=system=="leitner"?2:1,Result="success",ReviewedAtUtc=future,
                    SchedulerJson=system=="sm2"?JsonSerializer.Serialize(new SchedulerEventPayload("sm2","new","learning"),SyncValidator.JsonOptions):null});
            await database.SaveChangesAsync();
        }
        var created=await CreateAsync(Guid.NewGuid());Assert.Equal(HttpStatusCode.Created,created.StatusCode);
        var backup=(await created.Content.ReadFromJsonAsync<BackupMetadata>())!;
        await EraseLearningAsync(new BackupFixture(backup.Id,owner,deck.Id,[card.Id]));
        Assert.Equal(HttpStatusCode.OK,(await RestoreAsync(backup.Id)).StatusCode);
        var correction=new SyncPushChange(Guid.NewGuid(),Guid.NewGuid(),"reviewEvent",Guid.NewGuid(),"create",_clock.Now,0,
            JsonSerializer.SerializeToElement(new ReviewEventPayload(deck.Id,card.Id,system=="leitner"?2:1,1,"failure",_clock.Now,null,
                system=="sm2"?new SchedulerEventPayload("sm2","learning","learning"):null,new ReviewCalendarPayload(DateOnly.FromDateTime(_clock.Now.UtcDateTime),0)),SyncValidator.JsonOptions));
        Assert.Equal(HttpStatusCode.OK,(await _client.PostAsJsonAsync("/api/v1/sync/push",new SyncPushRequest([correction]))).StatusCode);
        using var inspect=_factory.Services.CreateScope();var stored=inspect.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.True(await stored.ReviewEvents.AnyAsync(e=>e.ReviewedAtUtc==future));
        var state=await stored.CardReviewStates.SingleAsync();Assert.Equal(2,state.TotalReviews);Assert.Equal(1,state.TotalSuccesses);
        Assert.Equal(DateOnly.FromDateTime(_clock.Now.UtcDateTime).AddDays(system=="sm2"?1:0),state.DueDate);
        var pull=await _client.GetFromJsonAsync<SyncPullResponse>("/api/v1/sync/pull?cursor=0");
        Assert.Equal(2,Assert.Single(pull!.ReviewCheckpoints!).State.TotalReviews);
    }
    [Theory]
    [InlineData("leitner")]
    [InlineData("sm2")]
    public async Task Scheduling_calendar_and_partial_baseline_survive_backup_restore_and_fresh_pull(string system)
    {
        await LoginAsync("calendar-baseline-backup@example.test");
        var deck=Guid.NewGuid();var card=Guid.NewGuid();var device=Guid.NewGuid();var eventId=Guid.NewGuid();
        SyncPushChange Change(string type,Guid id,object payload)=>new(Guid.NewGuid(),device,type,id,type=="reviewEvent"?"create":"upsert",
            _clock.Now,0,JsonSerializer.SerializeToElement(payload,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var calendar=new ReviewCalendarPayload(new DateOnly(2026,10,15),210);
        var initial=new SyncPushRequest([
            Change("deck",deck,new DeckPayload("Calendar baseline","","en","ltr","medium",system)),
            Change("card",card,new CardPayload(deck,"term","meaning",null,null,JsonSerializer.SerializeToElement(Array.Empty<object>()))),
            Change("reviewState",card,new ReviewStatePayload(system=="leitner"?4:1,new DateOnly(2026,10,15),_clock.Now.AddDays(-1),3,9,8,
                system=="sm2"?new SchedulerStatePayload("sm2","review",new Sm2Data(3,16,2.8)):null)),
            Change("reviewEvent",eventId,new ReviewEventPayload(deck,card,system=="leitner"?4:1,system=="leitner"?5:1,"success",
                new DateTimeOffset(2026,10,14,21,30,0,TimeSpan.Zero),null,system=="sm2"?new SchedulerEventPayload("sm2","review","review"):null,calendar))
        ]);
        Assert.Equal(HttpStatusCode.OK,(await _client.PostAsJsonAsync("/api/v1/sync/push",initial)).StatusCode);
        var response=await CreateAsync(Guid.NewGuid(),4);
        Assert.Equal(HttpStatusCode.Created,response.StatusCode);
        var backup=(await response.Content.ReadFromJsonAsync<BackupMetadata>())!;
        using var document=JsonDocument.Parse(await _client.GetByteArrayAsync($"/api/v1/backups/{backup.Id}/download"));
        var savedCalendar=document.RootElement.GetProperty("reviewEvents")[0].GetProperty("reviewCalendarJson").GetString();
        Assert.NotNull(savedCalendar);
        Guid owner;
        using(var scope=_factory.Services.CreateScope())owner=await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().Users.Select(u=>u.Id).SingleAsync();
        await EraseLearningAsync(new BackupFixture(backup.Id,owner,deck,[card]));
        Assert.Equal(HttpStatusCode.OK,(await RestoreAsync(backup.Id,4)).StatusCode);
        using(var inspect=_factory.Services.CreateScope()){
            var database=inspect.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            Assert.Equal(savedCalendar,(await database.ReviewEvents.SingleAsync()).ReviewCalendarJson);
            Assert.Equal(10,(await database.CardReviewStates.SingleAsync()).TotalReviews);
        }
        var pull=await _client.GetFromJsonAsync<SyncPullResponse>("/api/v1/sync/pull?cursor=0");
        Assert.Equal(10,Assert.Single(pull!.ReviewCheckpoints!).State.TotalReviews);
        Assert.Equal(9,Assert.Single(pull.ReviewCheckpoints!).State.TotalSuccesses);
    }
}
