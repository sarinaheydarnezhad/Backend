using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace Cent.Module;

// Uses the existing Cent role column and re-resolved database session identity.
// ADMIN is the backwards-compatible SuperAdmin alias; no Lerona identities apply.
public static class CentAdminSecurity
{
    public static readonly string[] Roles = ["SuperAdmin", "UserManager", "ContentManager", "ExamManager", "FinanceManager"];
    public static readonly string[] Permissions = ["overview", "users", "content", "exams", "finance", "roles", "settings"];
    public static bool IsAdmin(string? role) => role == "ADMIN" || Roles.Contains(role);
    public static bool Allows(string? role, string permission) => permission is not null && Permissions.Contains(permission) &&
        (role is "ADMIN" or "SuperAdmin" || permission == "overview" && IsAdmin(role) || (role, permission) is
            ("UserManager", "users") or ("ContentManager", "content") or ("ExamManager", "exams") or ("FinanceManager", "finance"));
    public static void Policies(AuthorizationOptions options)
    {
        options.AddPolicy("CentAdmin", p => p.AddAuthenticationSchemes(CentModule.Scheme).RequireAuthenticatedUser()
            .RequireAssertion(c => IsAdmin(c.User.FindFirstValue(ClaimTypes.Role))));
        foreach (var permission in Permissions) {
            var name = permission;
            options.AddPolicy("CentAdmin:" + name, p => p.AddAuthenticationSchemes(CentModule.Scheme).RequireAuthenticatedUser()
                .RequireAssertion(c => Allows(c.User.FindFirstValue(ClaimTypes.Role), name)));
        }
    }
}
