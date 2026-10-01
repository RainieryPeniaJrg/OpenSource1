extern alias BlazorApp;
using System.Net;
using BlazorApp::OpenSource1.Blazor.Services;
using Moq;
using OpenSource1.Application.Features.FacturasVenta.Calculo;
using OpenSource1.Application.Features.MovimientosCliente.Dtos;
using OpenSource1.Application.Features.SociosNegocio.Dtos;
using OpenSource1.Application.Features.NotasCreditoVenta.Borradores.Dtos;
using OpenSource1.Application.Features.NotasCreditoVenta.Posteo;
using OpenSource1.Application.Features.Series.Dtos;
using OpenSource1.Core.Common;
using OpenSource1.Core.Enums;
using OpenSource1.SmokeTests.TestInfrastructure;

namespace OpenSource1.SmokeTests.Blazor;

/// <summary>Borrador de nota de crédito en una sola página (spec no-series, Parte 4), Posteada en solo lectura, `/editar` y alta con series.</summary>
public sealed class DocumentoNotaBorradorTests
{
    private static readonly Guid IdAbierta = Guid.Parse("7d0c9a51-0000-0000-0000-0000000006b1");
    private static readonly Guid IdPosteada = Guid.Parse("7d0c9a51-0000-0000-0000-0000000006b2");
    private static readonly Guid IdSerieNc = Guid.Parse("7d0c9a51-0000-0000-0000-0000000006b3");
    private static readonly Guid IdSerieOtra = Guid.Parse("7d0c9a51-0000-0000-0000-0000000006b4");
    private static string Url(Guid id) => $"/notas-credito-venta/borradores/{id}";

