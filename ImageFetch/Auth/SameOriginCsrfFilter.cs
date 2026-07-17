using CoasterpediaServices.Common.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Options;

namespace CoasterpediaServices.ImageFetch.Auth;

/// <summary>
/// CSRF hygiene for a same-origin cookie-authed POST endpoint: the userinfo gate is the real
/// authN boundary, this is defense-in-depth against a page tricking the browser into sending
/// the cookie cross-origin.
/// </summary>
public class SameOriginCsrfFilter : IActionFilter
{
    private readonly IOptions<CoasterpediaConfig> _coasterpediaConfig;
    private readonly ProblemDetailsFactory _problemDetailsFactory;

    public SameOriginCsrfFilter(IOptions<CoasterpediaConfig> coasterpediaConfig, ProblemDetailsFactory problemDetailsFactory)
    {
        _coasterpediaConfig = coasterpediaConfig;
        _problemDetailsFactory = problemDetailsFactory;
    }

    public void OnActionExecuting(ActionExecutingContext context)
    {
        var request = context.HttpContext.Request;

        if (request.Headers["Sec-Fetch-Site"] == "same-origin")
        {
            return;
        }

        var expectedOrigin = new Uri(_coasterpediaConfig.Value.BaseUrl).GetLeftPart(UriPartial.Authority).TrimEnd('/');
        var origin = request.Headers.Origin.ToString().TrimEnd('/');

        if (!string.IsNullOrEmpty(origin) && string.Equals(origin, expectedOrigin, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var problemDetails = _problemDetailsFactory.CreateProblemDetails(
            context.HttpContext,
            statusCode: StatusCodes.Status403Forbidden,
            detail: "Cross-origin requests are not allowed.");
        context.Result = new ObjectResult(problemDetails) { StatusCode = problemDetails.Status };
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }
}
