---
title: Getting started
description: Get a key, make your first request, and read what comes back.
---

# Getting started

The Coasterpedia API gives you the wiki's records on attractions, parks, park
companies, manufacturers and products as JSON. You can read records through
it, but not change them.

## Get a key

Sign in at [Your keys](/account/keys) with your Coasterpedia account, then
create a key. Your wiki account needs a confirmed email address, because we
email you if the API changes or something goes wrong with one of your keys.

We show you the key once, when you create it, so copy it straight away. We
only keep its first eight characters. If you lose it, revoke it and create
another.

Free keys are for non-commercial projects. The [API terms](/terms/) explain
what counts as commercial.

## Make a request

Send the key in the `Authorization` header, and name your project in the
`User-Agent` header:

```sh
curl -H "Authorization: Bearer $COASTERPEDIA_KEY" \
  -H "User-Agent: RideCounter/1.2 (https://ridecounter.example; you@example.com)" \
  "https://api.coasterpedia.net/v1/attractions?sort=-height&limit=10"
```

Every request needs a `User-Agent`. Without one, Cloudflare refuses the
request with a `403` before it reaches the API. We suggest the project's
name and version, then a web address and an email address in brackets, as
above. If your requests start causing a problem, that tells us whose they
are and how to reach you before we limit anything.

Most HTTP libraries send a `User-Agent` of their own, but some don't. .NET's
`HttpClient` sends none unless you set one. Even where your library sends
one, set your own, because a library's default doesn't tell us who you are.

Keep the key on your server. If you put it in a web page or an app, anyone
who has the page or the app can read the key and use it.

You can call `/v1/meta` and `/v1/vocabularies` without a key. That's a quick
way to check the API is up before you sign in.

## Using the API from an app

If you're building a mobile or desktop app, don't put your key in the app.
Anyone who downloads the app can take the key out of it, and every copy of
the app would share your account's limit of 60 requests a minute.

Instead, have the app call your own server, and have your server call the
API. Your server can keep a copy of the data and follow
[the change feed](/docs/changes/) to keep it up to date, so your app's users
never wait on our limits. It can also combine our data with your own, such
as your users' ride counts or your rankings.

## What comes back

Each response has the records in `data` and information about the response
in `meta`:

```json
{
  "data": [
    { "id": 22784, "type": "coaster", "name": "…", "status": "…", "updated_at": "2026-10-13T12:28:06Z" }
  ],
  "meta": {
    "data_as_of": "2026-10-16T08:46:55Z",
    "next_cursor": "WyJoZWlnaHQiLDE2Ny42LDU2NTU3XQ",
    "view": "settled",
    "licence": { "name": "CC BY-SA 4.0", "url": "https://creativecommons.org/licenses/by-sa/4.0/", "attribution": "Coasterpedia contributors" }
  }
}
```

A few things work the same way everywhere in the API.

A record's `id` is the wiki's page ID, so it stays the same when the page is
renamed or moved. If two records are merged, a request for the old ID gets a
`301` redirect to the new one. The same happens for any page on the wiki
that redirects to a record, even one that was never in the API itself.
Filters that take an ID, such as `park=`, follow the merge for you.

If a field is missing, we don't know its value. A missing field and `null`
mean the same thing. For example, if a coaster has no `inversions` field,
don't assume it has no inversions.

Fields like `type` and `status` can gain new values at any time, without a
new version of the API, so your code should cope with a value it hasn't seen
before. [`/v1/vocabularies`](/docs/reference/#get-vocabularies) lists today's
values, with the labels the wiki uses for them.

Measurements are metric, with the unit beside the value. If the editor typed
an imperial figure, `entered` keeps what they typed:

```json
{ "value": 225.6, "unit": "m", "entered": { "value": 740, "unit": "ft" } }
```

Dates keep the precision the editor gave them. `edtf` is what they wrote,
such as `1996-05-04`, or `[..1989]` for "1989 or earlier". `earliest` and
`latest` are the first and last day the date could be, so you can sort and
filter by them without reading `edtf` at all.

## Next

- [Paging](/docs/paging/): how to get the rest of a list
- [Latest and settled](/docs/views/): the two versions of each record, and which to read
- [The change feed](/docs/changes/): how to keep your own copy up to date
- [Errors and limits](/docs/errors/): what the API sends when it refuses a request
- [Credit](/docs/attribution/): how to credit Coasterpedia when you show the data
- [Reference](/docs/reference/): every endpoint and field
