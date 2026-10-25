# Alta rápida de facturas y notas de crédito — diseño

Fecha: 2026-10-01 · Rama: `feat/alta-rapida-documentos` (desde `main` 2021653, tras el merge de `feat/no-series`).

## Objetivo

Agilizar la creación de facturas de venta y notas de crédito. El usuario no debe gestionar un "borrador" como paso
propio: el documento se prepara en una sola página, se guarda solo y se registra con una acción. Las series de
numeración se toman de la configuración por tipo y no se eligen en el documento (salvo el Administrador); el almacén
queda fijo en el predeterminado.

Criterio de éxito: una factura de 2 líneas pasa de ~12 a ~7 cargas de página; una nota de crédito total sale desde la
factura en 2 clics más confirmación; ningún error de validación pierde lo capturado; la numeración sin huecos y el
motor de posteo no cambian.

## Decisiones del usuario

| # | Decisión |
|---|---|
| D1 | Un documento sin terminar se guarda "En preparación" y se retoma después (no hay envío único). |
| D2 | Las series las cambia solo el Administrador; el resto usa la configurada. |
| D3 | Campos fijos además de los ya derivados del cliente: **almacén** (siempre el predeterminado). Fechas, vencimiento y descripción siguen editables. |
| D4 | La nota de crédito nace desde la factura registrada con todas las líneas pendientes copiadas (total por defecto); se ajusta o quita líneas para una parcial. |
| D5 | Enfoque A: el borrador sigue existiendo internamente y se crea automáticamente con la primera línea; se descartan el formulario de envío único (B) y el cambio solo de textos (C). |

## Evaluaciones de partida

- Técnica (agente dotnet): recomendó A. Toda la resolución de series pasa por `NumeracionBorrador.NumerarBorradorAsync`;
  el posteo (`PostearFacturaVentaCommandHandler`, `PostearNotaCreditoVentaCommandHandler`) numera con
  `borrador.SerieRegistroId` en su transacción; no hace falta migración. B duplicaría validaciones y en Static SSR
  obliga a repintar N líneas en cada error.
- UX (ui-ux-pro-max + design-critique): fricciones principales — el borrador como paso visible, dos numeraciones y dos
  selectores de serie, tres acciones primarias compitiendo, "Liberar/Reabrir" y "Postear" como jerga, 4 entradas de
  menú para 2 documentos. Recomendó A con creación implícita en la primera línea.

## 1. Flujo de la factura

1. `/facturas-venta/nueva`: una sola página con el diseño del documento único actual.
   - Cliente: buscador GET + "Usar" (se mantiene `?socioId=` para el precargado desde el cliente).
   - Tarjeta de solo lectura (`<dl>`): facturar a, RNC/razón social, término de pago, moneda, almacén
     (predeterminado) y, dentro de "Detalles contables", los grupos contables.
   - Vencimiento: calculado del término y editable (como hoy).
   - Línea de numeración (`SerieNumeracionInfo`): "Se numerará con la serie FV · próximo FV-000123 (el número
     definitivo se asigna al registrar)".
   - Editables: fecha de registro y de documento (hoy por defecto), vencimiento y descripción.
   - Fila de alta de línea y totales al pie.
2. El primer "Agregar línea" envía cabecera + línea en un POST; el servidor crea el borrador y la línea en una
   transacción y redirige a `/facturas-venta/en-preparacion/{id}` (misma vista con el documento cargado). No se
   muestra número de borrador. Si la línea o la cabecera fallan, no se crea nada y el formulario conserva lo
   capturado.
