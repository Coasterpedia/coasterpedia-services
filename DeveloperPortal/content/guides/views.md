---
title: Latest and settled
description: The two versions of each record, and which one to read.
---

# Latest and settled

Anyone can edit the wiki, so the API keeps two versions of each record, and
you choose which one you get with `view`.

`view=latest` gives you the wiki as it is now, a minute or two behind.

`view=settled` is the default. It holds back recent edits until they've had
time to be checked. An edit appears in the settled view once it has stood
for seven days without anyone changing it. Edits by administrators, bots and
editors the wiki trusts to skip review appear straight away. But if someone
else's edit to the same page is already waiting, a trusted edit made after
it waits the full seven days too.

Nobody approves the settled view by hand. It's the version of each page that
the wiki's patrollers have had a week to check. So if someone vandalises a
page and it's fixed within the week, you never see the vandalism.

```sh
curl -H "Authorization: Bearer $COASTERPEDIA_KEY" \
  "https://api.coasterpedia.net/v1/attractions/22784?view=latest"
```

Read the settled view unless you need an edit the moment it's made. Most
projects don't, because ride data changes slowly.

`meta.view` in each response tells you which view answered. `settle_after`
in `/v1/meta` gives the seven-day wait, in case we change it.

## How the two views differ

### A new page

When an editor who isn't trusted creates a page, the new record isn't in the
settled view until it has stood for seven days. Until then, lists in the
settled view leave it out. If you ask for it by ID, you get `404
not-settled`, and the error's `detail` gives the time it will appear if
nobody edits it first. Add `view=latest` to get it now.

### Finding out if a newer version is waiting

Each time someone saves an edit on the wiki, the page gets a new revision
number. When you fetch a single record, such as
`/v1/attractions/22784`, the response includes a `settlement` object with
three revision fields. They tell you whether the wiki has a newer version of
the record than the settled view does:

```json
"settlement": { "settled_revision": 489849, "pending_revision": null, "settles_at": null }
```

`settled_revision` is the revision the settled view holds. It's `null` if
the record isn't in the settled view yet.

`pending_revision` is a newer revision that hasn't settled yet. It's `null`
if there isn't one, as in the example above, where the settled view is up to
date.

`settles_at` is the time the pending revision will move into the settled
view, if nobody edits the page again before then. It's `null` when nothing
is pending.

You get the same `settlement` in both views. In the settled view, a
`pending_revision` tells you the wiki already shows something newer than
what you're reading. In the latest view, it tells you the version you're
reading is recent and hasn't stood for a week yet. Lists don't
include `settlement`; fetch the record to see it.

### When we add a field

When we add a field to the API, records get it in both views within a day,
with one exception. If a record has an edit waiting to settle at the time,
its settled version keeps the shape it had before, without the new field,
until that edit settles, up to seven days later. Treat the missing field
like any other missing field. We don't know its value yet.

### The change feed

The [change feed](/docs/changes/) has the same two views. The settled feed
lists a change once it has settled, so an edit that's undone within the week
never appears in it.

## Caching

We cache responses at each Cloudflare location for up to 30 seconds, so a
response can be up to 30 seconds older than the data behind it.
`meta.data_as_of` tells you when that data left the wiki.

We also answer from the copy of our database nearest to you, and a copy can
be a few seconds behind the newest one. If you make two requests in a row,
they can come from different copies, so `meta.data_as_of` can go back by a
few seconds between them. The change feed always reads the newest copy, so a
`next_cursor` it gives you always works on your next request.
