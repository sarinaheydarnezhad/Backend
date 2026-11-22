using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Cent.Module.Exams;

public static class CentExamIntegrity
{
    public static void Validate(ChangeTracker tracker)
    {
        tracker.DetectChanges();
        var mockIds = tracker.Entries<CentMockAttempt>().Select(e => e.Entity.AttemptId).ToHashSet();
        foreach (var entry in tracker.Entries().Where(e => e.State is EntityState.Modified or EntityState.Deleted)) {
            if (entry.Entity is CentSyllabusRevision or CentExamContentRelease or CentExamCommandReceipt or
                Curriculum.CentCurriculumRevision or Curriculum.CentPracticeContent)
                throw new InvalidOperationException("Published exam evidence and receipts are immutable; create another revision.");
            if (entry.Entity is CentExamVersion && entry.OriginalValues.GetValue<string>(nameof(CentExamVersion.Status)) != "DRAFT") {
                var version = (CentExamVersion)entry.Entity;
                if (version.Status is not ("PUBLISHED" or "ARCHIVED") ||
                    (version.Status == "ARCHIVED" && version.AvailableForNewStarts))
                    throw new InvalidOperationException("A published definition cannot become an editable draft or an active archive.");
                if (entry.State == EntityState.Deleted || entry.Properties.Any(p => p.IsModified && !Equals(p.CurrentValue, p.OriginalValue) && p.Metadata.Name is not
                    (nameof(CentExamVersion.Status) or nameof(CentExamVersion.AvailableForNewStarts) or nameof(CentExamVersion.Revision))))
                    throw new InvalidOperationException("Published exam definitions cannot be rewritten.");
            }
            if (entry.Entity is CentMockAttempt) {
                if (entry.State == EntityState.Deleted || entry.Properties.Any(p => p.IsModified && p.Metadata.Name is
                    nameof(CentMockAttempt.RulesSnapshotJson) or nameof(CentMockAttempt.ExamVersionId) or nameof(CentMockAttempt.ConfigurationRevision)))
                    throw new InvalidOperationException("Attempt rules snapshots are immutable.");
                if (entry.OriginalValues.GetValue<string?>(nameof(CentMockAttempt.ResultJson)) is not null && entry.Properties.Any(p => p.IsModified))
                    throw new InvalidOperationException("Final exam results cannot be rewritten.");
            }
            if (entry.Entity is CentAttemptSection && (entry.State == EntityState.Deleted || entry.Properties.Any(p => p.IsModified && !Equals(p.CurrentValue, p.OriginalValue) &&
                p.Metadata.Name is nameof(CentAttemptSection.SectionKey) or nameof(CentAttemptSection.SubjectId) or
                    nameof(CentAttemptSection.Order) or nameof(CentAttemptSection.QuestionCount) or nameof(CentAttemptSection.DurationSeconds))))
                throw new InvalidOperationException("Attempt section rules are immutable.");
            if (entry.Entity is CentAttemptQuestion q && mockIds.Contains(q.AttemptId) &&
                (entry.State == EntityState.Deleted || entry.Properties.Any(p => p.IsModified && !Equals(p.CurrentValue, p.OriginalValue) &&
                    p.Metadata.Name is not nameof(CentAttemptQuestion.Answer))))
                throw new InvalidOperationException("Assigned exam content and ordering are immutable.");
        }
    }
}
