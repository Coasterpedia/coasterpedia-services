using System.Text.Json;
using System.Text.Json.Nodes;
using CoasterpediaServices.Common;
using Microsoft.AspNetCore.Mvc;

namespace CoasterpediaServices.InternalApi.Events;

[ApiController]
[Route("events")]
public class EventsController : ControllerBase
{
    private readonly IEnumerable<IEventSubscriber> _subscribers;
    private readonly ILogger<EventsController> _logger;

    public EventsController(IEnumerable<IEventSubscriber> subscribers, ILogger<EventsController> logger)
    {
        _subscribers = subscribers;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Post()
    {
        try
        {
            var body = await JsonSerializer.DeserializeAsync<JsonNode>(Request.Body, cancellationToken: HttpContext.RequestAborted);
            if (body == null)
            {
                return Ok();
            }

            var events = body is JsonArray array ? array : [body];

            foreach (var eventBody in events)
            {
                var evt = EventBusEvent.FromJson(eventBody);
                if (evt == null)
                {
                    continue;
                }

                foreach (var subscriber in _subscribers)
                {
                    if (subscriber.Filter.Matches(evt))
                    {
                        _logger.LogInformation("Event matched {Subscriber} for {PageTitle}", subscriber.GetType().Name, evt.PageTitle);
                        subscriber.OnMatched(evt);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process EventBus payload");
        }

        return Ok();
    }
}
