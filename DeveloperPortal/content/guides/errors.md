---
title: Errors and limits
description: What the API sends when it refuses a request, and how many requests you can make.
---

# Errors and limits

## Errors

When the API refuses a request, it sends a problem document (RFC 9457) as
`application/problem+json`:

```json
{
  "type": "https://api.coasterpedia.net/problems/parameter-invalid",
  "title": "limit is an integer from 1 to 100",
  "status": 400
}
```

Check `type` in your code. It stays the same, while `title` and `detail` are
written for people and we may reword them.

| Status | Type | What happened |
| --- | --- | --- |
| 400 | `parameter-invalid` | A parameter is out of range or in the wrong format. `title` says which one. |
| 400 | `parameter-unknown` | The endpoint doesn't take that parameter. Check the spelling. |
| 400 | `parameter-unavailable` | We haven't published that filter, sort or kind yet. `detail` lists what you can use. |
| 400 | `parameter-required` | A required parameter is missing, such as `since` on `/v1/changes`. |
| 400 | `cursor-invalid` | The cursor was changed, or comes from a query with a different sort. |
| 401 | `key-required` | The request had no key. |
| 401 | `key-unknown` | We don't recognise the key. Check you copied all of it. |
| 403 | `key-revoked` | The key was revoked. Create another in [Your keys](/account/keys). |
| 404 | `not-found` | There's no record with that ID. |
| 404 | `not-settled` | The record exists, but isn't in the settled view yet. `detail` says when it will be. |
| 405 | `method` | The API only answers `GET`, because it's read-only. |
| 410 | `cursor-expired` | Your change-feed position is older than the feed keeps. See [the change feed](/docs/changes/). |
| 429 | `rate-limited` | You made too many requests. Wait for the number of seconds in `Retry-After`. |

Two refusals come from Cloudflare before your request reaches the API, so
they aren't problem documents:

- A request with no `User-Agent` header gets a `403` with an HTML page.
  Name your project in `User-Agent`, as [Getting started](/docs/) shows.
- A burst from one IP address gets a `429` reading `error code: 1015`.
  "Limits", below, explains it.

If you send a parameter the API doesn't know, or a filter it doesn't offer
yet, it refuses the request. That way, a typo can't give you the wrong
records.

## Limits

A free account can make 60 requests a minute. Your limit covers your whole
account, so all your keys share it. Each
response to a request with a key says which limit applies:

```
RateLimit-Policy: "burst";q=60;w=60
```

That means 60 requests in any 60 seconds. If you go over, the API answers
`429` with a `Retry-After` header giving the seconds to wait. Answers we
serve from the cache count too.

The free tier is fair use. If we think an account's use is unreasonable, we
can lower its limit or end its access, even if it stays under 60 a minute.
The [terms](/terms/) say how.

We count requests separately at each Cloudflare location, so the limit
catches bursts rather than counting exactly. The API doesn't tell you how
many requests you have left.

Separately, Cloudflare turns away any single IP address that sends more than
50 requests in 10 seconds, for 10 seconds. That refusal is a plain-text
`429` reading `error code: 1015`, not a problem document, and it also has a
`Retry-After` header. If you stay within your key's limit, you won't reach
it.

To use fewer requests, ask for pages of 100 rather than many small pages,
follow [the change feed](/docs/changes/) rather than reading whole lists
again, and keep what you show to users for a while, since ride data changes
slowly.
