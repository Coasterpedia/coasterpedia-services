# Coasterpedia API terms

Version 1, 16 October 2026. **Draft, not in force.**

These terms cover your use of the Coasterpedia API at `api.coasterpedia.net`
and the developer portal at `developer.coasterpedia.net`. You accept them
when you create a key, and we record which version you accepted.

Coasterpedia is a wiki about amusement rides, run by volunteers in the United
Kingdom. In these terms, "we" and "us" mean Coasterpedia. The wiki's own [Terms of use](https://coasterpedia.net/wiki/Coasterpedia:Terms_of_use)
still apply to your Coasterpedia account, and these terms add to them for the
API. Our [privacy policy](https://coasterpedia.net/wiki/Coasterpedia:Privacy_policy)
explains how we handle your data.

You can email us about anything on this page at developer@coasterpedia.net.

## The data and the service are separate

The data is free. Everything the API returns comes from the wiki and is
licensed under
[Creative Commons Attribution-ShareAlike 4.0](https://creativecommons.org/licenses/by-sa/4.0/)
(CC BY-SA 4.0). These terms don't change that licence, and nothing in them
limits what it lets you do with data you've received.

These terms cover access to our servers. They say who can have a key, how
many requests a key can make, and what we can do if someone misuses a key.

The non-commercial rule below only decides whether you can use a free key.
You, and anyone you pass the data to, can still reuse the data commercially
under the licence.

## Your account and your keys

You sign in to the portal with your Coasterpedia account. The account needs
a confirmed email address, and you can't sign in with an account that's
blocked on the wiki.

A key is a secret that identifies your account when you call the API. You
can create several, one for each project, and revoke any of them in the
portal.

- Keys belong to your account, and limits apply to the whole account, so ten
  keys have the same limit as one. Don't use more than one Coasterpedia
  account to get more.
- One account is one person. If several people work on a project, one of
  them holds its keys and is responsible for how they're used.
- Keep your keys secret. Call the API from your own server. Don't put a key
  in a web page, in code you publish, or in an app people can download,
  because anyone who has the page, the code or the app can take the key from
  it. If you're building an app, have the app call your own server, and have
  your server call the API. If a key leaks, revoke it and create another.
- You're responsible for anything anyone does with your keys, including
  people you give one to.

## The free tier is for non-commercial projects

**A project is commercial if it earns money, or if a business uses it in its
work.** Earning money includes charging for the project or any part of it,
showing adverts in it, and selling access to features. A project that earns
money is commercial even if it makes a loss. Taking donations is fine, as
long as donors get nothing extra.

Registered charities and museums can use a free key for their own projects.

Using the data to train or tune an AI model, or to build a dataset for one,
is commercial.

| Project | Free key? |
| --- | --- |
| A ride counter you use yourself, or share for free | Yes |
| A free fan site or app with no adverts and nothing to buy | Yes |
| The same site with a Ko-fi or Patreon link, where supporters get nothing extra | Yes |
| A school project, a student's thesis, or university research | Yes |
| A registered charity's or a museum's own project | Yes |
| A Patreon tier that unlocks features built on our data | No |
| A free app that shows adverts | No |
| A paid app, or one with in-app purchases, even if the part using our data is free | No |
| A tool used inside a park, manufacturer, travel company or consultancy, even if it's never sold | No |
| Training or tuning an AI model, or building a dataset to train one | No |

If you're not sure, ask us before you build on it. If a non-commercial
project is going to become commercial, tell us before it does. We won't
charge you for the time it was non-commercial.

There's no paid tier yet. Until there is, if your project is commercial,
email developer@coasterpedia.net and tell us what it is, and we'll agree how
you can use the API. To agree commercial use with us, you must be 18 or
over, or have a parent's or guardian's permission.

We trust what you tell us. If a free key is being used in a way that looks
like a business, we may ask about it.

## Credit Coasterpedia

The licence requires you to credit Coasterpedia wherever you show or share
the data. This line does that:

> Ride data from [Coasterpedia](https://coasterpedia.net), CC BY-SA 4.0

Put it where people see the data, on the screen or page that shows it. If the
data runs all through an app, the app's about screen will do. For a bot, a
command that shows the credit will do. If you changed the data, say so.

Share-alike applies to the data. If you publish a dataset that includes ours,
or one you made from it, you must license that dataset under CC BY-SA 4.0
too. Your own code, design and other content around it can use any licence
you like. This is a summary. Where it differs from the licence, the licence
applies.

## Limits

A free account can make 60 requests a minute, across all its keys. Every
response to a request with a key gives your limit in its `RateLimit-Policy`
header, and the [docs](https://developer.coasterpedia.net/docs/errors/)
always show the current limits.

The free tier is a fair-use service. Staying under 60 a minute doesn't by
itself make your use fair. If we think an account's use is unreasonable, we
can lower its limit or end its access, even if it never goes over.

If we lower the free limit for everyone, we'll email you at least 30 days
before, unless we have to act straight away to protect the service.

Paid plans, when they exist, will come with a guaranteed quota. We won't
lower a paid plan's quota during the plan, or end a paid account, because of
how much it uses inside that quota.

- Stay inside the limits. When you get a `429`, wait for the time in
  `Retry-After` before you try again.
- Don't fetch what you already have. Keep copies, and use `updated_since` or
  the change feed to find what's changed, rather than reading everything
  again.
- Don't try to get round the limits, for example with extra accounts or by
  spreading requests across other people's keys.
- Don't do anything that harms the service or other people's use of it, such
  as probing for weaknesses or flooding it with requests.

The API is how to read Coasterpedia's data automatically. A key doesn't
cover bulk reading of the wiki's pages or `api.php`, and we may block it.

## Changes to the API

We only add fields and endpoints to `/v1`, so code written for it keeps
working. If we need a change that would break it, we'll put the change in
`/v2`, and keep `/v1` working for at least six months after `/v2` arrives.

While the API is in beta, its version contains `beta`, such as
`1.0.0-beta.19`. Until then, we may change fields without notice. The
[changelog](https://developer.coasterpedia.net/changelog/) lists each change.

We can change, limit or stop the service, including the free tier. If we
stop the service or the free tier, we'll give you at least 30 days' notice by
email, unless something outside our control makes that impossible.

## When we can suspend or revoke a key

We can suspend or revoke a key, or every key on an account, if:

- the key has leaked, or someone is using it who wasn't given it;
- it's used for a commercial project on the free tier;
- on the free tier, its use is more than is fair, even inside the limit;
- it's used in a way that breaks these terms, the wiki's Terms of use, or the
  law;
- its use harms the service or other people's use of it;
- the account is blocked on the wiki. A block doesn't suspend your keys by
  itself, but we may suspend them because of it.

Where we can, we'll email you first and give you time to fix it. If we need
to stop something straight away, such as a leaked key or traffic that's
affecting the site, we'll act first and email you afterwards. If you think we
got it wrong, reply to that email.

You can stop using the API, and revoke your keys, at any time.

## How we contact you

We email the address on your Coasterpedia account, as it was when you last
signed in to the portal. We write about:

- changes that affect you, such as deprecations, lower limits and changes to
  these terms;
- problems with how one of your keys is being used;
- your showcase listings.

Keep that address working. If you change it, sign in to the portal again so
we have the new one. If we email that address, the notice counts as
delivered, even if you don't read it. We won't send you marketing.

## The showcase

You can ask us to list a project in the portal's
[showcase](https://developer.coasterpedia.net/showcase). We review every
listing before it appears, and we check that the credit is where you say it
is. We decide what the showcase includes, so we may decline a listing or
remove one. We'll tell you why in a line, but we don't owe you more detail.

We publish your listing and screenshot, and your username if you choose to
show it. Each listing also shows which kinds of record your key requested
over the last 30 days, such as attractions or parks, taken from the key's
traffic. The privacy policy has the details.

## No warranty

We provide the API and its data as they are. Volunteers write the data, and
it can be wrong, incomplete or out of date, as the wiki's disclaimer says.
The service can be slow, change, or be unavailable. We don't promise it's
suitable for any purpose. Don't rely on it for anything where a mistake could
hurt someone, such as safety information about a ride.

## Our liability

We aren't liable for any loss that comes from using the API or its data, or
from being unable to use them. That includes lost profits, lost data, and
losses from decisions made on the data.

If we're found liable anyway, the most we'll pay is £100 in total, or what
you paid us for the API in the 12 months before the claim, if that's more.

Nothing in these terms limits liability that the law doesn't allow to be
limited, such as for death or personal injury caused by negligence, or for
fraud.

## Changes to these terms

When we change these terms, we change the version number and date at the top
of this page.

- If a change is small, such as clearer wording or a fixed link, it takes
  effect when we publish it.
- If a change affects what you can do, or what you have to do, we'll email
  you at least 30 days before it takes effect. If you keep using your keys
  after that, you accept the new version. If you don't accept it, stop using
  the API and revoke your keys. The portal will ask you to accept the new
  version before you create another key.

## Law

These terms are governed by the law of England and Wales, and the courts of
England and Wales deal with any dispute. If you live in Scotland or Northern
Ireland, you can also bring a claim in your own courts.
