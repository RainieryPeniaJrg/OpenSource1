extern alias BlazorApp;
using BlazorApp::OpenSource1.Blazor.Components;
using BlazorApp::OpenSource1.Blazor.Components.Documento;
using BlazorApp::OpenSource1.Blazor.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenSource1.Application.Features.ConfiguracionNumeracion.Dtos;
using OpenSource1.Application.Features.FacturasVenta.Calculo;
using OpenSource1.Application.Features.Series.Dtos;
using OpenSource1.Core.Enums;
using OpenSource1.SmokeTests.TestInfrastructure;

namespace OpenSource1.SmokeTests.Blazor;

/// <summary>
/// Componentes compartidos del documento (spec no-series, Parte 4; S11): cabecera y totales en solo lectura, información de la
/// serie de registro, selector de series (conserva la serie vigente aunque no esté entre las activas, F7) y las cargas de
/// <see cref="SeriesOpciones"/> que no rompen la página.
/// </summary>
public sealed class ComponentesDocumentoTests
{
    [Fact]
    public async Task Totales_SubtotalDescuentosItbisYTotal_ConLosGruposDeIva()
    {
        var totales = TotalesDocumento.Desde(
            new TotalesFactura([new GrupoIvaCalculado("ITBIS18", 18m, 95m, 17.10m)], 95m, 17.10m, 112.10m), descuentos: 5m);

        var html = await Renderizar<DocumentoTotales>(new() { ["Totales"] = totales });

        Assert.Contains("data-testid=\"totales\"", html);
        Assert.Contains("data-testid=\"total-subtotal\">100.00<", html);
        Assert.Contains(">Grupo</th>", html);
        Assert.Contains("data-testid=\"total-descuentos\">5.00<", html);
        Assert.Contains("data-testid=\"total-sin-iva\">95.00<", html);
        Assert.Contains("data-testid=\"total-iva\">17.10<", html);
        Assert.Contains(">112.10<", html);
        Assert.Contains("ITBIS18", html);
        // Por defecto (borradores) sin la columna de la cuenta del grupo de IVA.
        Assert.DoesNotContain(">Cuenta</th>", html);
    }

    [Fact]
    public async Task Totales_ConMostrarCuenta_ColumnaCuentaDelGrupoDeIva()
    {
        var totales = new TotalesDocumento(100m, 0m, 100m, 18m, 118m,
            [new GrupoIvaCalculado("ITBIS18", 18m, 80m, 14.40m), new GrupoIvaCalculado("ITBIS16", 16m, 20m, 3.20m)], ["2101", null]);

        var html = await Renderizar<DocumentoTotales>(new() { ["Totales"] = totales, ["MostrarCuenta"] = true });

        Assert.Contains(">Cuenta</th>", html);
        Assert.Matches("ITBIS18</td>.*?<td[^>]*>2101</td>", html);
        Assert.Matches("ITBIS16</td>.*?<td[^>]*>—</td>", html);
        var sinGrupos = await Renderizar<DocumentoTotales>(new() { ["Totales"] = new TotalesDocumento(10m, 0m, 10m, 0m, 10m, []), ["MostrarCuenta"] = true });
        Assert.Contains("colspan=\"5\"", sinGrupos);
    }

    [Fact]
    public async Task Totales_SinTotales_MensajeYTitulo()
    {
        var html = await Renderizar<DocumentoTotales>(new() { ["Totales"] = null, ["Titulo"] = "Totales (vista previa)" });

        Assert.Contains("Totales (vista previa)", html);
        Assert.Contains("No fue posible calcular los totales.", html);
        Assert.DoesNotContain("data-testid=\"total\"", html);
    }

    [Fact]
    public async Task Totales_SinGrupos_FilaSinItbis()
    {
        var html = await Renderizar<DocumentoTotales>(new() { ["Totales"] = new TotalesDocumento(10m, 0m, 10m, 0m, 10m, []) });

        Assert.Contains("Sin ITBIS.", html);
        Assert.Contains("data-testid=\"total\">10.00<", html);
    }

    [Fact]
    public async Task Cabecera_CamposConEnlaceYDetalle()
    {
        var html = await Renderizar<DocumentoCabecera>(new()
        {
            ["Campos"] = new List<CampoDocumento> { new("Vender a", "C-001 — Comercial", "/clientes/1", "RNC 1"), new("Descripción", "Texto", Ancho: true) },
        });

        Assert.Contains("data-testid=\"documento-cabecera\"", html);
        Assert.Contains("href=\"/clientes/1\"", html);
        Assert.Contains("RNC 1", html);
        Assert.Contains("Vender a", html);
        Assert.Contains("sm:col-span-2 lg:col-span-4", html);
    }