3. Acciones:
   - Primaria "Registrar factura": confirmación con resumen (total, número de líneas, serie y próximo número, "se
     descontará el inventario y se generará el asiento; no se puede deshacer") con "Sí, registrar" / "Seguir
     editando". Usa el posteo existente. Resultado: "Factura FV-000123 registrada." con acceso a "Nueva factura".
   - Secundarias: "Guardar para después" (vuelve a la lista "En preparación") y "Descartar factura" (borra, con
     confirmación).
   - Se retiran de la UI "Liberar"/"Reabrir" (la API los conserva por compatibilidad; el posteo ya acepta Abierta y
     Liberada).
4. Series: la de preparación nunca se muestra. La de registro se muestra en solo lectura; el Administrador ve
   "Cambiar serie de registro" (el `SelectorSerie` actual) dentro de "Más opciones".
5. Sin configuración o con serie inactiva: aviso bloqueante antes de capturar ("No hay una serie configurada para
   facturas de venta…"), con enlace a `/configuracion/numeracion` si el usuario tiene `CanAdministrar` o "Pida a un
   administrador que la configure". "Registrar" queda deshabilitado con el motivo visible. El aviso de numeración
   (número de aviso alcanzado) se mantiene.

## 2. Notas de crédito y navegación

### Nota de crédito

1. Entrada principal: acción visible "Crear nota de crédito" en la factura registrada. Crea el borrador directamente
   con las series configuradas, `CopiarLineas = true` (todas las líneas pendientes con su cantidad acreditable) y
   "Devolver inventario" activado; abre `/notas-credito-venta/en-preparacion/{id}`.
2. Editor con el mismo diseño: solo lectura la factura origen, cliente, datos de facturación y series; editables las
   fechas, la descripción, "Devolver inventario" y por línea la cantidad (tope: acreditable) o "Quitar línea".
   Acciones "Registrar nota de crédito" (confirmación con total, factura origen, serie y "no se puede deshacer"),
   "Guardar para después" y "Descartar".
3. Entrada secundaria `/notas-credito-venta/nueva` (cliente → factura con pendiente), sin selectores de serie; al
   elegir la factura se crea la nota como en el punto 1.
4. El Administrador puede cambiar la serie de registro en "Más opciones".

### Navegación (grupo Facturación)

- El menú pasa de 4 a 2 entradas: "Facturas" y "Notas de crédito". Cada lista tiene pestañas "Registradas" y "En
  preparación (n)" (`?estado=preparacion`). La búsqueda global conserva las palabras clave "borrador" y "en
  preparación".
- Barra: "+ Nueva factura" / "+ Nueva nota de crédito". La lista "En preparación" muestra cliente, creado el, líneas,
  total y las acciones "Continuar" y "Descartar".
- Las rutas antiguas `/facturas-venta/borradores`, `/facturas-venta/borradores/{id}` y sus equivalentes de nota
  redirigen a las nuevas (enlaces guardados y el enlace del documento posteado siguen funcionando). El enlace "Ver
  preparación" de un documento registrado abre la vista de solo lectura.
- Textos de UI: "Postear" → "Registrar", "Borrador" → "En preparación". Los nombres internos y de API (`Borrador`,
  `postear`) no cambian.
- Accesibilidad: valores de solo lectura en `<dl>` (no `<input disabled>`); resumen de errores con `role="alert"` y
  enlaces a cada línea, enfocado tras la navegación mejorada con `Blazor.addEventListener('enhancedload', …)`;
  estados con insignias de texto; nombres accesibles en los botones de fila ("Eliminar línea 2").

### Documentos abandonados

No pueden existir documentos en preparación sin líneas (se crean con la primera). Los que tienen líneas quedan en "En
preparación" hasta registrarse o descartarse; no hay purga automática en esta fase.

## 3. API, reglas en el servidor y pruebas

### API y aplicación

1. Alta con primera línea: `POST api/facturas-venta/borradores` acepta una primera línea opcional. Cabecera y línea se
   guardan en una transacción (una llamada a `NumerarBorradorAsync` y la lógica de línea existente reutilizada, no
   duplicada). Si la línea falla, no se crea nada ni se consume número de borrador (rollback). Blazor siempre la
   envía; el alta sin líneas sigue disponible en la API por compatibilidad.
2. Series:
   - `SerieBorradorId`: siempre la configurada; si se envía → 400 `serie.no_editable` (para todos).
   - `SerieRegistroId` (alta y modificación): solo el Administrador (`CanAdministrar`); el resto → 400
     `serie.no_editable`.
   - El posteo conserva la serie congelada en el borrador y su revalidación (regla vigente).
3. Almacén: siempre el predeterminado, lo fija el servidor; un `AlmacenId` distinto → 400 `almacen.no_editable`.
4. Nota de crédito: `POST api/notas-credito-venta/borradores` desde una factura usa las series configuradas y
   `CopiarLineas = true` por defecto; mismas reglas de series que la factura. El ajuste de cantidad por línea usa el
   endpoint existente con su tope.
5. Listas: los listados de borradores exponen un conteo para las pestañas "En preparación (n)".
6. Sin migraciones: el esquema y la numeración sin huecos no cambian.

### Blazor

Rutas nuevas `/facturas-venta/en-preparacion/{id}` y `/notas-credito-venta/en-preparacion/{id}`, redirecciones desde
las antiguas, listas unificadas con pestañas, menú de 2 entradas, `SelectorSerie` solo para el Administrador y
`SerieNumeracionInfo` en solo lectura para el resto. Se respetan las reglas Static SSR del proyecto (formularios
siempre en el árbol, modelos antes del primer `await`, `NavigateTo` fuera del `try/catch`, `returnUrl` local
validado).

### Pruebas (TDD; pruebas Postgres serializadas)

- Handlers: alta con línea atómica y rollback con línea inválida; series rechazadas para no Administrador y aceptadas
  para el Administrador; almacén rechazado; nota total copia las líneas pendientes.
- API: códigos 400/403/409 y contratos.
- Páginas SSR: solo lectura en `<dl>`, vista Administrador frente a no Administrador, redirecciones, pestañas y
  contador, confirmación de registro, aviso bloqueante sin configuración.
- Actualizar los tests existentes que envían series o esperan "Crear borrador", "Postear" o "Liberar"
  (`DocumentoFacturaBorradorTests`, `DocumentoNotaBorradorTests`, `AccionesFacturasTests`, `SerieApiClientTests`,
  `FacturasVentaBorradoresApiTests`, `NotasCreditoVentaApiTests`, `NumeracionBorradorTests`).
- E2E: actualizar `09-documento-unico` y añadir `10-alta-rapida` (factura de 2 líneas y nota total, contando cargas
  de página).
- Puerta de cierre: suite completa, `dotnet build test.slnx -warnaserror`, `has-pending-model-changes` y E2E contra el
  stack reconstruido.

## Fuera de alcance

- Purga automática de documentos en preparación.
- Cambiar el motor de posteo, la numeración o el esquema.
- Renombrar entidades o endpoints internos.
- Registrar una nota de crédito total sin pasar por el editor.

## Resultado

La rama `feat/alta-rapida-documentos` entregó el alta rápida de facturas y notas de crédito tal como se especificó: factura en una sola página que se crea con su primera línea en una transacción, documentos "En preparación" con registro confirmado, descarte y "Guardar para después", nota de crédito creada desde la factura registrada con sus líneas pendientes, series y almacén fijados por la configuración (solo el Administrador cambia la serie de registro), menú de Facturación con dos entradas y pestañas "Registradas" / "En preparación (n)", y resumen de errores accesible. Sin migraciones ni cambios en el motor de posteo ni en la numeración. Suite xUnit de 2114 tests en verde con 0 avisos y E2E 06, 09 y 10 en verde. El detalle (commits, decisiones, revisión final y residuales) está en la sección "Resultado de feat/alta-rapida-documentos" del plan: `docs/superpowers/plans/2026-10-01-alta-rapida-documentos.md`.
