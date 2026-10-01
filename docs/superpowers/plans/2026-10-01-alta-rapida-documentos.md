# Alta rápida de facturas y notas de crédito · Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** que una factura de venta se prepare en una sola página (cliente, datos derivados en solo lectura, primera línea) que crea el
documento con la primera línea, se guarde sola "En preparación" y se registre con una acción confirmada; que la nota de crédito nazca
desde la factura registrada con todas las líneas pendientes; series fijas por configuración (solo el Administrador cambia la de
registro), almacén fijo en el predeterminado y un menú de Facturación con 2 entradas y pestañas "Registradas" / "En preparación (n)".

**Architecture:** el borrador sigue existiendo internamente (enfoque A del spec): `NumeracionBorrador` deja de aceptar la serie de
borradores y valida quién elige la de registro (`IUsuarioActual.EsAdministrador`, nuevo); el alta del borrador de factura acepta una
primera línea opcional que se guarda en la MISMA transacción con la lógica de alta de línea extraída a
`LineaFacturaVentaBorradorReglas.AgregarAsync` (rollback = ni documento ni número). El almacén de la cabecera lo fija el servidor
(`almacen.no_editable`). Las listas exponen el total de cada documento en preparación y Blazor cuenta con el `Total` del listado
existente (sin endpoint nuevo). En Blazor (Static SSR) aparecen las rutas `/facturas-venta/en-preparacion/{id}` y
`/notas-credito-venta/en-preparacion/{id}` (las antiguas redirigen), componentes compartidos de solo lectura y confirmación, y las
listas unificadas con pestañas. El motor de posteo, la numeración sin huecos y el esquema no cambian.

**Tech Stack:** .NET 10, MediatR 13, EF Core 10 + Npgsql, Dapper, PostgreSQL 17, Blazor Static SSR + Tailwind Play CDN, xUnit + Moq +
`WebApplicationFactory` + `PostgresTestFixture` (contenedor `opensource1-tests-postgres`, puerto 65432), Node + `@playwright/test` (E2E en `tests/e2e`).

**Spec:** [`docs/superpowers/specs/2026-10-01-alta-rapida-documentos-design.md`](../specs/2026-10-01-alta-rapida-documentos-design.md)
(vinculante). Convenciones: `CLAUDE.md`; patrón de página y rulings R1–R17 / NS1–NS22 / F1–F20 de
[`2026-09-29-no-series.md`](2026-09-29-no-series.md) (sección "Resultado de feat/no-series"), que siguen vigentes salvo donde este
plan los sustituye (tabla "Decisiones tomadas al planificar").

## Global Constraints

- Rama `feat/alta-rapida-documentos` (desde `f79a44a`); no cambiar de rama. Commits LOCALES solo con la autorización explícita del usuario (CLAUDE.md), mensaje en español y el trailer de atribución de la sesión que ejecute; nunca merge ni push.
- `README.md` ya está actualizado y commiteado en esta rama (commit de documentación previo a R1); solo R10 lo edita. `git add` siempre con rutas explícitas, nunca `git add -A`/`.`.
- TFM `net10.0`; `PackageReference` directo por proyecto, sin Central Package Management; **sin paquetes NuGet nuevos**.
- **Static SSR solamente**: sin `@rendermode`, `@onclick`, `@bind` ni manejadores interactivos. GET/POST con `EditForm` + `FormName`, `[SupplyParameterFromForm]`, `[SupplyParameterFromQuery]` (sin enums: `int?`/`string`), antiforgery, clientes tipados.
- **JWT solo en el servidor** (cookie HttpOnly + sesión del servidor); ningún cambio de autenticación ni de tokens en el navegador.
- **No tocar `appsettings*.json`**. **Sin secretos** en código, planes, specs ni commits (la contraseña E2E solo vía `bash scripts/con-credenciales.sh`, que la lee de `.env`).
- **Sin migraciones**: no se tocan `Data/Migrations/**` ni `ApplicationDbContext.cs`; `dotnet ef migrations has-pending-model-changes -p src/OpenSource1.Infrastructure -s src/OpenSource1.Api --context ApplicationDbContext` debe seguir diciendo "No changes have been made to the model since the last migration."
- **Nombres internos y de API sin cambios**: entidades, comandos, DTOs y rutas `api/facturas-venta/borradores…`, `api/notas-credito-venta/borradores…`, `…/postear`; los `FormName` existentes (`confirm-postear`, `guardar-cabecera`, `guardar-cabecera-nota`, `add-linea`, `update-linea`, `delete-linea`, `delete-borrador`, `delete-borrador-nota`, `reabrir`, `liberar`, `crear-nota`, `copiar-borrador`, `nueva-nota-socio`) se conservan. Solo cambian los textos de la UI.
- **Textos de UI exactamente como en el spec**: "Postear" → "Registrar", "Borrador" → "En preparación" en toda la UI de Facturación; "Registrar factura", "Registrar nota de crédito", "Sí, registrar", "Seguir editando", "Guardar para después", "Descartar factura", "Descartar", "Crear nota de crédito", "Continuar", "Quitar línea", "Registradas", "En preparación (n)", "+ Nueva factura", "+ Nueva nota de crédito" (`PageToolbar` antepone el "+ "), "Facturas", "Notas de crédito", "Más opciones", "Cambiar serie de registro", "Detalles contables", "Ver preparación", "Nueva factura", "Factura FV-000123 registrada." (con el número real), "Se numerará con la serie FV · próximo FV-000123 (el número definitivo se asigna al registrar)" (con la serie y el número reales), "se descontará el inventario y se generará el asiento; no se puede deshacer", "No hay una serie configurada para facturas de venta…", "Pida a un administrador que la configure".
- Onion + MediatR + `Result`/`Error` (`.no_encontrado`→404, `.conflicto`→409, `.bloqueado`→422, resto 400). Códigos nuevos: **`serie.no_editable`** y **`almacen.no_editable`** (ambos 400). La API devuelve los mensajes agrupados por campo (no el código): los tests de API comprueban campo + fragmento del mensaje.
- Numeración sin huecos y motor de posteo intactos: no se modifican `PostearFacturaVentaCommandHandler`, `PostearNotaCreditoVentaCommandHandler`, `GeneradorNumeroDocumento` ni `IGeneradorNumeroDocumento`; el posteo sigue usando `borrador.SerieRegistroId` y admite Abierta y Liberada.
- **Tests contra Postgres serializados** (los usa también la otra sesión): antes de la primera ejecución `mkdir -p /home/ray/test/.superpowers/sdd/alta-rapida/tmp`; cualquier `dotnet test` que toque una clase `[Collection(PostgresCollection.Name)]` (o la suite completa) se ejecuta como `flock /home/ray/test/.superpowers/sdd/alta-rapida/tmp/postgres-tests.lock env DOCKER_CONTEXT=default dotnet test …`. `DOCKER_CONTEXT=default` solo como variable de entorno: nunca `docker context use`. Los tests sin Docker (Blazor SSR con `BlazorSsrFactory`, componentes con `RenderizadorComponentes`, unitarios con Moq) se ejecutan sin `flock`.
- Tests de páginas: `BlazorSsrFactory` + `HtmlSsr` (aserciones sobre HTML decodificado) + `FormulariosSsr`; POST forzado sin permiso con `app.Cliente("Ejecutor", permisos: "CanConsult")` (NS21); mocks asíncronos con `ReturnsAsync` siempre.
- Patrón de página (rulings R1–R17 y NS17): formularios de acción SIEMPRE en el árbol (vacíos con el mismo `FormName` si no se muestran), modelos `X ??= new()` antes del primer `await`, `NavigateTo` FUERA del `try/catch` (R17), `returnUrl` local validado con `RetornoLocal`.
- JavaScript: re-inicialización tras la navegación mejorada SOLO con `window.Blazor.addEventListener('enhancedload', …)`; nunca `document.addEventListener('enhancedload', …)` en código nuevo.
- Suite de partida: anotar el total en G1 (README: 2004 tests); build con **0 avisos**. Cada fase termina con `dotnet build test.slnx -warnaserror` y la suite completa verde.
- Fuera de alcance: purga automática de documentos en preparación; cambios del motor de posteo, de la numeración o del esquema; renombrar entidades o endpoints internos; registrar una nota total sin pasar por el editor.

## Review Focus

1. **Error en la primera línea del alta rápida** (precio vacío o 0, producto bloqueado, cuenta sin posteo directo, cantidad con más decimales de los admitidos): no se crea ningún documento, no se consume el número de la serie de borradores y la página `/facturas-venta/nueva` vuelve a pintar el cliente, las fechas, la descripción y la línea tal como se escribieron, con el error en el resumen `role="alert"`. Tests en R2 (`AltaConPrimeraLineaInvalida_NoCreaNada_NiConsumeNumero`, API `AltaRapida_PrimeraLineaInvalida_400EnPrimeraLinea_YNoCreaBorrador`) y R6 (`Nueva_ErrorDeLaApi_ConservaLoCapturado_YNoNavega`).
2. **Un no Administrador que fuerza la serie** (manipula el HTML o llama a la API con `serieRegistroId`/`serieBorradorId`): la API responde 400 `serie.no_editable` en el campo de la serie; la página de un no Administrador nunca reenvía una serie aunque el POST la traiga, y la del Administrador la envía desde "Más opciones". Tests en R1 (`Series_SoloElAdministradorEligeLaDeRegistro_LaDeBorradorNuncaSeElige_400_403_409`) y R6 (`EnPreparacion_NoAdministrador_IgnoraLaSerieForzada_YAdministradorLaEnvia`).
3. **La serie de registro deja de numerar después de crear el documento** (desactivada, agotada o cambiada de tipo): "Registrar factura" queda deshabilitado con el motivo visible y un POST forzado de `confirm-postear` muestra el 400 real de la API sin escribir nada. Test en R6 (`EnPreparacion_SerieQueYaNoNumera_RegistrarDeshabilitadoConMotivo_YPostForzadoMuestraLaApi`).
4. **Documentos heredados en estado Liberada** (creados antes de esta versión): se abren en `/facturas-venta/en-preparacion/{id}`, se pueden registrar tal cual y "Volver a editar" los reabre; nunca se ofrece "Liberar". Test en R6 (`EnPreparacion_Liberada_RegistrarYVolverAEditar_SinLiberar`).
5. **"Crear nota de crédito" pulsado dos veces o con una nota ya en preparación para esa factura**: la factura registrada ofrece "Continuar nota de crédito" (enlace a la que ya existe) en lugar de crear otra; una factura sin pendiente no ofrece la acción y un POST forzado muestra el 400 real (`nota_credito.factura_sin_pendiente`). Test en R7 (`FacturaRegistrada_ConNotaEnPreparacion_OfreceContinuar_SinCrearOtra`, `FacturaRegistrada_SinPendiente_PostForzadoMuestraLaApi`).

---

## Estructura de archivos

**Application — `src/OpenSource1.Application/`**

| Archivo | Responsabilidad | Task |
|---|---|---|
| `Services/Inventario/IUsuarioActual.cs` | `bool EsAdministrador` (rol Administrador = lo que la API exige para `CanAdministrar`). | R1 |
| `Data/NumeracionBorrador.cs` | `ValidarSeriesSolicitadas` (`serie.no_editable`) y `NumerarBorradorAsync` sin serie de borradores elegible. | R1 |
| `Features/FacturasVenta/Borradores/Handlers/CreateFacturaVentaBorradorCommandHandler.cs`, `UpdateFacturaVentaBorradorCommandHandler.cs` | Reglas de series (R1); primera línea y almacén fijo (R2). | R1, R2 |
| `Features/FacturasVenta/Borradores/Commands/FacturaVentaBorradorCommands.cs` | `PrimeraLineaFacturaVenta` y parámetro `PrimeraLinea`. | R2 |
| `Features/FacturasVenta/Borradores/LineaFacturaVentaBorradorReglas.cs` | `AgregarAsync` (alta de línea sin commit, compartida). | R2 |
| `Features/FacturasVenta/Borradores/FacturaVentaBorradorReglas.cs` | `FacturaVentaBorradorErrores.AlmacenNoEditable`. | R2 |
| `Features/FacturasVenta/Borradores/Handlers/CreateLineaFacturaVentaBorradorCommandHandler.cs` | Usa `AgregarAsync`. | R2 |
| `Features/FacturasVenta/Copia/CopiarFacturaABorradorCommandHandler.cs` | Firma nueva de `NumerarBorradorAsync`. | R1 |
| `Features/NotasCreditoVenta/Borradores/Handlers/CreateNotaCreditoVentaBorradorCommandHandler.cs`, `UpdateNotaCreditoVentaBorradorCommandHandler.cs` | Reglas de series. | R1 |
| `Features/FacturasVenta/Borradores/Dtos/FacturaVentaBorradorResponse.cs`, `Features/NotasCreditoVenta/Borradores/Dtos/NotaCreditoVentaBorradorResponse.cs` | `ImporteTotal` (vista previa) y códigos de grupos. | R4 |

**Infrastructure / Api**

| Archivo | Responsabilidad | Task |
|---|---|---|
| `src/OpenSource1.Infrastructure/Services/Inventario/UsuarioActualSistema.cs` | `EsAdministrador => false`. | R1 |
| `src/OpenSource1.Api/Infrastructure/UsuarioActualHttp.cs` | `EsAdministrador` = `IsInRole("Administrador")`. | R1 |
| `src/OpenSource1.Infrastructure/Data/Queries/DapperFacturaVentaBorradorReadRepository.cs`, `DapperNotaCreditoVentaBorradorReadRepository.cs` | `ImporteTotal` y códigos de grupos en SQL. | R4 |
| `src/OpenSource1.Infrastructure/Data/Queries/DapperBusquedaGlobalRepository.cs` | Títulos y rutas "en preparación" de la búsqueda global. | R8 |
| `src/OpenSource1.Api/Controllers/FacturasVentaController.cs` | `PrimeraLinea` en `CreateFacturaVentaBorradorRequest`. | R2 |
| `src/OpenSource1.Api/Controllers/NotasCreditoVentaController.cs` | `CopiarLineas`/`DevolverInventario` por defecto `true`. | R3 |

**Blazor — `src/OpenSource1.Blazor/`**

| Archivo | Responsabilidad | Task |
|---|---|---|
| `Services/IFacturaVentaApiClient.cs`, `FacturaVentaApiClient.cs` | `PrimeraLinea` en `BorradorCabeceraInput` (R2); `ContarEnPreparacionAsync` (R4). | R2, R4 |
| `Services/INotaCreditoVentaApiClient.cs`, `NotaCreditoVentaApiClient.cs` | `ContarEnPreparacionAsync`. | R4 |
| `Components/Documento/DocumentoVistas.cs` | `DatoDerivado`, `NumeracionDocumento`, `AccionCabecera` (R5); `RutasPreparacion` (R6). | R5, R6 |
| `Components/Documento/DatosDerivados.razor`, `NumeracionRegistro.razor`, `AvisoSinSerie.razor`, `ConfirmarRegistro.razor`, `ResumenErrores.razor` | Componentes compartidos nuevos. | R5 |
| `Components/Documento/SerieNumeracionInfo.razor`, `DocumentoCabeceraEditable.razor` | Texto "Se numerará con…" y botones propios de la tarjeta. | R5 |
| `wwwroot/app.errores.js`, `Components/App.razor` | Foco en el resumen de errores (carga y `enhancedload`). | R5 |
| `Components/LineaFacturaForm.cs`, `AltaFacturaForm.cs`, `LineaFacturaCampos.razor`, `CabeceraFacturaFields.razor` | Formulario del alta rápida y campos de línea compartidos. | R6 |
| `Components/Pages/FacturaVentaNueva.razor`, `FacturaVentaBorrador.razor`, `FacturaVentaBorradorRedireccion.razor`, `FacturaVentaBorradorCabecera.razor`, `FacturasVentaBorradores.razor` (solo `BorradorForm`) | Alta rápida, documento en preparación y redirecciones de la factura. | R6 |
| `Components/Pages/FacturasVenta.razor` | Mensaje de registro (R6); "Crear nota de crédito" (R7); pestañas y lista en preparación (R8). | R6, R7, R8 |
| `Components/Pages/NotaCreditoVentaBorrador.razor`, `NotaCreditoNueva.razor`, `NotaCreditoVentaBorradorRedireccion.razor`, `NotaCreditoBorradorCabecera.razor`, `NotasCreditoVentaBorradores.razor` (solo `CabeceraNotaForm`), `NotasCreditoVenta.razor` | Nota en preparación, alta secundaria y redirecciones. | R7 |
| `Components/Documento/ListaFacturasEnPreparacion.razor`, `ListaNotasEnPreparacion.razor`, `PestanasDocumento.razor` | Pestañas y listas "En preparación". | R8 |
| `Navigation/CatalogoModulos.cs`, `Navigation/IndicadoresModulos.cs`, `Components/Pages/Buscar.razor`, `Components/VentasOpciones.cs` | Menú de 2 entradas, indicadores, búsqueda y textos de estado. | R8 |

**Tests — `tests/OpenSource1.SmokeTests/`** (nombres en cada task) y **E2E** `tests/e2e/specs/09-documento-unico.spec.ts`,
`tests/e2e/specs/10-alta-rapida.spec.ts` (R9).

---

## Orden de ejecución

```text
Fase 1 (servidor)                      Fase 2 (Blazor Static SSR)                  Fase 3
─────────────────                      ──────────────────────────                  ──────
R1 ─► R2 ─► R3 ─► R4 ─► G1 ─► R5 ─► R6 ─► R7 ─► R8 ─► G2 ─► R9 ─► R10
```

Todas en serie: R2–R4 cambian los mismos handlers, el mismo controlador y los mismos archivos de tests que R1; R6–R8 comparten
`FacturasVenta.razor`, `DocumentoVistas.cs` y los tests de páginas de documentos. R5 solo depende de R4 por los DTO
(`DatoDerivado` no los usa): si se quiere paralelizar, R5 puede correr en un worktree en paralelo con R2–R4 sin tocar ningún archivo
de Application/Infrastructure/Api ni de `Services/**`, y se integra antes de R6.

---
## Fase 1 — Servidor

### Task R1: Reglas de series en `NumeracionBorrador` y en sus llamadores (`serie.no_editable`)

**Grupo de paralelismo:** Fase 1, serie (primera). Tests con Postgres bajo `flock`.

**Files:**
- Modify: `src/OpenSource1.Application/Services/Inventario/IUsuarioActual.cs`
- Modify: `src/OpenSource1.Api/Infrastructure/UsuarioActualHttp.cs`
- Modify: `src/OpenSource1.Infrastructure/Services/Inventario/UsuarioActualSistema.cs`
- Modify: `src/OpenSource1.Application/Data/NumeracionBorrador.cs`
- Modify: `src/OpenSource1.Application/Features/FacturasVenta/Borradores/Handlers/CreateFacturaVentaBorradorCommandHandler.cs`
- Modify: `src/OpenSource1.Application/Features/FacturasVenta/Borradores/Handlers/UpdateFacturaVentaBorradorCommandHandler.cs`
- Modify: `src/OpenSource1.Application/Features/FacturasVenta/Copia/CopiarFacturaABorradorCommandHandler.cs`
- Modify: `src/OpenSource1.Application/Features/NotasCreditoVenta/Borradores/Handlers/CreateNotaCreditoVentaBorradorCommandHandler.cs`
- Modify: `src/OpenSource1.Application/Features/NotasCreditoVenta/Borradores/Handlers/UpdateNotaCreditoVentaBorradorCommandHandler.cs`
- Modify: `src/OpenSource1.Application/Features/FacturasVenta/Borradores/Commands/FacturaVentaBorradorCommands.cs`, `src/OpenSource1.Application/Features/NotasCreditoVenta/Borradores/Commands/NotaCreditoVentaBorradorCommands.cs` (solo los `<summary>`)
- Modify: `tests/OpenSource1.SmokeTests/TestInfrastructure/FechasRegistroPrueba.cs` (`Usuario` con `EsAdministrador`)
- Test: `tests/OpenSource1.SmokeTests/Application/Data/NumeracionBorradorTests.cs` (reescrito)
- Test: `tests/OpenSource1.SmokeTests/Api/UsuarioActualHttpTests.cs`
- Test: `tests/OpenSource1.SmokeTests/Features/FacturasVenta/PostearFacturaVentaTests.cs` (sección "Series del borrador")
- Test: `tests/OpenSource1.SmokeTests/Features/NotasCreditoVenta/PostearNotaCreditoVentaTests.cs` (sección de series)
- Test: `tests/OpenSource1.SmokeTests/Api/FacturasVentaBorradoresApiTests.cs`

**Interfaces:**
- Consumes: `IGeneradorNumeroDocumento.SerieConfiguradaAsync/ValidarSerieAsync/SiguienteAsync`, `SeriesBorradorNumeradas`, `ApplicationRoles.Administrator`, `SeriesPrueba`, `FechasRegistroPrueba`.
- Produces (R2, R3, R6, R7):
  - `IUsuarioActual.EsAdministrador : bool` (Http = rol `Administrador`; sistema = `false`).
  - `NumeracionBorrador.CodigoNoEditable = "serie.no_editable"`.
  - `static Error? NumeracionBorrador.ValidarSeriesSolicitadas(Guid? serieBorradorId, Guid? serieRegistroId, bool esAdministrador)` → `SerieBorradorId` enviada = error para todos; `SerieRegistroId` enviada sin ser Administrador = error; si no, `null`.
  - `Task<Result<SeriesBorradorNumeradas>> NumerarBorradorAsync(this IGeneradorNumeroDocumento, TipoDocumentoSerie tipoBorrador, TipoDocumentoSerie tipoRegistro, Guid? serieRegistroId, DateOnly fecha, CancellationToken)` (la de borradores SIEMPRE la configurada).
  - Los cinco handlers inyectan `IUsuarioActual usuario` y rechazan antes de abrir la transacción (no se consume número).
  - `FechasRegistroPrueba.Usuario(Guid? Id, bool EsAdministrador = false)` para los tests.

- [ ] **Step 1: Tests unitarios del helper (fallan: firma y método inexistentes)**

Reemplazar todo `tests/OpenSource1.SmokeTests/Application/Data/NumeracionBorradorTests.cs` por:

```csharp
using Moq;
using OpenSource1.Application.Data;
using OpenSource1.Core.Common;
using OpenSource1.Core.Enums;

namespace OpenSource1.SmokeTests.Application.Data;

/// <summary>
/// Helper de alta de borradores con series (ruling F10; spec alta rápida): la serie de borradores es SIEMPRE la configurada; la de
/// registro, la configurada o la que elige el Administrador; la de registro se valida antes de numerar y los errores del motor se
/// devuelven en el campo de la serie afectada. <see cref="NumeracionBorrador.ValidarSeriesSolicitadas"/> decide quién puede enviar series.
/// </summary>
public class NumeracionBorradorTests
{
    private static readonly DateOnly Hoy = new(2026, 9, 30);
    private static readonly Guid Borrador = Guid.NewGuid();
    private static readonly Guid Registro = Guid.NewGuid();

    [Fact]
    public async Task SinSerieDeRegistro_UsaLasConfiguradas_ValidaLaDeRegistro_YNumeraConLaDeBorrador()
    {
        var motor = new Mock<IGeneradorNumeroDocumento>(MockBehavior.Strict);
        motor.Setup(m => m.SerieConfiguradaAsync(TipoDocumentoSerie.BorradorFacturaVenta, default)).ReturnsAsync(Result<Guid>.Exito(Borrador));
        motor.Setup(m => m.SerieConfiguradaAsync(TipoDocumentoSerie.FacturaVenta, default)).ReturnsAsync(Result<Guid>.Exito(Registro));
        motor.Setup(m => m.ValidarSerieAsync(Registro, TipoDocumentoSerie.FacturaVenta, default)).ReturnsAsync(Result.Exito());
        motor.Setup(m => m.SiguienteAsync(Borrador, TipoDocumentoSerie.BorradorFacturaVenta, Hoy, default))
            .ReturnsAsync(Result<NumeroGenerado>.Exito(new NumeroGenerado("FV-BORR-9", null)));

        var resultado = await motor.Object.NumerarBorradorAsync(
            TipoDocumentoSerie.BorradorFacturaVenta, TipoDocumentoSerie.FacturaVenta, null, Hoy);

        Assert.Equal(new SeriesBorradorNumeradas(Borrador, Registro, new NumeroGenerado("FV-BORR-9", null)), resultado.Valor);
    }

    [Fact]
    public async Task ConSerieDeRegistroElegida_NoLeeSuConfiguracion_PeroLaDeBorradoresSiempreEsLaConfigurada()
    {
        var motor = new Mock<IGeneradorNumeroDocumento>(MockBehavior.Strict);
        motor.Setup(m => m.SerieConfiguradaAsync(TipoDocumentoSerie.BorradorNotaCreditoVenta, default)).ReturnsAsync(Result<Guid>.Exito(Borrador));
        motor.Setup(m => m.ValidarSerieAsync(Registro, TipoDocumentoSerie.NotaCreditoVenta, default)).ReturnsAsync(Result.Exito());
        motor.Setup(m => m.SiguienteAsync(Borrador, TipoDocumentoSerie.BorradorNotaCreditoVenta, Hoy, default))
            .ReturnsAsync(Result<NumeroGenerado>.Exito(new NumeroGenerado("NC-BORR-1", null)));

        var resultado = await motor.Object.NumerarBorradorAsync(
            TipoDocumentoSerie.BorradorNotaCreditoVenta, TipoDocumentoSerie.NotaCreditoVenta, Registro, Hoy);

        Assert.Equal((Borrador, Registro, "NC-BORR-1"), (resultado.Valor.SerieBorradorId, resultado.Valor.SerieRegistroId, resultado.Valor.Numero.Numero));
        motor.Verify(m => m.SerieConfiguradaAsync(TipoDocumentoSerie.NotaCreditoVenta, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SerieRegistroInvalida_ErrorEnSerieRegistroId_YNoNumera()
    {
        var motor = new Mock<IGeneradorNumeroDocumento>(MockBehavior.Strict);
        motor.Setup(m => m.SerieConfiguradaAsync(TipoDocumentoSerie.BorradorFacturaVenta, default)).ReturnsAsync(Result<Guid>.Exito(Borrador));
        motor.Setup(m => m.ValidarSerieAsync(Registro, TipoDocumentoSerie.FacturaVenta, default))
            .ReturnsAsync(Result.Fallo(new Error("numeracion.serie_inactiva", "Inactiva.", "SerieId")));

        var resultado = await motor.Object.NumerarBorradorAsync(
            TipoDocumentoSerie.BorradorFacturaVenta, TipoDocumentoSerie.FacturaVenta, Registro, Hoy);

        Assert.Equal(("numeracion.serie_inactiva", "SerieRegistroId"), (resultado.Errores[0].Codigo, resultado.Errores[0].Campo));
        motor.Verify(m => m.SiguienteAsync(It.IsAny<Guid>(), It.IsAny<TipoDocumentoSerie>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SerieDeBorradoresConfiguradaQueNoNumera_ErrorEnSerieBorradorId()
    {
        var motor = new Mock<IGeneradorNumeroDocumento>(MockBehavior.Strict);
        motor.Setup(m => m.SerieConfiguradaAsync(TipoDocumentoSerie.BorradorFacturaVenta, default)).ReturnsAsync(Result<Guid>.Exito(Borrador));
        motor.Setup(m => m.ValidarSerieAsync(Registro, TipoDocumentoSerie.FacturaVenta, default)).ReturnsAsync(Result.Exito());
        motor.Setup(m => m.SiguienteAsync(Borrador, TipoDocumentoSerie.BorradorFacturaVenta, Hoy, default))
            .ReturnsAsync(Result<NumeroGenerado>.Fallo(new Error("numeracion.tipo_incorrecto", "Otro tipo.", "SerieId")));

        var resultado = await motor.Object.NumerarBorradorAsync(
            TipoDocumentoSerie.BorradorFacturaVenta, TipoDocumentoSerie.FacturaVenta, Registro, Hoy);

        Assert.Equal(("numeracion.tipo_incorrecto", "SerieBorradorId"), (resultado.Errores[0].Codigo, resultado.Errores[0].Campo));
    }

    [Fact]
    public async Task SinConfiguracionDelTipoDeBorrador_TalCual_YNoValidaNiNumera()
    {
        var motor = new Mock<IGeneradorNumeroDocumento>(MockBehavior.Strict);
        motor.Setup(m => m.SerieConfiguradaAsync(TipoDocumentoSerie.BorradorNotaCreditoVenta, default))
            .ReturnsAsync(Result<Guid>.Fallo(new Error("numeracion.sin_configuracion", "Sin serie.", "TipoDocumento")));

        var resultado = await motor.Object.NumerarBorradorAsync(
            TipoDocumentoSerie.BorradorNotaCreditoVenta, TipoDocumentoSerie.NotaCreditoVenta, Registro, Hoy);

        Assert.Equal(("numeracion.sin_configuracion", "TipoDocumento"), (Assert.Single(resultado.Errores).Codigo, resultado.Errores[0].Campo));
        motor.Verify(m => m.ValidarSerieAsync(It.IsAny<Guid>(), It.IsAny<TipoDocumentoSerie>(), It.IsAny<CancellationToken>()), Times.Never);
        motor.Verify(m => m.SiguienteAsync(It.IsAny<Guid>(), It.IsAny<TipoDocumentoSerie>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ErrorSinCampo_SinTransaccion_PasaTalCual()
    {
        var motor = new Mock<IGeneradorNumeroDocumento>(MockBehavior.Strict);
        motor.Setup(m => m.SerieConfiguradaAsync(TipoDocumentoSerie.BorradorFacturaVenta, default)).ReturnsAsync(Result<Guid>.Exito(Borrador));
        motor.Setup(m => m.ValidarSerieAsync(Registro, TipoDocumentoSerie.FacturaVenta, default)).ReturnsAsync(Result.Exito());
        motor.Setup(m => m.SiguienteAsync(Borrador, TipoDocumentoSerie.BorradorFacturaVenta, Hoy, default))
            .ReturnsAsync(Result<NumeroGenerado>.Fallo(new Error("numeracion.sin_transaccion", "Sin transacción.")));

        var resultado = await motor.Object.NumerarBorradorAsync(
            TipoDocumentoSerie.BorradorFacturaVenta, TipoDocumentoSerie.FacturaVenta, Registro, Hoy);

        Assert.Equal(new Error("numeracion.sin_transaccion", "Sin transacción."), Assert.Single(resultado.Errores));
    }

    [Theory]
    [InlineData(true, false, false, "SerieBorradorId")]
    [InlineData(true, false, true, "SerieBorradorId")]
    [InlineData(false, true, false, "SerieRegistroId")]
    [InlineData(false, true, true, null)]
    [InlineData(false, false, false, null)]
    public void ValidarSeriesSolicitadas_LaDeBorradoresNuncaYLaDeRegistroSoloElAdministrador(
        bool conSerieBorrador, bool conSerieRegistro, bool esAdministrador, string? campo)
    {
        var error = NumeracionBorrador.ValidarSeriesSolicitadas(
            conSerieBorrador ? Borrador : null, conSerieRegistro ? Registro : null, esAdministrador);

        Assert.Equal(campo, error?.Campo);
        if (error is { } e)
        {
            Assert.Equal(NumeracionBorrador.CodigoNoEditable, e.Codigo);
        }
    }
}
```

- [ ] **Step 2: Ejecutarlos (fallan al compilar)**

Run: `dotnet test tests/OpenSource1.SmokeTests --filter "FullyQualifiedName~NumeracionBorradorTests"`
Expected: FAIL — `CS1501`/`CS0117` ("ValidarSeriesSolicitadas" y "CodigoNoEditable" no existen; `NumerarBorradorAsync` no tiene la sobrecarga de 4 argumentos).

- [ ] **Step 3: `IUsuarioActual.EsAdministrador` y sus tres implementaciones**

`src/OpenSource1.Application/Services/Inventario/IUsuarioActual.cs`:

```csharp
namespace OpenSource1.Application.Services.Inventario;

/// <summary>
/// Usuario en cuyo nombre se escribe el libro (<c>CreatedBy</c>/<c>UsuarioId</c>). En la API sale de los claims del
/// JWT; fuera de una petición HTTP (tests, procesos batch) vale <c>"system"</c> sin id.
/// </summary>
public interface IUsuarioActual
{
    string Nombre { get; }
    Guid? Id { get; }

    /// <summary>
    /// El usuario tiene el rol Administrador, que es lo que la API exige para <c>CanAdministrar</c> (spec alta rápida: solo él
    /// elige la serie de registro). Fuera de una petición HTTP es <see langword="false"/>: un proceso del sistema no elige series.
    /// </summary>
    bool EsAdministrador { get; }
}
```

`src/OpenSource1.Api/Infrastructure/UsuarioActualHttp.cs`: añadir `using OpenSource1.Application.Security;` y, tras `Id`:

```csharp
    /// <summary>Rol <see cref="ApplicationRoles.Administrator"/> en el JWT (la política <c>CanAdministrar</c> exige ese rol).</summary>
    public bool EsAdministrador => Usuario?.IsInRole(ApplicationRoles.Administrator) == true;
```

`src/OpenSource1.Infrastructure/Services/Inventario/UsuarioActualSistema.cs`: añadir `public bool EsAdministrador => false;`.

`tests/OpenSource1.SmokeTests/TestInfrastructure/FechasRegistroPrueba.cs`: cambiar el record por

```csharp
    public sealed record Usuario(Guid? Id, bool EsAdministrador = false) : IUsuarioActual
    {
        public string Nombre => Id is null ? "system" : $"usuario-{Id:N}"[..20];
    }
```

(`PostearLoteDiarioCommandHandlerTests` usa `Mock.Of<IUsuarioActual>()`: sin cambios, `EsAdministrador` vale `false`.)

- [ ] **Step 4: `NumeracionBorrador` sin serie de borradores elegible y con la validación de quién envía series**

Reemplazar la clase `NumeracionBorrador` de `src/OpenSource1.Application/Data/NumeracionBorrador.cs` (el record `SeriesBorradorNumeradas` no cambia) por:

```csharp
/// <summary>
/// Alta de borradores con series (spec no-series, ruling F10; spec alta rápida): el único punto que resuelve las series de un
/// borrador nuevo y le da número. Lo usan las altas de borradores de factura y de nota y la copia de factura a borrador. La serie de
/// borradores es SIEMPRE la configurada del tipo; la de registro, la configurada o la que elija el Administrador
/// (<see cref="ValidarSeriesSolicitadas"/>, que los llamadores aplican ANTES de abrir la transacción).
/// </summary>
public static class NumeracionBorrador
{
    /// <summary>Código del rechazo de una serie que el usuario no puede elegir (400).</summary>
    public const string CodigoNoEditable = "serie.no_editable";

    /// <summary>
    /// Series que llegan en una petición de alta o de modificación: <paramref name="serieBorradorId"/> nunca se admite (la del
    /// documento en preparación es siempre la configurada) y <paramref name="serieRegistroId"/> solo si <paramref name="esAdministrador"/>.
    /// <see langword="null"/> = la petición es válida en cuanto a series.
    /// </summary>
    public static Error? ValidarSeriesSolicitadas(Guid? serieBorradorId, Guid? serieRegistroId, bool esAdministrador)
    {
        if (serieBorradorId is not null)
        {
            return new Error(
                CodigoNoEditable,
                "La serie del documento en preparación no se elige: se usa la configurada para el tipo de documento.",
                "SerieBorradorId");
        }

        return serieRegistroId is not null && !esAdministrador
            ? new Error(
                CodigoNoEditable,
                "Solo un administrador puede cambiar la serie de registro: se usa la configurada para el tipo de documento.",
                "SerieRegistroId")
            : null;
    }

    /// <summary>
    /// Resuelve y valida las series de un borrador nuevo y numera el borrador, dentro de la transacción del alta:
    /// <list type="number">
    /// <item>Serie de borradores: la configurada para <paramref name="tipoBorrador"/>.</item>
    /// <item>Serie de registro: <paramref name="serieRegistroId"/> o, si es <c>null</c>, la configurada para <paramref name="tipoRegistro"/>.</item>
    /// <item>La de registro se valida (existe, es de <paramref name="tipoRegistro"/> y está activa) ANTES de numerar: un fallo no consume número.</item>
    /// <item>La de borradores numera con <see cref="IGeneradorNumeroDocumento.SiguienteAsync"/> (que valida tipo y actividad) a <paramref name="fecha"/>.</item>
    /// </list>
    /// Errores: sin configuración, <c>numeracion.sin_configuracion</c> en <c>TipoDocumento</c> (tal cual el motor); los errores del motor
    /// sobre una serie (campo <c>SerieId</c>) se devuelven en <c>SerieRegistroId</c> o <c>SerieBorradorId</c>; los demás
    /// (p. ej. <c>numeracion.sin_transaccion</c>) sin cambios. Exige transacción activa (la del alta).
    /// </summary>
    public static async Task<Result<SeriesBorradorNumeradas>> NumerarBorradorAsync(
        this IGeneradorNumeroDocumento generador,
        TipoDocumentoSerie tipoBorrador,
        TipoDocumentoSerie tipoRegistro,
        Guid? serieRegistroId,
        DateOnly fecha,
        CancellationToken cancellationToken = default)
    {
        var configuradaBorrador = await generador.SerieConfiguradaAsync(tipoBorrador, cancellationToken);
        if (!configuradaBorrador.TryObtenerValor(out var serieBorradorId))
        {
            return Result<SeriesBorradorNumeradas>.Fallo(configuradaBorrador);
        }

        if (serieRegistroId is null)
        {
            var configurada = await generador.SerieConfiguradaAsync(tipoRegistro, cancellationToken);
            if (!configurada.TryObtenerValor(out var id))
            {
                return Result<SeriesBorradorNumeradas>.Fallo(configurada);
            }

            serieRegistroId = id;
        }

        var registroValida = await generador.ValidarSerieAsync(serieRegistroId.Value, tipoRegistro, cancellationToken);
        if (registroValida.EsFallo)
        {
            return Result<SeriesBorradorNumeradas>.Fallo(EnCampo(registroValida, "SerieRegistroId"));
        }

        var numero = await generador.SiguienteAsync(serieBorradorId, tipoBorrador, fecha, cancellationToken);
        return numero.TryObtenerValor(out var generado)
            ? Result<SeriesBorradorNumeradas>.Exito(new SeriesBorradorNumeradas(serieBorradorId, serieRegistroId.Value, generado))
            : Result<SeriesBorradorNumeradas>.Fallo(EnCampo(numero, "SerieBorradorId"));
    }

    private static Error[] EnCampo(Result fallo, string campo) =>
        [.. fallo.Errores.Select(e => e.Campo == "SerieId" ? e with { Campo = campo } : e)];
}
```

y en el `<param name="SerieBorradorId">` del record: "Serie que dio <see cref="Numero"/> (siempre la configurada del tipo de borrador)".

- [ ] **Step 5: Los cinco llamadores**

`CreateFacturaVentaBorradorCommandHandler`: añadir `using OpenSource1.Application.Services.Inventario;`, el parámetro de constructor
`IUsuarioActual usuario` (último) y, como PRIMERA comprobación de `Handle` (antes de la descripción y de abrir la transacción):

```csharp
        if (NumeracionBorrador.ValidarSeriesSolicitadas(request.SerieBorradorId, request.SerieRegistroId, usuario.EsAdministrador) is { } noEditable)
        {
            return Result<FacturaVentaBorradorResponse>.Fallo(noEditable);
        }
```

y la llamada al motor pasa a

```csharp
        var series = await generadorNumero.NumerarBorradorAsync(
            TipoDocumentoSerie.BorradorFacturaVenta, TipoDocumentoSerie.FacturaVenta, request.SerieRegistroId, hoy, cancellationToken);
```

`UpdateFacturaVentaBorradorCommandHandler`: mismo `using`, parámetro `IUsuarioActual usuario` (último) y como primera comprobación:

```csharp
        if (NumeracionBorrador.ValidarSeriesSolicitadas(null, request.SerieRegistroId, usuario.EsAdministrador) is { } noEditable)
        {
            return Result<FacturaVentaBorradorResponse>.Fallo(noEditable);
        }
```

`CreateNotaCreditoVentaBorradorCommandHandler`: parámetro `IUsuarioActual usuario` (último; el `using` ya existe), la comprobación
`ValidarSeriesSolicitadas(request.SerieBorradorId, request.SerieRegistroId, usuario.EsAdministrador)` como primera instrucción de
`Handle` (con `Fallo(noEditable)`) y

```csharp
        var series = await generadorNumero.NumerarBorradorAsync(
            TipoDocumentoSerie.BorradorNotaCreditoVenta, TipoDocumentoSerie.NotaCreditoVenta, request.SerieRegistroId,
            DateOnly.FromDateTime(DateTime.UtcNow), cancellationToken);
```

`UpdateNotaCreditoVentaBorradorCommandHandler`: `using OpenSource1.Application.Services.Inventario;`, parámetro `IUsuarioActual usuario`
y como primera instrucción:

```csharp
        if (NumeracionBorrador.ValidarSeriesSolicitadas(null, request.SerieRegistroId, usuario.EsAdministrador) is { } noEditable)
        {
            return Result<NotaCreditoVentaBorradorResponse>.Fallo(noEditable);
        }
```

`CopiarFacturaABorradorCommandHandler` (sin parámetros de series: usa siempre las configuradas):

```csharp
        var series = await generadorNumero.NumerarBorradorAsync(
            TipoDocumentoSerie.BorradorFacturaVenta, TipoDocumentoSerie.FacturaVenta, null, hoy, cancellationToken);
```

En los `<summary>` de `CreateFacturaVentaBorradorCommand`, `UpdateFacturaVentaBorradorCommand`, `CreateNotaCreditoVentaBorradorCommand`
y `UpdateNotaCreditoVentaBorradorCommand`, sustituir la frase de las series por: "`SerieBorradorId` no se admite (400
`serie.no_editable`: se usa la serie configurada); `SerieRegistroId` solo la envía el Administrador (si no, 400 `serie.no_editable`);
null = la configurada (alta) o conservar (modificación)."

- [ ] **Step 6: Unitarios en verde**

Run: `dotnet test tests/OpenSource1.SmokeTests --filter "FullyQualifiedName~NumeracionBorradorTests"`
Expected: PASS (11 tests).

- [ ] **Step 7: Tests de integración (fallan hasta ajustar los helpers)**

`tests/OpenSource1.SmokeTests/Api/UsuarioActualHttpTests.cs`, añadir:

```csharp
    [Fact]
    public void EsAdministrador_SoloConElRolAdministrador_YSinPeticionEsFalso()
    {
        static UsuarioActualHttp Con(params string[] roles) => new(new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(roles.Select(r => new Claim(ClaimTypes.Role, r)), "Test")),
            },
        });

        Assert.True(Con("Administrador").EsAdministrador);
        Assert.False(Con("Supervisor", "Ejecutor").EsAdministrador);
        Assert.False(new UsuarioActualHttp(new HttpContextAccessor()).EsAdministrador);
    }
```

`tests/OpenSource1.SmokeTests/Features/FacturasVenta/PostearFacturaVentaTests.cs` (añadir `using OpenSource1.Application.Services.Inventario;`):
- Helpers: reemplazar `CrearBorradorConSeriesAsync` y `CambiarSerieRegistroAsync` por

```csharp
    private async Task<Result<FacturaVentaBorradorResponse>> CrearBorradorConSeriesAsync(
        Guid venderA, Guid? almacen, Guid? serieBorrador, Guid? serieRegistro, bool administrador = true)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<CreateFacturaVentaBorradorCommandHandler>(
            scope.ServiceProvider, (IUsuarioActual)new FechasRegistroPrueba.Usuario(null, administrador));
        return await handler.Handle(
            new CreateFacturaVentaBorradorCommand(venderA, null, D10, D10, null, almacen, null, serieBorrador, serieRegistro), default);
    }

    private async Task<Result<FacturaVentaBorradorResponse>> CambiarSerieRegistroAsync(
        Guid borradorId, Guid serieRegistro, long xmin, bool administrador = true)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<UpdateFacturaVentaBorradorCommandHandler>(
            scope.ServiceProvider, (IUsuarioActual)new FechasRegistroPrueba.Usuario(null, administrador));
        return await handler.Handle(
            new UpdateFacturaVentaBorradorCommand(borradorId, null, null, null, null, null, null, null, xmin, serieRegistro), default);
    }
```

- Sustituir `Alta_SinSeries_UsaLasConfiguradas_YConSeriesElegidasNumeraConLaDeBorrador` y
  `Alta_SerieDeBorradorDeOtroTipo_OSerieRegistroInactiva_400_SinConsumirNumero` por:

```csharp
    [Fact]
    public async Task Alta_SinSeries_UsaLasConfiguradas_YElAdministradorEligeLaDeRegistro()
    {
        var socio = await SocioAsync();
        var (serieRegistro, _, _) = await SeriesPrueba.CrearAsync(fixture.AppConnectionString, TipoDocumentoSerie.FacturaVenta);

        var porDefecto = Ok(await CrearBorradorConSeriesAsync(socio, null, null, null, administrador: false));
        var elegida = Ok(await CrearBorradorConSeriesAsync(socio, null, null, serieRegistro, administrador: true));

        Assert.Equal((SerieFacturaVentaIds.SerieBorradorId, SerieFacturaVentaIds.SeriePosteadaId), (porDefecto.SerieBorradorId, porDefecto.SerieRegistroId));
        Assert.Equal(("FV-BORR", "FV"), (porDefecto.SerieBorradorCodigo, porDefecto.SerieRegistroCodigo));
        Assert.Equal((SerieFacturaVentaIds.SerieBorradorId, serieRegistro), (elegida.SerieBorradorId, elegida.SerieRegistroId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Alta_SerieDeBorradorEnviada_400NoEditable_ParaTodos_SinConsumirNumero(bool administrador)
    {
        var socio = await SocioAsync();
        var (serieBorrador, lineaBorrador, _) = await SeriesPrueba.CrearAsync(fixture.AppConnectionString, TipoDocumentoSerie.BorradorFacturaVenta);
        var ultimoFvBorr = await UltimoNumeroAsync(SerieFacturaVentaIds.LineaSerieBorradorId);

        var resultado = await CrearBorradorConSeriesAsync(socio, null, serieBorrador, null, administrador);

        Assert.Equal(("serie.no_editable", "SerieBorradorId"), Unico(resultado));
        Assert.Equal("", await SeriesPrueba.UltimoAsync(fixture.AppConnectionString, lineaBorrador));
        Assert.Equal(ultimoFvBorr, await UltimoNumeroAsync(SerieFacturaVentaIds.LineaSerieBorradorId));
    }

    [Fact]
    public async Task Alta_SerieRegistroSinSerAdministrador_400NoEditable_YInactivaDelAdministrador_400_SinConsumirNumero()
    {
        var socio = await SocioAsync();
        var (otra, _, _) = await SeriesPrueba.CrearAsync(fixture.AppConnectionString, TipoDocumentoSerie.FacturaVenta);
        var (inactiva, _, _) = await SeriesPrueba.CrearAsync(fixture.AppConnectionString, TipoDocumentoSerie.FacturaVenta, activa: false);
        var ultimoFvBorr = await UltimoNumeroAsync(SerieFacturaVentaIds.LineaSerieBorradorId);

        var noAdministrador = await CrearBorradorConSeriesAsync(socio, null, null, otra, administrador: false);
        var inactivaDelAdministrador = await CrearBorradorConSeriesAsync(socio, null, null, inactiva, administrador: true);

        Assert.Equal(("serie.no_editable", "SerieRegistroId"), Unico(noAdministrador));
        Assert.Equal(("numeracion.serie_inactiva", "SerieRegistroId"), Unico(inactivaDelAdministrador));
        Assert.Equal(ultimoFvBorr, await UltimoNumeroAsync(SerieFacturaVentaIds.LineaSerieBorradorId));
    }
```

- En `Modificar_SerieRegistro_ValidaTipoYActividad_YUnLiberadoNoLaCambia` (renombrar a
  `Modificar_SerieRegistro_SoloElAdministrador_ValidaTipoYActividad_YUnLiberadoNoLaCambia`), añadir justo después de crear `borrador`:

```csharp
        Assert.Equal(("serie.no_editable", "SerieRegistroId"), Unico(await CambiarSerieRegistroAsync(borrador.Id, otra, borrador.Xmin, administrador: false)));
```

`tests/OpenSource1.SmokeTests/Features/NotasCreditoVenta/PostearNotaCreditoVentaTests.cs` (añadir `using OpenSource1.Application.Services.Inventario;`):
- Helpers:

```csharp
    private async Task<Result<NotaCreditoVentaBorradorResponse>> CrearBorradorConSeriesAsync(
        string factura, Guid? serieBorrador, Guid? serieRegistro, bool administrador = true)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<CreateNotaCreditoVentaBorradorCommandHandler>(
            scope.ServiceProvider, (IUsuarioActual)new FechasRegistroPrueba.Usuario(null, administrador));
        return await handler.Handle(
            new CreateNotaCreditoVentaBorradorCommand(factura, D12, null, null, true, false, serieBorrador, serieRegistro), default);
    }

    private async Task<Result<NotaCreditoVentaBorradorResponse>> ModificarCabeceraAsync(
        Guid borradorId, long xmin, Guid? serieRegistro = null, bool administrador = true)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<UpdateNotaCreditoVentaBorradorCommandHandler>(
            scope.ServiceProvider, (IUsuarioActual)new FechasRegistroPrueba.Usuario(null, administrador));
        return await handler.Handle(new UpdateNotaCreditoVentaBorradorCommand(borradorId, null, null, "x", xmin, serieRegistro), default);
    }
```

- `Postear_ConSeriesElegidas_NumeraConLaDeRegistro_YElBorradorQuedaPosteada` → renombrar a
  `Postear_ConSerieDeRegistroDelAdministrador_NumeraConElla_YElBorradorQuedaPosteada`; quitar la serie `serieBorrador`/`prefijoBorrador`,
  crear con `CrearBorradorConSeriesAsync(factura, null, serieRegistro)` y cambiar la primera aserción por
  `Assert.Equal((SerieNotaCreditoVentaIds.SerieBorradorId, EstadoNotaCreditoBorrador.Abierta), (borrador.SerieBorradorId, borrador.Estado));`.
- Añadir:

```csharp
    [Fact]
    public async Task Alta_SerieDeBorradorOSerieDeRegistroSinAdministrador_400NoEditable_YModificarIgual()
    {
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 10m, 4m, D1));
        var factura = await FacturaAsync(socio, almacen, P(producto, 2m, 50m));
        var (serieBorrador, _, _) = await SeriesPrueba.CrearAsync(fixture.AppConnectionString, TipoDocumentoSerie.BorradorNotaCreditoVenta);
        var (serieRegistro, _, _) = await SeriesPrueba.CrearAsync(fixture.AppConnectionString, TipoDocumentoSerie.NotaCreditoVenta);
        var ultimoNcBorr = await UltimoNumeroAsync(SerieNotaCreditoVentaIds.LineaSerieBorradorId);

        Assert.Equal(("serie.no_editable", "SerieBorradorId"), Unico(await CrearBorradorConSeriesAsync(factura, serieBorrador, null)));
        Assert.Equal(("serie.no_editable", "SerieRegistroId"), Unico(await CrearBorradorConSeriesAsync(factura, null, serieRegistro, administrador: false)));
        Assert.Equal(ultimoNcBorr, await UltimoNumeroAsync(SerieNotaCreditoVentaIds.LineaSerieBorradorId));

        var borrador = Ok(await CrearBorradorConSeriesAsync(factura, null, null, administrador: false));
        Assert.Equal(("serie.no_editable", "SerieRegistroId"), Unico(await ModificarCabeceraAsync(borrador.Id, borrador.Xmin, serieRegistro, administrador: false)));
        Assert.Equal(serieRegistro, Ok(await ModificarCabeceraAsync(borrador.Id, borrador.Xmin, serieRegistro)).SerieRegistroId);
    }
```

(`FacturaAsync(socio, almacen, …)` usa `almacen` en la cabecera y en las líneas de producto; R2 Step 1 deja la cabecera en `null`.)

`tests/OpenSource1.SmokeTests/Api/FacturasVentaBorradoresApiTests.cs`: sustituir `Alta_ConSeriesElegidas_LasGuarda_YUnaSerieDeOtroTipo400EnSuCampo` por:

```csharp
    [Fact]
    public async Task Series_SoloElAdministradorEligeLaDeRegistro_LaDeBorradorNuncaSeElige_400_403_409()
    {
        var admin = Admin();
        var socio = await CrearSocioAsync(admin, "Cliente series alta rapida");
        var (serieBorrador, _, _) = await SeriesPrueba.CrearAsync(_fixture.AppConnectionString, TipoDocumentoSerie.BorradorFacturaVenta);
        var (serieRegistro, _, _) = await SeriesPrueba.CrearAsync(_fixture.AppConnectionString, TipoDocumentoSerie.FacturaVenta);

        // La de borradores nunca se elige (tampoco el Administrador); una de registro de otro tipo -> 400 en su campo.
        await AssertErrorAsync(
            await admin.PostAsJsonAsync($"{Base}/borradores", new { socioNegocioId = socio, serieBorradorId = serieBorrador }),
            HttpStatusCode.BadRequest, "serie", "SerieBorradorId");
        await AssertErrorAsync(
            await admin.PostAsJsonAsync($"{Base}/borradores", new { socioNegocioId = socio, serieRegistroId = serieBorrador }),
            HttpStatusCode.BadRequest, null, "SerieRegistroId");

        // El Administrador elige la de registro en el alta y la cambia en la modificación.
        var borrador = await CrearBorradorOkAsync(admin, new { socioNegocioId = socio, serieRegistroId = serieRegistro });
        Assert.Equal((SerieFacturaVentaIds.SerieBorradorId, serieRegistro), (borrador.SerieBorradorId, borrador.SerieRegistroId));
        var cambiado = await admin.PutAsJsonAsync(
            $"{Base}/borradores/{borrador.Id}", new { xmin = borrador.Xmin, serieRegistroId = SerieFacturaVentaIds.SeriePosteadaId });
        Assert.True(cambiado.IsSuccessStatusCode, await cambiado.Content.ReadAsStringAsync());

        // 409: el xmin que vio el Administrador ya no vale.
        Assert.Equal(HttpStatusCode.Conflict,
            (await admin.PutAsJsonAsync($"{Base}/borradores/{borrador.Id}", new { xmin = borrador.Xmin, serieRegistroId = serieRegistro })).StatusCode);

        // Ejecutor (CanAdd): alta con serie de registro -> 400. Supervisor (CanModify, sin CanAdd): alta -> 403; modificación con serie -> 400.
        var ejecutor = Rol("Ejecutor");
        await AssertErrorAsync(
            await ejecutor.PostAsJsonAsync($"{Base}/borradores", new { socioNegocioId = socio, serieRegistroId = serieRegistro }),
            HttpStatusCode.BadRequest, "administrador", "SerieRegistroId");
        var supervisor = Rol("Supervisor");
        Assert.Equal(HttpStatusCode.Forbidden, (await supervisor.PostAsJsonAsync($"{Base}/borradores", new { socioNegocioId = socio })).StatusCode);
        var leido = await GetBorradorAsync(supervisor, borrador.Id);
        await AssertErrorAsync(
            await supervisor.PutAsJsonAsync($"{Base}/borradores/{leido.Id}", new { xmin = leido.Xmin, serieRegistroId = serieRegistro }),
            HttpStatusCode.BadRequest, "administrador", "SerieRegistroId");
    }
```

(`Rol()` reutiliza el mismo `HttpClient` cambiando cabeceras: `admin` deja de ser Administrador tras `Rol("Ejecutor")`, por eso no se usa después.)

- [ ] **Step 8: Integración en verde**

Run:
```bash
mkdir -p /home/ray/test/.superpowers/sdd/alta-rapida/tmp
flock /home/ray/test/.superpowers/sdd/alta-rapida/tmp/postgres-tests.lock env DOCKER_CONTEXT=default dotnet test tests/OpenSource1.SmokeTests --filter "FullyQualifiedName~NumeracionBorradorTests|FullyQualifiedName~UsuarioActualHttpTests|FullyQualifiedName~PostearFacturaVentaTests|FullyQualifiedName~PostearNotaCreditoVentaTests|FullyQualifiedName~FacturasVentaBorradoresApiTests|FullyQualifiedName~NotasCreditoVentaApiTests|FullyQualifiedName~FacturasVentaPosteoApiTests"
```
Expected: PASS. Si algún test existente de esas clases enviaba `serieBorradorId`/`SerieBorradorId` o una serie de registro con un
usuario no Administrador, adaptarlo a la regla (ver Global Constraints) en el mismo archivo; no relajar la regla.

- [ ] **Step 9: Commit**

```bash
git add src/OpenSource1.Application/Services/Inventario/IUsuarioActual.cs src/OpenSource1.Api/Infrastructure/UsuarioActualHttp.cs \
  src/OpenSource1.Infrastructure/Services/Inventario/UsuarioActualSistema.cs src/OpenSource1.Application/Data/NumeracionBorrador.cs \
  src/OpenSource1.Application/Features/FacturasVenta/Borradores/Handlers/CreateFacturaVentaBorradorCommandHandler.cs \
  src/OpenSource1.Application/Features/FacturasVenta/Borradores/Handlers/UpdateFacturaVentaBorradorCommandHandler.cs \
  src/OpenSource1.Application/Features/FacturasVenta/Copia/CopiarFacturaABorradorCommandHandler.cs \
  src/OpenSource1.Application/Features/NotasCreditoVenta/Borradores/Handlers/CreateNotaCreditoVentaBorradorCommandHandler.cs \
  src/OpenSource1.Application/Features/NotasCreditoVenta/Borradores/Handlers/UpdateNotaCreditoVentaBorradorCommandHandler.cs \
  src/OpenSource1.Application/Features/FacturasVenta/Borradores/Commands/FacturaVentaBorradorCommands.cs \
  src/OpenSource1.Application/Features/NotasCreditoVenta/Borradores/Commands/NotaCreditoVentaBorradorCommands.cs \
  tests/OpenSource1.SmokeTests/TestInfrastructure/FechasRegistroPrueba.cs tests/OpenSource1.SmokeTests/Application/Data/NumeracionBorradorTests.cs \
  tests/OpenSource1.SmokeTests/Api/UsuarioActualHttpTests.cs tests/OpenSource1.SmokeTests/Features/FacturasVenta/PostearFacturaVentaTests.cs \
  tests/OpenSource1.SmokeTests/Features/NotasCreditoVenta/PostearNotaCreditoVentaTests.cs tests/OpenSource1.SmokeTests/Api/FacturasVentaBorradoresApiTests.cs
git commit -m "feat: series fijas por configuracion y solo el administrador cambia la de registro"
```

---
### Task R2: Factura — alta con primera línea en una transacción y almacén fijo en el predeterminado

**Grupo de paralelismo:** Fase 1, serie (tras R1). Tests con Postgres bajo `flock`.

**Files:**
- Modify: `src/OpenSource1.Application/Features/FacturasVenta/Borradores/Commands/FacturaVentaBorradorCommands.cs`
- Modify: `src/OpenSource1.Application/Features/FacturasVenta/Borradores/LineaFacturaVentaBorradorReglas.cs` (`AgregarAsync`)
- Modify: `src/OpenSource1.Application/Features/FacturasVenta/Borradores/FacturaVentaBorradorReglas.cs` (`AlmacenNoEditable`)
- Modify: `src/OpenSource1.Application/Features/FacturasVenta/Borradores/Handlers/CreateFacturaVentaBorradorCommandHandler.cs`
- Modify: `src/OpenSource1.Application/Features/FacturasVenta/Borradores/Handlers/CreateLineaFacturaVentaBorradorCommandHandler.cs`
- Modify: `src/OpenSource1.Application/Features/FacturasVenta/Borradores/Handlers/UpdateFacturaVentaBorradorCommandHandler.cs`
- Modify: `src/OpenSource1.Api/Controllers/FacturasVentaController.cs`
- Modify: `src/OpenSource1.Blazor/Services/IFacturaVentaApiClient.cs`, `src/OpenSource1.Blazor/Services/FacturaVentaApiClient.cs`
- Test: `tests/OpenSource1.SmokeTests/Features/FacturasVenta/PostearFacturaVentaTests.cs`
- Test: `tests/OpenSource1.SmokeTests/Features/NotasCreditoVenta/PostearNotaCreditoVentaTests.cs` (helper `FacturaAsync`)
- Test: `tests/OpenSource1.SmokeTests/Api/FacturasVentaBorradoresApiTests.cs`
- Test: `tests/OpenSource1.SmokeTests/Api/CoherenciaVistasApiTests.cs`, `Api/ContabilidadVistasApiTests.cs`, `Api/NotasCreditoVentaApiTests.cs` (helpers `PostearFacturaAsync`: sin `almacenId` en la cabecera)
- Test: `tests/OpenSource1.SmokeTests/Blazor/FacturaVentaApiClientTests.cs`

**Interfaces:**
- Consumes (R1): `NumeracionBorrador.ValidarSeriesSolicitadas`, `NumerarBorradorAsync(tipoBorrador, tipoRegistro, serieRegistroId, fecha, ct)`, `IUsuarioActual.EsAdministrador`.
- Produces (R6, R9):
  - `public sealed record PrimeraLineaFacturaVenta(TipoLineaFactura Tipo, Guid? ProductoId, Guid? CuentaContableId, string? Descripcion, Guid? AlmacenId, Guid? UnidadMedidaId, decimal? Cantidad, decimal? PrecioUnitario, decimal? PorcentajeDescuentoLinea, Guid? GrupoIvaProductoId)`.
  - `CreateFacturaVentaBorradorCommand(…, Guid? SerieBorradorId = null, Guid? SerieRegistroId = null, PrimeraLineaFacturaVenta? PrimeraLinea = null)`; errores de la línea con campo `PrimeraLinea.<Campo>`.
  - `internal static Task<Result<LineaFacturaVentaBorrador>> LineaFacturaVentaBorradorReglas.AgregarAsync(IUnitOfWork, IConversionUnidadMedidaService, IDerivadorCuentas, FacturaVentaBorrador cabecera, LineaFacturaDatos datos, CancellationToken)` (sin commit).
  - `FacturaVentaBorradorErrores.AlmacenNoEditable()` → `almacen.no_editable` (400, campo `AlmacenId`).
  - API: `CreateFacturaVentaBorradorRequest(…, CreateLineaFacturaVentaBorradorRequest? PrimeraLinea = null)` → cuerpo JSON `primeraLinea { tipo, productoId, … }`.
  - Blazor: `BorradorCabeceraInput(…, Guid? SerieBorradorId = null, Guid? SerieRegistroId = null, LineaFacturaInput? PrimeraLinea = null)`; `CreateBorradorAsync` envía `primeraLinea`.

- [ ] **Step 1: Helpers de tests que enviaban un almacén de cabecera distinto del predeterminado**

El almacén de la cabecera queda fijo en el predeterminado; los tests que sembraban un almacén propio lo siguen usando en las LÍNEAS.

`PostearFacturaVentaTests.cs` — los borradores "heredados" con otro almacén se simulan por SQL (un borrador creado antes de esta
versión pudo tener cualquier almacén y el posteo lo sigue respetando):

```csharp
    private async Task<FacturaVentaBorradorResponse> BorradorAsync(Guid venderA, Guid? facturarA = null, Guid? almacen = null)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<CreateFacturaVentaBorradorCommandHandler>(scope.ServiceProvider);
        var creado = Ok(await handler.Handle(new CreateFacturaVentaBorradorCommand(venderA, facturarA, D10, D10, null, null, null), default));
        return almacen is { } a ? await ConAlmacenHeredadoAsync(creado.Id, a) : creado;
    }

    /// <summary>Borrador con un almacén de cabecera distinto del predeterminado, como los creados antes del alta rápida (por SQL).</summary>
    private async Task<FacturaVentaBorradorResponse> ConAlmacenHeredadoAsync(Guid borradorId, Guid almacen)
    {
        await EjecutarSqlAsync("""UPDATE "FacturasVentaBorrador" SET "AlmacenId" = @A WHERE "Id" = @Id""", new { A = almacen, Id = borradorId });
        return Ok(await ObtenerBorradorAsync(borradorId));
    }
```

y en `CrearBorradorConSeriesAsync` crear SIEMPRE con almacén `null` y aplicar `ConAlmacenHeredadoAsync` si `almacen` no es nulo y el
alta fue bien:

```csharp
        var resultado = await handler.Handle(
            new CreateFacturaVentaBorradorCommand(venderA, null, D10, D10, null, null, null, serieBorrador, serieRegistro), default);
        return almacen is { } a && resultado.TryObtenerValor(out var creado)
            ? Result<FacturaVentaBorradorResponse>.Exito(await ConAlmacenHeredadoAsync(creado.Id, a))
            : resultado;
```

`PostearNotaCreditoVentaTests.cs`, helper `FacturaAsync`: la cabecera con almacén `null`
(`new CreateFacturaVentaBorradorCommand(socio, null, D10, D10, null, null, null)`); las líneas de producto siguen recibiendo `almacen`.

`CoherenciaVistasApiTests.cs`, `ContabilidadVistasApiTests.cs` y `NotasCreditoVentaApiTests.cs`, helper `PostearFacturaAsync`: quitar
`almacenId = …` del cuerpo de `POST /api/facturas-venta/borradores` (la línea de producto ya lleva su `almacenId`).

- [ ] **Step 2: Tests nuevos (fallan)**

`PostearFacturaVentaTests.cs`, nueva sección `// ----- Alta rápida (spec alta rápida) -----`:

```csharp
    [Fact]
    public async Task AltaConPrimeraLinea_CreaElBorradorYLaLineaEnUnaTransaccion()
    {
        var socio = await SocioAsync();
        var producto = await ProductoAsync();

        var creado = Ok(await CrearConPrimeraLineaAsync(socio, new PrimeraLineaFacturaVenta(
            TipoLineaFactura.Producto, producto, null, null, null, null, 2m, 10m, null, null)));

        Assert.Equal((1, AlmacenIds.Principal), (creado.NumeroLineas, creado.AlmacenId));
        var linea = Assert.Single(await LineasBorradorAsync(creado.Id));
        Assert.Equal((10000, 2m, 20m, AlmacenIds.Principal), (linea.NumeroLinea, linea.Cantidad, linea.ImporteLinea, linea.AlmacenId!.Value));
    }

    [Fact]
    public async Task AltaConPrimeraLineaInvalida_NoCreaNada_NiConsumeNumero()
    {
        var socio = await SocioAsync();
        var ultimoFvBorr = await UltimoNumeroAsync(SerieFacturaVentaIds.LineaSerieBorradorId);

        var sinPrecio = await CrearConPrimeraLineaAsync(socio, new PrimeraLineaFacturaVenta(
            TipoLineaFactura.Producto, await ProductoAsync(), null, null, null, null, 1m, 0m, null, null));
        var productoInexistente = await CrearConPrimeraLineaAsync(socio, new PrimeraLineaFacturaVenta(
            TipoLineaFactura.Producto, Guid.NewGuid(), null, null, null, null, 1m, 5m, null, null));

        Assert.Equal(("factura.precio_invalido", "PrimeraLinea.PrecioUnitario"), Unico(sinPrecio));
        Assert.Equal(("factura.producto_invalido", "PrimeraLinea.ProductoId"), Unico(productoInexistente));
        Assert.Equal(ultimoFvBorr, await UltimoNumeroAsync(SerieFacturaVentaIds.LineaSerieBorradorId));
        Assert.Equal(0L, await EscalarAsync<long>(
            """SELECT COUNT(*) FROM "FacturasVentaBorrador" WHERE "SocioNegocioId" = @S""", new { S = socio }));
    }

    [Fact]
    public async Task Alta_AlmacenDistintoDelPredeterminado_400NoEditable_SinConsumirNumero_YElPredeterminadoSeAcepta()
    {
        var socio = await SocioAsync();
        var otro = await _prueba.SembrarAlmacenAsync();
        var ultimoFvBorr = await UltimoNumeroAsync(SerieFacturaVentaIds.LineaSerieBorradorId);

        var distinto = await CrearConAlmacenAsync(socio, otro);
        Assert.Equal(("almacen.no_editable", "AlmacenId"), Unico(distinto));
        Assert.Equal(ultimoFvBorr, await UltimoNumeroAsync(SerieFacturaVentaIds.LineaSerieBorradorId));

        Assert.Equal(AlmacenIds.Principal, Ok(await CrearConAlmacenAsync(socio, AlmacenIds.Principal)).AlmacenId);
    }

    [Fact]
    public async Task Modificar_AlmacenDistinto_400NoEditable_ElActualOElPredeterminadoSeAceptan()
    {
        var socio = await SocioAsync();
        var otro = await _prueba.SembrarAlmacenAsync();
        var heredado = await BorradorAsync(socio, almacen: otro);

        Assert.Equal(("almacen.no_editable", "AlmacenId"), Unico(await ModificarAlmacenAsync(heredado.Id, await _prueba.SembrarAlmacenAsync(), heredado.Xmin)));
        var mismo = Ok(await ModificarAlmacenAsync(heredado.Id, otro, heredado.Xmin));
        Assert.Equal(otro, mismo.AlmacenId);
        Assert.Equal(AlmacenIds.Principal, Ok(await ModificarAlmacenAsync(heredado.Id, AlmacenIds.Principal, mismo.Xmin)).AlmacenId);
    }
```

Helpers (sección de helpers de borradores):

```csharp
    private async Task<Result<FacturaVentaBorradorResponse>> CrearConPrimeraLineaAsync(Guid venderA, PrimeraLineaFacturaVenta linea)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<CreateFacturaVentaBorradorCommandHandler>(scope.ServiceProvider);
        return await handler.Handle(
            new CreateFacturaVentaBorradorCommand(venderA, null, D10, D10, null, null, null, PrimeraLinea: linea), default);
    }

    private async Task<Result<FacturaVentaBorradorResponse>> CrearConAlmacenAsync(Guid venderA, Guid almacen)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<CreateFacturaVentaBorradorCommandHandler>(scope.ServiceProvider);
        return await handler.Handle(new CreateFacturaVentaBorradorCommand(venderA, null, D10, D10, null, almacen, null), default);
    }

    private async Task<Result<FacturaVentaBorradorResponse>> ModificarAlmacenAsync(Guid borradorId, Guid almacen, long xmin)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<UpdateFacturaVentaBorradorCommandHandler>(scope.ServiceProvider);
        return await handler.Handle(
            new UpdateFacturaVentaBorradorCommand(borradorId, null, null, null, null, null, almacen, null, xmin), default);
    }
```

(Los códigos de `sinPrecio`/`productoInexistente` son los de `LineaFacturaVentaBorradorReglas`: comprobar en el archivo los literales
`factura.precio_invalido` y `factura.producto_invalido` y el campo que devuelven, y ajustar la aserción al campo real con el prefijo
`PrimeraLinea.`.)

`FacturasVentaBorradoresApiTests.cs`:

```csharp
    [Fact]
    public async Task AltaRapida_ConPrimeraLinea_201_ConLaLinea_YAlmacenPredeterminado()
    {
        var client = Admin();
        var socio = await CrearSocioAsync(client, "Cliente alta rapida");
        var producto = await CrearProductoAsync(client, 100m);

        var borrador = await CrearBorradorOkAsync(client, new
        {
            socioNegocioId = socio,
            primeraLinea = new { tipo = TipoLineaFactura.Producto, productoId = producto, cantidad = 2m, precioUnitario = 100m },
        });

        Assert.Equal((1, AlmacenIds.Principal), (borrador.NumeroLineas, borrador.AlmacenId));
        var lineas = await client.GetFromJsonAsync<List<LineaFacturaVentaBorradorResponse>>($"{Base}/borradores/{borrador.Id}/lineas");
        Assert.Equal(200m, Assert.Single(lineas!).ImporteLinea);
    }

    [Fact]
    public async Task AltaRapida_PrimeraLineaInvalida_400EnPrimeraLinea_YNoCreaBorrador()
    {
        var client = Admin();
        var socio = await CrearSocioAsync(client, "Cliente alta rapida invalida");
        var producto = await CrearProductoAsync(client, 100m);

        await AssertErrorAsync(
            await client.PostAsJsonAsync($"{Base}/borradores", new
            {
                socioNegocioId = socio,
                primeraLinea = new { tipo = TipoLineaFactura.Producto, productoId = producto, cantidad = 1m, precioUnitario = 0m },
            }),
            HttpStatusCode.BadRequest, "precio", "PrimeraLinea.PrecioUnitario");

        var pagina = await client.GetFromJsonAsync<PagedResult<FacturaVentaBorradorResponse>>($"{Base}/borradores?socioId={socio}");
        Assert.Empty(pagina!.Items);
    }

    [Fact]
    public async Task Almacen_DistintoDelPredeterminado_400NoEditable_EnAltaYModificacion()
    {
        var client = Admin();
        var socio = await CrearSocioAsync(client, "Cliente almacen fijo");
        var otro = await CrearAlmacenAsync(client);

        await AssertErrorAsync(
            await client.PostAsJsonAsync($"{Base}/borradores", new { socioNegocioId = socio, almacenId = otro }),
            HttpStatusCode.BadRequest, "predeterminado", "AlmacenId");
        var borrador = await CrearBorradorOkAsync(client, new { socioNegocioId = socio, almacenId = AlmacenIds.Principal });
        await AssertErrorAsync(
            await client.PutAsJsonAsync($"{Base}/borradores/{borrador.Id}", new { xmin = borrador.Xmin, almacenId = otro }),
            HttpStatusCode.BadRequest, "predeterminado", "AlmacenId");
    }
```

con el helper (si la clase no tiene ya uno equivalente):

```csharp
    private static async Task<Guid> CrearAlmacenAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/almacenes", new { codigo = Unico("AL")[..10], nombre = "Almacén secundario", bloqueado = false, esPredeterminado = false });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }
```

(mismo cuerpo que usa `AlmacenesApiTests`; comprobar la longitud máxima del código y ajustar el recorte.)

En `Alta_SociosInvalidos_GruposFaltantes_YFechas_Devuelven400` (el test que hoy envía `almacenId = Guid.NewGuid()`), el fragmento esperado pasa a
`"predeterminado"` (el mensaje de `almacen.no_editable`).

`FacturaVentaApiClientTests.cs`:

```csharp
    [Fact]
    public async Task CreateBorrador_ConPrimeraLinea_EnviaLaLineaConElTipoComoEntero()
    {
        var handler = new Grabador(HttpStatusCode.Created, BorradorJson);
        var client = new FacturaVentaApiClient(Http(handler), NullLogger<FacturaVentaApiClient>.Instance);
        var socio = Guid.NewGuid();
        var producto = Guid.NewGuid();

        await client.CreateBorradorAsync(new BorradorCabeceraInput(
            socio, null, null, null, null, null, null,
            PrimeraLinea: new LineaFacturaInput(TipoLineaFactura.Producto, producto, null, null, null, null, 2m, 10m, null, null)));

        Assert.EndsWith("api/facturas-venta/borradores", handler.Uri!.AbsolutePath);
        using var cuerpo = JsonDocument.Parse(handler.Cuerpo!);
        var linea = cuerpo.RootElement.GetProperty("primeraLinea");
        Assert.Equal((1, producto, 2m), (linea.GetProperty("tipo").GetInt32(), linea.GetProperty("productoId").GetGuid(), linea.GetProperty("cantidad").GetDecimal()));
        Assert.Equal(JsonValueKind.Null, cuerpo.RootElement.GetProperty("almacenId").ValueKind);
    }
```

- [ ] **Step 3: Ejecutarlos (fallan)**

Run: `dotnet test tests/OpenSource1.SmokeTests --filter "FullyQualifiedName~FacturaVentaApiClientTests"`
Expected: FAIL al compilar (`PrimeraLinea` no existe en `BorradorCabeceraInput` ni `PrimeraLineaFacturaVenta` en Application).

- [ ] **Step 4: Comando, regla de alta de línea compartida y error de almacén**

`FacturaVentaBorradorCommands.cs` (añadir `using OpenSource1.Application.Features.FacturasVenta.Borradores;` y
`using OpenSource1.Core.Enums;`):

```csharp
/// <summary>
/// Primera línea del alta rápida (spec alta rápida): mismos campos y valores por defecto que
/// <see cref="CreateLineaFacturaVentaBorradorCommand"/>; se guarda en la MISMA transacción que la cabecera.
/// </summary>
public sealed record PrimeraLineaFacturaVenta(
    TipoLineaFactura Tipo,
    Guid? ProductoId,
    Guid? CuentaContableId,
    string? Descripcion,
    Guid? AlmacenId,
    Guid? UnidadMedidaId,
    decimal? Cantidad,
    decimal? PrecioUnitario,
    decimal? PorcentajeDescuentoLinea,
    Guid? GrupoIvaProductoId)
{
    internal LineaFacturaDatos ADatos() => new(
        Tipo, ProductoId, CuentaContableId, Descripcion, AlmacenId, UnidadMedidaId, Cantidad, PrecioUnitario, PorcentajeDescuentoLinea,
        GrupoIvaProductoId);
}
```

y en `CreateFacturaVentaBorradorCommand` añadir el último parámetro `PrimeraLineaFacturaVenta? PrimeraLinea = null` y, en su
`<summary>`: "`AlmacenId`: el almacén es siempre el predeterminado; otro distinto -> 400 `almacen.no_editable`. `PrimeraLinea`
(opcional): se valida y se guarda con la cabecera en una transacción; si falla, no se crea nada ni se consume número (errores con
campo `PrimeraLinea.<Campo>`)." En `UpdateFacturaVentaBorradorCommand`: "`AlmacenId` null = conservar; el actual o el predeterminado se
aceptan; otro -> 400 `almacen.no_editable`."

`FacturaVentaBorradorReglas.cs`, en `FacturaVentaBorradorErrores`:

```csharp
    /// <summary>El almacén de la cabecera lo fija el servidor: siempre el predeterminado (spec alta rápida).</summary>
    public static Error AlmacenNoEditable() =>
        new("almacen.no_editable", "El almacén no se elige: el documento usa el almacén predeterminado.", "AlmacenId");
```

`LineaFacturaVentaBorradorReglas.cs`, añadir `using OpenSource1.Application.Features.FacturasVenta.Borradores.Handlers;` y el método:

```csharp
    /// <summary>
    /// Alta de una línea nueva en <paramref name="cabecera"/> SIN guardar (lo hace el commit del llamador, dentro de su transacción):
    /// valida y calcula (<see cref="ValidarYCalcularAsync"/>), comprueba el tope de
    /// <see cref="CreateLineaFacturaVentaBorradorCommandHandler.MaximoLineasPorBorrador"/> líneas y asigna <c>NumeroLinea</c> = máximo + 10000.
    /// Única lógica de alta de línea: la usan el alta de una línea y el alta del borrador con su primera línea (spec alta rápida).
    /// </summary>
    public static async Task<Result<LineaFacturaVentaBorrador>> AgregarAsync(
        IUnitOfWork unitOfWork,
        IConversionUnidadMedidaService conversion,
        IDerivadorCuentas derivador,
        FacturaVentaBorrador cabecera,
        LineaFacturaDatos datos,
        CancellationToken cancellationToken)
    {
        var calculo = await ValidarYCalcularAsync(unitOfWork, conversion, derivador, cabecera, datos, cancellationToken);
        if (!calculo.TryObtenerValor(out var valores))
        {
            return Result<LineaFacturaVentaBorrador>.Fallo(calculo);
        }

        var lineasRepo = unitOfWork.Repository<LineaFacturaVentaBorrador>();
        var lineas = await lineasRepo.ListAsync(x => x.FacturaVentaBorradorId == cabecera.Id, cancellationToken);
        if (lineas.Count >= CreateLineaFacturaVentaBorradorCommandHandler.MaximoLineasPorBorrador)
        {
            return Result<LineaFacturaVentaBorrador>.Fallo(new Error(
                "factura.limite_lineas",
                $"El borrador ya tiene el máximo de {CreateLineaFacturaVentaBorradorCommandHandler.MaximoLineasPorBorrador} líneas.",
                "FacturaVentaBorradorId"));
        }

        var entity = new LineaFacturaVentaBorrador
        {
            FacturaVentaBorradorId = cabecera.Id,
            NumeroLinea = (lineas.Count == 0 ? 0 : lineas.Max(x => x.NumeroLinea)) + 10000,
        };
        valores.Aplicar(entity);
        await lineasRepo.AddAsync(entity, cancellationToken);
        return Result<LineaFacturaVentaBorrador>.Exito(entity);
    }
```

`CreateLineaFacturaVentaBorradorCommandHandler.Handle`: sustituir desde `var calculo = …` hasta `await lineasRepo.AddAsync(entity, …);` por

```csharp
        var alta = await LineaFacturaVentaBorradorReglas.AgregarAsync(unitOfWork, conversion, derivador, cabecera!, datos, cancellationToken);
        if (!alta.TryObtenerValor(out var entity))
        {
            return Result<LineaFacturaVentaBorradorResponse>.Fallo(alta);
        }
```

(el `CommitAsync` y la lectura final con `entity.Id` no cambian).

- [ ] **Step 5: Alta y modificación del borrador**

`CreateFacturaVentaBorradorCommandHandler.cs` completo:

```csharp
using MediatR;
using OpenSource1.Application.Data;
using OpenSource1.Application.Data.UnitOfWork;
using OpenSource1.Application.Features.FacturasVenta.Borradores.Commands;
using OpenSource1.Application.Features.FacturasVenta.Borradores.Dtos;
using OpenSource1.Application.Services.Contabilidad;
using OpenSource1.Application.Services.Inventario;
using OpenSource1.Core.Common;
using OpenSource1.Core.Entities.Ventas;
using OpenSource1.Core.Enums;

namespace OpenSource1.Application.Features.FacturasVenta.Borradores.Handlers;

/// <summary>
/// Alta de un borrador (spec alta rápida): series según <see cref="NumeracionBorrador.ValidarSeriesSolicitadas"/> (antes de abrir la
/// transacción); snapshot del facturar-a y grupos congelados (<see cref="FacturaVentaBorradorReglas.TomarSnapshotAsync"/>), con los
/// socios bloqueados <c>FOR SHARE</c> ANTES de validarlos; vencimiento por el término de pago; almacén SIEMPRE el predeterminado
/// (otro -> 400 <c>almacen.no_editable</c>); moneda DOP; número de la serie de borradores configurada y serie de registro configurada o
/// la del Administrador (<see cref="NumeracionBorrador.NumerarBorradorAsync"/>). Con <c>PrimeraLinea</c>, la línea se valida y se añade
/// con <see cref="LineaFacturaVentaBorradorReglas.AgregarAsync"/> en la MISMA transacción: si falla, no se hace commit y no queda ni el
/// borrador ni el número consumido.
/// </summary>
public sealed class CreateFacturaVentaBorradorCommandHandler(
    IUnitOfWork unitOfWork,
    IGeneradorNumeroDocumento generadorNumero,
    IFacturaVentaBorradorReadRepository readRepository,
    IFacturaVentaBorradorDatos borradorDatos,
    IConversionUnidadMedidaService conversion,
    IDerivadorCuentas derivador,
    IUsuarioActual usuario)
    : IRequestHandler<CreateFacturaVentaBorradorCommand, Result<FacturaVentaBorradorResponse>>
{
    public async Task<Result<FacturaVentaBorradorResponse>> Handle(CreateFacturaVentaBorradorCommand request, CancellationToken cancellationToken)
    {
        if (NumeracionBorrador.ValidarSeriesSolicitadas(request.SerieBorradorId, request.SerieRegistroId, usuario.EsAdministrador) is { } noEditable)
        {
            return Result<FacturaVentaBorradorResponse>.Fallo(noEditable);
        }

        var descripcion = FacturaVentaBorradorReglas.Normalizar(request.Descripcion);
        if (FacturaVentaBorradorReglas.ValidarDescripcion(descripcion) is { } errorDescripcion)
        {
            return Result<FacturaVentaBorradorResponse>.Fallo(errorDescripcion);
        }

        // Sin CommitAsync, salir del "await using" deshace la transacción (el número del borrador no se consume).
        await using var transaccion = await unitOfWork.BeginTransactionAsync(cancellationToken);

        // Socios (FOR SHARE) antes de validarlos: el borrado del socio espera a este commit o este alta ve el socio borrado.
        var facturarAId = request.SocioNegocioFacturarAId ?? request.SocioNegocioId;
        await borradorDatos.BloquearSociosAsync([request.SocioNegocioId, facturarAId], cancellationToken);
        var snapshot = await FacturaVentaBorradorReglas.TomarSnapshotAsync(
            unitOfWork, request.SocioNegocioId, facturarAId, cancellationToken);
        if (!snapshot.TryObtenerValor(out var socios))
        {
            return Result<FacturaVentaBorradorResponse>.Fallo(snapshot);
        }

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var fechaRegistro = request.FechaRegistro ?? hoy;
        var fechaDocumento = request.FechaDocumento ?? fechaRegistro;
        var fechaVencimiento = request.FechaVencimiento
            ?? await FacturaVentaBorradorReglas.CalcularVencimientoAsync(unitOfWork, fechaDocumento, socios.TerminoPagoId, cancellationToken);
        if (FacturaVentaBorradorReglas.ValidarFechas(fechaRegistro, fechaDocumento, fechaVencimiento) is { } errorFecha)
        {
            return Result<FacturaVentaBorradorResponse>.Fallo(errorFecha);
        }

        // Almacén: siempre el predeterminado (spec alta rápida); uno distinto se rechaza ANTES de numerar.
        var almacen = await FacturaVentaBorradorReglas.ResolverAlmacenAsync(unitOfWork, null, cancellationToken);
        if (!almacen.TryObtenerValor(out var almacenId))
        {
            return Result<FacturaVentaBorradorResponse>.Fallo(almacen);
        }

        if (request.AlmacenId is { } almacenSolicitado && almacenSolicitado != almacenId)
        {
            return Result<FacturaVentaBorradorResponse>.Fallo(FacturaVentaBorradorErrores.AlmacenNoEditable());
        }

        var series = await generadorNumero.NumerarBorradorAsync(
            TipoDocumentoSerie.BorradorFacturaVenta, TipoDocumentoSerie.FacturaVenta, request.SerieRegistroId, hoy, cancellationToken);
        if (!series.TryObtenerValor(out var numeradas))
        {
            return Result<FacturaVentaBorradorResponse>.Fallo(series);
        }

        var entity = new FacturaVentaBorrador
        {
            Numero = numeradas.Numero.Numero,
            SerieBorradorId = numeradas.SerieBorradorId,
            SerieRegistroId = numeradas.SerieRegistroId,
            NombreFacturacion = socios.NombreFacturacion,
            FechaRegistro = fechaRegistro,
            FechaDocumento = fechaDocumento,
            FechaVencimiento = fechaVencimiento,
            AlmacenId = almacenId,
            Estado = EstadoFacturaBorrador.Abierta,
            Moneda = SerieFacturaVentaIds.MonedaPorDefecto,
            Descripcion = descripcion
        };
        socios.Aplicar(entity);

        await unitOfWork.Repository<FacturaVentaBorrador>().AddAsync(entity, cancellationToken);

        if (request.PrimeraLinea is { } primera)
        {
            var linea = await LineaFacturaVentaBorradorReglas.AgregarAsync(
                unitOfWork, conversion, derivador, entity, primera.ADatos(), cancellationToken);
            if (linea.EsFallo)
            {
                // Sin commit: la cabecera, la línea y el número del borrador se deshacen con la transacción.
                return Result<FacturaVentaBorradorResponse>.Fallo(
                    [.. linea.Errores.Select(e => e with { Campo = e.Campo is null ? "PrimeraLinea" : $"PrimeraLinea.{e.Campo}" })]);
            }
        }

        await unitOfWork.CommitAsync(cancellationToken);

        return Result<FacturaVentaBorradorResponse>.Exito((await readRepository.GetByIdAsync(entity.Id, cancellationToken))!);
    }
}
```

`UpdateFacturaVentaBorradorCommandHandler.cs`: sustituir el bloque `if (request.AlmacenId is { } almacenSolicitado && …) { … }` por

```csharp
        // Almacén (spec alta rápida): null o el actual = conservar; el predeterminado se acepta; cualquier otro -> 400.
        if (request.AlmacenId is { } almacenSolicitado && almacenSolicitado != entity.AlmacenId)
        {
            var predeterminado = await FacturaVentaBorradorReglas.ResolverAlmacenAsync(unitOfWork, null, cancellationToken);
            if (!predeterminado.TryObtenerValor(out var almacenId))
            {
                return Result<FacturaVentaBorradorResponse>.Fallo(predeterminado);
            }

            if (almacenSolicitado != almacenId)
            {
                return Result<FacturaVentaBorradorResponse>.Fallo(FacturaVentaBorradorErrores.AlmacenNoEditable());
            }

            entity.AlmacenId = almacenId;
        }
```

- [ ] **Step 6: API y cliente Blazor**

`FacturasVentaController.cs`: añadir a `CreateFacturaVentaBorradorRequest` el último parámetro
`CreateLineaFacturaVentaBorradorRequest? PrimeraLinea = null` y en `CreateBorrador`:

```csharp
        var result = await sender.Send(
            new CreateFacturaVentaBorradorCommand(
                request.SocioNegocioId, request.SocioNegocioFacturarAId, request.FechaRegistro, request.FechaDocumento,
                request.FechaVencimiento, request.AlmacenId, request.Descripcion, request.SerieBorradorId, request.SerieRegistroId,
                request.PrimeraLinea is { } l
                    ? new PrimeraLineaFacturaVenta(
                        l.Tipo, l.ProductoId, l.CuentaContableId, l.Descripcion, l.AlmacenId, l.UnidadMedidaId, l.Cantidad, l.PrecioUnitario,
                        l.PorcentajeDescuentoLinea, l.GrupoIvaProductoId)
                    : null),
            cancellationToken);
```

y en el `<summary>` de la clase: "Alta rápida: `POST borradores` admite `primeraLinea` (mismo cuerpo que `POST borradores/{id}/lineas`);
el alta sin líneas sigue disponible por compatibilidad."

`IFacturaVentaApiClient.cs`: `BorradorCabeceraInput` gana el último parámetro `LineaFacturaInput? PrimeraLinea = null` (documentar:
"Alta rápida: primera línea guardada con la cabecera en una transacción; Blazor siempre la envía").

`FacturaVentaApiClient.cs`:

```csharp
    public async Task<VentaOperationResult<FacturaVentaBorradorResponse>> CreateBorradorAsync(
        BorradorCabeceraInput input, CancellationToken cancellationToken = default)
    {
        var body = new CreateBorradorBody(
            input.SocioNegocioId, input.SocioNegocioFacturarAId, input.FechaRegistro, input.FechaDocumento, input.FechaVencimiento,
            input.AlmacenId, input.Descripcion, input.SerieBorradorId, input.SerieRegistroId,
            input.PrimeraLinea is { } linea ? Linea(linea) : null);
        using var response = await httpClient.PostAsJsonAsync($"{BaseRoute}/borradores", body, cancellationToken);
        return await VentaApiRespuestas.ToResultAsync<FacturaVentaBorradorResponse>(
            response, "Factura en preparación creada.", BorradorNoEncontrado, "borradores de factura", logger, cancellationToken);
    }
```

`CreateLineaAsync` usa `var body = Linea(input);`; nuevo helper y cuerpo:

```csharp
    private static LineaBody Linea(LineaFacturaInput input) => new(
        input.Tipo, input.ProductoId, input.CuentaContableId, input.Descripcion, input.AlmacenId, input.UnidadMedidaId,
        input.Cantidad, input.PrecioUnitario, input.PorcentajeDescuentoLinea, input.GrupoIvaProductoId);

    private sealed record CreateBorradorBody(
        Guid SocioNegocioId, Guid? SocioNegocioFacturarAId, DateOnly? FechaRegistro, DateOnly? FechaDocumento,
        DateOnly? FechaVencimiento, Guid? AlmacenId, string? Descripcion, Guid? SerieBorradorId, Guid? SerieRegistroId, LineaBody? PrimeraLinea);
```

- [ ] **Step 7: Verde**

Run:
```bash
dotnet test tests/OpenSource1.SmokeTests --filter "FullyQualifiedName~FacturaVentaApiClientTests"
flock /home/ray/test/.superpowers/sdd/alta-rapida/tmp/postgres-tests.lock env DOCKER_CONTEXT=default dotnet test tests/OpenSource1.SmokeTests --filter "FullyQualifiedName~PostearFacturaVentaTests|FullyQualifiedName~PostearNotaCreditoVentaTests|FullyQualifiedName~FacturasVentaBorradoresApiTests|FullyQualifiedName~FacturasVentaPosteoApiTests|FullyQualifiedName~CoherenciaVistasApiTests|FullyQualifiedName~ContabilidadVistasApiTests|FullyQualifiedName~NotasCreditoVentaApiTests|FullyQualifiedName~CobrosTests|FullyQualifiedName~BusquedaApiTests"
```
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/OpenSource1.Application/Features/FacturasVenta/Borradores src/OpenSource1.Api/Controllers/FacturasVentaController.cs \
  src/OpenSource1.Blazor/Services/IFacturaVentaApiClient.cs src/OpenSource1.Blazor/Services/FacturaVentaApiClient.cs \
  tests/OpenSource1.SmokeTests/Features/FacturasVenta/PostearFacturaVentaTests.cs tests/OpenSource1.SmokeTests/Features/NotasCreditoVenta/PostearNotaCreditoVentaTests.cs \
  tests/OpenSource1.SmokeTests/Api/FacturasVentaBorradoresApiTests.cs tests/OpenSource1.SmokeTests/Api/CoherenciaVistasApiTests.cs \
  tests/OpenSource1.SmokeTests/Api/ContabilidadVistasApiTests.cs tests/OpenSource1.SmokeTests/Api/NotasCreditoVentaApiTests.cs \
  tests/OpenSource1.SmokeTests/Blazor/FacturaVentaApiClientTests.cs
git commit -m "feat: alta de factura con primera linea en una transaccion y almacen predeterminado fijo"
```

---

### Task R3: Nota de crédito — alta desde la factura con las líneas pendientes y la devolución por defecto

**Grupo de paralelismo:** Fase 1, serie (tras R2). Tests con Postgres bajo `flock`.

**Files:**
- Modify: `src/OpenSource1.Api/Controllers/NotasCreditoVentaController.cs`
- Test: `tests/OpenSource1.SmokeTests/Api/NotasCreditoVentaApiTests.cs`
- Test: `tests/OpenSource1.SmokeTests/Api/BusquedaApiTests.cs`
- Test: `tests/OpenSource1.SmokeTests/Features/NotasCreditoVenta/PostearNotaCreditoVentaTests.cs`

**Interfaces:**
- Consumes (R1): reglas de series ya aplicadas en `CreateNotaCreditoVentaBorradorCommandHandler`/`Update…` (`serie.no_editable`). La nota no tiene almacén de cabecera (cada línea devuelve al almacén de su línea de factura): `almacen.no_editable` no aplica.
- Produces (R7, R9):
  - `CreateNotaCreditoVentaBorradorRequest(string FacturaVentaNumero, DateOnly? FechaRegistro = null, DateOnly? FechaDocumento = null, string? Descripcion = null, bool? CopiarLineas = null, bool? DevolverInventario = null, Guid? SerieBorradorId = null, Guid? SerieRegistroId = null)`: `null` = `true` (nota total con devolución). El comando y el handler no cambian.
  - El cliente Blazor sigue enviando `copiarLineas`/`devolverInventario` explícitos (`NotaCreditoBorradorInput` sin cambios).

- [ ] **Step 1: Tests (fallan)**

`NotasCreditoVentaApiTests.cs`:

```csharp
    [Fact]
    public async Task AltaRapida_SinOpciones_CopiaTodoLoPendienteConDevolucion_YSeriesSoloElAdministrador()
    {
        var ingresos = await CrearCuentaAsync("4195");
        var producto = await ProductoClasificadoAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var socio = await CrearSocioAsync("Cliente nota rapida");
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 10m, 5m, new DateOnly(2023, 7, 5)));
        var (factura, _) = await PostearFacturaAsync(socio, new DateOnly(2023, 7, 10), (ingresos, 20m), (producto, 2m, 50m, almacen));
        var (serieBorrador, _, _) = await SeriesPrueba.CrearAsync(fixture.AppConnectionString, TipoDocumentoSerie.BorradorNotaCreditoVenta);
        var (serieRegistro, _, _) = await SeriesPrueba.CrearAsync(fixture.AppConnectionString, TipoDocumentoSerie.NotaCreditoVenta);

        // Sin copiarLineas ni devolverInventario: nota total con devolución y las series configuradas.
        var creado = await _client.PostAsJsonAsync("/api/notas-credito-venta/borradores", new { facturaVentaNumero = factura, fechaRegistro = "2023-07-12" });
        Assert.True(creado.StatusCode == HttpStatusCode.Created, await creado.Content.ReadAsStringAsync());
        var borrador = (await creado.Content.ReadFromJsonAsync<NotaCreditoVentaBorradorResponse>())!;
        Assert.Equal((2, "NC-BORR", "NC"), (borrador.NumeroLineas, borrador.SerieBorradorCodigo, borrador.SerieRegistroCodigo));
        var lineas = await GetAsync<List<LineaNotaCreditoVentaBorradorResponse>>($"/api/notas-credito-venta/borradores/{borrador.Id}/lineas");
        Assert.Equal([(TipoLineaFactura.Producto, 2m, true), (TipoLineaFactura.CuentaContable, 1m, false)],
            lineas.OrderBy(l => l.Tipo).Select(l => (l.Tipo, l.Cantidad, l.DevolverInventario)));

        // Serie de borradores: 400 para todos. Serie de registro: 400 para el Ejecutor, 201 para el Administrador.
        var conSerieBorrador = await _client.PostAsJsonAsync("/api/notas-credito-venta/borradores",
            new { facturaVentaNumero = factura, copiarLineas = false, serieBorradorId = serieBorrador });
        Assert.Equal(HttpStatusCode.BadRequest, conSerieBorrador.StatusCode);
        Assert.Contains("SerieBorradorId", await conSerieBorrador.Content.ReadAsStringAsync());
        using var ejecutor = _factory.CreateClient();
        ejecutor.DefaultRequestHeaders.Add("X-Test-User", "ejecutor");
        ejecutor.DefaultRequestHeaders.Add("X-Test-Roles", "Ejecutor");
        var deEjecutor = await ejecutor.PostAsJsonAsync("/api/notas-credito-venta/borradores",
            new { facturaVentaNumero = factura, copiarLineas = false, serieRegistroId = serieRegistro });
        Assert.Equal(HttpStatusCode.BadRequest, deEjecutor.StatusCode);
        Assert.Contains("administrador", await deEjecutor.Content.ReadAsStringAsync());
        var deAdministrador = await _client.PostAsJsonAsync("/api/notas-credito-venta/borradores",
            new { facturaVentaNumero = factura, copiarLineas = false, serieRegistroId = serieRegistro });
        Assert.Equal(HttpStatusCode.Created, deAdministrador.StatusCode);
        Assert.Equal(serieRegistro, (await deAdministrador.Content.ReadFromJsonAsync<NotaCreditoVentaBorradorResponse>())!.SerieRegistroId);
    }
```

(Comprobar las firmas reales de `ProductoClasificadoAsync`, `PostearFacturaAsync` y `SeriesPrueba` en la clase y ajustar los argumentos.)

En el MISMO archivo, los tests que dependían del valor por defecto anterior (`copiarLineas` omitido = sin líneas) lo envían
explícito: en `ReviewFocus3_FacturaNotaParcialYPago_SaldoDerivadoExacto_YLasVistasCuadranEnCadaCorte` el cuerpo pasa a
`new { facturaVentaNumero = factura, fechaRegistro = "2022-01-20", copiarLineas = false }` (sigue esperando `NumeroLineas == 0`). En
`BusquedaApiTests.cs` (alta del borrador de nota) añadir `copiarLineas = false` para conservar exactamente el escenario del test.

`PostearNotaCreditoVentaTests.cs`: añadir el caso de la nota parcial previa (la nota total copia solo lo pendiente):

```csharp
    [Fact]
    public async Task NotaTotalTrasUnaParcial_CopiaSoloLoPendienteDeCadaLinea()
    {
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 10m, 4m, D1));
        var factura = await FacturaAsync(socio, almacen, P(producto, 3m, 50m));
        var parcial = Ok(await CrearBorradorAsync(factura));
        Ok(await LineaAsync(parcial.Id, Assert.Single(await LineasFacturaAsync(factura)).Id, 1m, devolver: true));
        Ok(await PostearAsync(parcial.Id));

        var total = Ok(await CrearBorradorAsync(factura, copiar: true, devolver: true));

        var linea = Assert.Single(await LineasBorradorAsync(total.Id));
        Assert.Equal((2m, true), (linea.Cantidad, linea.DevolverInventario));
    }
```

(`LineaAsync`, `LineasFacturaAsync`, `LineasBorradorAsync`, `CrearBorradorAsync` y `PostearAsync` son los helpers existentes de la clase.)

- [ ] **Step 2: Ejecutarlos (fallan)**

Run: `flock /home/ray/test/.superpowers/sdd/alta-rapida/tmp/postgres-tests.lock env DOCKER_CONTEXT=default dotnet test tests/OpenSource1.SmokeTests --filter "FullyQualifiedName~NotasCreditoVentaApiTests.AltaRapida"`
Expected: FAIL — `NumeroLineas` 0 (el valor por defecto aún es `false`).

- [ ] **Step 3: Valores por defecto en la API**

`NotasCreditoVentaController.cs`:

```csharp
public sealed record CreateNotaCreditoVentaBorradorRequest(
    string FacturaVentaNumero,
    DateOnly? FechaRegistro = null,
    DateOnly? FechaDocumento = null,
    string? Descripcion = null,
    bool? CopiarLineas = null,
    bool? DevolverInventario = null,
    Guid? SerieBorradorId = null,
    Guid? SerieRegistroId = null);
```

y en `CreateBorrador`: `request.CopiarLineas ?? true, request.DevolverInventario ?? true`. `<summary>` de la acción: "Crea un borrador
desde una factura posteada (spec alta rápida): por defecto con todas sus líneas pendientes (`copiarLineas`) y la devolución al
inventario de las de producto (`devolverInventario`); series configuradas (la de registro solo la cambia el Administrador)."

- [ ] **Step 4: Verde**

Run: `flock /home/ray/test/.superpowers/sdd/alta-rapida/tmp/postgres-tests.lock env DOCKER_CONTEXT=default dotnet test tests/OpenSource1.SmokeTests --filter "FullyQualifiedName~NotasCreditoVentaApiTests|FullyQualifiedName~PostearNotaCreditoVentaTests|FullyQualifiedName~BusquedaApiTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/OpenSource1.Api/Controllers/NotasCreditoVentaController.cs tests/OpenSource1.SmokeTests/Api/NotasCreditoVentaApiTests.cs \
  tests/OpenSource1.SmokeTests/Api/BusquedaApiTests.cs tests/OpenSource1.SmokeTests/Features/NotasCreditoVenta/PostearNotaCreditoVentaTests.cs
git commit -m "feat: nota de credito desde la factura con las lineas pendientes y la devolucion por defecto"
```

---
### Task R4: Conteo "En preparación (n)", total de cada documento en preparación y códigos de grupos

**Grupo de paralelismo:** Fase 1, serie (tras R3). Tests con Postgres bajo `flock` (los del cliente, sin Docker).

**Files:**
- Modify: `src/OpenSource1.Application/Features/FacturasVenta/Borradores/Dtos/FacturaVentaBorradorResponse.cs`
- Modify: `src/OpenSource1.Application/Features/NotasCreditoVenta/Borradores/Dtos/NotaCreditoVentaBorradorResponse.cs`
- Modify: `src/OpenSource1.Infrastructure/Data/Queries/DapperFacturaVentaBorradorReadRepository.cs`
- Modify: `src/OpenSource1.Infrastructure/Data/Queries/DapperNotaCreditoVentaBorradorReadRepository.cs`
- Modify: `src/OpenSource1.Blazor/Services/IFacturaVentaApiClient.cs`, `FacturaVentaApiClient.cs`, `INotaCreditoVentaApiClient.cs`, `NotaCreditoVentaApiClient.cs`
- Test: `tests/OpenSource1.SmokeTests/Api/FacturasVentaBorradoresApiTests.cs`, `tests/OpenSource1.SmokeTests/Api/NotasCreditoVentaApiTests.cs`
- Test: `tests/OpenSource1.SmokeTests/Blazor/FacturaVentaApiClientTests.cs`, `tests/OpenSource1.SmokeTests/Blazor/NotaCreditoVentaApiClientTests.cs`

**Interfaces:**
- Consumes: `GET api/facturas-venta/borradores` (sin `estado` = Abierta + Liberada = "En preparación") y `GET api/notas-credito-venta/borradores` (sin `estado` = Abierta), ya paginados con `PagedResult<T>.Total`. **No se añade endpoint**: el conteo es el `Total` de una página de tamaño 1.
- Produces (R6, R7, R8):
  - `FacturaVentaBorradorResponse.ImporteTotal : decimal` (vista previa: Σ por grupo de IVA de `ROUND(Σ ImporteLinea, 2)` + `ROUND(base × % / 100, 2)`, misma aritmética que `CalculadoraIvaFactura`), `GrupoNegocioCodigo`, `GrupoIvaNegocioCodigo`, `GrupoClienteContableCodigo : string?`.
  - `NotaCreditoVentaBorradorResponse.ImporteTotal : decimal` (vista previa sobre las líneas guardadas, sin topes: el definitivo lo calcula el posteo).
  - `Task<long> IFacturaVentaApiClient.ContarEnPreparacionAsync(CancellationToken cancellationToken = default)` y `Task<long> INotaCreditoVentaApiClient.ContarEnPreparacionAsync(CancellationToken cancellationToken = default)`.

- [ ] **Step 1: Tests (fallan)**

`FacturasVentaBorradoresApiTests.cs`, al final de `Totales_IvaAgrupadoPorIdentificador_SinComentarios` (que ya deja un borrador con
totales `(80.09, 5.42, 85.51)`):

```csharp
        // Spec alta rápida: el listado "En preparación" muestra el mismo total que la vista previa.
        Assert.Equal(totales.ImporteTotal, (await GetBorradorAsync(client, borrador.Id)).ImporteTotal);
        var lista = await client.GetFromJsonAsync<PagedResult<FacturaVentaBorradorResponse>>($"{Base}/borradores?numero={borrador.Numero}");
        Assert.Equal(totales.ImporteTotal, Assert.Single(lista!.Items).ImporteTotal);
```

y en `Alta_TomaSnapshotDelFacturarA_GruposCongelados_Vencimiento_AlmacenPredeterminado_YNumero`:

```csharp
        Assert.False(string.IsNullOrWhiteSpace(borrador.GrupoNegocioCodigo));
        Assert.False(string.IsNullOrWhiteSpace(borrador.GrupoIvaNegocioCodigo));
        Assert.False(string.IsNullOrWhiteSpace(borrador.GrupoClienteContableCodigo));
        Assert.Equal(0m, borrador.ImporteTotal);
```

Nuevo test del conteo:

```csharp
    [Fact]
    public async Task ConteoEnPreparacion_EsElTotalDeUnaPaginaDeUno_AbiertasYLiberadas()
    {
        var client = Admin();
        var socio = await CrearSocioAsync(client, "Cliente conteo");
        var producto = await CrearProductoAsync(client, 10m);
        await CrearBorradorOkAsync(client, new { socioNegocioId = socio });
        var liberado = await CrearBorradorOkAsync(client, new
        {
            socioNegocioId = socio, primeraLinea = new { tipo = TipoLineaFactura.Producto, productoId = producto, cantidad = 1m, precioUnitario = 10m },
        });
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"{Base}/borradores/{liberado.Id}/liberar", null)).StatusCode);

        var pagina = await client.GetFromJsonAsync<PagedResult<FacturaVentaBorradorResponse>>($"{Base}/borradores?socioId={socio}&tamanoPagina=1");

        Assert.Equal(2L, pagina!.Total);
        Assert.Single(pagina.Items);
    }
```

`NotasCreditoVentaApiTests.cs`, al final de `AltaRapida_SinOpciones_CopiaTodoLoPendienteConDevolucion_YSeriesSoloElAdministrador` (R3):

```csharp
        // Spec alta rápida: total de la lista = vista previa (sin notas previas sobre la factura, sin topes que apliquen).
        var vistaPrevia = await GetAsync<TotalesFactura>($"/api/notas-credito-venta/borradores/{borrador.Id}/totales");
        var enLista = await GetAsync<PagedResult<NotaCreditoVentaBorradorResponse>>($"/api/notas-credito-venta/borradores?numero={borrador.Numero}");
        Assert.Equal(vistaPrevia.ImporteTotal, Assert.Single(enLista.Items).ImporteTotal);
```

`FacturaVentaApiClientTests.cs`:

```csharp
    [Fact]
    public async Task ContarEnPreparacion_PideUnaPaginaDeUno_SinEstado_YDevuelveElTotal()
    {
        var handler = new Grabador(HttpStatusCode.OK, """{"items":[],"pagina":1,"tamanoPagina":1,"total":7}""");
        var client = new FacturaVentaApiClient(Http(handler), NullLogger<FacturaVentaApiClient>.Instance);

        var total = await client.ContarEnPreparacionAsync();

        Assert.Equal(7L, total);
        Assert.EndsWith("api/facturas-venta/borradores", handler.Uri!.AbsolutePath);
        Assert.Contains("tamanoPagina=1", handler.Uri.Query);
        Assert.DoesNotContain("estado=", handler.Uri.Query);
    }
```

`NotaCreditoVentaApiClientTests.cs`: el mismo test con `NotaCreditoVentaApiClient`/`NullLogger<NotaCreditoVentaApiClient>` y la ruta
`api/notas-credito-venta/borradores` (construir el `HttpClient` con el `BaseAddress` como en el resto de la clase).

- [ ] **Step 2: Ejecutarlos (fallan al compilar)**

Run: `dotnet test tests/OpenSource1.SmokeTests --filter "FullyQualifiedName~FacturaVentaApiClientTests|FullyQualifiedName~NotaCreditoVentaApiClientTests"`
Expected: FAIL — `ContarEnPreparacionAsync`, `ImporteTotal` y `Grupo…Codigo` no existen.

- [ ] **Step 3: DTOs y SQL**

`FacturaVentaBorradorResponse.cs`, tras `GrupoClienteContableId`:

```csharp
    public string? GrupoNegocioCodigo { get; init; }
    public string? GrupoIvaNegocioCodigo { get; init; }
    public string? GrupoClienteContableCodigo { get; init; }
```

y tras `NumeroLineas`:

```csharp
    /// <summary>
    /// Total con ITBIS de las líneas vivas (vista previa para las listas "En preparación"): misma aritmética que
    /// <c>CalculadoraIvaFactura</c> (base e IVA redondeados a 2 por identificador de IVA). El definitivo lo fija el posteo.
    /// </summary>
    public decimal ImporteTotal { get; init; }
```

`NotaCreditoVentaBorradorResponse.cs`, tras `NumeroLineas`:

```csharp
    /// <summary>Total con ITBIS de las líneas guardadas (vista previa de las listas, sin los topes de lo ya acreditado).</summary>
    public decimal ImporteTotal { get; init; }
```

`DapperFacturaVentaBorradorReadRepository.cs`, en `Base`, sustituir la columna `f."GrupoNegocioId", f."GrupoIvaNegocioId",
f."GrupoClienteContableId",` por:

```sql
               f."GrupoNegocioId", (SELECT g."Codigo" FROM "GruposNegocio" g WHERE g."Id" = f."GrupoNegocioId") AS "GrupoNegocioCodigo",
               f."GrupoIvaNegocioId", (SELECT g."Codigo" FROM "GruposIvaNegocio" g WHERE g."Id" = f."GrupoIvaNegocioId") AS "GrupoIvaNegocioCodigo",
               f."GrupoClienteContableId", (SELECT g."Codigo" FROM "GruposClienteContable" g WHERE g."Id" = f."GrupoClienteContableId") AS "GrupoClienteContableCodigo",
```

y tras la columna `"NumeroLineas"`:

```sql
               COALESCE((SELECT SUM(g."Base" + ROUND(g."Base" * g."PorcentajeIva" / 100, 2))
                         FROM (SELECT ROUND(SUM(l."ImporteLinea"), 2) AS "Base", l."PorcentajeIva"
                               FROM "LineasFacturaVentaBorrador" l
                               WHERE l."FacturaVentaBorradorId" = f."Id" AND l."IsDeleted" = false AND l."Tipo" <> 3
                               GROUP BY l."IdentificadorIva", l."PorcentajeIva") g), 0) AS "ImporteTotal",
```

`DapperNotaCreditoVentaBorradorReadRepository.cs`, en `Base`, tras `"NumeroLineas"`:

```sql
               COALESCE((SELECT SUM(g."Base" + ROUND(g."Base" * g."PorcentajeIva" / 100, 2))
                         FROM (SELECT ROUND(SUM(l."ImporteLinea"), 2) AS "Base", l."PorcentajeIva"
                               FROM "LineasNotaCreditoVentaBorrador" l
                               WHERE l."NotaCreditoVentaBorradorId" = n."Id" AND l."IsDeleted" = false AND l."Tipo" <> 3
                               GROUP BY l."IdentificadorIva", l."PorcentajeIva") g), 0) AS "ImporteTotal",
```

(`ROUND(numeric, 2)` de PostgreSQL redondea los medios alejándose de cero, igual que `MidpointRounding.AwayFromZero`; `Tipo` 3 =
`TipoLineaFactura.Comentario`.)

- [ ] **Step 4: Clientes**

`IFacturaVentaApiClient.cs`:

```csharp
    /// <summary>
    /// Facturas en preparación (borradores Abierta y Liberada) para la pestaña "En preparación (n)": el <c>Total</c> de una página de
    /// tamaño 1 del listado existente. Un error de la API lanza <see cref="HttpRequestException"/> (la página muestra la pestaña sin número).
    /// </summary>
    Task<long> ContarEnPreparacionAsync(CancellationToken cancellationToken = default);
```

`FacturaVentaApiClient.cs`:

```csharp
    public async Task<long> ContarEnPreparacionAsync(CancellationToken cancellationToken = default) =>
        (await ListBorradoresAsync(null, new PageRequest(1, 1, "CreatedAtUtc", Descendente: true), cancellationToken)).Total;
```

`INotaCreditoVentaApiClient.cs` / `NotaCreditoVentaApiClient.cs`: el mismo método (documentación: "Notas en preparación (borradores
Abierta)").

- [ ] **Step 5: Verde**

Run:
```bash
dotnet test tests/OpenSource1.SmokeTests --filter "FullyQualifiedName~FacturaVentaApiClientTests|FullyQualifiedName~NotaCreditoVentaApiClientTests"
flock /home/ray/test/.superpowers/sdd/alta-rapida/tmp/postgres-tests.lock env DOCKER_CONTEXT=default dotnet test tests/OpenSource1.SmokeTests --filter "FullyQualifiedName~FacturasVentaBorradoresApiTests|FullyQualifiedName~NotasCreditoVentaApiTests|FullyQualifiedName~PostearFacturaVentaTests|FullyQualifiedName~PostearNotaCreditoVentaTests"
```
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/OpenSource1.Application/Features/FacturasVenta/Borradores/Dtos/FacturaVentaBorradorResponse.cs \
  src/OpenSource1.Application/Features/NotasCreditoVenta/Borradores/Dtos/NotaCreditoVentaBorradorResponse.cs \
  src/OpenSource1.Infrastructure/Data/Queries/DapperFacturaVentaBorradorReadRepository.cs \
  src/OpenSource1.Infrastructure/Data/Queries/DapperNotaCreditoVentaBorradorReadRepository.cs \
  src/OpenSource1.Blazor/Services/IFacturaVentaApiClient.cs src/OpenSource1.Blazor/Services/FacturaVentaApiClient.cs \
  src/OpenSource1.Blazor/Services/INotaCreditoVentaApiClient.cs src/OpenSource1.Blazor/Services/NotaCreditoVentaApiClient.cs \
  tests/OpenSource1.SmokeTests/Api/FacturasVentaBorradoresApiTests.cs tests/OpenSource1.SmokeTests/Api/NotasCreditoVentaApiTests.cs \
  tests/OpenSource1.SmokeTests/Blazor/FacturaVentaApiClientTests.cs tests/OpenSource1.SmokeTests/Blazor/NotaCreditoVentaApiClientTests.cs
git commit -m "feat: conteo y total de los documentos en preparacion y codigos de grupos del borrador"
```

---

### Task G1: Puerta de la Fase 1

**Grupo de paralelismo:** serie (tras R4).

**Files:** ninguno nuevo (arreglos en el archivo de la task responsable).

- [ ] **Step 1: Build sin avisos**

Run: `dotnet build test.slnx -warnaserror`
Expected: 0 avisos, 0 errores.

- [ ] **Step 2: Suite completa**

Run: `flock /home/ray/test/.superpowers/sdd/alta-rapida/tmp/postgres-tests.lock env DOCKER_CONTEXT=default dotnet test test.slnx`
Expected: suite verde (anotar el total frente a la partida del README, 2004, y los tests nuevos de R1–R4).

- [ ] **Step 3: Modelo sin cambios**

Run: `dotnet ef migrations has-pending-model-changes -p src/OpenSource1.Infrastructure -s src/OpenSource1.Api --context ApplicationDbContext`
Expected: "No changes have been made to the model since the last migration."

- [ ] **Step 4: Motor de posteo y numeración intactos**

Run: `git diff f79a44a...HEAD --stat -- src/OpenSource1.Application/Features/FacturasVenta/Posteo src/OpenSource1.Application/Features/NotasCreditoVenta/Posteo src/OpenSource1.Infrastructure/Data/GeneradorNumeroDocumento.cs src/OpenSource1.Application/Data/IGeneradorNumeroDocumento.cs src/OpenSource1.Infrastructure/Data/Migrations src/OpenSource1.Infrastructure/Data/ApplicationDbContext.cs`
Expected: sin archivos.

- [ ] **Step 5: Sin commit si no hubo arreglos** (si los hubo: `git commit -m "fix: puerta de la fase 1 del alta rapida"` con las rutas explícitas).

---
## Fase 2 — Blazor (Static SSR)

### Task R5: Componentes compartidos — datos derivados, numeración, aviso sin serie, confirmación de registro y resumen de errores

**Grupo de paralelismo:** Fase 2, serie (primera; ver "Orden de ejecución" para correrla en paralelo con R2–R4). Sin Docker.

**Files:**
- Modify: `src/OpenSource1.Blazor/Components/Documento/DocumentoVistas.cs` (`DatoDerivado`, `NumeracionDocumento`, `AccionCabecera`)
- Create: `src/OpenSource1.Blazor/Components/Documento/DatosDerivados.razor`
- Create: `src/OpenSource1.Blazor/Components/Documento/NumeracionRegistro.razor`
- Create: `src/OpenSource1.Blazor/Components/Documento/AvisoSinSerie.razor`
- Create: `src/OpenSource1.Blazor/Components/Documento/ConfirmarRegistro.razor`
- Create: `src/OpenSource1.Blazor/Components/Documento/ResumenErrores.razor`
- Modify: `src/OpenSource1.Blazor/Components/Documento/SerieNumeracionInfo.razor`
- Modify: `src/OpenSource1.Blazor/Components/Documento/DocumentoCabeceraEditable.razor` (parámetro `Botones`)
- Create: `src/OpenSource1.Blazor/wwwroot/app.errores.js`
- Modify: `src/OpenSource1.Blazor/Components/App.razor` (script)
- Test: `tests/OpenSource1.SmokeTests/Blazor/ComponentesDocumentoTests.cs`

**Interfaces:**
- Consumes: `ConfiguracionNumeracionResponse` (`TipoDocumento`, `SerieId`, `SerieCodigo`, `SerieActiva`), `ProximoVista`, `SelectorSerie`, `ConfirmDialog`, `MessageBox`, `SerieResponse`.
- Produces (R6, R7, R8):
  - `public sealed record DatoDerivado(string Etiqueta, string Valor, string? Href = null)`.
  - `static class NumeracionDocumento { string? MotivoBloqueo(IReadOnlyList<ConfiguracionNumeracionResponse>? configuracion, TipoDocumentoSerie tipoBorrador, TipoDocumentoSerie tipoRegistro, string documentos); ConfiguracionNumeracionResponse? Configurada(IReadOnlyList<ConfiguracionNumeracionResponse>? configuracion, TipoDocumentoSerie tipo); int? LineaDelMensaje(string mensaje) }`.
  - `static class AccionCabecera { const string Registrar = "registrar"; const string Despues = "despues"; }`.
  - `<DatosDerivados Datos="IReadOnlyList<DatoDerivado>" DetallesContables="IReadOnlyList<DatoDerivado>" />` → `<dl>` con `data-testid="datos-derivados"`; detalles en `<details data-testid="detalles-contables"><summary>Detalles contables</summary>`.
  - `<NumeracionRegistro Codigo Proximo MostrarInfo PuedeCambiar Id Name Series Seleccion VigenteId CargaFallida />` (`MostrarInfo` = true por defecto) → `SerieNumeracionInfo` + (solo `PuedeCambiar`) `<details data-testid="mas-opciones"><summary>Más opciones</summary>` con la etiqueta "Cambiar serie de registro" y `SelectorSerie`.
  - `<AvisoSinSerie Motivo PuedeConfigurar />` → `data-testid="aviso-sin-serie"`, enlace "Configurar la numeración" (`/configuracion/numeracion`) o "Pida a un administrador que la configure."
  - `<ConfirmarRegistro Titulo Aviso Resumen Model FormName OnValidSubmit SeguirEditandoHref Errores />` → diálogo con `<dl data-testid="resumen-registro">`, botón `data-testid="si-registrar"` "Sí, registrar" y enlace "Seguir editando".
  - `<ResumenErrores Mensaje Errores />` → `role="alert"`, `tabindex="-1"`, `data-resumen-errores`, `data-testid="resumen-errores"`; los mensajes "Línea N: …" enlazan a `#linea-N`.
  - `DocumentoCabeceraEditable.Botones` (`RenderFragment?`): si se indica, sustituye al botón "Guardar cabecera".
  - `wwwroot/app.errores.js`: enfoca `[data-resumen-errores]` al cargar y en `window.Blazor.addEventListener('enhancedload', …)`.

- [ ] **Step 1: Tests de componentes (fallan)**

En `ComponentesDocumentoTests.cs` (añadir `using OpenSource1.Application.Features.ConfiguracionNumeracion.Dtos;`):
- `InfoDeSerie_MuestraProximoOAvisoOError`: cambiar `Assert.Contains("Próximo número: <strong>00000012</strong>", conProximo);` por
  `Assert.Contains("Se numerará con la serie <strong>FV</strong> · próximo <strong>00000012</strong> (el número definitivo se asigna al registrar)", conProximo);`
  y `Assert.DoesNotContain("Próximo número", conError);` por `Assert.DoesNotContain("Se numerará", conError);`.
- `Cabecera_CampoSeries_MuestraLaInfoDeSerieDentroDeLaCabecera`: `"Próximo número: <strong>FV-000012</strong>"` →
  `"próximo <strong>FV-000012</strong>"` y `Assert.DoesNotContain("Próximo número", posteada);` → `Assert.DoesNotContain("Se numerará", posteada);`.
- Tests nuevos:

```csharp
    [Fact]
    public async Task DatosDerivados_EnDl_SinInputs_YDetallesContablesPlegados()
    {
        var html = await Renderizar<DatosDerivados>(new()
        {
            ["Datos"] = new List<DatoDerivado> { new("Facturar a", "C-001 — Comercial Uno", "/clientes/1"), new("Almacén", "PRINC") },
            ["DetallesContables"] = new List<DatoDerivado> { new("Grupo de negocio", "NACIONAL") },
        });

        Assert.Contains("data-testid=\"datos-derivados\"", html);
        Assert.Contains("<dt class=\"text-xs font-semibold text-slate-500\">Facturar a</dt>", html);
        Assert.Contains("<a class=\"hover:underline\" href=\"/clientes/1\">C-001 — Comercial Uno</a>", html);
        Assert.Contains("<summary class=\"cursor-pointer text-xs font-semibold text-slate-600\">Detalles contables</summary>", html);
        Assert.Contains(">NACIONAL</dd>", html);
        Assert.DoesNotContain("<input", html);
        Assert.DoesNotContain("disabled", html);
    }

    [Fact]
    public async Task NumeracionRegistro_SoloElAdministradorVeCambiarSerieEnMasOpciones()
    {
        var parametros = new Dictionary<string, object?>
        {
            ["Codigo"] = "FV", ["Proximo"] = new ProximoVista("FV-000123", null, null), ["Id"] = "UpdateInput_SerieRegistroId",
            ["Name"] = "UpdateInput.SerieRegistroId", ["Series"] = new List<SerieResponse> { Serie("FV2", "Sucursal", null) },
        };
        var usuario = await Renderizar<NumeracionRegistro>(new(parametros) { ["PuedeCambiar"] = false });
        var administrador = await Renderizar<NumeracionRegistro>(new(parametros) { ["PuedeCambiar"] = true });

        Assert.Contains("Se numerará con la serie <strong>FV</strong> · próximo <strong>FV-000123</strong>", usuario);
        Assert.DoesNotContain("mas-opciones", usuario);
        Assert.DoesNotContain("name=\"UpdateInput.SerieRegistroId\"", usuario);
        Assert.Contains("data-testid=\"mas-opciones\"", administrador);
        Assert.Contains(">Más opciones</summary>", administrador);
        Assert.Contains(">Cambiar serie de registro</label>", administrador);
        Assert.Contains("name=\"UpdateInput.SerieRegistroId\"", administrador);
    }

    [Fact]
    public async Task AvisoSinSerie_EnlaceSoloParaQuienPuedeConfigurar()
    {
        var administrador = await Renderizar<AvisoSinSerie>(new() { ["Motivo"] = "No hay una serie configurada para facturas de venta.", ["PuedeConfigurar"] = true });
        var usuario = await Renderizar<AvisoSinSerie>(new() { ["Motivo"] = "No hay una serie configurada para facturas de venta.", ["PuedeConfigurar"] = false });

        Assert.Contains("data-testid=\"aviso-sin-serie\"", administrador);
        Assert.Contains("href=\"/configuracion/numeracion\"", administrador);
        Assert.DoesNotContain("/configuracion/numeracion", usuario);
        Assert.Contains("Pida a un administrador que la configure.", usuario);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void MotivoBloqueo_SerieInactivaBloquea_YSiLaConfiguracionNoCargaNo(bool? activa, bool hayConfigurada)
    {
        IReadOnlyList<ConfiguracionNumeracionResponse>? configuracion = activa is null
            ? null
            : [
                new ConfiguracionNumeracionResponse { TipoDocumento = TipoDocumentoSerie.BorradorFacturaVenta, SerieCodigo = "FV-BORR", SerieActiva = true },
                new ConfiguracionNumeracionResponse { TipoDocumento = TipoDocumentoSerie.FacturaVenta, SerieCodigo = "FV", SerieActiva = activa.Value },
            ];

        var motivo = NumeracionDocumento.MotivoBloqueo(configuracion, TipoDocumentoSerie.BorradorFacturaVenta, TipoDocumentoSerie.FacturaVenta, "facturas de venta");

        // activa = false: la serie de registro está inactiva -> bloquea; null: la configuración no cargó -> no bloquea (la API valida).
        Assert.Equal(activa == false, motivo is not null);
        Assert.Equal(hayConfigurada, NumeracionDocumento.Configurada(configuracion, TipoDocumentoSerie.FacturaVenta) is not null);
        if (motivo is not null)
        {
            Assert.StartsWith("No hay una serie configurada para facturas de venta", motivo);
        }
    }

    [Fact]
    public void MotivoBloqueo_SinFilaDelTipo_Bloquea()
    {
        var motivo = NumeracionDocumento.MotivoBloqueo(
            [new ConfiguracionNumeracionResponse { TipoDocumento = TipoDocumentoSerie.FacturaVenta, SerieActiva = true }],
            TipoDocumentoSerie.BorradorFacturaVenta, TipoDocumentoSerie.FacturaVenta, "facturas de venta");

        Assert.NotNull(motivo);
    }

    [Fact]
    public async Task ResumenErrores_RoleAlert_Enfocable_YEnlacesALasLineas()
    {
        var html = await Renderizar<ResumenErrores>(new()
        {
            ["Mensaje"] = "No fue posible registrar la factura.",
            ["Errores"] = new List<string> { "Línea 20000: La existencia del producto P1 es insuficiente.", "La fecha de registro no está permitida." },
        });
        var vacio = await Renderizar<ResumenErrores>(new());

        Assert.Contains("role=\"alert\"", html);
        Assert.Contains("tabindex=\"-1\"", html);
        Assert.Contains("data-resumen-errores", html);
        Assert.Contains("<a class=\"underline\" href=\"#linea-20000\">Línea 20000: La existencia del producto P1 es insuficiente.</a>", html);
        Assert.Contains("<li>La fecha de registro no está permitida.</li>", html);
        Assert.DoesNotContain("resumen-errores", vacio);
    }

    [Fact]
    public async Task ConfirmarRegistro_ResumenSiRegistrarYSeguirEditando()
    {
        var html = await Renderizar<ConfirmarRegistro>(new()
        {
            ["Titulo"] = "Registrar factura",
            ["Aviso"] = "Se descontará el inventario y se generará el asiento; no se puede deshacer.",
            ["Resumen"] = new List<DatoDerivado> { new("Total", "112.10"), new("Líneas", "2"), new("Serie", "FV"), new("Próximo número", "FV-000123") },
            ["Model"] = new ModeloPrueba(), ["FormName"] = "confirm-postear", ["SeguirEditandoHref"] = "/facturas-venta/en-preparacion/1",
        });

        Assert.Contains(">Registrar factura</h2>", html);
        Assert.Contains("se generará el asiento; no se puede deshacer", html);
        Assert.Contains("data-testid=\"resumen-registro\"", html);
        Assert.Contains("<dd>FV-000123</dd>", html);
        Assert.Contains(">Sí, registrar</button>", html);
        Assert.Contains("<a href=\"/facturas-venta/en-preparacion/1\"", html);
        Assert.Contains(">Seguir editando</a>", html);
    }

    [Fact]
    public async Task CabeceraEditable_ConBotonesPropios_SinGuardarCabecera()
    {
        RenderFragment botones = b => b.AddMarkupContent(0, "<button type=\"submit\" data-testid=\"registrar\">Registrar factura</button>");

        var html = await Renderizar<DocumentoCabeceraEditable>(new() { ["Model"] = new ModeloPrueba(), ["FormName"] = "guardar-cabecera", ["Botones"] = botones });

        Assert.Contains("data-testid=\"registrar\"", html);
        Assert.DoesNotContain(">Guardar cabecera</button>", html);
    }

    [Fact]
    public void AppErrores_EnfocaElResumen_ConBlazorAddEventListener_YSeCargaEnApp()
    {
        var raiz = Path.Combine(BlazorSsrFactory.RaizRepositorio(), "src", "OpenSource1.Blazor");
        var fuente = File.ReadAllText(Path.Combine(raiz, "wwwroot", "app.errores.js"));
        var app = File.ReadAllText(Path.Combine(raiz, "Components", "App.razor"));

        Assert.Contains("window.Blazor.addEventListener('enhancedload'", fuente);
        Assert.DoesNotContain("document.addEventListener('enhancedload'", fuente);
        Assert.Contains("[data-resumen-errores]", fuente);
        Assert.Contains("@Assets[\"app.errores.js\"]", app);
    }
```

(`Renderizar<T>` admite un `Dictionary<string, object?>`; `new(parametros) { … }` copia el diccionario. Si `ModeloPrueba` no tiene
ya un constructor público sin parámetros, el `private sealed class ModeloPrueba` existente lo tiene por defecto.)

- [ ] **Step 2: Ejecutarlos (fallan al compilar)**

Run: `dotnet test tests/OpenSource1.SmokeTests --filter "FullyQualifiedName~ComponentesDocumentoTests"`
Expected: FAIL — `DatoDerivado`, `DatosDerivados`, `NumeracionRegistro`, `AvisoSinSerie`, `ResumenErrores`, `ConfirmarRegistro` y `NumeracionDocumento` no existen.

- [ ] **Step 3: Tipos en `DocumentoVistas.cs`**

Añadir `using System.Text.RegularExpressions;` y `using OpenSource1.Application.Features.ConfiguracionNumeracion.Dtos;` y, al final del archivo:

```csharp
/// <summary>Un dato derivado en solo lectura (spec alta rápida): <see cref="DatosDerivados"/> lo pinta como <c>&lt;dt&gt;/&lt;dd&gt;</c>.</summary>
public sealed record DatoDerivado(string Etiqueta, string Valor, string? Href = null);

/// <summary>Valor del botón que envía la cabecera editable (<c>name="UpdateInput.Accion"</c>): qué hacer después de guardarla.</summary>
public static class AccionCabecera
{
    /// <summary>Guardar y abrir la confirmación de "Registrar".</summary>
    public const string Registrar = "registrar";

    /// <summary>Guardar y volver a la lista "En preparación".</summary>
    public const string Despues = "despues";
}

/// <summary>Numeración vista desde la UI del documento (spec alta rápida, Parte 1.5).</summary>
public static partial class NumeracionDocumento
{
    /// <summary>
    /// Motivo para no capturar ni registrar: falta la fila de configuración de <paramref name="tipoBorrador"/> o de
    /// <paramref name="tipoRegistro"/>, o su serie está inactiva. <see langword="null"/> = se puede; también si la configuración no
    /// cargó (<paramref name="configuracion"/> null): no se bloquea por un fallo de lectura, la API valida al guardar (como F7).
    /// </summary>
    public static string? MotivoBloqueo(
        IReadOnlyList<ConfiguracionNumeracionResponse>? configuracion, TipoDocumentoSerie tipoBorrador, TipoDocumentoSerie tipoRegistro, string documentos)
    {
        if (configuracion is null)
        {
            return null;
        }

        var falta = new[] { tipoBorrador, tipoRegistro }.Any(t => Configurada(configuracion, t) is null);
        return falta
            ? $"No hay una serie configurada para {documentos} (o la serie configurada está inactiva): no se puede preparar ni registrar."
            : null;
    }

    /// <summary>Fila de configuración del tipo con su serie activa; <see langword="null"/> si no hay o está inactiva.</summary>
    public static ConfiguracionNumeracionResponse? Configurada(IReadOnlyList<ConfiguracionNumeracionResponse>? configuracion, TipoDocumentoSerie tipo) =>
        configuracion?.FirstOrDefault(c => c.TipoDocumento == tipo && c.SerieActiva);

    /// <summary>Número de línea de un mensaje de la API ("Línea 20000: …") para enlazar a su fila <c>#linea-20000</c>; si no, null.</summary>
    public static int? LineaDelMensaje(string mensaje)
    {
        var coincidencia = PrefijoLinea().Match(mensaje);
        return coincidencia.Success && int.TryParse(coincidencia.Groups[1].Value, out var numero) ? numero : null;
    }

    [GeneratedRegex(@"\ALínea (\d{1,9}):")]
    private static partial Regex PrefijoLinea();
}
```

- [ ] **Step 4: Componentes nuevos**

`Components/Documento/DatosDerivados.razor`:

```razor
@* Datos derivados del documento en solo lectura (spec alta rápida, accesibilidad): <dl> con <dt>/<dd>, nunca <input disabled>. Los
   grupos contables van plegados en "Detalles contables" (<details> nativo, sin JS). *@
<div class="mb-4 rounded-xl border border-slate-100 bg-slate-50 px-4 py-3 text-sm dark:border-slate-700 dark:bg-slate-800/40" data-testid="datos-derivados">
    <dl class="grid grid-cols-1 gap-x-6 gap-y-2 sm:grid-cols-2 lg:grid-cols-3">
        @foreach (var d in Datos)
        {
            @Dato(d)
        }
    </dl>
    @if (DetallesContables.Count > 0)
    {
        <details class="mt-2" data-testid="detalles-contables">
            <summary class="cursor-pointer text-xs font-semibold text-slate-600">Detalles contables</summary>
            <dl class="mt-2 grid grid-cols-1 gap-x-6 gap-y-2 sm:grid-cols-2 lg:grid-cols-3">
                @foreach (var d in DetallesContables)
                {
                    @Dato(d)
                }
            </dl>
        </details>
    }
</div>

@code {
    [Parameter, EditorRequired] public IReadOnlyList<DatoDerivado> Datos { get; set; } = [];
    [Parameter] public IReadOnlyList<DatoDerivado> DetallesContables { get; set; } = [];

    private static RenderFragment Dato(DatoDerivado d) => __builder =>
    {
        <div>
            <dt class="text-xs font-semibold text-slate-500">@d.Etiqueta</dt>
            @if (d.Href is null)
            {
                <dd class="text-slate-800 dark:text-slate-100">@d.Valor</dd>
            }
            else
            {
                <dd class="text-slate-800 dark:text-slate-100"><a class="hover:underline" href="@d.Href">@d.Valor</a></dd>
            }
        </div>
    };
}
```

(El test busca `>NACIONAL</dd>`: el `<dd>` sin enlace termina en `@d.Valor</dd>`.)

`Components/Documento/NumeracionRegistro.razor`:

```razor
@using OpenSource1.Application.Features.Series.Dtos
@* Serie de registro en solo lectura (SerieNumeracionInfo) y, SOLO para el Administrador (CanAdministrar), "Cambiar serie de registro"
   con el SelectorSerie dentro de "Más opciones" (spec alta rápida, Parte 1.4). Sin PuedeCambiar no se pinta ningún campo: la página no
   envía serie y la API usa la configurada (o conserva la del documento). Va DENTRO del EditForm cuando PuedeCambiar. *@
<div class="mb-4" data-testid="numeracion-registro">
    @if (MostrarInfo)
    {
        <SerieNumeracionInfo Codigo="@Codigo" ProximoNumero="@Proximo?.Numero" Aviso="@Proximo?.Aviso" Error="@Proximo?.Error" />
    }
    @if (PuedeCambiar)
    {
        <details class="mt-2" data-testid="mas-opciones">
            <summary class="cursor-pointer text-xs font-semibold text-slate-600">Más opciones</summary>
            <div class="mt-2 max-w-sm">
                <label class="mb-1 block text-sm font-medium text-slate-700" for="@Id">Cambiar serie de registro</label>
                <SelectorSerie Id="@Id" Name="@Name" Series="@Series" Seleccion="@Seleccion" VigenteId="@VigenteId" VigenteCodigo="@Codigo"
                               OpcionConfigurada="@(VigenteId is null)" CargaFallida="@CargaFallida" />
            </div>
        </details>
    }
</div>

@code {
    [Parameter] public string? Codigo { get; set; }
    [Parameter] public ProximoVista? Proximo { get; set; }
    /// <summary>false = solo "Más opciones" (la línea de numeración ya se muestra fuera del formulario, p. ej. en el alta rápida).</summary>
    [Parameter] public bool MostrarInfo { get; set; } = true;
    /// <summary>CanAdministrar: muestra "Más opciones" con el selector.</summary>
    [Parameter] public bool PuedeCambiar { get; set; }
    [Parameter] public string Id { get; set; } = string.Empty;
    /// <summary>Nombre del campo del formulario (p. ej. <c>UpdateInput.SerieRegistroId</c>).</summary>
    [Parameter] public string Name { get; set; } = string.Empty;
    [Parameter] public IReadOnlyList<SerieResponse> Series { get; set; } = [];
    [Parameter] public Guid? Seleccion { get; set; }
    /// <summary>Serie guardada en el documento (null en el alta: la opción vacía "— La configurada —").</summary>
    [Parameter] public Guid? VigenteId { get; set; }
    [Parameter] public bool CargaFallida { get; set; }
}
```

`Components/Documento/AvisoSinSerie.razor`:

```razor
@* Aviso bloqueante (spec alta rápida, Parte 1.5): sin serie configurada o con la serie inactiva no se captura ni se registra. *@
<div class="mb-6" data-testid="aviso-sin-serie">
    <MessageBox Type="danger" Message="@Motivo" />
    <p class="mt-2 text-sm text-slate-600 dark:text-slate-300">
        @if (PuedeConfigurar)
        {
            <a class="font-semibold text-brand-600 hover:underline" href="/configuracion/numeracion">Configurar la numeración</a>
        }
        else
        {
            <span>Pida a un administrador que la configure.</span>
        }
    </p>
</div>

@code {
    [Parameter, EditorRequired] public string Motivo { get; set; } = string.Empty;
    /// <summary>CanAdministrar: enlace a /configuracion/numeracion.</summary>
    [Parameter] public bool PuedeConfigurar { get; set; }
}
```

`Components/Documento/ResumenErrores.razor`:

```razor
@* Resumen de errores (spec alta rápida, accesibilidad): role="alert" y enfocable (tabindex=-1); app.errores.js lo enfoca tras la carga y
   tras la navegación mejorada (Blazor.addEventListener('enhancedload')). Los mensajes "Línea N: …" enlazan a la fila #linea-N. *@
@if (!string.IsNullOrWhiteSpace(Mensaje) || Errores.Count > 0)
{
    <div class="mb-6 rounded-xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-900 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-300"
         role="alert" tabindex="-1" data-resumen-errores data-testid="resumen-errores">
        @if (!string.IsNullOrWhiteSpace(Mensaje))
        {
            <p class="font-semibold">@Mensaje</p>
        }
        @if (Errores.Count > 0)
        {
            <ul class="mt-1 list-disc pl-5">
                @foreach (var e in Errores)
                {
                    @if (NumeracionDocumento.LineaDelMensaje(e) is { } linea)
                    {
                        <li><a class="underline" href="#linea-@linea">@e</a></li>
                    }
                    else
                    {
                        <li>@e</li>
                    }
                }
            </ul>
        }
    </div>
}

@code {
    [Parameter] public string? Mensaje { get; set; }
    [Parameter] public IReadOnlyList<string> Errores { get; set; } = [];
}
```

`Components/Documento/ConfirmarRegistro.razor`:

```razor
@* Confirmación de "Registrar" (spec alta rápida, Parte 1.3): resumen en <dl>, qué hace el registro y "Sí, registrar" / "Seguir
   editando". El EditForm (FormName de la página: "confirm-postear") va aquí; cuando no se muestra, la página lo mantiene VACÍO en el árbol. *@
<ConfirmDialog Title="@Titulo" Message="@Aviso" Variant="success" CancelHref="@SeguirEditandoHref">
    @Errores
    <dl class="grid grid-cols-2 gap-x-4 gap-y-1 rounded-xl border border-brand-100 bg-brand-50 px-4 py-3 text-sm text-brand-900" data-testid="resumen-registro">
        @foreach (var d in Resumen)
        {
            <dt class="font-semibold">@d.Etiqueta</dt>
            <dd>@d.Valor</dd>
        }
    </dl>
    <EditForm Model="Model" FormName="@FormName" OnValidSubmit="OnValidSubmit" class="mt-4">
        <button type="submit" data-testid="si-registrar" class="btn-press rounded-lg bg-brand-600 px-4 py-2 text-sm font-semibold text-white shadow-sm hover:bg-brand-700">Sí, registrar</button>
        <a href="@SeguirEditandoHref" class="btn-press ml-2 rounded-lg border border-slate-300 bg-white px-4 py-2 text-sm font-semibold text-slate-600 shadow-sm hover:bg-slate-50">Seguir editando</a>
    </EditForm>
</ConfirmDialog>

@code {
    [Parameter, EditorRequired] public string Titulo { get; set; } = string.Empty;
    /// <summary>Qué hace el registro y que no se puede deshacer (texto del spec).</summary>
    [Parameter, EditorRequired] public string Aviso { get; set; } = string.Empty;
    [Parameter] public IReadOnlyList<DatoDerivado> Resumen { get; set; } = [];
    [Parameter, EditorRequired] public object Model { get; set; } = default!;
    [Parameter, EditorRequired] public string FormName { get; set; } = string.Empty;
    [Parameter] public EventCallback<EditContext> OnValidSubmit { get; set; }
    [Parameter, EditorRequired] public string SeguirEditandoHref { get; set; } = string.Empty;
    /// <summary>Errores del intento de registro (ResumenErrores), dentro del diálogo.</summary>
    [Parameter] public RenderFragment? Errores { get; set; }
}
```

- [ ] **Step 5: `SerieNumeracionInfo`, `DocumentoCabeceraEditable` y el script**

`Components/Documento/SerieNumeracionInfo.razor` (mismos parámetros):

```razor
@* Serie de registro y próximo número (vista previa sin reserva). Spec alta rápida: "Se numerará con la serie FV · próximo FV-000123
   (el número definitivo se asigna al registrar)". Error prevalece sobre ProximoNumero; el aviso se muestra siempre que haya. Sin
   próximo (documento registrado) solo "Serie de registro: FV". *@
<p class="text-xs text-slate-500" data-testid="serie-registro">
    @if (Codigo is null)
    {
        <span>Serie de registro: <span>—</span></span>
    }
    else if (Error is not null)
    {
        <span>Serie de registro: <strong>@Codigo</strong></span><span class="ml-1 text-red-600">· @Error</span>
    }
    else if (ProximoNumero is not null)
    {
        <span>Se numerará con la serie <strong>@Codigo</strong> · próximo <strong>@ProximoNumero</strong> (el número definitivo se asigna al registrar)</span>
    }
    else
    {
        <span>Serie de registro: <strong>@Codigo</strong></span>
    }
    @if (Aviso is not null)
    {
        <span class="ml-1 text-amber-700" data-testid="aviso-serie">· @Aviso</span>
    }
</p>

@code {
    [Parameter] public string? Codigo { get; set; }
    [Parameter] public string? ProximoNumero { get; set; }
    [Parameter] public string? Aviso { get; set; }
    [Parameter] public string? Error { get; set; }
}
```

(El test `InfoDeSerie_SinCodigo_Guion_SinStrongVacio` busca `Serie de registro: <span>—</span>`: se conserva.)

`DocumentoCabeceraEditable.razor`: añadir el parámetro

```csharp
    /// <summary>Botones propios (spec alta rápida: "Registrar …", "Guardar para después", "Guardar cambios"); sustituyen a "Guardar cabecera".</summary>
    [Parameter] public RenderFragment? Botones { get; set; }
```

y cambiar el bloque del botón por:

```razor
            @if (!CargaFallida)
            {
                @if (Botones is not null)
                {
                    <div class="mt-4 flex flex-wrap items-center gap-3" data-testid="cabecera-botones">@Botones</div>
                }
                else
                {
                    <div class="mt-4"><button type="submit" data-testid="guardar-cabecera" class="btn-press rounded-lg bg-brand-600 px-4 py-2 text-sm font-semibold text-white hover:bg-brand-700">@TextoGuardar</button></div>
                }
            }
```

`src/OpenSource1.Blazor/wwwroot/app.errores.js`:

```js
// app.errores.js — lleva el foco al resumen de errores ([data-resumen-errores], role="alert") de los documentos (spec alta rápida,
// accesibilidad): al cargar la página y tras cada navegación mejorada, que en Static SSR es como llega la respuesta de un POST con
// errores. Se suscribe con Blazor.addEventListener('enhancedload'): 'enhancedload' no se emite en document.
(() => {
  'use strict';

  if (window.__axionErrores) return;
  window.__axionErrores = true;

  function enfocar() {
    const resumen = document.querySelector('[data-resumen-errores]');
    if (resumen instanceof HTMLElement) {
      resumen.focus();
    }
  }

  if (document.readyState === 'loading') {
    window.addEventListener('DOMContentLoaded', enfocar);
  } else {
    enfocar();
  }

  if (window.Blazor?.addEventListener) {
    window.Blazor.addEventListener('enhancedload', enfocar);
  }
})();
```

`Components/App.razor`: tras `<script src="@Assets["app.menus.js"]"></script>` añadir `<script src="@Assets["app.errores.js"]"></script>`.

- [ ] **Step 6: Verde**

Run: `dotnet test tests/OpenSource1.SmokeTests --filter "FullyQualifiedName~ComponentesDocumentoTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/OpenSource1.Blazor/Components/Documento src/OpenSource1.Blazor/wwwroot/app.errores.js src/OpenSource1.Blazor/Components/App.razor \
  tests/OpenSource1.SmokeTests/Blazor/ComponentesDocumentoTests.cs
git commit -m "feat: componentes del documento en preparacion: datos derivados, numeracion, confirmacion y resumen de errores"
```

---
### Task R6: Factura — `/facturas-venta/nueva` (alta rápida) y `/facturas-venta/en-preparacion/{id}`

**Grupo de paralelismo:** Fase 2, serie (tras R5). Sin Docker.

**Files:**
- Modify: `src/OpenSource1.Blazor/Components/LineaFacturaForm.cs` (sin `sealed`; `Validate` virtual)
- Create: `src/OpenSource1.Blazor/Components/AltaFacturaForm.cs`
- Create: `src/OpenSource1.Blazor/Components/LineaFacturaCampos.razor` (campos de línea extraídos de `FacturaVentaBorrador.CamposLinea`)
- Modify: `src/OpenSource1.Blazor/Components/CabeceraFacturaFields.razor` (solo fechas, vencimiento y descripción)
- Modify: `src/OpenSource1.Blazor/Components/Documento/DocumentoVistas.cs` (`RutasPreparacion`, `DatosDocumento`)
- Modify: `src/OpenSource1.Blazor/Components/Documento/DocumentoAvisoPosteado.razor` (texto)
- Modify: `src/OpenSource1.Blazor/Components/VentasOpciones.cs` (`Estado`)
- Rewrite: `src/OpenSource1.Blazor/Components/Pages/FacturaVentaNueva.razor`
- Modify: `src/OpenSource1.Blazor/Components/Pages/FacturaVentaBorrador.razor`
- Create: `src/OpenSource1.Blazor/Components/Pages/FacturaVentaBorradorRedireccion.razor`
- Modify: `src/OpenSource1.Blazor/Components/Pages/FacturaVentaBorradorCabecera.razor`
- Modify: `src/OpenSource1.Blazor/Components/Pages/FacturasVentaBorradores.razor` (SOLO la clase anidada `BorradorForm`)
- Modify: `src/OpenSource1.Blazor/Components/Pages/FacturasVenta.razor` (SOLO el detalle: mensaje de registro, "Ver preparación", destino de la copia)
- Create: `tests/OpenSource1.SmokeTests/Blazor/AltaRapidaFacturaTests.cs`
- Test: `tests/OpenSource1.SmokeTests/Blazor/DocumentoFacturaBorradorTests.cs`, `AccionesFacturasTests.cs`, `ConversionDocumentosUsuariosTests.cs`, `DocumentosPosteadosTests.cs`, `ComponentesDocumentoTests.cs`, `LineaFacturaFormularioTests.cs`

**Interfaces:**
- Consumes (R2, R4, R5): `BorradorCabeceraInput(…, PrimeraLinea)`, `FacturaVentaBorradorResponse.GrupoNegocioCodigo/GrupoIvaNegocioCodigo/GrupoClienteContableCodigo/ImporteTotal`, `IConfiguracionNumeracionApiClient.ListAsync`, `SeriesOpciones.ProximoAsync/ActivasAsync`, `PermisosPagina.PuedeAsync(ApplicationPolicies.CanAdministrar)`, `DatosDerivados`, `NumeracionRegistro`, `AvisoSinSerie`, `ConfirmarRegistro`, `ResumenErrores`, `NumeracionDocumento`, `AccionCabecera`, `DatoDerivado`, `DocumentoCabeceraEditable.Botones`.
- Produces (R7, R8, R9):
  - `static class RutasPreparacion { const string Factura = "/facturas-venta/en-preparacion"; const string Nota = "/notas-credito-venta/en-preparacion"; const string ListaFacturas = "/facturas-venta?estado=preparacion"; const string ListaNotas = "/notas-credito-venta?estado=preparacion"; string Redireccion(string baseRuta, Guid id, string? returnUrl, string listaPorDefecto); string ConTipo(string baseRuta, Guid id, TipoLineaFactura tipo) }`.
  - `static class DatosDocumento { string Fiscal(string? razonSocial, string? numeroDocumento) }`.
  - `AltaFacturaForm : LineaFacturaForm` (`SocioNegocioId`, `FechaRegistroTexto`, `FechaDocumentoTexto`, `FechaVencimientoTexto`, `DescripcionDocumento`, `SerieRegistroId`, `BorradorCabeceraInput ToAlta(TipoLineaFactura tipo, Guid? productoId, Guid? unidadMedidaId, bool esAdministrador)`).
  - `<LineaFacturaCampos Model Prefix Tipo Vigente Almacenes Cuentas GruposIva AlmacenCabeceraCodigo OpcionesFallidas />`.
  - `FacturasVentaBorradores.BorradorForm.Accion : string?` (`AccionCabecera.Registrar`/`Despues`); `BorradorForm` pierde `SocioNegocioId`, `SocioNegocioFacturarAId`, `AlmacenId` y `SerieBorradorId`.
  - Rutas: `/facturas-venta/nueva` (formulario `alta-factura`, campos `AltaInput.*`, botón `data-testid="agregar-linea"`); `/facturas-venta/en-preparacion/{id}` (`?registrar=true` abre la confirmación; `?postear=true` se acepta como alias); `/facturas-venta/borradores/{id}` y `/…/editar` redirigen.
  - Destinos: alta → `/facturas-venta/en-preparacion/{id}[?tipo=n]&ok=linea_creada`; registrar → `/facturas-venta/{numero}?ok=registrada[&aviso=…]`; guardar para después → `/facturas-venta?estado=preparacion&ok=guardada`; descartar → `/facturas-venta?estado=preparacion&ok=descartada` (o el `returnUrl`).
  - `data-testid` nuevos: `agregar-linea`, `registrar`, `guardar-despues`, `descartar`, `motivo-registrar`, `aviso-liberada`, `volver-a-editar`, `cliente-elegido`, `clientes-encontrados`.

- [ ] **Step 1: Tests de las páginas nuevas (fallan)**

Crear `tests/OpenSource1.SmokeTests/Blazor/AltaRapidaFacturaTests.cs`:

```csharp
extern alias BlazorApp;
using System.Net;
using BlazorApp::OpenSource1.Blazor.Services;
using Moq;
using OpenSource1.Application.Features.Almacenes.Dtos;
using OpenSource1.Application.Features.ConfiguracionNumeracion.Dtos;
using OpenSource1.Application.Features.FacturasVenta.Borradores.Dtos;
using OpenSource1.Application.Features.FacturasVenta.Calculo;
using OpenSource1.Application.Features.FacturasVenta.Posteo;
using OpenSource1.Application.Features.Series.Dtos;
using OpenSource1.Application.Features.SociosNegocio.Dtos;
using OpenSource1.Core.Common;
using OpenSource1.Core.Enums;
using OpenSource1.SmokeTests.TestInfrastructure;

namespace OpenSource1.SmokeTests.Blazor;

/// <summary>
/// Spec alta rápida (Parte 1): /facturas-venta/nueva (cliente por GET + "Usar", datos derivados en &lt;dl&gt;, numeración y primera
/// línea en un POST) y /facturas-venta/en-preparacion/{id} (Registrar factura con confirmación, Guardar para después, Descartar,
/// sin Liberar/Reabrir, serie solo para el Administrador, aviso sin serie, redirecciones de las rutas antiguas).
/// </summary>
public sealed class AltaRapidaFacturaTests
{
    private static readonly Guid IdSocio = Guid.Parse("7d0c9a51-0000-0000-0000-0000000006a1");
    private static readonly Guid IdBorrador = Guid.Parse("7d0c9a51-0000-0000-0000-0000000006a2");
    private static readonly Guid IdLiberada = Guid.Parse("7d0c9a51-0000-0000-0000-0000000006a3");
    private static readonly Guid IdSerieFv = Guid.Parse("7d0c9a51-0000-0000-0000-0000000006a4");
    private static readonly Guid IdSerieOtra = Guid.Parse("7d0c9a51-0000-0000-0000-0000000006a5");
    private static readonly Guid IdNuevo = Guid.Parse("7d0c9a51-0000-0000-0000-0000000006a6");
    private const string Nueva = "/facturas-venta/nueva";
    private static string Url(Guid id) => $"/facturas-venta/en-preparacion/{id}";

    // ----- /facturas-venta/nueva -----

    [Fact]
    public async Task Nueva_SinCliente_BuscadorConUsar_SinFormularioDeLinea()
    {
        using var app = Configurar(new BlazorSsrFactory());

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), $"{Nueva}?socioQuery=Com");

        Assert.Contains("data-testid=\"buscar-socio\"", html);
        Assert.Contains($"href=\"{Nueva}?socioId={IdSocio}\"", html);
        Assert.Contains(">Usar</a>", html);
        Assert.DoesNotContain("data-testid=\"agregar-linea\"", html);
        Assert.Contains("name=\"_handler\" value=\"alta-factura\"", html);
    }

    [Fact]
    public async Task Nueva_ConCliente_DatosDerivadosEnDl_NumeracionYPrimeraLinea()
    {
        using var app = Configurar(new BlazorSsrFactory());

        var html = HtmlSsr.Decodificar(await HtmlSsr.HtmlAsync(app.Cliente("Ejecutor"), $"{Nueva}?socioId={IdSocio}&tipo=3"));

        var datos = html[html.IndexOf("data-testid=\"datos-derivados\"", StringComparison.Ordinal)..];
        Assert.Contains(">Comercial Uno SRL 101000001</dd>", datos);
        Assert.Contains(">PRINC — Principal (predeterminado)</dd>", datos);
        Assert.Contains("Detalles contables", datos);
        Assert.Contains(">NACIONAL</dd>", datos);
        Assert.Contains("Se numerará con la serie <strong>FV</strong> · próximo <strong>FV-000123</strong> (el número definitivo se asigna al registrar)", html);
        Assert.Contains("name=\"AltaInput.FechaRegistroTexto\"", html);
        Assert.Contains("name=\"AltaInput.DescripcionDocumento\"", html);
        Assert.Contains("name=\"AltaInput.Descripcion\"", html);
        Assert.Contains("data-testid=\"agregar-linea\"", html);
        Assert.DoesNotContain("name=\"AltaInput.SerieRegistroId\"", html);
        // Tipo comentario: sin almacén de línea; la cabecera nunca tiene selector de almacén (lo fija el servidor).
        Assert.DoesNotContain("name=\"AltaInput.AlmacenId\"", html);
    }

    [Fact]
    public async Task Nueva_Administrador_VeCambiarSerieEnMasOpciones()
    {
        using var app = Configurar(new BlazorSsrFactory());

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), $"{Nueva}?socioId={IdSocio}&tipo=3");

        Assert.Contains("data-testid=\"mas-opciones\"", html);
        Assert.Contains("name=\"AltaInput.SerieRegistroId\"", html);
    }

    [Fact]
    public async Task Nueva_AgregarLinea_CreaConLaPrimeraLinea_YAbreLaFacturaEnPreparacion()
    {
        using var app = Configurar(new BlazorSsrFactory());
        var api = app.Simular<IFacturaVentaApiClient>();
        api.Setup(c => c.CreateBorradorAsync(
                It.Is<BorradorCabeceraInput>(i => i.SocioNegocioId == IdSocio && i.AlmacenId == null && i.SerieBorradorId == null
                    && i.Descripcion == "Pedido 7" && i.PrimeraLinea!.Tipo == TipoLineaFactura.Comentario && i.PrimeraLinea.Descripcion == "Servicio"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<FacturaVentaBorradorResponse>(true, "ok", new FacturaVentaBorradorResponse { Id = IdNuevo }));

        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), $"{Nueva}?socioId={IdSocio}&tipo=3", "alta-factura", new Dictionary<string, string>
        {
            ["AltaInput.SocioNegocioId"] = IdSocio.ToString(), ["AltaInput.Tipo"] = "3", ["AltaInput.Descripcion"] = "Servicio",
            ["AltaInput.DescripcionDocumento"] = "Pedido 7", ["AltaInput.FechaRegistroTexto"] = "2026-10-01",
        });

        Assert.Equal($"{Url(IdNuevo)}?tipo=3&ok=linea_creada", FormulariosSsr.Destino(respuesta));
    }

    [Fact]
    public async Task Nueva_ErrorDeLaApi_ConservaLoCapturado_YNoNavega()
    {
        using var app = Configurar(new BlazorSsrFactory());
        app.Simular<IFacturaVentaApiClient>()
            .Setup(c => c.CreateBorradorAsync(It.IsAny<BorradorCabeceraInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<FacturaVentaBorradorResponse>(
                false, "Los datos enviados no son válidos.", Errors: ["La fecha de registro no está dentro de las fechas permitidas."]));

        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), $"{Nueva}?socioId={IdSocio}&tipo=3", "alta-factura", new Dictionary<string, string>
        {
            ["AltaInput.SocioNegocioId"] = IdSocio.ToString(), ["AltaInput.Tipo"] = "3", ["AltaInput.Descripcion"] = "Servicio",
            ["AltaInput.DescripcionDocumento"] = "Pedido 7", ["AltaInput.FechaRegistroTexto"] = "2020-01-01",
        });
        var html = HtmlSsr.Decodificar(await respuesta.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Contains("data-testid=\"resumen-errores\"", html);
        Assert.Contains("La fecha de registro no está dentro de las fechas permitidas.", html);
        Assert.Contains("value=\"Pedido 7\"", html);
        Assert.Contains("value=\"Servicio\"", html);
        Assert.Contains("value=\"2020-01-01\"", html);
    }

    [Fact]
    public async Task Nueva_NoAdministrador_IgnoraLaSerieForzada()
    {
        using var app = Configurar(new BlazorSsrFactory());
        var api = app.Simular<IFacturaVentaApiClient>();
        api.Setup(c => c.CreateBorradorAsync(It.IsAny<BorradorCabeceraInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<FacturaVentaBorradorResponse>(true, "ok", new FacturaVentaBorradorResponse { Id = IdNuevo }));

        await FormulariosSsr.EnviarAsync(app.Cliente("Ejecutor"), $"{Nueva}?socioId={IdSocio}&tipo=3", "alta-factura", new Dictionary<string, string>
        {
            ["AltaInput.SocioNegocioId"] = IdSocio.ToString(), ["AltaInput.Tipo"] = "3", ["AltaInput.Descripcion"] = "Servicio",
            ["AltaInput.SerieRegistroId"] = IdSerieOtra.ToString(),
        });

        api.Verify(c => c.CreateBorradorAsync(It.Is<BorradorCabeceraInput>(i => i.SerieRegistroId == null), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("Administrador", true)]
    [InlineData("Ejecutor", false)]
    public async Task Nueva_SinSerieConfigurada_AvisoBloqueante_SinFormulario(string rol, bool enlace)
    {
        using var app = Configurar(new BlazorSsrFactory(), serieRegistroActiva: false);

        var html = HtmlSsr.Decodificar(await HtmlSsr.HtmlAsync(app.Cliente(rol), $"{Nueva}?socioId={IdSocio}&tipo=3"));

        Assert.Contains("data-testid=\"aviso-sin-serie\"", html);
        Assert.Contains("No hay una serie configurada para facturas de venta", html);
        Assert.Equal(enlace, html.Contains("href=\"/configuracion/numeracion\"", StringComparison.Ordinal));
        Assert.Equal(!enlace, html.Contains("Pida a un administrador que la configure.", StringComparison.Ordinal));
        Assert.DoesNotContain("data-testid=\"agregar-linea\"", html);
    }

    // ----- /facturas-venta/en-preparacion/{id} -----

    [Fact]
    public async Task EnPreparacion_TextosRegistrarYDescartar_SinLiberarNiPostear_YNombresAccesiblesDeFila()
    {
        using var app = Configurar(new BlazorSsrFactory());

        var html = HtmlSsr.Decodificar(await HtmlSsr.HtmlAsync(app.Cliente(), Url(IdBorrador)));

        Assert.Contains(">Factura en preparación</h1>", html);
        Assert.Contains("data-testid=\"datos-derivados\"", html);
        Assert.Contains("data-testid=\"registrar\"", html);
        Assert.Contains(">Registrar factura</button>", html);
        Assert.Contains(">Guardar para después</button>", html);
        Assert.Contains(">Descartar factura</a>", html);
        Assert.DoesNotContain(">Liberar<", html);
        Assert.DoesNotContain(">Postear<", html);
        Assert.DoesNotContain(">Borrador ", html);
        Assert.Contains("aria-label=\"Modificar línea 1\"", html);
        Assert.Contains("aria-label=\"Eliminar línea 1\"", html);
        Assert.Contains("id=\"linea-10000\"", html);
        Assert.DoesNotContain("name=\"UpdateInput.AlmacenId\"", html);
        Assert.DoesNotContain("name=\"UpdateInput.SocioNegocioId\"", html);
    }

    [Theory]
    [InlineData("registrar", "/facturas-venta/en-preparacion/7d0c9a51-0000-0000-0000-0000000006a2?registrar=true")]
    [InlineData("despues", "/facturas-venta?estado=preparacion&ok=guardada")]
    [InlineData("", "/facturas-venta/en-preparacion/7d0c9a51-0000-0000-0000-0000000006a2?ok=modificado")]
    public async Task EnPreparacion_GuardarCabecera_SegunLaAccion(string accion, string destino)
    {
        using var app = Configurar(new BlazorSsrFactory());
        app.Simular<IFacturaVentaApiClient>()
            .Setup(c => c.UpdateBorradorAsync(IdBorrador, It.Is<BorradorCabeceraInput>(i => i.SocioNegocioId == IdSocio && i.AlmacenId == null), 11, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<FacturaVentaBorradorResponse>(true, "ok"));

        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), Url(IdBorrador), "guardar-cabecera", Cabecera(accion));

        Assert.Equal(destino, FormulariosSsr.Destino(respuesta));
    }

    [Fact]
    public async Task EnPreparacion_NoAdministrador_IgnoraLaSerieForzada_YAdministradorLaEnvia()
    {
        using var app = Configurar(new BlazorSsrFactory());
        var api = app.Simular<IFacturaVentaApiClient>();
        api.Setup(c => c.UpdateBorradorAsync(IdBorrador, It.IsAny<BorradorCabeceraInput>(), 11, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<FacturaVentaBorradorResponse>(true, "ok"));
        var campos = new Dictionary<string, string>(Cabecera("")) { ["UpdateInput.SerieRegistroId"] = IdSerieOtra.ToString() };

        var supervisor = await HtmlSsr.HtmlAsync(app.Cliente("Supervisor"), Url(IdBorrador));
        await FormulariosSsr.EnviarAsync(app.Cliente("Supervisor"), Url(IdBorrador), "guardar-cabecera", campos);
        await FormulariosSsr.EnviarAsync(app.Cliente(), Url(IdBorrador), "guardar-cabecera", campos);

        Assert.DoesNotContain("name=\"UpdateInput.SerieRegistroId\"", supervisor);
        api.Verify(c => c.UpdateBorradorAsync(IdBorrador, It.Is<BorradorCabeceraInput>(i => i.SerieRegistroId == null), 11, It.IsAny<CancellationToken>()), Times.Once);
        api.Verify(c => c.UpdateBorradorAsync(IdBorrador, It.Is<BorradorCabeceraInput>(i => i.SerieRegistroId == IdSerieOtra), 11, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnPreparacion_ConfirmacionDeRegistro_ResumenYSiRegistrar_VaALaFacturaRegistrada()
    {
        using var app = Configurar(new BlazorSsrFactory());
        app.Simular<IFacturaVentaApiClient>()
            .Setup(c => c.PostearAsync(IdBorrador, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<ResultadoPosteoFactura>(true, "ok", new ResultadoPosteoFactura("FV-000123", 112.10m, "00000005", null)));

        var html = HtmlSsr.Decodificar(await HtmlSsr.HtmlAsync(app.Cliente(), $"{Url(IdBorrador)}?registrar=true"));
        var legado = await HtmlSsr.HtmlAsync(app.Cliente(), $"{Url(IdBorrador)}?postear=true");
        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), $"{Url(IdBorrador)}?registrar=true", "confirm-postear", new Dictionary<string, string>());

        Assert.Contains(">Registrar factura</h2>", html);
        Assert.Contains("Se descontará el inventario y se generará el asiento; no se puede deshacer.", html);
        Assert.Contains("<dd>112.10</dd>", html);
        Assert.Contains("<dd>FV-000123</dd>", html);
        Assert.Contains(">Sí, registrar</button>", html);
        Assert.Contains(">Seguir editando</a>", html);
        Assert.Contains("data-testid=\"si-registrar\"", legado);
        Assert.Equal("/facturas-venta/FV-000123?ok=registrada", FormulariosSsr.Destino(respuesta));
    }

    [Fact]
    public async Task EnPreparacion_SerieQueYaNoNumera_RegistrarDeshabilitadoConMotivo_YPostForzadoMuestraLaApi()
    {
        using var app = Configurar(new BlazorSsrFactory(), proximoError: "La serie 'FV' está inactiva.");
        app.Simular<IFacturaVentaApiClient>()
            .Setup(c => c.PostearAsync(IdBorrador, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<ResultadoPosteoFactura>(false, "La serie FV está inactiva."));

        var html = HtmlSsr.Decodificar(await HtmlSsr.HtmlAsync(app.Cliente(), Url(IdBorrador)));
        var forzado = await FormulariosSsr.EnviarAsync(app.Cliente(), Url(IdBorrador), "confirm-postear", new Dictionary<string, string>());

        Assert.Contains("data-testid=\"motivo-registrar\"", html);
        Assert.Contains("No se puede registrar: La serie 'FV' está inactiva.", html);
        Assert.Contains("<button type=\"button\" disabled data-testid=\"registrar\"", html);
        Assert.Contains("La serie FV está inactiva.", HtmlSsr.Decodificar(await forzado.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task EnPreparacion_Liberada_RegistrarYVolverAEditar_SinLiberar()
    {
        using var app = Configurar(new BlazorSsrFactory());
        app.Simular<IFacturaVentaApiClient>()
            .Setup(c => c.ReabrirAsync(IdLiberada, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<FacturaVentaBorradorResponse>(true, "ok"));

        var html = HtmlSsr.Decodificar(await HtmlSsr.HtmlAsync(app.Cliente(), Url(IdLiberada)));
        var reabierta = await FormulariosSsr.EnviarAsync(app.Cliente(), Url(IdLiberada), "reabrir", new Dictionary<string, string>());

        Assert.Contains("data-testid=\"aviso-liberada\"", html);
        Assert.Contains($"href=\"{Url(IdLiberada)}?registrar=true\"", html);
        Assert.Contains(">Volver a editar</button>", html);
        Assert.DoesNotContain(">Liberar<", html);
        Assert.Equal($"{Url(IdLiberada)}?ok=reabierto", FormulariosSsr.Destino(reabierta));
    }

    [Fact]
    public async Task Descartar_ConConfirmacion_VuelveALaListaEnPreparacion()
    {
        using var app = Configurar(new BlazorSsrFactory());
        app.Simular<IFacturaVentaApiClient>()
            .Setup(c => c.DeleteBorradorAsync(IdBorrador, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<bool>(true, "ok", true));

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), $"{Url(IdBorrador)}?eliminar=true");
        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), $"{Url(IdBorrador)}?eliminar=true", "delete-borrador", new Dictionary<string, string>());

        Assert.Contains(">Descartar factura</h2>", HtmlSsr.Decodificar(html));
        Assert.Contains(">Sí, descartar</button>", HtmlSsr.Decodificar(html));
        Assert.Equal("/facturas-venta?estado=preparacion&ok=descartada", FormulariosSsr.Destino(respuesta));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("/editar", "")]
    [InlineData("?returnUrl=%2Ffacturas-venta%3Festado%3Dpreparacion", "?returnUrl=%2Ffacturas-venta%3Festado%3Dpreparacion")]
    [InlineData("?returnUrl=https%3A%2F%2Fevil.example%2Fx", "?returnUrl=%2Ffacturas-venta%3Festado%3Dpreparacion")]
    public async Task RutasAntiguas_RedirigenAEnPreparacion_ConservandoUnRetornoLocal(string sufijo, string query)
    {
        using var app = Configurar(new BlazorSsrFactory());

        var respuesta = await app.Cliente().GetAsync($"/facturas-venta/borradores/{IdBorrador}{sufijo}");

        Assert.Equal(HttpStatusCode.Redirect, respuesta.StatusCode);
        Assert.Equal($"{Url(IdBorrador)}{query}", FormulariosSsr.Destino(respuesta));
    }

    private static Dictionary<string, string> Cabecera(string accion) => new()
    {
        ["UpdateInput.Xmin"] = "11", ["UpdateInput.FechaRegistroTexto"] = "2026-10-01", ["UpdateInput.FechaDocumentoTexto"] = "2026-10-01",
        ["UpdateInput.FechaVencimientoTexto"] = "2026-10-31", ["UpdateInput.Descripcion"] = "Pedido 7", ["UpdateInput.Accion"] = accion,
    };

    private static FacturaVentaBorradorResponse Borrador(Guid id, EstadoFacturaBorrador estado) => new()
    {
        Id = id, Numero = "00000042", Estado = estado, SocioNegocioId = IdSocio, SocioNegocioCodigo = "C-001", SocioNegocioNombre = "Comercial Uno",
        SocioNegocioFacturarAId = IdSocio, SocioNegocioFacturarACodigo = "C-001", NombreFacturacion = "Comercial Uno",
        RazonSocialFacturacion = "Comercial Uno SRL", NumeroDocumentoFiscal = "101000001", TerminoPagoCodigo = "30D", Moneda = "DOP",
        AlmacenCodigo = "PRINC", GrupoNegocioCodigo = "NACIONAL", GrupoIvaNegocioCodigo = "ITBIS", GrupoClienteContableCodigo = "GENERAL",
        SerieBorradorCodigo = "FV-BORR", SerieRegistroId = IdSerieFv, SerieRegistroCodigo = "FV", FechaRegistro = new DateOnly(2026, 10, 1),
        FechaDocumento = new DateOnly(2026, 10, 1), FechaVencimiento = new DateOnly(2026, 10, 31), NumeroLineas = 1, ImporteTotal = 112.10m, Xmin = 11,
    };

    private static BlazorSsrFactory Configurar(BlazorSsrFactory app, bool serieRegistroActiva = true, string? proximoError = null)
    {
        var facturas = app.Simular<IFacturaVentaApiClient>();
        facturas.Setup(c => c.GetBorradorAsync(IdBorrador, It.IsAny<CancellationToken>())).ReturnsAsync(Borrador(IdBorrador, EstadoFacturaBorrador.Abierta));
        facturas.Setup(c => c.GetBorradorAsync(IdLiberada, It.IsAny<CancellationToken>())).ReturnsAsync(Borrador(IdLiberada, EstadoFacturaBorrador.Liberada));
        facturas.Setup(c => c.ListLineasAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new LineaFacturaVentaBorradorResponse
            {
                Id = Guid.NewGuid(), NumeroLinea = 10000, Tipo = TipoLineaFactura.Comentario, Descripcion = "Servicio", Xmin = 3,
            },
        ]);
        facturas.Setup(c => c.GetTotalesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TotalesFactura([new GrupoIvaCalculado("ITBIS18", 18m, 95m, 17.10m)], 95m, 17.10m, 112.10m));

        var series = app.Simular<ISerieApiClient>();
        series.Setup(c => c.ActivasDelTipoAsync(It.IsAny<TipoDocumentoSerie>(), It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new SerieResponse { Id = IdSerieFv, Codigo = "FV", Descripcion = "Facturas", TipoDocumento = TipoDocumentoSerie.FacturaVenta, Activa = true },
            new SerieResponse { Id = IdSerieOtra, Codigo = "FV2", Descripcion = "Sucursal", TipoDocumento = TipoDocumentoSerie.FacturaVenta, Activa = true },
        ]);
        series.Setup(c => c.ProximoAsync(IdSerieFv, It.IsAny<CancellationToken>())).ReturnsAsync(proximoError is null
            ? new VentaOperationResult<ProximoNumeroResponse>(true, "ok", new ProximoNumeroResponse("FV-000123", null))
            : new VentaOperationResult<ProximoNumeroResponse>(false, proximoError));

        app.Simular<IConfiguracionNumeracionApiClient>()
            .Setup(c => c.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new ConfiguracionNumeracionResponse { TipoDocumento = TipoDocumentoSerie.BorradorFacturaVenta, SerieCodigo = "FV-BORR", SerieActiva = true },
                new ConfiguracionNumeracionResponse { TipoDocumento = TipoDocumentoSerie.FacturaVenta, SerieId = IdSerieFv, SerieCodigo = "FV", SerieActiva = serieRegistroActiva },
            ]);

        var socio = new SocioNegocioResponse
        {
            Id = IdSocio, Codigo = "C-001", NombreComercial = "Comercial Uno", RazonSocial = "Comercial Uno SRL", NumeroDocumentoFiscal = "101000001",
            GrupoNegocioCodigo = "NACIONAL", GrupoIvaNegocioCodigo = "ITBIS", GrupoClienteContableCodigo = "GENERAL",
        };
        var socios = app.Simular<ISocioNegocioApiClient>();
        socios.Setup(c => c.GetByIdAsync(IdSocio, It.IsAny<CancellationToken>())).ReturnsAsync(socio);
        socios.Setup(c => c.ListAsync(It.IsAny<SocioNegocioSearchFilter?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<SocioNegocioResponse>([socio], 1, 50, 1));
        app.Simular<IAlmacenApiClient>()
            .Setup(c => c.ListAsync(It.IsAny<AlmacenSearchFilter?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<AlmacenResponse>([new AlmacenResponse { Id = Guid.NewGuid(), Codigo = "PRINC", Nombre = "Principal", EsPredeterminado = true }], 1, 50, 1));
        return app;
    }
}
```

(`ISocioNegocioApiClient.GetByIdAsync(Guid, CancellationToken)`, `SocioNegocioSearchFilter` y `AlmacenSearchFilter` son los tipos que
ya usan `DocumentoFacturaBorradorTests`/`AccionesFacturasTests`; comprobar los nombres exactos allí.)

`DocumentoFacturaBorradorTests.cs` (la página única existente pasa a `/facturas-venta/en-preparacion/{id}`):
- `Url(Guid id) => $"/facturas-venta/en-preparacion/{id}"`.
- `PaginaUnica_CabeceraEditable_Lineas_FilaNueva_TotalesYBarra`: quitar las aserciones de `value="liberar"`, `?postear=true`, `?eliminar=true` de la barra, `cabecera-datos-fijos`, "Se muestran los primeros 50 clientes"; cambiar `"Próximo número: <strong>00000013</strong>"` por `"próximo <strong>00000013</strong>"`; añadir `Assert.Contains($"href=\"{Url(IdAbierto)}?eliminar=true\" data-testid=\"descartar\"", html);`, `Assert.Contains("data-testid=\"datos-derivados\"", html);`, `Assert.Contains(">Término de pago</dt>", html);`; el enlace "Cancelar" de la tarjeta pasa a `<a href="/facturas-venta?estado=preparacion" data-testid="cancelar-cabecera"`.
- `ReturnUrlExterno_CaeAlListado`: el `cancelar-cabecera` esperado pasa a `/facturas-venta?estado=preparacion`; la redirección de `/editar` va a la ruta antigua `/facturas-venta/borradores/{id}/editar` y su destino es `{Url(IdAbierto)}?returnUrl=%2Ffacturas-venta%3Festado%3Dpreparacion`.
- `BorradorPosteada_PostObsoletoDePosteo_MuestraElConflictoDeLaApi`: la URL de la petición `?registrar=true`.
- `GuardarCabecera_EnviaLaSerieDeRegistroYElXmin_YVuelveALaMismaPagina` (Administrador): sin cambios salvo que el helper `Cabecera(...)` ya no envía `UpdateInput.SocioNegocioId`/`AlmacenId` y el `It.Is` comprueba además `i.SocioNegocioId == IdSocio && i.AlmacenId == null`.
- `Eliminar_ConConfirmacion_VuelveAlListado` → destino `"/facturas-venta?estado=preparacion&ok=descartada"` y texto `"Sí, descartar"`.
- `Postear_VaALaFactura_ConAviso` → renombrar `Registrar_VaALaFactura_ConAviso`; GET/POST a `?registrar=true`; destino `StartsWith("/facturas-venta/FV-000013?ok=registrada&aviso=")`.
- `RutaEditar_RedirigeALaPaginaUnica_ConservandoElRetorno`: pedir `/facturas-venta/borradores/{IdAbierto}/editar?returnUrl=%2Ffacturas-venta%3Festado%3Dpreparacion` y esperar `{Url(IdAbierto)}?returnUrl=%2Ffacturas-venta%3Festado%3Dpreparacion`.
- `Nueva_ConSelectoresDeSerie_EnviaLaSerieDeRegistroElegida`: eliminar (lo sustituyen los tests de `AltaRapidaFacturaTests`).
- `Listado_FiltroPosteada`: se mantiene hasta R8 (la lista aún existe).
- `FalloAlCargarSeries_NoBloqueaGuardar_YConservaLaSerieActual` (Administrador): el selector está dentro de `mas-opciones`; las aserciones del `(actual)` y de `guardar-cabecera` se conservan cambiando `data-testid="guardar-cabecera"` por `data-testid="registrar"`.
- `Liberada_CabeceraEnLecturaConProximo_SinFilaNueva`: añadir `Assert.Contains("data-testid=\"aviso-liberada\"", html);` y quitar las aserciones de "Reabrir".
- `ConRetorno_VolverYEliminarLoConservan`: los enlaces esperados con `?registrar=true&{retorno}` en lugar de `?postear=true&{retorno}`.
- En `Cabecera(Guid serie)`: quitar `UpdateInput.SocioNegocioId` y `UpdateInput.AlmacenId`.
- En `Configurar`: añadir `GrupoNegocioCodigo = "NACIONAL"` al `Borrador(...)` de prueba si alguna aserción lo usa.

`AccionesFacturasTests.cs`:
- `NuevaFactura_ConSocio_PrecargaYBloqueaElCliente` → `NuevaFactura_ConSocio_MuestraElClienteEnSoloLectura`: GET `/facturas-venta/nueva?socioId={IdSocio}` contiene `data-testid="cliente-elegido"`, `data-testid="datos-derivados"` y el enlace `Cambiar cliente` a `/facturas-venta/nueva`; NO contiene `<select` con `name="AltaInput.SocioNegocioId"`.
- `NuevaFactura_SocioInexistente_AvisoYSelectorNormal` → el aviso en `resumen-errores` y el buscador `data-testid="buscar-socio"`.
- `NuevaFactura_Guardar_CreaElBorradorConElCliente` → POST `alta-factura` (tipo 3, `AltaInput.Descripcion`) a `/facturas-venta/nueva?socioId={IdSocio}&tipo=3`; destino `/facturas-venta/en-preparacion/{borrador}?tipo=3&ok=linea_creada`.
- `NuevaFactura_SocioOcultoManipulado_UsaElClienteDeLaQuery` → mismo POST con `AltaInput.SocioNegocioId` = otro Guid; el `It.Is` exige `SocioNegocioId == IdSocio`.
- `AltaDesdeElCliente_CancelarVuelveAlCliente` (fila de la factura): el enlace "Volver" de la barra lleva al `returnUrl` del cliente.
- `EditorBorradorFactura_LaCabeceraSeEditaEnLaMismaPagina_SinRutaEditar`: URL `/facturas-venta/en-preparacion/{IdBorradorAbierto}`.
- Simular `IConfiguracionNumeracionApiClient` en su `Configurar` con las dos filas activas (FV-BORR, FV).

`ConversionDocumentosUsuariosTests.cs`:
- `Nuevo_RenderizaFormularioEnTarjeta`: la fila `InlineData("/facturas-venta/nueva", "add-borrador")` pasa a `InlineData("/facturas-venta/nueva", "alta-factura")` (el formulario está en el árbol, vacío, sin cliente; si el test exige `EntityFormPage`, mover este caso a `AltaRapidaFacturaTests.Nueva_SinCliente_BuscadorConUsar_SinFormularioDeLinea`, que ya lo cubre, y borrar la fila).
- `FacturaNueva_CreaYAbreElBorrador` → POST `alta-factura` como arriba; destino `en-preparacion`.
- `CabeceraFactura_ComponenteCompartido_EnAltaYEdicion` → `LineaFactura_ComponenteCompartido_EnAltaYEnPreparacion`: `/facturas-venta/nueva?socioId=…&tipo=3` contiene `name="AltaInput.Descripcion"` y `/facturas-venta/en-preparacion/{id}?tipo=3` contiene `name="AddInput.Descripcion"`; ambos con el mismo texto de ayuda del precio en tipo 1 (`data-testid="ayuda-precio"`).

`DocumentosPosteadosTests.cs` (detalle de la factura registrada):
- `Factura_DocumentoSoloLectura_EnlaceAlBorrador_YCrearCopiarABorrador`: el campo "Borrador de origen" pasa a "Preparación" con el enlace `Ver preparación` a `/facturas-venta/en-preparacion/{IdBorrador}`.
- `Factura_SinBorradorDeOrigen_MuestraElNumeroSinEnlace`: el texto sin enlace es `—`.
- `CopiarABorrador_SinAvisos_AbreElBorrador` → destino `/facturas-venta/en-preparacion/{IdNuevo}?ok=copiado`; `CopiarABorrador_ConAvisos_LosMuestraConElEnlace` → `href="/facturas-venta/en-preparacion/{IdNuevo}"`.
- `Factura_AvisoDeNumeracion_SoloTrasElPosteo`: añadir el caso `?ok=registrada&aviso=…` (el aviso se muestra igual que con `posteada`) y `Assert.Contains("Factura FV-… registrada.", …)` con el número del mock, y el enlace `Nueva factura` a `/facturas-venta/nueva`.

`ComponentesDocumentoTests.cs`, `Estados_IncluyenPosteada` → `Estados_EnPreparacionYRegistrada`:
`Assert.Equal("En preparación", VentasOpciones.Estado(EstadoFacturaBorrador.Abierta)); Assert.Equal("En preparación (liberada)", VentasOpciones.Estado(EstadoFacturaBorrador.Liberada)); Assert.Equal("Registrada", VentasOpciones.Estado(EstadoFacturaBorrador.Posteada));`
(las aserciones de `EstadoNota` las cambia R7).

`LineaFacturaFormularioTests.cs`, añadir:

```csharp
    [Fact]
    public void AltaFactura_ValidaLaLineaYLasFechas_YEnviaLaCabeceraConLaPrimeraLinea()
    {
        var form = new AltaFacturaForm
        {
            SocioNegocioId = Guid.NewGuid(), Tipo = (short)TipoLineaFactura.Comentario, Descripcion = "Servicio",
            FechaRegistroTexto = "2026-10-01", FechaVencimientoTexto = "no-es-fecha", DescripcionDocumento = " Pedido 7 ", SerieRegistroId = Guid.NewGuid(),
        };

        var errores = Validar(form);
        form.FechaVencimientoTexto = null;
        var usuario = form.ToAlta(TipoLineaFactura.Comentario, null, null, esAdministrador: false);
        var administrador = form.ToAlta(TipoLineaFactura.Comentario, null, null, esAdministrador: true);

        Assert.Contains(errores, e => e.MemberNames.Contains(nameof(AltaFacturaForm.FechaVencimientoTexto)));
        Assert.Equal(("Pedido 7", (Guid?)null, (Guid?)null), (usuario.Descripcion, usuario.AlmacenId, usuario.SerieRegistroId));
        Assert.Equal(form.SerieRegistroId, administrador.SerieRegistroId);
        Assert.Equal((TipoLineaFactura.Comentario, "Servicio"), (usuario.PrimeraLinea!.Tipo, usuario.PrimeraLinea.Descripcion));
        Assert.Equal(new DateOnly(2026, 10, 1), usuario.FechaRegistro);
    }
```

(`Validar` es el helper existente de la clase; si su parámetro es `LineaFacturaForm`, acepta `AltaFacturaForm` por herencia.)

- [ ] **Step 2: Ejecutarlos (fallan)**

Run: `dotnet test tests/OpenSource1.SmokeTests --filter "FullyQualifiedName~AltaRapidaFacturaTests|FullyQualifiedName~LineaFacturaFormularioTests"`
Expected: FAIL al compilar (`AltaFacturaForm`, `PrimeraLinea` en el formulario y la ruta nueva no existen).

- [ ] **Step 3: Formularios**

`Components/LineaFacturaForm.cs`: `public class LineaFacturaForm : IValidatableObject` (sin `sealed`) y
`public virtual IEnumerable<ValidationResult> Validate(ValidationContext validationContext)`.

`Components/AltaFacturaForm.cs`:

```csharp
using System.ComponentModel.DataAnnotations;
using OpenSource1.Blazor.Services;
using OpenSource1.Core.Enums;

namespace OpenSource1.Blazor.Components;

/// <summary>
/// Alta rápida de una factura (spec alta rápida, Parte 1): cliente, fechas, vencimiento y descripción del documento y la PRIMERA línea en
/// un único formulario Static SSR. Hereda los campos de <see cref="LineaFacturaForm"/> (mismos <c>name</c> que la fila de alta del
/// documento, para usar <see cref="LineaFacturaCampos"/>); la descripción de la cabecera es <see cref="DescripcionDocumento"/> porque
/// <c>Descripcion</c> es la de la línea. El almacén no se envía (lo fija el servidor) y la serie de registro solo la del Administrador.
/// </summary>
public sealed class AltaFacturaForm : LineaFacturaForm
{
    [Required(ErrorMessage = "Elija el cliente.")]
    public Guid? SocioNegocioId { get; set; }

    public string? FechaRegistroTexto { get; set; }

    public string? FechaDocumentoTexto { get; set; }

    public string? FechaVencimientoTexto { get; set; }

    [MaxLength(200, ErrorMessage = "La descripción admite como máximo 200 caracteres.")]
    public string? DescripcionDocumento { get; set; }

    /// <summary>Solo el Administrador ("Más opciones"); null = la configurada.</summary>
    public Guid? SerieRegistroId { get; set; }

    public DateOnly? FechaRegistro => Fecha(FechaRegistroTexto);

    public DateOnly? FechaDocumento => Fecha(FechaDocumentoTexto);

    public DateOnly? FechaVencimiento => Fecha(FechaVencimientoTexto);

    public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var resultado in base.Validate(validationContext))
        {
            yield return resultado;
        }

        foreach (var (texto, campo, etiqueta) in new[]
                 {
                     (FechaRegistroTexto, nameof(FechaRegistroTexto), "La fecha de registro"),
                     (FechaDocumentoTexto, nameof(FechaDocumentoTexto), "La fecha de documento"),
                     (FechaVencimientoTexto, nameof(FechaVencimientoTexto), "La fecha de vencimiento"),
                 })
        {
            if (!string.IsNullOrWhiteSpace(texto) && !EntradaFecha.TryParse(texto, out _, out var error, etiqueta))
            {
                yield return new ValidationResult(error ?? $"{etiqueta} no es válida.", [campo]);
            }
        }
    }

    /// <summary>Cabecera + primera línea hacia la API (spec alta rápida): sin almacén; serie de registro solo si <paramref name="esAdministrador"/>.</summary>
    public BorradorCabeceraInput ToAlta(TipoLineaFactura tipo, Guid? productoId, Guid? unidadMedidaId, bool esAdministrador) =>
        new(
            SocioNegocioId ?? Guid.Empty, null, FechaRegistro, FechaDocumento, FechaVencimiento, null,
            string.IsNullOrWhiteSpace(DescripcionDocumento) ? null : DescripcionDocumento.Trim(),
            SerieBorradorId: null,
            SerieRegistroId: esAdministrador ? SerieRegistroId : null,
            PrimeraLinea: ToInput(tipo, productoId, unidadMedidaId));

    private static DateOnly? Fecha(string? texto) =>
        !string.IsNullOrWhiteSpace(texto) && EntradaFecha.TryParse(texto, out var valor, out _) ? valor : null;
}
```

- [ ] **Step 4: Campos de línea compartidos**

Crear `Components/LineaFacturaCampos.razor` con el contenido de `CamposLinea` de `FacturaVentaBorrador.razor` (líneas 670–760 actuales),
convertido en componente:

```razor
@using OpenSource1.Application.Features.Almacenes.Dtos
@using OpenSource1.Application.Features.CuentasContables.Dtos
@using OpenSource1.Application.Features.FacturasVenta.Borradores.Dtos
@using OpenSource1.Core.Enums
@* Campos de una línea de factura (tipo oculto, almacén de la línea, cuenta y grupo de IVA, cantidad, precio, descuento y descripción),
   compartidos por el alta rápida (/facturas-venta/nueva, Prefix="AltaInput") y la fila de alta/edición de la factura en preparación
   (Prefix="AddInput"/"UpdateInputLinea"). Va DENTRO del EditForm de la página: los ValidationMessage usan el mismo Model. *@
<input type="hidden" name="@($"{Prefix}.Tipo")" value="@((short)Tipo)" />
@if (OpcionesFallidas)
{
    <div class="mb-4"><MessageBox Type="danger" Message="No fue posible cargar las opciones de la línea (almacenes, cuentas o grupos de IVA). No se puede guardar hasta que carguen; recargue la página." /></div>
}
<div class="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
    @if (Tipo == TipoLineaFactura.Producto)
    {
        <div>
            <label class="block text-sm font-medium text-slate-700 mb-1" for="@($"{Prefix}_AlmacenId")">Almacén</label>
            <select id="@($"{Prefix}_AlmacenId")" name="@($"{Prefix}.AlmacenId")" class="@VentasCss.Input">
                <option value="" selected="@(Model.AlmacenId is null)">— El de la cabecera (@AlmacenCabeceraCodigo) —</option>
                @if (Model.AlmacenId is { } almacenId && Almacenes.All(a => a.Id != almacenId))
                {
                    <option value="@almacenId" selected="selected">@(Vigente?.AlmacenId == almacenId && Vigente.AlmacenCodigo is not null ? $"{Vigente.AlmacenCodigo} (no disponible)" : $"Almacén no disponible ({almacenId})")</option>
                }
                @foreach (var a in Almacenes)
                {
                    <option value="@a.Id" selected="@(a.Id == Model.AlmacenId)">@a.Codigo — @a.Nombre</option>
                }
            </select>
        </div>
    }
    @if (Tipo == TipoLineaFactura.CuentaContable)
    {
        <div class="sm:col-span-2">
            <label class="block text-sm font-medium text-slate-700 mb-1" for="@($"{Prefix}_CuentaContableId")">Cuenta contable <span class="text-red-500">*</span></label>
            <select id="@($"{Prefix}_CuentaContableId")" name="@($"{Prefix}.CuentaContableId")" class="@VentasCss.Input">
                <option value="" selected="@(Model.CuentaContableId is null)">— Seleccione —</option>
                @if (Model.CuentaContableId is { } cuentaId && Cuentas.All(c => c.Id != cuentaId))
                {
                    <option value="@cuentaId" selected="selected">@(Vigente?.CuentaContableId == cuentaId && Vigente.CuentaContableNumero is not null ? $"{Vigente.CuentaContableNumero} (no elegible)" : $"Cuenta no disponible ({cuentaId})")</option>
                }
                @foreach (var c in Cuentas)
                {
                    <option value="@c.Id" selected="@(c.Id == Model.CuentaContableId)">@c.Numero — @c.Nombre</option>
                }
            </select>
            <ValidationMessage For="() => Model.CuentaContableId" />
            <p class="mt-1 text-xs text-slate-400">Solo cuentas de posteo, no bloqueadas y de posteo directo.</p>
        </div>
        <div>
            <label class="block text-sm font-medium text-slate-700 mb-1" for="@($"{Prefix}_GrupoIvaProductoId")">Grupo de IVA de producto <span class="text-red-500">*</span></label>
            <select id="@($"{Prefix}_GrupoIvaProductoId")" name="@($"{Prefix}.GrupoIvaProductoId")" class="@VentasCss.Input">
                <option value="" selected="@(Model.GrupoIvaProductoId is null)">— Seleccione —</option>
                @if (Model.GrupoIvaProductoId is { } grupoId && GruposIva.All(g => g.Id != grupoId))
                {
                    <option value="@grupoId" selected="selected">Grupo no disponible (@grupoId)</option>
                }
                @foreach (var g in GruposIva)
                {
                    <option value="@g.Id" selected="@(g.Id == Model.GrupoIvaProductoId)">@g.Rotulo</option>
                }
            </select>
            <ValidationMessage For="() => Model.GrupoIvaProductoId" />
        </div>
    }
    @if (Tipo != TipoLineaFactura.Comentario)
    {
        <div>
            <label class="block text-sm font-medium text-slate-700 mb-1" for="@($"{Prefix}_CantidadTexto")">Cantidad <span class="text-red-500">*</span></label>
            <input id="@($"{Prefix}_CantidadTexto")" type="text" inputmode="decimal" name="@($"{Prefix}.CantidadTexto")" value="@Model.CantidadTexto" class="@VentasCss.Input" />
            <ValidationMessage For="() => Model.CantidadTexto" />
        </div>
        <div>
            <label class="block text-sm font-medium text-slate-700 mb-1" for="@($"{Prefix}_PrecioUnitarioTexto")">Precio unitario <span class="text-red-500">*</span></label>
            <input id="@($"{Prefix}_PrecioUnitarioTexto")" type="text" inputmode="decimal" name="@($"{Prefix}.PrecioUnitarioTexto")" value="@Model.PrecioUnitarioTexto"
                   aria-describedby="@($"{Prefix}_PrecioAyuda")" class="@VentasCss.Input" />
            <ValidationMessage For="() => Model.PrecioUnitarioTexto" />
            <p id="@($"{Prefix}_PrecioAyuda")" class="mt-1 text-xs text-slate-400" data-testid="ayuda-precio">Mayor que cero; @LineaFacturaForm.AyudaRegalo.</p>
        </div>
        <div>
            <label class="block text-sm font-medium text-slate-700 mb-1" for="@($"{Prefix}_PorcentajeDescuentoTexto")">Descuento %</label>
            <input id="@($"{Prefix}_PorcentajeDescuentoTexto")" type="text" inputmode="decimal" name="@($"{Prefix}.PorcentajeDescuentoTexto")" value="@Model.PorcentajeDescuentoTexto" placeholder="0" class="@VentasCss.Input" />
            <ValidationMessage For="() => Model.PorcentajeDescuentoTexto" />
        </div>
    }
    <div class="@(Tipo == TipoLineaFactura.Comentario ? "sm:col-span-2 lg:col-span-4" : "sm:col-span-2")">
        <label class="block text-sm font-medium text-slate-700 mb-1" for="@($"{Prefix}_Descripcion")">Descripción de la línea@(Tipo == TipoLineaFactura.Comentario ? " *" : "")</label>
        <input id="@($"{Prefix}_Descripcion")" type="text" name="@($"{Prefix}.Descripcion")" value="@Model.Descripcion" maxlength="200"
               placeholder="@(Tipo == TipoLineaFactura.Producto ? "Vacío = nombre del producto" : Tipo == TipoLineaFactura.CuentaContable ? "Vacío = nombre de la cuenta" : "")" class="@VentasCss.Input" />
        <ValidationMessage For="() => Model.Descripcion" />
    </div>
</div>
@if (Tipo == TipoLineaFactura.Producto)
{
    <p class="mt-2 text-xs text-slate-400">La línea usa la unidad base del producto.</p>
}

@code {
    [Parameter, EditorRequired] public LineaFacturaForm Model { get; set; } = default!;
    /// <summary>Prefijo de los <c>name</c>/<c>id</c> (la propiedad <c>[SupplyParameterFromForm]</c> de la página).</summary>
    [Parameter, EditorRequired] public string Prefix { get; set; } = string.Empty;
    [Parameter] public TipoLineaFactura Tipo { get; set; } = TipoLineaFactura.Producto;
    /// <summary>Línea guardada (edición), para rotular almacén o cuenta que ya no están entre las opciones.</summary>
    [Parameter] public LineaFacturaVentaBorradorResponse? Vigente { get; set; }
    [Parameter] public IReadOnlyList<AlmacenResponse> Almacenes { get; set; } = [];
    [Parameter] public IReadOnlyList<CuentaContableResponse> Cuentas { get; set; } = [];
    [Parameter] public IReadOnlyList<GrupoOpcion> GruposIva { get; set; } = [];
    /// <summary>Código del almacén de la cabecera (el predeterminado), para la opción vacía de la línea.</summary>
    [Parameter] public string? AlmacenCabeceraCodigo { get; set; }
    [Parameter] public bool OpcionesFallidas { get; set; }
}
```

(Única diferencia de marcado con el actual: la etiqueta "Descripción" de la línea pasa a "Descripción de la línea", para distinguirla de
la del documento en el alta rápida. Comprobar con `grep -rn '>Descripción' tests/OpenSource1.SmokeTests/Blazor` si algún test busca
la etiqueta antigua y ajustarlo.)

`CabeceraFacturaFields.razor`: quedan SOLO fecha de registro, fecha de documento, vencimiento y descripción (se borran los `<select>` de
socios, el de almacén, los dos `SelectorSerie` y `SerieNumeracionInfo`, y los parámetros `Socios`, `Almacenes`, `SocioBloqueado`,
`SeriesRegistro`, `SeriesBorrador`, `SeriesCargaFallida`, `Proximo` y el helper `OpcionSocioVigente`). Comentario de cabecera: "Campos
editables de la factura en preparación (spec alta rápida): fechas, vencimiento y descripción; cliente, almacén y series se muestran en
solo lectura (DatosDerivados / NumeracionRegistro)". El texto de ayuda queda como el de la modificación ("Si cambia la fecha de documento
y deja el vencimiento como estaba, la API lo recalcula con el término de pago."). `Obligatoria` pasa a `" *"` fijo.

- [ ] **Step 5: Tipos de apoyo, textos de estado y aviso de documento registrado**

`DocumentoVistas.cs`:

```csharp
/// <summary>Rutas de los documentos en preparación (spec alta rápida, Parte 2).</summary>
public static class RutasPreparacion
{
    public const string Factura = "/facturas-venta/en-preparacion";
    public const string Nota = "/notas-credito-venta/en-preparacion";
    public const string ListaFacturas = "/facturas-venta?estado=preparacion";
    public const string ListaNotas = "/notas-credito-venta?estado=preparacion";

    /// <summary>Destino de una ruta antigua (<c>…/borradores/{id}</c>): la nueva, con el <c>returnUrl</c> solo si es local (si no, la lista).</summary>
    public static string Redireccion(string baseRuta, Guid id, string? returnUrl, string listaPorDefecto)
    {
        var destino = $"{baseRuta}/{id}";
        return string.IsNullOrWhiteSpace(returnUrl)
            ? destino
            : RetornoLocal.ConRetorno(destino, RetornoLocal.Validar(returnUrl, listaPorDefecto));
    }

    /// <summary>Documento en preparación con la pestaña de tipo de línea de la fila de alta (sin query para Producto, el tipo por defecto).</summary>
    public static string ConTipo(string baseRuta, Guid id, TipoLineaFactura tipo) =>
        tipo == TipoLineaFactura.Producto ? $"{baseRuta}/{id}" : $"{baseRuta}/{id}?tipo={(int)tipo}";
}

/// <summary>Textos derivados comunes de la cabecera.</summary>
public static class DatosDocumento
{
    /// <summary>"Razón social RNC" o "—".</summary>
    public static string Fiscal(string? razonSocial, string? numeroDocumento) =>
        string.IsNullOrWhiteSpace($"{razonSocial} {numeroDocumento}") ? "—" : $"{razonSocial} {numeroDocumento}".Trim();
}
```

`VentasOpciones.Estado`: `Abierta => "En preparación"`, `Liberada => "En preparación (liberada)"`, `Posteada => "Registrada"`.

`DocumentoAvisoPosteado.razor`: mensaje `$"Esta preparación ya se registró como {Documento} {Numero}: es de solo lectura."`.

- [ ] **Step 6: `/facturas-venta/nueva`**

Reemplazar todo `Components/Pages/FacturaVentaNueva.razor` por:

```razor
@page "/facturas-venta/nueva"
@attribute [Authorize(Policy = ApplicationPolicies.CanConsult)]
@using System.Globalization
@using Microsoft.AspNetCore.Authorization
@using OpenSource1.Application.Features.Almacenes.Dtos
@using OpenSource1.Application.Features.ConfiguracionNumeracion.Dtos
@using OpenSource1.Application.Features.CuentasContables.Dtos
@using OpenSource1.Application.Features.Productos.Dtos
@using OpenSource1.Application.Features.Series.Dtos
@using OpenSource1.Application.Features.SociosNegocio.Dtos
@using OpenSource1.Blazor.Services
@using OpenSource1.Core.Common
@using OpenSource1.Core.Entities.Ventas
@using OpenSource1.Core.Enums
@inject IFacturaVentaApiClient FacturaApiClient
@inject ISocioNegocioApiClient SocioApiClient
@inject IAlmacenApiClient AlmacenApiClient
@inject ITerminoPagoApiClient TerminoPagoApiClient
@inject IProductoApiClient ProductoApiClient
@inject ICuentaContableApiClient CuentaApiClient
@inject IGrupoContableApiClient GrupoApiClient
@inject ISerieApiClient SerieApiClient
@inject IConfiguracionNumeracionApiClient ConfiguracionApiClient
@inject IAuthorizationService AuthorizationService
@inject ILogger<FacturaVentaNueva> Logger
@inject NavigationManager Nav

<PageTitle>Nueva factura – AxionERP</PageTitle>

@* Alta rápida de la factura (spec alta rápida, Parte 1): cliente por GET + "Usar" (?socioId= también desde la ficha del cliente),
   datos derivados en solo lectura (<dl>), numeración informativa y la primera línea. "Agregar línea" envía cabecera + línea en UN POST:
   la API crea el documento con la línea en una transacción y se abre /facturas-venta/en-preparacion/{id}. Un error no crea nada y la
   página vuelve a pintar lo capturado. Sin serie configurada (o inactiva): aviso bloqueante y sin formulario. *@
<PageToolbar Titulo="Nueva factura" Subtitulo="Elija el cliente y agregue la primera línea: la factura queda en preparación hasta que la registre."
             Migas="@Migas" Permisos="@permisos" NuevoHref="@null">
    <a href="@Volver" class="btn-press rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm font-semibold text-slate-600 hover:bg-slate-50">Volver</a>
</PageToolbar>

<ResumenErrores Mensaje="@message" Errores="@errores" />

@if (!canAdd)
{
    <p class="text-sm text-slate-600 dark:text-slate-300">No tiene permiso para realizar esta acción.</p>
}
else if (motivoBloqueo is not null)
{
    <AvisoSinSerie Motivo="@motivoBloqueo" PuedeConfigurar="@esAdministrador" />
}
else if (socio is null)
{
    <div class="mb-6 rounded-2xl border border-slate-200 bg-white px-6 py-5 shadow-sm">
        <form method="get" action="/facturas-venta/nueva" data-enhance class="mb-4 flex flex-wrap items-end gap-2" data-testid="buscar-socio">
            @RetornoOculto
            <div class="min-w-60 flex-1">
                <label class="mb-1 block text-xs font-semibold text-slate-500" for="socioQuery">Buscar cliente por nombre</label>
                <input id="socioQuery" type="text" name="socioQuery" value="@SocioQuery" placeholder="ej. Comercial" class="@VentasCss.Filtro" />
            </div>
            <button type="submit" class="btn-press rounded-lg border border-slate-300 bg-white px-4 py-2 text-sm font-semibold text-slate-600 shadow-sm hover:bg-slate-50">Buscar</button>
            <p class="w-full text-xs text-slate-400">Se muestran los primeros @VentasOpciones.MaximoSocios clientes por nombre@(string.IsNullOrWhiteSpace(SocioQuery) ? "" : $" que contienen «{SocioQuery}»").</p>
        </form>
        @if (socios.CargaFallida)
        {
            <MessageBox Type="danger" Message="No fue posible cargar los clientes; recargue la página." />
        }
        else if (socios.Items.Count == 0)
        {
            <p class="text-sm text-slate-400">No hay clientes con ese nombre.</p>
        }
        else
        {
            <ul class="divide-y divide-slate-100 rounded-xl border border-slate-200" data-testid="clientes-encontrados">
                @foreach (var s in socios.Items)
                {
                    <li @key="s.Id" class="flex items-center justify-between px-4 py-2 text-sm">
                        <span>@s.Codigo — @s.NombreComercial</span>
                        <a href="@UrlConSocio(s.Id)" aria-label="@($"Usar {s.NombreComercial}")" class="btn-press rounded-lg border border-brand-200 bg-brand-50 px-3 py-1 text-xs font-semibold text-brand-700 hover:bg-brand-100">Usar</a>
                    </li>
                }
            </ul>
        }
    </div>
}
else
{
    <div class="mb-6 rounded-2xl border border-slate-200 bg-white px-6 py-5 shadow-sm" data-testid="cliente-elegido">
        <div class="mb-3 flex items-center justify-between">
            <h2 class="text-sm font-semibold text-slate-700">Cliente</h2>
            <a href="@CambiarClienteHref" class="text-xs font-semibold text-brand-600 hover:text-brand-700">Cambiar cliente</a>
        </div>
        <DatosDerivados Datos="@DatosCliente" DetallesContables="@DetallesContables" />
        <NumeracionRegistro Codigo="@serieRegistro?.SerieCodigo" Proximo="@proximo" />
    </div>

    <div class="mb-6 rounded-2xl border border-slate-200 bg-white shadow-sm" data-testid="fila-alta">
        <div class="flex flex-wrap items-center justify-between gap-2 border-b border-slate-100 px-6 py-4">
            <h2 class="text-sm font-semibold text-slate-700">Primera línea</h2>
            <div class="flex items-center gap-1 text-xs" data-testid="tipos-linea">
                @foreach (var t in new[] { TipoLineaFactura.Producto, TipoLineaFactura.CuentaContable, TipoLineaFactura.Comentario })
                {
                    <a href="@BuildUrl(tipo: (int)t)" aria-current="@(AddTipo == t ? "true" : null)" class="@(AddTipo == t ? "rounded-lg bg-green-600 px-3 py-1 font-semibold text-white" : "rounded-lg border border-green-200 bg-white px-3 py-1 font-semibold text-green-700 hover:bg-green-50")">@VentasOpciones.TipoLinea(t)</a>
                }
            </div>
        </div>
        <div class="px-6 py-5">
            @if (AddTipo == TipoLineaFactura.Producto && addProducto is null)
            {
                <BuscadorProducto ActionUrl="/facturas-venta/nueva" QueryParamName="addProductoQuery" Termino="@AddProductoQuery"
                                  Resultados="@addProductoOpciones" CargaFallida="@addProductoOpcionesCargaFallida"
                                  ConstruirUrlUsar="@(id => BuildUrl(tipo: 1, addProductoId: id, addProductoQuery: AddProductoQuery))">
                    <CamposOcultos><input type="hidden" name="socioId" value="@socio.Id" /><input type="hidden" name="tipo" value="1" />@RetornoOculto</CamposOcultos>
                </BuscadorProducto>
            }
            else
            {
                @if (addProducto is not null && AddTipo == TipoLineaFactura.Producto)
                {
                    <div class="mb-4 flex items-center justify-between rounded-xl border border-slate-200 bg-slate-50 px-4 py-3">
                        <p class="text-sm text-slate-700">Producto: <span class="font-semibold">@addProducto.Codigo — @addProducto.Nombre</span>
                            <span class="text-xs text-slate-400">· Unidad base: @addProducto.UnidadMedidaCodigo · Precio de venta: @addProducto.PrecioVenta.ToString("0.00##", CultureInfo.InvariantCulture)</span></p>
                        <a href="@BuildUrl(tipo: 1)" class="btn-press rounded-lg border border-slate-300 bg-white px-3 py-1 text-xs font-semibold text-slate-600 hover:bg-slate-50">Cambiar producto</a>
                    </div>
                }
                <EditForm Model="AltaInput" FormName="alta-factura" OnValidSubmit="CrearAsync" Enhance="true">
                    <DataAnnotationsValidator />
                    <input type="hidden" name="AltaInput.SocioNegocioId" value="@socio.Id" />
                    <NumeracionRegistro MostrarInfo="false" PuedeCambiar="@esAdministrador" Id="AltaInput_SerieRegistroId" Name="AltaInput.SerieRegistroId"
                                        Codigo="@serieRegistro?.SerieCodigo" Series="@seriesRegistro.Items" Seleccion="@AltaInput!.SerieRegistroId"
                                        CargaFallida="@seriesRegistro.CargaFallida" />
                    <div class="mb-5 grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-4" data-testid="cabecera-alta">
                        <div>
                            <label class="mb-1 block text-sm font-medium text-slate-700" for="AltaInput_FechaRegistroTexto">Fecha de registro</label>
                            <input id="AltaInput_FechaRegistroTexto" type="date" name="AltaInput.FechaRegistroTexto" value="@AltaInput.FechaRegistroTexto" class="@VentasCss.Input" />
                            <ValidationMessage For="() => AltaInput.FechaRegistroTexto" />
                        </div>
                        <div>
                            <label class="mb-1 block text-sm font-medium text-slate-700" for="AltaInput_FechaDocumentoTexto">Fecha de documento</label>
                            <input id="AltaInput_FechaDocumentoTexto" type="date" name="AltaInput.FechaDocumentoTexto" value="@AltaInput.FechaDocumentoTexto" class="@VentasCss.Input" />
                            <ValidationMessage For="() => AltaInput.FechaDocumentoTexto" />
                        </div>
                        <div>
                            <label class="mb-1 block text-sm font-medium text-slate-700" for="AltaInput_FechaVencimientoTexto">Fecha de vencimiento</label>
                            <input id="AltaInput_FechaVencimientoTexto" type="date" name="AltaInput.FechaVencimientoTexto" value="@AltaInput.FechaVencimientoTexto"
                                   aria-describedby="AltaInput_VencimientoAyuda" class="@VentasCss.Input" />
                            <ValidationMessage For="() => AltaInput.FechaVencimientoTexto" />
                            <p id="AltaInput_VencimientoAyuda" class="mt-1 text-xs text-slate-400">Vacío = fecha de documento + días del término de pago.</p>
                        </div>
                        <div>
                            <label class="mb-1 block text-sm font-medium text-slate-700" for="AltaInput_DescripcionDocumento">Descripción de la factura</label>
                            <input id="AltaInput_DescripcionDocumento" type="text" name="AltaInput.DescripcionDocumento" value="@AltaInput.DescripcionDocumento" maxlength="200" class="@VentasCss.Input" />
                            <ValidationMessage For="() => AltaInput.DescripcionDocumento" />
                        </div>
                    </div>
                    <LineaFacturaCampos Model="AltaInput" Prefix="AltaInput" Tipo="@AddTipo" Almacenes="@almacenes.Items" Cuentas="@cuentas.Items"
                                        GruposIva="@gruposIva.Items" AlmacenCabeceraCodigo="@AlmacenPredeterminado?.Codigo" OpcionesFallidas="@OpcionesFallidas(AddTipo)" />
                    <div class="mt-5 flex items-center gap-3">
                        @if (!OpcionesFallidas(AddTipo))
                        {
                            <button type="submit" data-testid="agregar-linea" class="btn-press rounded-lg bg-green-600 px-4 py-2 text-sm font-semibold text-white shadow-sm hover:bg-green-700">Agregar línea</button>
                        }
                        <a href="@BuildUrl(tipo: (int)AddTipo)" class="btn-press rounded-lg border border-slate-300 bg-white px-4 py-2 text-sm font-semibold text-slate-600 shadow-sm hover:bg-slate-50">Limpiar campos</a>
                    </div>
                </EditForm>
            }
        </div>
    </div>
}

@* Formulario siempre en el árbol: sin permiso, sin cliente o con el buscador de producto a la vista, un POST obsoleto o forzado llega
   al handler y muestra el mensaje real (de la guarda o de la API, 403/400). *@
@if (!MuestraFormulario)
{
    <EditForm Model="AltaInput" FormName="alta-factura" OnValidSubmit="CrearAsync"></EditForm>
}

@code {
    [SupplyParameterFromQuery(Name = "returnUrl")] private string? ReturnUrl { get; set; }
    [SupplyParameterFromQuery(Name = "socioQuery")] private string? SocioQuery { get; set; }
    // Tolerante (ola final Fix-Features): un ?socioId= ilegible se ignora; con Guid? el binder lanzaría (página de error).
    [SupplyParameterFromQuery(Name = "socioId")] private string? SocioIdTexto { get; set; }
    private Guid? SocioId => InventarioVistasOpciones.ParseGuid(SocioIdTexto);
    // [SupplyParameterFromQuery] no admite enums: 1 Producto, 2 CuentaContable, 3 Comentario.
    [SupplyParameterFromQuery(Name = "tipo")] private int? TipoQuery { get; set; }
    [SupplyParameterFromQuery(Name = "addProductoQuery")] private string? AddProductoQuery { get; set; }
    [SupplyParameterFromQuery(Name = "addProductoId")] private string? AddProductoIdTexto { get; set; }
    private Guid? AddProductoId => InventarioVistasOpciones.ParseGuid(AddProductoIdTexto);
    [SupplyParameterFromForm(FormName = "alta-factura")] private AltaFacturaForm? AltaInput { get; set; }
    [CascadingParameter] private Task<AuthenticationState>? AuthenticationStateTask { get; set; }

    private OpcionesCargadas<SocioNegocioResponse> socios = OpcionesCargadas<SocioNegocioResponse>.SinCargar;
    private OpcionesCargadas<AlmacenResponse> almacenes = OpcionesCargadas<AlmacenResponse>.SinCargar;
    private OpcionesCargadas<CuentaContableResponse> cuentas = OpcionesCargadas<CuentaContableResponse>.SinCargar;
    private OpcionesCargadas<GrupoOpcion> gruposIva = OpcionesCargadas<GrupoOpcion>.SinCargar;
    private OpcionesCargadas<SerieResponse> seriesRegistro = OpcionesCargadas<SerieResponse>.SinCargar;
    private ConfiguracionNumeracionResponse? serieRegistro;
    private ProximoVista? proximo;
    private string? motivoBloqueo;
    private PermisosPagina? permisos;
    private SocioNegocioResponse? socio;
    private string? terminoPago;
    private ProductoResponse? addProducto;
    private IReadOnlyList<ProductoResponse> addProductoOpciones = [];
    private bool addProductoOpcionesCargaFallida;
    private bool altaNueva;
    private bool canAdd;
    private bool esAdministrador;
    private string? message;
    private IReadOnlyList<string> errores = [];

    private TipoLineaFactura AddTipo => TipoQuery is 2 ? TipoLineaFactura.CuentaContable : TipoQuery is 3 ? TipoLineaFactura.Comentario : TipoLineaFactura.Producto;

    private bool MuestraFormulario => canAdd && motivoBloqueo is null && socio is not null && !(AddTipo == TipoLineaFactura.Producto && addProducto is null);

    private string Volver => RetornoLocal.Volver(ReturnUrl, RutasPreparacion.ListaFacturas);

    private AlmacenResponse? AlmacenPredeterminado => almacenes.Items.FirstOrDefault(a => a.EsPredeterminado);

    private IReadOnlyList<Miga> Migas => [new("Facturación", "/modulos/facturacion"), new("Facturas", "/facturas-venta"), new("Nueva factura")];

    // "Cambiar cliente" quita el socioId y conserva la vuelta (returnUrl) si la hay.
    private string CambiarClienteHref => string.IsNullOrWhiteSpace(ReturnUrl)
        ? "/facturas-venta/nueva"
        : RetornoLocal.ConRetorno("/facturas-venta/nueva", Volver);

    private string UrlConSocio(Guid socioId) => RetornoLocal.ConParametro(CambiarClienteHref, "socioId", socioId.ToString());

    private IReadOnlyList<DatoDerivado> DatosCliente => socio is null ? [] :
    [
        new("Vender a", $"{socio.Codigo} — {socio.NombreComercial}", $"/clientes/{socio.Id}"),
        new("Facturar a", $"{socio.Codigo} — {socio.NombreComercial}"),
        new("RNC / razón social", DatosDocumento.Fiscal(socio.RazonSocial, socio.NumeroDocumentoFiscal)),
        new("Término de pago", terminoPago ?? "Sin término"),
        new("Moneda", SerieFacturaVentaIds.MonedaPorDefecto),
        new("Almacén", AlmacenPredeterminado is { } a ? $"{a.Codigo} — {a.Nombre} (predeterminado)" : "El predeterminado"),
    ];

    private IReadOnlyList<DatoDerivado> DetallesContables => socio is null ? [] :
    [
        new("Grupo de negocio", socio.GrupoNegocioCodigo ?? "Sin asignar"),
        new("Grupo de IVA de negocio", socio.GrupoIvaNegocioCodigo ?? "Sin asignar"),
        new("Grupo de cliente contable", socio.GrupoClienteContableCodigo ?? "Sin asignar"),
    ];

    protected override async Task OnInitializedAsync()
    {
        // En un GET el formulario es nuevo; en un POST llega enlazado y no se retoca (el precio vacío debe fallar).
        altaNueva = AltaInput is null;
        var hoy = EntradaFecha.Formatear(DateOnly.FromDateTime(DateTime.Today));
        AltaInput ??= new()
        {
            Tipo = (short)AddTipo,
            CantidadTexto = AddTipo == TipoLineaFactura.Comentario ? null : "1",
            FechaRegistroTexto = hoy,
            FechaDocumentoTexto = hoy,
        };

        permisos = await PermisosPagina.CargarAsync(AuthorizationService, AuthenticationStateTask);
        canAdd = permisos.CanAdd;
        esAdministrador = await permisos.PuedeAsync(ApplicationPolicies.CanAdministrar);
        if (!canAdd)
        {
            return;
        }

        await CargarNumeracionAsync();
        if (motivoBloqueo is not null)
        {
            return;
        }

        if (SocioId is not null)
        {
            await CargarSocioAsync();
        }

        if (socio is null)
        {
            socios = await VentasOpciones.SociosAsync(SocioApiClient, SocioQuery, Logger);
            return;
        }

        almacenes = await VentasOpciones.AlmacenesAsync(AlmacenApiClient, Logger);
        if (AddTipo == TipoLineaFactura.CuentaContable)
        {
            cuentas = await VentasOpciones.CuentasCapturaDirectaAsync(CuentaApiClient, Logger);
            gruposIva = await VentasOpciones.GruposIvaProductoAsync(GrupoApiClient, Logger);
        }

        if (AddTipo == TipoLineaFactura.Producto)
        {
            await CargarProductoAsync();
        }
    }

    /// <summary>
    /// Serie configurada y su próximo número; sin fila o con la serie inactiva, el aviso bloqueante. Si la configuración no carga, no se
    /// bloquea (la API valida al guardar, como F7) y la línea de numeración lo indica.
    /// </summary>
    private async Task CargarNumeracionAsync()
    {
        IReadOnlyList<ConfiguracionNumeracionResponse>? configuracion = null;
        try
        {
            configuracion = await ConfiguracionApiClient.ListAsync();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not load configuración de numeración.");
        }

        motivoBloqueo = NumeracionDocumento.MotivoBloqueo(
            configuracion, TipoDocumentoSerie.BorradorFacturaVenta, TipoDocumentoSerie.FacturaVenta, "facturas de venta");
        serieRegistro = NumeracionDocumento.Configurada(configuracion, TipoDocumentoSerie.FacturaVenta);
        proximo = serieRegistro is null
            ? new ProximoVista(null, null, "No fue posible comprobar la serie configurada.")
            : await SeriesOpciones.ProximoAsync(SerieApiClient, serieRegistro.SerieId, Logger);
        if (esAdministrador && motivoBloqueo is null)
        {
            seriesRegistro = await SeriesOpciones.ActivasAsync(SerieApiClient, TipoDocumentoSerie.FacturaVenta, Logger);
        }
    }

    private async Task CargarSocioAsync()
    {
        try
        {
            socio = await SocioApiClient.GetByIdAsync(SocioId!.Value);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not load socio {Id} for nueva factura.", SocioId);
            Show("No fue posible cargar el cliente indicado; elija uno de la lista.");
            return;
        }

        if (socio is null)
        {
            Show("No se encontró el cliente indicado; elija uno de la lista.");
            return;
        }

        if (socio.TerminoPagoId is { } terminoId)
        {
            terminoPago = await TerminoPagoOpciones.NombreAsync(TerminoPagoApiClient, terminoId, Logger);
        }
    }

    private async Task CargarProductoAsync()
    {
        if (AddProductoId is { } id)
        {
            try { addProducto = await ProductoApiClient.GetByIdAsync(id); }
            catch (Exception ex) { Logger.LogWarning(ex, "Could not load producto {Id}.", id); }
            if (addProducto is not null)
            {
                // El precio es obligatorio (Task 8.4): se propone el precio de venta (unidad base) si es mayor que cero.
                if (altaNueva && addProducto.PrecioVenta > 0 && string.IsNullOrWhiteSpace(AltaInput!.PrecioUnitarioTexto))
                {
                    AltaInput.PrecioUnitarioTexto = addProducto.PrecioVenta.ToString("0.00##", CultureInfo.InvariantCulture);
                }

                return;
            }
        }

        try
        {
            var filtro = new ProductoSearchFilter(null, string.IsNullOrWhiteSpace(AddProductoQuery) ? null : AddProductoQuery.Trim(), null, null, null, null, null, null);
            addProductoOpciones = (await ProductoApiClient.ListAsync(filtro, new PageRequest(1, 20, "Nombre", Descendente: false))).Items;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not load productos.");
            addProductoOpcionesCargaFallida = true;
        }
    }

    private bool OpcionesFallidas(TipoLineaFactura tipo) => tipo switch
    {
        TipoLineaFactura.Producto => almacenes.CargaFallida,
        TipoLineaFactura.CuentaContable => cuentas.CargaFallida || gruposIva.CargaFallida,
        _ => false,
    };

    private async Task CrearAsync()
    {
        // Con permiso, las guardas de la página; sin permiso el POST (forzado) va a la API igualmente y se muestra su 403.
        if (canAdd && motivoBloqueo is not null)
        {
            Show(motivoBloqueo);
            return;
        }

        if (canAdd && socio is null)
        {
            Show("Elija primero el cliente.");
            return;
        }

        if (canAdd && AddTipo == TipoLineaFactura.Producto && addProducto is null)
        {
            Show("Seleccione primero un producto.");
            return;
        }

        if (canAdd && OpcionesFallidas(AddTipo))
        {
            Show("No fue posible cargar las opciones de la línea; recargue la página.");
            return;
        }

        var f = AltaInput!;
        // El cliente de la query manda sobre el campo oculto: un POST manipulado no cambia de cliente.
        if (socio is not null)
        {
            f.SocioNegocioId = socio.Id;
        }

        var input = f.ToAlta(AddTipo, addProducto?.Id, addProducto?.UnidadMedidaBaseId, esAdministrador);
        // R17: la navegación va FUERA del try (NavigateTo puede lanzar NavigationException y el catch se la tragaría).
        string? destino = null;
        try
        {
            var result = await FacturaApiClient.CreateBorradorAsync(input);
            if (result.Succeeded)
            {
                destino = RetornoLocal.ConParametro(RutasPreparacion.ConTipo(RutasPreparacion.Factura, result.Valor!.Id, AddTipo), "ok", "linea_creada");
            }
            else
            {
                Show(result.Message, result.Errors);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not create factura en preparación.");
            Show("No fue posible crear la factura.");
        }

        if (destino is not null)
        {
            Nav.NavigateTo(destino);
        }
    }

    private string BuildUrl(int? tipo = null, Guid? addProductoId = null, string? addProductoQuery = null)
    {
        var parametros = new List<string>();
        if (socio is not null) parametros.Add($"socioId={socio.Id}");
        if (tipo is { } t) parametros.Add($"tipo={t}");
        if (addProductoId.HasValue) parametros.Add($"addProductoId={addProductoId.Value}");
        if (!string.IsNullOrWhiteSpace(addProductoQuery)) parametros.Add($"addProductoQuery={Uri.EscapeDataString(addProductoQuery)}");
        if (!string.IsNullOrWhiteSpace(ReturnUrl)) parametros.Add($"returnUrl={Uri.EscapeDataString(Volver)}");
        return parametros.Count == 0 ? "/facturas-venta/nueva" : $"/facturas-venta/nueva?{string.Join("&", parametros)}";
    }

    /// <summary>returnUrl oculto en los formularios GET (buscadores), para no perder la vuelta.</summary>
    private RenderFragment RetornoOculto => __builder =>
    {
        if (!string.IsNullOrWhiteSpace(ReturnUrl))
        {
            <input type="hidden" name="returnUrl" value="@Volver" />
        }
    };

    private void Show(string? text, IReadOnlyList<string>? errors = null)
    {
        message = text;
        errores = errors ?? [];
    }
}
```

(Comprobar la firma real de `ProductoSearchFilter` —ocho argumentos posicionales en `FacturaVentaBorrador.BuscarProductosAsync`— y de
`TerminoPagoOpciones.NombreAsync`; son las que ya usan las páginas actuales.)

- [ ] **Step 7: `/facturas-venta/en-preparacion/{id}` (cambios en `FacturaVentaBorrador.razor`)**

1. Cabecera del archivo: `@page "/facturas-venta/en-preparacion/{BorradorId:guid}"`; quitar `@inject ISocioNegocioApiClient SocioApiClient` y
   `@using OpenSource1.Application.Features.SociosNegocio.Dtos`. Comentario: "Factura en preparación (spec alta rápida; internamente,
   el borrador): datos derivados en solo lectura, fechas y descripción editables, líneas con alta y edición en la fila, totales al pie y
   'Registrar factura' (guarda la cabecera y abre la confirmación) / 'Guardar para después' / 'Descartar factura'. Sin Liberar ni
   Reabrir (un documento heredado Liberada ofrece 'Volver a editar'). Registrada: todo en solo lectura con enlace a la factura."
2. `<PageTitle>@Titulo – AxionERP</PageTitle>` y la barra:

```razor
<PageToolbar Titulo="@Titulo"
             Subtitulo="@(borrador is null ? null : $"{borrador.NombreFacturacion} · {VentasOpciones.Estado(borrador.Estado)}")"
             Migas="@Migas" Permisos="@permisos" NuevoHref="@null" SeleccionId="@borrador?.Id.ToString()" Ver="@VerDocumento">
    @if (PuedeEliminar)
    {
        <a href="@BuildUrl(eliminar: true)" data-testid="descartar" class="btn-press rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm font-semibold text-red-700 hover:bg-red-100">Descartar factura</a>
    }
    @if (EsPosteada)
    {
        <a href="@FacturaHref" class="btn-press rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm font-semibold text-slate-600 hover:bg-slate-50">Ver factura @borrador!.FacturaVentaNumero</a>
    }
    <a href="@Volver" class="btn-press rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm font-semibold text-slate-600 hover:bg-slate-50">Volver</a>
</PageToolbar>
```

3. Mensajes: el bloque `@if (!string.IsNullOrWhiteSpace(message)) { … }` pasa a

```razor
@if (!string.IsNullOrWhiteSpace(message) && messageType == "success")
{
    <div class="mb-6"><ActionFeedback Type="success" Message="@message" /></div>
}
else
{
    <ResumenErrores Mensaje="@message" Errores="@errores" />
}

@if (posteoError is not null && !MuestraDialogoPosteo)
{
    <div data-testid="errores-posteo"><ResumenErrores Mensaje="@posteoError" Errores="@posteoErrores" /></div>
}
```

   (el `MessageBox` de `loadError` se conserva; el texto "No se encontró el borrador de factura solicitado (puede haber sido eliminado)."
   pasa a "No se encontró la factura en preparación (puede haberse registrado o descartado)." y el enlace a "Ver las facturas registradas").
4. El aviso de Liberada se sustituye por:

```razor
    @if (borrador.Estado == EstadoFacturaBorrador.Liberada)
    {
        <div class="mb-6" data-testid="aviso-liberada">
            <MessageBox Type="warning" Message="Esta factura quedó lista para registrar en la versión anterior: puede registrarla tal cual o volver a editarla." />
            @if (canModify)
            {
                <div class="mt-3 flex flex-wrap items-center gap-3">
                    <a href="@BuildUrl(registrar: true)" class="btn-press rounded-lg bg-brand-600 px-4 py-2 text-sm font-semibold text-white shadow-sm hover:bg-brand-700">Registrar factura</a>
                    <EditForm Model="ReabrirInput" FormName="reabrir" OnValidSubmit="ReabrirAsync">
                        <button type="submit" data-testid="volver-a-editar" class="btn-press rounded-lg border border-slate-300 bg-white px-4 py-2 text-sm font-semibold text-slate-700 hover:bg-slate-50">Volver a editar</button>
                    </EditForm>
                </div>
            }
        </div>
    }
```

5. La cabecera (desde el formulario GET `buscar-socio` hasta el `DocumentoCabecera` de solo lectura) pasa a:

```razor
    <DocumentoCabeceraEditable Model="UpdateInput" FormName="guardar-cabecera" OnValidSubmit="GuardarCabeceraAsync"
                               Mostrar="MuestraFormCabecera" CargaFallida="false" Botones="@BotonesCabecera">
        <input type="hidden" name="UpdateInput.Xmin" value="@UpdateInput!.Xmin" />
        <DatosDerivados Datos="@DatosCliente" DetallesContables="@DetallesContables" />
        <NumeracionRegistro Codigo="@borrador.SerieRegistroCodigo" Proximo="@proximo" PuedeCambiar="@esAdministrador"
                            Id="UpdateInput_SerieRegistroId" Name="UpdateInput.SerieRegistroId" Series="@seriesRegistro.Items"
                            Seleccion="@UpdateInput.SerieRegistroId" VigenteId="@borrador.SerieRegistroId" CargaFallida="@seriesRegistro.CargaFallida" />
        <CabeceraFacturaFields Model="UpdateInput" Prefix="UpdateInput" />
    </DocumentoCabeceraEditable>
    @if (!MuestraFormCabecera)
    {
        <DocumentoCabecera Campos="@CamposCabecera" />
    }
```

   con, en `@code`:

```csharp
    /// <summary>Botones de la tarjeta: Registrar (guarda y abre la confirmación), Guardar para después y Guardar cambios.</summary>
    private RenderFragment BotonesCabecera => __builder =>
    {
        @if (MotivoNoRegistrable is { } motivo)
        {
            <button type="button" disabled data-testid="registrar" aria-describedby="motivo-registrar" class="rounded-lg bg-slate-300 px-4 py-2 text-sm font-semibold text-white">Registrar factura</button>
            <p id="motivo-registrar" class="w-full text-xs text-red-700" data-testid="motivo-registrar">@motivo</p>
        }
        else
        {
            <button type="submit" name="UpdateInput.Accion" value="@AccionCabecera.Registrar" data-testid="registrar" class="btn-press rounded-lg bg-brand-600 px-4 py-2 text-sm font-semibold text-white shadow-sm hover:bg-brand-700">Registrar factura</button>
        }
        <button type="submit" name="UpdateInput.Accion" value="@AccionCabecera.Despues" data-testid="guardar-despues" class="btn-press rounded-lg border border-slate-300 bg-white px-4 py-2 text-sm font-semibold text-slate-700 shadow-sm hover:bg-slate-50">Guardar para después</button>
        <button type="submit" data-testid="guardar-cabecera" class="btn-press rounded-lg border border-slate-300 bg-white px-4 py-2 text-sm font-semibold text-slate-700 shadow-sm hover:bg-slate-50">Guardar cambios</button>
        <a href="@UrlPropia" data-testid="limpiar-cabecera" class="btn-press rounded-lg border border-slate-300 bg-white px-4 py-2 text-sm font-semibold text-slate-600 shadow-sm hover:bg-slate-50">Limpiar campos</a>
        <a href="@Volver" data-testid="cancelar-cabecera" class="btn-press rounded-lg border border-slate-300 bg-white px-4 py-2 text-sm font-semibold text-slate-600 shadow-sm hover:bg-slate-50">Cancelar</a>
    };

    /// <summary>Motivo visible para no registrar (la serie de registro ya no numera: inactiva, agotada o de otro tipo).</summary>
    private string? MotivoNoRegistrable => proximo?.Error is { } error ? $"No se puede registrar: {error}" : null;
```

6. Filas de la tabla: el `@foreach (var l in lineas.OrderBy(x => x.NumeroLinea))` pasa a
   `@foreach (var (l, i) in lineas.OrderBy(x => x.NumeroLinea).Select((l, i) => (l, i)))`; el `<tr @key="l.Id" …>` gana `id="linea-@l.NumeroLinea"`;
   los enlaces de la fila ganan `aria-label="@($"Modificar línea {i + 1}")"` y `aria-label="@($"Eliminar línea {i + 1}")"`. En
   `ContenidoAltaLinea` y `ContenidoEdicionLinea`, `@CamposLinea(AddInput!, "AddInput", AddTipo, null)` y
   `@CamposLinea(UpdateInputLinea, "UpdateInputLinea", lineaAEditar.Tipo, lineaAEditar)` pasan a

```razor
<LineaFacturaCampos Model="AddInput!" Prefix="AddInput" Tipo="@AddTipo" Almacenes="@almacenes.Items" Cuentas="@cuentas.Items"
                    GruposIva="@gruposIva.Items" AlmacenCabeceraCodigo="@borrador!.AlmacenCodigo" OpcionesFallidas="@OpcionesFallidas(AddTipo)" />
```

```razor
<LineaFacturaCampos Model="UpdateInputLinea" Prefix="UpdateInputLinea" Tipo="@lineaAEditar.Tipo" Vigente="@lineaAEditar" Almacenes="@almacenes.Items"
                    Cuentas="@cuentas.Items" GruposIva="@gruposIva.Items" AlmacenCabeceraCodigo="@borrador!.AlmacenCodigo" OpcionesFallidas="@OpcionesFallidas(lineaAEditar.Tipo)" />
```

   y se borra el método `CamposLinea` y la constante `InputCss`.
7. Diálogos: el de posteo pasa a

```razor
    @if (MuestraDialogoPosteo)
    {
        <ConfirmarRegistro Titulo="Registrar factura" Aviso="Se descontará el inventario y se generará el asiento; no se puede deshacer."
                           Resumen="@ResumenRegistro" Model="PostearInput" FormName="confirm-postear" OnValidSubmit="PostearAsync"
                           SeguirEditandoHref="@UrlPropia">
            <Errores>
                @if (posteoError is not null)
                {
                    <div data-testid="errores-posteo"><ResumenErrores Mensaje="@posteoError" Errores="@posteoErrores" /></div>
                }
            </Errores>
        </ConfirmarRegistro>
    }
```

   y el de eliminar: `Title="Descartar factura"`, `Message="Se descartará la factura en preparación con todas sus líneas. No se puede deshacer."`,
   botón "Sí, descartar". El diálogo de borrar línea: "Esta acción quitará la línea seleccionada de la factura."
8. Formularios vacíos del final: `liberar` se deja SIEMPRE vacío en el árbol (compatibilidad con una pestaña abierta de la versión
   anterior; no hay botón): `<EditForm Model="LiberarInput" FormName="liberar" OnValidSubmit="LiberarAsync"></EditForm>` sin `@if`;
   `reabrir` vacío cuando `!MuestraReabrir`. Se borra la propiedad `MuestraLiberar`.
9. `@code`:

```csharp
    [SupplyParameterFromQuery(Name = "registrar")] private bool RegistrarConfirm { get; set; }
    // Enlaces de la versión anterior (?postear=true) siguen abriendo la confirmación.
    [SupplyParameterFromQuery(Name = "postear")] private bool PostearLegado { get; set; }

    private string UrlBase => $"{RutasPreparacion.Factura}/{BorradorId}";
    private string Volver => RetornoLocal.Volver(ReturnUrl, RutasPreparacion.ListaFacturas);
    private string Titulo => borrador is null || !EsPosteada ? "Factura en preparación" : $"Preparación de la factura {borrador.FacturaVentaNumero}";
    private IReadOnlyList<Miga> Migas =>
        [new("Facturación", "/modulos/facturacion"), new("Facturas", "/facturas-venta"), new("En preparación", RutasPreparacion.ListaFacturas), new(borrador?.NombreFacturacion ?? "Factura")];
    private bool MuestraDialogoPosteo => borrador is not null && (RegistrarConfirm || PostearLegado) && canModify && !EsPosteada;
    private bool esAdministrador;

    private IReadOnlyList<DatoDerivado> DatosCliente => borrador is null ? [] :
    [
        new("Vender a", $"{borrador.SocioNegocioCodigo} — {borrador.SocioNegocioNombre}", $"/clientes/{borrador.SocioNegocioId}"),
        new("Facturar a", $"{borrador.SocioNegocioFacturarACodigo} — {borrador.NombreFacturacion}"),
        new("RNC / razón social", DatosDocumento.Fiscal(borrador.RazonSocialFacturacion, borrador.NumeroDocumentoFiscal)),
        new("Término de pago", borrador.TerminoPagoCodigo ?? "Sin término"),
        new("Moneda", borrador.Moneda),
        new("Almacén", borrador.AlmacenCodigo ?? "—"),
    ];

    private IReadOnlyList<DatoDerivado> DetallesContables => borrador is null ? [] :
    [
        new("Grupo de negocio", borrador.GrupoNegocioCodigo ?? "—"),
        new("Grupo de IVA de negocio", borrador.GrupoIvaNegocioCodigo ?? "—"),
        new("Grupo de cliente contable", borrador.GrupoClienteContableCodigo ?? "—"),
    ];

    private IReadOnlyList<DatoDerivado> ResumenRegistro => borrador is null ? [] :
    [
        new("Total", totales is null ? "—" : VentasOpciones.Importe(totales.ImporteTotal)),
        new("Líneas", lineas.Count.ToString(CultureInfo.InvariantCulture)),
        new("Serie", borrador.SerieRegistroCodigo ?? "—"),
        new("Próximo número", proximo?.Numero ?? "—"),
    ];
```

   - Borrar `SocioQuery`, `socios`, `OpcionesCabeceraFallidas`, `MuestraLiberar` y la propiedad `PostearConfirm`.
   - En `OnInitializedAsync`, tras cargar los permisos: `esAdministrador = await permisos.PuedeAsync(ApplicationPolicies.CanAdministrar);`.
     El bloque `if (MuestraFormCabecera) { … }` pasa a:

```csharp
        if (MuestraFormCabecera)
        {
            if (esAdministrador)
            {
                seriesRegistro = await SeriesOpciones.ActivasAsync(SerieApiClient, TipoDocumentoSerie.FacturaVenta, Logger);
            }

            // Solo se rellena con lo guardado cuando NO llegó el formulario de la cabecera (Xmin = 0).
            if (UpdateInput.Xmin == 0)
            {
                UpdateInput.FechaRegistroTexto = EntradaFecha.Formatear(borrador.FechaRegistro);
                UpdateInput.FechaDocumentoTexto = EntradaFecha.Formatear(borrador.FechaDocumento);
                UpdateInput.FechaVencimientoTexto = EntradaFecha.Formatear(borrador.FechaVencimiento);
                UpdateInput.Descripcion = borrador.Descripcion;
                UpdateInput.SerieRegistroId = esAdministrador ? borrador.SerieRegistroId : null;
                UpdateInput.Xmin = borrador.Xmin;
            }
        }
```

   - Mensajes `Ok`: `"creado" => "Factura en preparación. Agregue sus líneas."`, `"copiado" => "Factura en preparación creada desde la factura registrada. Revise las líneas y regístrela."`, `"modificado" => "Cambios guardados."`, `"reabierto" => "La factura volvió a edición."`; se borra `"liberado"`; el resto igual.
   - `GuardarCabeceraAsync`:

```csharp
    private async Task GuardarCabeceraAsync()
    {
        if (borrador is null)
        {
            Show(loadError is null ? "warning" : "danger", BorradorNoCargado);
            return;
        }

        // El cliente no se cambia en la preparación (spec alta rápida): viajan los del documento. El almacén no viaja (lo fija el servidor)
        // y la serie de registro solo si la eligió el Administrador en "Más opciones". Sin permiso o con el documento ya registrado, el
        // POST va a la API igualmente y la página muestra su mensaje real (403/409; Review Focus 5 de no-series).
        var f = UpdateInput!;
        // El vencimiento solo viaja si el usuario lo cambió: "null = conservar" deja que la API lo recalcule con la fecha de documento.
        var vencimiento = f.FechaVencimiento == borrador.FechaVencimiento ? null : f.FechaVencimiento;
        var input = new BorradorCabeceraInput(
            borrador.SocioNegocioId, borrador.SocioNegocioFacturarAId, f.FechaRegistro, f.FechaDocumento, vencimiento, null,
            f.Descripcion?.Trim() ?? string.Empty, SerieRegistroId: esAdministrador ? f.SerieRegistroId : null);

        // R17: el destino se calcula dentro del try y la navegación va DESPUÉS del try/catch.
        string? destino = null;
        try
        {
            var result = await FacturaApiClient.UpdateBorradorAsync(BorradorId, input, f.Xmin);
            if (result.Succeeded)
            {
                destino = f.Accion switch
                {
                    AccionCabecera.Registrar => BuildUrl(registrar: true),
                    AccionCabecera.Despues => RetornoLocal.ConParametro(RutasPreparacion.ListaFacturas, "ok", "guardada"),
                    _ => RetornoLocal.ConParametro(BuildUrl(), "ok", "modificado"),
                };
            }
            else
            {
                Show("warning", result.Message, result.Errors);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not update factura en preparación {Id}.", BorradorId);
            Show("danger", "No fue posible guardar los cambios.");
        }

        if (destino is not null)
        {
            Nav.NavigateTo(destino);
        }
    }
```

   - `EliminarBorradorAsync`: `destino = RetornoLocal.ConParametro(Volver, "ok", "descartada");` y el mensaje de fallo "No fue posible descartar la factura."
   - `PostearAsync`: `destino = $"/facturas-venta/{Uri.EscapeDataString(r.Numero)}?ok=registrada{aviso}";` y el mensaje de excepción "No fue posible registrar la factura."
   - `BuildUrl`: el parámetro `bool? postear` pasa a `bool? registrar` y añade `"registrar=true"`.
   - `LiberarAsync` se conserva (formulario vacío de compatibilidad).
   - `CamposCabecera` (solo lectura de Liberada/registrada/sin permiso): la etiqueta `"Factura"` del documento registrado pasa a `"Factura registrada"`.

- [ ] **Step 8: Rutas antiguas, `BorradorForm` y factura registrada**

`Components/Pages/FacturaVentaBorradorRedireccion.razor`:

```razor
@page "/facturas-venta/borradores/{BorradorId:guid}"
@attribute [Authorize(Policy = ApplicationPolicies.CanConsult)]
@using Microsoft.AspNetCore.Authorization
@inject NavigationManager Nav

@* Spec alta rápida: la ruta antigua de la factura en preparación (enlaces guardados y "Borrador de origen" de versiones anteriores)
   redirige a /facturas-venta/en-preparacion/{id}, conservando un returnUrl local (uno externo cae a la lista). *@

@code {
    [Parameter] public Guid BorradorId { get; set; }
    [SupplyParameterFromQuery(Name = "returnUrl")] private string? ReturnUrl { get; set; }

    protected override void OnInitialized() =>
        Nav.NavigateTo(RutasPreparacion.Redireccion(RutasPreparacion.Factura, BorradorId, ReturnUrl, RutasPreparacion.ListaFacturas), replace: true);
}
```

`FacturaVentaBorradorCabecera.razor` (`/facturas-venta/borradores/{id}/editar`): `OnInitialized` pasa a la misma llamada
`Nav.NavigateTo(RutasPreparacion.Redireccion(RutasPreparacion.Factura, BorradorId, ReturnUrl, RutasPreparacion.ListaFacturas), replace: true);`.

`FacturasVentaBorradores.razor`, clase `BorradorForm`: borrar `SocioNegocioId` (y su `[Required]`), `SocioNegocioFacturarAId`, `AlmacenId`
y `SerieBorradorId`; añadir

```csharp
        /// <summary>Botón que envió la cabecera: <see cref="AccionCabecera.Registrar"/>, <see cref="AccionCabecera.Despues"/> o vacío (guardar y seguir).</summary>
        public string? Accion { get; set; }
```

y actualizar su `<summary>`: "Cabecera editable de la factura en preparación (`UpdateInput`): fechas, vencimiento, descripción, serie de
registro (solo el Administrador) y la acción". (El resto de la página de lista no cambia en esta task; R8 la convierte en redirección.)

`FacturasVenta.razor` (detalle):
- `Ok switch` / mensaje: `"registrada"` (y `"posteada"` por compatibilidad) → `ActionFeedback` con `Title="Factura registrada"` y
  `Message="@($"Factura {detalle.Cabecera.Numero} registrada.")"`, seguido de
  `<a href="/facturas-venta/nueva" data-testid="nueva-factura" class="…">Nueva factura</a>`. La condición del aviso de numeración:
  `Ok is "posteada" or "registrada"`.
- `CamposFactura`: `new("Preparación", c.FacturaVentaBorradorId is null ? "—" : "Ver preparación", c.FacturaVentaBorradorId is { } b ? $"{RutasPreparacion.Factura}/{b}" : null)`.
- Destino de la copia: `$"{RutasPreparacion.Factura}/{valor.BorradorId}?ok=copiado"`; enlace `borrador-copiado`:
  `href="@($"{RutasPreparacion.Factura}/{copia.BorradorId}")"` con el texto "Abrir la factura en preparación"; la acción "Copiar a
  borrador" pasa a "Copiar a factura en preparación" y su diálogo a `Title="Copiar a factura en preparación"`.

- [ ] **Step 9: Verde**

Run: `dotnet test tests/OpenSource1.SmokeTests --filter "FullyQualifiedName~AltaRapidaFacturaTests|FullyQualifiedName~DocumentoFacturaBorradorTests|FullyQualifiedName~AccionesFacturasTests|FullyQualifiedName~ConversionDocumentosUsuariosTests|FullyQualifiedName~DocumentosPosteadosTests|FullyQualifiedName~ComponentesDocumentoTests|FullyQualifiedName~LineaFacturaFormularioTests|FullyQualifiedName~RegistroModulosTests|FullyQualifiedName~NavMenuTests"`
Expected: PASS. (`RegistroModulosTests`/`NavMenuTests` siguen verdes aquí porque el catálogo no cambia hasta R8; si
`CadaListadoTieneModulo_YCadaRutaDelRegistroExiste` falla por `/facturas-venta/nueva` o por la ruta nueva con `{id}`, no aplica: ambas
están excluidas por el propio test.)

- [ ] **Step 10: Sin interactividad nueva**

Run: `git diff --cached -G'@rendermode|@onclick|@bind' --stat -- 'src/**/*.razor'; git diff -G'@rendermode|@onclick|@bind' --stat -- 'src/**/*.razor'`
Expected: sin archivos.

- [ ] **Step 11: Commit**

```bash
git add src/OpenSource1.Blazor/Components/LineaFacturaForm.cs src/OpenSource1.Blazor/Components/AltaFacturaForm.cs \
  src/OpenSource1.Blazor/Components/LineaFacturaCampos.razor src/OpenSource1.Blazor/Components/CabeceraFacturaFields.razor \
  src/OpenSource1.Blazor/Components/Documento/DocumentoVistas.cs src/OpenSource1.Blazor/Components/Documento/DocumentoAvisoPosteado.razor \
  src/OpenSource1.Blazor/Components/VentasOpciones.cs src/OpenSource1.Blazor/Components/Pages/FacturaVentaNueva.razor \
  src/OpenSource1.Blazor/Components/Pages/FacturaVentaBorrador.razor src/OpenSource1.Blazor/Components/Pages/FacturaVentaBorradorRedireccion.razor \
  src/OpenSource1.Blazor/Components/Pages/FacturaVentaBorradorCabecera.razor src/OpenSource1.Blazor/Components/Pages/FacturasVentaBorradores.razor \
  src/OpenSource1.Blazor/Components/Pages/FacturasVenta.razor tests/OpenSource1.SmokeTests/Blazor/AltaRapidaFacturaTests.cs \
  tests/OpenSource1.SmokeTests/Blazor/DocumentoFacturaBorradorTests.cs tests/OpenSource1.SmokeTests/Blazor/AccionesFacturasTests.cs \
  tests/OpenSource1.SmokeTests/Blazor/ConversionDocumentosUsuariosTests.cs tests/OpenSource1.SmokeTests/Blazor/DocumentosPosteadosTests.cs \
  tests/OpenSource1.SmokeTests/Blazor/ComponentesDocumentoTests.cs tests/OpenSource1.SmokeTests/Blazor/LineaFacturaFormularioTests.cs
git commit -m "feat: alta rapida de factura y factura en preparacion con registrar, guardar para despues y descartar"
```

---
### Task R7: Nota de crédito — "Crear nota de crédito" desde la factura registrada y `/notas-credito-venta/en-preparacion/{id}`

**Grupo de paralelismo:** Fase 2, serie (tras R6). Sin Docker.

**Files:**
- Modify: `src/OpenSource1.Blazor/Components/Pages/FacturasVenta.razor` (detalle: acción "Crear nota de crédito" / "Continuar nota de crédito", diálogo `?crearNota=true` sin campos, enlaces a las notas en preparación)
- Modify: `src/OpenSource1.Blazor/Components/Pages/NotaCreditoVentaBorrador.razor`
- Create: `src/OpenSource1.Blazor/Components/Pages/NotaCreditoVentaBorradorRedireccion.razor`
- Modify: `src/OpenSource1.Blazor/Components/Pages/NotaCreditoBorradorCabecera.razor`
- Modify: `src/OpenSource1.Blazor/Components/Pages/NotaCreditoNueva.razor`
- Modify: `src/OpenSource1.Blazor/Components/Pages/NotasCreditoVentaBorradores.razor` (SOLO la clase anidada `CabeceraNotaForm`)
- Modify: `src/OpenSource1.Blazor/Components/Pages/NotasCreditoVenta.razor` (detalle: "Ver preparación" y mensaje de registro)
- Modify: `src/OpenSource1.Blazor/Components/VentasOpciones.cs` (`EstadoNota`)
- Create: `tests/OpenSource1.SmokeTests/Blazor/AltaRapidaNotaTests.cs`
- Test: `tests/OpenSource1.SmokeTests/Blazor/DocumentoNotaBorradorTests.cs`, `DocumentosPosteadosTests.cs`, `AccionesFacturasTests.cs`, `ConversionDocumentosUsuariosTests.cs`, `ComponentesDocumentoTests.cs`

**Interfaces:**
- Consumes (R3–R6): `NotaCreditoBorradorInput(FacturaVentaNumero, null, null, null, CopiarLineas: true, DevolverInventario: true)` (series null), `RutasPreparacion.Nota`/`ListaNotas`/`Redireccion`, `DatosDerivados`, `NumeracionRegistro`, `ConfirmarRegistro`, `ResumenErrores`, `AccionCabecera`, `DocumentoCabeceraEditable.Botones`, `DatosDocumento.Fiscal`.
- Produces (R8, R9):
  - En la factura registrada (`/facturas-venta/{numero}`), con `CanAdd` y pendiente: `<EditForm FormName="crear-nota">` con el botón `data-testid="crear-nota"` "Crear nota de crédito"; si ya hay una nota en preparación de esa factura, el enlace `data-testid="continuar-nota"` "Continuar nota de crédito" a `/notas-credito-venta/en-preparacion/{id}` en su lugar. Éxito → `/notas-credito-venta/en-preparacion/{id}?ok=creado`.
  - `/notas-credito-venta/en-preparacion/{id}`: botones `registrar` ("Registrar nota de crédito"), `guardar-despues`, `guardar-cabecera` ("Guardar cambios"); barra `descartar` ("Descartar"); `?registrar=true` (alias `?postear=true`) abre la confirmación; filas con "Quitar línea" (`aria-label="Quitar línea n"`) y `aria-label="Modificar línea n"`, `id="linea-{NumeroLinea}"`. Registrar → `/notas-credito-venta/{numero}?ok=registrada&aplicado=…`; descartar → `/notas-credito-venta?estado=preparacion&ok=descartada`; guardar para después → `/notas-credito-venta?estado=preparacion&ok=guardada`.
  - `/notas-credito-venta/nueva`: cliente → factura con pendiente → "Crear nota de crédito" (sin fechas, descripción ni series).
  - `NotasCreditoVentaBorradores.CabeceraNotaForm.Accion : string?`.

- [ ] **Step 1: Tests (fallan)**

Crear `tests/OpenSource1.SmokeTests/Blazor/AltaRapidaNotaTests.cs`:

```csharp
extern alias BlazorApp;
using System.Net;
using BlazorApp::OpenSource1.Blazor.Services;
using Moq;
using OpenSource1.Application.Features.FacturasVenta.Calculo;
using OpenSource1.Application.Features.FacturasVenta.Posteadas;
using OpenSource1.Application.Features.FacturasVenta.Posteadas.Dtos;
using OpenSource1.Application.Features.NotasCreditoVenta.Borradores.Dtos;
using OpenSource1.Application.Features.NotasCreditoVenta.Posteadas;
using OpenSource1.Application.Features.NotasCreditoVenta.Posteadas.Dtos;
using OpenSource1.Application.Features.NotasCreditoVenta.Posteo;
using OpenSource1.Application.Features.Series.Dtos;
using OpenSource1.Core.Common;
using OpenSource1.Core.Enums;
using OpenSource1.SmokeTests.TestInfrastructure;

namespace OpenSource1.SmokeTests.Blazor;

/// <summary>
/// Spec alta rápida (Parte 2): la nota de crédito nace desde la factura registrada con "Crear nota de crédito" (todas las líneas
/// pendientes y la devolución activada, series configuradas) y se edita en /notas-credito-venta/en-preparacion/{id}: Registrar nota de
/// crédito con confirmación, Guardar para después, Descartar, Quitar línea; /notas-credito-venta/nueva sin series; rutas antiguas redirigen.
/// </summary>
public sealed class AltaRapidaNotaTests
{
    private const string Factura = "FV-000123";
    private static readonly Guid IdNota = Guid.Parse("7d0c9a51-0000-0000-0000-0000000007a1");
    private static readonly Guid IdSerieNc = Guid.Parse("7d0c9a51-0000-0000-0000-0000000007a2");
    private static readonly Guid IdSocio = Guid.Parse("7d0c9a51-0000-0000-0000-0000000007a3");
    private static string Url(Guid id) => $"/notas-credito-venta/en-preparacion/{id}";

    [Fact]
    public async Task FacturaRegistrada_CrearNotaDeCredito_UnClic_CreaConTodoLoPendienteYDevolucion()
    {
        using var app = Configurar(new BlazorSsrFactory(), notasEnPreparacion: []);
        var notas = app.Simular<INotaCreditoVentaApiClient>();
        notas.Setup(c => c.CreateBorradorAsync(
                It.Is<NotaCreditoBorradorInput>(i => i.FacturaVentaNumero == Factura && i.CopiarLineas && i.DevolverInventario
                    && i.SerieBorradorId == null && i.SerieRegistroId == null && i.FechaRegistro == null),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<NotaCreditoVentaBorradorResponse>(true, "ok", new NotaCreditoVentaBorradorResponse { Id = IdNota }));

        var html = HtmlSsr.Decodificar(await HtmlSsr.HtmlAsync(app.Cliente(), $"/facturas-venta/{Factura}"));
        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), $"/facturas-venta/{Factura}", "crear-nota", new Dictionary<string, string>());

        Assert.Contains("data-testid=\"crear-nota\"", html);
        Assert.Contains(">Crear nota de crédito</button>", html);
        Assert.DoesNotContain("CrearNotaInput.SerieBorradorId", html);
        Assert.Equal($"{Url(IdNota)}?ok=creado", FormulariosSsr.Destino(respuesta));
    }

    [Fact]
    public async Task FacturaRegistrada_ConNotaEnPreparacion_OfreceContinuar_SinCrearOtra()
    {
        using var app = Configurar(new BlazorSsrFactory(), notasEnPreparacion: [new NotaCreditoVentaBorradorResponse { Id = IdNota, Numero = "00000003", FacturaVentaNumero = Factura }]);

        var html = HtmlSsr.Decodificar(await HtmlSsr.HtmlAsync(app.Cliente(), $"/facturas-venta/{Factura}"));

        Assert.Contains($"href=\"{Url(IdNota)}\" data-testid=\"continuar-nota\"", html);
        Assert.Contains(">Continuar nota de crédito</a>", html);
        Assert.DoesNotContain("data-testid=\"crear-nota\"", html);
    }

    [Fact]
    public async Task FacturaRegistrada_SinPendiente_PostForzadoMuestraLaApi()
    {
        using var app = Configurar(new BlazorSsrFactory(), notasEnPreparacion: [], acreditadaPorCompleto: true);
        app.Simular<INotaCreditoVentaApiClient>()
            .Setup(c => c.CreateBorradorAsync(It.IsAny<NotaCreditoBorradorInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<NotaCreditoVentaBorradorResponse>(false, "Los datos enviados no son válidos.",
                Errors: [$"La factura {Factura} ya está acreditada por completo: no le queda nada pendiente de acreditar."]));

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), $"/facturas-venta/{Factura}");
        var forzado = await FormulariosSsr.EnviarAsync(app.Cliente(), $"/facturas-venta/{Factura}", "crear-nota", new Dictionary<string, string>());

        Assert.DoesNotContain("data-testid=\"crear-nota\"", html);
        Assert.Equal(HttpStatusCode.OK, forzado.StatusCode);
        Assert.Contains("ya está acreditada por completo", HtmlSsr.Decodificar(await forzado.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task EnPreparacion_DatosDerivados_RegistrarGuardarDescartar_YQuitarLinea()
    {
        using var app = Configurar(new BlazorSsrFactory(), notasEnPreparacion: []);

        var html = HtmlSsr.Decodificar(await HtmlSsr.HtmlAsync(app.Cliente(), Url(IdNota)));

        Assert.Contains(">Nota de crédito en preparación</h1>", html);
        Assert.Contains("data-testid=\"datos-derivados\"", html);
        Assert.Contains($">{Factura}</a></dd>", html);
        Assert.Contains(">Registrar nota de crédito</button>", html);
        Assert.Contains(">Guardar para después</button>", html);
        Assert.Contains(">Descartar</a>", html);
        Assert.Contains(">Quitar línea</a>", html);
        Assert.Contains("aria-label=\"Quitar línea 1\"", html);
        Assert.Contains("aria-label=\"Modificar línea 1\"", html);
        Assert.Contains("id=\"linea-10000\"", html);
        Assert.DoesNotContain(">Postear<", html);
        Assert.Contains("Se numerará con la serie <strong>NC</strong> · próximo <strong>NC-000045</strong>", html);
    }

    [Fact]
    public async Task EnPreparacion_Registrar_ConfirmacionYDestino()
    {
        using var app = Configurar(new BlazorSsrFactory(), notasEnPreparacion: []);
        var notas = app.Simular<INotaCreditoVentaApiClient>();
        notas.Setup(c => c.UpdateBorradorAsync(IdNota, It.Is<NotaCreditoCabeceraInput>(i => i.SerieRegistroId == IdSerieNc), 21, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<NotaCreditoVentaBorradorResponse>(true, "ok"));
        notas.Setup(c => c.PostearAsync(IdNota, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<ResultadoPosteoNotaCredito>(true, "ok", new ResultadoPosteoNotaCredito("NC-000045", 59m, 59m, "00000009", null)));

        var guardado = await FormulariosSsr.EnviarAsync(app.Cliente(), Url(IdNota), "guardar-cabecera-nota", new Dictionary<string, string>
        {
            ["UpdateInput.Xmin"] = "21", ["UpdateInput.FechaRegistroTexto"] = "2026-10-02", ["UpdateInput.FechaDocumentoTexto"] = "2026-10-02",
            ["UpdateInput.SerieRegistroId"] = IdSerieNc.ToString(), ["UpdateInput.Accion"] = "registrar",
        });
        var dialogo = HtmlSsr.Decodificar(await HtmlSsr.HtmlAsync(app.Cliente(), $"{Url(IdNota)}?registrar=true"));
        var registrado = await FormulariosSsr.EnviarAsync(app.Cliente(), $"{Url(IdNota)}?registrar=true", "confirm-postear", new Dictionary<string, string>());

        Assert.Equal($"{Url(IdNota)}?registrar=true", FormulariosSsr.Destino(guardado));
        Assert.Contains(">Registrar nota de crédito</h2>", dialogo);
        Assert.Contains("no se puede deshacer", dialogo);
        Assert.Contains($"<dd>{Factura}</dd>", dialogo);
        Assert.StartsWith("/notas-credito-venta/NC-000045?ok=registrada&aplicado=59.00", FormulariosSsr.Destino(registrado));
    }

    [Fact]
    public async Task EnPreparacion_NoAdministrador_NoVeNiEnviaLaSerie()
    {
        using var app = Configurar(new BlazorSsrFactory(), notasEnPreparacion: []);
        var notas = app.Simular<INotaCreditoVentaApiClient>();
        notas.Setup(c => c.UpdateBorradorAsync(IdNota, It.IsAny<NotaCreditoCabeceraInput>(), 21, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<NotaCreditoVentaBorradorResponse>(true, "ok"));

        var html = await HtmlSsr.HtmlAsync(app.Cliente("Supervisor"), Url(IdNota));
        await FormulariosSsr.EnviarAsync(app.Cliente("Supervisor"), Url(IdNota), "guardar-cabecera-nota", new Dictionary<string, string>
        {
            ["UpdateInput.Xmin"] = "21", ["UpdateInput.FechaRegistroTexto"] = "2026-10-02", ["UpdateInput.FechaDocumentoTexto"] = "2026-10-02",
            ["UpdateInput.SerieRegistroId"] = IdSerieNc.ToString(), ["UpdateInput.Accion"] = "despues",
        });

        Assert.DoesNotContain("name=\"UpdateInput.SerieRegistroId\"", html);
        notas.Verify(c => c.UpdateBorradorAsync(IdNota, It.Is<NotaCreditoCabeceraInput>(i => i.SerieRegistroId == null), 21, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Descartar_VuelveALaListaEnPreparacion()
    {
        using var app = Configurar(new BlazorSsrFactory(), notasEnPreparacion: []);
        app.Simular<INotaCreditoVentaApiClient>()
            .Setup(c => c.DeleteBorradorAsync(IdNota, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<bool>(true, "ok", true));

        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), $"{Url(IdNota)}?eliminar=true", "delete-borrador-nota", new Dictionary<string, string>());

        Assert.Equal("/notas-credito-venta?estado=preparacion&ok=descartada", FormulariosSsr.Destino(respuesta));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/editar")]
    public async Task RutasAntiguas_RedirigenAEnPreparacion(string sufijo)
    {
        using var app = Configurar(new BlazorSsrFactory(), notasEnPreparacion: []);

        var respuesta = await app.Cliente().GetAsync($"/notas-credito-venta/borradores/{IdNota}{sufijo}");

        Assert.Equal(HttpStatusCode.Redirect, respuesta.StatusCode);
        Assert.Equal(Url(IdNota), FormulariosSsr.Destino(respuesta));
    }

    private static BlazorSsrFactory Configurar(
        BlazorSsrFactory app, IReadOnlyList<NotaCreditoVentaBorradorResponse> notasEnPreparacion, bool acreditadaPorCompleto = false)
    {
        var facturas = app.Simular<IFacturaVentaApiClient>();
        facturas.Setup(c => c.GetFacturaAsync(Factura, It.IsAny<CancellationToken>())).ReturnsAsync(FacturaRegistrada());

        var notas = app.Simular<INotaCreditoVentaApiClient>();
        notas.Setup(c => c.ListBorradoresAsync(It.IsAny<NotaCreditoVentaBorradorFiltro?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<NotaCreditoVentaBorradorResponse>(notasEnPreparacion, 1, 50, notasEnPreparacion.Count));
        notas.Setup(c => c.ListNotasAsync(It.IsAny<NotaCreditoVentaSearchCriteria?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(acreditadaPorCompleto
                ? new PagedResult<NotaCreditoVentaResponse>([new NotaCreditoVentaResponse { Numero = "NC-000001", FacturaVentaNumero = Factura, ImporteTotal = 118m }], 1, 50, 1)
                : new PagedResult<NotaCreditoVentaResponse>([], 1, 50, 0));
        notas.Setup(c => c.GetNotaAsync("NC-000001", It.IsAny<CancellationToken>())).ReturnsAsync(NotaQueAcreditaTodo());
        notas.Setup(c => c.GetBorradorAsync(IdNota, It.IsAny<CancellationToken>())).ReturnsAsync(new NotaCreditoVentaBorradorResponse
        {
            Id = IdNota, Numero = "00000003", FacturaVentaNumero = Factura, FacturaImporteTotal = 118m, SocioNegocioId = IdSocio,
            SocioNegocioCodigo = "C-001", SocioNegocioNombre = "Comercial Uno", NombreFacturacion = "Comercial Uno", RazonSocialFacturacion = "Comercial Uno SRL",
            NumeroDocumentoFiscal = "101000001", Moneda = "DOP", CuentaCxCNumero = "1201", Estado = EstadoNotaCreditoBorrador.Abierta,
            SerieRegistroId = IdSerieNc, SerieRegistroCodigo = "NC", FechaRegistro = new DateOnly(2026, 10, 2), FechaDocumento = new DateOnly(2026, 10, 2),
            NumeroLineas = 1, ImporteTotal = 59m, Xmin = 21,
        });
        notas.Setup(c => c.ListLineasAsync(IdNota, It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new LineaNotaCreditoVentaBorradorResponse
            {
                Id = Guid.NewGuid(), NumeroLinea = 10000, Tipo = TipoLineaFactura.Producto, ProductoCodigo = "P-1", Descripcion = "Producto uno",
                Cantidad = 1m, CantidadFacturada = 2m, PrecioUnitario = 50m, ImporteLinea = 50m, IdentificadorIva = "ITBIS18", PorcentajeIva = 18m,
                DevolverInventario = true, Xmin = 4,
            },
        ]);
        notas.Setup(c => c.GetTotalesAsync(IdNota, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TotalesFactura([new GrupoIvaCalculado("ITBIS18", 18m, 50m, 9m)], 50m, 9m, 59m));
        notas.Setup(c => c.ListLineasAcreditablesAsync(IdNota, It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var series = app.Simular<ISerieApiClient>();
        series.Setup(c => c.ProximoAsync(IdSerieNc, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<ProximoNumeroResponse>(true, "ok", new ProximoNumeroResponse("NC-000045", null)));
        series.Setup(c => c.ActivasDelTipoAsync(It.IsAny<TipoDocumentoSerie>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SerieResponse { Id = IdSerieNc, Codigo = "NC", Descripcion = "Notas", TipoDocumento = TipoDocumentoSerie.NotaCreditoVenta, Activa = true }]);
        return app;
    }

    private static FacturaVentaDetalleResponse FacturaRegistrada() => new(
        new FacturaVentaResponse
        {
            Numero = Factura, SocioNegocioId = IdSocio, SocioNegocioCodigo = "C-001", SocioNegocioNombre = "Comercial Uno",
            SocioNegocioFacturarAId = IdSocio, NombreFacturacion = "Comercial Uno", ImporteSinIva = 100m, ImporteIva = 18m, ImporteTotal = 118m,
        },
        [new LineaFacturaVentaResponse { Id = 1, NumeroLinea = 10000, Tipo = TipoLineaFactura.Producto, ProductoCodigo = "P-1", Cantidad = 2m, PrecioUnitario = 50m, ImporteLinea = 100m }],
        []);

    private static NotaCreditoVentaDetalleResponse NotaQueAcreditaTodo() => new(
        new NotaCreditoVentaResponse { Numero = "NC-000001", FacturaVentaNumero = Factura, ImporteTotal = 118m },
        [new LineaNotaCreditoVentaResponse { LineaFacturaVentaId = 1, NumeroLinea = 10000, Tipo = TipoLineaFactura.Producto, Cantidad = 2m, ImporteLinea = 100m }],
        []);
}
```

(Los constructores de `FacturaVentaDetalleResponse`, `NotaCreditoVentaDetalleResponse`, `ResultadoPosteoNotaCredito` y los nombres de
propiedades de `LineaFacturaVentaResponse`/`LineaNotaCreditoVentaResponse` son los que ya usan `DocumentosPosteadosTests` y
`AccionesFacturasTests` para sus mocks; copiar de allí la forma exacta —posicional o con inicializador— y ajustar.)

`DocumentoNotaBorradorTests.cs` (la página única de la nota pasa a `/notas-credito-venta/en-preparacion/{id}`):
- `Url(Guid id) => $"/notas-credito-venta/en-preparacion/{id}"`.
- `PaginaUnica_CabeceraEditable_ConSerieYProximo_TablaYTotales`: `?postear=true` → el botón `data-testid="registrar"`; "Próximo número: <strong>" → "próximo <strong>"; añadir `data-testid="datos-derivados"`.
- `Postear_VaALaNota_ConAviso` → `Registrar_VaALaNota_ConAviso`: `?registrar=true`; destino `…?ok=registrada&aplicado=…&aviso=…`.
- `RutaEditar_Redirige_YListadoConFiltroPosteada` y `RutaEditar_RedirigeALaPaginaUnica_ConservandoElRetorno`: pedir la ruta antigua `/notas-credito-venta/borradores/{id}/editar…` y esperar `Url(id)` (+ `?returnUrl=` local). La parte del listado con `estado=3` se mantiene hasta R8.
- `Nueva_ConSelectoresDeSerie_EnviaLasDosSeries` → `Nueva_SinSelectoresDeSerie_CreaLaNotaTotalConDevolucion`: GET `/notas-credito-venta/nueva?socioId=…` sin `NotaInput.SerieBorradorId`/`SerieRegistroId`/`FechaRegistroTexto`; POST `nueva-nota-socio` con solo `NotaInput.FacturaVentaNumero`; `It.Is<NotaCreditoBorradorInput>(i => i.CopiarLineas && i.DevolverInventario && i.SerieBorradorId == null && i.SerieRegistroId == null)`; destino `/notas-credito-venta/en-preparacion/{nota}?ok=creado`.
- `FalloAlCargarSeries_NoBloqueaGuardar_YConservaLaSerieActual` (Administrador): el selector está en `mas-opciones`; `guardar-cabecera` → `registrar`.
- `Posteada_SinPostearEliminarNiAcreditar_LineasEnLectura_SinProximo`: `?postear=true` → `?registrar=true` en el `DoesNotContain`.
- `Eliminar_ConConfirmacion_VuelveAlListado` → destino `"/notas-credito-venta?estado=preparacion&ok=descartada"`.
- `ConRetorno_VolverLimpiarGuardarYEliminarLoConservan`: `?postear=true&{retorno}` → `?registrar=true&{retorno}`; el destino de eliminar conserva el `returnUrl` con `&ok=descartada`.
- `GuardarCabecera_EnviaFechasDescripcionYSerie` (Administrador): sin cambios de datos; destino `?ok=modificado` igual.

`DocumentosPosteadosTests.cs`:
- `CrearNota_DialogoConSeries_EnviaLasSeriesElegidas` → `CrearNota_DesdeLaBarra_SinCampos_CreaLaNotaTotal`: GET `/facturas-venta/{n}?crearNota=true` muestra el diálogo "Crear nota de crédito" sin `CrearNotaInput.SerieBorradorId` ni fechas; POST `crear-nota` → `It.Is<NotaCreditoBorradorInput>(i => i.CopiarLineas && i.DevolverInventario && i.SerieRegistroId == null)`; destino `/notas-credito-venta/en-preparacion/{nota}?ok=creado`.
- `Nota_DocumentoSoloLectura_ConEnlaceAlBorrador`: "Borrador de origen" → "Preparación" con `Ver preparación` a `/notas-credito-venta/en-preparacion/{IdBorrador}`.
- Las filas de "Notas de crédito" de la factura enlazan las notas en preparación a `/notas-credito-venta/en-preparacion/{id}`.

`AccionesFacturasTests.cs`:
- `FacturaDetalle_TieneBarraConNotaDeCreditoYMovimientos`: además del menú Crear ▾, `data-testid="crear-nota"`.
- `FacturaDetalle_AcreditadaPorCompleto_SinCrearNota`: `DoesNotContain("data-testid=\"crear-nota\"")`.
- `FacturaDetalle_DialogoNota_CancelarVuelveAlOrigen`: el diálogo ya no tiene campos; el "Cancelar" sigue volviendo al origen.
- `NotaCreditoNueva_ListaSoloFacturasConPendiente_YCreaElBorrador`: destino `/notas-credito-venta/en-preparacion/{nota}?ok=creado`.

`ConversionDocumentosUsuariosTests.cs`: la fila `/notas-credito-venta/borradores?editId={0}` de `EditIdLegado_RedirigeALaRutaNueva` se
mantiene hasta R8.

`ComponentesDocumentoTests.Estados_EnPreparacionYRegistrada`: añadir
`Assert.Equal("En preparación", VentasOpciones.EstadoNota(EstadoNotaCreditoBorrador.Abierta)); Assert.Equal("Registrada", VentasOpciones.EstadoNota(EstadoNotaCreditoBorrador.Posteada));`.

- [ ] **Step 2: Ejecutarlos (fallan)**

Run: `dotnet test tests/OpenSource1.SmokeTests --filter "FullyQualifiedName~AltaRapidaNotaTests"`
Expected: FAIL (la ruta `/notas-credito-venta/en-preparacion/{id}` no existe; la factura no tiene `crear-nota` visible).

- [ ] **Step 3: "Crear nota de crédito" en la factura registrada (`FacturasVenta.razor`)**

- En la barra del detalle (`ChildContent` del segundo `PageToolbar`), antes de "Volver al listado":

```razor
        @if (canAdd && detalle is not null && TienePendiente)
        {
            @if (NotaEnPreparacion is { } enPreparacion)
            {
                <a href="@($"{RutasPreparacion.Nota}/{enPreparacion.Id}")" data-testid="continuar-nota" class="btn-press rounded-lg bg-brand-600 px-3 py-2 text-sm font-semibold text-white shadow-sm hover:bg-brand-700">Continuar nota de crédito</a>
            }
            else
            {
                <EditForm Model="CrearNotaInput" FormName="crear-nota" OnValidSubmit="CrearNotaAsync">
                    <button type="submit" data-testid="crear-nota" class="btn-press rounded-lg bg-brand-600 px-3 py-2 text-sm font-semibold text-white shadow-sm hover:bg-brand-700">Crear nota de crédito</button>
                </EditForm>
            }
        }
```

  y el botón "Borradores" de las dos barras se borra (las pestañas de R8 lo sustituyen; mientras tanto, el menú lleva a la lista).
- `@code`: `private NotaCreditoVentaBorradorResponse? NotaEnPreparacion => borradoresNota.FirstOrDefault();` (la lista ya carga las
  notas en preparación de la factura). `MuestraFormCrearNota` pasa a `detalle is not null && canAdd && CrearNota && NotaEnPreparacion is null`;
  el formulario vacío de `crear-nota` se pinta cuando NO se muestra ni el diálogo ni el botón de la barra:
  `@if (!MuestraFormCrearNota && !(canAdd && detalle is not null && TienePendiente && NotaEnPreparacion is null)) { <EditForm … FormName="crear-nota" …></EditForm> }`.
- El diálogo `?crearNota=true` (lo abre el Crear ▾ de la lista) queda sin campos:

```razor
    @if (MuestraFormCrearNota)
    {
        var c = detalle!.Cabecera;
        <ConfirmDialog Title="Crear nota de crédito" Message="@($"Se creará una nota de crédito en preparación de la factura {c.Numero} con todas sus líneas pendientes y la devolución al inventario; podrá ajustar las cantidades o quitar líneas antes de registrarla.")" Variant="warning" CancelHref="@VolverNota">
            @if (crearNotaError is not null)
            {
                <div class="mb-4" data-testid="errores-crear-nota"><ResumenErrores Mensaje="@crearNotaError" Errores="@crearNotaErrores" /></div>
            }
            <EditForm Model="CrearNotaInput" FormName="crear-nota" OnValidSubmit="CrearNotaAsync" Enhance="true">
                <button type="submit" class="btn-press rounded-lg bg-brand-600 px-4 py-2 text-sm font-semibold text-white shadow-sm hover:bg-brand-700">Sí, crear</button>
                <a href="@VolverNota" class="btn-press ml-2 rounded-lg border border-slate-300 bg-white px-4 py-2 text-sm font-semibold text-slate-600 shadow-sm hover:bg-slate-50">Cancelar</a>
            </EditForm>
        </ConfirmDialog>
    }
```

  (se borran `seriesBorradorNota`, `seriesRegistroNota`, su carga en `OnParametersSetAsync`, `LimpiarNotaHref` y los campos de
  `CrearNotaForm`, que queda como `public sealed class CrearNotaForm { }`; si `ISerieApiClient` deja de usarse en la página, quitar su `@inject`.)
- `CrearNotaAsync`:

```csharp
            var result = await NotaApiClient.CreateBorradorAsync(new NotaCreditoBorradorInput(
                detalle.Cabecera.Numero, null, null, null, CopiarLineas: true, DevolverInventario: true));
            if (result.Succeeded)
            {
                destino = $"{RutasPreparacion.Nota}/{result.Valor!.Id}?ok=creado";
            }
```

  y, si el diálogo no está a la vista, el error se muestra arriba con `Show("warning", result.Message, result.Errors)` (como hoy).
- Filas de "Notas de crédito" de la factura: `href="@($"{RutasPreparacion.Nota}/{b.Id}")"` para las notas en preparación; el texto
  del resumen "`{borradoresNota.Count} borrador(es)`" pasa a "`{borradoresNota.Count} en preparación`".

- [ ] **Step 4: `/notas-credito-venta/en-preparacion/{id}` (cambios en `NotaCreditoVentaBorrador.razor`)**

1. `@page "/notas-credito-venta/en-preparacion/{BorradorId:guid}"`; `<PageTitle>@Titulo – AxionERP</PageTitle>`; comentario: "Nota de crédito
   en preparación (spec alta rápida): nace de la factura registrada con todas las líneas pendientes; datos de la factura y del cliente
   en solo lectura; fechas y descripción editables; por línea, cantidad (tope: lo acreditable) y devolución, o Quitar línea; Registrar
   nota de crédito (confirmación), Guardar para después y Descartar."
2. Barra: `Titulo="@Titulo"` (`"Nota de crédito en preparación"`, o `$"Preparación de la nota {borrador.NotaCreditoVentaNumero}"` si
   está registrada), subtítulo `$"Factura {borrador.FacturaVentaNumero} · {VentasOpciones.EstadoNota(borrador.Estado)}"`, sin
   `EliminarUrl`; en `ChildContent` se borra el enlace "Postear" y se añade, si `PuedeEliminar`,
   `<a href="@BuildUrl(eliminar: true)" data-testid="descartar" class="…rojo…">Descartar</a>`.
3. Mensajes: igual que la factura (Step 7.3 de R6): `ActionFeedback` para `success` y `ResumenErrores` para el resto; `errores-posteo`
   envuelve un `ResumenErrores`.
4. Cabecera: dentro de `DocumentoCabeceraEditable` (con `Botones="@BotonesCabecera"`), sustituir el párrafo de la factura, los dos campos
   de serie (`SelectorSerie` y `SerieNumeracionInfo`) por:

```razor
    <DatosDerivados Datos="@DatosNota" />
    <NumeracionRegistro Codigo="@borrador?.SerieRegistroCodigo" Proximo="@proximo" PuedeCambiar="@esAdministrador"
                        Id="UpdateInput_SerieRegistroId" Name="UpdateInput.SerieRegistroId" Series="@seriesRegistro.Items"
                        Seleccion="@UpdateInput.SerieRegistroId" VigenteId="@borrador?.SerieRegistroId" CargaFallida="@seriesRegistro.CargaFallida" />
```

   conservando los tres campos de fechas y descripción y borrando los enlaces "Limpiar campos"/"Cancelar" del final (pasan a `BotonesCabecera`). En `@code`:

```csharp
    private IReadOnlyList<DatoDerivado> DatosNota => borrador is null ? [] :
    [
        new("Factura de origen", borrador.FacturaVentaNumero, FacturaHref),
        new("Cliente", $"{borrador.SocioNegocioCodigo} — {borrador.NombreFacturacion}", $"/clientes/{borrador.SocioNegocioFacturarAId}"),
        new("RNC / razón social", DatosDocumento.Fiscal(borrador.RazonSocialFacturacion, borrador.NumeroDocumentoFiscal)),
        new("Moneda", borrador.Moneda),
        new("Cuenta por cobrar", borrador.CuentaCxCNumero ?? "—"),
        new("Total de la factura", VentasOpciones.Importe(borrador.FacturaImporteTotal)),
    ];

    private RenderFragment BotonesCabecera => __builder =>
    {
        @if (MotivoNoRegistrable is { } motivo)
        {
            <button type="button" disabled data-testid="registrar" aria-describedby="motivo-registrar" class="rounded-lg bg-slate-300 px-4 py-2 text-sm font-semibold text-white">Registrar nota de crédito</button>
            <p id="motivo-registrar" class="w-full text-xs text-red-700" data-testid="motivo-registrar">@motivo</p>
        }
        else
        {
            <button type="submit" name="UpdateInput.Accion" value="@AccionCabecera.Registrar" data-testid="registrar" class="btn-press rounded-lg bg-brand-600 px-4 py-2 text-sm font-semibold text-white shadow-sm hover:bg-brand-700">Registrar nota de crédito</button>
        }
        <button type="submit" name="UpdateInput.Accion" value="@AccionCabecera.Despues" data-testid="guardar-despues" class="btn-press rounded-lg border border-slate-300 bg-white px-4 py-2 text-sm font-semibold text-slate-700 shadow-sm hover:bg-slate-50">Guardar para después</button>
        <button type="submit" data-testid="guardar-cabecera" class="btn-press rounded-lg border border-slate-300 bg-white px-4 py-2 text-sm font-semibold text-slate-700 shadow-sm hover:bg-slate-50">Guardar cambios</button>
        <a href="@UrlPropia" data-testid="limpiar-cabecera" class="btn-press rounded-lg border border-slate-300 bg-white px-4 py-2 text-sm font-semibold text-slate-600 shadow-sm hover:bg-slate-50">Limpiar campos</a>
        <a href="@Volver" data-testid="cancelar-cabecera" class="btn-press rounded-lg border border-slate-300 bg-white px-4 py-2 text-sm font-semibold text-slate-600 shadow-sm hover:bg-slate-50">Cancelar</a>
    };

    private string? MotivoNoRegistrable => proximo?.Error is { } error ? $"No se puede registrar: {error}" : null;

    private IReadOnlyList<DatoDerivado> ResumenRegistro => borrador is null ? [] :
    [
        new("Total", totales is null ? "—" : VentasOpciones.Importe(totales.ImporteTotal)),
        new("Factura de origen", borrador.FacturaVentaNumero),
        new("Serie", borrador.SerieRegistroCodigo ?? "—"),
        new("Próximo número", proximo?.Numero ?? "—"),
    ];
```

5. Tabla de líneas: `@foreach (var (l, i) in lineas.OrderBy(x => x.NumeroLinea).Select((l, i) => (l, i)))`; `<tr @key="l.Id" id="linea-@l.NumeroLinea" …>`;
   el enlace "Modificar" con `aria-label="@($"Modificar línea {i + 1}")"`; el de borrar pasa a
   `<a href="@BuildUrl(deleteLineaId: l.Id)" aria-label="@($"Quitar línea {i + 1}")" class="…rojo…">Quitar línea</a>`. Textos: "El
   borrador todavía no tiene líneas…" → "La nota todavía no tiene líneas: elija abajo las líneas de la factura a acreditar."; el aviso de
   importes: "Vista previa: los importes definitivos se calculan al registrar, descontando lo ya acreditado de la factura."; el diálogo
   de borrar línea: `Title="Quitar línea"`, mensaje "Esta acción quitará la línea seleccionada de la nota de crédito.", botón "Sí, quitar".
6. Diálogo de posteo:

```razor
    @if (MuestraDialogoPosteo)
    {
        <ConfirmarRegistro Titulo="Registrar nota de crédito"
                           Aviso="Se generará la nota de crédito con su asiento, se aplicará a la factura y se devolverá al inventario lo marcado; no se puede deshacer."
                           Resumen="@ResumenRegistro" Model="PostearInput" FormName="confirm-postear" OnValidSubmit="PostearAsync" SeguirEditandoHref="@UrlPropia">
            <Errores>
                @if (posteoError is not null)
                {
                    <div data-testid="errores-posteo"><ResumenErrores Mensaje="@posteoError" Errores="@posteoErrores" /></div>
                }
            </Errores>
        </ConfirmarRegistro>
    }
```

   y el de eliminar: `Title="Descartar nota de crédito"`, mensaje "Se descartará la nota de crédito en preparación con todas sus líneas.
   No se puede deshacer.", botón "Sí, descartar".
7. `@code`: `[SupplyParameterFromQuery(Name = "registrar")] private bool RegistrarConfirm { get; set; }` y `postear` como alias
   (`PostearLegado`), `MuestraDialogoPosteo => borrador is not null && (RegistrarConfirm || PostearLegado) && canModify && !EsPosteada`;
   `UrlBase => $"{RutasPreparacion.Nota}/{BorradorId}"`; `Volver => RetornoLocal.Volver(ReturnUrl, RutasPreparacion.ListaNotas)`;
   `Migas => [Facturación, Notas de crédito ("/notas-credito-venta"), En preparación (RutasPreparacion.ListaNotas), borrador?.NombreFacturacion ?? "Nota de crédito"]`;
   `esAdministrador = await permisos.PuedeAsync(ApplicationPolicies.CanAdministrar);` tras los permisos; las series activas solo se cargan
   si `esAdministrador`; al rellenar `UpdateInput` (Xmin = 0), `UpdateInput.SerieRegistroId = esAdministrador ? borrador.SerieRegistroId : null`.
   `Ok`: `"creado" => "Nota de crédito en preparación con todas las líneas pendientes de la factura. Ajuste las cantidades o quite líneas para una nota parcial."`,
   `"modificado" => "Cambios guardados."`, `"linea_eliminada" => "Línea quitada."`.
   `GuardarCabeceraAsync`:

```csharp
        var f = UpdateInput!;
        var input = new NotaCreditoCabeceraInput(
            f.FechaRegistro, f.FechaDocumento, f.Descripcion?.Trim() ?? string.Empty, esAdministrador ? f.SerieRegistroId : null);
        string? destino = null;
        try
        {
            var result = await NotaApiClient.UpdateBorradorAsync(BorradorId, input, f.Xmin);
            if (result.Succeeded)
            {
                destino = f.Accion switch
                {
                    AccionCabecera.Registrar => BuildUrl(registrar: true),
                    AccionCabecera.Despues => RetornoLocal.ConParametro(RutasPreparacion.ListaNotas, "ok", "guardada"),
                    _ => RetornoLocal.ConParametro(BuildUrl(), "ok", "modificado"),
                };
            }
            else
            {
                Show("warning", result.Message, result.Errors);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not update nota de crédito en preparación {Id}.", BorradorId);
            Show("danger", "No fue posible guardar los cambios.");
        }

        if (destino is not null)
        {
            Nav.NavigateTo(destino);
        }
```

   (conservando la guarda inicial de `borrador is null` del método actual). `EliminarBorradorAsync` → `RetornoLocal.ConParametro(Volver, "ok", "descartada")`.
   `PostearAsync` → `$"{NotaHref(r.Numero)}?ok=registrada&aplicado={…}{aviso}"`; mensaje de excepción "No fue posible registrar la nota de crédito.".
   `BuildUrl`: parámetro `postear` → `registrar` (`"registrar=true"`).
8. `NotasCreditoVentaBorradores.razor`, clase `CabeceraNotaForm`: añadir
   `public string? Accion { get; set; }` (con el mismo `<summary>` que en `BorradorForm`).

- [ ] **Step 5: Alta secundaria, redirecciones, nota registrada y estados**

`NotaCreditoNueva.razor`:
- Quitar del `EditForm` los campos de fechas, descripción, los dos `SelectorSerie`, las dos casillas y el texto de ayuda; queda el
  `<fieldset data-testid="facturas-acreditables">` con los radios y, como acciones,
  `<FormularioAcciones CancelarHref="@Volver" LimpiarHref="@Nav.Uri" TextoGuardar="Crear nota de crédito" />`.
- Subtítulo de `EntityFormPage`: "Elija la factura registrada con importe pendiente: la nota se crea con todas sus líneas pendientes y la devolución al inventario; después podrá ajustarla."
- `Volver => RetornoLocal.Volver(ReturnUrl, RutasPreparacion.ListaNotas)`; migas `[Facturación, Notas de crédito ("/notas-credito-venta"), Nueva nota de crédito]`.
- Quitar `ISerieApiClient`, `seriesBorrador`, `seriesRegistro` y su carga; en `NuevaNotaForm` quitar `FechaRegistroTexto`,
  `FechaDocumentoTexto`, `Descripcion`, `CopiarLineas`, `DevolverInventario`, `SerieBorradorId`, `SerieRegistroId`, `Validate` y `Fecha`
  (queda `FacturaVentaNumero` con su `[Required]`; la clase deja de implementar `IValidatableObject`).
- `CrearAsync`:

```csharp
            var result = await NotaApiClient.CreateBorradorAsync(new NotaCreditoBorradorInput(
                f.FacturaVentaNumero.Trim(), null, null, null, CopiarLineas: true, DevolverInventario: true));
            if (result.Succeeded)
            {
                destino = $"{RutasPreparacion.Nota}/{result.Valor!.Id}?ok=creado";
            }
```

`NotaCreditoVentaBorradorRedireccion.razor`:

```razor
@page "/notas-credito-venta/borradores/{BorradorId:guid}"
@attribute [Authorize(Policy = ApplicationPolicies.CanConsult)]
@using Microsoft.AspNetCore.Authorization
@inject NavigationManager Nav

@* Spec alta rápida: la ruta antigua de la nota en preparación redirige a /notas-credito-venta/en-preparacion/{id} (returnUrl local). *@

@code {
    [Parameter] public Guid BorradorId { get; set; }
    [SupplyParameterFromQuery(Name = "returnUrl")] private string? ReturnUrl { get; set; }

    protected override void OnInitialized() =>
        Nav.NavigateTo(RutasPreparacion.Redireccion(RutasPreparacion.Nota, BorradorId, ReturnUrl, RutasPreparacion.ListaNotas), replace: true);
}
```

`NotaCreditoBorradorCabecera.razor` (`/notas-credito-venta/borradores/{id}/editar`): `OnInitialized` con la misma llamada.

`NotasCreditoVenta.razor` (detalle): `new("Preparación", c.NotaCreditoVentaBorradorId is null ? "—" : "Ver preparación", c.NotaCreditoVentaBorradorId is { } b ? $"{RutasPreparacion.Nota}/{b}" : null)`;
mensaje `"registrada"` (y `"posteada"`): `$"Nota de crédito {numero} registrada."` con el importe aplicado como hoy; el botón "Borradores"
de la barra se borra.

`VentasOpciones.EstadoNota`: `Abierta => "En preparación"`, `Posteada => "Registrada"`.

- [ ] **Step 6: Verde**

Run: `dotnet test tests/OpenSource1.SmokeTests --filter "FullyQualifiedName~AltaRapidaNotaTests|FullyQualifiedName~DocumentoNotaBorradorTests|FullyQualifiedName~DocumentosPosteadosTests|FullyQualifiedName~AccionesFacturasTests|FullyQualifiedName~ConversionDocumentosUsuariosTests|FullyQualifiedName~ComponentesDocumentoTests|FullyQualifiedName~AltaRapidaFacturaTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/OpenSource1.Blazor/Components/Pages/FacturasVenta.razor src/OpenSource1.Blazor/Components/Pages/NotaCreditoVentaBorrador.razor \
  src/OpenSource1.Blazor/Components/Pages/NotaCreditoVentaBorradorRedireccion.razor src/OpenSource1.Blazor/Components/Pages/NotaCreditoBorradorCabecera.razor \
  src/OpenSource1.Blazor/Components/Pages/NotaCreditoNueva.razor src/OpenSource1.Blazor/Components/Pages/NotasCreditoVentaBorradores.razor \
  src/OpenSource1.Blazor/Components/Pages/NotasCreditoVenta.razor src/OpenSource1.Blazor/Components/VentasOpciones.cs \
  tests/OpenSource1.SmokeTests/Blazor/AltaRapidaNotaTests.cs tests/OpenSource1.SmokeTests/Blazor/DocumentoNotaBorradorTests.cs \
  tests/OpenSource1.SmokeTests/Blazor/DocumentosPosteadosTests.cs tests/OpenSource1.SmokeTests/Blazor/AccionesFacturasTests.cs \
  tests/OpenSource1.SmokeTests/Blazor/ConversionDocumentosUsuariosTests.cs tests/OpenSource1.SmokeTests/Blazor/ComponentesDocumentoTests.cs
git commit -m "feat: nota de credito desde la factura registrada y nota en preparacion con registrar y descartar"
```

---
### Task R8: Navegación — Facturación con 2 entradas, pestañas "Registradas" / "En preparación (n)" y búsqueda

**Grupo de paralelismo:** Fase 2, serie (tras R7). Sin Docker salvo `BusquedaApiTests` (Postgres, bajo `flock`).

**Files:**
- Create: `src/OpenSource1.Blazor/Components/Documento/PestanasDocumento.razor`
- Create: `src/OpenSource1.Blazor/Components/Documento/ListaFacturasEnPreparacion.razor`
- Create: `src/OpenSource1.Blazor/Components/Documento/ListaNotasEnPreparacion.razor`
- Modify: `src/OpenSource1.Blazor/Components/Pages/FacturasVenta.razor` (modo listado: pestañas, "+ Nueva factura", lista en preparación)
- Modify: `src/OpenSource1.Blazor/Components/Pages/NotasCreditoVenta.razor` (modo listado: pestañas, "+ Nueva nota de crédito", lista en preparación)
- Modify: `src/OpenSource1.Blazor/Components/Pages/FacturasVentaBorradores.razor` (redirección; conserva `BorradorForm`)
- Modify: `src/OpenSource1.Blazor/Components/Pages/NotasCreditoVentaBorradores.razor` (redirección; conserva `CabeceraNotaForm`)
- Modify: `src/OpenSource1.Blazor/Navigation/CatalogoModulos.cs`, `src/OpenSource1.Blazor/Navigation/IndicadoresModulos.cs`
- Modify: `src/OpenSource1.Blazor/Components/Pages/Buscar.razor` (`VerTodosUrl`)
- Modify: `src/OpenSource1.Infrastructure/Data/Queries/DapperBusquedaGlobalRepository.cs` (títulos y rutas de los grupos de borradores)
- Create: `tests/OpenSource1.SmokeTests/Blazor/ListasEnPreparacionTests.cs`
- Test: `tests/OpenSource1.SmokeTests/Blazor/RegistroModulosTests.cs`, `NavMenuTests.cs`, `InicioYGruposTests.cs`, `BuscarPaginaTests.cs`, `AccionesFacturasTests.cs`, `ConversionDocumentosUsuariosTests.cs`, `DocumentoFacturaBorradorTests.cs`, `DocumentoNotaBorradorTests.cs`
- Test: `tests/OpenSource1.SmokeTests/Api/BusquedaApiTests.cs`

**Interfaces:**
- Consumes (R4, R6, R7): `ContarEnPreparacionAsync`, `ListBorradoresAsync` (sin estado = en preparación), `FacturaVentaBorradorResponse.ImporteTotal/NumeroLineas/CreatedAtUtc`, `NotaCreditoVentaBorradorResponse.ImporteTotal`, `RutasPreparacion`, `VentasOpciones.Estado/EstadoNota`, `ResumenErrores`.
- Produces (R9):
  - `/facturas-venta` y `/notas-credito-venta` con `<nav data-testid="pestanas-documento">`: `pestana-registradas` ("Registradas") y `pestana-preparacion` ("En preparación (n)" o "En preparación" si el conteo no carga); `?estado=preparacion` muestra la lista en preparación (`data-testid="tabla-en-preparacion"`, columnas Cliente, Creado el, Líneas, Total, Estado, Acciones; filas con "Continuar" y "Descartar").
  - Menú/inicio/paleta: grupo Facturación con 2 módulos (`facturas`, `notas-credito`); palabras clave "borrador", "en preparación", "en preparacion".
  - `/facturas-venta/borradores[?numero&nombre&estado&editId]` y `/notas-credito-venta/borradores[…]` redirigen a la pestaña (o a la página en preparación con `editId`).
  - Búsqueda global: grupos "Facturas en preparación" y "Notas de crédito en preparación" con rutas `/…/en-preparacion/{id}`; "Ver todos" a `?estado=preparacion&…`.

- [ ] **Step 1: Tests (fallan)**

Crear `tests/OpenSource1.SmokeTests/Blazor/ListasEnPreparacionTests.cs`:

```csharp
extern alias BlazorApp;
using System.Net;
using BlazorApp::OpenSource1.Blazor.Services;
using Moq;
using OpenSource1.Application.Features.FacturasVenta.Borradores.Dtos;
using OpenSource1.Application.Features.FacturasVenta.Posteadas;
using OpenSource1.Application.Features.FacturasVenta.Posteadas.Dtos;
using OpenSource1.Application.Features.NotasCreditoVenta.Borradores.Dtos;
using OpenSource1.Application.Features.NotasCreditoVenta.Posteadas;
using OpenSource1.Application.Features.NotasCreditoVenta.Posteadas.Dtos;
using OpenSource1.Core.Common;
using OpenSource1.Core.Enums;
using OpenSource1.SmokeTests.TestInfrastructure;

namespace OpenSource1.SmokeTests.Blazor;

/// <summary>
/// Spec alta rápida (Parte 2, navegación): listas de facturas y notas con pestañas "Registradas" / "En preparación (n)"
/// (?estado=preparacion), la lista en preparación (cliente, creado el, líneas, total, Continuar y Descartar) y las rutas antiguas de
/// las listas de borradores, que redirigen.
/// </summary>
public sealed class ListasEnPreparacionTests
{
    private static readonly Guid IdAbierta = Guid.Parse("7d0c9a51-0000-0000-0000-0000000008a1");
    private static readonly Guid IdLiberada = Guid.Parse("7d0c9a51-0000-0000-0000-0000000008a2");
    private static readonly Guid IdNota = Guid.Parse("7d0c9a51-0000-0000-0000-0000000008a3");

    [Fact]
    public async Task Facturas_PestanasConConteo_YNuevaFactura()
    {
        using var app = Configurar(new BlazorSsrFactory());

        var registradas = HtmlSsr.Decodificar(await HtmlSsr.HtmlAsync(app.Cliente(), "/facturas-venta"));
        var preparacion = HtmlSsr.Decodificar(await HtmlSsr.HtmlAsync(app.Cliente(), "/facturas-venta?estado=preparacion"));

        Assert.Contains("data-testid=\"pestanas-documento\"", registradas);
        Assert.Contains("aria-current=\"page\" class=\"inline-block border-b-2 border-brand-600 px-1 pb-2 text-brand-700\" data-testid=\"pestana-registradas\">Registradas</a>", registradas);
        Assert.Contains(">En preparación (2)</a>", registradas);
        Assert.Contains("data-testid=\"tabla-facturas\"", registradas);
        Assert.Contains(">+ Nueva factura</a>", registradas);
        Assert.DoesNotContain("href=\"/facturas-venta/borradores\"", registradas);
        Assert.Contains("data-testid=\"pestana-preparacion\">En preparación (2)</a>", preparacion);
        Assert.Contains("data-testid=\"tabla-en-preparacion\"", preparacion);
        Assert.DoesNotContain("data-testid=\"tabla-facturas\"", preparacion);
    }

    [Fact]
    public async Task Facturas_EnPreparacion_ClienteCreadoLineasTotal_ContinuarYDescartar()
    {
        using var app = Configurar(new BlazorSsrFactory());

        var html = HtmlSsr.Decodificar(await HtmlSsr.HtmlAsync(app.Cliente(), "/facturas-venta?estado=preparacion"));

        Assert.Contains(">Cliente</th>", html);
        Assert.Contains(">Creado el</th>", html);
        Assert.Contains(">Líneas</th>", html);
        Assert.Contains(">Total</th>", html);
        Assert.Contains(">1,234.50</td>", html);
        Assert.Contains($"href=\"/facturas-venta/en-preparacion/{IdAbierta}\"", html);
        Assert.Contains(">Continuar</a>", html);
        Assert.Contains($"href=\"/facturas-venta?estado=preparacion&deleteId={IdAbierta}\"", html);
        Assert.DoesNotContain($"deleteId={IdLiberada}", html);
        Assert.Contains(">En preparación</span>", html);
        Assert.Contains(">En preparación (liberada)</span>", html);
    }

    [Fact]
    public async Task Facturas_EnPreparacion_Descartar_ConConfirmacion()
    {
        using var app = Configurar(new BlazorSsrFactory());
        app.Simular<IFacturaVentaApiClient>()
            .Setup(c => c.DeleteBorradorAsync(IdAbierta, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<bool>(true, "ok", true));
        var url = $"/facturas-venta?estado=preparacion&deleteId={IdAbierta}";

        var html = HtmlSsr.Decodificar(await HtmlSsr.HtmlAsync(app.Cliente(), url));
        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), url, "delete-borrador", new Dictionary<string, string> { ["DeleteInput.Id"] = IdAbierta.ToString() });

        Assert.Contains(">Descartar factura</h2>", html);
        Assert.Equal("/facturas-venta?estado=preparacion&ok=descartada", FormulariosSsr.Destino(respuesta));
    }

    [Fact]
    public async Task Facturas_ConteoQueNoCarga_PestanaSinNumero()
    {
        using var app = Configurar(new BlazorSsrFactory());
        app.Simular<IFacturaVentaApiClient>()
            .Setup(c => c.ContarEnPreparacionAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("API caída"));

        var html = HtmlSsr.Decodificar(await HtmlSsr.HtmlAsync(app.Cliente(), "/facturas-venta"));

        Assert.Contains("data-testid=\"pestana-preparacion\">En preparación</a>", html);
    }

    [Fact]
    public async Task Notas_PestanasConConteo_NuevaNota_YLista()
    {
        using var app = Configurar(new BlazorSsrFactory());

        var registradas = HtmlSsr.Decodificar(await HtmlSsr.HtmlAsync(app.Cliente(), "/notas-credito-venta"));
        var preparacion = HtmlSsr.Decodificar(await HtmlSsr.HtmlAsync(app.Cliente(), "/notas-credito-venta?estado=preparacion"));

        Assert.Contains(">En preparación (1)</a>", registradas);
        Assert.Contains(">+ Nueva nota de crédito</a>", registradas);
        Assert.DoesNotContain("href=\"/notas-credito-venta/borradores\"", registradas);
        Assert.Contains("data-testid=\"tabla-en-preparacion\"", preparacion);
        Assert.Contains($"href=\"/notas-credito-venta/en-preparacion/{IdNota}\"", preparacion);
        Assert.Contains(">59.00</td>", preparacion);
    }

    [Theory]
    [InlineData("/facturas-venta/borradores", "/facturas-venta?estado=preparacion")]
    [InlineData("/facturas-venta/borradores?numero=0001&nombre=Com", "/facturas-venta?estado=preparacion&numero=0001&nombre=Com")]
    [InlineData("/facturas-venta/borradores?estado=3", "/facturas-venta")]
    [InlineData("/facturas-venta/borradores?editId=7d0c9a51-0000-0000-0000-0000000008a1", "/facturas-venta/en-preparacion/7d0c9a51-0000-0000-0000-0000000008a1")]
    [InlineData("/notas-credito-venta/borradores", "/notas-credito-venta?estado=preparacion")]
    [InlineData("/notas-credito-venta/borradores?editId=7d0c9a51-0000-0000-0000-0000000008a3", "/notas-credito-venta/en-preparacion/7d0c9a51-0000-0000-0000-0000000008a3")]
    public async Task ListasAntiguas_Redirigen(string origen, string destino)
    {
        using var app = Configurar(new BlazorSsrFactory());

        var respuesta = await app.Cliente().GetAsync(origen);

        Assert.Equal(HttpStatusCode.Redirect, respuesta.StatusCode);
        Assert.Equal(destino, FormulariosSsr.Destino(respuesta));
    }

    private static BlazorSsrFactory Configurar(BlazorSsrFactory app)
    {
        var facturas = app.Simular<IFacturaVentaApiClient>();
        facturas.Setup(c => c.ContarEnPreparacionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(2L);
        facturas.Setup(c => c.ListFacturasAsync(It.IsAny<FacturaVentaSearchCriteria?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<FacturaVentaResponse>([], 1, 50, 0));
        facturas.Setup(c => c.ListBorradoresAsync(It.IsAny<FacturaVentaBorradorFiltro?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<FacturaVentaBorradorResponse>(
            [
                new FacturaVentaBorradorResponse
                {
                    Id = IdAbierta, Numero = "00000041", Estado = EstadoFacturaBorrador.Abierta, SocioNegocioCodigo = "C-001", NombreFacturacion = "Comercial Uno",
                    NumeroLineas = 2, ImporteTotal = 1234.50m, CreatedAtUtc = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc),
                },
                new FacturaVentaBorradorResponse
                {
                    Id = IdLiberada, Numero = "00000040", Estado = EstadoFacturaBorrador.Liberada, SocioNegocioCodigo = "C-002", NombreFacturacion = "Comercial Dos",
                    NumeroLineas = 1, ImporteTotal = 10m, CreatedAtUtc = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc),
                },
            ], 1, 50, 2));
        facturas.Setup(c => c.GetBorradorAsync(IdAbierta, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FacturaVentaBorradorResponse { Id = IdAbierta, NombreFacturacion = "Comercial Uno", NumeroLineas = 2 });

        var notas = app.Simular<INotaCreditoVentaApiClient>();
        notas.Setup(c => c.ContarEnPreparacionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1L);
        notas.Setup(c => c.ListNotasAsync(It.IsAny<NotaCreditoVentaSearchCriteria?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<NotaCreditoVentaResponse>([], 1, 50, 0));
        notas.Setup(c => c.ListBorradoresAsync(It.IsAny<NotaCreditoVentaBorradorFiltro?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<NotaCreditoVentaBorradorResponse>(
            [
                new NotaCreditoVentaBorradorResponse
                {
                    Id = IdNota, Numero = "00000003", FacturaVentaNumero = "FV-000123", Estado = EstadoNotaCreditoBorrador.Abierta, SocioNegocioCodigo = "C-001",
                    NombreFacturacion = "Comercial Uno", NumeroLineas = 1, ImporteTotal = 59m, CreatedAtUtc = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc),
                },
            ], 1, 50, 1));
        return app;
    }
}
```

`RegistroModulosTests.cs`:
- `RutasFueraDelRegistro`: añadir `"/facturas-venta/borradores", "/notas-credito-venta/borradores"` (solo redirigen; comentario: "listas antiguas de borradores: redirigen a la pestaña En preparación (spec alta rápida)").
- `ModuloDeRuta_ElPrefijoMasLargoGana`: `("facturas-venta/borradores/{guid}", "facturas")`.
- `ModuloDeRuta_AltasYEdiciones_MarcanSuModulo`: `/notas-credito-venta/nueva[?socioId=…]` → `"notas-credito"`; `/facturas-venta/nueva[/?socioId=…]` → `"facturas"`; `/facturas-venta/borradores/{guid}/editar` → `"facturas"`; `/notas-credito-venta/borradores/{guid}/editar` → `"notas-credito"`; añadir `("/facturas-venta/en-preparacion/3f2b8c1e-0000-0000-0000-000000000001", "facturas")` y `("/notas-credito-venta/en-preparacion/3f2b8c1e-0000-0000-0000-000000000001", "notas-credito")`. Actualizar el `<summary>` del test ("las altas y las páginas en preparación marcan el módulo de su documento").
- Nuevo:

```csharp
    [Fact]
    public void Facturacion_DosEntradas_YLasPalabrasClaveDeLaPreparacion()
    {
        var facturacion = CatalogoModulos.Modulos.Where(m => m.Grupo == "facturacion").Select(m => m.Clave).ToList();

        Assert.Equal(["facturas", "notas-credito"], facturacion);
        Assert.Equal(["facturas", "notas-credito"], TextoBusqueda.FiltrarModulos(CatalogoModulos.Modulos, "borrador").Select(m => m.Clave).Order());
        Assert.Equal(["facturas", "notas-credito"], TextoBusqueda.FiltrarModulos(CatalogoModulos.Modulos, "en preparación").Select(m => m.Clave).Order());
        Assert.Empty(CatalogoModulos.RutasAsociadas);
    }
```

(si `FiltrarModulos` también busca en títulos y descripciones y devuelve más módulos para "borrador", sustituir el `Equal` por dos
`Contains` de `facturas` y `notas-credito`.)

`NavMenuTests.AltaDeDocumento_MarcaSuModuloEnElMenu`: `("/facturas-venta/nueva", "/facturas-venta", "facturacion")` y
`("/notas-credito-venta/nueva", "/notas-credito-venta", "facturacion")`; resumen: "las altas de documentos marcan su documento".

`InicioYGruposTests.cs` (indicadores de Facturación): las aserciones pasan a `"Facturas en preparación"`, `"Facturas registradas"` y
`"Notas de crédito en preparación"`; el `Verify` de `ListBorradoresAsync` exige `f == null` (en preparación = Abierta + Liberada); el
orden esperado del test del tiempo máximo: `["Facturas registradas", "Notas de crédito en preparación"]`; `href="/facturas-venta?estado=preparacion"`.

`BuscarPaginaTests.cs` (`VerTodosUrl`): `BorradoresFactura` → `"/facturas-venta?estado=preparacion&numero=0001"` y
`"/facturas-venta?estado=preparacion&nombre=B%201"`; `BorradoresNotaCredito` → `"/notas-credito-venta?estado=preparacion&numero=NC"`.

`BusquedaApiTests.cs`: `Assert.StartsWith("/facturas-venta/en-preparacion/", borradorItem.Ruta);` y, si algún test asevera los títulos
de grupo, "Facturas en preparación" / "Notas de crédito en preparación".

`AccionesFacturasTests.cs`: `Borradores_ConNuevaFacturaYVerCliente` → `EnPreparacion_ConNuevaFacturaYNuevaNota` sobre
`/facturas-venta?estado=preparacion` y `/notas-credito-venta?estado=preparacion` (`>+ Nueva factura</a>`, `>+ Nueva nota de crédito</a>`);
`BorradoresFactura_Liberado_SeleccionableParaVer_SinEditarNiEliminar` → `EnPreparacion_Liberada_SinDescartar` (sin `deleteId={IdBorradorLiberado}`,
con "Continuar"); `BorradoresNota_SeleccionHabilitaVerCliente` → eliminar (la lista en preparación no tiene selección: "Continuar" abre
el documento y su barra tiene Ver ▾ Cliente).

`ConversionDocumentosUsuariosTests.cs`: `EditIdLegado_RedirigeALaRutaNueva` (filas de borradores) → destinos
`/facturas-venta/en-preparacion/{0}` y `/notas-credito-venta/en-preparacion/{0}` sin `returnUrl`; `Borradores_SoloAbiertosSeModificanOEliminan`
→ eliminar (lo cubre `Facturas_EnPreparacion_ClienteCreadoLineasTotal_ContinuarYDescartar`); `Listado_SinFormulariosEnLinea`: filas
`("/facturas-venta?estado=preparacion", "add-borrador")` y `("/notas-credito-venta?estado=preparacion", "update-borrador-nota")`.

`DocumentoFacturaBorradorTests.Listado_FiltroPosteada` y `DocumentoNotaBorradorTests.Listado_SeleccionPosteada_SinEditarNiEliminar` y la
parte de listado de `RutaEditar_Redirige_YListadoConFiltroPosteada`: eliminar (la lista de borradores Posteada ya no existe en la UI:
el documento registrado enlaza su preparación con "Ver preparación").

- [ ] **Step 2: Ejecutarlos (fallan)**

Run: `dotnet test tests/OpenSource1.SmokeTests --filter "FullyQualifiedName~ListasEnPreparacionTests|FullyQualifiedName~RegistroModulosTests"`
Expected: FAIL (sin pestañas; el catálogo aún tiene 4 entradas).

- [ ] **Step 3: Componentes de la lista**

`Components/Documento/PestanasDocumento.razor`:

```razor
@* Pestañas "Registradas" / "En preparación (n)" de las listas de facturas y notas (spec alta rápida, Parte 2): enlaces GET con
   aria-current; n es el Total del listado en preparación (sin número si no cargó). *@
<nav aria-label="Estado de los documentos" class="mb-6 border-b border-slate-200 dark:border-slate-700" data-testid="pestanas-documento">
    <ul class="-mb-px flex gap-4 text-sm font-semibold">
        <li><a href="@Base" aria-current="@(EnPreparacion ? null : "page")" class="@Clase(!EnPreparacion)" data-testid="pestana-registradas">Registradas</a></li>
        <li><a href="@($"{Base}?estado=preparacion")" aria-current="@(EnPreparacion ? "page" : null)" class="@Clase(EnPreparacion)" data-testid="pestana-preparacion">@TextoPreparacion</a></li>
    </ul>
</nav>

@code {
    /// <summary>Ruta de la lista (<c>/facturas-venta</c> o <c>/notas-credito-venta</c>).</summary>
    [Parameter, EditorRequired] public string Base { get; set; } = string.Empty;
    [Parameter] public bool EnPreparacion { get; set; }
    /// <summary>Documentos en preparación; null = no cargó (la pestaña se muestra sin número).</summary>
    [Parameter] public long? Conteo { get; set; }

    private string TextoPreparacion => Conteo is { } n ? $"En preparación ({n})" : "En preparación";

    private static string Clase(bool activa) => activa
        ? "inline-block border-b-2 border-brand-600 px-1 pb-2 text-brand-700"
        : "inline-block border-b-2 border-transparent px-1 pb-2 text-slate-500 hover:text-slate-700";
}
```

`Components/Documento/ListaFacturasEnPreparacion.razor`:

```razor
@using System.ComponentModel.DataAnnotations
@using OpenSource1.Application.Features.FacturasVenta.Borradores.Dtos
@using OpenSource1.Blazor.Services
@using OpenSource1.Core.Common
@using OpenSource1.Core.Enums
@inject IFacturaVentaApiClient FacturaApiClient
@inject ILogger<ListaFacturasEnPreparacion> Logger
@inject NavigationManager Nav

@* Pestaña "En preparación" de /facturas-venta (spec alta rápida, Parte 2): cliente, creado el, líneas, total (vista previa) y estado
   como insignia de texto; "Continuar" abre la factura y "Descartar" (CanDelete, solo las abiertas: una heredada Liberada se reabre
   antes en su página) pide confirmación. Los filtros, la página y deleteId llegan de la página como parámetros. El formulario de
   descarte está siempre en el árbol (un POST obsoleto o forzado recibe el mensaje real de la API). *@
@if (Ok is "descartada" or "eliminado")
{
    <div class="mb-6"><ActionFeedback Type="success" Message="Factura descartada." /></div>
}
else if (Ok == "guardada")
{
    <div class="mb-6"><ActionFeedback Type="success" Message="La factura quedó guardada en preparación." /></div>
}
<ResumenErrores Mensaje="@error" Errores="@errores" />

<div class="mb-6 overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm">
    <form method="get" action="/facturas-venta" data-enhance class="p-4 sm:p-6">
        <input type="hidden" name="estado" value="preparacion" />
        <div class="flex flex-col gap-4 lg:flex-row lg:items-end lg:justify-between">
            <div class="grid max-w-3xl flex-1 grid-cols-1 gap-3 sm:grid-cols-2">
                <div>
                    <label class="mb-1 block text-xs font-semibold text-slate-500" for="prepNombre">Cliente</label>
                    <input id="prepNombre" type="text" name="nombre" value="@Nombre" placeholder="ej. Comercial" class="@VentasCss.Filtro" />
                </div>
                <div>
                    <label class="mb-1 block text-xs font-semibold text-slate-500" for="prepNumero">Número interno</label>
                    <input id="prepNumero" type="text" name="numero" value="@Numero" class="@VentasCss.Filtro" />
                </div>
            </div>
            <div class="flex items-center gap-2">
                <button type="submit" class="btn-press rounded-lg bg-brand-600 px-4 py-2 text-sm font-semibold text-white shadow-sm hover:bg-brand-700">Consultar</button>
                <a href="@RutasPreparacion.ListaFacturas" class="btn-press rounded-lg border border-slate-300 bg-white px-4 py-2 text-sm font-semibold text-slate-600 shadow-sm hover:bg-slate-50">Limpiar campos</a>
            </div>
        </div>
    </form>
</div>

<div class="mb-8 overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm">
    <div class="border-b border-slate-100 px-6 py-4">
        <h2 class="text-sm font-semibold text-slate-700">Facturas en preparación</h2>
        <p class="mt-1 text-xs text-slate-500">@total resultado(s) · página @pagina de @Math.Max(totalPaginas, 1).</p>
    </div>
    <div class="overflow-x-auto">
        <table class="min-w-full divide-y divide-slate-100 text-sm" data-testid="tabla-en-preparacion">
            <thead class="bg-slate-50">
                <tr>
                    <th class="px-4 py-3 text-left text-xs font-semibold uppercase text-slate-500">Cliente</th>
                    <th class="px-4 py-3 text-left text-xs font-semibold uppercase text-slate-500">Creado el</th>
                    <th class="px-4 py-3 text-right text-xs font-semibold uppercase text-slate-500">Líneas</th>
                    <th class="px-4 py-3 text-right text-xs font-semibold uppercase text-slate-500">Total</th>
                    <th class="px-4 py-3 text-left text-xs font-semibold uppercase text-slate-500">Estado</th>
                    <th class="px-4 py-3 text-right text-xs font-semibold uppercase text-slate-500">Acciones</th>
                </tr>
            </thead>
            <tbody class="divide-y divide-slate-50 bg-white">
                @if (facturas.Count == 0)
                {
                    <tr><td colspan="6" class="px-4 py-8 text-center text-sm text-slate-400">No hay facturas en preparación con esos criterios.</td></tr>
                }
                @foreach (var b in facturas)
                {
                    <tr @key="b.Id" class="hover:bg-slate-50">
                        <td class="px-4 py-3 text-slate-700">@b.SocioNegocioCodigo <span class="text-xs text-slate-400">@b.NombreFacturacion</span></td>
                        <td class="px-4 py-3 text-xs text-slate-500">@EntradaFecha.Formatear(DateOnly.FromDateTime(b.CreatedAtUtc))</td>
                        <td class="px-4 py-3 text-right text-slate-700">@b.NumeroLineas</td>
                        <td class="px-4 py-3 text-right font-semibold text-slate-800">@VentasOpciones.Importe(b.ImporteTotal)</td>
                        <td class="px-4 py-3 text-xs"><span class="@EstadoCss(b.Estado)">@VentasOpciones.Estado(b.Estado)</span></td>
                        <td class="px-4 py-3 text-right">
                            <div class="flex items-center justify-end gap-2">
                                <a href="@($"{RutasPreparacion.Factura}/{b.Id}")" aria-label="@($"Continuar la factura de {b.NombreFacturacion}")" class="btn-press rounded-lg border border-brand-200 bg-brand-50 px-3 py-1 text-xs font-semibold text-brand-700 hover:bg-brand-100">Continuar</a>
                                @if (Permisos?.CanDelete == true && b.Estado == EstadoFacturaBorrador.Abierta)
                                {
                                    <a href="@Url(deleteId: b.Id)" aria-label="@($"Descartar la factura de {b.NombreFacturacion}")" class="btn-press rounded-lg border border-red-200 bg-red-50 px-3 py-1 text-xs font-semibold text-red-700 hover:bg-red-100">Descartar</a>
                                }
                            </div>
                        </td>
                    </tr>
                }
            </tbody>
        </table>
    </div>
    @if (totalPaginas > 1)
    {
        <div class="flex items-center justify-between gap-3 border-t border-slate-100 px-6 py-4">
            <p class="text-xs text-slate-500">Página @pagina de @totalPaginas</p>
            <div class="flex items-center gap-2">
                @if (pagina > 1)
                {
                    <a href="@Url(pagina: pagina - 1)" class="btn-press rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-xs font-semibold text-slate-600 hover:bg-slate-50">« Anterior</a>
                }
                @if (pagina < totalPaginas)
                {
                    <a href="@Url(pagina: pagina + 1)" class="btn-press rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-xs font-semibold text-slate-600 hover:bg-slate-50">Siguiente »</a>
                }
            </div>
        </div>
    }
</div>

@if (MuestraDialogo)
{
    <ConfirmDialog Title="Descartar factura" Message="Se descartará la factura en preparación con todas sus líneas. No se puede deshacer." Variant="danger" CancelHref="@Url()">
        <p class="rounded-xl border border-red-100 bg-red-50 px-4 py-3 text-sm text-red-900">@(aDescartar is null ? DeleteId?.ToString() : $"{aDescartar.NombreFacturacion} ({aDescartar.NumeroLineas} línea(s))")</p>
        <EditForm Model="DeleteInput" FormName="delete-borrador" OnValidSubmit="DescartarAsync" class="mt-4">
            <input type="hidden" name="DeleteInput.Id" value="@DeleteId" />
            <button type="submit" class="btn-press rounded-lg bg-red-600 px-4 py-2 text-sm font-semibold text-white shadow-sm hover:bg-red-700">Sí, descartar</button>
            <a href="@Url()" class="btn-press ml-2 rounded-lg border border-slate-300 bg-white px-4 py-2 text-sm font-semibold text-slate-600 shadow-sm hover:bg-slate-50">Cancelar</a>
        </EditForm>
    </ConfirmDialog>
}
else
{
    <EditForm Model="DeleteInput" FormName="delete-borrador" OnValidSubmit="DescartarAsync"></EditForm>
}

@code {
    [Parameter] public string? Numero { get; set; }
    [Parameter] public string? Nombre { get; set; }
    [Parameter] public int Pagina { get; set; } = 1;
    [Parameter] public Guid? DeleteId { get; set; }
    [Parameter] public string? Ok { get; set; }
    [Parameter] public PermisosPagina? Permisos { get; set; }

    [SupplyParameterFromForm(FormName = "delete-borrador")] private DescartarForm? DeleteInput { get; set; }

    private IReadOnlyList<FacturaVentaBorradorResponse> facturas = [];
    private FacturaVentaBorradorResponse? aDescartar;
    private long total;
    private int totalPaginas;
    private int pagina = 1;
    private string? error;
    private IReadOnlyList<string> errores = [];

    private bool MuestraDialogo => DeleteId.HasValue && Permisos?.CanDelete == true;

    protected override async Task OnInitializedAsync()
    {
        DeleteInput ??= new();
        pagina = Math.Max(Pagina, 1);
        try
        {
            var filtro = new FacturaVentaBorradorFiltro(Vacio(Numero), Vacio(Nombre), null, null);
            // Orden explícito: lo último creado primero.
            var resultado = await FacturaApiClient.ListBorradoresAsync(filtro, new PageRequest(pagina, PageRequest.TamanoPorDefecto, "CreatedAtUtc", Descendente: true));
            if (resultado.TotalPaginas > 0 && pagina > resultado.TotalPaginas)
            {
                pagina = resultado.TotalPaginas;
                resultado = await FacturaApiClient.ListBorradoresAsync(filtro, new PageRequest(pagina, PageRequest.TamanoPorDefecto, "CreatedAtUtc", Descendente: true));
            }

            (facturas, total, totalPaginas) = (resultado.Items, resultado.Total, resultado.TotalPaginas);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not load facturas en preparación.");
            error = "No fue posible cargar las facturas en preparación.";
        }

        if (MuestraDialogo)
        {
            aDescartar = facturas.FirstOrDefault(f => f.Id == DeleteId);
            if (aDescartar is null)
            {
                try { aDescartar = await FacturaApiClient.GetBorradorAsync(DeleteId!.Value); }
                catch (Exception ex) { Logger.LogWarning(ex, "Could not load factura en preparación {Id} to discard.", DeleteId); }
            }
        }
    }

    private async Task DescartarAsync()
    {
        // R17: la navegación va FUERA del try.
        string? destino = null;
        try
        {
            var result = await FacturaApiClient.DeleteBorradorAsync(DeleteInput!.Id);
            if (result.Succeeded)
            {
                destino = RetornoLocal.ConParametro(Url(), "ok", "descartada");
            }
            else
            {
                (error, errores) = (result.Message, result.Errors ?? []);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not discard factura en preparación.");
            error = "No fue posible descartar la factura.";
        }

        if (destino is not null)
        {
            Nav.NavigateTo(destino);
        }
    }

    private string Url(int? pagina = null, Guid? deleteId = null)
    {
        var url = RutasPreparacion.ListaFacturas;
        if (!string.IsNullOrWhiteSpace(Numero)) url = RetornoLocal.ConParametro(url, "numero", Numero.Trim());
        if (!string.IsNullOrWhiteSpace(Nombre)) url = RetornoLocal.ConParametro(url, "nombre", Nombre.Trim());
        var paginaEfectiva = pagina ?? this.pagina;
        if (paginaEfectiva > 1) url = RetornoLocal.ConParametro(url, "pagina", paginaEfectiva.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (deleteId is { } id) url = RetornoLocal.ConParametro(url, "deleteId", id.ToString());
        return url;
    }

    private static string EstadoCss(EstadoFacturaBorrador estado) => estado == EstadoFacturaBorrador.Liberada
        ? "inline-flex rounded-full bg-amber-50 px-2 py-0.5 font-semibold text-amber-700"
        : "inline-flex rounded-full bg-green-50 px-2 py-0.5 font-semibold text-green-700";

    private static string? Vacio(string? valor) => string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();

    public sealed class DescartarForm
    {
        public Guid Id { get; set; }
    }
}
```

`Components/Documento/ListaNotasEnPreparacion.razor`: el mismo componente para notas, con estas diferencias exactas —
`@inject INotaCreditoVentaApiClient NotaApiClient`; acción del formulario `"/notas-credito-venta"` y `Limpiar campos` a
`RutasPreparacion.ListaNotas`; filtros `nombre` (Cliente) y `factura` (Factura de origen, parámetro `Factura`) en lugar de `numero`;
título "Notas de crédito en preparación"; columnas Cliente, Factura de origen (`b.FacturaVentaNumero`), Creado el, Líneas, Total,
Estado (`VentasOpciones.EstadoNota`), Acciones; "Continuar" a `$"{RutasPreparacion.Nota}/{b.Id}"` y "Descartar" (CanDelete; toda nota
en preparación está Abierta) con `FormName="delete-borrador-nota"` y el diálogo "Descartar nota de crédito"; filtro hacia la API
`new NotaCreditoVentaBorradorFiltro(null, Vacio(Factura), Vacio(Nombre), null)`; mensajes "Nota de crédito descartada." /
"La nota de crédito quedó guardada en preparación."; `Url()` sobre `RutasPreparacion.ListaNotas`.

- [ ] **Step 4: Listas con pestañas**

`FacturasVenta.razor` (modo listado, `Numero is null`):
- Parámetros nuevos: `[SupplyParameterFromQuery(Name = "estado")] private string? Estado { get; set; }`,
  `[SupplyParameterFromQuery(Name = "deleteId")] private Guid? DeleteId { get; set; }` y
  `private bool EnPreparacion => Numero is null && string.Equals(Estado, "preparacion", StringComparison.OrdinalIgnoreCase);`
  más `private long? conteoEnPreparacion;`.
- Barra del listado: `Subtitulo="Facturas de venta registradas y en preparación."`, se borra el botón "Borradores"; `NuevoTexto="Nueva factura"` se conserva (se pinta "+ Nueva factura").
- Bajo la barra del listado: `<PestanasDocumento Base="/facturas-venta" EnPreparacion="@EnPreparacion" Conteo="@conteoEnPreparacion" />`.
- `@if (EnPreparacion) { <ListaFacturasEnPreparacion Numero="@SearchNumero" Nombre="@SearchNombre" Pagina="@Pagina" DeleteId="@DeleteId" Ok="@Ok" Permisos="@permisos" /> } else { …filtros y tabla actuales… }`;
  el título de la tabla "Facturas posteadas" pasa a "Facturas registradas".
- `OnParametersSetAsync`, rama `Numero is null`:

```csharp
        if (Numero is null)
        {
            try
            {
                conteoEnPreparacion = await FacturaApiClient.ContarEnPreparacionAsync();
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Could not count facturas en preparación.");
                conteoEnPreparacion = null;
            }

            if (!EnPreparacion)
            {
                await CargarListadoAsync();
            }

            return;
        }
```

`NotasCreditoVenta.razor` (modo listado): mismos cambios —parámetros `estado`/`deleteId`, `conteoEnPreparacion` con
`NotaApiClient.ContarEnPreparacionAsync()`, `<PestanasDocumento Base="/notas-credito-venta" …/>`, `<ListaNotasEnPreparacion Factura="@SearchFactura" Nombre="@SearchNombre" Pagina="@Pagina" DeleteId="@DeleteId" Ok="@Ok" Permisos="@permisos" />`
en `?estado=preparacion`—; en la cabecera de la página se borra el enlace "Borradores" y, con `CanAdd`, se añade
`<a href="/notas-credito-venta/nueva" data-testid="accion-nuevo" class="btn-press inline-flex items-center gap-1.5 rounded-lg bg-brand-600 px-4 py-2 text-sm font-semibold text-white shadow-sm hover:bg-brand-700">+ Nueva nota de crédito</a>`
(cargar `permisos = await PermisosPagina.CargarAsync(AuthorizationService, AuthenticationStateTask)` si la página aún no lo hace, con
`@inject IAuthorizationService AuthorizationService` y el `[CascadingParameter]`); el subtítulo del listado: "Notas de crédito de venta
registradas y en preparación."

- [ ] **Step 5: Listas antiguas → redirección**

`FacturasVentaBorradores.razor`: se sustituyen el marcado y el código de la lista (filtros, tabla, diálogo, `LoadAsync`, `DeleteAsync`,
`BuildListUrl`, `EstadoCss`, `DeleteForm`) por la redirección; la clase `BorradorForm` (R6) se queda tal cual al final del `@code`:

```razor
@page "/facturas-venta/borradores"
@attribute [Authorize(Policy = ApplicationPolicies.CanConsult)]
@using System.ComponentModel.DataAnnotations
@using Microsoft.AspNetCore.Authorization
@inject NavigationManager Nav

@* Spec alta rápida: la lista de borradores es ahora la pestaña "En preparación" de /facturas-venta. La ruta se conserva para los
   enlaces guardados: ?editId= abre la factura en preparación; ?estado=3 (borradores ya posteados) lleva a las registradas; los
   filtros numero/nombre se conservan. Aloja también BorradorForm, el modelo de la cabecera editable de la factura en preparación. *@

@code {
    [SupplyParameterFromQuery(Name = "numero")] private string? SearchNumero { get; set; }
    [SupplyParameterFromQuery(Name = "nombre")] private string? SearchNombre { get; set; }
    [SupplyParameterFromQuery(Name = "estado")] private int? SearchEstado { get; set; }
    [SupplyParameterFromQuery(Name = "editId")] private Guid? EditId { get; set; }

    protected override void OnInitialized()
    {
        if (EditId is { } id)
        {
            Nav.NavigateTo($"{RutasPreparacion.Factura}/{id}", replace: true);
            return;
        }

        var destino = SearchEstado == 3 ? "/facturas-venta" : RutasPreparacion.ListaFacturas;
        if (!string.IsNullOrWhiteSpace(SearchNumero)) destino = RetornoLocal.ConParametro(destino, "numero", SearchNumero.Trim());
        if (!string.IsNullOrWhiteSpace(SearchNombre)) destino = RetornoLocal.ConParametro(destino, "nombre", SearchNombre.Trim());
        Nav.NavigateTo(destino, replace: true);
    }

    // (aquí sigue, sin cambios, public sealed class BorradorForm : IValidatableObject { … } de R6)
}
```

`NotasCreditoVentaBorradores.razor`: lo mismo con `RutasPreparacion.Nota`/`ListaNotas`, `/notas-credito-venta` para `estado=3` y los
filtros `numero` (se ignora: el número interno no es un filtro de la pestaña), `factura` y `nombre`; la clase `CabeceraNotaForm` (R7) se conserva.

- [ ] **Step 6: Catálogo, indicadores y búsqueda**

`CatalogoModulos.cs`:
- Grupo: `new("facturacion", "Facturación", IconosModulo.Documento, "Facturas y notas de crédito, registradas y en preparación.", 5)`.
- Módulos de Facturación (se borran `borradores-factura` y `borradores-nota-credito`):

```csharp
        new("facturas", "facturacion", "Facturas", "Facturas de venta registradas y en preparación.", "/facturas-venta", IconosModulo.Documento, Consultar, null,
            ["factura", "venta", "fv", "borrador", "en preparación", "en preparacion"]),
        new("notas-credito", "facturacion", "Notas de crédito", "Notas de crédito registradas y en preparación.", "/notas-credito-venta", IconosModulo.Documento, Consultar, null,
            ["nc", "devolucion", "nota de credito", "borrador", "en preparación", "en preparacion"]),
```

- `RutasAsociadas`: diccionario vacío (`new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)`), con el comentario: "Spec alta
  rápida: las altas y las páginas en preparación cuelgan de la ruta de su documento (/facturas-venta/…, /notas-credito-venta/…) y el
  prefijo ya las marca; se conserva para futuras rutas que no cuelguen de su listado."

`IndicadoresModulos.cs`, grupo `"facturacion"`:

```csharp
            ("Facturas en preparación", RutasPreparacion.ListaFacturas, async ct => (await facturas.ListBorradoresAsync(null, Uno, ct)).Total),
            ("Facturas registradas", "/facturas-venta", async ct => (await facturas.ListFacturasAsync(null, Uno, ct)).Total),
            ("Notas de crédito en preparación", RutasPreparacion.ListaNotas, async ct => (await notas.ListBorradoresAsync(null, Uno, ct)).Total),
```

(`using OpenSource1.Blazor.Components.Documento;` si el archivo no lo tiene.)

`Buscar.razor`, `VerTodosUrl`:

```csharp
            TiposResultadoBusqueda.BorradoresFactura => $"{RutasPreparacion.ListaFacturas}&{CampoFactura(q)}={valor}",
            TiposResultadoBusqueda.BorradoresNotaCredito => $"{RutasPreparacion.ListaNotas}&numero={valor}",
```

(y, si la página rotula los grupos con textos propios, "Facturas en preparación" / "Notas de crédito en preparación").

`DapperBusquedaGlobalRepository.cs`:

```csharp
            await GrupoAsync(TiposResultadoBusqueda.BorradoresFactura, "Facturas en preparación", SqlBorradoresFactura, id => $"/facturas-venta/en-preparacion/{id}"),
            await GrupoAsync(TiposResultadoBusqueda.BorradoresNotaCredito, "Notas de crédito en preparación", SqlBorradoresNotaCredito, id => $"/notas-credito-venta/en-preparacion/{id}"),
```

- [ ] **Step 7: Verde**

Run:
```bash
dotnet test tests/OpenSource1.SmokeTests --filter "FullyQualifiedName~ListasEnPreparacionTests|FullyQualifiedName~RegistroModulosTests|FullyQualifiedName~NavMenuTests|FullyQualifiedName~InicioYGruposTests|FullyQualifiedName~BuscarPaginaTests|FullyQualifiedName~PaletaBusquedaTests|FullyQualifiedName~AccionesFacturasTests|FullyQualifiedName~ConversionDocumentosUsuariosTests|FullyQualifiedName~DocumentoFacturaBorradorTests|FullyQualifiedName~DocumentoNotaBorradorTests|FullyQualifiedName~DocumentosPosteadosTests|FullyQualifiedName~AltaRapida"
flock /home/ray/test/.superpowers/sdd/alta-rapida/tmp/postgres-tests.lock env DOCKER_CONTEXT=default dotnet test tests/OpenSource1.SmokeTests --filter "FullyQualifiedName~BusquedaApiTests"
```
Expected: PASS.

- [ ] **Step 8: Textos antiguos fuera de la UI de Facturación**

Run: `grep -rnE '>Postear<|Sí, postear|Borradores de factura|Borradores de nota|Nuevo borrador|Crear borrador|>Liberar<' src/OpenSource1.Blazor --include='*.razor' --include='*.cs'`
Expected: sin resultados (los comentarios `@* … *@` que mencionen "borrador" como nombre interno se admiten; los textos visibles no).

- [ ] **Step 9: Commit**

```bash
git add src/OpenSource1.Blazor/Components/Documento/PestanasDocumento.razor src/OpenSource1.Blazor/Components/Documento/ListaFacturasEnPreparacion.razor \
  src/OpenSource1.Blazor/Components/Documento/ListaNotasEnPreparacion.razor src/OpenSource1.Blazor/Components/Pages/FacturasVenta.razor \
  src/OpenSource1.Blazor/Components/Pages/NotasCreditoVenta.razor src/OpenSource1.Blazor/Components/Pages/FacturasVentaBorradores.razor \
  src/OpenSource1.Blazor/Components/Pages/NotasCreditoVentaBorradores.razor src/OpenSource1.Blazor/Navigation/CatalogoModulos.cs \
  src/OpenSource1.Blazor/Navigation/IndicadoresModulos.cs src/OpenSource1.Blazor/Components/Pages/Buscar.razor \
  src/OpenSource1.Infrastructure/Data/Queries/DapperBusquedaGlobalRepository.cs tests/OpenSource1.SmokeTests/Blazor/ListasEnPreparacionTests.cs \
  tests/OpenSource1.SmokeTests/Blazor/RegistroModulosTests.cs tests/OpenSource1.SmokeTests/Blazor/NavMenuTests.cs \
  tests/OpenSource1.SmokeTests/Blazor/InicioYGruposTests.cs tests/OpenSource1.SmokeTests/Blazor/BuscarPaginaTests.cs \
  tests/OpenSource1.SmokeTests/Blazor/AccionesFacturasTests.cs tests/OpenSource1.SmokeTests/Blazor/ConversionDocumentosUsuariosTests.cs \
  tests/OpenSource1.SmokeTests/Blazor/DocumentoFacturaBorradorTests.cs tests/OpenSource1.SmokeTests/Blazor/DocumentoNotaBorradorTests.cs \
  tests/OpenSource1.SmokeTests/Api/BusquedaApiTests.cs
git commit -m "feat: facturacion con dos entradas y pestanas registradas y en preparacion"
```

---

### Task G2: Puerta de la Fase 2 (suite completa y runtime)

**Grupo de paralelismo:** serie (tras R8).

**Files:** ninguno nuevo (arreglos en el archivo de la task responsable).

- [ ] **Step 1: Build y suite completa**

Run:
```bash
dotnet build test.slnx -warnaserror
flock /home/ray/test/.superpowers/sdd/alta-rapida/tmp/postgres-tests.lock env DOCKER_CONTEXT=default dotnet test test.slnx
```
Expected: 0 avisos; suite verde (anotar el total).

- [ ] **Step 2: Restricciones globales**

Run:
```bash
git diff f79a44a...HEAD -G'@rendermode|@onclick|@bind' --stat -- 'src/**/*.razor'
git diff --stat f79a44a...HEAD -- '*appsettings*.json' 'src/OpenSource1.Infrastructure/Data/Migrations' 'src/OpenSource1.Infrastructure/Data/ApplicationDbContext.cs'
grep -rn "document.addEventListener('enhancedload'" src/OpenSource1.Blazor/wwwroot/app.errores.js || echo "sin document.enhancedload"
dotnet ef migrations has-pending-model-changes -p src/OpenSource1.Infrastructure -s src/OpenSource1.Api --context ApplicationDbContext
```
Expected: sin interactividad añadida; ni `appsettings*.json`, ni migraciones, ni `ApplicationDbContext.cs` cambiados; "sin document.enhancedload"; "No changes have been made to the model since the last migration."

- [ ] **Step 3: Runtime sobre el stack Docker de ESTE proyecto**

Solo se reconstruyen y reinician los servicios de este `docker compose` (`api` y `blazor`); nunca se tocan otros contenedores ni se
cambia el contexto de Docker (variable de entorno, no `docker context use`):

```bash
env DOCKER_CONTEXT=default docker compose up -d --build api blazor
env DOCKER_CONTEXT=default docker compose ps
```

Comprobar en `http://localhost:8080` como `admin` y como `supervisor` (contraseña: la de `AUTH_SEED_DEFAULT_PASSWORD` en `.env`; no copiarla en ningún archivo):
- Menú Facturación con 2 entradas (Facturas, Notas de crédito); la paleta Ctrl+K encuentra "borrador" y "en preparación".
- Facturas → pestañas "Registradas" / "En preparación (n)"; "+ Nueva factura" → buscar cliente → Usar → datos en solo lectura y "Se numerará con la serie FV · próximo …" → tipo "Comentario" → "Agregar línea" abre `/facturas-venta/en-preparacion/{id}`; el número de borrador no se ve.
- En la factura en preparación: "Guardar para después" vuelve a la pestaña con el contador +1; "Registrar factura" abre la confirmación con total, líneas, serie y próximo número; "Seguir editando" vuelve; "Descartar factura" la elimina (no registrar en este paso). El Administrador ve "Más opciones → Cambiar serie de registro"; el supervisor no.
- `/facturas-venta/borradores` y `/facturas-venta/borradores/{id}` redirigen.
- Con el teclado: tras un error de validación (precio vacío), el foco queda en el resumen de errores.
Expected: todo OK; descartar al terminar los documentos de prueba creados.

- [ ] **Step 4: Sin commit si no hubo arreglos** (si los hubo: `git commit -m "fix: puerta de la fase 2 del alta rapida"` con las rutas explícitas).

---
## Fase 3 — Cierre

### Task R9: E2E — documento único actualizado y alta rápida (factura de 2 líneas y nota total, contando cargas)

**Grupo de paralelismo:** serie (tras G2). Contra el stack de Docker de este proyecto.

**Files:**
- Modify: `tests/e2e/specs/09-documento-unico.spec.ts`
- Create: `tests/e2e/specs/10-alta-rapida.spec.ts`

**Interfaces:**
- Consumes: `iniciarSesion`, `unico`, `usuarios` de `tests/e2e/specs/ayuda.ts`; `data-testid` de R5–R8 (`buscar-socio`, `clientes-encontrados`, `cliente-elegido`, `datos-derivados`, `serie-registro`, `tipos-linea`, `agregar-linea`, `fila-nueva`, `fila-edicion`, `tabla-lineas`, `total-sin-iva`, `registrar`, `si-registrar`, `guardar-cabecera`, `descartar`, `crear-nota`, `pestana-preparacion`, `tabla-en-preparacion`).
- Produces: dos specs que `npx playwright test` ejecuta. **10-alta-rapida registra una factura y una nota de crédito**: los documentos registrados no se pueden borrar y quedan en la base de desarrollo (cliente `E2E Alta …`); se anota en "Resultado" (R10).

- [ ] **Step 1: Actualizar 09 (no registra nada)**

En `tests/e2e/specs/09-documento-unico.spec.ts`, sustituir el bloque desde `// Alta con los selectores de serie` hasta la comprobación de
`/editar` por:

```ts
    // Alta rápida: cliente por GET + "Usar", datos derivados en solo lectura y la primera línea (producto) en un solo envío.
    await page.goto(`/facturas-venta/nueva?socioQuery=${encodeURIComponent(nombreCliente)}`);
    await page.getByTestId('clientes-encontrados').locator('li', { hasText: nombreCliente }).getByRole('link', { name: /Usar/ }).click();
    await expect(page.getByTestId('cliente-elegido')).toBeVisible();
    await expect(page.getByTestId('datos-derivados')).toContainText('Moneda');
    await expect(page.getByTestId('serie-registro')).toContainText('Se numerará con la serie');
    await page.locator('[name="addProductoQuery"]').fill(codigo);
    await page.getByRole('button', { name: 'Buscar' }).click();
    await expect(page).toHaveURL(/addProductoQuery=/);
    await page.locator('li', { hasText: codigo }).getByRole('link', { name: 'Usar' }).click();
    await expect(page).toHaveURL(/addProductoId=/);
    await page.locator('[name="AltaInput.CantidadTexto"]').fill('2');
    await page.getByTestId('agregar-linea').click();
    await expect(page).toHaveURL(/\/facturas-venta\/en-preparacion\/[0-9a-f-]+.*ok=linea_creada/);
    borradorUrl = new URL(page.url()).pathname;

    // Una sola página: datos derivados, numeración, tabla con la línea y fila nueva, totales; sin número de borrador.
    await expect(page.getByTestId('cabecera-editable')).toBeVisible();
    await expect(page.getByTestId('tabla-lineas')).toContainText(codigo);
    await expect(page.getByTestId('fila-nueva')).toBeVisible();
    // 2 × 100 sin descuento (el ITBIS depende del grupo de IVA por defecto del producto: no se asevera el total).
    await expect(page.getByTestId('total-sin-iva')).toHaveText('200.00');

    // Barra del documento: Ver ▾ lleva al cliente.
    await page.getByTestId('menu-ver').locator('summary').click();
    await expect(page.getByTestId('menu-ver').getByRole('link', { name: 'Cliente', exact: true })).toBeVisible();
    await page.getByTestId('menu-ver').locator('summary').click();

    // Guardar cambios vuelve a la misma página.
    await page.locator('[name="UpdateInput.Descripcion"]').fill(`E2E ${codigo}`);
    await page.getByTestId('guardar-cabecera').click();
    await expect(page).toHaveURL(new RegExp(`${borradorUrl}\\?ok=modificado`));
    await expect(page.locator('[name="UpdateInput.Descripcion"]')).toHaveValue(`E2E ${codigo}`);

    // Editar la línea en su fila (nombre accesible "Modificar línea 1").
    await page.getByRole('link', { name: 'Modificar línea 1' }).click();
    await expect(page.getByTestId('fila-edicion')).toBeVisible();
    await page.getByTestId('fila-edicion').locator('[name="UpdateInputLinea.CantidadTexto"]').fill('3');
    await page.getByTestId('fila-edicion').getByRole('button', { name: 'Guardar cambios' }).click();
    await expect(page).toHaveURL(/ok=linea_modificada/);
    await expect(page.getByTestId('total-sin-iva')).toHaveText('300.00');

    // Las rutas antiguas redirigen a la factura en preparación.
    const id = borradorUrl.split('/').pop();
    await page.goto(`/facturas-venta/borradores/${id}`);
    await expect(page).toHaveURL(new RegExp(`${borradorUrl}$`));
    await page.goto(`/facturas-venta/borradores/${id}/editar`);
    await expect(page).toHaveURL(new RegExp(`${borradorUrl}$`));
```

y en el `finally`, el descarte:

```ts
    if (borradorUrl) {
      await intentar('factura en preparación', async () => {
        await page.goto(`${borradorUrl}?eliminar=true`);
        await page.getByRole('button', { name: 'Sí, descartar' }).click();
        await expect(page).toHaveURL(/ok=descartada/);
      });
    }
```

El título del test pasa a `'factura en preparación en una sola página: alta rápida, líneas, totales y rutas antiguas'` y el comentario
inicial a "Spec alta rápida: …; la suite no registra documentos en este spec".

- [ ] **Step 2: Crear 10-alta-rapida**

`tests/e2e/specs/10-alta-rapida.spec.ts`:

```ts
import { expect, test, type Page } from '@playwright/test';
import { iniciarSesion, unico, usuarios } from './ayuda';

// Spec alta rápida: criterio de éxito. Una factura de 2 líneas se prepara y se registra en pocas cargas de página y su nota de crédito
// total sale desde la factura en 2 clics más la confirmación. Las líneas son de cuenta contable (4101 Ventas, ITBIS18): no mueven
// inventario y no necesitan existencias. ESTE SPEC REGISTRA DOCUMENTOS: la factura y la nota quedan en la base de desarrollo (cliente
// "E2E Alta …"); no se pueden borrar.

/**
 * Cuenta las cargas de página del recorrido: cada acción del usuario que pide una página al servidor (enlace, formulario GET o POST
 * con redirección) se ejecuta con cargar(); se espera a la URL de destino si se indica. Contar acciones (no peticiones HTTP) evita
 * depender de cómo la navegación mejorada de Blazor encadena el POST y el GET de la redirección.
 */
function contadorDeCargas(page: Page) {
  let cargas = 0;
  return {
    get total(): number {
      return cargas;
    },
    async cargar(accion: () => Promise<unknown>, url?: RegExp): Promise<void> {
      await accion();
      if (url) {
        await expect(page).toHaveURL(url);
      }
      await page.waitForLoadState('domcontentloaded');
      cargas += 1;
    },
  };
}

async function rellenarLineaDeCuenta(page: Page, importe: string): Promise<void> {
  await page.locator('[name$=".CuentaContableId"]').selectOption({ label: '4101 — Ventas' });
  await page.locator('[name$=".GrupoIvaProductoId"]').selectOption({ label: 'ITBIS18 — Gravado con ITBIS 18%' });
  await page.locator('[name$=".CantidadTexto"]').fill('1');
  await page.locator('[name$=".PrecioUnitarioTexto"]').fill(importe);
}

test('alta rápida: factura de 2 líneas registrada y nota de crédito total desde la factura', async ({ page }) => {
  await iniciarSesion(page, usuarios.admin);

  const codigo = unico('E2E');
  const nombreCliente = `E2E Alta ${codigo}`;
  await page.goto('/clientes/nuevo');
  await page.locator('[name="Input.NombreComercial"]').fill(nombreCliente);
  await page.locator('[name="Input.Email"]').fill(`${codigo.toLowerCase()}@e2e.local`);
  await page.getByTestId('guardar').click();
  await expect(page).toHaveURL(/\/clientes\?.*ok=created/);

  // ---- Factura de 2 líneas: desde la lista hasta "Factura … registrada." ----
  await page.goto('/facturas-venta');
  const factura = contadorDeCargas(page);
  await factura.cargar(() => page.getByRole('link', { name: '+ Nueva factura' }).click(), /\/facturas-venta\/nueva/);
  await page.locator('[name="socioQuery"]').fill(nombreCliente);
  await factura.cargar(() => page.getByRole('button', { name: 'Buscar' }).click(), /socioQuery=/);
  await factura.cargar(() => page.getByRole('link', { name: `Usar ${nombreCliente}` }).click(), /socioId=/);
  await expect(page.getByTestId('datos-derivados')).toBeVisible();
  await factura.cargar(() => page.getByTestId('tipos-linea').getByRole('link', { name: 'Cuenta contable' }).click(), /tipo=2/);
  await rellenarLineaDeCuenta(page, '100');
  await factura.cargar(() => page.getByTestId('agregar-linea').click(), /\/facturas-venta\/en-preparacion\/[0-9a-f-]+\?tipo=2&ok=linea_creada/);
  await rellenarLineaDeCuenta(page, '50');
  await factura.cargar(() => page.getByTestId('fila-nueva').getByRole('button', { name: 'Agregar línea' }).click());
  await expect(page.getByTestId('tabla-lineas').locator('tbody tr[id^="linea-"]')).toHaveCount(2);
  await expect(page.getByTestId('total-sin-iva')).toHaveText('150.00');
  await factura.cargar(() => page.getByTestId('registrar').click(), /registrar=true/);
  await expect(page.getByTestId('resumen-registro')).toContainText('Total');
  await factura.cargar(() => page.getByTestId('si-registrar').click(), /\/facturas-venta\/[^/?]+\?ok=registrada/);
  await expect(page.getByText(/Factura .+ registrada\./)).toBeVisible();
  console.log(`Cargas de página de la factura de 2 líneas: ${factura.total}`);
  // Criterio del spec: de ~12 a ~7 (aquí 8: incluye buscar el cliente y elegir el tipo de línea "Cuenta contable").
  expect(factura.total).toBeLessThanOrEqual(8);
  const urlFactura = new URL(page.url()).pathname;

  // ---- Nota de crédito total: 2 clics más la confirmación ----
  const nota = contadorDeCargas(page);
  await nota.cargar(() => page.getByTestId('crear-nota').click(), /\/notas-credito-venta\/en-preparacion\/[0-9a-f-]+\?ok=creado/);
  await expect(page.getByTestId('tabla-lineas').locator('tbody tr[id^="linea-"]')).toHaveCount(2);
  await nota.cargar(() => page.getByTestId('registrar').click(), /registrar=true/);
  await nota.cargar(() => page.getByTestId('si-registrar').click(), /\/notas-credito-venta\/[^/?]+\?ok=registrada/);
  console.log(`Cargas de página de la nota total: ${nota.total}`);
  expect(nota.total).toBeLessThanOrEqual(3);

  // La factura ya no tiene pendiente: sin "Crear nota de crédito".
  await page.goto(urlFactura);
  await expect(page.getByTestId('crear-nota')).toHaveCount(0);
});

test('las listas muestran el contador "En preparación (n)" y cargan', async ({ page }) => {
  await iniciarSesion(page, usuarios.admin);

  for (const ruta of ['/facturas-venta', '/notas-credito-venta']) {
    await page.goto(ruta);
    await expect(page.getByTestId('pestana-preparacion')).toHaveText(/^En preparación( \(\d+\))?$/);
    await page.getByTestId('pestana-preparacion').click();
    await expect(page).toHaveURL(/estado=preparacion/);
    await expect(page.getByTestId('tabla-en-preparacion')).toBeVisible();
  }
});
```

(`GrupoOpcion.Rotulo` es "Código — Descripción" y las cuentas se rotulan "Número — Nombre": comprobar los rótulos reales de la semilla
— `ITBIS18`/"Gravado con ITBIS 18%" y `4101`/"Ventas"— y ajustar los `label` si difieren.)

- [ ] **Step 3: Ejecutar contra el stack reconstruido**

Run:
```bash
env DOCKER_CONTEXT=default docker compose up -d --build api blazor
cd tests/e2e && bash scripts/con-credenciales.sh npx playwright test specs/09-documento-unico.spec.ts specs/10-alta-rapida.spec.ts
```
Expected: 3 passed (anotar las cargas impresas por el spec 10).

- [ ] **Step 4: Suite E2E completa**

Run: `cd tests/e2e && bash scripts/con-credenciales.sh npx playwright test`
Expected: todo en verde salvo `@api-caida` (skipped). Los specs 06 y 07 siguen verdes (si alguno usaba `/facturas-venta/borradores` o
"Crear borrador", actualizarlo con las rutas y textos nuevos en este mismo step).

- [ ] **Step 5: Restaurar las evidencias regeneradas**

Run:
```bash
git status --short docs/entregable-parte-1
git restore docs/entregable-parte-1
```
Expected: el reporte HTML y las capturas versionadas vuelven a su estado (no se commitean evidencias regeneradas por esta ejecución).

- [ ] **Step 6: Commit**

```bash
git add tests/e2e/specs/09-documento-unico.spec.ts tests/e2e/specs/10-alta-rapida.spec.ts
git commit -m "test: e2e del alta rapida de factura y nota de credito"
```

---

### Task R10: Cierre de la rama `feat/alta-rapida-documentos`

**Grupo de paralelismo:** serie (último).

**Files:**
- Modify: `README.md` (secciones "Módulos" y "Changelog": "Versión 0.6 — Alta rápida de facturas y notas de crédito")
- Modify: `docs/superpowers/plans/2026-10-01-alta-rapida-documentos.md` (sección final "Resultado de feat/alta-rapida-documentos")
- Modify: `docs/superpowers/specs/2026-10-01-alta-rapida-documentos-design.md` (sección final "Resultado")

- [ ] **Step 1: README limpio**

Run: `git status --short README.md`
Expected: sin salida (el README se commiteó antes de R1). Si aparece un cambio que no es de esta rama, PARAR y avisar.

- [ ] **Step 2: Comprobaciones finales**

Run:
```bash
dotnet build test.slnx -warnaserror
flock /home/ray/test/.superpowers/sdd/alta-rapida/tmp/postgres-tests.lock env DOCKER_CONTEXT=default dotnet test test.slnx
dotnet ef migrations has-pending-model-changes -p src/OpenSource1.Infrastructure -s src/OpenSource1.Api --context ApplicationDbContext
git diff f79a44a...HEAD -G'@rendermode|@onclick|@bind' --stat -- 'src/**/*.razor'
git diff --stat f79a44a...HEAD -- '*appsettings*.json' src/OpenSource1.Infrastructure/Data/Migrations src/OpenSource1.Infrastructure/Data/ApplicationDbContext.cs
git diff --stat f79a44a...HEAD -- src/OpenSource1.Application/Features/FacturasVenta/Posteo src/OpenSource1.Application/Features/NotasCreditoVenta/Posteo src/OpenSource1.Infrastructure/Data/GeneradorNumeroDocumento.cs
```
Expected: 0 avisos; suite verde (anotar el total frente a la partida); sin cambios de modelo; sin interactividad añadida; sin cambios en
`appsettings*.json`, migraciones, `ApplicationDbContext.cs`, motores de posteo ni generador de números.

- [ ] **Step 3: Documentación**

`README.md`:
- Tabla "Módulos": la fila de Facturación pasa a `| Facturación | Facturas y notas de crédito (registradas y en preparación) |`.
- Lista de funciones: "Facturación de ventas con alta rápida (cliente, datos derivados y primera línea en una página), documentos en preparación que se guardan solos, registro con confirmación, notas de crédito desde la factura y cobros."
- Changelog, antes de "Versión 0.5":

```markdown
### Versión 0.6 — Alta rápida de facturas y notas de crédito

Alta rápida de facturas y notas de crédito (rama `feat/alta-rapida-documentos`).

Incluye:

- Factura en una sola página (`/facturas-venta/nueva`): cliente con búsqueda y "Usar", datos derivados en solo lectura, numeración informativa y la primera línea; el documento se crea con esa línea en una transacción y queda "En preparación" (`/facturas-venta/en-preparacion/{id}`).
- "Registrar factura" con confirmación (total, líneas, serie y próximo número), "Guardar para después" y "Descartar factura"; sin Liberar/Reabrir en la UI.
- Nota de crédito desde la factura registrada con "Crear nota de crédito": todas las líneas pendientes y la devolución al inventario; se ajusta en `/notas-credito-venta/en-preparacion/{id}` y se registra con confirmación.
- Series fijas por la configuración (la de registro solo la cambia el Administrador) y almacén fijo en el predeterminado; la API rechaza lo demás (`serie.no_editable`, `almacen.no_editable`).
- Facturación con 2 entradas de menú y pestañas "Registradas" / "En preparación (n)"; las rutas antiguas de borradores redirigen.
- Accesibilidad: datos en solo lectura en `<dl>`, resumen de errores con `role="alert"` enfocado tras la navegación mejorada, nombres accesibles en las acciones de fila.
- Suite xUnit ampliada a N tests (0 avisos) y E2E `10-alta-rapida`.
```

(sustituir N por el total real del Step 2). Sin contraseñas ni datos de `.env`.

Plan: añadir al final "## Resultado de feat/alta-rapida-documentos" con: commits por task (`git log --oneline f79a44a..HEAD`), total de la
suite, cargas de página medidas por el E2E 10 (factura y nota), decisiones tomadas durante la ejecución, residuales aceptados y los
documentos registrados que dejó el E2E en la base de desarrollo (números de factura y nota y el cliente `E2E Alta …`).
Spec: añadir "## Resultado" con un resumen de lo entregado y enlace a esa sección del plan.

- [ ] **Step 4: Revisión final**

Solicitar la revisión final de la rama (`superpowers:requesting-code-review` sobre `f79a44a..HEAD`) y registrar en "Resultado" los
hallazgos y su resolución.

- [ ] **Step 5: Commit**

```bash
git add README.md docs/superpowers/plans/2026-10-01-alta-rapida-documentos.md docs/superpowers/specs/2026-10-01-alta-rapida-documentos-design.md
git commit -m "docs: resultado y cierre de la rama de alta rapida de documentos"
```

---

## Decisiones tomadas al planificar (ambigüedades del spec)

| # | Ambigüedad | Decisión |
|---|---|---|
| AR1 | Cómo sabe Application que el usuario tiene `CanAdministrar` | `IUsuarioActual` (existente: `Nombre`, `Id`) gana `EsAdministrador`: en la API, `ClaimsPrincipal.IsInRole("Administrador")`, que es exactamente lo que exige la política `CanAdministrar`; fuera de HTTP (`UsuarioActualSistema`), `false`. No se crea otra abstracción. |
| AR2 | `SerieRegistroId` enviada por un no Administrador con el MISMO valor que ya tiene el documento | Se rechaza igual (400 `serie.no_editable`): la regla es "si llega"; Blazor nunca la envía para un no Administrador. Se valida antes de abrir la transacción (no se consume número). |
| AR3 | Alcance de "almacén fijo" | Solo la cabecera (lo que el spec lista como dato derivado): alta con `AlmacenId` distinto del predeterminado → 400; en la modificación, null o el actual = conservar, el predeterminado se acepta, otro → 400. Las líneas de producto conservan su almacén opcional (API y fila de línea). La copia de factura a borrador conserva NS13 (almacén de la factura o el predeterminado con aviso). |
| AR4 | Campo de los errores de la primera línea | `PrimeraLinea.<Campo>` (p. ej. `PrimeraLinea.PrecioUnitario`), para no confundirlos con los de la cabecera (`Descripcion` existe en ambas). |
| AR5 | "`CopiarLineas = true` por defecto" en la API | `CopiarLineas` y `DevolverInventario` pasan a `bool?` en el cuerpo HTTP; omitidos = `true`. El comando y el handler no cambian. Los tests que dependían del valor anterior lo envían explícito. |
| AR6 | "Los listados exponen un conteo" | Sin endpoint nuevo: el conteo es el `Total` del listado existente con `tamanoPagina=1` (`ContarEnPreparacionAsync`). Para la columna "total" de la lista se añade `ImporteTotal` (vista previa en SQL con la aritmética de `CalculadoraIvaFactura`; en la nota, sin topes de lo ya acreditado). |
| AR7 | ¿Se cambia el cliente o el facturar-a en la preparación? | No: el spec solo lista como editables fechas, vencimiento y descripción. Facturar-a = vender-a en la UI (la API conserva `SocioNegocioFacturarAId`). Para otro cliente se descarta y se crea de nuevo. |
| AR8 | Documentos heredados en estado Liberada sin "Reabrir" en la UI | La página en preparación los marca y ofrece "Registrar factura" y "Volver a editar" (llama a `reabrir`); nunca "Liberar". El formulario `liberar` queda vacío en el árbol para POST de pestañas antiguas. |
| AR9 | Qué guarda "Guardar para después" y "Registrar factura" | Ambos son botones del formulario de la cabecera (`UpdateInput.Accion`): guardan lo editado y luego vuelven a la lista "En preparación" o abren la confirmación. "Guardar cambios" guarda y se queda. Así ningún cambio tecleado se pierde al registrar. |
| AR10 | "Devolver inventario" editable en la nota | Por línea (casilla existente de la edición de línea); la nota no tiene ese dato en la cabecera. Al crearla se activa en todas las líneas de producto. |
| AR11 | Número del nombre accesible "Eliminar línea 2" | Posición ordinal en la tabla (1, 2, …); el ancla de la fila usa `NumeroLinea` (`#linea-20000`), que es lo que citan los errores de la API. |
| AR12 | E2E "factura de 2 líneas y nota total" frente a la convención "la suite no registra documentos" | `10-alta-rapida` sí registra (lo exige el criterio de éxito); usa líneas de cuenta contable (sin existencias) y un cliente propio; los documentos quedan en la base de desarrollo y se anotan en "Resultado". |
| AR13 | Lo tecleado en `/facturas-venta/nueva` antes de una búsqueda GET de producto | Se pierde (la búsqueda es un GET); el spec garantiza conservar lo capturado ante errores de VALIDACIÓN (POST), que sí se conserva. Las fechas vienen por defecto a hoy. |
| AR14 | Texto del aviso sin serie ("No hay una serie configurada para facturas de venta…") | "No hay una serie configurada para facturas de venta (o la serie configurada está inactiva): no se puede preparar ni registrar." (ídem "notas de crédito"); se bloquea si falta o está inactiva la serie del tipo de borrador O la de registro. Si la configuración no carga, no se bloquea (la API valida). |
| AR15 | Cómo contar "~7 cargas" | Se cuentan las acciones del usuario que piden una página (enlace, GET o POST con redirección), no las peticiones HTTP (la navegación mejorada encadena POST + GET). El flujo de 2 líneas de cuenta contable da 8 (incluye buscar el cliente y elegir el tipo de línea); límite del E2E: ≤ 8 para la factura y ≤ 3 para la nota. |
| AR16 | Mensajes de la API con "borrador"/"posteó" | Son textos de la API (nombres internos que el spec deja sin cambios); la UI no los reescribe. Los textos propios de Blazor sí siguen "Registrar"/"En preparación". |
| AR17 | `README.md` con un cambio sin commitear ajeno | Ninguna task lo toca hasta R10, que pregunta al usuario antes. |

---

## Cobertura del spec

| Sección del spec | Task |
|---|---|
| Objetivo / criterio de éxito (cargas, nota en 2 clics + confirmación, sin perder lo capturado, numeración y motor intactos) | R2, R6, R7, R9 (cargas), G1/R10 (motor y numeración sin cambios) |
| D1 documento "En preparación" que se retoma | R6 (Guardar para después), R8 (pestaña y lista) |
| D2 series solo el Administrador | R1 (API), R5 (`NumeracionRegistro`), R6/R7 (páginas) |
| D3 almacén fijo; fechas, vencimiento y descripción editables | R2 (API), R6 (UI) |
| D4 nota desde la factura con todo lo pendiente | R3 (API), R7 (UI) |
| D5 enfoque A (borrador interno creado con la primera línea) | R2, R6 |
| 1.1 `/facturas-venta/nueva`: buscador + "Usar", `?socioId=`, tarjeta `<dl>`, "Detalles contables", vencimiento, línea de numeración, editables, fila de línea | R5, R6 |
| 1.2 primer "Agregar línea" = cabecera + línea en un POST, transacción, redirección, sin número de borrador, sin crear nada si falla | R2, R6 |
| 1.3 "Registrar factura" con confirmación y resultado "Factura … registrada." + "Nueva factura"; "Guardar para después"; "Descartar factura"; sin Liberar/Reabrir | R5, R6 |
| 1.4 serie de preparación oculta; registro en solo lectura; "Cambiar serie de registro" en "Más opciones" | R5, R6 |
| 1.5 aviso bloqueante sin configuración/serie inactiva, enlace o "Pida a un administrador…", "Registrar" deshabilitado con motivo, aviso de numeración | R5, R6 (Review Focus 3) |
| 2 Nota: "Crear nota de crédito" con series configuradas, `CopiarLineas`, devolución, editor, Registrar/Guardar/Descartar, `/notas-credito-venta/nueva` sin series, Administrador en "Más opciones" | R3, R7 |
| 2 Navegación: menú de 2 entradas, pestañas `?estado=preparacion` con (n), palabras clave, "+ Nueva …", lista con cliente/creado/líneas/total y Continuar/Descartar | R4, R8 |
| 2 Rutas antiguas redirigen; "Ver preparación" en el documento registrado | R6, R7, R8 |
| 2 Textos "Registrar"/"En preparación"; nombres internos sin cambios | R6, R7, R8 (Step 8), Global Constraints |
| 2 Accesibilidad: `<dl>`, `role="alert"` con enlaces a líneas y foco tras `enhancedload` (`Blazor.addEventListener`), insignias de texto, "Eliminar línea 2" | R5, R6, R7, R8 |
| 2 Documentos abandonados (sin documentos sin líneas; sin purga) | R2 (se crea con la línea), R6 (Blazor siempre envía la primera línea) |
| 3.1 API alta con primera línea en transacción, lógica de línea reutilizada, rollback, alta sin líneas disponible | R2 |
| 3.2 Series: `SerieBorradorId` → 400; `SerieRegistroId` solo Administrador; posteo con la serie congelada | R1 (posteo sin cambios: G1 Step 4) |
| 3.3 Almacén predeterminado, `almacen.no_editable` | R2 |
| 3.4 Nota con series configuradas y `CopiarLineas = true`; ajuste por línea con su tope | R3 (endpoint de línea existente) |
| 3.5 Conteo para "En preparación (n)" | R4 |
| 3.6 Sin migraciones | G1, G2, R10 |
| Blazor: rutas, redirecciones, listas, menú, reglas Static SSR | R6, R7, R8, G2 |
| Pruebas: handlers, API 400/403/409, páginas SSR, tests existentes actualizados, E2E 09 y 10, puerta de cierre | R1–R4, R5–R8, R9, G1, G2, R10 |
| Fuera de alcance | Global Constraints |
