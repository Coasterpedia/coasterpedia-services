---
title: The change feed
description: How to keep your own copy up to date with /v1/changes.
---

# The change feed

`/v1/changes` lists every record that changed, in the order the changes
happened. If you keep your own copy of the data, you can follow it to stay up
to date instead of downloading everything again.

## Starting

First, page through the lists to build your copy, and note the time you
started. Then ask for every change since that time:

```sh
curl -H "Authorization: Bearer $COASTERPEDIA_KEY" \
  "https://api.coasterpedia.net/v1/changes?since=2026-10-16T00:00:00Z"
```

```json
{
  "data": [
    { "kind": "attraction", "id": 53854, "op": "upsert", "revision": 488862, "at": "2026-10-16T03:17:05Z" }
  ],
  "meta": { "next_cursor": "WyJjaGFuZ2VzIiwiMTVlOTA5…", "has_more": true, "delay": "PT0H", "…": "…" }
}
```

`since` is required. It takes a time, like the one above, or a cursor from a
previous response.

Every key gets a change as soon as it reaches the API, usually a minute or
two after the edit, whether the key is free or paid. `meta.delay` says how
far behind real time your feed runs, and it's `PT0H` for every key.

## Following

Each entry names a record. If `op` is `upsert`, fetch the record and replace
your copy of it. If `op` is `delete`, remove it. When a record was merged
into another, `merged_into` gives the other record's ID.

To fetch many attractions at once, pass up to 50 IDs to
`/v1/attractions?ids=`, separated by commas. You get the full records, as
`/v1/attractions/{id}` gives them, in one request. `/v1/parks?ids=` does the
same for parks.

Every response has a `meta.next_cursor`. Pass it as `since` on your next
call.

If `meta.has_more` is `true`, more changes are already waiting, so call again
straight away. A page can come back with fewer entries than `limit`, or none
at all, and still have `has_more` set, because each call only looks through
part of the log.

When `has_more` is `false`, your copy is up to date. Call again later with
the same cursor. Every few minutes is plenty.

To follow only some kinds of record, list them in `kinds`, separated by
commas, for example `kinds=attraction,park`. The kinds are `attraction`,
`park`, `park_company`, `manufacturer` and `product`.

You may sometimes get the same change twice. Applying it a second time does
no harm.

## How far back it goes

The feed keeps the last 90 days of changes (`changes_window` in `/v1/meta`).
If you call it at least once every 90 days, your copy never falls further
behind than the feed reaches, and you never need to rebuild it. Once a day is
plenty.

If your `since` is older than that, you get `410 cursor-expired`, because
your copy has missed changes the feed can no longer tell you about. You also
get it, rarely, if we've had to rebuild the log. Either way, rebuild your copy
from the lists, then follow the feed from the time you started the rebuild.

The feed has the same two views as the rest of the API. In the settled view,
a change appears only once it has settled, so an edit that's undone within
the week never shows up. See [Latest and settled](/docs/views/).
