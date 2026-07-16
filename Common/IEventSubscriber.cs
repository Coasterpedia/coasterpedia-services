namespace CoasterpediaServices.Common;

// Implement one per bot. MediaWiki EventBus points at a single ingest endpoint;
// each registered subscriber independently decides whether an event is for it.
public interface IEventSubscriber
{
    EventFilter Filter { get; }

    void OnMatched(EventBusEvent evt);
}
