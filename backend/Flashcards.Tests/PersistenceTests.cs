using System.Text.Json;
using Flashcards.Domain;
using Flashcards.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Flashcards.Tests;

public sealed class PersistenceTests
{
    [Fact]
    public void Sql_server_model_maps_keys_constraints_and_restricts_history_deletion()
    {
        using var database = SqlServerContext();
        var model = database.Model;
        Assert.Equal(10, model.GetEntityTypes().Count());
        var state = model.FindEntityType(typeof(CardReviewState))!;
        Assert.Equal(nameof(CardReviewState.CardId), state.FindPrimaryKey()!.Properties.Single().Name);
        var settings = model.FindEntityType(typeof(UserSettings))!;
        Assert.Equal(nameof(UserSettings.UserId), settings.FindPrimaryKey()!.Properties.Single().Name);
        var history = model.FindEntityType(typeof(ReviewEvent))!;
        Assert.All(history.GetForeignKeys(), key => Assert.Equal(DeleteBehavior.NoAction, key.DeleteBehavior));
        Assert.Contains(history.GetIndexes(), index => index.Properties.Select(property => property.Name)
            .SequenceEqual([nameof(ReviewEvent.UserId), nameof(ReviewEvent.ReviewedAtUtc), nameof(ReviewEvent.Id)]));
        Assert.Contains(model.FindEntityType(typeof(Deck))!.GetKeys(), key => key.Properties.Select(property => property.Name)
            .SequenceEqual([nameof(Deck.UserId), nameof(Deck.Id)]));
        Assert.Equal("date", state.FindProperty(nameof(CardReviewState.DueDate))!.GetColumnType());
        var refreshToken = model.FindEntityType(typeof(RefreshToken))!;
        Assert.Contains(refreshToken.GetIndexes(), index => index.IsUnique && index.Properties.Single().Name == nameof(RefreshToken.TokenHash));
        Assert.All(refreshToken.GetForeignKeys(), key => Assert.Equal(DeleteBehavior.NoAction, key.DeleteBehavior));
        var syncChange = model.FindEntityType(typeof(SyncChange))!;
        Assert.Equal([nameof(SyncChange.UserId), nameof(SyncChange.Version)],
            syncChange.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.Contains(syncChange.GetIndexes(), index => index.IsUnique && index.Properties.Select(property => property.Name)
            .SequenceEqual([nameof(SyncChange.UserId), nameof(SyncChange.DeviceId), nameof(SyncChange.ClientChangeId)]));
        Assert.All(syncChange.GetForeignKeys(), key => Assert.Equal(DeleteBehavior.NoAction, key.DeleteBehavior));
    }

    [Fact]
    public void Initial_sql_server_migration_matches_the_model_and_protects_history()
    {
        using var database = SqlServerContext();
        Assert.False(database.Database.HasPendingModelChanges());
        Assert.Equal(3, database.Database.GetMigrations().Count());
        var script = database.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("CREATE TABLE [ReviewEvents]", script);
        Assert.Contains("EXEC(N'CREATE TRIGGER [TR_ReviewEvents_Immutable]", script);
        Assert.Contains("THROW 50001", script);
        Assert.Contains("CK_ReviewStates_Box", script);
        Assert.Contains("__EFMigrationsHistory", script);
        Assert.Contains("CREATE TABLE [AuthSessions]", script);
        Assert.Contains("CREATE TABLE [RefreshTokens]", script);
        Assert.Contains("CREATE TABLE [SyncChanges]", script);
        Assert.Contains("CREATE TABLE [SyncHeads]", script);
        Assert.Contains("INSERT INTO [SyncHeads]", script);
        Assert.Contains("EXEC(N'CREATE TRIGGER [TR_SyncChanges_Immutable]", script);
        Assert.Contains("THROW 50002", script);
        Assert.DoesNotContain("ON DELETE CASCADE", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Crud_relationships_and_async_queries_work_with_a_relational_database()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var database = SqliteContext(connection);
        await database.Database.EnsureCreatedAsync();

        var user = new User { DisplayName = "Student" };
        var other = new User { DisplayName = "Other" };
        var deck = new Deck { User = user, Name = "Vocabulary" };
        var card = new Card { Deck = deck, FrontText = "hola", Meaning = "hello" };
        var state = new CardReviewState { Card = card, DueDate = new DateOnly(2026, 10, 1) };
        var settings = new UserSettings { User = user };
        var review = new ReviewEvent
        {
            User = user, Deck = deck, Card = card, PreviousBox = 1, NewBox = 2,
            Result = "success", ReviewedAtUtc = DateTimeOffset.UtcNow
        };
        database.AddRange(user, other, deck, card, state, settings, review);
        await database.SaveChangesAsync();
        Assert.True(user.CreatedAtUtc > DateTimeOffset.MinValue);
        Assert.True(review.CreatedAtUtc > DateTimeOffset.MinValue);

        database.ChangeTracker.Clear();
        var queries = new FlashcardsQueries(database);
        Assert.Single(await queries.GetUserDecksAsync(user.Id));
        Assert.Empty(await queries.GetUserDecksAsync(other.Id));
        Assert.Equal(1, (await queries.GetReviewStateAsync(user.Id, card.Id))!.Box);
        Assert.Null(await queries.GetReviewStateAsync(other.Id, card.Id));
        Assert.Equal(card.Id, (await database.ReviewEvents.SingleAsync()).CardId);

        var savedCard = await database.Cards.SingleAsync();
        savedCard.Meaning = "greeting";
        await database.SaveChangesAsync();
        Assert.True(savedCard.UpdatedAtUtc >= savedCard.CreatedAtUtc);
        Assert.Equal("greeting", (await database.Cards.AsNoTracking().SingleAsync()).Meaning);

        var savedDeck = await database.Decks.SingleAsync();
        savedDeck.ArchivedAtUtc = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync();
        Assert.Empty(await queries.GetUserDecksAsync(user.Id));
        Assert.Single(await queries.GetUserDecksAsync(user.Id, includeArchived: true));

        database.ChangeTracker.Clear();
        database.Cards.Remove(new Card { Id = card.Id, DeckId = deck.Id });
        await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());
    }

    [Fact]
    public async Task Relational_constraints_reject_invalid_state_duplicate_settings_and_cross_deck_history()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var database = SqliteContext(connection);
        await database.Database.EnsureCreatedAsync();
        var user = new User { DisplayName = "Student" };
        var firstDeck = new Deck { User = user, Name = "First" };
        var secondDeck = new Deck { User = user, Name = "Second" };
        var card = new Card { Deck = firstDeck, FrontText = "one", Meaning = "one" };
        database.AddRange(user, firstDeck, secondDeck, card);
        await database.SaveChangesAsync();

        database.CardReviewStates.Add(new CardReviewState { CardId = card.Id, Box = 6, DueDate = DateOnly.FromDateTime(DateTime.UtcNow) });
        await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());
        database.ChangeTracker.Clear();