    [Fact]
    public async Task PaginaUnica_CabeceraEditable_ConSerieYProximo_TablaYTotales()
    {
        using var app = Configurar(new BlazorSsrFactory());

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), Url(IdAbierta));

        Assert.Contains("name=\"_handler\" value=\"guardar-cabecera-nota\"", html);
        Assert.Contains("name=\"UpdateInput.SerieRegistroId\"", html);
        Assert.Contains("próximo <strong>NC-000004</strong>", html);
        Assert.Contains("data-testid=\"tabla-lineas\"", html);
        Assert.Contains("data-testid=\"totales\"", html);
        Assert.Contains($"href=\"{Url(IdAbierta)}?postear=true\"", html);
        Assert.Contains($"href=\"{Url(IdAbierta)}?eliminar=true\"", html);
        Assert.DoesNotContain("/editar", html);
    }

    [Fact]
    public async Task GuardarCabecera_EnviaFechasDescripcionYSerie()
    {
        using var app = Configurar(new BlazorSsrFactory());
        var api = app.Simular<INotaCreditoVentaApiClient>();
        api.Setup(c => c.UpdateBorradorAsync(IdAbierta, It.Is<NotaCreditoCabeceraInput>(i => i.SerieRegistroId == IdSerieOtra && i.Descripcion == "Devolución"), 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<NotaCreditoVentaBorradorResponse>(true, "ok"));

        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), Url(IdAbierta), "guardar-cabecera-nota", new Dictionary<string, string>
        {
            ["UpdateInput.Xmin"] = "5", ["UpdateInput.FechaRegistroTexto"] = "2026-09-02", ["UpdateInput.FechaDocumentoTexto"] = "2026-09-03",
            ["UpdateInput.Descripcion"] = "Devolución", ["UpdateInput.SerieRegistroId"] = IdSerieOtra.ToString(),
        });

        Assert.Equal($"{Url(IdAbierta)}?ok=modificado", FormulariosSsr.Destino(respuesta));
    }

    [Fact]
    public async Task Posteada_SoloLectura_EnlazaLaNota_YPostObsoletoMuestraElConflicto()
    {
        using var app = Configurar(new BlazorSsrFactory());
        app.Simular<INotaCreditoVentaApiClient>()
            .Setup(c => c.UpdateBorradorAsync(IdPosteada, It.IsAny<NotaCreditoCabeceraInput>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<NotaCreditoVentaBorradorResponse>(false, "El borrador de nota de crédito ya se posteó: es de solo lectura (abra su nota)."));

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), Url(IdPosteada));
        var obsoleto = await FormulariosSsr.EnviarAsync(app.Cliente(), Url(IdPosteada), "guardar-cabecera-nota", new Dictionary<string, string>
        {
            ["UpdateInput.Xmin"] = "5", ["UpdateInput.FechaRegistroTexto"] = "2026-09-02", ["UpdateInput.FechaDocumentoTexto"] = "2026-09-02",
        });

        Assert.Contains("data-testid=\"documento-cabecera\"", html);
        Assert.Contains("href=\"/notas-credito-venta/NC-000003\"", html);
        Assert.DoesNotContain("data-testid=\"guardar-cabecera\"", html);
        Assert.Contains("value=\"guardar-cabecera-nota\"", html);
        Assert.Contains("ya se posteó", HtmlSsr.Decodificar(await obsoleto.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task Postear_VaALaNota_ConAviso()
    {
        using var app = Configurar(new BlazorSsrFactory());
        app.Simular<INotaCreditoVentaApiClient>()
            .Setup(c => c.PostearAsync(IdAbierta, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<ResultadoPosteoNotaCredito>(true, "ok", new ResultadoPosteoNotaCredito("NC-000004", 50m, 50m, "00000007", "Aviso de NC")));

        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), $"{Url(IdAbierta)}?postear=true", "confirm-postear", new Dictionary<string, string>());

        Assert.StartsWith("/notas-credito-venta/NC-000004?ok=posteada&aplicado=50.00&aviso=", FormulariosSsr.Destino(respuesta));
    }

    [Fact]
    public async Task RutaEditar_Redirige_YListadoConFiltroPosteada()
    {
        using var app = Configurar(new BlazorSsrFactory());
        var api = app.Simular<INotaCreditoVentaApiClient>();

        var respuesta = await app.Cliente().GetAsync($"{Url(IdAbierta)}/editar");
        var lista = await HtmlSsr.HtmlAsync(app.Cliente(), "/notas-credito-venta/borradores?estado=3");

        Assert.Equal(HttpStatusCode.Redirect, respuesta.StatusCode);
        Assert.Equal(Url(IdAbierta), FormulariosSsr.Destino(respuesta));
        Assert.Contains("<option value=\"3\" selected", lista);
        api.Verify(c => c.ListBorradoresAsync(It.Is<NotaCreditoVentaBorradorFiltro?>(f => f!.Estado == 3), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task GuardarCabecera_PostForzadoSinPermiso_MensajeDeLaApi()
    {
        using var app = Configurar(new BlazorSsrFactory());
        app.Simular<INotaCreditoVentaApiClient>()
            .Setup(c => c.UpdateBorradorAsync(IdAbierta, It.IsAny<NotaCreditoCabeceraInput>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<NotaCreditoVentaBorradorResponse>(false, "No tiene permisos para modificar borradores."));
        var soloConsulta = app.Cliente("Ejecutor", permisos: "CanConsult");

        var html = await HtmlSsr.HtmlAsync(soloConsulta, Url(IdAbierta));
        var forzado = await FormulariosSsr.EnviarAsync(soloConsulta, Url(IdAbierta), "guardar-cabecera-nota", Cabecera(IdSerieNc));

        Assert.DoesNotContain("data-testid=\"guardar-cabecera\"", html);
        Assert.Contains("value=\"guardar-cabecera-nota\"", html);
        Assert.Contains("data-testid=\"documento-cabecera\"", html);
        Assert.Equal(HttpStatusCode.OK, forzado.StatusCode);
        Assert.Contains("No tiene permisos para modificar borradores.", HtmlSsr.Decodificar(await forzado.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task RutaEditar_RedirigeALaPaginaUnica_ConservandoElRetorno()
    {
        using var app = Configurar(new BlazorSsrFactory());

        var respuesta = await app.Cliente().GetAsync($"{Url(IdAbierta)}/editar?returnUrl=%2Fnotas-credito-venta%2Fborradores%3Festado%3D3");

        Assert.Equal(HttpStatusCode.Redirect, respuesta.StatusCode);
        Assert.Equal($"{Url(IdAbierta)}?returnUrl=%2Fnotas-credito-venta%2Fborradores%3Festado%3D3", FormulariosSsr.Destino(respuesta));
    }

    [Fact]
    public async Task Nueva_ConSelectoresDeSerie_EnviaLasDosSeries()
    {
        using var app = Configurar(new BlazorSsrFactory());
        var socioId = Guid.NewGuid();
        var nueva = Guid.NewGuid();
        app.Simular<ISocioNegocioApiClient>().Setup(c => c.GetByIdAsync(socioId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocioNegocioResponse { Id = socioId, Codigo = "C0001", NombreComercial = "Comercial Uno" });
        app.Simular<ICobroApiClient>().Setup(c => c.ListMovimientosAbiertosAsync(socioId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MovimientoClienteResponse>
            {
                new() { Id = 1, TipoDocumento = TipoDocumentoCliente.Factura, NumeroDocumento = "FV-000001", ImporteOriginal = 118m, ImporteRestante = 118m, Abierta = true },
            });
        app.Simular<INotaCreditoVentaApiClient>()
            .Setup(c => c.CreateBorradorAsync(It.Is<NotaCreditoBorradorInput>(i => i.SerieBorradorId == null && i.SerieRegistroId == IdSerieOtra), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<NotaCreditoVentaBorradorResponse>(true, "ok", new NotaCreditoVentaBorradorResponse { Id = nueva }));
        var url = $"/notas-credito-venta/nueva?socioId={socioId}";

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), url);
        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), url, "nueva-nota-socio", new Dictionary<string, string>
        {
            ["NotaInput.FacturaVentaNumero"] = "FV-000001", ["NotaInput.SerieBorradorId"] = "", ["NotaInput.SerieRegistroId"] = IdSerieOtra.ToString(),
        });

        Assert.Contains("name=\"NotaInput.SerieBorradorId\"", html);
        Assert.Contains("name=\"NotaInput.SerieRegistroId\"", html);
        Assert.Contains("— La configurada —", html);
        Assert.Contains("NC2 — Sucursal", html);
        Assert.Equal($"{Url(nueva)}?ok=creado", FormulariosSsr.Destino(respuesta));
    }

    [Fact]
    public async Task FalloAlCargarSeries_NoBloqueaGuardar_YConservaLaSerieActual()
    {
        // F7: la nota no tiene opciones imprescindibles; el fallo de series solo rotula la serie actual "(actual)".
        using var app = Configurar(new BlazorSsrFactory());
        app.Simular<ISerieApiClient>()
            .Setup(c => c.ActivasDelTipoAsync(It.IsAny<TipoDocumentoSerie>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("caída"));

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), Url(IdAbierta));

        Assert.Contains("data-testid=\"guardar-cabecera\"", html);
        Assert.Contains($"<option value=\"{IdSerieNc}\" selected=\"selected\">NC (actual)</option>", html);
    }

    [Fact]
    public async Task Posteada_SinPostearEliminarNiAcreditar_LineasEnLectura_SinProximo()
    {
        using var app = Configurar(new BlazorSsrFactory());
        var series = app.Simular<ISerieApiClient>();

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), Url(IdPosteada));

        Assert.Contains("data-testid=\"aviso-posteada\"", html);
        Assert.Contains("data-testid=\"lineas-documento\"", html);
        Assert.DoesNotContain("data-testid=\"tabla-lineas\"", html);
        Assert.DoesNotContain("data-testid=\"tabla-acreditables\"", html);
        Assert.DoesNotContain("?postear=true", html);
        Assert.DoesNotContain("?eliminar=true", html);
        Assert.DoesNotContain("Acreditar", html);
        Assert.Contains("Serie de registro: <strong>NC</strong>", html);
        Assert.DoesNotContain("Se numerará", html);
        Assert.Contains("value=\"add-linea\"", html);
        Assert.Contains("value=\"update-linea\"", html);
        Assert.Contains("value=\"delete-linea\"", html);
        Assert.Contains("value=\"confirm-postear\"", html);
        Assert.Contains("value=\"delete-borrador-nota\"", html);
        series.Verify(c => c.ProximoAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EditarLinea_EnLaFilaDeLaTabla()
    {
        using var app = Configurar(new BlazorSsrFactory());
        var idLinea = Guid.NewGuid();
        app.Simular<INotaCreditoVentaApiClient>().Setup(c => c.ListLineasAsync(IdAbierta, It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new LineaNotaCreditoVentaBorradorResponse { Id = idLinea, NumeroLinea = 10000, Tipo = TipoLineaFactura.Producto, ProductoCodigo = "P1", Descripcion = "Producto", Cantidad = 1m, CantidadFacturada = 2m, Xmin = 3 },
        ]);

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), $"{Url(IdAbierta)}?editLineaId={idLinea}");

        var tabla = html[html.IndexOf("data-testid=\"tabla-lineas\"", StringComparison.Ordinal)..];
        Assert.Contains("data-testid=\"fila-edicion\"", tabla);
        Assert.Contains("value=\"update-linea\"", tabla);
        Assert.Contains("name=\"UpdateInputLinea.Xmin\" value=\"3\"", tabla);
    }

    [Theory]
    [InlineData("7")]
    [InlineData("0")]
    public async Task BorradorPosteada_PostObsoletoDeAgregarLinea_VaALaApi_YMuestraSuConflicto(string lineaFactura)
    {
        using var app = Configurar(new BlazorSsrFactory());
        var api = app.Simular<INotaCreditoVentaApiClient>();
        api.Setup(c => c.CreateLineaAsync(IdPosteada, It.IsAny<long>(), It.IsAny<decimal?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<LineaNotaCreditoVentaBorradorResponse>(false, "El borrador de nota de crédito ya se posteó: es de solo lectura (abra su nota)."));

        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), $"{Url(IdPosteada)}?addLineaId={lineaFactura}", "add-linea", new Dictionary<string, string>
        {
            ["AddInput.LineaFacturaVentaId"] = lineaFactura, ["AddInput.CantidadTexto"] = "1",
        });

        var html = HtmlSsr.Decodificar(await respuesta.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Contains("ya se posteó: es de solo lectura (abra su nota).", html);
        Assert.DoesNotContain("Seleccione primero", html);
        api.Verify(c => c.CreateLineaAsync(IdPosteada, long.Parse(lineaFactura), 1m, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BorradorPosteada_PostObsoletoDeModificarLinea_VaALaApi_YMuestraSuConflicto()
    {
        using var app = Configurar(new BlazorSsrFactory());
        var idLinea = Guid.NewGuid();
        var api = app.Simular<INotaCreditoVentaApiClient>();
        api.Setup(c => c.UpdateLineaAsync(idLinea, It.IsAny<decimal?>(), It.IsAny<bool>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<LineaNotaCreditoVentaBorradorResponse>(false, "El borrador de nota de crédito ya se posteó: es de solo lectura (abra su nota)."));

        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), $"{Url(IdPosteada)}?editLineaId={idLinea}", "update-linea", new Dictionary<string, string>
        {
            ["UpdateInputLinea.CantidadTexto"] = "2", ["UpdateInputLinea.Xmin"] = "4",
        });

        var html = HtmlSsr.Decodificar(await respuesta.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Contains("ya se posteó: es de solo lectura (abra su nota).", html);
        Assert.DoesNotContain("No se encontró la línea seleccionada", html);
        api.Verify(c => c.UpdateLineaAsync(idLinea, 2m, false, 4, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EditarLinea_QueYaNoExiste_AvisoDentroDeLaFilaDeEdicion()
    {
        using var app = Configurar(new BlazorSsrFactory());
        var idLinea = Guid.NewGuid();
        app.Simular<INotaCreditoVentaApiClient>()
            .Setup(c => c.GetLineaAsync(idLinea, It.IsAny<CancellationToken>())).ReturnsAsync((LineaNotaCreditoVentaBorradorResponse?)null);

        var html = HtmlSsr.Decodificar(await HtmlSsr.HtmlAsync(app.Cliente(), $"{Url(IdAbierta)}?editLineaId={idLinea}"));

        var inicioTabla = html.IndexOf("data-testid=\"tabla-lineas\"", StringComparison.Ordinal);
        var aviso = html.IndexOf("No se encontró la línea seleccionada", StringComparison.Ordinal);
        Assert.True(inicioTabla >= 0 && aviso > inicioTabla, "El aviso debe estar dentro de la tabla de líneas.");
        var fila = html.LastIndexOf("data-testid=\"fila-edicion\"", aviso, StringComparison.Ordinal);
        Assert.True(fila > inicioTabla, "El aviso debe estar en la fila de edición.");
    }

    [Fact]
    public async Task Eliminar_ConConfirmacion_VuelveAlListado()
    {
        using var app = Configurar(new BlazorSsrFactory());
        app.Simular<INotaCreditoVentaApiClient>()
            .Setup(c => c.DeleteBorradorAsync(IdAbierta, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<bool>(true, "ok", true));

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), $"{Url(IdAbierta)}?eliminar=true");
        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), $"{Url(IdAbierta)}?eliminar=true", "delete-borrador-nota", new Dictionary<string, string>());

        Assert.Contains("Sí, eliminar", html);
        Assert.Equal("/notas-credito-venta/borradores?ok=eliminado", FormulariosSsr.Destino(respuesta));
    }

    [Fact]
    public async Task ConRetorno_VolverLimpiarGuardarYEliminarLoConservan()
    {
        using var app = Configurar(new BlazorSsrFactory());
        var api = app.Simular<INotaCreditoVentaApiClient>();
        api.Setup(c => c.DeleteBorradorAsync(IdAbierta, It.IsAny<CancellationToken>())).ReturnsAsync(new VentaOperationResult<bool>(true, "ok", true));
        api.Setup(c => c.UpdateBorradorAsync(IdAbierta, It.IsAny<NotaCreditoCabeceraInput>(), 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<NotaCreditoVentaBorradorResponse>(true, "ok"));
        const string retorno = "returnUrl=%2Fnotas-credito-venta%2Fborradores%3Festado%3D3";

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), $"{Url(IdAbierta)}?{retorno}");
        var guardado = await FormulariosSsr.EnviarAsync(app.Cliente(), $"{Url(IdAbierta)}?{retorno}", "guardar-cabecera-nota", Cabecera(IdSerieNc));
        var eliminado = await FormulariosSsr.EnviarAsync(app.Cliente(), $"{Url(IdAbierta)}?eliminar=true&{retorno}", "delete-borrador-nota", new Dictionary<string, string>());

        Assert.Contains("href=\"/notas-credito-venta/borradores?estado=3\"", html);
        Assert.Contains($"href=\"{Url(IdAbierta)}?eliminar=true&{retorno}\"", html);
        Assert.Contains($"href=\"{Url(IdAbierta)}?postear=true&{retorno}\"", html);
        Assert.Contains($"<a href=\"{Url(IdAbierta)}?{retorno}\" data-testid=\"limpiar-cabecera\"", html);
        Assert.Contains("<a href=\"/notas-credito-venta/borradores?estado=3\" data-testid=\"cancelar-cabecera\"", html);
        Assert.Equal($"{Url(IdAbierta)}?{retorno}&ok=modificado", FormulariosSsr.Destino(guardado));
        Assert.Equal("/notas-credito-venta/borradores?estado=3&ok=eliminado", FormulariosSsr.Destino(eliminado));
    }

    [Fact]
    public async Task Listado_SeleccionPosteada_SinEditarNiEliminar()
    {
        using var app = Configurar(new BlazorSsrFactory());
        app.Simular<INotaCreditoVentaApiClient>()
            .Setup(c => c.ListBorradoresAsync(It.IsAny<NotaCreditoVentaBorradorFiltro?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<NotaCreditoVentaBorradorResponse>(
                [Nota(IdPosteada, EstadoNotaCreditoBorrador.Posteada, "NC-000003"), Nota(IdAbierta, EstadoNotaCreditoBorrador.Abierta, null)], 1, 50, 2));

        var posteada = await HtmlSsr.HtmlAsync(app.Cliente(), $"/notas-credito-venta/borradores?estado=3&sel={IdPosteada}");
        var abierta = await HtmlSsr.HtmlAsync(app.Cliente(), $"/notas-credito-venta/borradores?sel={IdAbierta}");

        Assert.DoesNotContain("data-testid=\"accion-editar\"", posteada);
        Assert.DoesNotContain("data-testid=\"accion-eliminar\"", posteada);
        Assert.Contains("Posteada", posteada);
        Assert.Contains($"<a data-testid=\"accion-editar\" href=\"{Url(IdAbierta)}?returnUrl=", abierta);
    }

    [Theory]
    [InlineData("guardar-cabecera-nota")]
    [InlineData("add-linea")]
    [InlineData("update-linea")]
    public async Task PostTrasFalloDeCarga_PideRecargar_YNoDiceQueFueEliminado(string formulario)
    {
        using var app = Configurar(new BlazorSsrFactory());
        var id = Guid.NewGuid();
        app.Simular<INotaCreditoVentaApiClient>()
            .Setup(c => c.GetBorradorAsync(id, It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("caída"));

        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), Url(id), formulario, Cabecera(IdSerieNc));
        var html = HtmlSsr.Decodificar(await respuesta.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Contains("No fue posible cargar el borrador; recargue la página.", html);
        Assert.DoesNotContain("puede haber sido eliminado", html);
    }

    private static Dictionary<string, string> Cabecera(Guid serie) => new()
    {
        ["UpdateInput.Xmin"] = "5", ["UpdateInput.FechaRegistroTexto"] = "2026-09-02", ["UpdateInput.FechaDocumentoTexto"] = "2026-09-02",
        ["UpdateInput.Descripcion"] = "Devolución", ["UpdateInput.SerieRegistroId"] = serie.ToString(),
    };

    private static NotaCreditoVentaBorradorResponse Nota(Guid id, EstadoNotaCreditoBorrador estado, string? nota) => new()
    {
        Id = id, Numero = "NCB-000009", FacturaVentaNumero = "FV-000001", FacturaImporteTotal = 118m, Estado = estado, NotaCreditoVentaNumero = nota,
        NombreFacturacion = "Comercial Uno", FechaRegistro = new DateOnly(2026, 9, 1), FechaDocumento = new DateOnly(2026, 9, 1),
        Descripcion = "Vieja", Moneda = "DOP", SerieRegistroId = IdSerieNc, SerieRegistroCodigo = "NC", SerieBorradorCodigo = "NC-BORR", Xmin = 5,
    };

    private static BlazorSsrFactory Configurar(BlazorSsrFactory app)
    {
        var notas = app.Simular<INotaCreditoVentaApiClient>();
        notas.Setup(c => c.GetBorradorAsync(IdAbierta, It.IsAny<CancellationToken>())).ReturnsAsync(Nota(IdAbierta, EstadoNotaCreditoBorrador.Abierta, null));
        notas.Setup(c => c.GetBorradorAsync(IdPosteada, It.IsAny<CancellationToken>())).ReturnsAsync(Nota(IdPosteada, EstadoNotaCreditoBorrador.Posteada, "NC-000003"));
        notas.Setup(c => c.ListLineasAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        notas.Setup(c => c.ListLineasAcreditablesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        notas.Setup(c => c.GetTotalesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(new TotalesFactura([], 0m, 0m, 0m));
        notas.Setup(c => c.ListBorradoresAsync(It.IsAny<NotaCreditoVentaBorradorFiltro?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<NotaCreditoVentaBorradorResponse>([], 1, 50, 0));

        var series = app.Simular<ISerieApiClient>();
        series.Setup(c => c.ActivasDelTipoAsync(It.IsAny<TipoDocumentoSerie>(), It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new SerieResponse { Id = IdSerieNc, Codigo = "NC", Descripcion = "Notas", TipoDocumento = TipoDocumentoSerie.NotaCreditoVenta, Activa = true },
            new SerieResponse { Id = IdSerieOtra, Codigo = "NC2", Descripcion = "Sucursal", TipoDocumento = TipoDocumentoSerie.NotaCreditoVenta, Activa = true },
        ]);
        series.Setup(c => c.ProximoAsync(IdSerieNc, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<ProximoNumeroResponse>(true, "ok", new ProximoNumeroResponse("NC-000004", null)));
        return app;
    }
}
