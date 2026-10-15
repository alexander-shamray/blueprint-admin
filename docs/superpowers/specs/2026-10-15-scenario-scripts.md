# Scenario scripts: one cause per branch, and scripted state in the fakes

Design note for #67. It records which compensation branches the Scenario can
walk, the cause each one has, and the fixture format FakePlatform uses so that
every script runs against the fakes as it does against a stack. Facts have one
owner: the list of scripts is `SCRIPTS` in `scenario-run.ts`; the fake's
rules are `FakeOrders.cs`; the causes are paragraphs of the workspace's
`run-locally.md`.

## The rule

Three journeys walk one platform: the backend's
`Platform.IntegrationTests/Journey/CompensationJourney.cs`, the client's smoke,
and these scripts. They share **one cause per branch** or they are three
opinions. A script exists only for a branch whose cause is a paragraph of
`run-locally.md` that a developer follows by hand. A branch whose cause is not
there is not scripted here; it is filed against the document's owner first
(see *Not scripted*).

## Branches the Scenario walks

Enumerated from the backend's journey test, by what each `[Covers]` branch
needs, not from the plan's list.

| Branch (backend test) | Cause by hand | Script | Watch |
|---|---|---|---|
| Fulfilment, place to delivered (`FulfilmentJourney`) | none | `deliver` | `confirmed`, `dispatched`, `delivered` |
| Cancel before despatch (`Cancelling_…`) | `run-locally.md`, Cancel | `cancel` | `cancelled` |
| Reservation failed (`Stock_that_cannot_cover_the_order_…`) | stock set to 0 (`run-locally.md`, stock paragraph) | `out-of-stock` | `out_of_stock` |
| Payment declined (`A_declined_card_…`) | an amount ending `.01` (`run-locally.md`, "A declined payment is caused by the amount") | `declined` | `declined` |

One script walks no branch of the saga and exists for the store instead:

| What it proves | Cause by hand | Script | Watch |
|---|---|---|---|
| A product published with an image is stored and served (`run-locally.md`, "Attach a product image by hand") | none | `image` | the edge's `HEAD` of the thumbnail |

It is a script of its own and not two steps woven into the order scripts: the
store is not the saga, and an order script that failed on a broken store would
hide the saga regression it exists to find. It ends on the edge's answer being
an image with `nosniff`, because that is what a buyer's browser needs and what a
store the BFF could sign for but Catalog could not read would not give.

A script that applies a cause sets it with the paragraph's own call: the
publish's `amount`, the stock write's `onHand`. It never guesses a product: it
publishes its own, so the stock it sets is a count it chose and not a `null`
meaning "never reported".

## Not scripted

Each of these needs a cause `run-locally.md` does not yet carry, so the
console may not invent one:

- **Confirmation, stock, payment, despatch and release timeouts.** The backend
  journey fires them with `ExpireAsync`, a test hook. By hand there is no
  lever; the deadlines are minutes.
- **Carrier outcomes by delivery postal code** (`SIM-REFUSED`, `SIM-DOWN`, and
  the rest in the backend's `carrier-simulator/README.md`). `run-locally.md`
  names the carrier simulator's request log and not the postal codes.
- **Cancel after despatch, and refund of a confirmed order.** Both are by hand
  a cancel of an order Shipping has moved, whose rejection or refund is read
  from the order read and the payment; `run-locally.md` documents neither
  outcome, and the scripts need to say what a developer should see.

## The fixture format: scripted state in the fakes

A recording holds one instant; an order has to move. FakePlatform therefore
keeps a small amount of state in `FakeOrders`, written by hand:

- **Products.** `Publish(amount)` remembers the price of every product it
  publishes; `Stock(productId, onHand)` remembers the stock a write set. The
  first product is the recorded id, each later one a fresh id, as orders are.
- **Orders.** `Place(productId)` decides the order's end once, at the place:
  stock below one ends it `out_of_stock`, else a price whose minor units are
  `DeclinedMinorUnits` ends it `declined`. Either lands one `Step` after the
  place with nothing confirmed, despatched or delivered, and a cancel asked of
  it changes nothing. A product the fake never published takes the happy path.
- **The object store** (`FakeObjectStore`) keeps a key when a signed `PUT`
  reaches it and refuses one with no signature in the URL; it keeps any bytes
  and records whether they were an image. A publish with an `imageKey` is
  refused as the platform refuses it, with the 422 rule errors Catalog's
  `ProductErrors` names: not this seller's key, nothing uploaded under it,
  or not an image. The edge serves a key only once a publish attached it.
- **The gateway** reads the request bodies it needs (`amount`, `onHand`,
  `items[0].productId`, and for an image `imageKey`) and nothing else. The cause lives in the request, as
  it does on a stack, so the script's request is what scripts the fake.

Order of precedence matches the saga's: a reservation comes before a payment,
so a product with no stock ends out of stock whatever it costs.

A new branch adds a row to the table above, a rule to `FakeOrders.EndOf`, a
script in `SCRIPTS`, and a Playwright spec against FakePlatform. The status
names are the BFF's `CancelOutcomes`, which `LiteralDriftTests` reads.