        database.ReviewEvents.Add(new ReviewEvent
        {
            UserId = user.Id, DeckId = secondDeck.Id, CardId = card.Id,
            PreviousBox = 1, NewBox = 2, Result = "success", ReviewedAtUtc = DateTimeOffset.UtcNow
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());
        database.ChangeTracker.Clear();

        database.UserSettings.Add(new UserSettings { UserId = user.Id });
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();
        database.UserSettings.Add(new UserSettings { UserId = user.Id });
        await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());
    }

    [Fact]
    public async Task Tracked_review_events_cannot_be_changed_or_deleted()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var database = SqliteContext(connection);
        await database.Database.EnsureCreatedAsync();
        var user = new User { DisplayName = "Student" };
        var deck = new Deck { User = user, Name = "Deck" };
        var card = new Card { Deck = deck, FrontText = "front", Meaning = "back" };
        var review = new ReviewEvent { User = user, Deck = deck, Card = card, Result = "success", PreviousBox = 1, NewBox = 2, ReviewedAtUtc = DateTimeOffset.UtcNow };
        database.Add(review);
        await database.SaveChangesAsync();
        review.Result = "failure";
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.SaveChangesAsync());
        database.Entry(review).State = EntityState.Deleted;
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.SaveChangesAsync());
    }

    private static FlashcardsDbContext SqlServerContext() => new(
        new DbContextOptionsBuilder<FlashcardsDbContext>()
            .UseSqlServer("Server=localhost;Database=FlashcardsModelTests;Trusted_Connection=True;TrustServerCertificate=True")
            .Options);

    private static SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.CreateFunction("ISJSON", (string value) =>
        {
            try { using var document = JsonDocument.Parse(value); return 1; }
            catch (JsonException) { return 0; }
        });
        return connection;
    }

    private static FlashcardsDbContext SqliteContext(SqliteConnection connection) => new(
        new DbContextOptionsBuilder<FlashcardsDbContext>().UseSqlite(connection).Options);
}
