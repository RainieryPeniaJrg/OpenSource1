# Fase 7 — Vistas de movimientos · Plan de implementación

> Historical execution record. Its branch-integration statements and open checkboxes describe that phase's execution time, not the current branch. Part 2 starts at main `45bc4bc` with phases 0–8 already present. Current coverage, corrected residuals and validation are tracked in [the Part 2 plan](2026-11-03-parte2-opensource-2.md); this historical record is preserved rather than silently rewritten.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** vistas de consulta sobre los libros ya construidos (inventario, clientes, contabilidad): movimientos de
producto con saldo acumulado, movimientos de valor, existencias y valor por almacén, movimientos de cliente, estado de
cuenta por antigüedad, movimientos contables y balance de comprobación.

**Architecture:** solo lectura. Cada vista es una consulta Dapper paginada con allow-list de columnas (`ColumnasPermitidas`,
orden estable por `Id`), expuesta por la API con `CanConsult` y consumida por una página Blazor Static SSR con filtros por
formulario GET y paginación por query string. Todos los saldos se derivan (D1); nada se almacena.

**Tech Stack:** .NET 10, Dapper, MediatR 13, Blazor Static SSR, xUnit, Docker/Postgres 17.

**Spec:** [`docs/superpowers/specs/2026-09-12-axionerp-erp-modules-design.md`](../specs/2026-09-12-axionerp-erp-modules-design.md), sección "Fase 7", más el bloque "Desviaciones acordadas durante la ejecución de la Fase 7" que añade la Task 7.1. Pendientes heredados: sección "Resultado y pendientes que hereda la Fase 7" de [`2026-09-28-fase-6-facturacion.md`](2026-09-28-fase-6-facturacion.md).

## Global Constraints

- TFM `net10.0`; nullable e implicit usings. Solución `test.slnx`. Sin paquetes nuevos, sin CPM. PostgreSQL 15+.
- Blazor Static SSR: sin `@rendermode`, `@onclick`, `@bind` interactivo ni JS nuevo; filtros con formularios GET y
  paginación por query string; `[SupplyParameterFromQuery]` no admite enums (usar `int?`); `PageRequest.Descendente` es
  `true` por defecto (fijar el orden); fechas con `EntradaFecha`; importes con el formato de las páginas existentes;
  selects cargados de la API con el valor vigente; mensajes reales de la API; si la API falla, la página responde 200 con
  un aviso.
- Sin escrituras: ninguna consulta de esta fase hace INSERT/UPDATE/DELETE; no hay migraciones salvo índices de lectura
  justificados con `EXPLAIN ANALYZE` (y entonces con `ApplicationDbContextModelTests` y sonda vacía).
- Consultas: SQL parametrizado; columnas de orden solo desde `ColumnasPermitidas`; orden estable (`, "Id"`); filtros por
  rango de fechas inclusivos; paginación con total. Rendimiento objetivo < 200 ms con 100 000 movimientos por vista
  paginada (medido con datos generados, `EXPLAIN ANALYZE` en el informe).
- Permisos: todas las vistas `CanConsult`.
- Errores: `Result`/`Error`; parámetros inválidos → 400 con `Campo`; entidad de ruta inexistente → 404.
- Tests contra Postgres real (`DOCKER_CONTEXT=default` solo como variable de entorno); suite completa al final de cada
  task (hoy 1204). 6 avisos CS0618 preexistentes; no añadir avisos.
- Commits LOCALES autorizados en `feat/erp-fase-7-vistas` (desde `feat/erp-fase-6-facturacion`). Nunca merge ni push. No
  tocar `appsettings*.json`.
- Runtime obligatorio (API + Blazor + Postgres reales) para cada página nueva, con evidencia en el informe.

## Review Focus

1. **Saldo acumulado** de movimientos de producto: correcto a través de páginas (la página 3 empieza con el saldo que
   dejó la 2) y con filtros de fecha (el saldo inicial incluye lo anterior al rango). Test en Task 7.2.