    [Fact]
    public async Task InfoDeSerie_MuestraProximoOAvisoOError()
    {
        var conProximo = await Renderizar<SerieNumeracionInfo>(new() { ["Codigo"] = "FV", ["ProximoNumero"] = "00000012", ["Aviso"] = "Quedan 3." });
        var conError = await Renderizar<SerieNumeracionInfo>(new() { ["Codigo"] = "FV", ["Error"] = "La serie 'FV' está inactiva." });

        Assert.Contains("Se numerará con la serie <strong>FV</strong> · próximo <strong>00000012</strong> (el número definitivo se asigna al registrar)", conProximo);
        Assert.Contains("Quedan 3.", conProximo);
        Assert.Contains("La serie 'FV' está inactiva.", conError);
        Assert.DoesNotContain("Se numerará", conError);
    }

    [Fact]
    public async Task InfoDeSerie_SinCodigo_Guion_SinStrongVacio()
    {
        var html = await Renderizar<SerieNumeracionInfo>(new() { ["Codigo"] = null });

        Assert.Contains("Serie de registro: <span>—</span>", html);
        Assert.DoesNotContain("<strong></strong>", html);
    }

    [Fact]
    public async Task Cabecera_CampoSeries_MuestraLaInfoDeSerieDentroDeLaCabecera()
    {
        var html = await Renderizar<DocumentoCabecera>(new()
        {
            ["Campos"] = new List<CampoDocumento>
            {
                new("Vender a", "C-001"), CamposDocumento.Series("FV", new ProximoVista("FV-000012", null, null)),
            },
        });
        var posteada = await Renderizar<DocumentoCabecera>(new() { ["Campos"] = new List<CampoDocumento> { CamposDocumento.Series("NC") } });

        Assert.Contains(">Series</p>", html);
        Assert.Contains("data-testid=\"serie-registro\"", html);
        Assert.Contains("próximo <strong>FV-000012</strong>", html);
        Assert.Contains("Serie de registro: <strong>NC</strong>", posteada);
        Assert.DoesNotContain("Se numerará", posteada);
    }

    [Fact]
    public async Task CabeceraEditable_TarjetaConCamposYGuardar_OSoloElFormularioVacio()
    {
        var modelo = new ModeloPrueba();
        RenderFragment campos = b => b.AddMarkupContent(0, "<input name=\"UpdateInput.Xmin\" value=\"5\" />");

        var visible = await Renderizar<DocumentoCabeceraEditable>(new() { ["Model"] = modelo, ["FormName"] = "guardar-cabecera", ["ChildContent"] = campos });
        var fallida = await Renderizar<DocumentoCabeceraEditable>(new()
        {
            ["Model"] = modelo, ["FormName"] = "guardar-cabecera", ["ChildContent"] = campos, ["CargaFallida"] = true, ["MensajeCargaFallida"] = "No fue posible cargar los clientes.",
        });
        var oculta = await Renderizar<DocumentoCabeceraEditable>(new() { ["Model"] = modelo, ["FormName"] = "guardar-cabecera", ["Mostrar"] = false, ["ChildContent"] = campos });

        Assert.Contains("data-testid=\"cabecera-editable\"", visible);
        // method="post" y _handler solo los emite el host SSR; aquí se comprueba el <form>. El POST real a través de este componente
        // se verificó con la página de cabecera de nota (NotaCabecera_CargaLoGuardado_YGuardaConXmin) al preparar el contrato.
        Assert.Contains("<form data-enhance", visible);
        Assert.Contains("name=\"UpdateInput.Xmin\"", visible);
        Assert.Contains("data-testid=\"guardar-cabecera\"", visible);
        Assert.Contains(">Guardar cabecera</button>", visible);
        Assert.Contains("No fue posible cargar los clientes.", fallida);
        Assert.DoesNotContain("data-testid=\"guardar-cabecera\"", fallida);
        Assert.DoesNotContain("cabecera-editable", oculta);
        Assert.DoesNotContain("UpdateInput.Xmin", oculta);
        Assert.Contains("<form></form>", oculta);
    }

    [Fact]
    public async Task AvisoPosteado_TextoFijoYEnlace()
    {
        var html = await Renderizar<DocumentoAvisoPosteado>(new() { ["Documento"] = "la factura", ["Numero"] = "00000012", ["Href"] = "/facturas-venta/00000012" });

        Assert.Contains("data-testid=\"aviso-posteada\"", html);
        Assert.Contains("Borrador posteado como la factura 00000012: es de solo lectura.", html);
        Assert.Contains("href=\"/facturas-venta/00000012\" data-testid=\"ver-posteado\">Ver la factura 00000012</a>", html);
    }

