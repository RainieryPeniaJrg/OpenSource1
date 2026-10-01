extern alias BlazorApp;

using System.Net;
using BlazorApp::OpenSource1.Blazor.Services;
using Moq;
using OpenSource1.Application.Features.Almacenes.Dtos;
using OpenSource1.Application.Features.ConfiguracionNumeracion.Dtos;
using OpenSource1.Application.Features.DiariosInventario.Lotes;
using OpenSource1.Application.Features.DiariosInventario.Lotes.Dtos;
using OpenSource1.Application.Features.FacturasVenta.Borradores.Dtos;
using OpenSource1.Application.Features.NotasCreditoVenta.Borradores.Dtos;
using OpenSource1.Application.Features.Productos.Dtos;
using OpenSource1.Application.Features.SociosNegocio.Dtos;
using OpenSource1.Application.Features.Users.Dtos;
using OpenSource1.Core.Common;
using OpenSource1.Core.Enums;
using OpenSource1.SmokeTests.TestInfrastructure;

namespace OpenSource1.SmokeTests.Blazor;

/// <summary>Fix-Features B2d: lotes de diario, cabeceras de borradores y alta de usuarios en página-tarjeta propia.</summary>
public sealed class ConversionDocumentosUsuariosTests
{
    private static readonly Guid IdLote = Guid.Parse("7d0c9a51-0000-0000-0000-00000000b2d1");
    private static readonly Guid IdBorrador = Guid.Parse("7d0c9a51-0000-0000-0000-00000000b2d2");
    private static readonly Guid IdBorradorLiberado = Guid.Parse("7d0c9a51-0000-0000-0000-00000000b2d3");
    private static readonly Guid IdNota = Guid.Parse("7d0c9a51-0000-0000-0000-00000000b2d4");
    private static readonly Guid IdSocio = Guid.Parse("7d0c9a51-0000-0000-0000-00000000b2d5");
    private static readonly Guid IdAlmacen = Guid.Parse("7d0c9a51-0000-0000-0000-00000000b2d6");

