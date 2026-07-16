using System.Text.Json;
using System.Text.Json.Nodes;
using CoasterpediaServices.Common;

namespace CoasterpediaServices.InternalApi.Events;

public static class EventIngestEndpoint
{
    public static void MapEventIngest(this WebApplication app)
    {
        app.MapPost("/events", async (HttpRequest request, IEnumerable<IEventSubscriber> subscribers, ILogger<Program> logger) =>
        {
            try
            {
                var body = await JsonSerializer.DeserializeAsync<JsonArray>(request.Body, cancellationToken: request.HttpContext.RequestAborted);
                if (body == null)
                {
                    return Results.Ok();
                }

                foreach (var eventBody in body)
                {
                    var evt = EventBusEvent.FromJson(eventBody);
                    if (evt == null)
                    {
                        continue;
                    }

                    foreach (var subscriber in subscribers)
                    {
                        if (subscriber.Filter.Matches(evt))
                        {
                            logger.LogInformation("Event matched {Subscriber} for {PageTitle}", subscriber.GetType().Name, evt.PageTitle);
                            subscriber.OnMatched(evt);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process EventBus payload");
            }

            return Results.Ok();
        });
    }
}
