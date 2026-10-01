extern alias BlazorApp;
using System.Net;
using BlazorApp::OpenSource1.Blazor.Services;
using Moq;
using OpenSource1.Application.Features.Almacenes.Dtos;
using OpenSource1.Application.Features.ConfiguracionNumeracion.Dtos;
using OpenSource1.Application.Features.FacturasVenta.Borradores.Dtos;
using OpenSource1.Application.Features.FacturasVenta.Calculo;
using OpenSource1.Application.Features.FacturasVenta.Posteo;
using OpenSource1.Application.Features.Productos.Dtos;
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
    private static readonly Guid IdLinea = Guid.Parse("7d0c9a51-0000-0000-0000-0000000006a7");
    private static readonly Guid IdProducto = Guid.Parse("7d0c9a51-0000-0000-0000-0000000006a8");
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
        Assert.DoesNotContain("name=\"UpdateInput.SerieBorradorId\"", html);
        // "Volver a editar" solo para un documento heredado Liberada; nunca "Liberar".
        Assert.DoesNotContain("data-testid=\"volver-a-editar\"", html);
        Assert.DoesNotContain("data-testid=\"aviso-liberada\"", html);
        Assert.Contains("data-testid=\"guardar-despues\"", html);
    }

    [Fact]
    public async Task EnPreparacion_RegistroFallido_UnSoloResumenDeErrores_DentroDelDialogo_YEnlazaLaLinea()
    {
        using var app = Configurar(new BlazorSsrFactory());
        app.Simular<IFacturaVentaApiClient>()
            .Setup(c => c.PostearAsync(IdBorrador, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<ResultadoPosteoFactura>(
                false, "No se pudo registrar.", Errors: ["Línea 10000: La existencia del producto P1 es insuficiente."]));

        var dialogo = await FormulariosSsr.EnviarAsync(app.Cliente(), $"{Url(IdBorrador)}?registrar=true", "confirm-postear", new Dictionary<string, string>());
        var pagina = await FormulariosSsr.EnviarAsync(app.Cliente(), Url(IdBorrador), "confirm-postear", new Dictionary<string, string>());
        var htmlDialogo = HtmlSsr.Decodificar(await dialogo.Content.ReadAsStringAsync());
        var htmlPagina = HtmlSsr.Decodificar(await pagina.Content.ReadAsStringAsync());

        Assert.Equal(1, Ocurrencias(htmlDialogo, "data-testid=\"resumen-errores\""));
        Assert.Contains(">Sí, registrar</button>", htmlDialogo);
        Assert.Contains("href=\"#linea-10000\"", htmlDialogo);
        Assert.Contains("id=\"linea-10000\"", htmlDialogo);
        Assert.Equal(1, Ocurrencias(htmlPagina, "data-testid=\"resumen-errores\""));
        Assert.Contains("No se pudo registrar.", htmlPagina);
    }

    [Fact]
    public async Task EnPreparacion_Administrador_GuardaSinSerieDeBorradorNiAlmacen()
    {
        using var app = Configurar(new BlazorSsrFactory());
        var api = app.Simular<IFacturaVentaApiClient>();
        api.Setup(c => c.UpdateBorradorAsync(IdBorrador, It.IsAny<BorradorCabeceraInput>(), 11, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<FacturaVentaBorradorResponse>(true, "ok"));
        var campos = new Dictionary<string, string>(Cabecera(""))
        {
            ["UpdateInput.SerieBorradorId"] = IdSerieOtra.ToString(), ["UpdateInput.AlmacenId"] = Guid.NewGuid().ToString(),
            ["UpdateInput.SocioNegocioId"] = Guid.NewGuid().ToString(),
        };

        await FormulariosSsr.EnviarAsync(app.Cliente(), Url(IdBorrador), "guardar-cabecera", campos);

        api.Verify(c => c.UpdateBorradorAsync(IdBorrador, It.Is<BorradorCabeceraInput>(i =>
            i.SerieBorradorId == null && i.AlmacenId == null && i.SocioNegocioId == IdSocio && i.PrimeraLinea == null), 11, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ----- Follow-up R9: errores de validación del formulario en el resumen (R9-C1) y "Más opciones" siempre visible (R9-C3) -----

    private const string PrecioObligatorio = "El precio unitario es obligatorio (para regalar, use 100 % de descuento).";

    [Fact]
    public async Task Nueva_PrecioVacio_ResumenDeErroresConElMensaje_SinLlamarALaApi()
    {
        using var app = Configurar(new BlazorSsrFactory());
        var api = app.Simular<IFacturaVentaApiClient>();

        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), $"{Nueva}?socioId={IdSocio}&tipo=1&addProductoId={IdProducto}", "alta-factura",
            new Dictionary<string, string>
            {
                ["AltaInput.SocioNegocioId"] = IdSocio.ToString(), ["AltaInput.Tipo"] = "1", ["AltaInput.CantidadTexto"] = "1", ["AltaInput.PrecioUnitarioTexto"] = "",
            });
        var html = HtmlSsr.Decodificar(await respuesta.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var resumen = Resumen(html);
        Assert.Contains("role=\"alert\"", resumen);
        Assert.Contains($"<li>{PrecioObligatorio}</li>", resumen);
        Assert.Equal(1, Ocurrencias(html, "data-testid=\"resumen-errores\""));
        api.Verify(c => c.CreateBorradorAsync(It.IsAny<BorradorCabeceraInput>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnPreparacion_AgregarLineaConPrecioVacio_ResumenDeErrores()
    {
        using var app = Configurar(new BlazorSsrFactory());

        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), $"{Url(IdBorrador)}?tipo=1&addProductoId={IdProducto}", "add-linea",
            new Dictionary<string, string> { ["AddInput.Tipo"] = "1", ["AddInput.CantidadTexto"] = "1", ["AddInput.PrecioUnitarioTexto"] = "" });
        var html = HtmlSsr.Decodificar(await respuesta.Content.ReadAsStringAsync());

        var resumen = Resumen(html);
        Assert.Contains("role=\"alert\"", resumen);
        Assert.Contains($"<li>{PrecioObligatorio}</li>", resumen);
        Assert.Equal(1, Ocurrencias(html, "data-testid=\"resumen-errores\""));
    }

    [Fact]
    public async Task EnPreparacion_ModificarLineaInvalida_ResumenConEnlaceALaFila()
    {
        using var app = Configurar(new BlazorSsrFactory());

        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), $"{Url(IdBorrador)}?editLineaId={IdLinea}", "update-linea",
            new Dictionary<string, string> { ["UpdateInputLinea.Tipo"] = "3", ["UpdateInputLinea.Descripcion"] = "", ["UpdateInputLinea.Xmin"] = "3" });
        var html = HtmlSsr.Decodificar(await respuesta.Content.ReadAsStringAsync());

        var resumen = Resumen(html);
        Assert.Contains("<a class=\"underline\" href=\"#linea-10000\">Línea 10000: La línea de comentario requiere una descripción.</a>", resumen);
        Assert.Contains("id=\"linea-10000\" data-testid=\"fila-edicion\"", html);
    }

    [Fact]
    public async Task EnPreparacion_CabeceraSinFechaDeRegistro_ResumenDeErrores()
    {
        using var app = Configurar(new BlazorSsrFactory());
        var campos = new Dictionary<string, string>(Cabecera("")) { ["UpdateInput.FechaRegistroTexto"] = "" };

        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), Url(IdBorrador), "guardar-cabecera", campos);
        var html = HtmlSsr.Decodificar(await respuesta.Content.ReadAsStringAsync());

        Assert.Contains("<li>La fecha de registro es obligatoria al modificar.</li>", Resumen(html));
    }

    [Theory]
    [InlineData("tipo=1")]
    [InlineData("tipo=1&addProductoQuery=uno")]
    [InlineData("tipo=2")]
    [InlineData("tipo=3")]
    public async Task Nueva_MasOpciones_SoloElAdministrador_EnCualquierTipoDeLinea(string query)
    {
        using var app = Configurar(new BlazorSsrFactory());

        var administrador = await HtmlSsr.HtmlAsync(app.Cliente(), $"{Nueva}?socioId={IdSocio}&{query}");
        var ejecutor = await HtmlSsr.HtmlAsync(app.Cliente("Ejecutor"), $"{Nueva}?socioId={IdSocio}&{query}");

        Assert.Contains("data-testid=\"mas-opciones\"", administrador);
        Assert.Contains("name=\"serieRegistroId\"", administrador);
        Assert.DoesNotContain("data-testid=\"mas-opciones\"", ejecutor);
        Assert.DoesNotContain("serieRegistroId", ejecutor);
    }

    [Fact]
    public async Task Nueva_SerieElegida_ViajaEnLosEnlacesYEnElAlta_SoloParaElAdministrador()
    {
        using var app = Configurar(new BlazorSsrFactory());
        var api = app.Simular<IFacturaVentaApiClient>();
        api.Setup(c => c.CreateBorradorAsync(It.IsAny<BorradorCabeceraInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<FacturaVentaBorradorResponse>(true, "ok", new FacturaVentaBorradorResponse { Id = IdNuevo }));
        var ruta = $"{Nueva}?socioId={IdSocio}&tipo=3&serieRegistroId={IdSerieOtra}";

        var administrador = await HtmlSsr.HtmlAsync(app.Cliente(), ruta);
        var ejecutor = await HtmlSsr.HtmlAsync(app.Cliente("Ejecutor"), ruta);
        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), ruta, "alta-factura", new Dictionary<string, string>
        {
            ["AltaInput.SocioNegocioId"] = IdSocio.ToString(), ["AltaInput.Tipo"] = "3", ["AltaInput.Descripcion"] = "Servicio",
            ["AltaInput.SerieRegistroId"] = IdSerieOtra.ToString(),
        });

        Assert.Contains($"<input type=\"hidden\" name=\"AltaInput.SerieRegistroId\" value=\"{IdSerieOtra}\"", administrador);
        Assert.Contains($"<option value=\"{IdSerieOtra}\" selected", administrador);
        Assert.Contains($"href=\"{Nueva}?socioId={IdSocio}&tipo=1&serieRegistroId={IdSerieOtra}\"", administrador);
        Assert.DoesNotContain("serieRegistroId", ejecutor.Replace(ruta, string.Empty, StringComparison.Ordinal));
        Assert.DoesNotContain("name=\"AltaInput.SerieRegistroId\"", ejecutor);
        Assert.Equal($"{Url(IdNuevo)}?tipo=3&ok=linea_creada", FormulariosSsr.Destino(respuesta));
        api.Verify(c => c.CreateBorradorAsync(It.Is<BorradorCabeceraInput>(i => i.SerieRegistroId == IdSerieOtra), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static string Resumen(string html)
    {
        var inicio = html.IndexOf("data-testid=\"resumen-errores\"", StringComparison.Ordinal);
        Assert.True(inicio >= 0, "Sin resumen de errores.");
        var fin = html.IndexOf("</div>", inicio, StringComparison.Ordinal);
        return html[html.LastIndexOf('<', inicio)..fin];
    }

    private static int Ocurrencias(string html, string texto)
    {
        var total = 0;
        for (var i = html.IndexOf(texto, StringComparison.Ordinal); i >= 0; i = html.IndexOf(texto, i + texto.Length, StringComparison.Ordinal))
        {
            total++;
        }

        return total;
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
    public async Task EnPreparacion_Liberada_SerieQueYaNoNumera_RegistrarDeshabilitadoConMotivo()
    {
        using var app = Configurar(new BlazorSsrFactory(), proximoError: "La serie 'FV' está inactiva.");

        var html = HtmlSsr.Decodificar(await HtmlSsr.HtmlAsync(app.Cliente(), Url(IdLiberada)));

        var aviso = html[html.IndexOf("data-testid=\"aviso-liberada\"", StringComparison.Ordinal)..];
        Assert.Contains("<button type=\"button\" disabled data-testid=\"registrar\"", aviso);
        Assert.Contains("No se puede registrar: La serie 'FV' está inactiva.", aviso);
        Assert.DoesNotContain($"href=\"{Url(IdLiberada)}?registrar=true\"", html);
        Assert.Contains(">Volver a editar</button>", aviso);
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
                Id = IdLinea, NumeroLinea = 10000, Tipo = TipoLineaFactura.Comentario, Descripcion = "Servicio", Xmin = 3,
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
        app.Simular<IProductoApiClient>()
            .Setup(c => c.GetByIdAsync(IdProducto, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProductoResponse { Id = IdProducto, Codigo = "P-1", Nombre = "Producto uno", PrecioVenta = 10m, UnidadMedidaBaseId = Guid.NewGuid() });
        app.Simular<IAlmacenApiClient>()
            .Setup(c => c.ListAsync(It.IsAny<AlmacenSearchFilter?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<AlmacenResponse>([new AlmacenResponse { Id = Guid.NewGuid(), Codigo = "PRINC", Nombre = "Principal", EsPredeterminado = true }], 1, 50, 1));
        return app;
    }
}
