using Microsoft.EntityFrameworkCore;

namespace Cent.Module;

public sealed class CentMobileIdentity
{
    public string Phone { get; set; } = "";
    public Guid UserId { get; set; }
    public DateTimeOffset VerifiedAt { get; set; }
}

public sealed class CentSmsChallenge
{
    public string IdHash { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Verifier { get; set; } = "";
    public Guid? LinkUserId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset ResendAt { get; set; }
    public int Attempts { get; set; }
    public bool Delivered { get; set; }
    public bool Consumed { get; set; }
}

// Durable budgets are shared by all Cent instances. Keys contain keyed hashes, never raw IPs.
public sealed class CentSmsBudget
{
    public string Key { get; set; } = "";
    public DateTimeOffset ResetAt { get; set; }
    public DateTimeOffset NextAt { get; set; }
    public int Count { get; set; }
}

internal static class CentSmsModel
{
    internal static void Configure(ModelBuilder b)
    {
        b.Entity<CentMobileIdentity>(e => {
            e.ToTable("cent_mobile_identities"); e.HasKey(x => x.Phone);
            e.Property(x => x.Phone).HasMaxLength(13).IsUnicode(false);
            e.HasIndex(x => x.UserId).IsUnique();
            e.HasOne<CentUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.NoAction);
        });
        b.Entity<CentSmsChallenge>(e => {
            e.ToTable("cent_sms_challenges"); e.HasKey(x => x.IdHash);
            e.Property(x => x.IdHash).HasMaxLength(64).IsUnicode(false);
            e.Property(x => x.Verifier).HasMaxLength(64).IsUnicode(false);
            e.Property(x => x.Phone).HasMaxLength(13).IsUnicode(false);
            e.HasIndex(x => x.Phone); e.HasIndex(x => x.ExpiresAt);
        });
        b.Entity<CentSmsBudget>(e => {
            e.ToTable("cent_sms_budgets"); e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(80).IsUnicode(false);
            e.HasIndex(x => x.ResetAt);
        });
    }
}
