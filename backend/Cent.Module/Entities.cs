namespace Cent.Module;

public sealed class CentUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string NormalizedEmail { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string? GoogleSubject { get; set; }
    public string Role { get; set; } = "STUDENT";
    public long PurchaseVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

// No raw state, nonce or PKCE verifier is persisted.
public sealed class CentGoogleAttempt
{
    public string StateHash { get; set; } = "";
    public string VerifierHash { get; set; } = "";
    public string NonceHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class CentSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class CentTraining
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IncludedInSubscription { get; set; } = true;
    public bool Published { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class CentDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TrainingId { get; set; }
    public Guid? SourceId { get; set; }
    public string Title { get; set; } = "";
    public int SortOrder { get; set; }
    public int PageCount { get; set; }
    public bool Published { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class CentDocumentPage
{
    public Guid DocumentId { get; set; }
    public int Number { get; set; }
    public byte[] Image { get; set; } = [];
    public int Width { get; set; }
    public int Height { get; set; }
}

public sealed class CentSection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public string Title { get; set; } = "";
    public int StartPage { get; set; }
}

public sealed class CentNote
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid DocumentId { get; set; }
    public int Page { get; set; }
    public double AnchorY { get; set; }
    public string Body { get; set; } = "";
    public long Revision { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class CentProgress
{
    public Guid UserId { get; set; }
    public Guid DocumentId { get; set; }
    public int LastPage { get; set; } = 1;
    public int FurthestPage { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class CentProduct
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Kind { get; set; } = "TRAINING";
    public Guid? TrainingId { get; set; }
    public long PriceRial { get; set; }
    public int? DurationDays { get; set; }
    public bool Active { get; set; }
}

public sealed class CentOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid ProductId { get; set; }
    public Guid RequestId { get; set; }
    public string Title { get; set; } = "";
    public string Kind { get; set; } = "";
    public Guid? TrainingId { get; set; }
    public int? DurationDays { get; set; }
    public long AmountRial { get; set; }
    public string Status { get; set; } = "PENDING";
    public string Provider { get; set; } = "ZARINPAL";
    public string? Authority { get; set; }
    public string? Reference { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PaidAt { get; set; }
}

public sealed class CentPaymentEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public string Action { get; set; } = "";
    public int Code { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class CentEntitlement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid OrderId { get; set; }
    public Guid? TrainingId { get; set; }
    public string Kind { get; set; } = "";
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class CentQuestion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Subject { get; set; } = "math";
    public Guid? TopicId { get; set; }
    public Guid? SourceSectionId { get; set; }
    public Guid? SourceExerciseId { get; set; }
    public string? ImportKey { get; set; }
    public string Origin { get; set; } = "manual";
    public string? FigureSvg { get; set; }
    public string Topic { get; set; } = "";
    public string Difficulty { get; set; } = "medium";
    public string Text { get; set; } = "";
    public string OptionsJson { get; set; } = "[]";
    public int CorrectAnswer { get; set; }
    public string Explanation { get; set; } = "";
    public string Kind { get; set; } = "both";
    public bool Active { get; set; }
}

public sealed class CentAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid RequestId { get; set; }
    public string Kind { get; set; } = "diagnostic";
    public string CourseCodesJson { get; set; } = "[]";
    public string AssessmentMode { get; set; } = "overview";
    public int DurationMinutes { get; set; } = 60;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SubmittedAt { get; set; }
    public string? ResultJson { get; set; }
}

// Immutable snapshots keep historic results correct after an administrator edits a question.
public sealed class CentAttemptQuestion
{
    public Guid AttemptId { get; set; }
    public int Position { get; set; }
    public Guid QuestionId { get; set; }
    public string SnapshotJson { get; set; } = "";
    public int? Answer { get; set; }
}

public sealed class CentAiAnalysis
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AttemptId { get; set; }
    public string Provider { get; set; } = "rules";
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class CentAuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ActorId { get; set; }
    public string Action { get; set; } = "";
    public Guid EntityId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
