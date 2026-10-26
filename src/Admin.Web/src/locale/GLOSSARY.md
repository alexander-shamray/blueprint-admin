# Translation glossary

`messages.en-US.json` is what `ng extract-i18n` writes from the templates and
`$localize` calls; `messages.<locale>.json` is each language the console ships.
The build copies only `messages.*.json` from this directory (`angular.json`,
`assets`), so this file is not served. `npm run lint` checks that every id has
text and every placeholder survives (`scripts/check-translations.mjs`).

## ru-RU

The terms `messages.ru-RU.json` uses for the console's recurring words. A new
message takes these; a change to one changes every message that uses it.

| English | Russian | Note |
|---|---|---|
| alert | оповещение | firing → «срабатывает», alight → «горит», clears → «гаснет» |
| runbook | ранбук | |
| cause (a runbook's fault script, as a noun) | сбой | to cause it → «вызвать сбой» |
| job | задание | neuter: «задание завершилось» |
| identity (who a call is sent as) | учётная запись | send / run / read as X → «от имени X»; anonymous → «анонимно» |
| surface (a platform endpoint) | точка доступа | |
| follow (logs) | отслеживать | |
| trace (noun) / to trace | трасса / проследить | tracing as an activity → «трассировка» |
| diagram | схема | |
| replay / discard / dry run | повтор / отбрасывание / пробный | |
| dead letters | недоставленные (сообщения) | |
| holder (of personal data) | владелец (данных) | |
| lease | аренда | |
| saga / stack / broker | сага / стек / брокер | |
| exchange / binding | обменник / привязка | |
| gateway | шлюз | edge policy → «политика шлюза» |
| queue depth | глубина очереди | |
| drained / empty | опустела / пуста | a queue is feminine |
| mail sink | приёмник почты | a mail item is «письмо» |
| layout / workspace / widget | раскладка / рабочее пространство / виджет | |
| Scenario | сценарий | |
| run (Scenario) / run (load, Playwright) | запуск / прогон | |
| golden signals | золотые сигналы | |

These stay Latin: service names (Ordering, Catalog, Inventory, Payments,
Shipping, Notifications, Privacy, BFF, edge), correlation id, realm, parked,
platform status values (confirmed, dispatched, Reserved), commands, paths and
headers.

Counts read «noun: N» without agreement, because `$localize` has no run-time
ICU plural; a template's own ICU plural gets every Russian form. The
Scenario's wait fragments complete «Ждём, пока {$what}», so each is a clause.
An English «·» separator inside a message becomes «—»; one a template
writes outside its i18n blocks stays as it is.

No native speaker has read the Russian text. The owner reviewed and approved
it in that reader's place (#520).
