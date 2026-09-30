using Flashcards.Domain;
using Microsoft.EntityFrameworkCore;

namespace Flashcards.Infrastructure.Persistence;

public sealed class FlashcardsQueries(FlashcardsDbContext database)
{
    public Task<List<Deck>> GetUserDecksAsync(Guid userId, bool includeArchived = false, CancellationToken cancellationToken = default) =>
        database.Decks.AsNoTracking()
            .Where(deck => deck.UserId == userId && (includeArchived || deck.ArchivedAtUtc == null))
            .OrderBy(deck => deck.Name).ThenBy(deck => deck.Id).ToListAsync(cancellationToken);

    public Task<List<Card>> GetCardsByDeckAsync(Guid userId, Guid deckId, bool includeArchived = false, CancellationToken cancellationToken = default) =>
        database.Cards.AsNoTracking()
            .Where(card => card.DeckId == deckId && card.Deck.UserId == userId && (includeArchived || card.ArchivedAtUtc == null))
            .OrderBy(card => card.CreatedAtUtc).ThenBy(card => card.Id).ToListAsync(cancellationToken);

    public Task<CardReviewState?> GetReviewStateAsync(Guid userId, Guid cardId, CancellationToken cancellationToken = default) =>
        database.CardReviewStates.AsNoTracking()
            .SingleOrDefaultAsync(state => state.CardId == cardId && state.Card.Deck.UserId == userId, cancellationToken);

    public Task<List<ReviewEvent>> GetReviewHistoryAsync(Guid userId, Guid cardId, CancellationToken cancellationToken = default) =>
        database.ReviewEvents.AsNoTracking()
            .Where(review => review.UserId == userId && review.CardId == cardId)
            .OrderBy(review => review.ReviewedAtUtc).ThenBy(review => review.Id).ToListAsync(cancellationToken);

    public Task<UserSettings?> GetUserSettingsAsync(Guid userId, CancellationToken cancellationToken = default) =>
        database.UserSettings.AsNoTracking().SingleOrDefaultAsync(settings => settings.UserId == userId, cancellationToken);

    public async Task<UserChanges> GetChangesSinceAsync(Guid userId, DateTimeOffset sinceUtc, CancellationToken cancellationToken = default)
    {
        var user = await database.Users.AsNoTracking()
            .Where(item => item.Id == userId && item.UpdatedAtUtc > sinceUtc)
            .Select(item => new UserChange(item.Id, item.DisplayName, item.UpdatedAtUtc, item.ArchivedAtUtc))
            .SingleOrDefaultAsync(cancellationToken);
        var decks = await database.Decks.AsNoTracking()
            .Where(item => item.UserId == userId && item.UpdatedAtUtc > sinceUtc)
            .OrderBy(item => item.UpdatedAtUtc).ThenBy(item => item.Id).ToListAsync(cancellationToken);
        var cards = await database.Cards.AsNoTracking()
            .Where(item => item.Deck.UserId == userId && item.UpdatedAtUtc > sinceUtc)
            .OrderBy(item => item.UpdatedAtUtc).ThenBy(item => item.Id).ToListAsync(cancellationToken);
        var states = await database.CardReviewStates.AsNoTracking()
            .Where(item => item.Card.Deck.UserId == userId && item.UpdatedAtUtc > sinceUtc)
            .OrderBy(item => item.UpdatedAtUtc).ThenBy(item => item.CardId).ToListAsync(cancellationToken);
        var reviews = await database.ReviewEvents.AsNoTracking()
            .Where(item => item.UserId == userId && item.CreatedAtUtc > sinceUtc)
            .OrderBy(item => item.CreatedAtUtc).ThenBy(item => item.Id).ToListAsync(cancellationToken);
        var settings = await database.UserSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId && item.UpdatedAtUtc > sinceUtc, cancellationToken);
        return new UserChanges(user, decks, cards, states, reviews, settings);
    }
}

public sealed record UserChanges(
    UserChange? User,
    IReadOnlyList<Deck> Decks,
    IReadOnlyList<Card> Cards,
    IReadOnlyList<CardReviewState> ReviewStates,
    IReadOnlyList<ReviewEvent> ReviewEvents,
    UserSettings? Settings);

public sealed record UserChange(Guid Id, string DisplayName, DateTimeOffset UpdatedAtUtc, DateTimeOffset? ArchivedAtUtc);
