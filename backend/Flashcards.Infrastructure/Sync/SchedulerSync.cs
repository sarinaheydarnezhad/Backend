using System.Text.Json;
using Flashcards.Domain;

namespace Flashcards.Infrastructure.Sync;

public sealed record Sm2Data(int Repetitions, int IntervalDays, double EaseFactor);
public sealed record SchedulerStatePayload(string SchedulerId, string Key, Sm2Data Data);
public sealed record SchedulerEventPayload(string SchedulerId, string PreviousState, string NewState);

internal static class SchedulerSync
{
    internal static T? Read<T>(string? json) where T : class =>
        json is null ? null : JsonSerializer.Deserialize<T>(json, SyncValidator.JsonOptions);

    internal static string? Json<T>(T? value) where T : class =>
        value is null ? null : JsonSerializer.Serialize(value, SyncValidator.JsonOptions);

    internal static bool ValidState(SchedulerStatePayload? state) => state is null ||
        state.SchedulerId == "sm2" && Key(state.Key) && state.Data is not null &&
        state.Data.Repetitions >= 0 && state.Data.IntervalDays >= 0 &&
        double.IsFinite(state.Data.EaseFactor) && state.Data.EaseFactor >= 1.3;

    internal static bool ValidEvent(SchedulerEventPayload? review) => review is null ||
        review.SchedulerId == "sm2" && Key(review.PreviousState) && Key(review.NewState);

    private static bool Key(string key) => key is "new" or "learning" or "review";

    // Replay in server receipt order on every device. Device timestamps cannot move time backwards.
    internal static void Apply(CardReviewState state, string result, DateTimeOffset reviewedAt, string reviewSystem)
    {
        var effective = state.LastReviewedAtUtc > reviewedAt ? state.LastReviewedAtUtc.Value : reviewedAt;
        int interval;
        if (reviewSystem == "sm2")
        {
            var previous = Read<SchedulerStatePayload>(state.SchedulerStateJson)?.Data ?? new Sm2Data(0, 0, 2.5);
            var quality = result == "success" ? 5 : 2;
            var ease = Math.Round(Math.Max(1.3, previous.EaseFactor + 0.1 -
                (5 - quality) * (0.08 + (5 - quality) * 0.02)), 2, MidpointRounding.AwayFromZero);
            var repetitions = result == "success" ? checked(previous.Repetitions + 1) : 0;
            interval = result == "failure" ? 1 : repetitions == 1 ? 1 : repetitions == 2 ? 6 :
                Math.Max(1, checked((int)Math.Round(previous.IntervalDays * ease, MidpointRounding.AwayFromZero)));
            state.Box = 1;
            state.SchedulerStateJson = Json(new SchedulerStatePayload("sm2", repetitions < 2 ? "learning" : "review",
                new Sm2Data(repetitions, interval, ease)));
        }
        else
        {
            state.Box = result == "success" ? Math.Min(state.Box + 1, 5) : 1;
            interval = result == "success" ? 1 << (state.Box - 1) : 0;
        }
        state.DueDate = DateOnly.FromDateTime(effective.UtcDateTime).AddDays(interval);
        state.LastReviewedAtUtc = effective;
        state.TotalReviews++;
        if (result == "success") { state.TotalSuccesses++; state.ConsecutiveSuccesses++; }
        else state.ConsecutiveSuccesses = 0;
    }
}
