# Security, reporting and business processes

## Scope and compatibility

Part 1 already implemented authentication, policy enforcement, custom validators, transactional inventory, invoices and customer ledgers. This delivery reuses that coverage and adds the missing sales role, four business reports and their exports. It also addresses the confirmed receivable reclassification, application-date and truncated-credit-note issues.

The five Onion projects, Static SSR rendering, typed API clients and existing routes are preserved. No interactive Blazor runtime is introduced. Where existing behavior is replaced, the original code is retained as comments as requested. New functionality lives in focused feature, service, page and test files.

## Roles and permissions

| Operation | Administrator | Supervisor | Vendedor | Legacy Ejecutor |
|---|---|---|---|---|
| Consult business records and reports; export | Yes | Yes | Yes | Yes |
| Create customers/products and other master data | Yes | No | No | Yes |
| Modify master data | Yes | Yes | No | No |
| Capture invoice drafts and lines | Yes | No | Yes | Yes |
| Modify/release/post invoices | Yes | Yes | Yes, invoice-specific policy | No |
| Delete records | Yes | No | No | No |
| Manage users and application configuration | Yes | No | No | No |
| Register customer payments | Yes | Yes | No | No |
| Apply payments / post inventory and cost | Yes | Yes | No | No |

`Vendedor` is a new role rather than a rename: existing Ejecutor users and permissions remain valid. A data-only Identity migration provisions the role even when demo-user seeding is disabled; it preserves an existing Vendedor role ID, users and memberships. Its non-destructive rollback deliberately leaves role assignments intact. It receives consultation and invoice-specific permissions, not global `CanAdd` or `CanModify`. API and SSR use `CanCapturarFactura` and `CanEmitirFactura`, with compatibility for the original coarse policies. Document collaboration remains shared; ownership restrictions are not introduced silently.

## Security and validation

Identity hashes passwords and enforces password composition, unique emails and lockout after three failed logins for five minutes. The API validates JWT issuer, audience, signature and lifetime. Blazor holds the token in a server session; it is not exposed to browser storage. HttpOnly and SameSite cookies, form antiforgery and API policies protect sensitive operations.

Server validation includes required fields, decimal precision, quantities, prices, fiscal identifiers, email, date limits and concurrency tokens. Custom validation includes exact unit conversion and per-user allowed posting dates. New reports reject reversed ranges, unsupported states, invalid pagination, unsupported filter combinations and invalid currency codes. Inventory and receivables are cumulative reports: they accept a cutoff date rather than a misleading start-date filter.

Errors use controlled messages and existing ProblemDetails/Result conventions. Production TLS terminates at Caddy, forwarded headers are limited to a known proxy and one hop, and production cookies require HTTPS. Login and report downloads remain authorized at the server; menu visibility is only a usability aid.

## Four business reports

| Report | Data and meaning | Filters | Export |
|---|---|---|---|
| Sales by period | Posted invoices and credit notes; sales, returns and net total including tax | Inclusive start/end, customer, matching product/warehouse, sales/returns, currency | PDF/XLSX |
| Available inventory | Product and value books accumulated through cutoff, by product and warehouse | Cutoff, product, warehouse, with/without quantity | PDF/XLSX |
| Best-selling products | Product invoice lines and credit lines; quantities converted to base units; net line value excluding tax; ranked by net value | Inclusive start/end, customer, product, warehouse, sales/returns, currency | PDF/XLSX |
| Accounts receivable | Existing historical customer balances and aging: current, 1–30, 31–60, 61–90, over90, unapplied | Cutoff, customer, all/with balance | PDF/XLSX |

Drafts never count as sales. A sales report filtered by product or warehouse selects matching whole documents; the product ranking reports matching product lines. Returns are explicit rather than hidden by netting alone. The ranking uses monetary value; it also exposes net quantity so users can assess units sold. Different product units are not meaningfully summed.

Sales/ranking default to DOP and never combine different currencies in one total. Inventory/customer books use the application base currency DOP; this delivery does not add exchange-rate accounting.

Screen totals cover the whole filtered result, not only the visible page. PDF and Excel use the same columns, filter metadata and authoritative totals. XLSX stores amounts as numeric cells and text as text, including strings resembling formulas. Exports must use a stable database snapshot and include all matching rows.

## Business process and consistency

The seller selects the customer, products, quantities, warehouse and posting/due dates in an invoice draft. The existing tax engine calculates preview totals. Posting validates setup, dates and availability under locks, numbers the invoice and commits inventory, customer and accounting records atomically. A failed operation rolls everything back, including consumed transactional series numbers. Posted documents and books remain immutable. Invoice credit-note summaries now read all exact matches and line aggregates in one snapshot, without the former 200-row cap or one HTTP request per note.

For reconciliation, the existing inventory view's `SoloConExistencia` filter now retains a row if quantity **or residual value** is nonzero. The new business report's with/without-existence states describe physical quantity; choose All to inspect zero-quantity valuation balances.

An invoice with a positive unpaid amount creates a customer movement with invoice number, customer, original amount, posting/document/due dates and derived remaining balance. Aging and open/settled status are derived from the movement detail rather than independent mutable flags. Credit notes and payment applications reduce the balance.

If a customer changes accounting group between invoicing and payment, applying the payment now adds a balanced reclassification from the payment's frozen CxC account to the invoice's frozen CxC account. It uses the applied amount and the same transaction as the customer application. Applying before either document's posting date is rejected. Historical, already-confirmed applications are not silently rewritten.

Inventory cost accounting remains an explicit, authorized business batch. Run it after inventory operations to reflect cost in the general ledger; a second run has no effect. This is a deliberate operational process, not an automatic-posting claim. The delivery tests the batch's idempotence.

## Verification and operational boundaries

See [validation evidence](evidencias/validacion.md) for exact tests and fresh results. Historical Part 1 test counts are not claimed as new execution evidence. Tests use a dedicated PostgreSQL fixture and the browser demonstration uses a separate Compose project, ports, credentials and volumes. GitHub workflow configuration is delivered; a remote GitHub run is only proven by an actual remote result.

Accepted architectural limits remain explicit: session storage supports one replica; some cost-batch account derivations scale per movement; historical credential rotation and public deployment require operator action. These are not missing academic report/authorization/transaction features.