    [Theory]
    [InlineData("/diarios-inventario/nuevo", "save-lote-diario")]
    // /facturas-venta/nueva ya no es una EntityFormPage (alta rápida): la cubre AltaRapidaFacturaTests.Nueva_SinCliente_BuscadorConUsar_SinFormularioDeLinea.
    [InlineData("/admin/users/nuevo", "create-user")]
    public async Task Nuevo_RenderizaFormularioEnTarjeta(string ruta, string formName)
    {
        using var app = Configurar(new BlazorSsrFactory());

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), ruta);

        Assert.Contains("data-testid=\"entity-form-page\"", html);
        Assert.Contains($"name=\"_handler\" value=\"{formName}\"", html);
        Assert.Contains("data-testid=\"cancelar\"", html);
    }

    [Theory]
    [InlineData("/diarios-inventario?editId={0}", "/diarios-inventario/{0}/editar", "/diarios-inventario")]
    [InlineData("/facturas-venta/borradores?editId={0}", "/facturas-venta/borradores/{0}", "/facturas-venta/borradores")]
    [InlineData("/notas-credito-venta/borradores?editId={0}", "/notas-credito-venta/borradores/{0}", "/notas-credito-venta/borradores")]
    // Los borradores de factura y nota van directamente a la página única (un solo salto), sin pasar por /editar.
    public async Task EditIdLegado_RedirigeALaRutaNueva(string origen, string destino, string lista)
    {
        using var app = Configurar(new BlazorSsrFactory());
        var id = Guid.NewGuid();

        var respuesta = await app.Cliente().GetAsync(string.Format(origen, id));

        Assert.Equal(HttpStatusCode.Redirect, respuesta.StatusCode);
        var location = FormulariosSsr.Destino(respuesta);
        Assert.StartsWith(string.Format(destino, id) + "?returnUrl=", location);
        var returnUrl = Uri.UnescapeDataString(location[(location.IndexOf("returnUrl=", StringComparison.Ordinal) + "returnUrl=".Length)..]);
        Assert.StartsWith(lista, returnUrl);
        Assert.DoesNotContain("editId", returnUrl);
    }

    [Fact]
    public async Task UsuariosNewTrue_RedirigeANuevo_YEditarEsAliasDeLaFicha()
    {
        using var app = Configurar(new BlazorSsrFactory());

        var legado = await app.Cliente().GetAsync("/admin/users?new=true");
        var editar = await app.Cliente().GetAsync("/admin/users/u-1/editar");
        var nuevo = await HtmlSsr.HtmlAsync(app.Cliente(), "/admin/users/nuevo");

        Assert.Equal("/admin/users/nuevo", FormulariosSsr.Destino(legado));
        Assert.Equal(HttpStatusCode.OK, editar.StatusCode);
        Assert.Contains("name=\"CreateInput.Email\"", nuevo);
    }

    [Fact]
    public async Task Usuarios_SeleccionHabilitaEditarAliasDeLaFicha_SinEliminar()
    {
        using var app = Configurar(new BlazorSsrFactory());
        app.Simular<IUserAdminApiClient>()
            .Setup(c => c.ListUsersAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(([new UserSummaryResponse("u-1", "Ana Pérez", "ana@test.local", true, ["Administrador"])], null));

        var valida = await HtmlSsr.HtmlAsync(app.Cliente(), "/admin/users?sel=u-1");
        var obsoleta = await HtmlSsr.HtmlAsync(app.Cliente(), "/admin/users?sel=u-9");

        Assert.Contains("<a data-testid=\"accion-editar\" href=\"/admin/users/u-1/editar\"", valida);
        Assert.Contains("aria-current=\"true\"", valida);
        Assert.Contains("href=\"/admin/users/nuevo\" data-testid=\"accion-nuevo\"", valida);
        Assert.DoesNotContain("data-testid=\"accion-eliminar\"", valida);
        Assert.Contains("<span data-testid=\"accion-editar\" aria-disabled=\"true\"", obsoleta);
    }

    [Fact]
    public async Task UsuarioNuevo_CreaYVuelveALaLista()
    {
        using var app = Configurar(new BlazorSsrFactory());
        app.Simular<IUserAdminApiClient>()
            .Setup(c => c.CreateAsync("ana@test.local", "Ana Pérez", "secreto1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, null));

        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), "/admin/users/nuevo", "create-user",
            new Dictionary<string, string> { ["CreateInput.FullName"] = "Ana Pérez", ["CreateInput.Email"] = "ana@test.local", ["CreateInput.Password"] = "secreto1" });

        Assert.Equal(HttpStatusCode.Redirect, respuesta.StatusCode);
        Assert.StartsWith("/admin/users?ok=", FormulariosSsr.Destino(respuesta));
    }

    [Fact]
    public async Task LoteDiario_AltaRedirigeConOk()
    {
        using var app = Configurar(new BlazorSsrFactory());
        var plantilla = Guid.NewGuid();
        app.Simular<IDiarioInventarioApiClient>()
            .Setup(c => c.CreateLoteAsync(It.Is<LoteDiarioInput>(i => i.Codigo == "AJ-01" && i.PlantillaDiarioId == plantilla), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LoteDiarioOperationResult(true, "ok", new LoteDiarioResponse { Id = Guid.NewGuid(), Codigo = "AJ-01" }));

        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), "/diarios-inventario/nuevo", "save-lote-diario",
            new Dictionary<string, string> { ["SaveInput.PlantillaDiarioId"] = plantilla.ToString(), ["SaveInput.Codigo"] = "AJ-01", ["SaveInput.Nombre"] = "Ajustes" });

        Assert.True(respuesta.StatusCode == HttpStatusCode.Redirect, HtmlSsr.Decodificar(await respuesta.Content.ReadAsStringAsync()));
        Assert.Equal("/diarios-inventario?ok=created", FormulariosSsr.Destino(respuesta));
    }

    [Fact]
    public async Task LoteDiario_Editar_CargaLoGuardado_YGuardaConElIdDeLaRuta_VolviendoAlReturnUrl()
    {
        using var app = Configurar(new BlazorSsrFactory());
        var api = app.Simular<IDiarioInventarioApiClient>();
        api.Setup(c => c.UpdateLoteAsync(IdLote, It.Is<LoteDiarioInput>(i => i.Codigo == "AJ-02"), 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LoteDiarioOperationResult(true, "ok"));
        var url = $"/diarios-inventario/{IdLote}/editar?returnUrl=%2Fdiarios-inventario%3Fcodigo%3DAJ%26sel%3D{IdLote}";

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), url);
        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), url, "update-lote-diario", new Dictionary<string, string>
        {
            ["UpdateInput.Id"] = IdLote.ToString(), ["UpdateInput.PlantillaDiarioId"] = Guid.NewGuid().ToString(), ["UpdateInput.Xmin"] = "7",
            ["UpdateInput.Codigo"] = "AJ-02", ["UpdateInput.Nombre"] = "Ajustes",
        });

        Assert.Contains("value=\"AJ-01\"", html);
        Assert.Contains("name=\"_handler\" value=\"update-lote-diario\"", html);
        Assert.Equal($"/diarios-inventario?codigo=AJ&sel={IdLote}&ok=updated", FormulariosSsr.Destino(respuesta));
        api.Verify(c => c.UpdateLoteAsync(IdLote, It.IsAny<LoteDiarioInput>(), 7, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LoteDiario_Nuevo_SinCanAdd_Mensaje_YPostForzado_DevuelveMensajeDeLaApi()
    {
        using var app = Configurar(new BlazorSsrFactory());
        app.Simular<IDiarioInventarioApiClient>()
            .Setup(c => c.CreateLoteAsync(It.IsAny<LoteDiarioInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LoteDiarioOperationResult(false, "No tiene permisos para agregar lotes de diario."));
        var supervisor = app.Cliente("Supervisor");

        var html = await HtmlSsr.HtmlAsync(supervisor, "/diarios-inventario/nuevo");
        var forzado = await FormulariosSsr.EnviarAsync(supervisor, "/diarios-inventario/nuevo", "save-lote-diario",
            new Dictionary<string, string> { ["SaveInput.PlantillaDiarioId"] = Guid.NewGuid().ToString(), ["SaveInput.Codigo"] = "AJ-01", ["SaveInput.Nombre"] = "Ajustes" });

        Assert.Contains("No tiene permiso para realizar esta acción.", html);
        Assert.Contains("value=\"save-lote-diario\"", html);
        Assert.DoesNotContain("data-testid=\"guardar\"", html);
        Assert.Equal(HttpStatusCode.OK, forzado.StatusCode);
        Assert.Contains("No tiene permisos para agregar lotes de diario.", HtmlSsr.Decodificar(await forzado.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task Diarios_SeleccionValidaHabilitaEditar_YLaQueNoEstaEnLaPaginaLaDeshabilita()
    {
        using var app = Configurar(new BlazorSsrFactory());

        var valida = await HtmlSsr.HtmlAsync(app.Cliente(), $"/diarios-inventario?sel={IdLote}");
        var obsoleta = await HtmlSsr.HtmlAsync(app.Cliente(), $"/diarios-inventario?sel={Guid.NewGuid()}");

        Assert.Contains($"<a data-testid=\"accion-editar\" href=\"/diarios-inventario/{IdLote}/editar?returnUrl=", valida);
        Assert.Contains($"<a data-testid=\"accion-eliminar\" href=\"/diarios-inventario?sel={IdLote}&deleteId={IdLote}\"", valida);
        Assert.Contains("aria-current=\"true\"", valida);
        Assert.Contains("href=\"/diarios-inventario/nuevo?returnUrl=", valida);
        Assert.Contains("<span data-testid=\"accion-editar\" aria-disabled=\"true\"", obsoleta);
    }

    [Fact]
    public async Task Borradores_SoloAbiertosSeModificanOEliminan()
    {
        using var app = Configurar(new BlazorSsrFactory());

        var abierto = await HtmlSsr.HtmlAsync(app.Cliente(), $"/facturas-venta/borradores?numero=B&sel={IdBorrador}");
        var liberado = await HtmlSsr.HtmlAsync(app.Cliente(), $"/facturas-venta/borradores?sel={IdBorradorLiberado}");

        Assert.Contains($"<a data-testid=\"accion-editar\" href=\"/facturas-venta/borradores/{IdBorrador}?returnUrl=", abierto);
        Assert.Contains("href=\"/facturas-venta/nueva?returnUrl=", abierto);
        Assert.Contains("value=\"B\"", abierto);
        // Fix-Features C3 (R7): un liberado se selecciona (Ver ▾ Abrir borrador / Cliente) pero Modificar y Eliminar se ocultan.
        Assert.DoesNotContain("data-testid=\"accion-editar\"", liberado);
        Assert.DoesNotContain("data-testid=\"accion-eliminar\"", liberado);
        Assert.Contains($"href=\"/facturas-venta/borradores/{IdBorradorLiberado}\"", liberado);
    }

    [Fact]
    public async Task FacturaNueva_CreaYAbreElBorrador()
    {
        using var app = Configurar(new BlazorSsrFactory());
        var nuevo = Guid.NewGuid();
        app.Simular<IFacturaVentaApiClient>()
            .Setup(c => c.CreateBorradorAsync(It.Is<BorradorCabeceraInput>(i => i.SocioNegocioId == IdSocio && i.PrimeraLinea != null), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<FacturaVentaBorradorResponse>(true, "ok", new FacturaVentaBorradorResponse { Id = nuevo }));

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), "/facturas-venta/nueva");
        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), $"/facturas-venta/nueva?socioId={IdSocio}&tipo=3", "alta-factura",
            new Dictionary<string, string>
            {
                ["AltaInput.SocioNegocioId"] = IdSocio.ToString(), ["AltaInput.Tipo"] = "3", ["AltaInput.Descripcion"] = "Servicio",
            });

        Assert.Contains("C-001 — Comercial Uno", html);
        Assert.Contains("data-testid=\"buscar-socio\"", html);
        Assert.Equal($"/facturas-venta/en-preparacion/{nuevo}?tipo=3&ok=linea_creada", FormulariosSsr.Destino(respuesta));
    }

    [Fact]
    public async Task LineaFactura_ComponenteCompartido_EnAltaYEnPreparacion()
    {
        using var app = Configurar(new BlazorSsrFactory());
        var producto = Guid.NewGuid();
        app.Simular<IProductoApiClient>()
            .Setup(c => c.GetByIdAsync(producto, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProductoResponse { Id = producto, Codigo = "P-1", Nombre = "Producto uno", PrecioVenta = 10m });

        var alta = HtmlSsr.Decodificar(await HtmlSsr.HtmlAsync(app.Cliente(), $"/facturas-venta/nueva?socioId={IdSocio}&tipo=3"));
        var preparacion = HtmlSsr.Decodificar(await HtmlSsr.HtmlAsync(app.Cliente(), $"/facturas-venta/en-preparacion/{IdBorrador}?tipo=3"));
        var altaProducto = HtmlSsr.Decodificar(await HtmlSsr.HtmlAsync(app.Cliente(), $"/facturas-venta/nueva?socioId={IdSocio}&tipo=1&addProductoId={producto}"));
        var preparacionProducto = HtmlSsr.Decodificar(await HtmlSsr.HtmlAsync(app.Cliente(), $"/facturas-venta/en-preparacion/{IdBorrador}?tipo=1&addProductoId={producto}"));

        Assert.Contains("name=\"AltaInput.Descripcion\"", alta);
        Assert.Contains("for=\"AltaInput_Descripcion\">Descripción de la línea *</label>", alta);
        Assert.Contains("name=\"AddInput.Descripcion\"", preparacion);
        Assert.Contains("for=\"AddInput_Descripcion\">Descripción de la línea *</label>", preparacion);
        const string ayuda = "data-testid=\"ayuda-precio\">Mayor que cero; para regalar, use 100 % de descuento.</p>";
        Assert.Contains(ayuda, altaProducto);
        Assert.Contains("name=\"AltaInput.PrecioUnitarioTexto\" value=\"10.00\"", altaProducto);
        Assert.Contains(ayuda, preparacionProducto);
        Assert.Contains("name=\"AddInput.PrecioUnitarioTexto\" value=\"10.00\"", preparacionProducto);
    }

    [Fact]
    public async Task Usuarios_TarjetaEditar_EscapaElId()
    {
        using var app = Configurar(new BlazorSsrFactory());
        app.Simular<IUserAdminApiClient>()
            .Setup(c => c.ListUsersAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(([new UserSummaryResponse("u 1/x", "Ana Pérez", "ana@test.local", true, ["Administrador"])], null));

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), "/admin/users");

        Assert.Contains("href=\"/admin/users/u%201%2Fx/editar\"", html);
        Assert.DoesNotContain("href=\"/admin/users/u 1/x/editar\"", html);
    }

    [Theory]
    [InlineData("/diarios-inventario", "save-lote-diario")]
    [InlineData("/facturas-venta/borradores", "add-borrador")]
    [InlineData("/notas-credito-venta/borradores", "update-borrador-nota")]
    [InlineData("/admin/users", "create-user")]
    public async Task Listado_SinFormulariosEnLinea(string ruta, string formName)
    {
        using var app = Configurar(new BlazorSsrFactory());

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), ruta);

        Assert.Contains("data-testid=\"page-toolbar\"", html);
        Assert.DoesNotContain($"value=\"{formName}\"", html);
        Assert.DoesNotContain("id=\"agregar\"", html);
        Assert.DoesNotContain("id=\"modificar\"", html);
    }

    private static BlazorSsrFactory Configurar(BlazorSsrFactory app)
    {
        var diarios = app.Simular<IDiarioInventarioApiClient>();
        diarios.Setup(c => c.ListPlantillasAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        diarios.Setup(c => c.ListLotesAsync(It.IsAny<LoteDiarioSearchCriteria?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<LoteDiarioResponse>([new LoteDiarioResponse { Id = IdLote, Codigo = "AJ-01", Nombre = "Ajustes" }], 1, 50, 1));
        diarios.Setup(c => c.GetLoteByIdAsync(IdLote, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LoteDiarioResponse { Id = IdLote, Codigo = "AJ-01", Nombre = "Ajustes", Xmin = 7 });

        var facturas = app.Simular<IFacturaVentaApiClient>();
        facturas.Setup(c => c.ListBorradoresAsync(It.IsAny<FacturaVentaBorradorFiltro?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<FacturaVentaBorradorResponse>(
            [
                new FacturaVentaBorradorResponse { Id = IdBorrador, Numero = "B-1", Estado = EstadoFacturaBorrador.Abierta },
                new FacturaVentaBorradorResponse { Id = IdBorradorLiberado, Numero = "B-2", Estado = EstadoFacturaBorrador.Liberada },
            ], 1, 50, 2));
        facturas.Setup(c => c.GetBorradorAsync(IdBorrador, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FacturaVentaBorradorResponse
            {
                Id = IdBorrador, Numero = "B-1", Estado = EstadoFacturaBorrador.Abierta, SocioNegocioId = IdSocio, SocioNegocioFacturarAId = IdSocio,
                AlmacenId = IdAlmacen, FechaRegistro = new DateOnly(2026, 9, 1), FechaDocumento = new DateOnly(2026, 9, 1),
                FechaVencimiento = new DateOnly(2026, 9, 30), Descripcion = "Vieja", Xmin = 11,
            });

        var notas = app.Simular<INotaCreditoVentaApiClient>();
        notas.Setup(c => c.GetBorradorAsync(IdNota, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotaCreditoVentaBorradorResponse
            {
                Id = IdNota, Numero = "NC-1", FacturaVentaNumero = "FAC-0001", FechaRegistro = new DateOnly(2026, 9, 1),
                FechaDocumento = new DateOnly(2026, 9, 1), Descripcion = "Vieja", Xmin = 5,
            });

        app.Simular<ISocioNegocioApiClient>()
            .Setup(c => c.ListAsync(It.IsAny<SocioNegocioSearchFilter?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<SocioNegocioResponse>([new SocioNegocioResponse { Id = IdSocio, Codigo = "C-001", NombreComercial = "Comercial Uno" }], 1, 50, 1));
        app.Simular<IAlmacenApiClient>()
            .Setup(c => c.ListAsync(It.IsAny<AlmacenSearchFilter?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<AlmacenResponse>([new AlmacenResponse { Id = IdAlmacen, Codigo = "PRINC", Nombre = "Principal" }], 1, 50, 1));
        app.Simular<IUserAdminApiClient>();
        app.Simular<ISocioNegocioApiClient>()
            .Setup(c => c.GetByIdAsync(IdSocio, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocioNegocioResponse { Id = IdSocio, Codigo = "C-001", NombreComercial = "Comercial Uno" });
        // Alta rápida: numeración configurada y activa (sin ella, /facturas-venta/nueva muestra el aviso bloqueante).
        app.Simular<IConfiguracionNumeracionApiClient>()
            .Setup(c => c.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new ConfiguracionNumeracionResponse { TipoDocumento = TipoDocumentoSerie.BorradorFacturaVenta, SerieCodigo = "FV-BORR", SerieActiva = true },
                new ConfiguracionNumeracionResponse { TipoDocumento = TipoDocumentoSerie.FacturaVenta, SerieId = Guid.NewGuid(), SerieCodigo = "FV", SerieActiva = true },
            ]);
        // F7: series simuladas (sin series activas) para el alta y la cabecera del borrador.
        app.Simular<ISerieApiClient>()
            .Setup(c => c.ActivasDelTipoAsync(It.IsAny<TipoDocumentoSerie>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        return app;
    }
}
