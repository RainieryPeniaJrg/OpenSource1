extern alias BlazorApp;

using BlazorApp::OpenSource1.Blazor.Services;
using Moq;
using OpenSource1.Application.Features.Almacenes.Dtos;
using OpenSource1.Application.Features.ConfiguracionNumeracion.Dtos;
using OpenSource1.Application.Features.FacturasVenta.Borradores.Dtos;
using OpenSource1.Application.Features.FacturasVenta.Posteadas;
using OpenSource1.Application.Features.FacturasVenta.Posteadas.Dtos;
using OpenSource1.Application.Features.MovimientosCliente.Dtos;
using OpenSource1.Application.Features.NotasCreditoVenta.Borradores.Dtos;
using OpenSource1.Application.Features.NotasCreditoVenta.Posteadas;
using OpenSource1.Application.Features.NotasCreditoVenta.Posteadas.Dtos;
using OpenSource1.Application.Features.SociosNegocio.Dtos;
using OpenSource1.Application.Features.TerminosPago.Dtos;
using OpenSource1.Core.Common;
using OpenSource1.Core.Enums;
using OpenSource1.SmokeTests.TestInfrastructure;

namespace OpenSource1.SmokeTests.Blazor;

/// <summary>Fix-Features C3: facturas filtradas por cliente, nueva factura con cliente precargado y nota de crédito por cliente.</summary>
public sealed class AccionesFacturasTests
{
    private static readonly Guid IdCliente = Guid.Parse("7f000000-0000-0000-0000-000000000001");
    private static readonly Guid IdBorradorAbierto = Guid.Parse("7f000000-0000-0000-0000-0000000000b1");
    private static readonly Guid IdBorradorLiberado = Guid.Parse("7f000000-0000-0000-0000-0000000000b2");
    private static readonly Guid IdNota = Guid.Parse("7f000000-0000-0000-0000-0000000000c1");

