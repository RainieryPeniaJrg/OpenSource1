# Parte 2 — analysis and execution plan

> Execute autonomously task by task with the installed .NET skills and targeted tests. Preserve existing behavior. User authorizes UI, UX and data-flow decisions; no additional design approval is required.

**Goal:** Complete the actual gaps in Part 2, preserve Part 1, and deliver verifiable security, reporting and business-process evidence.

**Baseline:** `45bc4bc`, branch `parte2_opensource_2`, created from `main` on 2026-11-03.

**Architecture:** Keep Onion, Static SSR, typed HTTP clients, PostgreSQL, EF/Dapper, QuestPDF and ClosedXML. Add focused features rather than replace existing modules. Retain commented originals when replacing existing behavior, as requested.

## Requirement coverage and acceptance

| Step | Existing coverage | Work and acceptance |
|---|---|---|
| 1 Login | Identity, username/email, password, SSR session | Re-run positive/negative login tests; document evidence. |
| 2 Roles | Administrador, Supervisor, Ejecutor | Add Vendedor without removing Ejecutor or changing legacy permissions; prove allowed sales and denied administration. |
| 3 Authorization | API policies and SSR policies | Test direct forbidden requests, including reports and exports. |
| 4 Validation | FluentValidation, domain validation, form controls | Preserve existing coverage; add report date/filter validation and document a custom validator. |
| 5 Security | Identity hashing/lockout, JWT, HttpOnly, antiforgery, safe errors | Review production configuration, TLS/proxy/session behavior; provide an opt-in production setup without disturbing local defaults. Never copy secrets. |
| 6 Four reports | Existing customer/product master exports; inventory/CxC queries | Add period sales, available inventory, best-selling products and receivables with authoritative book-based data. |
| 7 Filters | Several existing query criteria | Report-specific date/client/product/status filters; reject unsupported or invalid values rather than silently ignore them. |
| 8 Export | QuestPDF and ClosedXML | Add PDF and XLSX for all four reports; preserve filtering, numeric types and totals. |
| 9 Sale | Draft → lines → release → post → invoice | Reuse workflow; ensure Vendedor can complete it without administrative permissions. |
| 10 Inventory | Transactional posting and inventory books | Verify insufficient-stock rejection and no overselling under concurrency. |
| 11 Receivables | Invoice customer movements, due dates, payments | Reuse and prove invoice/customer/amount/date/due/state. Fix CxC reclassification gap when customer group changes. |
| 12 Transactions | Shared transaction, locking, rollback | Prove no partial writes on failed sale and concurrency boundaries. |
| 13 Tests | xUnit integration/SSR and Playwright suites | Record exact commands, results and limitations for all requirements. Historical results are not new evidence. |
| 14 Delivery | Part 1 documents and screenshots | Add Part 2 technical report, role matrix, report/export evidence, presentation and demonstration script. |

## Previously identified pending work

- Reconcile CxC accounts on applying payment after a customer-group change; prove balancing and rollback.
- Retain manual inventory-cost posting unless an additive automatic option can be safely verified; clearly expose its operational status and document behavior.
- Correct inventory availability filter to include residual value when relevant; test quantity zero with nonzero value.
- Address confirmed N+1 paths where practical, with query/result regression evidence; do not speculate or refactor unrelated code.
- Add production configuration and readiness checks, preserving development workflow.
- Integrate E2E validation into an isolated CI path; preserve current .NET checks.
- Reconcile stale README references, removed sample modules, historical branch statements and delivery links.
- Historical credential rotation is an external operator action: document it without claiming rotation or reproducing credentials.

## Tasks

- [x] T0 Create requested branch and baseline analysis.
- [x] T1 Complete gap analysis; record concrete interfaces and design decisions below.
- [x] T2 Add Vendedor and least-privilege sales permissions with negative API/SSR tests.
- [x] T3 Add four report queries, validated filters and protected API with deterministic PostgreSQL tests.
- [x] T4 Add Static SSR report UI, typed client, filtered PDF/XLSX downloads and tests.
- [x] T5 Fix confirmed financial/inventory gaps with regression tests before implementation.
- [x] T6 Add production/CI readiness configuration and targeted security checks.
- [x] T7 Run isolated end-to-end demonstration and regression; capture report/export/business-process evidence.
- [x] T8 Write technical report, role matrix, presentation and demo instructions; reconcile documentation.
- [x] T9 Independent final review, fix material findings, verify branch diff and report remaining external actions.

