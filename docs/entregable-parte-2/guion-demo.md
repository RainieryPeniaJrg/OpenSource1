# Part 2 demonstration

Use only the dedicated Part 2 lab. Prepare the browser at `http://localhost:18080` and retrieve the seed password privately from `.local/parte2.env`.

1. Show an incorrect login, then log in as `vendedor` using username or email. Show the available sales/report modules.
2. Attempt `/admin/users`: access is denied. Explain that the API independently returns403, even when a request is forced.
3. With the administrator, prepare a customer, product, posting setup and 10 units through an inventory journal. Explain that product existence comes from the inventory book.
4. As `vendedor`, select that customer and product, enter quantity 2, preview tax/total and post the invoice. Show its number, date, due date and immutable detail.
5. Show remaining inventory 8 and the matching unpaid customer balance. Attempt quantity 99 in a second draft: it must fail without changing either inventory or the balance.
6. Run inventory-cost accounting as an authorized user; rerun it and show that no duplicate accounting records appear.
7. Open `/reporteria/empresarial`. Select Sales by period, dates and the demonstration customer. Explain sales, returns and net total.
8. Select Available inventory, the product and cutoff. Show quantity8 and valuation. Explain that the date is cumulative.
9. Select Best-selling products and the customer. Show base-unit quantity and net value excluding tax.
10. Select Accounts receivable and the customer. Show due/current/overdue aging and unapplied amounts.
11. Download PDF and Excel from each report. Open at least one PDF and one workbook; compare filters and totals to the screen.
12. Show a reversed report date range being rejected. Show the technical report, role matrix and fresh validation evidence.

The automated scenario `tests/e2e/specs/10-parte2.spec.ts` reproduces this path with isolated data and saves screenshots and downloads. Posted documents are intentionally retained as demonstration evidence. Do not delete business books to reset a demonstration.