2. **Antigüedad:** una factura parcialmente pagada entra en su tramo por su restante, no por su original; los pagos no
   aplicados restan del total del cliente (tramo "sin aplicar"); la fecha de corte excluye movimientos posteriores. Test
   en Task 7.3.
3. **Balance de comprobación** cuadra (suma de saldos = 0) para cualquier rango; saldo inicial + débitos − créditos =
   saldo final por cuenta; cuentas de resultado y balance tratadas igual (sin cierre de ejercicio). Test en Task 7.4.
4. **Existencia y valor por almacén** coinciden con `IConsultaInventario` y con el saldo de la cuenta de inventario tras el
   batch de costo. Test en Task 7.2.
5. **Filtros inválidos** (fechas invertidas, página 0, tamaño > máximo, ordenar por columna no permitida) → 400 o valor
   por defecto documentado, nunca 500. Test en cada task.

---

## Task 7.1 — Desviaciones de diseño (solo docs; la hace el controlador)

```markdown
**Desviaciones acordadas durante la ejecución de la Fase 7:**

- **Saldo acumulado** en movimientos de producto solo cuando el filtro fija un producto (y opcionalmente un almacén): es
  el único caso en que tiene sentido; se calcula con una función de ventana ordenada por `(FechaRegistro, Id)` más el
  saldo anterior al rango (`desde`) y a la página. Sin producto, la columna no se devuelve.
- **Estado de cuenta:** antigüedad por `FechaVencimiento` respecto a una **fecha de corte** (por defecto hoy): corriente
  (no vencido), 1-30, 31-60, 61-90, 90+ días vencidos, sobre el **restante a la fecha de corte** (detalle con
  `FechaRegistro <= corte`); los pagos con restante negativo se muestran como "sin aplicar" y restan del total.
- **Balance de comprobación** por rango de fechas: saldo inicial (antes de `desde`), débitos y créditos del rango y saldo
  final, solo cuentas de Posteo con movimientos o saldo; sin cierre de ejercicio (las cuentas de resultado acumulan).
- **Valor de inventario** por producto y almacén = `SUM(ImporteCosto)` de los movimientos de valor hasta la fecha;
  existencia = `SUM(Cantidad)` de movimientos de producto (misma derivación que `IConsultaInventario`).
- **Limpieza heredada:** `ConvertirABaseAsync` y `ObtenerFactorAsync` (redondean) se retiran de
  `IConversionUnidadMedidaService` si no tienen llamadores.
```

---

## Task 7.2 — Vistas de inventario

**Files:**
- Create: `src/OpenSource1.Application/Features/Inventario/Consultas/**` (queries y DTOs), `src/OpenSource1.Infrastructure/Data/Queries/DapperInventarioConsultasRepository.cs`, endpoints en `InventarioController` (`GET api/inventario/movimientos-producto`, `movimientos-valor`, `existencias`)
- Create: páginas `Pages/MovimientosProducto.razor`, `Pages/MovimientosValor.razor`, `Pages/Existencias.razor` + clientes HTTP + menú "Inventario"
- Test: API contra Postgres (movimientos sembrados con `IRegistroMovimientosInventario` y diarios)

Movimientos de producto: filtros `productoId`, `almacenId`, `desde`, `hasta`, `tipoMovimiento` (int), `tipoOrigen` (int),
`numeroDocumento`; columnas: fecha, tipo, documento, almacén, cantidad, unidad, restante (entradas), origen; con
`productoId`: `saldoAcumulado`. Movimientos de valor: mismos filtros (sin restante) + `soloAjustes`; columnas: cantidad
valorada, importe costo, costo por unidad, importe venta, ajuste, tipo de valor, contabilizado (`ImporteCostoPosteadoContabilidad
= ImporteCosto`). Existencias: filtros `almacenId`, `productoId`/texto, `fecha` (por defecto hoy), `soloConExistencia`;
columnas: producto, almacén, existencia, valor, costo medio (valor/existencia si existencia > 0).

