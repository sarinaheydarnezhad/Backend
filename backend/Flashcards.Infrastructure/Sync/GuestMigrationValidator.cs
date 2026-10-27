namespace Flashcards.Infrastructure.Sync;

internal static class GuestMigrationValidator
{
    internal static IReadOnlyList<ValidatedSyncChange> Validate(SyncPushRequest request)
    {
        var changes = SyncValidator.Validate(request, 10000);
        var identities = new HashSet<(string Type, Guid Id)>();
        var decks = new HashSet<Guid>();
        var cards = new Dictionary<Guid, Guid>();
        var states = new HashSet<Guid>();
        var progress = new Dictionary<Guid, (int Box, DateTimeOffset ReviewedAt)>();
        foreach (var item in changes)
        {
            var change = item.Change;
            if (change.ExpectedVersion != 0 || change.Operation == "archive" ||
                !identities.Add((change.EntityType, change.EntityId)))
                throw Invalid();
            switch (item.Payload)
            {
                case DeckPayload:
                    decks.Add(change.EntityId);
                    break;
                case CardPayload card when decks.Contains(card.DeckId):
                    cards.Add(change.EntityId, card.DeckId);
                    break;
                case ReviewStatePayload state when cards.ContainsKey(change.EntityId):
                    if (progress.ContainsKey(change.EntityId) || state.TotalReviews != 0 || state.Box != 1 ||
                        state.LastReviewedAtUtc is not null)
                        throw Invalid();
                    states.Add(change.EntityId);
                    break;
                case ReviewEventPayload review when cards.TryGetValue(review.CardId, out var deckId) && deckId == review.DeckId:
                    var previous = progress.GetValueOrDefault(review.CardId, (Box: 1, ReviewedAt: DateTimeOffset.MinValue));
                    var nextBox = review.Result == "success" ? Math.Min(previous.Box + 1, 5) : 1;
                    if (states.Contains(review.CardId) || review.PreviousBox != previous.Box || review.NewBox != nextBox ||
                        review.ReviewedAtUtc < previous.ReviewedAt)
                        throw Invalid();
                    progress[review.CardId] = (nextBox, review.ReviewedAtUtc);
                    break;
                default:
                    throw Invalid();
            }
        }
        return changes;
    }

    private static SyncException Invalid() => new(
        "Guest migration requires new decks, their cards, and complete ordered review histories or unreviewed states. Existing account data cannot be replaced.",
        code: "invalid_guest_migration");
}
