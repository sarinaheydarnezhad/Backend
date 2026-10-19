using System.Text.Json;
using Flashcards.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Flashcards.Infrastructure.Backups;

public static class BackupSnapshot
{
    public const int CurrentFormatVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void ValidateVersion(int version)
    {
        if (version != CurrentFormatVersion)
            throw new BackupException(400, "backup_format_unsupported", "This backup format version is not supported.");
    }

    public static async Task<byte[]> CaptureAsync(FlashcardsDbContext database, Guid userId, Guid backupId,
        DateTimeOffset capturedAtUtc, long syncVersion, int maxBytes, CancellationToken cancellationToken)
    {
        using var stream = new LimitedMemoryStream(maxBytes);
        await using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteNumber("formatVersion", CurrentFormatVersion);
        writer.WriteString("backupId", backupId);
        writer.WriteString("userId", userId);
        writer.WriteString("capturedAtUtc", capturedAtUtc);
        writer.WriteNumber("syncVersion", syncVersion);
        await WriteRowsAsync(writer, "decks", database.Decks.AsNoTracking().Where(item => item.UserId == userId)
            .OrderBy(item => item.Id).Select(item => new
            {
                item.Id, item.Name, item.Description, item.Language, item.TextAlignment, item.TypographySize,
                item.CreatedAtUtc, item.UpdatedAtUtc, item.ArchivedAtUtc, item.SyncVersion
            }), cancellationToken);
        await WriteRowsAsync(writer, "cards", database.Cards.AsNoTracking().Where(item => item.Deck.UserId == userId)
            .OrderBy(item => item.Id).Select(item => new
            {
                item.Id, item.DeckId, item.FrontText, item.Phonetic, item.Category, item.Meaning, item.ExamplesJson,
                item.CreatedAtUtc, item.UpdatedAtUtc, item.ArchivedAtUtc, item.SyncVersion
            }), cancellationToken);
        await WriteRowsAsync(writer, "reviewStates", database.CardReviewStates.AsNoTracking()
            .Where(item => item.Card.Deck.UserId == userId).OrderBy(item => item.CardId).Select(item => new
            {
                item.CardId, item.Box, item.DueDate, item.LastReviewedAtUtc, item.ConsecutiveSuccesses,
                item.TotalReviews, item.TotalSuccesses, item.CreatedAtUtc, item.UpdatedAtUtc, item.SyncVersion
            }), cancellationToken);
        await WriteRowsAsync(writer, "reviewEvents", database.ReviewEvents.AsNoTracking()
            .Where(item => item.UserId == userId && item.Deck.UserId == userId && item.Card.Deck.UserId == userId)
            .OrderBy(item => item.Id).Select(item => new
            {
                item.Id, item.DeckId, item.CardId, item.PreviousBox, item.NewBox, item.Result, item.ReviewedAtUtc,
                item.StudySessionId, item.CreatedAtUtc
            }), cancellationToken);
        await WriteRowsAsync(writer, "settings", database.UserSettings.AsNoTracking().Where(item => item.UserId == userId)
            .Select(item => new
            {
                item.Theme, item.HapticsEnabled, item.Language, item.DailyReminderEnabled, item.DailyReminderTime,
                item.PreferredSpeechLanguage, item.PreferredSpeechAccent, item.CreatedAtUtc, item.UpdatedAtUtc, item.SyncVersion
            }), cancellationToken);
        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken);
        return stream.ToArray();
    }

    private static async Task WriteRowsAsync<T>(Utf8JsonWriter writer, string name, IQueryable<T> query, CancellationToken cancellationToken)
    {
        writer.WriteStartArray(name);
        await foreach (var row in query.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            JsonSerializer.Serialize(writer, row, JsonOptions);
            await writer.FlushAsync(cancellationToken);
        }
        writer.WriteEndArray();
        await writer.FlushAsync(cancellationToken);
    }

    private sealed class LimitedMemoryStream(int maxBytes) : MemoryStream
    {
        private void Check(int count)
        {
            if (Position + count > maxBytes)
                throw new BackupException(413, "backup_too_large", "The account backup exceeds the configured size limit.");
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Check(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Check(buffer.Length);
            base.Write(buffer);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }
    }
}
