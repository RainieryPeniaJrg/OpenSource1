extern alias BlazorApp;
using System.Net;
using BlazorApp::OpenSource1.Blazor.Services;
using Moq;
using OpenSource1.Application.Features.Almacenes.Dtos;
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
/// Factura en preparación en una sola página (spec no-series, Parte 4; spec alta rápida: /facturas-venta/en-preparacion/{id}): cabecera
/// editable en el sitio con serie de registro y próximo número, líneas con fila de alta y edición en la fila, totales al pie, barra del
/// documento; documento registrado (Posteada) en solo lectura; POST obsoleto o forzado con el mensaje real de la API (Review Focus 5);
/// las rutas antiguas redirigen; filtro Posteada del listado (hasta R8).
/// </summary>
public sealed class DocumentoFacturaBorradorTests
{
    private static readonly Guid IdAbierto = Guid.Parse("7d0c9a51-0000-0000-0000-0000000005a1");
    private static readonly Guid IdPosteado = Guid.Parse("7d0c9a51-0000-0000-0000-0000000005a2");
    private static readonly Guid IdLinea = Guid.Parse("7d0c9a51-0000-0000-0000-0000000005a3");
    private static readonly Guid IdSocio = Guid.Parse("7d0c9a51-0000-0000-0000-0000000005a4");
    private static readonly Guid IdAlmacen = Guid.Parse("7d0c9a51-0000-0000-0000-0000000005a5");
    private static readonly Guid IdSerieFv = Guid.Parse("7d0c9a51-0000-0000-0000-0000000005a6");
    private static readonly Guid IdSerieOtra = Guid.Parse("7d0c9a51-0000-0000-0000-0000000005a7");
    private static string Url(Guid id) => $"/facturas-venta/en-preparacion/{id}";

