extern alias BlazorApp;

using BlazorApp::OpenSource1.Blazor.Services;
using Moq;
using OpenSource1.Application.Features.FacturasVenta.Copia;
using OpenSource1.Application.Features.FacturasVenta.Posteadas;
using OpenSource1.Application.Features.FacturasVenta.Posteadas.Dtos;
using OpenSource1.Application.Features.NotasCreditoVenta.Posteadas;
using OpenSource1.Application.Features.NotasCreditoVenta.Posteadas.Dtos;
using OpenSource1.Core.Common;
using OpenSource1.SmokeTests.TestInfrastructure;

namespace OpenSource1.SmokeTests.Blazor;

/// <summary>Factura y nota posteadas con el diseño del documento (solo lectura), Copiar a borrador y enlaces al borrador (spec no-series).</summary>
public sealed class DocumentosPosteadosTests
{
    private static readonly Guid IdBorrador = Guid.Parse("7d0c9a51-0000-0000-0000-0000000007c1");
    private static readonly Guid IdNuevo = Guid.Parse("7d0c9a51-0000-0000-0000-0000000007c2");
    // Avisos con la forma exacta que genera el posteo (CalculoNumeroSerie), ya codificados para la query.
    private static readonly string AvisoFv = Uri.EscapeDataString("La serie FV alcanzó su número de aviso (FV-000091): quedan 3 número(s) en la línea.");
    private static readonly string AvisoNc = Uri.EscapeDataString("La serie NC alcanzó su número de aviso (NC-000091): quedan 2 número(s) en la línea.");

