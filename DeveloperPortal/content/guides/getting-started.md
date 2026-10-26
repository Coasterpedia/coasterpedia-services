---
title: Getting started
description: Get a key, make your first request, and read what comes back.
---

# Getting started

[Coasterpedia](https://coasterpedia.net) is a wiki about amusement rides
that anyone can edit. Volunteers have written it since 2009. It covers rides
around the world, whether they're open, closed, demolished or not built yet,
including travelling fairground rides.

The Coasterpedia API gives you the wiki's records as JSON. You can read
records through it, but not change them. To correct a record, edit its
article on the wiki. The API shows the edit once it has stood for a week, or
within a couple of minutes if you ask for the [latest](/docs/views/) version.

## What the records are

- Attractions are roller coasters, other rides such as flat rides and dark
  rides, and water slides. `type` says which.
- Parks are theme parks, water parks and the other places rides stand.
- Park companies own or operate parks.
- Manufacturers are the companies that build rides.
- Products are the ride models a manufacturer sells, such as Vekoma's
  Boomerang. A product lists every attraction built as one.

Each record is one article on the wiki, so you can open the article to read
more or see who edited it.

## Get a key

Sign in at [Your keys](/account/keys) with a Coasterpedia account, then
create a key. If you don't have an account,
[create one on the wiki](https://coasterpedia.net/wiki/Special:CreateAccount).
It's free. Your account needs a confirmed email address, because we email
you if the API changes or something goes wrong with one of your keys.

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

## Fetching a lot of records

If you need many full records, ask for up to 50 in each request instead of
one. Your account can make 60 requests a minute, so fetching 1,200 parks one
at a time takes about 20 minutes. Fetching them 50 at a time takes 24
requests, and less than a minute.

To build your own copy of the data:

1. Page through the list, for example `/v1/parks?limit=100`, to collect the
   IDs. The list gives a short summary of each record, up to 100 a page.
   [Paging](/docs/paging/) explains how to get each next page.
2. Pass the IDs to `/v1/parks?ids=`, up to 50 at a time, separated by
   commas. You get the full records, as `/v1/parks/{id}` gives them.
3. Follow [the change feed](/docs/changes/) to keep your copy up to date, so
   you never need to fetch everything again.

```sh
curl -H "Authorization: Bearer $COASTERPEDIA_KEY" \
  -H "User-Agent: RideCounter/1.2 (https://ridecounter.example; you@example.com)" \
  "https://api.coasterpedia.net/v1/parks?ids=182,2156,99001"
```

`ids=` works on `/v1/attractions` and `/v1/parks`. The only parameter you can
add to it is `view`, because a filter or a sort would have nothing to choose
from.

You get each record once, in the order you asked for them. If an ID was
merged into another record, you get that record instead, and `meta.merged`
gives you its ID. If an ID has no record, it's listed in `meta.missing`. If
its record hasn't settled yet, it's listed in `meta.not_settled`, and you can
ask again with `view=latest` to get it now. None of these make the request
fail, so check them rather than the status code.

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