    [Fact]
    public async Task PaginaUnica_CabeceraEditable_Lineas_FilaNueva_TotalesYBarra()
    {
        using var app = Configurar(new BlazorSsrFactory());

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), Url(IdAbierto));

        Assert.Contains("data-testid=\"page-toolbar\"", html);
        Assert.Contains("name=\"_handler\" value=\"guardar-cabecera\"", html);
        Assert.Contains("data-testid=\"guardar-cabecera\"", html);
        Assert.Contains("name=\"UpdateInput.SerieRegistroId\"", html);
        Assert.Contains("próximo <strong>00000013</strong>", html);
        Assert.Contains("data-testid=\"tabla-lineas\"", html);
        Assert.Contains("data-testid=\"fila-nueva\"", html);
        Assert.Contains("data-testid=\"total-descuentos\">5.00<", html);
        Assert.Contains($"href=\"{Url(IdAbierto)}?eliminar=true\" data-testid=\"descartar\"", html);
        Assert.Contains("data-testid=\"datos-derivados\"", html);
        Assert.Contains(">Término de pago</dt>", html);
        Assert.DoesNotContain("/editar", html);

        // Limpiar campos y Cancelar en la tarjeta editable.
        var editable = html[html.IndexOf("data-testid=\"cabecera-editable\"", StringComparison.Ordinal)..];
        Assert.Contains($"<a href=\"{Url(IdAbierto)}\" data-testid=\"limpiar-cabecera\"", editable);
        Assert.Contains("<a href=\"/facturas-venta?estado=preparacion\" data-testid=\"cancelar-cabecera\"", editable);
    }

    [Theory]
    [InlineData("https://evil.example/x")]
    [InlineData("//evil.example/x")]
    public async Task ReturnUrlExterno_CaeAlListado(string externo)
    {
        using var app = Configurar(new BlazorSsrFactory());
        var escapado = Uri.EscapeDataString(externo);

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), $"{Url(IdAbierto)}?returnUrl={escapado}");
        var redireccion = await app.Cliente().GetAsync($"/facturas-venta/borradores/{IdAbierto}/editar?returnUrl={escapado}");

        // Ningún enlace ni campo de vuelta lleva el destino externo (la acción de los formularios es la URL pedida, que no es un enlace).
        Assert.DoesNotMatch("href=\"[^\"]*evil\\.example", html);
        Assert.DoesNotMatch("name=\"returnUrl\" value=\"[^\"]*evil\\.example", html);
        Assert.Contains("<a href=\"/facturas-venta?estado=preparacion\" data-testid=\"cancelar-cabecera\"", html);
        Assert.Equal(HttpStatusCode.Redirect, redireccion.StatusCode);
        Assert.Equal($"{Url(IdAbierto)}?returnUrl=%2Ffacturas-venta%3Festado%3Dpreparacion", FormulariosSsr.Destino(redireccion));
    }

    [Fact]
    public async Task BorradorPosteada_PostObsoletoDeLinea_MuestraElConflictoDeLaApi()
    {
        using var app = Configurar(new BlazorSsrFactory());
        app.Simular<IFacturaVentaApiClient>()
            .Setup(c => c.CreateLineaAsync(IdPosteado, It.Is<LineaFacturaInput>(i => i.Tipo == TipoLineaFactura.Comentario && i.Descripcion == "Comentario"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<LineaFacturaVentaBorradorResponse>(false, "El borrador ya se posteó: es de solo lectura (abra su factura)."));

        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), $"{Url(IdPosteado)}?tipo=3", "add-linea", new Dictionary<string, string>
        {
            ["AddInput.Tipo"] = "3", ["AddInput.Descripcion"] = "Comentario",
        });

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Contains("El borrador ya se posteó: es de solo lectura (abra su factura).", HtmlSsr.Decodificar(await respuesta.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task BorradorPosteada_PostObsoletoDePosteo_MuestraElConflictoDeLaApi()
    {
        using var app = Configurar(new BlazorSsrFactory());
        app.Simular<IFacturaVentaApiClient>()
            .Setup(c => c.PostearAsync(IdPosteado, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<ResultadoPosteoFactura>(false, "El borrador ya se posteó como la factura FV-000009."));

        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), $"{Url(IdPosteado)}?registrar=true", "confirm-postear", new Dictionary<string, string>());

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Contains("El borrador ya se posteó como la factura FV-000009.", HtmlSsr.Decodificar(await respuesta.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task GuardarCabecera_EnviaLaSerieDeRegistroYElXmin_YVuelveALaMismaPagina()
    {
        using var app = Configurar(new BlazorSsrFactory());
        var api = app.Simular<IFacturaVentaApiClient>();
        api.Setup(c => c.UpdateBorradorAsync(IdAbierto, It.Is<BorradorCabeceraInput>(i =>
                i.SerieRegistroId == IdSerieOtra && i.Descripcion == "Nueva" && i.SocioNegocioId == IdSocio && i.AlmacenId == null), 11, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<FacturaVentaBorradorResponse>(true, "ok"));

        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), Url(IdAbierto), "guardar-cabecera", Cabecera(IdSerieOtra));

        Assert.Equal($"{Url(IdAbierto)}?ok=modificado", FormulariosSsr.Destino(respuesta));
        api.Verify(c => c.UpdateBorradorAsync(IdAbierto, It.IsAny<BorradorCabeceraInput>(), 11, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GuardarCabecera_PostForzadoSinPermiso_MensajeDeLaApi()
    {
        using var app = Configurar(new BlazorSsrFactory());
        app.Simular<IFacturaVentaApiClient>()
            .Setup(c => c.UpdateBorradorAsync(IdAbierto, It.IsAny<BorradorCabeceraInput>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<FacturaVentaBorradorResponse>(false, "No tiene permisos para modificar borradores."));
        var soloConsulta = app.Cliente("Ejecutor", permisos: "CanConsult");

        var html = await HtmlSsr.HtmlAsync(soloConsulta, Url(IdAbierto));
        var forzado = await FormulariosSsr.EnviarAsync(soloConsulta, Url(IdAbierto), "guardar-cabecera", Cabecera(IdSerieFv));

        Assert.DoesNotContain("data-testid=\"guardar-cabecera\"", html);
        Assert.Contains("value=\"guardar-cabecera\"", html);
        Assert.Equal(HttpStatusCode.OK, forzado.StatusCode);
        Assert.Contains("No tiene permisos para modificar borradores.", HtmlSsr.Decodificar(await forzado.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task BorradorPosteada_SoloLectura_ConEnlaceALaFactura()
    {
        using var app = Configurar(new BlazorSsrFactory());

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), Url(IdPosteado));

        Assert.Contains("data-testid=\"documento-cabecera\"", html);
        Assert.Contains("data-testid=\"aviso-posteada\"", html);
        Assert.Contains("href=\"/facturas-venta/FV-000009\"", html);
        Assert.DoesNotContain("data-testid=\"guardar-cabecera\"", html);
        Assert.DoesNotContain("data-testid=\"fila-nueva\"", html);
        Assert.DoesNotContain("?eliminar=true", html);
        Assert.DoesNotContain("?postear=true", html);
        Assert.DoesNotContain("?registrar=true", html);
        Assert.Contains("value=\"guardar-cabecera\"", html);
        Assert.Contains("value=\"confirm-postear\"", html);
    }

    [Fact]
    public async Task BorradorPosteada_PostObsoletoDeCabecera_MuestraElConflictoDeLaApi()
    {
        using var app = Configurar(new BlazorSsrFactory());
        app.Simular<IFacturaVentaApiClient>()
            .Setup(c => c.UpdateBorradorAsync(IdPosteado, It.IsAny<BorradorCabeceraInput>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<FacturaVentaBorradorResponse>(false, "El borrador ya se posteó: es de solo lectura (abra su factura)."));

        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), Url(IdPosteado), "guardar-cabecera", Cabecera(IdSerieFv));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Contains("El borrador ya se posteó", HtmlSsr.Decodificar(await respuesta.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task EditarLinea_EnLaFilaDeLaTabla()
    {
        using var app = Configurar(new BlazorSsrFactory());

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), $"{Url(IdAbierto)}?editLineaId={IdLinea}");

        var tabla = html[html.IndexOf("data-testid=\"tabla-lineas\"", StringComparison.Ordinal)..];
        Assert.Contains("data-testid=\"fila-edicion\"", tabla);
        Assert.Contains("value=\"update-linea\"", tabla);
    }

    [Fact]
    public async Task Eliminar_ConConfirmacion_VuelveAlListado()
    {
        using var app = Configurar(new BlazorSsrFactory());
        app.Simular<IFacturaVentaApiClient>()
            .Setup(c => c.DeleteBorradorAsync(IdAbierto, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<bool>(true, "ok", true));

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), $"{Url(IdAbierto)}?eliminar=true");
        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), $"{Url(IdAbierto)}?eliminar=true", "delete-borrador", new Dictionary<string, string>());

        Assert.Contains("Sí, descartar", html);
        Assert.Equal("/facturas-venta?estado=preparacion&ok=descartada", FormulariosSsr.Destino(respuesta));
    }

    [Fact]
    public async Task Registrar_VaALaFactura_ConAviso()
    {
        using var app = Configurar(new BlazorSsrFactory());
        app.Simular<IFacturaVentaApiClient>()
            .Setup(c => c.PostearAsync(IdAbierto, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<ResultadoPosteoFactura>(true, "ok",
                new ResultadoPosteoFactura("FV-000013", 118m, "00000005", "La serie FV alcanzó su número de aviso")));

        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), $"{Url(IdAbierto)}?registrar=true", "confirm-postear", new Dictionary<string, string>());

        Assert.StartsWith("/facturas-venta/FV-000013?ok=registrada&aviso=", FormulariosSsr.Destino(respuesta));
    }

    [Fact]
    public async Task RutaEditar_RedirigeALaPaginaUnica_ConservandoElRetorno()
    {
        using var app = Configurar(new BlazorSsrFactory());

        var respuesta = await app.Cliente().GetAsync($"/facturas-venta/borradores/{IdAbierto}/editar?returnUrl=%2Ffacturas-venta%3Festado%3Dpreparacion");

        Assert.Equal(HttpStatusCode.Redirect, respuesta.StatusCode);
        Assert.Equal($"{Url(IdAbierto)}?returnUrl=%2Ffacturas-venta%3Festado%3Dpreparacion", FormulariosSsr.Destino(respuesta));
    }

    [Fact]
    public async Task FalloAlCargarSeries_NoBloqueaGuardar_YConservaLaSerieActual()
    {
        // F7: el fallo de series no bloquea guardar; el selector del Administrador (en "Más opciones") rotula la serie actual "(actual)".
        using var app = Configurar(new BlazorSsrFactory());
        app.Simular<ISerieApiClient>()
            .Setup(c => c.ActivasDelTipoAsync(It.IsAny<TipoDocumentoSerie>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("caída"));

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), Url(IdAbierto));

        Assert.Contains("data-testid=\"registrar\"", html);
        Assert.Contains("data-testid=\"mas-opciones\"", html);
        Assert.Contains($"<option value=\"{IdSerieFv}\" selected=\"selected\">FV (actual)</option>", html);
        Assert.DoesNotContain("No fue posible cargar los clientes o los almacenes", html);
    }

    [Fact]
    public async Task BorradorPosteada_LineasEnSoloLectura_YSerieSinProximo()
    {
        using var app = Configurar(new BlazorSsrFactory());
        var series = app.Simular<ISerieApiClient>();

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), Url(IdPosteado));

        Assert.Contains("data-testid=\"lineas-documento\"", html);
        Assert.DoesNotContain("data-testid=\"tabla-lineas\"", html);
        Assert.Contains("Serie de registro: <strong>FV</strong>", html);
        Assert.DoesNotContain("Se numerará", html);
        Assert.Contains("value=\"add-linea\"", html);
        Assert.Contains("value=\"update-linea\"", html);
        Assert.Contains("value=\"delete-linea\"", html);
        Assert.Contains("value=\"delete-borrador\"", html);
        series.Verify(c => c.ProximoAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConRetorno_VolverYEliminarLoConservan()
    {
        using var app = Configurar(new BlazorSsrFactory());
        app.Simular<IFacturaVentaApiClient>()
            .Setup(c => c.DeleteBorradorAsync(IdAbierto, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<bool>(true, "ok", true));
        const string retorno = "returnUrl=%2Ffacturas-venta%2Fborradores%3Festado%3D2";

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), $"{Url(IdAbierto)}?{retorno}");
        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), $"{Url(IdAbierto)}?eliminar=true&{retorno}", "delete-borrador", new Dictionary<string, string>());

        Assert.Contains("href=\"/facturas-venta/borradores?estado=2\"", html);
        Assert.Contains($"href=\"{Url(IdAbierto)}?eliminar=true&{retorno}\"", html);
        Assert.Equal("/facturas-venta/borradores?estado=2&ok=descartada", FormulariosSsr.Destino(respuesta));
    }

    [Fact]
    public async Task Liberada_CabeceraEnLecturaConProximo_SinFilaNueva()
    {
        using var app = Configurar(new BlazorSsrFactory());
        var liberado = Guid.NewGuid();
        app.Simular<IFacturaVentaApiClient>()
            .Setup(c => c.GetBorradorAsync(liberado, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Borrador(liberado, EstadoFacturaBorrador.Liberada, null));

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), Url(liberado));

        Assert.Contains("data-testid=\"documento-cabecera\"", html);
        Assert.Contains("próximo <strong>00000013</strong>", html);
        Assert.DoesNotContain("data-testid=\"fila-nueva\"", html);
        Assert.Contains("value=\"reabrir\"", html);
        Assert.Contains($"href=\"{Url(liberado)}?registrar=true\"", html);
        Assert.Contains("data-testid=\"aviso-liberada\"", html);
    }

    private static Dictionary<string, string> Cabecera(Guid serieRegistro) => new()
    {
        ["UpdateInput.Xmin"] = "11",
        ["UpdateInput.FechaRegistroTexto"] = "2026-09-01", ["UpdateInput.FechaDocumentoTexto"] = "2026-09-01",
        ["UpdateInput.Descripcion"] = "Nueva", ["UpdateInput.SerieRegistroId"] = serieRegistro.ToString(),
    };

    private static FacturaVentaBorradorResponse Borrador(Guid id, EstadoFacturaBorrador estado, string? factura) => new()
    {
        Id = id, Numero = "00000077", Estado = estado, FacturaVentaNumero = factura, SocioNegocioId = IdSocio, SocioNegocioFacturarAId = IdSocio,
        SocioNegocioCodigo = "C-001", SocioNegocioNombre = "Comercial Uno", NombreFacturacion = "Comercial Uno", AlmacenId = IdAlmacen,
        AlmacenCodigo = "PRINC", FechaRegistro = new DateOnly(2026, 9, 1), FechaDocumento = new DateOnly(2026, 9, 1),
        FechaVencimiento = new DateOnly(2026, 9, 30), Descripcion = "Vieja", Moneda = "DOP", SerieRegistroId = IdSerieFv,
        SerieRegistroCodigo = "FV", SerieBorradorCodigo = "FV-BORR", NumeroLineas = 1, Xmin = 11,
        TerminoPagoCodigo = "30D", RazonSocialFacturacion = "Comercial Uno SRL", NumeroDocumentoFiscal = "101000001",
    };

    [Theory]
    [InlineData("guardar-cabecera")]
    [InlineData("add-linea")]
    [InlineData("update-linea")]
    public async Task PostTrasFalloDeCarga_PideRecargar_YNoDiceQueFueEliminado(string formulario)
    {
        using var app = Configurar(new BlazorSsrFactory());
        var id = Guid.NewGuid();
        app.Simular<IFacturaVentaApiClient>()
            .Setup(c => c.GetBorradorAsync(id, It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("caída"));

        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), Url(id), formulario, new Dictionary<string, string>());
        var html = HtmlSsr.Decodificar(await respuesta.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Contains("No fue posible cargar la factura en preparación; recargue la página.", html);
        Assert.DoesNotContain("puede haber sido", html);
    }

    private static BlazorSsrFactory Configurar(BlazorSsrFactory app)
    {
        var facturas = app.Simular<IFacturaVentaApiClient>();
        facturas.Setup(c => c.GetBorradorAsync(IdAbierto, It.IsAny<CancellationToken>())).ReturnsAsync(Borrador(IdAbierto, EstadoFacturaBorrador.Abierta, null));
        facturas.Setup(c => c.GetBorradorAsync(IdPosteado, It.IsAny<CancellationToken>())).ReturnsAsync(Borrador(IdPosteado, EstadoFacturaBorrador.Posteada, "FV-000009"));
        facturas.Setup(c => c.ListLineasAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new LineaFacturaVentaBorradorResponse
            {
                Id = IdLinea, NumeroLinea = 10000, Tipo = TipoLineaFactura.Producto, ProductoId = Guid.NewGuid(), ProductoCodigo = "P-1",
                Descripcion = "Producto uno", Cantidad = 1m, PrecioUnitario = 100m, PorcentajeDescuentoLinea = 5m, ImporteDescuentoLinea = 5m,
                ImporteLinea = 95m, IdentificadorIva = "ITBIS18", PorcentajeIva = 18m, UnidadMedidaCodigo = "UND", Xmin = 3,
            },
        ]);
        facturas.Setup(c => c.GetTotalesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TotalesFactura([new GrupoIvaCalculado("ITBIS18", 18m, 95m, 17.10m)], 95m, 17.10m, 112.10m));
        facturas.Setup(c => c.ListBorradoresAsync(It.IsAny<FacturaVentaBorradorFiltro?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<FacturaVentaBorradorResponse>([Borrador(IdPosteado, EstadoFacturaBorrador.Posteada, "FV-000009")], 1, 50, 1));

        var series = app.Simular<ISerieApiClient>();
        series.Setup(c => c.ActivasDelTipoAsync(It.IsAny<TipoDocumentoSerie>(), It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new SerieResponse { Id = IdSerieFv, Codigo = "FV", Descripcion = "Facturas", TipoDocumento = TipoDocumentoSerie.FacturaVenta, Activa = true },
            new SerieResponse { Id = IdSerieOtra, Codigo = "FV2", Descripcion = "Sucursal", TipoDocumento = TipoDocumentoSerie.FacturaVenta, Activa = true },
        ]);
        series.Setup(c => c.ProximoAsync(IdSerieFv, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<ProximoNumeroResponse>(true, "ok", new ProximoNumeroResponse("00000013", null)));

        app.Simular<ISocioNegocioApiClient>()
            .Setup(c => c.ListAsync(It.IsAny<SocioNegocioSearchFilter?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<SocioNegocioResponse>([new SocioNegocioResponse { Id = IdSocio, Codigo = "C-001", NombreComercial = "Comercial Uno" }], 1, 50, 1));
        app.Simular<IAlmacenApiClient>()
            .Setup(c => c.ListAsync(It.IsAny<AlmacenSearchFilter?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<AlmacenResponse>([new AlmacenResponse { Id = IdAlmacen, Codigo = "PRINC", Nombre = "Principal" }], 1, 50, 1));
        return app;
    }
}
