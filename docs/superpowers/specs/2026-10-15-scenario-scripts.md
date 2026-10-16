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
| Carrier refuses the address (the carrier simulator's `SIM-REFUSED`, which no journey test walks) | the delivery postal code `SIM-REFUSED` | `carrier-refused` | the log: one booking answered 422; then the order read `confirmed` |
| Carrier is down (`SIM-DOWN`) | the delivery postal code `SIM-DOWN` | `carrier-down` | the log: repeated bookings of one shipment, each 503; then `confirmed` |
| Carrier is slow (`SIM-SLOW`) | the delivery postal code `SIM-SLOW` | `carrier-slow` | the log: repeated bookings of one shipment, each 201 (the answer comes after Shipping has stopped waiting); then `confirmed` |
| Carrier feed is strange (`SIM-STRANGE`) | the delivery postal code `SIM-STRANGE` | `carrier-strange` | the log: one booking answered 201, then the feed polled at least twice since; then `confirmed` |
| Stock timeout (`A_stock_wait_that_lapses_cancels_the_order_and_Inventory_releases_off_the_cancellation`) | Inventory stopped with the saga's waits shortened, then an order (`run-locally.md`, "The branches, then") | `stock-timeout` | `out_of_stock`, which only the saga's own wait can have caused with Inventory stopped |
| Payment timeout (`A_payment_wait_that_lapses_compensates_and_flags_the_money_that_arrives_late`) | a total ending `.09` under the short waits, Inventory up | `payment-timeout` | `declined`, which only the payment wait can have caused: `.09` is no decline code |
| Despatch timeout (`A_despatch_that_never_comes_is_escalated_and_the_stock_stays_reserved`) | an order to `SIM-STRANGE` under the short waits (`run-locally.md`, "Despatch timeout") | `despatch-timeout` | `flagged`: the line Ordering logs when it raises `not_despatched` (the Loki query `run-locally.md` names), then the order read `held` at `confirmed` |
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

- **The release timeout.** The cause is by hand (place a `.09` order with Inventory
  up, stop Inventory after its stock has reserved, cancel the order, wait the release
  delay). What it ends in is a review row `stock_not_released` and an order read that
  says `cancelled` throughout, exactly as an ordinary cancel does; the evidence is
  the log line the review read finds (#1079 landed as that line), so the evidence now
  exists. The lease acquires its changes together, and this cause needs Inventory
  stopped *after* the reservation, so the script waits on a lease that can take one
  more change while it is held. Not scripted yet; the next change.
- **The confirmation timeout.** `run-locally.md` says it is not reachable by
  hand: it needs Ordering silent after it has confirmed, and the saga lives inside
  Ordering.

The stock and payment timeouts need no such evidence: Inventory is stopped, or the
total ends `.09`, so the status they end in has one cause. The order read folds the
saga's cancel reason (`stock_timeout` into `out_of_stock`, `payment_timeout` into
`declined`), which is why the script stops the only thing that could have refused the
reservation.
- **The shipment's own state for the four carrier outcomes** (`SIM-REFUSED`,
  `SIM-DOWN`, `SIM-SLOW`, `SIM-STRANGE`): `Unfulfillable`, `Pending` or `Booked`,
  which only the shipment table shows. The four scripts assert what the carrier
  saw, from the simulator's own request log, and the order read held at
  `confirmed`; they do not assert the shipment, which the console cannot read.
  `SIM-REVERSED` and `SIM-LATE` end `delivered`, like the happy path, and are not
  scripted.
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
  stock never set, or set below one, ends it `out_of_stock` (a product published a moment ago has no count, and the
  platform's reservation fails for it as for zero), else a price whose minor units are
  `DeclinedMinorUnits` ends it `declined`. Either lands one `Step` after the
  place with nothing confirmed, despatched or delivered, and a cancel asked of
  it changes nothing. A product the fake never published takes the happy path.
- **The object store** (`FakeObjectStore`) keeps a key when a signed `PUT`
  reaches it and refuses one with no signature in the URL; it keeps any bytes
  and records whether they were an image. A publish with an `imageKey` is
  refused as the platform refuses it, with the 422 rule errors Catalog's
  `ProductErrors` names: not this seller's key, nothing uploaded under it,
  or not an image. The edge serves a key only once a publish attached it.
- **The review flag.** `FakeOrders.ReviewFlags(orderId)` answers the Loki query for the
  flag line (`FakeGrafana.Loki`): an order to `SIM-STRANGE` placed under the short waits
  is flagged `not_despatched` a few seconds after the place (the real wait is 60 s with
  the override and three days without, which no watch outlasts). Every other Loki query
  answers as before.
- **The carrier's request log.** `FakeOrders.CarrierLog(code, polls)` answers the
  simulator's `POST /__admin/requests/find` for the two filters the Scenario sends
  (`FakeCarrier`): the bookings whose body names a code, and the polls of the
  `SIM-STRANGE` parcel's feed. An order to one of the four codes is confirmed and
  stays so, and the log shows the shapes `run-locally.md` gives: `SIM-REFUSED`
  one booking, 422; `SIM-DOWN` repeated bookings, 503; `SIM-SLOW` repeated
  bookings of one shipment, 201; `SIM-STRANGE` one booking and then polls. The
  cadence is the fake's own. The log carries no order id (blueprint-backend#1074
  would add one), so the step reads from when the order was placed and groups
  retries by the booking's `shipmentId`: an order to the same code at the same time
  by another run is not told apart.
- **The stack.** `FakeStack` records whether Inventory is stopped and whether Ordering
  was recreated with the short waits, set by the scripted `docker` commands a lease
  runs. `FakeOrders` reads it at the place: with Inventory stopped the order ends
  `out_of_stock` when the stock wait lapses (seconds with the short waits, five
  minutes without, which no watch outlasts), and a total ending `.09` ends
  `declined` by the payment wait the same way. A script that took no lease would time
  out against the fakes as against a stack.
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
