using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flashcards.Domain;
using Flashcards.Infrastructure.Backups;
using Flashcards.Infrastructure.Persistence;
using Flashcards.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flashcards.Tests;

public sealed partial class BackupTests
{
    [Fact]
    public async Task Backup_download_restore_round_trip_preserves_all_learning_data_and_publishes_sync_changes()
    {
        await LoginAsync("roundtrip@example.com");
        var fixture = await SeedBackupAsync(2);
        var response = await _client.GetAsync($"/api/v1/backups/{fixture.BackupId}/download");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var original = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal(2, original["cards"]!.AsArray().Count);
        Assert.False((await _client.GetFromJsonAsync<BackupStatus>("/api/v1/backups/status"))!.RestoreSupported);
        var capabilities = (await _client.GetFromJsonAsync<BackupStatus>("/api/v1/backups/status?includeRestoreCapability=true"))!;
        Assert.True(capabilities.RestoreSupported);
        Assert.True(capabilities.LatestCompleted!.CanRestore);
        await EraseLearningAsync(fixture);
        Guid foreignId;
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            var foreign = new User { DisplayName = "Other account" };
            foreignId = foreign.Id;
            database.Add(new Deck { User = foreign, Name = "Other account deck" });
            database.Add(new Deck { UserId = fixture.UserId, Name = "Keep this deck", ArchivedAtUtc = _clock.Now });
            await database.SaveChangesAsync();
        }
        var restore = await RestoreAsync(fixture.BackupId);
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        var result = (await restore.Content.ReadFromJsonAsync<BackupRestoreResult>())!;
        Assert.Equal((1, 2, 2, 2, 1), (result.Decks, result.Cards, result.ReviewStates, result.ReviewEvents, result.Settings));
        Assert.Equal(8, result.Cursor);
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            Assert.Equal(2, await database.Decks.CountAsync(item => item.UserId == fixture.UserId));
            Assert.Equal("Other account deck", (await database.Decks.SingleAsync(item => item.UserId == foreignId)).Name);
            Assert.Equal(8, (await database.SyncHeads.SingleAsync(item => item.UserId == fixture.UserId)).Version);
            var payload = await BackupSnapshot.CaptureAsync(database, fixture.UserId, fixture.BackupId, _clock.Now, 8, 1048576, default);
            var restored = JsonNode.Parse(payload)!;
            foreach (var collection in new[] { "decks", "cards", "reviewStates", "reviewEvents", "settings" })
            {
                var expectedRows = original[collection]!.AsArray();
                var actualRows = restored[collection]!.AsArray();
                if (collection == "decks") actualRows.Remove(actualRows.Single(item => item!["id"]!.GetValue<Guid>() != fixture.DeckId));
                foreach (var row in expectedRows.Concat(actualRows))
                {
                    row!.AsObject().Remove("syncVersion");
                    row.AsObject().Remove("serverVersion");
                }
                Assert.True(JsonNode.DeepEquals(expectedRows, actualRows), collection);
            }
        }
        var pull = (await _client.GetFromJsonAsync<SyncPullResponse>("/api/v1/sync/pull?cursor=0&limit=100"))!;
        Assert.Equal(8, pull.Changes.Count);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6, 7, 8 }, pull.Changes.Select(item => item.ServerVersion));
        Assert.Equal(2, pull.Changes.Count(item => item.EntityType == "reviewState"));
        var replay = await RestoreAsync(fixture.BackupId, result.Cursor);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = (await replay.Content.ReadFromJsonAsync<BackupRestoreResult>())!;
        Assert.Equal((0, 0, 0, 0, 0), (replayed.Decks, replayed.Cards, replayed.ReviewStates, replayed.ReviewEvents, replayed.Settings));
        Assert.Equal(result.Cursor, replayed.Cursor);
    }

    [Fact]
    public async Task Reset_progress_and_immutable_history_are_both_preserved_by_backup_and_restore()
    {
        await LoginAsync("reset-progress@example.com");
        var fixture = await SeedBackupAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            var state = await database.CardReviewStates.SingleAsync();
            state.Box = 1;
            state.TotalReviews = 0;
            state.TotalSuccesses = 0;
            state.ConsecutiveSuccesses = 0;
            state.LastReviewedAtUtc = null;
            await database.SaveChangesAsync();
        }
        var response = await CreateAsync(Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var backup = (await response.Content.ReadFromJsonAsync<BackupMetadata>())!;
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/api/v1/backups/{backup.Id}/download")).StatusCode);
        await EraseLearningAsync(fixture);
        Assert.Equal(HttpStatusCode.OK, (await RestoreAsync(backup.Id)).StatusCode);
        using var inspect = _factory.Services.CreateScope();
        var stored = inspect.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        var restored = await stored.CardReviewStates.SingleAsync();
        Assert.Equal((1, 0, 0, 0), (restored.Box, restored.TotalReviews, restored.TotalSuccesses, restored.ConsecutiveSuccesses));
        Assert.Null(restored.LastReviewedAtUtc);
        Assert.Single(await stored.ReviewEvents.ToListAsync());
        var changes = await stored.SyncChanges.OrderBy(item => item.Version).ToListAsync();
        Assert.True(changes.Single(item => item.EntityType == "reviewState").Version >
            changes.Single(item => item.EntityType == "reviewEvent").Version);
    }

    [Fact]
    public async Task Restore_never_overwrites_existing_content_progress_settings_or_archive_state()
    {
        await LoginAsync("preserve@example.com");
        var fixture = await SeedBackupAsync();
        string before;
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            var deck = await database.Decks.SingleAsync();
            deck.Name = "Newer name";
            deck.ArchivedAtUtc = _clock.Now;
            (await database.Cards.SingleAsync()).Meaning = "Newer meaning";
            var progress = await database.CardReviewStates.SingleAsync();
            progress.Box = 4;
            progress.TotalReviews = 9;
            (await database.UserSettings.SingleAsync()).Theme = "light";
            await database.SaveChangesAsync();
            before = Encoding.UTF8.GetString(await BackupSnapshot.CaptureAsync(database, fixture.UserId, fixture.BackupId, _clock.Now, 0, 1048576, default));
        }
        var response = await RestoreAsync(fixture.BackupId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<BackupRestoreResult>())!;
        Assert.Equal((1, 1, 1), (result.SkippedDecks, result.SkippedCards, result.SkippedSettings));
        using var inspect = _factory.Services.CreateScope();
        var stored = inspect.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Equal(before, Encoding.UTF8.GetString(await BackupSnapshot.CaptureAsync(stored, fixture.UserId, fixture.BackupId, _clock.Now, 0, 1048576, default)));
        Assert.Empty(await stored.SyncChanges.ToListAsync());
    }

    [Fact]
    public async Task Missing_card_is_restored_under_existing_deck_without_changing_its_sibling()
    {
        await LoginAsync("missing-card@example.com");
        var fixture = await SeedBackupAsync(2);
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            await database.ReviewEvents.Where(item => item.CardId == fixture.CardIds[0]).ExecuteDeleteAsync();
            await database.CardReviewStates.Where(item => item.CardId == fixture.CardIds[0]).ExecuteDeleteAsync();
            await database.Cards.Where(item => item.Id == fixture.CardIds[0]).ExecuteDeleteAsync();
            await database.Cards.Where(item => item.Id == fixture.CardIds[1]).ExecuteUpdateAsync(update => update.SetProperty(item => item.Meaning, "Keep sibling"));
        }
        var response = await RestoreAsync(fixture.BackupId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<BackupRestoreResult>())!;
        Assert.Equal((0, 1, 1, 1, 0), (result.Decks, result.Cards, result.ReviewStates, result.ReviewEvents, result.Settings));
        using var inspect = _factory.Services.CreateScope();
        var stored = inspect.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Equal(2, await stored.Cards.CountAsync(item => item.DeckId == fixture.DeckId));
        Assert.Equal("Keep sibling", (await stored.Cards.SingleAsync(item => item.Id == fixture.CardIds[1])).Meaning);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("missing-array")]
    [InlineData("null-array")]
    [InlineData("null-row")]
    [InlineData("missing-field")]
    [InlineData("duplicate-deck")]
    [InlineData("duplicate-card")]
    [InlineData("duplicate-state")]
    [InlineData("duplicate-event")]
    [InlineData("bad-parent")]
    [InlineData("bad-event")]
    [InlineData("bad-state")]
    [InlineData("missing-state")]
    [InlineData("bad-settings")]
    [InlineData("bad-examples")]
    [InlineData("bad-date")]
    [InlineData("wrong-owner")]
    [InlineData("wrong-id")]
    [InlineData("wrong-version")]
    [InlineData("unsupported")]
    [InlineData("duplicate-property")]
    public async Task Invalid_snapshots_are_fully_rejected_before_restore_writes(string corruption)
    {
        await LoginAsync("invalid@example.com");
        var fixture = await SeedBackupAsync();
        var document = JsonNode.Parse(await _client.GetStringAsync($"/api/v1/backups/{fixture.BackupId}/download"))!;
        switch (corruption)
        {
            case "missing-array": document.AsObject().Remove("cards"); break;
            case "null-array": document["cards"] = null; break;
            case "null-row": document["cards"]![0] = null; break;
            case "missing-field": document["cards"]![0]!.AsObject().Remove("frontText"); break;
            case "duplicate-deck": document["decks"]!.AsArray().Add(document["decks"]![0]!.DeepClone()); break;
            case "duplicate-card": document["cards"]!.AsArray().Add(document["cards"]![0]!.DeepClone()); break;
            case "duplicate-state": document["reviewStates"]!.AsArray().Add(document["reviewStates"]![0]!.DeepClone()); break;
            case "duplicate-event": document["reviewEvents"]!.AsArray().Add(document["reviewEvents"]![0]!.DeepClone()); break;
            case "bad-parent": document["cards"]![0]!["deckId"] = Guid.NewGuid(); break;
            case "bad-event": document["reviewEvents"]![0]!["cardId"] = Guid.NewGuid(); break;
            case "bad-state": document["reviewStates"]![0]!["totalSuccesses"] = 99; break;
            case "missing-state": document["reviewStates"]!.AsArray().Clear(); break;
            case "bad-settings": document["settings"]![0]!["theme"] = "invalid"; break;
            case "bad-examples": document["cards"]![0]!["examplesJson"] = "{"; break;
            case "bad-date": document["cards"]![0]!["updatedAtUtc"] = "0001-01-01T00:00:00Z"; break;
            case "wrong-owner": document["userId"] = Guid.NewGuid(); break;
            case "wrong-id": document["backupId"] = Guid.NewGuid(); break;
            case "wrong-version": document["syncVersion"] = 100; break;
            case "unsupported": document["formatVersion"] = 99; break;
        }
        var json = corruption == "malformed" ? "{" : document.ToJsonString();
        if (corruption == "duplicate-property") json = json.Insert(1, "\"formatVersion\":1,");
        await RewritePayloadAsync(fixture, Encoding.UTF8.GetBytes(json));
        await EraseLearningAsync(fixture);
        Assert.Equal(HttpStatusCode.BadRequest, (await RestoreAsync(fixture.BackupId)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync($"/api/v1/backups/{fixture.BackupId}/download")).StatusCode);
        await AssertNothingRestoredAsync();
    }

    [Fact]
    public async Task Entirely_missing_progress_is_recovered_without_overwriting_the_existing_card()
    {
        await LoginAsync("missing-progress@example.com");
        var fixture = await SeedBackupAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            await database.ReviewEvents.ExecuteDeleteAsync();
            await database.CardReviewStates.ExecuteDeleteAsync();
            await database.Cards.ExecuteUpdateAsync(update => update.SetProperty(item => item.Meaning, "Keep current card"));
        }
        var response = await RestoreAsync(fixture.BackupId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<BackupRestoreResult>())!;
        Assert.Equal((0, 1, 1, 1), (result.Cards, result.SkippedCards, result.ReviewStates, result.ReviewEvents));
        using var inspect = _factory.Services.CreateScope();
        var stored = inspect.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Equal("Keep current card", (await stored.Cards.SingleAsync()).Meaning);
        Assert.Equal(1, (await stored.CardReviewStates.SingleAsync()).TotalReviews);
        Assert.Equal(fixture.CardIds[0], (await stored.ReviewEvents.SingleAsync()).CardId);
    }

    [Fact]
    public async Task Tampered_ciphertext_is_rejected_without_revealing_private_data()
    {
        await LoginAsync("tampered@example.com");
        var fixture = await SeedBackupAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            var backup = await database.UserBackups.SingleAsync();
            var payload = backup.Payload!.ToArray();
            payload[^1] ^= 1;
            backup.Payload = payload;
            await database.SaveChangesAsync();
        }
        await EraseLearningAsync(fixture);
        var response = await RestoreAsync(fixture.BackupId);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.DoesNotContain("Saved meaning", await response.Content.ReadAsStringAsync());
        await AssertNothingRestoredAsync();
    }

    [Fact]
    public async Task Failure_after_database_inserts_rolls_back_learning_data_cursor_and_feed_then_retry_succeeds()
    {
        await LoginAsync("rollback@example.com");
        var fixture = await SeedBackupAsync(2);
        await EraseLearningAsync(fixture);
        _failure.FailRestoreAfterSave = true;
        var response = await RestoreAsync(fixture.BackupId);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("sensitive restore marker", await response.Content.ReadAsStringAsync());
        await AssertNothingRestoredAsync();
        _failure.FailRestoreAfterSave = false;
        Assert.Equal(HttpStatusCode.OK, (await RestoreAsync(fixture.BackupId)).StatusCode);
        using var inspect = _factory.Services.CreateScope();
        var stored = inspect.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Equal(2, await stored.Cards.CountAsync());
        Assert.Equal("Completed", (await stored.UserBackups.SingleAsync()).Status);
    }

    [Fact]
    public async Task Large_backups_restore_with_batched_identity_checks_and_all_relationships()
    {
        await LoginAsync("large@example.com");
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            database.Subscriptions.Add(new Subscription
            {
                UserId = (await database.Users.SingleAsync()).Id, Plan = SubscriptionPlan.Plus, Status = SubscriptionStatus.Active,
                StartsAtUtc = _clock.Now.AddDays(-1), ExpiresAtUtc = _clock.Now.AddDays(30)
            });
            await database.SaveChangesAsync();
        }
        var fixture = await SeedBackupAsync(600);
        await EraseLearningAsync(fixture);
        var response = await RestoreAsync(fixture.BackupId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<BackupRestoreResult>())!;
        Assert.Equal((600, 600, 600), (result.Cards, result.ReviewStates, result.ReviewEvents));
        using var inspect = _factory.Services.CreateScope();
        var stored = inspect.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Equal(600, await stored.Cards.CountAsync(item => item.DeckId == fixture.DeckId && item.Deck.UserId == fixture.UserId));
        Assert.Equal(600, await stored.ReviewEvents.CountAsync(item => item.DeckId == item.Card.DeckId && item.UserId == fixture.UserId));
        Assert.Equal(1802, await stored.SyncChanges.CountAsync());
    }

    [Theory]
    [InlineData("deck")]
    [InlineData("card")]
    [InlineData("event")]
    public async Task Foreign_identifier_collisions_abort_restore_without_mixing_accounts(string entity)
    {
        await LoginAsync("collision@example.com");
        var fixture = await SeedBackupAsync();
        Guid eventId;
        using (var scope = _factory.Services.CreateScope())
            eventId = (await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().ReviewEvents.SingleAsync()).Id;
        await EraseLearningAsync(fixture);
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            var foreign = new User { DisplayName = "Foreign" };
            var deck = new Deck { User = foreign, Id = entity == "deck" ? fixture.DeckId : Guid.NewGuid(), Name = "Foreign" };
            var card = new Card { Deck = deck, Id = entity == "card" ? fixture.CardIds[0] : Guid.NewGuid(), FrontText = "Foreign", Meaning = "Foreign" };
            database.Add(card);
            if (entity == "event") database.Add(new ReviewEvent
            {
                Id = eventId, User = foreign, Deck = deck, Card = card, PreviousBox = 1, NewBox = 2,
                Result = "success", ReviewedAtUtc = _clock.Now
            });
            await database.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Conflict, (await RestoreAsync(fixture.BackupId)).StatusCode);
        using var inspect = _factory.Services.CreateScope();
        var stored = inspect.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.False(await stored.Decks.AnyAsync(item => item.UserId == fixture.UserId));
        Assert.Equal("Foreign", (await stored.Cards.SingleAsync()).FrontText);
        Assert.Empty(await stored.SyncChanges.ToListAsync());
    }

    [Fact]
    public async Task Format_one_without_event_versions_and_with_legacy_examples_remains_restorable()
    {
        await LoginAsync("legacy@example.com");
        var fixture = await SeedBackupAsync();
        var document = JsonNode.Parse(await _client.GetStringAsync($"/api/v1/backups/{fixture.BackupId}/download"))!;
        document["reviewEvents"]![0]!.AsObject().Remove("serverVersion");
        document["cards"]![0]!["examplesJson"] = "[\"legacy example\"]";
        await RewritePayloadAsync(fixture, Encoding.UTF8.GetBytes(document.ToJsonString()));
        await EraseLearningAsync(fixture);
        Assert.Equal(HttpStatusCode.OK, (await RestoreAsync(fixture.BackupId)).StatusCode);
        using var inspect = _factory.Services.CreateScope();
        Assert.Equal("[\"legacy example\"]", (await inspect.ServiceProvider.GetRequiredService<FlashcardsDbContext>().Cards.SingleAsync()).ExamplesJson);
    }

    [Fact]
    public async Task Restore_rejects_stale_versions_and_quota_overflow_without_partial_writes()
    {
        await LoginAsync("quota@example.com");
        var fixture = await SeedBackupAsync(51);
        await EraseLearningAsync(fixture);
        Assert.Equal(HttpStatusCode.Conflict, (await RestoreAsync(fixture.BackupId, 1)).StatusCode);
        var response = await RestoreAsync(fixture.BackupId);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("free_tier_card_limit", await response.Content.ReadAsStringAsync());
        await AssertNothingRestoredAsync();
    }

    [Fact]
    public async Task Expired_backups_cannot_be_downloaded_or_restored()
    {
        await LoginAsync("expired-restore@example.com");
        var fixture = await SeedBackupAsync();
        _clock.Now = _clock.Now.AddDays(7);
        Assert.Equal(HttpStatusCode.Gone, (await RestoreAsync(fixture.BackupId)).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await _client.GetAsync($"/api/v1/backups/{fixture.BackupId}/download")).StatusCode);
    }

    [Fact]
    public async Task Capability_opt_in_preserves_legacy_metadata_contracts()
    {
        await LoginAsync("capabilities@example.com");
        var created = await _client.PostAsJsonAsync("/api/v1/backups/?includeRestoreCapability=true", Request(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var metadata = (await created.Content.ReadFromJsonAsync<BackupMetadata>())!;
        Assert.True(metadata.CanRestore);
        Assert.False((await _client.GetFromJsonAsync<BackupMetadata>($"/api/v1/backups/{metadata.Id}"))!.CanRestore);
        Assert.True((await _client.GetFromJsonAsync<BackupMetadata>($"/api/v1/backups/{metadata.Id}?includeRestoreCapability=true"))!.CanRestore);
        Assert.False(Assert.Single((await _client.GetFromJsonAsync<BackupPage>("/api/v1/backups/"))!.Items).CanRestore);
        Assert.True(Assert.Single((await _client.GetFromJsonAsync<BackupPage>("/api/v1/backups/?includeRestoreCapability=true"))!.Items).CanRestore);
    }

    [Fact]
    public async Task Ciphertext_size_is_bounded_and_revoked_sessions_cannot_download_or_restore()
    {
        await LoginAsync("bounded@example.com");
        var fixture = await SeedBackupAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            var row = await database.UserBackups.SingleAsync();
            row.Payload = new byte[1048576 + 1025];
            row.SizeBytes = row.Payload.Length;
            await database.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await RestoreAsync(fixture.BackupId)).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await _client.GetAsync($"/api/v1/backups/{fixture.BackupId}/download")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsync("/api/v1/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RestoreAsync(fixture.BackupId)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync($"/api/v1/backups/{fixture.BackupId}/download")).StatusCode);
    }

    [Fact]
    public async Task Unknown_length_request_bodies_are_limited_and_non_json_requests_are_rejected()
    {
        await LoginAsync("chunked@example.com");
        var route = $"/api/v1/backups/{Guid.NewGuid()}/restore";
        using var content = new StreamContent(new NonSeekableBackupStream(Encoding.UTF8.GetBytes(new string(' ', 4097))));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        Assert.Null(content.Headers.ContentLength);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await _client.PostAsync(route, content)).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await _client.PostAsync(route, new StringContent("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge,
            (await _client.PostAsync("/api/v1/backups/", new StringContent(new string(' ', 4097), Encoding.UTF8, "application/json"))).StatusCode);
    }

    private sealed class NonSeekableBackupStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    [Fact]
    public async Task Snapshot_preserves_original_review_order_even_if_source_feed_is_lost()
    {
        await LoginAsync("review-order@example.com");
        var fixture = await SeedBackupAsync();
        Guid firstEventId;
        Guid secondEventId;
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            firstEventId = (await database.ReviewEvents.SingleAsync()).Id;
            var second = new ReviewEvent
            {
                UserId = fixture.UserId, DeckId = fixture.DeckId, CardId = fixture.CardIds[0], PreviousBox = 2, NewBox = 1,
                Result = "failure", ReviewedAtUtc = _clock.Now.AddHours(-1)
            };
            secondEventId = second.Id;
            database.ReviewEvents.Add(second);
            var state = await database.CardReviewStates.SingleAsync();
            state.Box = 1;
            state.TotalReviews = 2;
            state.ConsecutiveSuccesses = 0;
            foreach (var entry in new[] { (firstEventId, 1L), (secondEventId, 2L) })
                database.SyncChanges.Add(new SyncChange
                {
                    UserId = fixture.UserId, Version = entry.Item2, DeviceId = Guid.NewGuid(), ClientChangeId = Guid.NewGuid(),
                    EntityType = "reviewEvent", EntityId = entry.Item1, Operation = "create", ExpectedVersion = 0,
                    ClientChangedAtUtc = _clock.Now, ServerChangedAtUtc = _clock.Now, PayloadJson = "{}", Fingerprint = new string('A', 64)
                });
            await database.SaveChangesAsync();
            await database.SyncHeads.ExecuteUpdateAsync(update => update.SetProperty(item => item.Version, 2));
        }
        var response = await CreateAsync(Guid.NewGuid(), 2);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var backupId = (await response.Content.ReadFromJsonAsync<BackupMetadata>())!.Id;
        await EraseLearningAsync(fixture);
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>().SyncChanges.ExecuteDeleteAsync();
        Assert.Equal(HttpStatusCode.OK, (await RestoreAsync(backupId, 2)).StatusCode);
        using var inspect = _factory.Services.CreateScope();
        var stored = inspect.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        var events = await stored.SyncChanges.Where(item => item.EntityType == "reviewEvent").OrderBy(item => item.Version).ToListAsync();
        Assert.Equal(new[] { firstEventId, secondEventId }, events.Select(item => item.EntityId));
        Assert.Equal(1, (await stored.CardReviewStates.SingleAsync()).Box);
        var checkpoint = await stored.SyncChanges.SingleAsync(item => item.EntityType == "reviewState");
        Assert.True(checkpoint.Version > events[^1].Version);
    }

    [Fact]
    public async Task Cancellation_after_restore_inserts_rolls_back_before_commit()
    {
        await LoginAsync("cancel-restore@example.com");
        var fixture = await SeedBackupAsync();
        await EraseLearningAsync(fixture);
        using var cancellation = new CancellationTokenSource();
        _failure.OnRestoreSaved = cancellation.Cancel;
        using (var scope = _factory.Services.CreateScope())
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.ServiceProvider.GetRequiredService<BackupRestoreService>()
                .RestoreAsync(fixture.UserId, fixture.BackupId, new RestoreBackupRequest("mergeMissing", 0), cancellation.Token));
        _failure.OnRestoreSaved = null;
        await AssertNothingRestoredAsync();
    }

    [Theory]
    [InlineData("{}", 400)]
    [InlineData("{\"mode\":\"mergeMissing\"}", 400)]
    [InlineData("{\"mode\":\"replace\",\"expectedSyncVersion\":0}", 400)]
    [InlineData("{\"mode\":\"mergeMissing\",\"expectedSyncVersion\":-1}", 400)]
    [InlineData("null", 400)]
    [InlineData("oversized", 413)]
    public async Task Restore_requests_require_explicit_strategy_complete_json_and_bounded_bodies(string json, int status)
    {
        await LoginAsync("request@example.com");
        if (json == "oversized") json = new string(' ', 4097);
        var response = await _client.PostAsync($"/api/v1/backups/{Guid.NewGuid()}/restore", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        using var inspect = _factory.Services.CreateScope();
        Assert.Empty(await inspect.ServiceProvider.GetRequiredService<FlashcardsDbContext>().UserBackups.ToListAsync());
    }

    private Task<HttpResponseMessage> RestoreAsync(Guid backupId, long version = 0) =>
        _client.PostAsJsonAsync($"/api/v1/backups/{backupId}/restore", new RestoreBackupRequest("mergeMissing", version));

    [Fact]
    public async Task Creation_never_marks_an_inconsistent_learning_snapshot_completed()
    {
        await LoginAsync("inconsistent-source@example.com");
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            var userId = (await database.Users.SingleAsync()).Id;
            var deck = new Deck { UserId = userId, Name = "Preserve source" };
            var card = new Card { Deck = deck, FrontText = "Front", Meaning = "Meaning" };
            database.Add(new ReviewEvent
            {
                UserId = userId, Deck = deck, Card = card, PreviousBox = 1, NewBox = 2,
                Result = "success", ReviewedAtUtc = _clock.Now
            });
            await database.SaveChangesAsync();
        }
        var response = await CreateAsync(Guid.NewGuid());
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var inspect = _factory.Services.CreateScope();
        var stored = inspect.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        var backup = await stored.UserBackups.SingleAsync();
        Assert.Equal("Failed", backup.Status);
        Assert.Equal("backup_invalid", backup.FailureCode);
        Assert.Null(backup.Payload);
        Assert.Equal(1, await stored.Cards.CountAsync());
        Assert.Equal(1, await stored.ReviewEvents.CountAsync());
    }

    private sealed record BackupFixture(Guid BackupId, Guid UserId, Guid DeckId, Guid[] CardIds);

    private async Task<BackupFixture> SeedBackupAsync(int cardCount = 1)
    {
        Guid userId;
        var deck = new Deck { Name = "Saved deck", Description = "Saved description", Language = "fa", TextAlignment = "rtl", TypographySize = "large" };
        var cardIds = new List<Guid>();
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
            userId = (await database.Users.SingleAsync()).Id;
            deck.UserId = userId;
            database.Decks.Add(deck);
            for (var index = 0; index < cardCount; index++)
            {
                var card = new Card { Deck = deck, FrontText = $"واژه {index}", Meaning = "Saved meaning", Phonetic = "sound", Category = "noun", ExamplesJson = "[{\"sentence\":\"Example\"}]" };
                cardIds.Add(card.Id);
                database.AddRange(card, new CardReviewState
                {
                    Card = card, Box = 2, DueDate = new DateOnly(2026, 11, 5), LastReviewedAtUtc = _clock.Now,
                    TotalReviews = 1, TotalSuccesses = 1, ConsecutiveSuccesses = 1
                }, new ReviewEvent
                {
                    UserId = userId, Deck = deck, Card = card, PreviousBox = 1, NewBox = 2,
                    Result = "success", ReviewedAtUtc = _clock.Now, StudySessionId = Guid.NewGuid()
                });
            }
            database.UserSettings.Add(new UserSettings
            {
                UserId = userId, Theme = "dark", Language = "fa", HapticsEnabled = false, DailyReminderEnabled = true,
                DailyReminderTime = new TimeOnly(12, 30), PreferredSpeechLanguage = "en", PreferredSpeechAccent = "uk"
            });
            await database.SaveChangesAsync();
        }
        var response = await CreateAsync(Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return new BackupFixture((await response.Content.ReadFromJsonAsync<BackupMetadata>())!.Id, userId, deck.Id, cardIds.ToArray());
    }

    private async Task EraseLearningAsync(BackupFixture fixture)
    {
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        await database.ReviewEvents.Where(item => item.UserId == fixture.UserId).ExecuteDeleteAsync();
        await database.CardReviewStates.Where(item => item.Card.Deck.UserId == fixture.UserId).ExecuteDeleteAsync();
        await database.Cards.Where(item => item.Deck.UserId == fixture.UserId).ExecuteDeleteAsync();
        await database.Decks.Where(item => item.UserId == fixture.UserId).ExecuteDeleteAsync();
        await database.UserSettings.Where(item => item.UserId == fixture.UserId).ExecuteDeleteAsync();
    }

    private async Task RewritePayloadAsync(BackupFixture fixture, byte[] plaintext)
    {
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        var backup = await database.UserBackups.SingleAsync();
        backup.Payload = new BackupPayloadProtector(_protection).Protect(fixture.UserId, fixture.BackupId, 1, plaintext);
        backup.SizeBytes = backup.Payload.Length;
        await database.SaveChangesAsync();
    }

    private async Task AssertNothingRestoredAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<FlashcardsDbContext>();
        Assert.Empty(await database.Decks.ToListAsync());
        Assert.Empty(await database.Cards.ToListAsync());
        Assert.Empty(await database.CardReviewStates.ToListAsync());
        Assert.Empty(await database.ReviewEvents.ToListAsync());
        Assert.Empty(await database.UserSettings.ToListAsync());
        Assert.Empty(await database.SyncChanges.ToListAsync());
        Assert.Equal(0, (await database.SyncHeads.SingleAsync()).Version);
        Assert.Equal("Completed", (await database.UserBackups.SingleAsync()).Status);
    }
}