- [ ] Tests (Review Focus 1, 4, 5; paginación y totales; tipos de movimiento) → implementar → `EXPLAIN ANALYZE` con 100 000
  movimientos → runtime de las tres páginas → suite → commit `feat: vistas de movimientos de producto, de valor y existencias por almacen`.

---

## Task 7.3 — Vistas de clientes

**Files:**
- Modify/Create: `ClientesController` (`GET api/clientes/{id}/movimientos` ya existe: añadir filtros `desde`, `hasta`, `soloAbiertos`, `tipoDocumento` si faltan), `GET api/clientes/estado-cuenta?fechaCorte=&socioId=` (todos los clientes o uno) con tramos
- Create: páginas `Pages/MovimientosCliente.razor` (por cliente, con restante y abierto) y `Pages/EstadoCuenta.razor` (tabla por cliente con tramos y total; enlace al detalle)
- Test: API contra Postgres (facturas posteadas con el motor real, pagos y aplicaciones)

- [ ] Tests (Review Focus 2, 5; varios clientes; fecha de corte anterior a un pago; factura vencida parcialmente pagada) →
  implementar → runtime → suite → commit `feat: movimientos de cliente y estado de cuenta por antiguedad`.

---

## Task 7.4 — Vistas contables

**Files:**
- Modify/Create: `ContabilidadController` (`GET api/contabilidad/movimientos` ya existe: completar filtros `cuentaId`, `desde`, `hasta`, `tipoDocumento`, `numeroDocumento`, `socioId`), `GET api/contabilidad/balance-comprobacion?desde=&hasta=`
- Create: páginas `Pages/MovimientosContables.razor` (por cuenta y rango, con enlace al registro) y `Pages/BalanceComprobacion.razor` (saldo inicial, débitos, créditos, saldo final; totales que cuadran)
- Test: API contra Postgres (asientos de facturas, cobros y batch de costo)

- [ ] Tests (Review Focus 3, 5) → implementar → runtime → suite → commit `feat: movimientos contables y balance de comprobacion`.

---

## Task 7.5 — Limpieza y cierre de la Fase 7 (y del proyecto de módulos ERP)

- [x] Retirar `ConvertirABaseAsync`/`ObtenerFactorAsync` si no tienen llamadores (tests adaptados).
- [x] `ApplicationDbContextModelTests`, `has-pending-model-changes`, `grep` TODO, suite completa.
- [x] Sección "Resultado del proyecto y pendientes" al final de este plan: resumen de las Fases 0-7, requisitos de
  despliegue (PostgreSQL 15+), limitaciones aceptadas y pendientes acumulados de todas las fases.

---

## Resultado del proyecto y pendientes

### Resultado de la Fase 7

Fase 7 cerrada en `feat/erp-fase-7-vistas`: vistas de movimientos de producto (saldo acumulado con producto fijado,
correcto entre páginas y con `desde`), de valor y existencias/valor por almacén (= `IConsultaInventario` = saldo de la 1301
tras el batch) (Task 7.2, `e45c0b6`); movimientos de cliente con restante a la fecha de corte y estado de cuenta por
antigüedad (corriente, 1-30, 31-60, 61-90, 90+ y "sin aplicar"; total = saldo de CxC a la fecha) (Task 7.3, `7ba812a`);
movimientos contables con filtros y enlace al asiento, y balance de comprobación por rango que cuadra (Task 7.4, `7d803dd`).
Todo solo lectura, sin migraciones ni índices nuevos, < 200 ms con 100 000 movimientos salvo el estado de cuenta de toda la
cartera (ver pendientes). Cierre (Task 7.5): retirados `ConvertirABaseAsync`/`ObtenerFactorAsync` (sin llamadores; tests
reexpresados sobre `ObtenerConversionAsync`), `PageRequest.Offset` acotado a `[0, int.MaxValue]` (los 19 listados
anteriores a la Fase 7 respondían 500 con `pagina=2147483647`; ahora 200 con página vacía), y las reglas refinadas del
estado de cuenta y del balance registradas en el spec. Tanda final de correcciones: retirada `PaginacionValidacion` (las
vistas de la Fase 7 respondían 400 con `Campo = "Pagina"`; ahora 200 con página vacía como el resto, y sus páginas Blazor
saltan a la última); test permanente de coherencia entre vistas (`CoherenciaVistasApiTests`: estado de cuenta = Σ tramos =
Σ restantes de movimientos de cliente = CxC del balance, valor de existencias = 1301, existencia = `IConsultaInventario`, en
varias fechas de corte, tras entrada retroactiva, ajuste de costo y batch).

