using System.Text.Json;
using Hangfire.Dashboard;

namespace CoasterpediaServices.InternalApi.Auth;

public class CloudflareAccessAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var email = context.GetHttpContext().Request.Headers["Cf-Access-Authenticated-User-Email"].ToString();
        return !string.IsNullOrEmpty(email);
    }
}