    [Fact]
    public async Task Facturas_FiltranPorCliente_YBarraConNuevaFacturaYAccionesDeLaSeleccion()
    {
        using var app = Configurar(new BlazorSsrFactory());
        var api = app.Simular<IFacturaVentaApiClient>();

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), $"/facturas-venta?socioId={IdCliente}&sel=FV0001");

        api.Verify(c => c.ListFacturasAsync(It.Is<FacturaVentaSearchCriteria?>(f => f!.SocioNegocioId == IdCliente), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        Assert.Contains("data-testid=\"page-toolbar\"", html);
        Assert.Contains("href=\"/facturas-venta/nueva", html);
        // Puerta C: la nota desde la lista lleva la lista (cliente y selección) como returnUrl.
        Assert.Contains($"href=\"/facturas-venta/FV0001?crearNota=true&returnUrl={Uri.EscapeDataString($"/facturas-venta?socioId={IdCliente}&sel=FV0001")}\"", html);
        Assert.Contains($"href=\"/ventas/movimientos-cliente?socioId={IdCliente}\"", html);
        Assert.Contains("C0001 — Comercial Uno", html);
        Assert.Contains($"<input type=\"hidden\" name=\"socioId\" value=\"{IdCliente}\"", html);
        // La selección y la paginación conservan el cliente filtrado.
        Assert.Contains($"href=\"/facturas-venta?socioId={IdCliente}&sel=FV0001\"", html);
    }

    [Fact]
    public async Task Facturas_SeleccionQueNoEstaEnLaPagina_DeshabilitaLasAcciones()
    {
        using var app = Configurar(new BlazorSsrFactory());

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), "/facturas-venta?sel=FV9999");

        Assert.DoesNotContain("href=\"/facturas-venta/FV9999?crearNota=true", html);
        Assert.Contains("aria-disabled=\"true\"", html);
    }

    [Fact]
    public async Task FacturaDetalle_TieneBarraConNotaDeCreditoYMovimientos()
    {
        using var app = Configurar(new BlazorSsrFactory());
        app.Simular<IFacturaVentaApiClient>()
            .Setup(c => c.GetFacturaAsync("FV0001", It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await Task.Yield();
                return new FacturaVentaDetalleResponse(
                    new FacturaVentaResponse { Numero = "FV0001", SocioNegocioId = IdCliente, NombreFacturacion = "Comercial Uno" }, [], []);
            });

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), "/facturas-venta/FV0001");

        Assert.Contains("data-testid=\"page-toolbar\"", html);
        // Una sola entrada para crear la nota (la de Crear ▾), con la propia factura como vuelta.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "\\?crearNota=true"));
        Assert.Contains("href=\"/facturas-venta/FV0001?crearNota=true&returnUrl=%2Ffacturas-venta%2FFV0001\"", html);
        // Y el botón directo de la barra (spec alta rápida): un clic crea la nota total en preparación.
        Assert.Contains("data-testid=\"crear-nota\"", html);
        Assert.Contains($"href=\"/ventas/movimientos-cliente?socioId={IdCliente}\"", html);
    }

    [Fact]
    public async Task FacturaDetalle_AcreditadaPorCompleto_SinCrearNota()
    {
        using var app = Configurar(new BlazorSsrFactory());
        app.Simular<IFacturaVentaApiClient>()
            .Setup(c => c.GetFacturaAsync("FV0001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FacturaVentaDetalleResponse(
                new FacturaVentaResponse { Numero = "FV0001", SocioNegocioId = IdCliente, NombreFacturacion = "Comercial Uno" }, [], []));
        app.Simular<INotaCreditoVentaApiClient>()
            .Setup(c => c.ListNotasAsync(It.IsAny<NotaCreditoVentaSearchCriteria?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<NotaCreditoVentaResponse>([], 1, 50, 0));

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), "/facturas-venta/FV0001");

        Assert.Contains("Factura acreditada por completo.", html);
        Assert.DoesNotContain("crearNota=true", html);
        Assert.DoesNotContain("Nota de crédito de la factura", html);
        Assert.DoesNotContain("data-testid=\"crear-nota\"", html);
    }

    [Fact]
    public async Task FacturaDetalle_DialogoNota_CancelarVuelveAlOrigen()
    {
        using var app = Configurar(new BlazorSsrFactory());
        app.Simular<IFacturaVentaApiClient>()
            .Setup(c => c.GetFacturaAsync("FV0001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FacturaVentaDetalleResponse(
                new FacturaVentaResponse { Numero = "FV0001", SocioNegocioId = IdCliente, NombreFacturacion = "Comercial Uno" }, [], []));
        var origen = Uri.EscapeDataString($"/facturas-venta?socioId={IdCliente}&sel=FV0001");

        var conOrigen = await HtmlSsr.HtmlAsync(app.Cliente(), $"/facturas-venta/FV0001?crearNota=true&returnUrl={origen}");
        var sinOrigen = await HtmlSsr.HtmlAsync(app.Cliente(), "/facturas-venta/FV0001?crearNota=true");
        var hostil = await HtmlSsr.HtmlAsync(app.Cliente(), "/facturas-venta/FV0001?crearNota=true&returnUrl=%2F%2Fevil.com");

        Assert.Contains($"href=\"/facturas-venta?socioId={IdCliente}&sel=FV0001\"", conOrigen);
        // El diálogo ya no tiene campos (spec alta rápida): sin "Limpiar campos"; "Sí, crear" o "Cancelar" (vuelve al origen).
        Assert.DoesNotContain($"href=\"/facturas-venta/FV0001?crearNota=true&returnUrl={origen}\"", conOrigen);
        Assert.DoesNotContain("name=\"CrearNotaInput.", conOrigen);
        Assert.Contains(">Sí, crear</button>", HtmlSsr.Decodificar(conOrigen));
        Assert.Contains("href=\"/facturas-venta/FV0001\"", sinOrigen);
        Assert.DoesNotContain("href=\"//evil.com", hostil);
        Assert.Contains("href=\"/facturas-venta/FV0001\" class=\"btn-press ml-2 rounded-lg", hostil);
    }

    [Fact]
    public async Task NuevaFactura_ConSocio_MuestraElClienteEnSoloLectura()
    {
        using var app = Configurar(new BlazorSsrFactory());

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), $"/facturas-venta/nueva?socioId={IdCliente}");

        Assert.Contains("data-testid=\"cliente-elegido\"", html);
        Assert.Contains("data-testid=\"datos-derivados\"", html);
        Assert.Contains("CONTADO — Contado", html);
        Assert.Contains("GENERAL", html);
        Assert.Contains("href=\"/facturas-venta/nueva\"", html);
        Assert.Contains(">Cambiar cliente</a>", html);
        // Cliente elegido: ni selector "Vender a" ni buscador de clientes.
        Assert.DoesNotMatch("<select[^>]*name=\"AltaInput.SocioNegocioId\"", html);
        Assert.DoesNotContain("data-testid=\"buscar-socio\"", html);
    }

    [Fact]
    public async Task NuevaFactura_SocioInexistente_AvisoYSelectorNormal()
    {
        using var app = Configurar(new BlazorSsrFactory());

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), $"/facturas-venta/nueva?socioId={Guid.NewGuid()}");

        var resumen = html[html.IndexOf("data-testid=\"resumen-errores\"", StringComparison.Ordinal)..];
        Assert.Contains("No se encontró el cliente indicado", resumen);
        Assert.DoesNotContain("data-testid=\"cliente-elegido\"", html);
        Assert.Contains("data-testid=\"buscar-socio\"", html);
    }

    // Ola final (Minor 2): ?socioId= ilegible se ignora (alta sin cliente precargado); nunca la página de error.
    [Theory]
    [InlineData("/facturas-venta/nueva?socioId=abc", "data-testid=\"buscar-socio\"")]
    [InlineData("/facturas-venta?socioId=abc", "data-testid=\"page-toolbar\"")]
    public async Task SocioIdIlegible_SeIgnora_SinPaginaDeError(string ruta, string marcaDeLaPagina)
    {
        using var app = Configurar(new BlazorSsrFactory());

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), ruta);

        Assert.Contains(marcaDeLaPagina, html);
        Assert.DoesNotContain("data-testid=\"cliente-elegido\"", html);
        Assert.DoesNotContain("No se encontró el cliente indicado", html);
        // Los enlaces construidos por la página usan el valor ya interpretado (Limpiar recarga Nav.Uri tal cual, absoluta).
        Assert.DoesNotMatch("href=\"/[^\"]*socioId=abc", html);
    }

    [Fact]
    public async Task NuevaFactura_Guardar_CreaElBorradorConElCliente()
    {
        using var app = Configurar(new BlazorSsrFactory());
        var borrador = Guid.NewGuid();
        app.Simular<IFacturaVentaApiClient>()
            .Setup(c => c.CreateBorradorAsync(It.Is<BorradorCabeceraInput>(i => i.SocioNegocioId == IdCliente), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<FacturaVentaBorradorResponse>(true, "ok", new FacturaVentaBorradorResponse { Id = borrador }));

        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), $"/facturas-venta/nueva?socioId={IdCliente}&tipo=3", "alta-factura",
            new Dictionary<string, string>
            {
                ["AltaInput.SocioNegocioId"] = IdCliente.ToString(), ["AltaInput.Tipo"] = "3", ["AltaInput.Descripcion"] = "Servicio",
                ["AltaInput.FechaRegistroTexto"] = "2026-09-28",
            });

        Assert.Equal($"/facturas-venta/en-preparacion/{borrador}?tipo=3&ok=linea_creada", FormulariosSsr.Destino(respuesta));
    }

    [Fact]
    public async Task NotaCreditoNueva_ListaSoloFacturasConPendiente_YCreaElBorrador()
    {
        using var app = Configurar(new BlazorSsrFactory());
        var nota = Guid.NewGuid();
        app.Simular<INotaCreditoVentaApiClient>()
            .Setup(c => c.CreateBorradorAsync(It.Is<NotaCreditoBorradorInput>(i => i.FacturaVentaNumero == "FV0001" && i.CopiarLineas), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<NotaCreditoVentaBorradorResponse>(true, "ok", new NotaCreditoVentaBorradorResponse { Id = nota }));

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), $"/notas-credito-venta/nueva?socioId={IdCliente}");
        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), $"/notas-credito-venta/nueva?socioId={IdCliente}", "nueva-nota-socio",
            new Dictionary<string, string> { ["NotaInput.FacturaVentaNumero"] = "FV0001", ["NotaInput.CopiarLineas"] = "true" });

        Assert.Contains("C0001 — Comercial Uno", html);
        Assert.Contains("value=\"FV0001\"", html);
        Assert.DoesNotContain("value=\"PAG-0001\"", html);
        Assert.DoesNotContain("value=\"FV0002\"", html);
        Assert.Equal($"/notas-credito-venta/en-preparacion/{nota}?ok=creado", FormulariosSsr.Destino(respuesta));
    }

    [Fact]
    public async Task NuevaFactura_SocioOcultoManipulado_UsaElClienteDeLaQuery()
    {
        using var app = Configurar(new BlazorSsrFactory());
        var api = app.Simular<IFacturaVentaApiClient>();
        api.Setup(c => c.CreateBorradorAsync(It.IsAny<BorradorCabeceraInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<FacturaVentaBorradorResponse>(true, "ok", new FacturaVentaBorradorResponse { Id = Guid.NewGuid() }));
        var otro = Guid.NewGuid();

        await FormulariosSsr.EnviarAsync(app.Cliente(), $"/facturas-venta/nueva?socioId={IdCliente}&tipo=3", "alta-factura",
            new Dictionary<string, string>
            {
                ["AltaInput.SocioNegocioId"] = otro.ToString(), ["AltaInput.Tipo"] = "3", ["AltaInput.Descripcion"] = "Servicio",
                ["AltaInput.FechaRegistroTexto"] = "2026-09-28",
            });

        api.Verify(c => c.CreateBorradorAsync(It.Is<BorradorCabeceraInput>(i => i.SocioNegocioId == IdCliente), It.IsAny<CancellationToken>()), Times.Once);
        api.Verify(c => c.CreateBorradorAsync(It.Is<BorradorCabeceraInput>(i => i.SocioNegocioId == otro), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("/facturas-venta/nueva")]
    [InlineData("/notas-credito-venta/nueva")]
    public async Task AltaDesdeElCliente_CancelarVuelveAlCliente(string ruta)
    {
        using var app = Configurar(new BlazorSsrFactory());

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), $"{ruta}?socioId={IdCliente}&returnUrl={Uri.EscapeDataString($"/clientes/{IdCliente}")}");

        Assert.Contains($"href=\"/clientes/{IdCliente}\" data-testid=\"cancelar\"", html);
    }

    [Fact]
    public async Task NotaCreditoNueva_ClienteNoEncontrado_SinCuadroDeFacturasNiGuid()
    {
        using var app = Configurar(new BlazorSsrFactory());
        var inexistente = Guid.NewGuid();

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), $"/notas-credito-venta/nueva?socioId={inexistente}");

        Assert.Contains("No se encontró el cliente indicado", html);
        Assert.DoesNotContain("no tiene facturas registradas con importe pendiente", html);
        Assert.DoesNotContain(inexistente.ToString(), html.Replace($"socioId={inexistente}", string.Empty, StringComparison.Ordinal));
        Assert.Contains("href=\"/notas-credito-venta/nueva\"", html);
    }

    [Fact]
    public async Task NotaCreditoNueva_SubtituloAclaraElClienteDeFacturacion()
    {
        using var app = Configurar(new BlazorSsrFactory());

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), $"/notas-credito-venta/nueva?socioId={IdCliente}");

        Assert.Contains("facturadas a este cliente", html);
    }

    [Fact]
    public async Task NotaCreditoNueva_FacturaQueNoEsDelCliente_AvisaSinLlamarALaApi()
    {
        using var app = Configurar(new BlazorSsrFactory());
        var api = app.Simular<INotaCreditoVentaApiClient>();

        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), $"/notas-credito-venta/nueva?socioId={IdCliente}", "nueva-nota-socio",
            new Dictionary<string, string> { ["NotaInput.FacturaVentaNumero"] = "FV0002", ["NotaInput.CopiarLineas"] = "true" });
        var html = HtmlSsr.Decodificar(await respuesta.Content.ReadAsStringAsync());

        Assert.Equal(System.Net.HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Contains("La factura FV0002 no está entre las facturas pendientes de este cliente", html);
        api.Verify(c => c.CreateBorradorAsync(It.IsAny<NotaCreditoBorradorInput>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NotaCreditoNueva_SinSocio_ListaClientesParaElegir()
    {
        using var app = Configurar(new BlazorSsrFactory());

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), "/notas-credito-venta/nueva");

        Assert.Contains("data-testid=\"elegir-cliente\"", html);
        Assert.Contains($"href=\"/notas-credito-venta/nueva?socioId={IdCliente}\"", html);
    }

    [Fact]
    public async Task Borradores_ConNuevaFacturaYVerCliente()
    {
        using var app = Configurar(new BlazorSsrFactory());

        var facturas = await HtmlSsr.HtmlAsync(app.Cliente(), "/facturas-venta/borradores");
        var notas = await HtmlSsr.HtmlAsync(app.Cliente(), "/notas-credito-venta/borradores");

        Assert.Contains("href=\"/facturas-venta/nueva?returnUrl=", facturas);
        Assert.Contains("data-testid=\"menu-ver\"", facturas);
        Assert.Contains("href=\"/notas-credito-venta/nueva", notas);
        Assert.Contains("data-testid=\"menu-ver\"", notas);
    }

    [Fact]
    public async Task BorradoresFactura_Liberado_SeleccionableParaVer_SinEditarNiEliminar()
    {
        using var app = Configurar(new BlazorSsrFactory());

        var liberado = await HtmlSsr.HtmlAsync(app.Cliente(), $"/facturas-venta/borradores?sel={IdBorradorLiberado}");
        var abierto = await HtmlSsr.HtmlAsync(app.Cliente(), $"/facturas-venta/borradores?sel={IdBorradorAbierto}");
        var sinSeleccion = await HtmlSsr.HtmlAsync(app.Cliente(), "/facturas-venta/borradores");

        Assert.Contains($"href=\"/facturas-venta/borradores/{IdBorradorLiberado}\"", liberado);
        Assert.Contains($"href=\"/clientes/{IdCliente}\"", liberado);
        Assert.DoesNotContain("data-testid=\"accion-editar\"", liberado);
        Assert.DoesNotContain("data-testid=\"accion-eliminar\"", liberado);

        Assert.Contains($"<a data-testid=\"accion-editar\" href=\"/facturas-venta/borradores/{IdBorradorAbierto}?returnUrl=", abierto);
        Assert.Contains($"href=\"/clientes/{IdCliente}\"", abierto);

        Assert.Contains("<span data-testid=\"accion-editar\" aria-disabled=\"true\"", sinSeleccion);
        Assert.Contains("<span data-testid=\"accion-eliminar\" aria-disabled=\"true\"", sinSeleccion);
    }

    [Fact]
    public async Task BorradoresNota_SeleccionHabilitaVerCliente()
    {
        using var app = Configurar(new BlazorSsrFactory());

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), $"/notas-credito-venta/borradores?sel={IdNota}");

        Assert.Contains($"href=\"/notas-credito-venta/borradores/{IdNota}\"", html);
        Assert.Contains($"href=\"/clientes/{IdCliente}\"", html);
        Assert.Contains("href=\"/notas-credito-venta/nueva?returnUrl=", html);
    }

    [Fact]
    public async Task EditorBorradorFactura_LaCabeceraSeEditaEnLaMismaPagina_SinRutaEditar()
    {
        using var app = Configurar(new BlazorSsrFactory());

        var html = await HtmlSsr.HtmlAsync(app.Cliente(), $"/facturas-venta/en-preparacion/{IdBorradorAbierto}");

        Assert.Contains("data-testid=\"cabecera-editable\"", html);
        Assert.DoesNotContain("/editar", html);
        Assert.DoesNotContain("editId=", html);
    }

    private static BlazorSsrFactory Configurar(BlazorSsrFactory app)
    {
        var terminoId = Guid.NewGuid();
        var cliente = new SocioNegocioResponse { Id = IdCliente, Codigo = "C0001", NombreComercial = "Comercial Uno", TerminoPagoId = terminoId, GrupoClienteContableCodigo = "GENERAL" };
        var socios = app.Simular<ISocioNegocioApiClient>();
        // Respuesta asíncrona de verdad (lección de la puerta B: el primer render ocurre antes de que termine la carga).
        socios.Setup(c => c.GetByIdAsync(IdCliente, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await Task.Yield();
                return cliente;
            });
        socios.Setup(c => c.ListAsync(It.IsAny<SocioNegocioSearchFilter?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<SocioNegocioResponse>([cliente], 1, 50, 1));
        app.Simular<ITerminoPagoApiClient>().Setup(c => c.GetByIdAsync(terminoId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TerminoPagoResponse { Id = terminoId, Codigo = "CONTADO", Descripcion = "Contado" });

        var facturas = app.Simular<IFacturaVentaApiClient>();
        facturas.Setup(c => c.ListFacturasAsync(It.IsAny<FacturaVentaSearchCriteria?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await Task.Yield();
                return new PagedResult<FacturaVentaResponse>([new FacturaVentaResponse { Numero = "FV0001", SocioNegocioId = IdCliente, NombreFacturacion = "Comercial Uno" }], 1, 50, 1);
            });
        facturas.Setup(c => c.ListBorradoresAsync(It.IsAny<FacturaVentaBorradorFiltro?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<FacturaVentaBorradorResponse>(
            [
                new FacturaVentaBorradorResponse { Id = IdBorradorAbierto, Numero = "B-1", Estado = EstadoFacturaBorrador.Abierta, SocioNegocioId = IdCliente },
                new FacturaVentaBorradorResponse { Id = IdBorradorLiberado, Numero = "B-2", Estado = EstadoFacturaBorrador.Liberada, SocioNegocioId = IdCliente },
            ], 1, 50, 2));
        facturas.Setup(c => c.GetBorradorAsync(IdBorradorAbierto, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FacturaVentaBorradorResponse { Id = IdBorradorAbierto, Numero = "B-1", Estado = EstadoFacturaBorrador.Abierta, SocioNegocioId = IdCliente });

        var notas = app.Simular<INotaCreditoVentaApiClient>();
        notas.Setup(c => c.ListBorradoresAsync(It.IsAny<NotaCreditoVentaBorradorFiltro?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<NotaCreditoVentaBorradorResponse>(
                [new NotaCreditoVentaBorradorResponse { Id = IdNota, Numero = "NC-1", FacturaVentaNumero = "FV0001", SocioNegocioId = IdCliente }], 1, 50, 1));
        notas.Setup(c => c.GetBorradorAsync(IdNota, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotaCreditoVentaBorradorResponse { Id = IdNota, Numero = "NC-1", FacturaVentaNumero = "FV0001", SocioNegocioId = IdCliente });

        app.Simular<ICobroApiClient>().Setup(c => c.ListMovimientosAbiertosAsync(IdCliente, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await Task.Yield();
                return new List<MovimientoClienteResponse>
                {
                    new() { Id = 1, TipoDocumento = TipoDocumentoCliente.Factura, NumeroDocumento = "FV0001", ImporteOriginal = 100m, ImporteRestante = 60m, Abierta = true },
                    new() { Id = 2, TipoDocumento = TipoDocumentoCliente.Pago, NumeroDocumento = "PAG-0001", ImporteOriginal = -40m, ImporteRestante = -40m, Abierta = true },
                    new() { Id = 3, TipoDocumento = TipoDocumentoCliente.Factura, NumeroDocumento = "FV0002", ImporteOriginal = 50m, ImporteRestante = 0m, Abierta = false },
                };
            });

        // R2: con un almacén para que VentasOpciones.AlmacenesAsync cargue.
        app.Simular<IAlmacenApiClient>()
            .Setup(c => c.ListAsync(It.IsAny<AlmacenSearchFilter?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<AlmacenResponse>([new AlmacenResponse { Id = Guid.NewGuid(), Codigo = "PRINC", Nombre = "Principal" }], 1, 50, 1));
        // F7: series simuladas (sin series activas): el alta y la cabecera del borrador las cargan.
        app.Simular<ISerieApiClient>()
            .Setup(c => c.ActivasDelTipoAsync(It.IsAny<TipoDocumentoSerie>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        // Alta rápida: numeración configurada y activa (sin ella, /facturas-venta/nueva muestra el aviso bloqueante).
        app.Simular<IConfiguracionNumeracionApiClient>()
            .Setup(c => c.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new ConfiguracionNumeracionResponse { TipoDocumento = TipoDocumentoSerie.BorradorFacturaVenta, SerieCodigo = "FV-BORR", SerieActiva = true },
                new ConfiguracionNumeracionResponse { TipoDocumento = TipoDocumentoSerie.FacturaVenta, SerieId = Guid.NewGuid(), SerieCodigo = "FV", SerieActiva = true },
            ]);
        return app;
    }
}
