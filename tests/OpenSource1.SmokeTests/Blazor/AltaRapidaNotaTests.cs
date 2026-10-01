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
        // La fila de la nota en "Notas de crédito" de la factura enlaza a la nota en preparación.
        Assert.Contains($"href=\"{Url(IdNota)}\">00000003</a>", html);
        Assert.Contains("1 en preparación", html);
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
        // El total viene de /totales (ya con los topes que aplica el registro): sin calificativos.
        Assert.Contains(">Total a acreditar</h2>", html);
        Assert.DoesNotContain("vista previa)", html);
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
        Assert.Contains("<dt class=\"font-semibold\">Total</dt>", dialogo);
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

    [Fact]
    public async Task EnPreparacion_ModificarLineaSinCantidad_ResumenDeErroresConEnlaceALaFila()
    {
        using var app = Configurar(new BlazorSsrFactory(), []);
        var idLinea = Guid.Parse("7d0c9a51-0000-0000-0000-0000000007b9");
        app.Simular<INotaCreditoVentaApiClient>().Setup(c => c.ListLineasAsync(IdNota, It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new LineaNotaCreditoVentaBorradorResponse
            {
                Id = idLinea, NumeroLinea = 10000, Tipo = TipoLineaFactura.Producto, ProductoCodigo = "P-1", Descripcion = "Producto uno",
                Cantidad = 1m, CantidadFacturada = 2m, PrecioUnitario = 50m, ImporteLinea = 50m, IdentificadorIva = "ITBIS18", PorcentajeIva = 18m,
                DevolverInventario = true, Xmin = 4,
            },
        ]);

        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), $"{Url(IdNota)}?editLineaId={idLinea}", "update-linea",
            new Dictionary<string, string> { ["UpdateInputLinea.CantidadTexto"] = "", ["UpdateInputLinea.Xmin"] = "4" });
        var html = HtmlSsr.Decodificar(await respuesta.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var inicio = html.IndexOf("data-testid=\"resumen-errores\"", StringComparison.Ordinal);
        Assert.True(inicio >= 0, "Sin resumen de errores.");
        var resumen = html[html.LastIndexOf('<', inicio)..html.IndexOf("</div>", inicio, StringComparison.Ordinal)];
        Assert.Contains("role=\"alert\"", resumen);
        Assert.Contains("<a class=\"underline\" href=\"#linea-10000\">Línea 10000: La cantidad es obligatoria.</a>", resumen);
        Assert.Contains("id=\"linea-10000\" data-testid=\"fila-edicion\"", html);
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
