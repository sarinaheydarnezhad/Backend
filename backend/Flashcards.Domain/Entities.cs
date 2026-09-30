namespace Flashcards.Domain;

public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? NormalizedEmail { get; set; }
    public string? PasswordHash { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? ArchivedAtUtc { get; set; }
    public ICollection<Deck> Decks { get; } = new List<Deck>();
    public UserSettings? Settings { get; set; }
    public ICollection<AuthSession> AuthSessions { get; } = new List<AuthSession>();
}

public sealed class AuthSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public ICollection<RefreshToken> RefreshTokens { get; } = new List<RefreshToken>();
}

public sealed class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AuthSessionId { get; set; }
    public AuthSession AuthSession { get; set; } = null!;
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? ConsumedAtUtc { get; set; }
}

public sealed class Deck
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Language { get; set; } = "en";
    public string TextAlignment { get; set; } = "ltr";
    public string TypographySize { get; set; } = "medium";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? ArchivedAtUtc { get; set; }
    public ICollection<Card> Cards { get; } = new List<Card>();
}

public sealed class Card
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeckId { get; set; }
    public Deck Deck { get; set; } = null!;
    public string FrontText { get; set; } = string.Empty;
    public string? Phonetic { get; set; }
    public string? Category { get; set; }
    public string Meaning { get; set; } = string.Empty;
    public string ExamplesJson { get; set; } = "[]";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? ArchivedAtUtc { get; set; }
    public CardReviewState? ReviewState { get; set; }
}

public sealed class CardReviewState
{
    public Guid CardId { get; set; }
    public Card Card { get; set; } = null!;
    public int Box { get; set; } = 1;
    public DateOnly DueDate { get; set; }
    public DateTimeOffset? LastReviewedAtUtc { get; set; }
    public int ConsecutiveSuccesses { get; set; }
    public int TotalReviews { get; set; }
    public int TotalSuccesses { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class ReviewEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public Guid CardId { get; set; }
    public Card Card { get; set; } = null!;
    public Guid DeckId { get; set; }
    public Deck Deck { get; set; } = null!;
    public int PreviousBox { get; set; }
    public int NewBox { get; set; }
    public string Result { get; set; } = string.Empty;
    public DateTimeOffset ReviewedAtUtc { get; set; }
    public Guid? StudySessionId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class UserSettings
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string Theme { get; set; } = "system";
    public bool HapticsEnabled { get; set; } = true;
    public string Language { get; set; } = "en";
    public bool DailyReminderEnabled { get; set; }
    public TimeOnly? DailyReminderTime { get; set; }
    public string PreferredSpeechLanguage { get; set; } = "en";
    public string? PreferredSpeechAccent { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