    [Fact]
    public async Task Factura_DocumentoSoloLectura_EnlaceAlBorrador_YCrearCopiarABorrador()
    {
        using var app = Configurar(new BlazorSsrFactory());

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), $"/facturas-venta/FV-000009?ok=posteada&aviso={AvisoFv}");

        Assert.Contains("data-testid=\"cabecera-factura\"", html);
        Assert.Contains("data-testid=\"documento-cabecera\"", html);
        Assert.Contains("Vender a", html);
        Assert.Contains("C-001 — Comercial Uno", html);
        Assert.Contains("data-testid=\"lineas-documento\"", html);
        Assert.Contains("La factura no tiene líneas.", html);
        Assert.Contains("data-testid=\"totales\"", html);
        Assert.Contains($"href=\"/facturas-venta/en-preparacion/{IdBorrador}\"", html);
        Assert.Contains("Ver preparación", html);
        Assert.Contains("href=\"/facturas-venta/FV-000009?copiar=true", html);
        Assert.Contains("data-testid=\"aviso-numeracion\"", html);
        Assert.Contains("quedan 3 número(s)", html);
        // El diálogo solo se abre con ?copiar=true; el formulario sigue en el árbol.
        Assert.DoesNotContain("Sí, copiar", html);
        Assert.Contains("value=\"copiar-borrador\"", html);
    }

    [Fact]
    public async Task Factura_AvisoDeNumeracion_SoloTrasElPosteo()
    {
        using var app = Configurar(new BlazorSsrFactory());

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), $"/facturas-venta/FV-000009?aviso={AvisoFv}");
        var registrada = HtmlSsr.Decodificar(await HtmlSsr.HtmlAsync(app.Cliente(), $"/facturas-venta/FV-000009?ok=registrada&aviso={AvisoFv}"));

        Assert.DoesNotContain("data-testid=\"aviso-numeracion\"", html);
        Assert.DoesNotContain("data-testid=\"nueva-factura\"", html);
        Assert.Contains("data-testid=\"aviso-numeracion\"", registrada);
        Assert.Contains("Factura FV-000009 registrada.", registrada);
        Assert.Contains("<a href=\"/facturas-venta/nueva\" data-testid=\"nueva-factura\"", registrada);
        Assert.Contains(">Nueva factura</a>", registrada);
    }

    [Fact]
    public async Task Factura_SinBorradorDeOrigen_MuestraElNumeroSinEnlace()
    {
        using var app = Configurar(new BlazorSsrFactory(), conBorrador: false);

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), "/facturas-venta/FV-000009");

        Assert.Contains("Preparación", html);
        Assert.DoesNotContain("Ver preparación", html);
        Assert.DoesNotContain("href=\"/facturas-venta/en-preparacion/", html);
        Assert.DoesNotContain("href=\"/facturas-venta/borradores/", html.Replace("href=\"/facturas-venta/borradores\"", string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CopiarABorrador_Dialogo_ConConfirmacionYCancelar()
    {
        using var app = Configurar(new BlazorSsrFactory());

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), "/facturas-venta/FV-000009?copiar=true");

        Assert.Contains("Copiar a factura en preparación", html);
        Assert.Contains("Sí, copiar", html);
        Assert.Contains("value=\"copiar-borrador\"", html);
        Assert.Contains("href=\"/facturas-venta/FV-000009\"", html);
    }

    [Fact]
    public async Task CopiarABorrador_SinAvisos_AbreElBorrador()
    {
        using var app = Configurar(new BlazorSsrFactory());
        app.Simular<IFacturaVentaApiClient>()
            .Setup(c => c.CopiarABorradorAsync("FV-000009", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<CopiaFacturaResponse>(true, "ok", new CopiaFacturaResponse(IdNuevo, "00000078", [])));

        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), "/facturas-venta/FV-000009?copiar=true", "copiar-borrador", new Dictionary<string, string>());

        Assert.Equal($"/facturas-venta/en-preparacion/{IdNuevo}?ok=copiado", FormulariosSsr.Destino(respuesta));
    }

    [Fact]
    public async Task CopiarABorrador_ConAvisos_LosMuestraConElEnlace()
    {
        using var app = Configurar(new BlazorSsrFactory());
        app.Simular<IFacturaVentaApiClient>()
            .Setup(c => c.CopiarABorradorAsync("FV-000009", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<CopiaFacturaResponse>(true, "ok",
                new CopiaFacturaResponse(IdNuevo, "00000078", ["Línea 20000: El producto no existe o está bloqueado para la venta. La línea no se copió."])));

        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), "/facturas-venta/FV-000009?copiar=true", "copiar-borrador", new Dictionary<string, string>());
        var html = HtmlSsr.Decodificar(await respuesta.Content.ReadAsStringAsync());

        Assert.Equal(System.Net.HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Contains("Línea 20000", html);
        Assert.Contains($"href=\"/facturas-venta/en-preparacion/{IdNuevo}\"", html);
        Assert.Contains("data-testid=\"borrador-copiado\"", html);
        // Hecha la copia, el diálogo ya no se vuelve a ofrecer (el formulario sigue en el árbol).
        Assert.DoesNotContain("Sí, copiar", html);
    }

    [Fact]
    public async Task CopiarABorrador_Error_MuestraElMensajeDeLaApi()
    {
        using var app = Configurar(new BlazorSsrFactory());
        app.Simular<IFacturaVentaApiClient>()
            .Setup(c => c.CopiarABorradorAsync("FV-000009", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<CopiaFacturaResponse>(false, "Revise los datos:", Errors: ["El cliente C-001 está bloqueado."]));

        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), "/facturas-venta/FV-000009?copiar=true", "copiar-borrador", new Dictionary<string, string>());
        var html = HtmlSsr.Decodificar(await respuesta.Content.ReadAsStringAsync());

        Assert.Contains("Revise los datos:", html);
        Assert.Contains("El cliente C-001 está bloqueado.", html);
        Assert.DoesNotContain("data-testid=\"borrador-copiado\"", html);
    }

    [Fact]
    public async Task CopiarABorrador_SinCanAdd_NoSeOfrece_YElPostForzadoMuestraElMensajeDeLaApi()
    {
        using var app = Configurar(new BlazorSsrFactory());
        app.Simular<IFacturaVentaApiClient>()
            .Setup(c => c.CopiarABorradorAsync("FV-000009", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<CopiaFacturaResponse>(false, "No tiene permisos para crear borradores."));
        var soloConsulta = app.Cliente("Ejecutor", permisos: "CanConsult");

        var html = await HtmlSsr.HtmlAsync(soloConsulta, "/facturas-venta/FV-000009");
        var forzado = await FormulariosSsr.EnviarAsync(soloConsulta, "/facturas-venta/FV-000009", "copiar-borrador", new Dictionary<string, string>());

        Assert.DoesNotContain("?copiar=true", html);
        Assert.Contains("value=\"copiar-borrador\"", html);
        Assert.Contains("No tiene permisos para crear borradores.", HtmlSsr.Decodificar(await forzado.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task CrearNota_DialogoConSeries_EnviaLasSeriesElegidas()
    {
        using var app = Configurar(new BlazorSsrFactory());
        var serieBorrador = Guid.NewGuid();
        var serieRegistro = Guid.NewGuid();
        var series = app.Simular<ISerieApiClient>();
        series.Setup(c => c.ActivasDelTipoAsync(OpenSource1.Core.Enums.TipoDocumentoSerie.BorradorNotaCreditoVenta, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new OpenSource1.Application.Features.Series.Dtos.SerieResponse { Id = serieBorrador, Codigo = "NCB", Descripcion = "Borradores de nota" }]);
        series.Setup(c => c.ActivasDelTipoAsync(OpenSource1.Core.Enums.TipoDocumentoSerie.NotaCreditoVenta, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new OpenSource1.Application.Features.Series.Dtos.SerieResponse { Id = serieRegistro, Codigo = "NC", Descripcion = "Notas de crédito" }]);
        var nota = Guid.NewGuid();
        var notas = app.Simular<INotaCreditoVentaApiClient>();
        notas.Setup(c => c.CreateBorradorAsync(It.IsAny<NotaCreditoBorradorInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<OpenSource1.Application.Features.NotasCreditoVenta.Borradores.Dtos.NotaCreditoVentaBorradorResponse>(
                true, "ok", new OpenSource1.Application.Features.NotasCreditoVenta.Borradores.Dtos.NotaCreditoVentaBorradorResponse { Id = nota }));

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), "/facturas-venta/FV-000009?crearNota=true");
        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), "/facturas-venta/FV-000009?crearNota=true", "crear-nota",
            new Dictionary<string, string> { ["CrearNotaInput.SerieBorradorId"] = serieBorrador.ToString(), ["CrearNotaInput.SerieRegistroId"] = "" });

        Assert.Contains("name=\"CrearNotaInput.SerieBorradorId\"", html);
        Assert.Contains("name=\"CrearNotaInput.SerieRegistroId\"", html);
        Assert.Contains("— La configurada —", html);
        Assert.Contains("NC — Notas de crédito", html);
        Assert.Equal($"/notas-credito-venta/borradores/{nota}?ok=creado", FormulariosSsr.Destino(respuesta));
        notas.Verify(c => c.CreateBorradorAsync(
            It.Is<NotaCreditoBorradorInput>(i => i.FacturaVentaNumero == "FV-000009" && i.SerieBorradorId == serieBorrador && i.SerieRegistroId == null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Nota_DocumentoSoloLectura_ConEnlaceAlBorrador()
    {
        using var app = Configurar(new BlazorSsrFactory());

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), $"/notas-credito-venta/NC-000003?ok=posteada&aviso={AvisoNc}");

        Assert.Contains("data-testid=\"cabecera-nota\"", html);
        Assert.Contains("data-testid=\"documento-cabecera\"", html);
        Assert.Contains("href=\"/facturas-venta/FV-000009\"", html);
        Assert.Contains("data-testid=\"lineas-documento\"", html);
        Assert.Contains("La nota de crédito no tiene líneas.", html);
        Assert.Contains("data-testid=\"totales\"", html);
        Assert.Contains($"href=\"/notas-credito-venta/borradores/{IdBorrador}\"", html);
        Assert.Contains("data-testid=\"aviso-numeracion\"", html);
        Assert.Contains("quedan 2 número(s)", html);
    }

    [Fact]
    public async Task Factura_ConLineas_DireccionAcreditadoComentarioYAritmeticaDeDescuentos()
    {
        using var app = Configurar(new BlazorSsrFactory());
        var cabecera = new FacturaVentaResponse
        {
            Numero = "FV-000009", NumeroBorrador = "00000077", FacturaVentaBorradorId = IdBorrador,
            NombreFacturacion = "Comercial Uno", RazonSocialFacturacion = "Comercial Uno SRL", NumeroDocumentoFiscal = "101000001",
            DireccionFacturacionLinea1 = "Calle Duarte 10", CiudadFacturacion = "Santiago", PaisCodigoFacturacion = "DO",
            SocioNegocioCodigo = "C-001", SocioNegocioNombre = "Comercial Uno", SocioNegocioFacturarACodigo = "C-001",
            FechaRegistro = new DateOnly(2026, 9, 1), FechaDocumento = new DateOnly(2026, 9, 1), FechaVencimiento = new DateOnly(2026, 9, 30),
            AlmacenCodigo = "PRINC", Moneda = "DOP", ImporteSinIva = 180m, ImporteIva = 32.40m, ImporteTotal = 212.40m,
        };
        var facturas = app.Simular<IFacturaVentaApiClient>();
        facturas.Setup(c => c.GetFacturaAsync("FV-000009", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FacturaVentaDetalleResponse(cabecera,
                [
                    new LineaFacturaVentaResponse
                    {
                        Id = 101, NumeroLinea = 10000, Tipo = OpenSource1.Core.Enums.TipoLineaFactura.Producto, ProductoCodigo = "P-001", Descripcion = "Producto A",
                        AlmacenCodigo = "PRINC", UnidadMedidaCodigo = "UND", Cantidad = 2m, PrecioUnitario = 100m, PorcentajeDescuentoLinea = 10m,
                        ImporteDescuentoLinea = 20m, ImporteLinea = 180m, IdentificadorIva = "ITBIS18", PorcentajeIva = 18m,
                    },
                    new LineaFacturaVentaResponse { Id = 102, NumeroLinea = 20000, Tipo = OpenSource1.Core.Enums.TipoLineaFactura.Comentario, Descripcion = "Nota interna" },
                ],
                [new LineaIvaFacturaVentaResponse { IdentificadorIva = "ITBIS18", PorcentajeIva = 18m, BaseImponible = 180m, ImporteIva = 32.40m, CuentaIvaNumero = "2101" }]));
        var notas = app.Simular<INotaCreditoVentaApiClient>();
        notas.Setup(c => c.ListNotasAsync(It.IsAny<NotaCreditoVentaSearchCriteria?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<NotaCreditoVentaResponse>([new NotaCreditoVentaResponse { Numero = "NC-000003", FacturaVentaNumero = "FV-000009" }], 1, 50, 1));
        notas.Setup(c => c.GetNotaAsync("NC-000003", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotaCreditoVentaDetalleResponse(
                new NotaCreditoVentaResponse { Numero = "NC-000003", FacturaVentaNumero = "FV-000009" },
                [new LineaNotaCreditoVentaResponse { Id = 1, LineaFacturaVentaId = 101, Tipo = OpenSource1.Core.Enums.TipoLineaFactura.Producto, Cantidad = 1m, ImporteLinea = 90m, DevolverInventario = true }],
                []));
        notas.Setup(c => c.ListBorradoresAsync(It.IsAny<NotaCreditoVentaBorradorFiltro?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<OpenSource1.Application.Features.NotasCreditoVenta.Borradores.Dtos.NotaCreditoVentaBorradorResponse>([], 1, 50, 0));

        var html = HtmlSsr.Decodificar(await HtmlSsr.HtmlAsync(app.Cliente(), "/facturas-venta/FV-000009"));

        // Facturar a: razón social, RNC y la dirección de facturación (línea 1, ciudad y país).
        var cabeceraHtml = html[html.IndexOf("data-testid=\"cabecera-factura\"", StringComparison.Ordinal)..html.IndexOf("data-testid=\"lineas-documento\"", StringComparison.Ordinal)];
        Assert.Contains("Comercial Uno SRL 101000001", cabeceraHtml);
        Assert.Contains("Calle Duarte 10", cabeceraHtml);
        Assert.Contains("Santiago", cabeceraHtml);
        Assert.Contains("DO", cabeceraHtml);

        // Línea con lo acreditado por la nota posteada (cantidad · importe) y lo devuelto.
        var producto = Fila(html, "Producto A");
        Assert.Contains("180.00", producto);
        Assert.Contains("1 · 90.00", producto);
        Assert.Contains("devuelto 1", producto);

        // Línea de comentario: sin columnas numéricas ni acreditado.
        var comentario = Fila(html, "Nota interna");
        Assert.DoesNotContain("·", comentario);
        Assert.DoesNotContain("%", comentario);
        Assert.DoesNotContain(".00", comentario);

        // Totales: el subtotal suma los descuentos de las líneas al importe sin ITBIS (180 + 20).
        Assert.Contains("data-testid=\"total-subtotal\">200.00", html);
        Assert.Contains("data-testid=\"total-descuentos\">20.00", html);
        Assert.Contains("data-testid=\"total-sin-iva\">180.00", html);
        Assert.Contains("data-testid=\"total-iva\">32.40", html);
        Assert.Contains("data-testid=\"total\">212.40", html);
        // Grupo de IVA con su cuenta (la del posteo, guardada en la línea de IVA).
        var totales = html[html.IndexOf("data-testid=\"totales\"", StringComparison.Ordinal)..];
        Assert.Contains(">Cuenta</th>", totales);
        Assert.Matches("ITBIS18</td>.*?<td[^>]*>2101</td>", totales);
    }

    [Fact]
    public async Task Nota_ConLineaDeDevolucion_YAritmeticaDeDescuentos()
    {
        using var app = Configurar(new BlazorSsrFactory());
        app.Simular<INotaCreditoVentaApiClient>()
            .Setup(c => c.GetNotaAsync("NC-000003", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotaCreditoVentaDetalleResponse(
                new NotaCreditoVentaResponse
                {
                    Numero = "NC-000003", NumeroBorrador = "NCB-1", NotaCreditoVentaBorradorId = IdBorrador, FacturaVentaNumero = "FV-000009",
                    NombreFacturacion = "Comercial Uno", SocioNegocioFacturarACodigo = "C-001", FechaRegistro = new DateOnly(2026, 9, 2),
                    FechaDocumento = new DateOnly(2026, 9, 2), Moneda = "DOP", ImporteSinIva = 90m, ImporteIva = 16.20m, ImporteTotal = 106.20m,
                },
                [
                    new LineaNotaCreditoVentaResponse
                    {
                        Id = 1, NumeroLinea = 10000, LineaFacturaVentaId = 101, Tipo = OpenSource1.Core.Enums.TipoLineaFactura.Producto, ProductoCodigo = "P-001",
                        Descripcion = "Producto devuelto", Cantidad = 1m, PrecioUnitario = 100m, PorcentajeDescuentoLinea = 10m, ImporteDescuentoLinea = 10m,
                        ImporteLinea = 90m, IdentificadorIva = "ITBIS18", PorcentajeIva = 18m, DevolverInventario = true,
                    },
                ],
                [new LineaIvaNotaCreditoVentaResponse { IdentificadorIva = "ITBIS18", PorcentajeIva = 18m, BaseImponible = 90m, ImporteIva = 16.20m, CuentaIvaNumero = "2102" }]));

        var html = HtmlSsr.Decodificar(await HtmlSsr.HtmlAsync(app.Cliente(), "/notas-credito-venta/NC-000003"));

        var fila = Fila(html, "Producto devuelto");
        Assert.Contains("90.00", fila);
        Assert.Contains(">Sí</td>", fila);
        Assert.Contains("data-testid=\"total-subtotal\">100.00", html);
        Assert.Contains("data-testid=\"total-descuentos\">10.00", html);
        Assert.Contains("data-testid=\"total-sin-iva\">90.00", html);
        Assert.Contains("data-testid=\"total-iva\">16.20", html);
        Assert.Contains("data-testid=\"total\">106.20", html);
        var totales = html[html.IndexOf("data-testid=\"totales\"", StringComparison.Ordinal)..];
        Assert.Contains(">Cuenta</th>", totales);
        Assert.Matches("ITBIS18</td>.*?<td[^>]*>2102</td>", totales);
    }

    [Fact]
    public async Task AvisoDeNumeracion_SinDocumentoCargado_NoSeMuestra()
    {
        using var app = Configurar(new BlazorSsrFactory());
        app.Simular<IFacturaVentaApiClient>()
            .Setup(c => c.GetFacturaAsync("FV-000404", It.IsAny<CancellationToken>())).ReturnsAsync((FacturaVentaDetalleResponse?)null);
        app.Simular<INotaCreditoVentaApiClient>()
            .Setup(c => c.GetNotaAsync("NC-000404", It.IsAny<CancellationToken>())).ReturnsAsync((NotaCreditoVentaDetalleResponse?)null);

        var factura = await HtmlSsr.HtmlAsync(app.Cliente(), $"/facturas-venta/FV-000404?ok=posteada&aviso={AvisoFv}");
        var nota = await HtmlSsr.HtmlAsync(app.Cliente(), $"/notas-credito-venta/NC-000404?ok=posteada&aviso={AvisoNc}");

        Assert.DoesNotContain("data-testid=\"aviso-numeracion\"", factura);
        Assert.DoesNotContain("quedan 3 número(s)", factura);
        Assert.DoesNotContain("data-testid=\"aviso-numeracion\"", nota);
        Assert.DoesNotContain("quedan 2 número(s)", nota);
    }

    /// <summary>
    /// Final review 4: ?aviso= y ?registro= no reflejan texto libre. Un aviso que no tiene la forma exacta del que genera el posteo
    /// no se muestra, y el registro contable del mensaje sale del documento, nunca de la query.
    /// </summary>
    [Fact]
    public async Task AvisoYRegistro_TextoArbitrarioEnLaQuery_NoSeMuestra()
    {
        using var app = Configurar(new BlazorSsrFactory());
        var aviso = Uri.EscapeDataString("Su cuenta fue bloqueada: llame al 555-0100.");
        var avisoConPlantilla = Uri.EscapeDataString("La serie FV alcanzó su número de aviso (llame al 555 0100): quedan 3 número(s) en la línea.");
        var registro = Uri.EscapeDataString("LLAME-AL-5550100");

        var factura = await HtmlSsr.HtmlAsync(app.Cliente(), $"/facturas-venta/FV-000009?ok=posteada&registro={registro}&aviso={aviso}");
        var facturaPlantilla = await HtmlSsr.HtmlAsync(app.Cliente(), $"/facturas-venta/FV-000009?ok=posteada&aviso={avisoConPlantilla}");
        var nota = await HtmlSsr.HtmlAsync(app.Cliente(), $"/notas-credito-venta/NC-000003?ok=posteada&registro={registro}&aviso={aviso}");

        foreach (var html in new[] { factura, facturaPlantilla, nota })
        {
            Assert.DoesNotContain("data-testid=\"aviso-numeracion\"", html);
            // La URL de la página (retorno, formularios) conserva la query codificada; lo que no debe aparecer es el texto mostrado.
            Assert.DoesNotContain("Su cuenta fue bloqueada", html);
            Assert.DoesNotContain("llame al 555", html);
            Assert.DoesNotContain("Registro contable LLAME", html);
        }

        Assert.Contains("Factura FV-000009 registrada.", factura);
        Assert.Contains("Nota de crédito NC-000003 posteada", nota);
    }

    /// <summary>La fila (tr) de la tabla de líneas que contiene el texto.</summary>
    private static string Fila(string html, string texto)
    {
        var tabla = html.IndexOf("data-testid=\"lineas-documento\"", StringComparison.Ordinal);
        var posicion = html.IndexOf(texto, tabla, StringComparison.Ordinal);
        Assert.True(tabla >= 0 && posicion > tabla, $"No se encontró la línea '{texto}'.");
        var inicio = html.LastIndexOf("<tr", posicion, StringComparison.Ordinal);
        var fin = html.IndexOf("</tr>", posicion, StringComparison.Ordinal);
        return html[inicio..fin];
    }

    private static BlazorSsrFactory Configurar(BlazorSsrFactory app, bool conBorrador = true)
    {
        var cabecera = new FacturaVentaResponse
        {
            Numero = "FV-000009", NumeroBorrador = "00000077", FacturaVentaBorradorId = conBorrador ? IdBorrador : null,
            NombreFacturacion = "Comercial Uno", SocioNegocioCodigo = "C-001", SocioNegocioNombre = "Comercial Uno", SocioNegocioFacturarACodigo = "C-001",
            FechaRegistro = new DateOnly(2026, 9, 1), FechaDocumento = new DateOnly(2026, 9, 1), FechaVencimiento = new DateOnly(2026, 9, 30),
            AlmacenCodigo = "PRINC", Moneda = "DOP", ImporteSinIva = 100m, ImporteIva = 18m, ImporteTotal = 118m,
        };
        var facturas = app.Simular<IFacturaVentaApiClient>();
        facturas.Setup(c => c.GetFacturaAsync("FV-000009", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FacturaVentaDetalleResponse(cabecera, [],
                [new LineaIvaFacturaVentaResponse { IdentificadorIva = "ITBIS18", PorcentajeIva = 18m, BaseImponible = 100m, ImporteIva = 18m }]));
        facturas.Setup(c => c.ListFacturasAsync(It.IsAny<FacturaVentaSearchCriteria?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<FacturaVentaResponse>([cabecera], 1, 50, 1));

        var notas = app.Simular<INotaCreditoVentaApiClient>();
        notas.Setup(c => c.ListNotasAsync(It.IsAny<NotaCreditoVentaSearchCriteria?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<NotaCreditoVentaResponse>([], 1, 50, 0));
        notas.Setup(c => c.GetNotaAsync("NC-000003", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotaCreditoVentaDetalleResponse(
                new NotaCreditoVentaResponse
                {
                    Numero = "NC-000003", NumeroBorrador = "NCB-1", NotaCreditoVentaBorradorId = IdBorrador, FacturaVentaNumero = "FV-000009",
                    NombreFacturacion = "Comercial Uno", SocioNegocioFacturarACodigo = "C-001", FechaRegistro = new DateOnly(2026, 9, 2),
                    FechaDocumento = new DateOnly(2026, 9, 2), Moneda = "DOP", ImporteSinIva = 50m, ImporteIva = 9m, ImporteTotal = 59m,
                },
                [],
                [new LineaIvaNotaCreditoVentaResponse { IdentificadorIva = "ITBIS18", PorcentajeIva = 18m, BaseImponible = 50m, ImporteIva = 9m }]));
        return app;
    }
}