## Verification constraints

- Do not use shared CRM/Agenda databases or ports. Existing services occupy 5432 and 8080.
- PostgreSQL tests use a dedicated test container on 65432; only one test process may own it at once.
- Do not start the default Compose stack against occupied ports. Use an isolated project, ports, volumes and names for demonstrations.
- No destructive migration against existing data, global Docker cleanup, production deployment, push or merge is authorized by this task.
- Add tests for empty results, invalid/reversed dates, unauthorized downloads, exact date boundaries, exports with more than one page and rollback/concurrent sales.

## Execution ledger

- 2026-11-03: User delegated design decisions and autonomous execution. Preserve Ejecutor; add Vendedor as a separate role. This avoids migrating existing role assignments and preserves Part 1 tests.
- 2026-11-03: Reports are new business reports; existing master-data reports remain available.
- 2026-11-03: Credit-limit enforcement is not explicitly required by Part 2; do not expand the scope merely because a field exists. Credit-sale receivables and consistency remain mandatory.
- 2026-11-03: Four query routes `/api/reportes/{ventas,inventario,top-productos,cuentas-por-cobrar}` expose `ReporteResponse<T>` with full filtered totals. Additive SSR route `/reporteria/empresarial` preserves master reports.
- 2026-11-03: Sales/ranking filter one currency (DOP default). Ranking uses net line value excluding tax and base-unit quantities; sales product filters select whole documents. Cumulative inventory/CxC reject start dates.
- 2026-11-03: Initial report/export/proxy tests23/23 passed. Payment/reclassification/vendor tests69/69 passed. Initial dedicated browser scenario passed (1 scenario,47.7s), including all eight downloads; final-version reexecution still required.
- 2026-11-03: Review found client-side multi-page export could mix concurrent writes. Replace it with one server export response gathered under RepeatableRead/ReadOnly, rather than claim consistency from matching totals.
- 2026-11-03: Exact invoice credit-note summary will use three constant queries in one snapshot. This fixes both N+1 and the prior cap200/prefix-filter truncation.
- 2026-11-03: Inventory-cost posting remains a supported manual business batch with idempotency evidence; no new automatic financial posting is introduced without an outbox/process requirement. Some per-movement account lookup scaling remains explicit.
- 2026-11-03: Production reference targets one replica and reauthentication after restart. Shared distributed sessions/HA, real secret rotation and public TLS acceptance are separate operational work requiring a target; do not claim them complete from a local lab.
- 2026-11-03: Independent read-only review found no material issues; the late Identity migration received its own independent review. It creates Vendedor with user seeding disabled and preserves existing role IDs and memberships on rollback/reapply (3/3 targeted migration/notes tests green).
- 2026-11-03: Production Compose validated with a fictitious hostname; Caddy syntax validated in a disposable container without networking. No public deployment or certificate issuance was performed.
- 2026-11-03: Mixed Release regression exposed HTTP-only SSR fixtures after production cookies became HTTPS-only. The fixture now uses HTTPS; production policy was retained.
- 2026-11-03: Full regression exposed missing central module registration for the new listing and an old empty-invoice test that depended on a failed notes lookup. Added the module and made that fixture represent a real uncredited line, preserving the original test assertion and commented baseline data. Final broad reexecution is required.
- 2026-11-04: Broad reexecution completed2093cases:2092passed and1historical-fixture setup failed against the new date restriction. Preserved its historical-read assertions and seeded the legacy rows directly; the whole20-case `ClientesVistasApiTests` class passed. All10production-assembly hashes remained identical. Closure uses explicit full-run-plus-fixture-retest reconciliation, not a claim of a later single all-green full run. Receipts and exact commands: `docs/entregable-parte-2/evidencias/validacion.md`.
- 2026-11-04: Final browser regression16passed/1outage skipped; separate outage1passed and API recovered. All17unique scenarios verified, with5screenshots and8downloads collected; workbooks checked for numeric business figures and no formulas.
- 2026-11-04: Delivery complete for the academic Part2 scope. External operator actions remain explicit: real DNS/public TLS acceptance, historical-secret rotation and production provisioning. Accepted architecture: single-replica in-memory sessions and manual idempotent cost batch; no claim of HA or global removal of every per-movement lookup.
