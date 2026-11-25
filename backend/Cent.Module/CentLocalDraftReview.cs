using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Cent.Module;

// Explicit local acceptance only. Production ignores these settings even if copied.
// Only the two additive reviewed documents qualify; this cannot open arbitrary drafts.
public static class CentLocalDraftReview
{
    public static Guid[] Documents(Guid user, IConfiguration? configuration, IHostEnvironment? environment)
    {
        if (environment?.IsDevelopment() != true || configuration?.GetValue<bool>("Cent:Reader:LocalReview:Enabled") != true ||
            !Guid.TryParse(configuration["Cent:Reader:LocalReview:UserId"], out var owner) || owner != user)
            return [];
        return CentReviewedContent.Content.Changes.Where(c => c.Entity == "documents" && c.Before is null).Select(c => c.Id).ToArray();
    }
}
