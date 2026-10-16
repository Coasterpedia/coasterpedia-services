---
title: Paging
description: How to get the rest of a list with a cursor.
---

# Paging

A list returns 25 records unless you ask for a different number with
`limit`, which can be anything from 1 to 100. If there are more records after
the ones you got, `meta.next_cursor` holds a cursor. Send it back as `cursor`,
with the rest of the query unchanged, and you get the next page. When
`next_cursor` is `null`, you've reached the end of the list.

```sh
curl -H "Authorization: Bearer $COASTERPEDIA_KEY" \
  "https://api.coasterpedia.net/v1/attractions?country=GB&limit=100"

curl -H "Authorization: Bearer $COASTERPEDIA_KEY" \
  "https://api.coasterpedia.net/v1/attractions?country=GB&limit=100&cursor=WyJuYW1lIiwiLi4uIl0"
```

If you change the query while you're paging, start again without a cursor.
If you change the sort, the API refuses the old cursor with `400
cursor-invalid`. If you change a filter, the API accepts the old cursor, but
the page starts partway through the new list, and you'll miss the records
that come before that point.

Pass the cursor back exactly as you got it. Its format isn't documented and
can change at any time, so don't build one yourself or change one.

A cursor marks a place in the sorted list, not a number of records. So if
records are added or removed while you're paging, you won't skip or repeat
any. The exception is a record whose own sort value changes while you page.
For example, if you're sorting by name and an attraction is renamed, it can
move to a page you've already read, or one you haven't reached yet.

`/v1/search` returns a single page, of up to `limit` results, with no cursor.

## Sorting

You can sort attractions with `sort=name` (the default), `opened`,
`updated`, `height`, `speed` or `length`. Put a `-` in front to reverse the
order, so `sort=-height` lists the tallest first.

If you sort by height, speed or length, the list leaves out attractions that
don't have that measurement. `sort=opened` leaves out attractions whose
opening date has no earliest day, such as "1989 or earlier". To see every
attraction, sort by name.

Parks, park companies, manufacturers and products sort by name, or by `-name`
for the reverse order.

## Keeping a copy

To keep your own copy up to date, page through each list once. After that,
follow [the change feed](/docs/changes/), which tells you about every change.

If you only need to catch up now and then, every list takes
`updated_since`. It returns only the records whose content changed at or
after the time you give. For attractions, add `sort=updated` to get them in
the order they changed.
