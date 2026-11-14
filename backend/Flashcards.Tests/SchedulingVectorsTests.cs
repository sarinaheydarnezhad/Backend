using System.Text.Json;
using Flashcards.Domain;
using Flashcards.Infrastructure.Sync;

namespace Flashcards.Tests;

public sealed class SchedulingVectorsTests
{
    [Theory]
    [InlineData("leitner")]
    [InlineData("sm2")]
    public void Shared_calendar_vectors_match_TypeScript(string scheduler)
    {
        using var data = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"SchedulingVectors.json")));
        foreach(var vector in data.RootElement.EnumerateArray())
        {
            var instant=DateTimeOffset.Parse(vector.GetProperty("instant").GetString()!,System.Globalization.CultureInfo.InvariantCulture);
            var calendar=new ReviewCalendarPayload(DateOnly.Parse(vector.GetProperty("day").GetString()!),vector.GetProperty("offset").GetInt32());
            Assert.True(SchedulerSync.ValidCalendar(calendar,instant));
            var state=new CardReviewState();
            SchedulerSync.Apply(state,"success",instant,scheduler,calendar);
            Assert.Equal(vector.GetProperty(scheduler).GetString(),state.DueDate.ToString("yyyy-MM-dd"));
            Assert.Equal(1,state.TotalReviews);
        }
    }

    [Fact]
    public void Future_tolerance_is_bounded_but_delayed_offline_upload_is_supported()
    {
        var receipt=new DateTimeOffset(2026,11,13,12,0,0,TimeSpan.Zero);
        SchedulerSync.ValidateNewTime(receipt.AddMinutes(5),receipt);
        SchedulerSync.ValidateNewTime(receipt.AddYears(-5),receipt);
        Assert.Throws<SyncException>(()=>SchedulerSync.ValidateNewTime(receipt.AddMinutes(5).AddTicks(1),receipt));
        Assert.False(SchedulerSync.ValidCalendar(new ReviewCalendarPayload(new DateOnly(2026,11,13),0),receipt.AddDays(-1)));
    }

    [Theory]
    [InlineData("leitner")]
    [InlineData("sm2")]
    public void A_new_real_review_recovers_previously_accepted_future_progress_without_rewriting_history(string scheduler)
    {
        var future=new DateTimeOffset(2076,11,13,8,0,0,TimeSpan.Zero);
        var now=new DateTimeOffset(2026,11,13,8,0,0,TimeSpan.Zero);
        var state=new CardReviewState();
        SchedulerSync.Apply(state,"success",future,scheduler);
        SchedulerSync.Apply(state,"failure",now,scheduler,new ReviewCalendarPayload(new DateOnly(2026,11,13),0));
        Assert.Equal(now,state.LastReviewedAtUtc);
        Assert.Equal(new DateOnly(2026,11,scheduler=="leitner"?13:14),state.DueDate);
        Assert.Equal(2,state.TotalReviews);
        Assert.Equal(1,state.TotalSuccesses);
    }
}