Verificación final: `dotnet build test.slnx --no-incremental` con 0 errores y solo los 6 avisos CS0618 preexistentes;
`ApplicationDbContextModelTests` en verde; `dotnet ef migrations has-pending-model-changes` → "No changes have been made to
the model since the last migration."; `grep -rnE "TODO|FIXME|NotImplemented"` en `src/`/`tests/` sin marcadores reales (solo
la palabra española "TODO(S)" como énfasis); suite completa (`DOCKER_CONTEXT=default dotnet test test.slnx`):
**1293/1293** (1273 + 26 nuevos − 6 retirados de conversión), 17 min 11 s.

### Resultado de la Fase 8

Fase 8 cerrada en `feat/erp-fase-8-notas-credito` (plan `2026-09-30-fase-8-notas-credito.md`): retirados los módulos
obsoletos `Entradas` y `AppSettings` con sus tablas (migración `RetirarEntradasYAppSettings`; la build queda con **0 avisos**)
(Task 8.2, `68e1481`); `Producto.CostoUnitario` al día con cada movimiento (V/Q de todo el libro si Q > 0) y en el ajuste
de costo, con la migración de datos `RecalcularCostoUnitario` (Task 8.3, `c790a51` y `e70914a`); nueva regla de importes
en facturas: precio > 0 obligatorio, importe 0 solo con 100 % de descuento, y una factura de total 0 se postea con
inventario y sin cliente ni asiento (Task 8.4, `8fe7ba0`); fechas de registro permitidas generales y por usuario
(`registro.fecha_no_permitida`) en todos los posteos, configurables solo por el Administrador (Task 8.5, `2e4c348`);
notas de crédito de venta ligadas a una factura posteada, con topes de cantidad, importe, IVA y costo, devolución opcional
de inventario al costo de la venta, asiento inverso y aplicación automática a la factura (Task 8.6, `437161b` y `998a7b2`),
con sus páginas e integración en las vistas de la Fase 7 (Task 8.7, `7ebfc4e`).

Verificación final (Task 8.8): cadena de migraciones en PostgreSQL 17 desde una base vacía hasta HEAD, `Down` hasta el
final de la Fase 7 (`AddSerieCobro`) y vuelta a HEAD con esquema idéntico al de una base nueva (`pg_dump -s`); desde una
base de la Fase 7 (`b661b62`) con datos (filas de `Entradas`/`AppSettings`, productos, libro de inventario, factura posteada
con su movimiento de cliente) sube a HEAD con los libros y documentos intactos, las tablas obsoletas eliminadas y el costo
unitario recalculado (producto con movimientos → V/Q a 4 decimales; Q = 0 y sin movimientos → conserva; borrado con
movimientos → recalculado); con una nota de crédito posteada, un borrador y fechas configuradas baja a la Fase 7 (las
tablas obsoletas vuelven vacías, con sus columnas en otro orden) y vuelve a HEAD. `ApplicationDbContextModelTests` en verde;
`has-pending-model-changes` → "No changes have been made to the model since the last migration."; sonda con `Up()` vacío;
`dotnet build test.slnx --no-incremental` con 0 errores y **0 avisos**; suite completa: **1359/1359**, 19 min 44 s.