    [Fact]
    public async Task FilaFormulario_AltaYEdicion()
    {
        RenderFragment contenido = b => b.AddMarkupContent(0, "<form>x</form>");

        var alta = await Renderizar<DocumentoFilaFormulario>(new() { ["Columnas"] = 11, ["ChildContent"] = contenido });
        var edicion = await Renderizar<DocumentoFilaFormulario>(new() { ["Columnas"] = 13, ["Variante"] = VarianteFilaFormulario.Edicion, ["ChildContent"] = contenido });

        Assert.Contains("<tr data-testid=\"fila-nueva\"><td colspan=\"11\" class=\"px-4 py-4 bg-green-50/40\">", alta.Replace("\n", "").Replace("    ", ""));
        Assert.Contains("<form>x</form>", alta);
        Assert.Contains("data-testid=\"fila-edicion\"", edicion);
        Assert.Contains("colspan=\"13\"", edicion);
        Assert.Contains("bg-amber-50/40", edicion);
    }

    [Fact]
    public async Task LineasLectura_FacturaYNota_ColumnasFijas()
    {
        var producto = new LineaDocumento(10000, TipoLineaFactura.Producto, "P-001", "Tornillo", "ALM1", 2m, "UD", 50m, 5m, 95m, "ITBIS18", 18m);
        var comentario = new LineaDocumento(20000, TipoLineaFactura.Comentario, null, "Entrega parcial", null, 0m, null, 0m, 0m, 0m, null, 0m);

        var factura = await Renderizar<DocumentoLineasLectura<LineaDocumento>>(new()
        {
            ["Columnas"] = ColumnasDocumento.Factura, ["Lineas"] = new List<LineaDocumento> { producto, comentario },
        });
        var nota = await Renderizar<DocumentoLineasLectura<LineaDocumento>>(new()
        {
            ["Columnas"] = ColumnasDocumento.Nota, ["Lineas"] = new List<LineaDocumento> { producto with { Devolucion = true } },
        });
        var vacia = await Renderizar<DocumentoLineasLectura<LineaDocumento>>(new()
        {
            ["Columnas"] = ColumnasDocumento.Factura, ["Lineas"] = new List<LineaDocumento>(), ["TextoVacio"] = "La factura no tiene líneas.",
        });

        Assert.Equal(10, ColumnasDocumento.Factura.Count);
        Assert.Equal(11, ColumnasDocumento.Nota.Count);
        Assert.Contains("data-testid=\"lineas-documento\"", factura);
        Assert.Contains(">Producto / Cuenta</th>", factura);
        Assert.Contains(">95.00<", factura);
        Assert.Contains("ITBIS18 18%", factura);
        Assert.Contains("<span class=\"ml-1 text-xs text-slate-400\">UD</span>", factura);
        Assert.Contains("Entrega parcial", factura);
        Assert.Contains("2 línea(s).", factura);
        Assert.DoesNotContain(">Devolución</th>", factura);
        Assert.Contains(">Devolución</th>", nota);
        Assert.Contains(">Sí<", nota);
        Assert.Contains("colspan=\"10\"", vacia);
        Assert.Contains("La factura no tiene líneas.", vacia);
    }

    [Fact]
    public async Task Selector_ConservaLaSerieVigenteAunqueNoEsteEntreLasActivas()
    {
        var vigente = Guid.NewGuid();
        var otra = Serie("FV2", "Facturas 2", "FV2-0001");

        var html = await Renderizar<SelectorSerie>(new()
        {
            ["Id"] = "serie-registro", ["Name"] = "Input.SerieRegistroId", ["Series"] = new List<SerieResponse> { otra },
            ["Seleccion"] = vigente, ["VigenteId"] = vigente, ["VigenteCodigo"] = "FV",
        });
        var sinCarga = await Renderizar<SelectorSerie>(new()
        {
            ["Id"] = "serie-registro", ["Name"] = "Input.SerieRegistroId", ["Series"] = new List<SerieResponse>(),
            ["Seleccion"] = vigente, ["VigenteId"] = vigente, ["VigenteCodigo"] = "FV", ["CargaFallida"] = true,
        });

        Assert.Contains("name=\"Input.SerieRegistroId\"", html);
        Assert.Contains($"<option value=\"{vigente}\" selected=\"selected\">FV (no disponible)</option>", html);
        Assert.Contains($"<option value=\"{otra.Id}\">FV2 — Facturas 2 (próximo FV2-0001)</option>", html);
        Assert.Contains($"<option value=\"{vigente}\" selected=\"selected\">FV (actual)</option>", sinCarga);
    }

