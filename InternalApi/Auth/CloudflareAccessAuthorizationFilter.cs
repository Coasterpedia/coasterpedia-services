using Hangfire.Dashboard;

namespace CoasterpediaServices.InternalApi.Auth;

public class CloudflareAccessAuthorizationFilter : IDashboardAuthorizationFilter
{
    private static readonly HashSet<string> AllowedEmails = new(StringComparer.OrdinalIgnoreCase)
    {
        "your@email.com"
    };

    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        var email = httpContext.Request.Headers["Cf-Access-Authenticated-User-Email"].ToString();
        return !string.IsNullOrEmpty(email) && AllowedEmails.Contains(email);
    }
}