Revisión final de la rama (`b661b62..1f25046`) y ola de arreglos (`f97a1d8`, suite **1361/1361**): el validador de fechas
**falla cerrado** si falta la fila general sembrada (también para usuarios con excepción propia; el posteo termina en 500
sin escribir nada), comentarios obsoletos corregidos y limpiezas de UI. Residuales aceptados en esa revisión:
- Una línea con cantidad pendiente pero importe restante 0 (residuo de redondeo por debajo del céntimo) no puede
  acreditarse sin devolución y queda pendiente para siempre (la ficha sigue ofreciendo "Crear nota" y "copiar todo lo
  pendiente" falla). Nunca acredita de más; cambiar la regla es decisión del usuario.
- Una devolución no se revalúa si el costo de la venta original se ajusta después de la nota (las entradas no se revalúan).
- `PostearNotaCreditoVenta` bloquea la factura con `FOR UPDATE` (bastaría `FOR NO KEY UPDATE`).
- Sin guarda en la migración para las series sembradas `NC`/`NC-BORR`: se prefiere que el `Up` falle con claridad a adoptar
  en silencio una serie creada a mano (mismo criterio que `FV`, `COBRO` y `CONTAB`).
- Sin `xmin` en la API de configuración de fechas (una fila, solo Administrador, gana la última escritura); borrado de línea
  de borrador de nota sin `xmin` (el posteo revalida bajo bloqueo).
- Duplicación entre el handler de la nota y el de la factura; validación de fecha duplicada en los formularios de la nota;
  el borrador de nota enmascara un fallo de carga como "no encontrado"; la lista de notas muestra todo 400 como
  "desde > hasta"; `HttpClient` compartido y mutado en tests de API.

### Resumen del proyecto (Fases 0-8)

- **Fase 0 — Saneamiento:** secretos fuera del repositorio (`appsettings.Example.json`, User Secrets), vulnerabilidad de
  OpenApi resuelta, sin borrado automático de la base, CORS sin fallback permisivo, `Location` correcto al crear usuario.
- **Fase 1 — Kernel transversal:** `Result`/`Error` y manejo global de errores (400 con `Campo`, 404, 409, 500 sin
  detalles), `ValueObject.Equals` corregido, allow-list de columnas (`ColumnasPermitidas`) y filtros exactos, contrato de
  paginación `PageRequest`/`PagedResult` en todas las capas, `IDbSession` (EF y Dapper en la misma conexión y transacción),
  API de transacción en `IUnitOfWork`, behaviors de MediatR, concurrencia optimista con `xmin`, borrado lógico, índices y
  permisos por recurso (`CanConsult`, etc.).
- **Fase 2 — Dominio maestro:** términos de pago, series de numeración con y sin huecos (`IGeneradorNumeroDocumento`),
  unidades de medida administrables con conversión por producto, categorías de producto, `Cliente` → `SocioDeNegocio`
  con campos nuevos y numeración, `Producto` con unidad base y categoría.
- **Fase 3 — Libro de inventario:** almacenes; libro append-only (`MovimientosProducto`, `MovimientosValor`,
  `AplicacionesMovimientoProducto`, protegidos por trigger) con `IRegistroMovimientosInventario` como único escritor;
  existencia y costo derivados (`IConsultaInventario`); rutina de ajuste de costo promedio; `Producto.Stock` eliminado y
  migrado al libro.
- **Fase 4 — Diarios de inventario:** plantillas, lotes y líneas con validación al capturar, registro atómico de un lote
  con numeración sin huecos y bloqueo de productos, páginas Static SSR.
- **Fase 5 — Contabilidad:** plan de cuentas, grupos contables y de cliente, setups con comodín e `IDerivadorCuentas`,
  libro contable append-only con cuadre por constraint trigger diferido (`IRegistroContable` único escritor) y batch
  idempotente de contabilización del costo de inventario.
- **Fase 6 — Facturación y CxC:** borradores de factura, documento posteado con IVA agrupado, libro de clientes con restante
  derivado, motor de posteo atómico (inventario + cliente + asiento), cobros y aplicación, guarda de borrado de socios y
  orden global de locks.
- **Fase 7 — Vistas de movimientos:** ver arriba.
- **Fase 8 — Notas de crédito, reglas de importe, fechas permitidas y limpieza:** ver arriba.

### Ramas e integración

Las ramas están encadenadas, cada una creada desde la anterior: `feat/erp-fase-0-1-kernel` (ya integrada en `main`) →
`feat/erp-fase-2-dominio-maestro` → `feat/erp-fase-3-inventario` → `feat/erp-fase-4-diarios` →
`feat/erp-fase-5-contabilidad` → `feat/erp-fase-6-facturacion` → `feat/erp-fase-7-vistas` →
`feat/erp-fase-8-notas-credito`. Todos los commits son locales; no se ha hecho merge ni push de ninguna. **La integración en
`main` y el PR los hará el usuario** (basta con integrar `feat/erp-fase-8-notas-credito`, que contiene todas las
anteriores).

### Requisitos de despliegue

- **PostgreSQL 15 o superior** (índices únicos `NULLS NOT DISTINCT` de los setups contables). Todas las pruebas usaron
  `postgres:17-alpine`.
- **Rotar las credenciales que quedaron expuestas en el historial de git** (contraseña de PostgreSQL y clave de firma JWT
  del `appsettings.json` anterior a la Fase 0): sacarlas del árbol no las borra del historial. Ya se avisó al usuario.
  Configurar los secretos por User Secrets o variables de entorno a partir de `appsettings.Example.json`.
- **`/uploads` debe seguir autenticado:** el host Blazor solo sirve `/uploads` (avatares e imágenes) a usuarios
  autenticados, con `nosniff` y `Cache-Control: private, no-store`; un proxy inverso no debe servir esa carpeta
  directamente.
- Migraciones aplicadas con `dotnet ef database update` (la aplicación no borra ni recrea la base). Los `Down` de
  `AddLibroContable` y `AddFacturasVentaYLibroClientes` son destructivos (ver contabilidad y facturación).
- Fase 8: el `Up` de `RetirarEntradasYAppSettings` **borra los datos** de `Entradas` y `AppSettings` (su `Down` recrea las
  tablas vacías); el `Down` de `RecalcularCostoUnitario` no hace nada (el costo recalculado se conserva); el `Down` de
  `AddNotasCreditoVenta` borra las notas de crédito (posteadas y borradores) y sus series, pero sus movimientos de
  inventario, cliente y contables quedan en los libros con valores de enumeración que la Fase 7 no conoce. Las series
  sembradas `NC` y `NC-BORR` chocan con una serie creada a mano con ese código (el `Up` fallaría por el índice único).

### Limitaciones aceptadas y pendientes acumulados

**Inventario**
- Unidad de línea siempre la base del producto en la UI (diarios y facturas): no hay selector de unidades alternativas ni
  endpoint de equivalencias para ese flujo; la API sí acepta `UnidadMedidaId` y congela el factor.
- Una cadena de transferencias del mismo día solo se registra si sus líneas están en el orden de la dependencia
  (`NumeroLinea` inmutable).
- Cambiar la unidad base con líneas de diario capturadas no las actualiza (`diario.factor_cambiado` al registrar; hay que
  recapturar la línea). Si cambian el factor y la exactitud a la vez, el diario da `conversion.cantidad_no_exacta` en vez de
  `diario.factor_cambiado`.
- La relajación de 4 decimales del costo aplica a cualquier entrada de tipo Transferencia, no solo a las del diario.
- `NumeroRegistro` de diarios único global entre series; fecha de la serie en UTC.
- Borrar una línea durante el registro de su lote da 409 en vez de 404; borrar un lote bloqueado no se rechaza.
- Un factor < 0.0000005 se redondea a 0 al congelarse (sin UI de `UnidadesMedidaProducto` hoy).
- Movimientos escritos antes de `52e3249` con cantidades redondeadas (si existieran en una base real) no se corrigen.
- `soloConExistencia` = existencia ≠ 0: un producto con existencia 0 y valor residual ≠ 0 queda fuera del filtro y de su
  `ValorTotal` (definir como existencia ≠ 0 o valor ≠ 0, o documentarlo en la página).
- El mensaje del trigger append-only responde "UPDATE no permitido" también a un `TRUNCATE`.
- ~~`Producto.CostoUnitario` puede quedar desfasado: solo se escribe al crear el producto (0) y lo reescribe la rutina de
  ajuste de costo (`AjusteCostoInventario`); entre ajustes no sigue a las entradas, y `RegistroMovimientosInventario` lo usa
  como costo de respaldo (salida sin existencia valorable). Pendiente de la Fase 3 que no se revisó en la Fase 5.~~
  **Resuelto en la Fase 8** (Task 8.3): se actualiza con cada movimiento y en el ajuste (V/Q de todo el libro; con Q ≤ 0
  el registro lo conserva y el ajuste deja el último promedio ajustado), y la migración `RecalcularCostoUnitario` lo
  recalculó en las bases existentes. Queda: una consulta de agregación extra por movimiento.

**Contabilidad**
- `PosteoAutomaticoCosto` sin implementar: el asiento de la factura no lleva costo de ventas/inventario; lo contabiliza el
  batch manual `POST api/contabilidad/postear-costo-inventario`, que hay que ejecutar para que el libro refleje el costo.
- El batch aborta con 500 ante una excepción inesperada en un grupo (los grupos ya confirmados quedan; es seguro
  reejecutarlo) y hace N+1 al derivador de cuentas por movimiento.
- `ClaveOrigen` del batch es la clave del grupo (producto × fecha), no el número de registro.
- `SetupsContableGeneral.CuentaAjusteInventarioId` y `CuentaDescuentoVentas` no se usan (descuento de línea neto).
- El `Down` de `AddLibroContable` borra el libro y no rearma el batch (`ImporteCostoPosteadoContabilidad` conserva lo
  contabilizado).
- Balance de comprobación: sin cierre de ejercicio (las cuentas de resultado acumulan), sin reglas de totalización, número y
  nombre actuales de la cuenta (una cuenta renumerada se ordena por su número nuevo).
- Guardas de uso check-then-act (grupos, cuentas, almacenes, socios): consultas sin bloqueo antes del borrado lógico; el
  posteo revalida bajo lock.

**Facturación y CxC**
- ~~**Notas de crédito sin implementar** (y ningún otro documento que revierta una factura posteada).~~ **Resuelto en la
  Fase 8** (Tasks 8.6-8.7): notas de crédito de venta ligadas a la factura. Quedan: la devolución se registra como entrada
  de tipo "Venta" con cantidad positiva (así se ve en movimientos de producto); un socio bloqueado para facturación impide
  postear la nota; las guardas de uso de grupos y cuentas no cuentan los documentos posteados (ni facturas ni notas); la
  ficha de la factura calcula lo acreditado con una llamada por nota (N+1).
- **Resuelto en la Fase 8** (Task 8.4): la regla de importe 0 (`factura.importe_cero` retirado; importe 0 solo con 100 %
  de descuento, precio > 0 obligatorio; factura de total 0 sin cliente ni asiento). Queda: la API conserva el precio por
  defecto del producto si no se envía (la UI lo exige).
- **Resuelto en la Fase 8** (Task 8.5): fechas de registro permitidas (rango general y excepciones por usuario) en
  diarios, facturas, notas de crédito, cobros y aplicaciones. Quedan: una aplicación sin fecha valida la fecha de hoy
  (UTC); una sesión de Blazor abierta antes del despliegue no tiene el claim `CanAdministrar` hasta volver a iniciar sesión;
  la excepción de un usuario borrado en Identity queda hasta que se elimina desde la página.
- **CxC congelada vs. vigente (M-7):** la factura congela la CxC de su grupo y el pago usa la del grupo vigente del socio;
  si el grupo cambia entre ambos, contabilidad queda con dos CxC de saldo contrario. Pendiente una **reclasificación de CxC**
  (asiento entre ambas cuentas al aplicar o al cambiar el grupo).
- Números de documento sin prefijo (`00000001` en `FV` y en `COBRO`; se distinguen por tipo de documento); la serie con
  huecos `FV-BORR` se serializa igual que las sin huecos; número de `COBRO` con la fecha de hoy.
- El registro de cobros propaga el Campo `Lineas[i]` de los errores de `IRegistroContable` (la factura ya no).
- El `Down` de `AddFacturasVentaYLibroClientes` borra facturas y libro de clientes sin revertir inventario ni contabilidad.
- Estado de cuenta: los restantes negativos van a "sin aplicar"; solo aparecen socios con movimientos a la fecha de corte;
  la tarjeta de saldo del cliente (vigente) y la tabla a una fecha de corte pueden diferir con movimientos de fecha futura.

**UI (Blazor Static SSR)**
- Duplicación entre páginas de vistas (`MovimientosProducto.razor`/`MovimientosValor.razor`; campos de línea y buscador de
  diarios y facturas); `Definido<TEnum>` duplicado (moverlo a `Application/Common`).
- Una página enorme en la URL en los listados anteriores a la Fase 7 muestra una página vacía (las vistas de la Fase 7
  saltan a la última; con cero resultados muestran "página N de 1").
- Selects cargados con `TamanoMaximo = 200` (almacenes) en lugar de buscador; búsqueda de productos en cada GET del
  borrador; mensajes "no encontrado" cuando lo que falló fue la carga; un 200 sin cuerpo en `ContabilidadApiClient` se
  muestra con el mensaje genérico; modo oscuro incompleto; texto (código o nombre) frente a select de producto (solo
  nombre) en las vistas de inventario.
- Filtro `registroId` oculto que se conserva con otros filtros en movimientos contables (asiento parcial).

**Rendimiento**
- Estado de cuenta de **toda la cartera** sin filtros ~220-250 ms con 1 003 clientes y 100 000 movimientos (objetivo 200 ms;
  ningún índice ayuda; con filtro 5-105 ms). Aceptado para un informe poco frecuente.
- Filtrar u ordenar productos por existencia ~150-175 ms con 30 000 productos / 100 000 movimientos; escalado lineal de
  existencias sin filtros.
- ~7 consultas por línea en la prevalidación del registro de diarios (lote ≤ 1000 líneas); N+1 en
  `RecalcularIvaLineasAsync`; serialización de todos los escritores de un libro por su serie de numeración.

**Pruebas**
- Tests que usan "hoy UTC" como fecha por defecto pueden fallar si cruzan la medianoche UTC durante la ejecución.
- El oráculo del test de estado de cuenta (`SaldoSqlAsync`) no es del todo independiente de la consulta probada (usar
  `SUM(ImporteOriginal)` con `FechaRegistro <= corte`); la consulta de control del test del balance tampoco del todo.
- Números de cuenta aleatorios en los tests contables; el test del redondeo con transferencia no ejecuta una segunda pasada.
- Los tests de integración requieren Docker (`DOCKER_CONTEXT=default`); la suite completa tarda ~16 min (~19 min tras la
  Fase 8).

**Código heredado**
- ~~Módulos de prueba `Entradas` y `AppSettings` (API, cliente y páginas Blazor) marcados `[Obsolete]` desde el Entregable 2:
  son el origen de los 6 avisos CS0618 de la build. Candidatos a retirar (con sus tests y rutas).~~ **Resuelto en la
  Fase 8** (Task 8.2): retirados código, API, páginas, tests y tablas; la build queda con 0 avisos.
- Residuales de la revisión de la tanda final: el test `CoherenciaVistasApiTests` fija valores esperados del lado de
  clientes pero del lado de inventario solo la existencia final (no el valor ni existencias intermedias): conviene fijar
  el valor final y alguna existencia intermedia. La API de cobros acepta una aplicación con fecha anterior a alguno de sus
  movimientos (`AplicarPagoCommandHandler` no valida la fecha); el estado de cuenta lo tolera con la regla del movimiento
  contrario, pero conviene decidir si se rechaza.
