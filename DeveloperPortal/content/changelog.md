---
title: Changelog
description: What each version of the API added.
---

# Changelog

This page lists every change to the Coasterpedia API, newest first. It
follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and
`/v1/meta` gives the version you're talking to as `schema_version`.

We only add fields and endpoints to `/v1`, so code written for an older
version keeps working. The [terms](/terms/) let us change a field without
notice while the API is in beta. We haven't needed to yet. If we do, the
entry lists it under "Changed" and says what to do. A missing field means we
don't know the value.

When we add a field, a record with an edit waiting to settle keeps its old
shape in the settled view until that edit settles, up to seven days later.
[Latest and settled](/docs/views/) explains why.

## 1.0.0-beta.22 - 2026-10-19

### Added

- `videos` on an attraction: the YouTube video IDs the article lists, at
  most three. Watch one at `https://www.youtube.com/watch?v=` followed by
  the ID. The videos belong to whoever uploaded them, so the data licence
  doesn't cover them.
- `ids` on `/v1/attractions`: up to 50 attraction IDs, separated by commas.
  You get their full records in one request, with `meta.merged`,
  `meta.missing` and `meta.not_settled` saying what happened to each ID you
  didn't get as asked. It takes `view` and no other parameter, and needs an
  API key.

### Changed

- An ID of any page that redirects to an attraction, park, manufacturer,
  product or park company now answers `301` to that record. Before, a page
  that someone redirected by hand answered `404` unless the API had held it
  as a record when it became a redirect. These redirects don't appear in
  `/v1/changes`, because most were never records. If you hold an ID that
  answered `404`, ask again.

## 1.0.0-beta.21 - 2026-10-18

### Changed

- `introduced` and `discontinued` on a product, and on each of its
  `variants`, can now be an uncertain date, such as `[..1989]` (1989 or
  earlier) or `198X` (the 1980s). Before, they were always exact, and a
  product whose date wasn't exact had none. About 40 products now have a
  date they didn't. To filter or sort by these dates, read `earliest` and
  `latest` rather than parsing `edtf`. Either can be null at an open end.

## 1.0.0-beta.20 - 2026-10-16

### Added

- `history.names` on a park: the names it has had, oldest first, with their
  dates.
- `/v1/search` and `q` on `/v1/parks` find a park by a past name.

### Changed

- `products` on a water slide gives each product its own `variant`. Before,
  a slide built from two or more products gave no variant for any of them.

## 1.0.0-beta.19 - 2026-10-13

### Added

- `/v1/attractions/facets` counts by `manufacturer` and `product`, and by
  `variant` when you filter by a product.

## 1.0.0-beta.18 - 2026-10-13

### Added

- `resort.parks` on `/v1/parks/{id}`: every park in the resort, this one
  included, in the resort's own order.

## 1.0.0-beta.17 - 2026-10-13

### Added

- `/v1/search`: find any record by name.
- `q` on every list and on `/v1/attractions/facets`. Each word you give
  must match the start of a word in the name, ignoring case and accents.
  One-letter words are ignored. Attractions are also found by their past
  names.
- `updated_since` and `sort=updated`, to read only the records that changed.
- `bbox`, for records inside a box on the map.
- `include=former` and `include=all` on `/v1/parks/{id}/attractions`, for
  the attractions a park used to have.

## 1.0.0-beta.16 - 2026-10-13

### Added

- `builders`, `replaced`, `replaced_by` and `coaster.train_manufacturers`
  on an attraction.
- `predecessors` and `successors` on a manufacturer and a park company.
- `owners` and `operators` on a park: the companies in charge now.
- `designers` on an attraction and a product, by name only.
- `categories` on an attraction, as the wiki writes them.
- `offered.types` and `offered.lift_launch` on a product.

Each of these can name several companies or records, so each arrives as a
list, even when it holds one.

## 1.0.0-beta.15 - 2026-10-13

### Added

- `history.names` on an attraction: the names it has had, oldest first,
  with `location_index` saying where it stood when it had each one.

## 1.0.0-beta.14 - 2026-10-13

### Added

- `/v1/park-companies` and `/v1/park-companies/{id}`.
- `/v1/park-companies/{id}/parks`: every park the company has owned or
  operated.
- `history.owners` and `history.operators` on a park, oldest first, with
  `current` marking the companies in charge now.

## 1.0.0-beta.13 - 2026-10-13

### Added

- `name_is_placeholder` on an attraction: true when its name is a stand-in
  until the real one is known.

## 1.0.0-beta.12 - 2026-10-13

### Added

- `location` on an attraction: its `kind`, `on_loan`, `park`, `owner`,
  `place` and `state`.
- `history.locations` on an attraction: the places it stood before.
- `dates` on an attraction: `opened`, `closed`, `opening` and `closing`,
  each with the earliest and latest day it can be.
- `restraints`, `restrictions`, and the `coaster`, `ride` and `water_slide`
  details.
- `/v1/parks/{id}/attractions`.
- `park`, `location_kind`, `state`, `opened_from` and `opened_to` filters
  on `/v1/attractions`, and `sort=opened`.

## 1.0.0-beta.11 - 2026-10-12

### Added

- `products` on an attraction: the products it's built as.
- `/v1/products/{id}/installations`, and `installations` on a product.
- `product` and `variant` filters on `/v1/attractions` and its facets.

## 1.0.0-beta.10 - 2026-10-12

### Added

- `/v1/products` and `/v1/products/{id}`.
- `/v1/manufacturers/{id}/products`.

## 1.0.0-beta.9 - 2026-10-12

### Added

- `manufacturer` filter on `/v1/attractions` and its facets.

## 1.0.0-beta.8 - 2026-10-12

### Added

- `/v1/manufacturers` and `/v1/manufacturers/{id}`, with `status`,
  `founded`, `headquarters`, `country` and `website`.

## 1.0.0-beta.7 - 2026-10-12

### Added

- `/v1/parks` and `/v1/parks/{id}`, with `status`, `dates`, `place`,
  `state`, `country`, `coordinates`, `website` and `resort`.

## 1.0.0-beta.6 - 2026-10-12

### Added

- `/v1/attractions/facets`: how many attractions match each filter value,
  and histograms of heights, speeds and lengths.

## 1.0.0-beta.5 - 2026-10-12

### Added

- We cache responses for up to 30 seconds, and the `Age` header says how
  long.
- A response to a request with a key carries `RateLimit-Policy`, and a 429
  carries `Retry-After`.

## 1.0.0-beta.4 - 2026-10-11

### Added

- `/v1/changes`: every change to every record, in order.
- `/v1/vocabularies`: the values each open list can hold, with labels.

## 1.0.0-beta.3 - 2026-10-11

### Added

- `stats` on an attraction: height, drop, length, speed, inversions and
  more.
- `sort=height`, `sort=speed` and `sort=length` on `/v1/attractions`.

## 1.0.0-beta.2 - 2026-10-11

### Added

- The settled view, and `settlement` on a record.
- `view` is optional and defaults to `settled`. In beta.1 you had to send
  `view=latest`, and requests that send it still get the latest view.

## 1.0.0-beta.1 - 2026-10-11

### Added

- `/v1/attractions`, `/v1/attractions/{id}` and `/v1/meta`.
- `name`, `type`, `status`, `location.country`, `location.coordinates` and
  `manufacturers` on an attraction.
- `type`, `status` and `country` filters, and `sort=name`.
