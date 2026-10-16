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
| Parcel collected, never delivered (the carrier simulator's `SIM-TRANSIT`, which no journey test walks) | the delivery postal code `SIM-TRANSIT` (`run-locally.md`, the carrier table) | `in-transit` | `confirmed`, `dispatched` |
| Cancel after despatch (`A_shipped_order_cannot_be_cancelled_and_nothing_moves`) | an order to `SIM-TRANSIT`, cancelled once it reads `dispatched` (`run-locally.md`, "After despatch there is nothing to compensate") | `already-shipped` | the cancel's 422 `order.already_shipped`, then the order read `unchanged` |

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

- **Stock, release and payment timeouts.** `run-locally.md` now gives the by-hand
  cause: stop Inventory and wait out the saga's own delay, five, ten or fifteen
  minutes. A script would have the console stop a service the platform needs
  and hold a run open that long, which is a decision about the Stack screen's
  powers (spec §2.1) before it is a script; it is not made here.
- **Confirmation and despatch timeouts.** `run-locally.md` says neither is
  reachable by hand: the backend journey fires them with `ExpireAsync`, a test
  hook with no counterpart a developer can run.
- **The carrier outcomes the order read cannot tell apart** (`SIM-REFUSED`,
  `SIM-DOWN`, `SIM-SLOW`, `SIM-STRANGE`). `run-locally.md` now gives each its
  postal code, and says where each ends: the shipment is `Unfulfillable`,
  `Pending` or `Booked`, which only the shipment table shows, and the order
  read says `confirmed` for all four, as it does a healthy order a moment
  before its parcel is booked. The console reads no database, so a script for
  one would assert an absence it cannot see. `SIM-TRANSIT` is scripted
  because the order read does tell it apart, by staying `dispatched`; `SIM-REVERSED`
  and `SIM-LATE` end `delivered`, like the happy path.
- **Refund of a confirmed order.** `run-locally.md` says it is the void of an
  authorisation by a cancel before despatch and nothing else: there is no refund
  after delivery to produce, and the order read's `refunded` is that void. The
  `cancel` script already walks the cause, and the payment's `refund` is read
  from the payment, which the Scenario does not read.

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
- **The carrier.** `Place(productId, postalCode)` marks an order to
  `SIM-TRANSIT` as collected and never delivered: it reads `dispatched` from two
  steps on and its `delivered` stays null. A cancel asked of an order already
  despatched (`FakeOrders.Cancel`) is refused as the platform refuses it, the
  gateway answering 422 `order.already_shipped`; the old fake took it with a 204
  and changed nothing, which no stack does.
- **The gateway** reads the request bodies it needs (`amount`, `onHand`,
  `items[0].productId`, `shippingAddress.postalCode`, and for an image `imageKey`) and nothing else. The cause lives in the request, as
  it does on a stack, so the script's request is what scripts the fake.

Order of precedence matches the saga's: a reservation comes before a payment,
so a product with no stock ends out of stock whatever it costs.

A new branch adds a row to the table above, a rule to `FakeOrders.EndOf`, a
script in `SCRIPTS`, and a Playwright spec against FakePlatform. The status
names are the BFF's `CancelOutcomes`, which `LiteralDriftTests` reads.