    [Fact]
    public async Task Selector_SinSeleccion_ConservaLaVigente()
    {
        var vigente = Serie("FV", "Facturas", null);
        var otra = Serie("FV2", "Facturas 2", null);

        var html = await Renderizar<SelectorSerie>(new()
        {
            ["Id"] = "s", ["Name"] = "UpdateInput.SerieRegistroId", ["Series"] = new List<SerieResponse> { otra, vigente }, ["VigenteId"] = vigente.Id,
        });

        Assert.Matches($"<option value=\"{vigente.Id}\" selected[^>]*>FV — Facturas</option>", html);
        Assert.Contains($"<option value=\"{otra.Id}\">FV2", html);
    }

    [Fact]
    public async Task Selector_OpcionConfiguradaSeleccionadaSinSeleccion()
    {
        var serie = Serie("NC", "Notas", null);

        var html = await Renderizar<SelectorSerie>(new()
        {
            ["Id"] = "serie-borrador", ["Name"] = "AddInput.SerieBorradorId", ["Series"] = new List<SerieResponse> { serie }, ["OpcionConfigurada"] = true,
        });

        // El renderizador emite value="" y un bool true como atributos sin valor (el POST envía "" = la configurada).
        Assert.Contains("<option value selected>— La configurada —</option>", html);
        Assert.Contains($"<option value=\"{serie.Id}\">NC — Notas</option>", html);
    }

    [Fact]
    public async Task SeriesOpciones_FalloDeCarga_ListaVaciaConCargaFallida_YProximoConError()
    {
        var cliente = new Mock<ISerieApiClient>();
        cliente.Setup(c => c.ActivasDelTipoAsync(TipoDocumentoSerie.FacturaVenta, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("caída"));
        cliente.Setup(c => c.ProximoAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<ProximoNumeroResponse>(false, "La serie 'FV' está inactiva."));

        var series = await SeriesOpciones.ActivasAsync(cliente.Object, TipoDocumentoSerie.FacturaVenta, NullLogger.Instance);
        var proximo = await SeriesOpciones.ProximoAsync(cliente.Object, Guid.NewGuid(), NullLogger.Instance);

        Assert.True(series.CargaFallida);
        Assert.Empty(series.Items);
        Assert.Equal(new ProximoVista(null, null, "La serie 'FV' está inactiva."), proximo);
    }

    [Fact]
    public async Task SeriesOpciones_ProximoConAviso()
    {
        var cliente = new Mock<ISerieApiClient>();
        cliente.Setup(c => c.ProximoAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<ProximoNumeroResponse>(true, "ok", new ProximoNumeroResponse("FV-000012", "Quedan 3.")));
        var roto = new Mock<ISerieApiClient>();
        roto.Setup(c => c.ProximoAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("caída"));

        Assert.Equal(new ProximoVista("FV-000012", "Quedan 3.", null), await SeriesOpciones.ProximoAsync(cliente.Object, Guid.NewGuid(), NullLogger.Instance));
        Assert.Equal("No fue posible calcular el próximo número.", (await SeriesOpciones.ProximoAsync(roto.Object, Guid.NewGuid(), NullLogger.Instance)).Error);

        // Un 400 trae el motivo real en Errors ("Revise los datos:" en Message): la vista muestra el motivo.
        var invalida = new Mock<ISerieApiClient>();
        invalida.Setup(c => c.ProximoAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<ProximoNumeroResponse>(false, "Revise los datos:", Errors: ["No hay una línea vigente de la serie 'FV'."]));
        Assert.Equal("No hay una línea vigente de la serie 'FV'.", (await SeriesOpciones.ProximoAsync(invalida.Object, Guid.NewGuid(), NullLogger.Instance)).Error);
    }

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

    [Fact]
    public void Estados_IncluyenPosteada()
    {
        Assert.Equal("Posteada", VentasOpciones.Estado(EstadoFacturaBorrador.Posteada));
        Assert.Equal("Abierta", VentasOpciones.EstadoNota(EstadoNotaCreditoBorrador.Abierta));
        Assert.Equal("Posteada", VentasOpciones.EstadoNota(EstadoNotaCreditoBorrador.Posteada));
    }

    private sealed class ModeloPrueba
    {
        public long Xmin { get; set; }
    }

    private static SerieResponse Serie(string codigo, string descripcion, string? proximo) => new()
    {
        Id = Guid.NewGuid(), Codigo = codigo, Descripcion = descripcion, TipoDocumento = TipoDocumentoSerie.FacturaVenta, Activa = true, ProximoNumero = proximo,
    };

    private static async Task<string> Renderizar<T>(Dictionary<string, object?> parametros) where T : Microsoft.AspNetCore.Components.IComponent =>
        HtmlSsr.Decodificar(await RenderizadorComponentes.RenderizarAsync<T>(PermisosTestAuthHandler.Principal("Administrador"), parametros));
}
