using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CoasterpediaServices.Common.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Options;

namespace CoasterpediaServices.ImageFetch.Auth;

/// <summary>
/// The entire security boundary: forwards the inbound Cookie header to the wiki's own
/// meta=userinfo and only proceeds if the wiki vouches for a real logged-in user. Never
/// parses or trusts the cookie itself.
/// </summary>
public class WikiUserInfoGateFilter : IAsyncActionFilter
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<CoasterpediaConfig> _coasterpediaConfig;
    private readonly ProblemDetailsFactory _problemDetailsFactory;

    public WikiUserInfoGateFilter(
        IHttpClientFactory httpClientFactory,
        IOptions<CoasterpediaConfig> coasterpediaConfig,
        ProblemDetailsFactory problemDetailsFactory)
    {
        _httpClientFactory = httpClientFactory;
        _coasterpediaConfig = coasterpediaConfig;
        _problemDetailsFactory = problemDetailsFactory;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var httpContext = context.HttpContext;
        var cookie = httpContext.Request.Headers.Cookie.ToString();
        if (string.IsNullOrEmpty(cookie))
        {
            context.Result = NotLoggedIn(context);
            return;
        }

        var client = _httpClientFactory.CreateClient("WikiUserInfoGate");
        var baseUrl = _coasterpediaConfig.Value.BaseUrl.TrimEnd('/');
        var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/w/api.php?action=query&meta=userinfo&format=json");
        request.Headers.TryAddWithoutValidation("Cookie", cookie);

        UserInfoEnvelope? envelope;
        try
        {
            var response = await client.SendAsync(request, httpContext.RequestAborted);
            response.EnsureSuccessStatusCode();
            envelope = await response.Content.ReadFromJsonAsync<UserInfoEnvelope>(httpContext.RequestAborted);
        }
        catch (Exception)
        {
            var problemDetails = _problemDetailsFactory.CreateProblemDetails(
                httpContext,
                statusCode: StatusCodes.Status401Unauthorized,
                detail: "Could not verify login with the wiki.");
            context.Result = new ObjectResult(problemDetails) { StatusCode = problemDetails.Status };
            return;
        }

        if (envelope?.Query?.Userinfo is not { Id: not 0 })
        {
            context.Result = NotLoggedIn(context);
            return;
        }

        await next();
    }

    private ObjectResult NotLoggedIn(ActionExecutingContext context)
    {
        var problemDetails = _problemDetailsFactory.CreateProblemDetails(
            context.HttpContext,
            statusCode: StatusCodes.Status401Unauthorized,
            detail: "Not logged in.");
        return new ObjectResult(problemDetails) { StatusCode = problemDetails.Status };
    }

    private record UserInfoEnvelope(UserInfoQuery? Query);

    private record UserInfoQuery(UserInfoUser? Userinfo);

    private record UserInfoUser([property: JsonPropertyName("id")] int Id);
}
