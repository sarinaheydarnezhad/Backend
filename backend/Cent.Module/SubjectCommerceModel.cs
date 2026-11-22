using Microsoft.EntityFrameworkCore;

namespace Cent.Module;

// Companions extend the existing product/order/payment/grant identities. Legacy
// TRAINING and SUBSCRIPTION rows keep their original meaning.
public sealed class CentSubjectOffer
{
    public string SubjectId { get; set; } = "";
    public Guid ProductId { get; set; }
    public string ContentReleaseId { get; set; } = "";
    public string? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public bool Available { get; set; }
}
public sealed class CentSubjectQuote
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string PayloadJson { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public Guid? OrderId { get; set; }
}
public sealed class CentSubjectOrder
{
    public Guid OrderId { get; set; }
    public Guid QuoteId { get; set; }
    public string PayloadJson { get; set; } = "";
    public string InitiationState { get; set; } = "NOT_STARTED";
    public string? DevelopmentOutcome { get; set; }
}
public sealed class CentSubjectOrderLine
{
    public Guid OrderId { get; set; }
    public string SubjectId { get; set; } = "";
    public Guid ProductId { get; set; }
    public string ContentReleaseId { get; set; } = "";
    public long ListRial { get; set; }
    public long DiscountRial { get; set; }
    public long PayableRial { get; set; }
}
public sealed class CentSubjectAccess
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EntitlementId { get; set; }
    public Guid OrderId { get; set; }
    public Guid UserId { get; set; }
    public string SubjectId { get; set; } = "";
    public string ContentReleaseId { get; set; } = "";
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
public sealed class CentRecommendedBundle
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string SubjectsJson { get; set; } = "[]";
    public string Rationale { get; set; } = "";
    public string Evidence { get; set; } = "";
    public bool Active { get; set; }
}
public static class CentSubjectCommerceModel
{
    public static void Configure(ModelBuilder b)
    {
        b.Entity<CentSubjectOffer>(e => {
            e.ToTable("cent_subject_offers"); e.HasKey(x => x.SubjectId); e.Property(x => x.SubjectId).HasMaxLength(24);
            e.Property(x => x.ContentReleaseId).HasMaxLength(100); e.Property(x => x.ReviewedBy).HasMaxLength(256);
            e.HasIndex(x => x.ProductId).IsUnique();
            e.HasOne<CentCourse>().WithMany().HasForeignKey(x => x.SubjectId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<CentProduct>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.NoAction);
        });
        b.Entity<CentSubjectQuote>(e => {
            e.ToTable("cent_subject_quotes"); e.HasKey(x => x.Id); e.HasIndex(x => new { x.UserId, x.ExpiresAt });
            e.HasOne<CentUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<CentOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.NoAction);
        });
        b.Entity<CentSubjectOrder>(e => {
            e.ToTable("cent_subject_orders"); e.HasKey(x => x.OrderId); e.Property(x => x.InitiationState).HasMaxLength(24);
            e.Property(x => x.DevelopmentOutcome).HasMaxLength(16);
            e.HasIndex(x => x.QuoteId).IsUnique();
            e.HasOne<CentOrder>().WithOne().HasForeignKey<CentSubjectOrder>(x => x.OrderId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<CentSubjectQuote>().WithMany().HasForeignKey(x => x.QuoteId).OnDelete(DeleteBehavior.NoAction);
        });
        b.Entity<CentSubjectOrderLine>(e => {
            e.ToTable("cent_subject_order_lines", t => t.HasCheckConstraint("CK_cent_subject_line_amount", "[ListRial] > 0 AND [DiscountRial] >= 0 AND [PayableRial] = [ListRial] - [DiscountRial] AND [PayableRial] > 0"));
            e.HasKey(x => new { x.OrderId, x.SubjectId }); e.Property(x => x.SubjectId).HasMaxLength(24); e.Property(x => x.ContentReleaseId).HasMaxLength(100);
            e.HasOne<CentOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<CentProduct>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.NoAction);
        });
        b.Entity<CentSubjectAccess>(e => {
            e.ToTable("cent_subject_access", t => t.HasCheckConstraint("CK_cent_subject_term", "[ExpiresAt] > [StartsAt]")); e.HasKey(x => x.Id);
            e.Property(x => x.SubjectId).HasMaxLength(24); e.Property(x => x.ContentReleaseId).HasMaxLength(100);
            e.HasIndex(x => new { x.OrderId, x.SubjectId }).IsUnique(); e.HasIndex(x => new { x.UserId, x.SubjectId, x.ExpiresAt });
            e.HasOne<CentEntitlement>().WithMany().HasForeignKey(x => x.EntitlementId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<CentOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<CentUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<CentCourse>().WithMany().HasForeignKey(x => x.SubjectId).OnDelete(DeleteBehavior.NoAction);
        });
        b.Entity<CentRecommendedBundle>(e => {
            e.ToTable("cent_recommended_bundles"); e.HasKey(x => x.Id); e.Property(x => x.Id).HasMaxLength(100); e.Property(x => x.Title).HasMaxLength(256);
        });
    }
}
