# Browser and export evidence

The successful Part 2 scenario prepares 10 units, sells 2 at 100 DOP each, posts the invoice with tax, checks 8 units remain and verifies the corresponding unpaid receivable and due date. An attempted sale of 99 units is rejected without changing inventory or the customer balance. The downloaded workbooks are independently inspected for numeric figures and executable formulas.

| Report | Screenshot with filters | PDF | Excel |
|---|---|---|---|
| Sales by period | [Screen](capturas/ventas.png) | [PDF](exportaciones/ventas.pdf) | [XLSX](exportaciones/ventas.xlsx) |
| Available inventory | [Screen](capturas/inventario.png) | [PDF](exportaciones/inventario.pdf) | [XLSX](exportaciones/inventario.xlsx) |
| Best-selling products | [Screen](capturas/top-productos.png) | [PDF](exportaciones/top-productos.pdf) | [XLSX](exportaciones/top-productos.xlsx) |
| Accounts receivable | [Screen](capturas/cuentas-por-cobrar.png) | [PDF](exportaciones/cuentas-por-cobrar.pdf) | [XLSX](exportaciones/cuentas-por-cobrar.xlsx) |

- [Posted invoice](capturas/factura.png).
- [Browser regression report](reporte-e2e/index.html).
- [Controlled API-outage report](reporte-api-caida/index.html).
- [Exact validation record and test names](validacion.md).
- [Machine-readable counters and verification receipts](resultados.json).

Sales totals include tax; the product ranking's net value excludes tax. Inventory quantities use each product's base unit. These differences are intentional and explained in the technical report.
