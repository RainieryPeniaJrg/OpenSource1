using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using OpenSource1.Application.Features.FacturasVenta.Borradores.Dtos;
using OpenSource1.Application.Features.FacturasVenta.Calculo;
using OpenSource1.Core.Common;
using OpenSource1.Core.Entities;
using OpenSource1.Core.Entities.Contabilidad;
using OpenSource1.Core.Entities.Ventas;
using OpenSource1.Core.Enums;
using OpenSource1.SmokeTests.TestInfrastructure;

namespace OpenSource1.SmokeTests.Api;

/// <summary>
/// Borradores de factura de venta (Task 6.2) contra Postgres real: snapshot del cliente y grupos congelados, vencimiento,
/// líneas por tipo con IVA y factor congelados, importes, liberar/reabrir, xmin, rutas 404 y roles. REQUIERE DOCKER.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FacturasVentaBorradoresApiTests : IClassFixture<PostgresTestFixture>
{
    private const string Base = "/api/facturas-venta";
    private static readonly DateOnly D10 = new(2026, 9, 10);

    private readonly PostgresTestFixture _fixture;
    private readonly HttpClient _client;

    public FacturasVentaBorradoresApiTests(PostgresTestFixture fixture)
    {
        _fixture = fixture;
        var factory = fixture.CreateFactory();
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    // ----- Series -----

    [Fact]
    public async Task Series_FvBorrYFv_SembradasConOchoDigitos()
    {
        Admin();
        await using var conexion = new NpgsqlConnection(_fixture.AppConnectionString);
        var filas = (await conexion.QueryAsync<(string Codigo, bool PermiteHuecos, string NumeroInicial)>(
            """
            SELECT s."Codigo", s."PermiteHuecos", l."NumeroInicial"
            FROM "Series" s JOIN "LineasSerie" l ON l."SerieId" = s."Id"
            WHERE s."Codigo" IN ('FV-BORR', 'FV') ORDER BY s."Codigo"
            """)).ToList();

        Assert.Equal([("FV", false, "00000001"), ("FV-BORR", true, "00000001")], filas);
    }

    // ----- Cabecera -----

    [Fact]
    public async Task Alta_TomaSnapshotDelFacturarA_GruposCongelados_Vencimiento_AlmacenPredeterminado_YNumero()
    {
        var client = Admin();
        var termino = await CrearTerminoAsync(client, 30);
        var socio = await CrearSocioAsync(client, "Cliente Snapshot", new
        {
            razonSocial = "Cliente Snapshot SRL",
            tipoDocumentoFiscal = TipoDocumentoFiscal.Rnc,
            numeroDocumentoFiscal = Unico("1"),
            direccionLinea1 = "Calle 1",
            direccionLinea2 = "Apto 2",
            ciudad = "Santiago",
            paisCodigo = "DO",
            terminoPagoId = termino
        });

        var borrador = await CrearBorradorOkAsync(client, new { socioNegocioId = socio, fechaRegistro = D10, fechaDocumento = D10 });

        Assert.Matches("^[0-9]{8}$", borrador.Numero);
        Assert.Equal((socio, socio), (borrador.SocioNegocioId, borrador.SocioNegocioFacturarAId));
        Assert.Equal("Cliente Snapshot", borrador.NombreFacturacion);
        Assert.Equal("Cliente Snapshot SRL", borrador.RazonSocialFacturacion);
        Assert.Equal(TipoDocumentoFiscal.Rnc, borrador.TipoDocumentoFiscal);
        Assert.Equal(("Calle 1", "Apto 2", "Santiago", "DO"),
            (borrador.DireccionFacturacionLinea1, borrador.DireccionFacturacionLinea2, borrador.CiudadFacturacion, borrador.PaisCodigoFacturacion));
        Assert.Equal(termino, borrador.TerminoPagoId);
        Assert.Equal(D10.AddDays(30), borrador.FechaVencimiento);
        Assert.Equal(
            (GrupoContableIds.NegocioNacional, GrupoContableIds.IvaNegocioItbis18, GrupoContableIds.ClienteContableGeneral),
            (borrador.GrupoNegocioId, borrador.GrupoIvaNegocioId, borrador.GrupoClienteContableId));
        Assert.Equal(AlmacenIds.Principal, borrador.AlmacenId);
        Assert.Equal((EstadoFacturaBorrador.Abierta, "DOP", 0), (borrador.Estado, borrador.Moneda, borrador.NumeroLineas));
        Assert.True(borrador.Xmin > 0);

        // Numeración consecutiva de FV-BORR.
        var otro = await CrearBorradorOkAsync(client, new { socioNegocioId = socio });
        Assert.Equal(long.Parse(borrador.Numero) + 1, long.Parse(otro.Numero));
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), otro.FechaRegistro);
        Assert.Equal(otro.FechaRegistro, otro.FechaDocumento);

        // Snapshot y grupos congelados: cambiar el socio después no altera el borrador.
        await EjecutarSqlAsync(
            """UPDATE "SociosNegocio" SET "NombreComercial" = 'Renombrado', "GrupoNegocioId" = @G WHERE "Id" = @Id""",
            new { Id = socio, G = GrupoContableIds.NegocioExterior });
        var releido = await GetBorradorAsync(client, borrador.Id);
        Assert.Equal("Cliente Snapshot", releido.NombreFacturacion);
        Assert.Equal(GrupoContableIds.NegocioNacional, releido.GrupoNegocioId);
    }

    [Fact]
    public async Task Alta_FacturarADistinto_SnapshotYClienteContableDelFacturarA_GruposDeNegocioDelVenderA()
    {
        var client = Admin();
        var grupoCliente = await CrearGrupoClienteContableAsync(client);
        var venderA = await CrearSocioAsync(client, "Vende A", new
        {
            grupoNegocioId = GrupoContableIds.NegocioExterior,
            grupoIvaNegocioId = GrupoContableIds.IvaNegocioExento
        });
        var facturarA = await CrearSocioAsync(client, "Factura A", new { grupoClienteContableId = grupoCliente });

        var borrador = await CrearBorradorOkAsync(client, new { socioNegocioId = venderA, socioNegocioFacturarAId = facturarA });

        Assert.Equal("Factura A", borrador.NombreFacturacion);
        Assert.Equal(
            (GrupoContableIds.NegocioExterior, GrupoContableIds.IvaNegocioExento, grupoCliente),
            (borrador.GrupoNegocioId, borrador.GrupoIvaNegocioId, borrador.GrupoClienteContableId));
        // Sin término de pago: vence el mismo día del documento.
        Assert.Null(borrador.TerminoPagoId);
        Assert.Equal(borrador.FechaDocumento, borrador.FechaVencimiento);
    }

    [Fact]
    public async Task Alta_SociosInvalidos_GruposFaltantes_YFechas_Devuelven400()
    {
        var client = Admin();

        await AssertErrorAsync(await client.PostAsJsonAsync($"{Base}/borradores", new { socioNegocioId = Guid.NewGuid() }),
            HttpStatusCode.BadRequest, "no existe o está bloqueado", "SocioNegocioId");

        var bloqueado = await CrearSocioAsync(client, "Bloqueado", new { bloqueado = BloqueoSocioNegocio.Todo });
        await AssertErrorAsync(await client.PostAsJsonAsync($"{Base}/borradores", new { socioNegocioId = bloqueado }),
            HttpStatusCode.BadRequest, "no existe o está bloqueado", "SocioNegocioId");

        // Bloqueado solo para facturación: el borrador se admite (el posteo, Task 6.4, lo rechazará).
        var soloFacturacion = await CrearSocioAsync(client, "Solo facturación", new { bloqueado = BloqueoSocioNegocio.Facturacion });
        await CrearBorradorOkAsync(client, new { socioNegocioId = soloFacturacion });

        var ok = await CrearSocioAsync(client, "Ok");
        await AssertErrorAsync(
            await client.PostAsJsonAsync($"{Base}/borradores", new { socioNegocioId = ok, socioNegocioFacturarAId = bloqueado }),
            HttpStatusCode.BadRequest, "no existe o está bloqueado", "SocioNegocioFacturarAId");

        var sinGrupoNegocio = await CrearSocioAsync(client, "Sin grupo negocio");
        await EjecutarSqlAsync("""UPDATE "SociosNegocio" SET "GrupoNegocioId" = NULL WHERE "Id" = @Id""", new { Id = sinGrupoNegocio });
        await AssertErrorAsync(await client.PostAsJsonAsync($"{Base}/borradores", new { socioNegocioId = sinGrupoNegocio }),
            HttpStatusCode.BadRequest, "grupo de negocio", "GrupoNegocioId");

        var sinGrupoIva = await CrearSocioAsync(client, "Sin grupo IVA");
        await EjecutarSqlAsync("""UPDATE "SociosNegocio" SET "GrupoIvaNegocioId" = NULL WHERE "Id" = @Id""", new { Id = sinGrupoIva });
        await AssertErrorAsync(await client.PostAsJsonAsync($"{Base}/borradores", new { socioNegocioId = sinGrupoIva }),
            HttpStatusCode.BadRequest, "grupo de IVA de negocio", "GrupoIvaNegocioId");

        // El grupo contable de cliente se toma del FACTURAR-A.
        var sinCliente = await CrearSocioAsync(client, "Sin cliente contable");
        await EjecutarSqlAsync("""UPDATE "SociosNegocio" SET "GrupoClienteContableId" = NULL WHERE "Id" = @Id""", new { Id = sinCliente });
        await AssertErrorAsync(
            await client.PostAsJsonAsync($"{Base}/borradores", new { socioNegocioId = ok, socioNegocioFacturarAId = sinCliente }),
            HttpStatusCode.BadRequest, "grupo contable de cliente", "GrupoClienteContableId");

        await AssertErrorAsync(
            await client.PostAsJsonAsync($"{Base}/borradores", new { socioNegocioId = ok, fechaDocumento = D10, fechaVencimiento = D10.AddDays(-1) }),
            HttpStatusCode.BadRequest, "vencimiento", "FechaVencimiento");

        await AssertErrorAsync(
            await client.PostAsJsonAsync($"{Base}/borradores", new { socioNegocioId = ok, almacenId = Guid.NewGuid() }),
            HttpStatusCode.BadRequest, "almacén", "AlmacenId");
    }

    [Fact]
    public async Task Modificar_RecalculaVencimiento_RetomaSnapshotYGrupos_RecalculaIvaDeLineas_Y409ConXminViejo()
    {
        var client = Admin();
        var termino = await CrearTerminoAsync(client, 15);
        var socio = await CrearSocioAsync(client, "Original", new { terminoPagoId = termino });
        var exento = await CrearSocioAsync(client, "Exento", new { grupoIvaNegocioId = GrupoContableIds.IvaNegocioExento });
        var producto = await CrearProductoAsync(client, 100m);
        var borrador = await CrearBorradorOkAsync(client, new { socioNegocioId = socio, fechaRegistro = D10, fechaDocumento = D10 });
        var linea = await CrearLineaOkAsync(client, borrador.Id, LineaProducto(producto, 1m));
        Assert.Equal(("ITBIS18", 18m), (linea.IdentificadorIva, linea.PorcentajeIva));

        // Cambiar la fecha de documento sin enviar vencimiento -> se recalcula con el término (15 días).
        var put = await client.PutAsJsonAsync($"{Base}/borradores/{borrador.Id}", new { xmin = borrador.Xmin, fechaDocumento = D10.AddDays(5) });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var modificado = (await put.Content.ReadFromJsonAsync<FacturaVentaBorradorResponse>())!;
        Assert.Equal((D10, D10.AddDays(5), D10.AddDays(20)), (modificado.FechaRegistro, modificado.FechaDocumento, modificado.FechaVencimiento));
        Assert.Equal("Original", modificado.NombreFacturacion); // null = conservar

        // Xmin viejo -> 409.
        var conflicto = await client.PutAsJsonAsync($"{Base}/borradores/{borrador.Id}", new { xmin = borrador.Xmin, descripcion = "x" });
        Assert.Equal(HttpStatusCode.Conflict, conflicto.StatusCode);

        // Cambiar el socio -> nuevo snapshot, término (ninguno: vence el día del documento) y grupos; el IVA de las líneas se
        // recalcula con el nuevo grupo de IVA de negocio (EXENTO × ITBIS18 = EXENTO 0 %).
        var cambio = await client.PutAsJsonAsync(
            $"{Base}/borradores/{borrador.Id}", new { xmin = modificado.Xmin, socioNegocioId = exento, socioNegocioFacturarAId = exento });
        Assert.Equal(HttpStatusCode.OK, cambio.StatusCode);
        var cambiado = (await cambio.Content.ReadFromJsonAsync<FacturaVentaBorradorResponse>())!;
        Assert.Equal(("Exento", exento, GrupoContableIds.IvaNegocioExento), (cambiado.NombreFacturacion, cambiado.SocioNegocioId, cambiado.GrupoIvaNegocioId));
        Assert.Null(cambiado.TerminoPagoId);
        Assert.Equal(cambiado.FechaDocumento, cambiado.FechaVencimiento);
        var lineaRecalculada = await GetLineaAsync(client, linea.Id);
        Assert.Equal(("EXENTO", 0m), (lineaRecalculada.IdentificadorIva, lineaRecalculada.PorcentajeIva));

        // Descripción "" = limpiar; roles: Ejecutor no puede modificar.
        Assert.Equal(HttpStatusCode.Forbidden, (await Rol("Ejecutor").PutAsJsonAsync(
            $"{Base}/borradores/{borrador.Id}", new { xmin = cambiado.Xmin, descripcion = "No" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Rol("Supervisor").PutAsJsonAsync(
            $"{Base}/borradores/{borrador.Id}", new { xmin = cambiado.Xmin, descripcion = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin().PutAsJsonAsync(
            $"{Base}/borradores/{Guid.NewGuid()}", new { xmin = 1L })).StatusCode);
    }

    // ----- Líneas -----

    [Fact]
    public async Task LineaProducto_PrecioPorDefecto_GruposEIvaCongelados_NumeroLinea_EImportesConDescuento()
    {
        var client = Admin();
        var borrador = await CrearBorradorOkAsync(client, new { socioNegocioId = await CrearSocioAsync(client, "Líneas") });
        var producto = await CrearProductoAsync(client, 100m);

        var primera = await CrearLineaOkAsync(client, borrador.Id, LineaProducto(producto, 3m));

        Assert.Equal((10000, TipoLineaFactura.Producto), (primera.NumeroLinea, primera.Tipo));
        Assert.Equal((100m, 300m, 0m), (primera.PrecioUnitario, primera.ImporteLinea, primera.ImporteDescuentoLinea));
        Assert.Equal((AlmacenIds.Principal, 1m), (primera.AlmacenId!.Value, primera.CantidadPorUnidadMedida));
        Assert.Equal("UND", primera.UnidadMedidaCodigo);
        Assert.Equal(
            (GrupoContableIds.ProductoBienes, GrupoContableIds.IvaProductoItbis18, GrupoContableIds.InventarioGeneral),
            (primera.GrupoProductoId!.Value, primera.GrupoIvaProductoId!.Value, primera.GrupoInventarioId!.Value));
        Assert.Equal(("ITBIS18", 18m), (primera.IdentificadorIva, primera.PorcentajeIva));
        Assert.StartsWith("Producto factura", primera.Descripcion);

        // 3 × 33.335 = 100.005 -> ROUND 100.01; descuento ROUND(100.005 × 10 %, 2) = 10.00; importe neto 90.01.
        var segunda = await CrearLineaOkAsync(client, borrador.Id, new
        {
            tipo = TipoLineaFactura.Producto, productoId = producto, cantidad = 3m, precioUnitario = 33.335m, porcentajeDescuentoLinea = 10m
        });
        Assert.Equal((20000, 10.00m, 90.01m), (segunda.NumeroLinea, segunda.ImporteDescuentoLinea, segunda.ImporteLinea));

        // Unidad CJA (factor 12): factor congelado y precio por defecto = PrecioVenta × factor.
        await EjecutarSqlAsync(
            """
            INSERT INTO "UnidadesMedidaProducto" ("Id", "ProductoId", "UnidadMedidaId", "CantidadPorUnidadMedida", "CreatedAtUtc", "CreatedBy", "IsDeleted")
            SELECT gen_random_uuid(), @P, "Id", 12, now(), 'test', false FROM "UnidadesMedida" WHERE "Codigo" = 'CJA'
            """, new { P = producto });
        var cja = await UnidadAsync("CJA");
        var tercera = await CrearLineaOkAsync(client, borrador.Id, new
        {
            tipo = TipoLineaFactura.Producto, productoId = producto, unidadMedidaId = cja, cantidad = 2m
        });
        Assert.Equal((12m, 1200m, 2400m), (tercera.CantidadPorUnidadMedida, tercera.PrecioUnitario, tercera.ImporteLinea));

        var lineas = await client.GetFromJsonAsync<List<LineaFacturaVentaBorradorResponse>>($"{Base}/borradores/{borrador.Id}/lineas");
        Assert.Equal([10000, 20000, 30000], lineas!.Select(l => l.NumeroLinea));
        Assert.Equal(3, (await GetBorradorAsync(client, borrador.Id)).NumeroLineas);
    }

    [Fact]
    public async Task LineaProducto_Validaciones_Devuelven400ConElCampo()
    {
        var client = Admin();
        var borrador = await CrearBorradorOkAsync(client, new { socioNegocioId = await CrearSocioAsync(client, "Validaciones") });
        var producto = await CrearProductoAsync(client, 10m);
        var url = $"{Base}/borradores/{borrador.Id}/lineas";

        await AssertErrorAsync(await client.PostAsJsonAsync(url, LineaProducto(Guid.NewGuid(), 1m)),
            HttpStatusCode.BadRequest, "bloqueado para la venta", "ProductoId");

        var bloqueadoVenta = await CrearProductoAsync(client, 10m, BloqueoProducto.Venta);
        await AssertErrorAsync(await client.PostAsJsonAsync(url, LineaProducto(bloqueadoVenta, 1m)),
            HttpStatusCode.BadRequest, "bloqueado para la venta", "ProductoId");

        var sinGrupoIva = await CrearProductoAsync(client, 10m);
        await EjecutarSqlAsync("""UPDATE "Productos" SET "GrupoIvaProductoId" = NULL WHERE "Id" = @Id""", new { Id = sinGrupoIva });
        await AssertErrorAsync(await client.PostAsJsonAsync(url, LineaProducto(sinGrupoIva, 1m)),
            HttpStatusCode.BadRequest, "grupo de IVA de producto", "ProductoId");

        await AssertErrorAsync(await client.PostAsJsonAsync(url, LineaProducto(producto, 0m)),
            HttpStatusCode.BadRequest, "cantidad", "Cantidad");
        await AssertErrorAsync(await client.PostAsJsonAsync(url, LineaProducto(producto, 1.0000001m)),
            HttpStatusCode.BadRequest, "cantidad", "Cantidad");
        await AssertErrorAsync(await client.PostAsJsonAsync(url, new { tipo = TipoLineaFactura.Producto, productoId = producto, cantidad = 1m, precioUnitario = -1m }),
            HttpStatusCode.BadRequest, "precio unitario", "PrecioUnitario");
        await AssertErrorAsync(await client.PostAsJsonAsync(url, new { tipo = TipoLineaFactura.Producto, productoId = producto, cantidad = 1m, precioUnitario = 1.00001m }),
            HttpStatusCode.BadRequest, "precio unitario", "PrecioUnitario");
        await AssertErrorAsync(await client.PostAsJsonAsync(url, new { tipo = TipoLineaFactura.Producto, productoId = producto, cantidad = 1m, porcentajeDescuentoLinea = 100.00001m }),
            HttpStatusCode.BadRequest, "descuento", "PorcentajeDescuentoLinea");
        await AssertErrorAsync(await client.PostAsJsonAsync(url, new { tipo = TipoLineaFactura.Producto, productoId = producto, cantidad = 1m, porcentajeDescuentoLinea = 1.123456m }),
            HttpStatusCode.BadRequest, "descuento", "PorcentajeDescuentoLinea");
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(url, new
        {
            tipo = TipoLineaFactura.Producto, productoId = producto, cantidad = 1m, porcentajeDescuentoLinea = 12.12345m
        })).StatusCode);
        await AssertErrorAsync(await client.PostAsJsonAsync(url, new { tipo = TipoLineaFactura.Producto, productoId = producto, cantidad = 999_999_999_999m, precioUnitario = 99_999_999_999_999m }),
            HttpStatusCode.BadRequest, "importe de la línea", "Cantidad");
        await AssertErrorAsync(await client.PostAsJsonAsync(url, new { tipo = TipoLineaFactura.Producto, productoId = producto, cantidad = 1m, unidadMedidaId = await UnidadAsync("KG") }),
            HttpStatusCode.BadRequest, null, "UnidadMedidaId");
        await AssertErrorAsync(await client.PostAsJsonAsync(url, new { tipo = TipoLineaFactura.Producto, productoId = producto, cantidad = 1m, almacenId = Guid.NewGuid() }),
            HttpStatusCode.BadRequest, "almacén", "AlmacenId");
        await AssertErrorAsync(await client.PostAsJsonAsync(url, new { tipo = TipoLineaFactura.Producto, productoId = producto, cantidad = 1m, cuentaContableId = CuentaContableIds.Ventas }),
            HttpStatusCode.BadRequest, "admite", "CuentaContableId");
        await AssertErrorAsync(await client.PostAsJsonAsync(url, new { tipo = (TipoLineaFactura)9, cantidad = 1m }),
            HttpStatusCode.BadRequest, "tipo de línea", "Tipo");
    }

    [Fact]
    public async Task ReglaDeImportes_PrecioCeroEImporteCeroBloqueados_SalvoCienPorCientoDeDescuento()
    {
        var client = Admin();
        var borrador = await CrearBorradorOkAsync(client, new { socioNegocioId = await CrearSocioAsync(client, "Importes") });
        var producto = await CrearProductoAsync(client, 10m);
        var url = $"{Base}/borradores/{borrador.Id}/lineas";

        // Precio 0 informado, en Producto y en CuentaContable: 400 con la ayuda del 100 % de descuento.
        await AssertErrorAsync(await client.PostAsJsonAsync(url, new { tipo = TipoLineaFactura.Producto, productoId = producto, cantidad = 1m, precioUnitario = 0m }),
            HttpStatusCode.BadRequest, "100 % de descuento", "PrecioUnitario");
        await AssertErrorAsync(await client.PostAsJsonAsync(url, new
        {
            tipo = TipoLineaFactura.CuentaContable, cuentaContableId = CuentaContableIds.Ventas, cantidad = 1m, precioUnitario = 0m,
            porcentajeDescuentoLinea = 100m, grupoIvaProductoId = GrupoContableIds.IvaProductoItbis18
        }), HttpStatusCode.BadRequest, "mayor que cero", "PrecioUnitario");

        // Precio por defecto 0 (producto sin precio de venta) y sin precio informado: el mismo error, explicado.
        var sinPrecio = await CrearProductoAsync(client, 0m);
        await AssertErrorAsync(await client.PostAsJsonAsync(url, LineaProducto(sinPrecio, 1m)),
            HttpStatusCode.BadRequest, "no tiene precio de venta", "PrecioUnitario");

        // Importe de línea 0 por redondeo (0.001 × 1 = 0.001 -> 0.00) sin 100 % de descuento: 400; con descuento parcial también.
        // (Línea de cuenta: sin unidad base que limite los decimales de la cantidad.)
        await AssertErrorAsync(await client.PostAsJsonAsync(url, new
        {
            tipo = TipoLineaFactura.CuentaContable, cuentaContableId = CuentaContableIds.Ventas, cantidad = 0.001m, precioUnitario = 1m,
            grupoIvaProductoId = GrupoContableIds.IvaProductoItbis18
        }), HttpStatusCode.BadRequest, "importe de la línea", "Cantidad");
        await AssertErrorAsync(await client.PostAsJsonAsync(url, new
        {
            tipo = TipoLineaFactura.CuentaContable, cuentaContableId = CuentaContableIds.Ventas, cantidad = 0.001m, precioUnitario = 1m,
            porcentajeDescuentoLinea = 50m, grupoIvaProductoId = GrupoContableIds.IvaProductoItbis18
        }), HttpStatusCode.BadRequest, "importe de la línea", "Cantidad");
        Assert.Equal(0, (await GetBorradorAsync(client, borrador.Id)).NumeroLineas);

        // Con el 100 % de descuento el importe 0 se admite (regalo), también con el precio por defecto.
        var regalo = await CrearLineaOkAsync(client, borrador.Id, new
        {
            tipo = TipoLineaFactura.Producto, productoId = producto, cantidad = 3m, porcentajeDescuentoLinea = 100m
        });
        Assert.Equal((10m, 100m, 30m, 0m), (regalo.PrecioUnitario, regalo.PorcentajeDescuentoLinea, regalo.ImporteDescuentoLinea, regalo.ImporteLinea));
        var cuentaRegalo = await CrearLineaOkAsync(client, borrador.Id, new
        {
            tipo = TipoLineaFactura.CuentaContable, cuentaContableId = CuentaContableIds.Ventas, cantidad = 1m, precioUnitario = 25m,
            porcentajeDescuentoLinea = 100m, grupoIvaProductoId = GrupoContableIds.IvaProductoItbis18
        });
        Assert.Equal(0m, cuentaRegalo.ImporteLinea);

        // Modificar aplica la misma regla.
        await AssertErrorAsync(await client.PutAsJsonAsync($"{Base}/lineas-borrador/{regalo.Id}", new
        {
            tipo = TipoLineaFactura.Producto, productoId = producto, cantidad = 3m, precioUnitario = 0m, porcentajeDescuentoLinea = 100m,
            xmin = regalo.Xmin
        }), HttpStatusCode.BadRequest, "100 % de descuento", "PrecioUnitario");

        Assert.Equal(2, (await GetBorradorAsync(client, borrador.Id)).NumeroLineas);
    }

    [Fact]
    public async Task LineaProducto_CantidadNoExactaEnLaUnidadBase_400EnCantidadConLosDecimalesAdmitidos_SinRedondear()
    {
        var client = Admin();
        var borrador = await CrearBorradorOkAsync(client, new { socioNegocioId = await CrearSocioAsync(client, "Cantidades") });
        var url = $"{Base}/borradores/{borrador.Id}/lineas";

        // Base UND (0 decimales): 2.5 se rechaza (antes el inventario la redondeaba a 3 mientras la factura decía 2.5).
        var und = await CrearProductoAsync(client, 10m);
        await AssertErrorAsync(await client.PostAsJsonAsync(url, LineaProducto(und, 2.5m)),
            HttpStatusCode.BadRequest, "no admite decimales", "Cantidad");

        // Caja de 3 con base MT (2 decimales): 0.5 CJA = 1.5 MT se acepta; 1/3 CJA (0.333333) = 0.999999 MT no.
        var metro = await CrearProductoAsync(client, 10m);
        await EjecutarSqlAsync(
            """
            UPDATE "Productos" SET "UnidadMedidaBaseId" = (SELECT "Id" FROM "UnidadesMedida" WHERE "Codigo" = 'MT') WHERE "Id" = @P;
            INSERT INTO "UnidadesMedidaProducto" ("Id", "ProductoId", "UnidadMedidaId", "CantidadPorUnidadMedida", "CreatedAtUtc", "CreatedBy", "IsDeleted")
            SELECT gen_random_uuid(), @P, "Id", 3, now(), 'test', false FROM "UnidadesMedida" WHERE "Codigo" = 'CJA'
            """, new { P = metro });
        var cja = await UnidadAsync("CJA");
        var valida = await CrearLineaOkAsync(client, borrador.Id, new
        {
            tipo = TipoLineaFactura.Producto, productoId = metro, unidadMedidaId = cja, cantidad = 0.5m, precioUnitario = 30m
        });
        Assert.Equal((0.5m, 3m), (valida.Cantidad, valida.CantidadPorUnidadMedida));
        await AssertErrorAsync(await client.PostAsJsonAsync(url, new
            {
                tipo = TipoLineaFactura.Producto, productoId = metro, unidadMedidaId = cja, cantidad = 0.333333m, precioUnitario = 30m
            }),
            HttpStatusCode.BadRequest, "como máximo 2 decimales", "Cantidad");

        Assert.Equal(1, (await GetBorradorAsync(client, borrador.Id)).NumeroLineas);
    }

    [Fact]
    public async Task LineaCuentaContable_YComentario_ReglasPorTipo()
    {
        var client = Admin();
        var borrador = await CrearBorradorOkAsync(client, new { socioNegocioId = await CrearSocioAsync(client, "Cuentas") });
        var url = $"{Base}/borradores/{borrador.Id}/lineas";

        // Cuenta de posteo directo (4101 Ventas) con su propio grupo de IVA de producto.
        var cuenta = await CrearLineaOkAsync(client, borrador.Id, new
        {
            tipo = TipoLineaFactura.CuentaContable, cuentaContableId = CuentaContableIds.Ventas, cantidad = 2m, precioUnitario = 25m,
            grupoIvaProductoId = GrupoContableIds.IvaProductoExento
        });
        Assert.Equal(("4101", "Ventas", 50m, 1m), (cuenta.CuentaContableNumero, cuenta.Descripcion, cuenta.ImporteLinea, cuenta.CantidadPorUnidadMedida));
        Assert.Equal(("EXENTO", 0m), (cuenta.IdentificadorIva, cuenta.PorcentajeIva));
        Assert.Null(cuenta.AlmacenId);
        Assert.Null(cuenta.GrupoProductoId);

        // 1201 CxC no es de posteo directo; 1 es de encabezado.
        await AssertErrorAsync(await client.PostAsJsonAsync(url, LineaCuenta(CuentaContableIds.CxC)),
            HttpStatusCode.BadRequest, "captura directa", "CuentaContableId");
        await AssertErrorAsync(await client.PostAsJsonAsync(url, LineaCuenta(CuentaContableIds.Activos)),
            HttpStatusCode.BadRequest, "captura directa", "CuentaContableId");
        await AssertErrorAsync(await client.PostAsJsonAsync(url, new
        {
            tipo = TipoLineaFactura.CuentaContable, cuentaContableId = CuentaContableIds.Ventas, cantidad = 1m, precioUnitario = 1m
        }), HttpStatusCode.BadRequest, "requiere el grupo de IVA", "GrupoIvaProductoId");
        await AssertErrorAsync(await client.PostAsJsonAsync(url, new
        {
            tipo = TipoLineaFactura.CuentaContable, cuentaContableId = CuentaContableIds.Ventas, cantidad = 1m,
            grupoIvaProductoId = GrupoContableIds.IvaProductoItbis18
        }), HttpStatusCode.BadRequest, "precio unitario", "PrecioUnitario");
        await AssertErrorAsync(await client.PostAsJsonAsync(url, new
        {
            tipo = TipoLineaFactura.CuentaContable, cuentaContableId = CuentaContableIds.Ventas, cantidad = 1m, precioUnitario = 1m,
            grupoIvaProductoId = GrupoContableIds.IvaProductoItbis18, almacenId = AlmacenIds.Principal
        }), HttpStatusCode.BadRequest, "admite", "AlmacenId");

        // Grupo de IVA sin setup para (ITBIS18 × nuevo grupo) -> 400 con la combinación en el mensaje.
        var grupoSinSetup = await CrearGrupoAsync(client, "iva-producto");
        var sinSetup = await client.PostAsJsonAsync(url, new
        {
            tipo = TipoLineaFactura.CuentaContable, cuentaContableId = CuentaContableIds.Ventas, cantidad = 1m, precioUnitario = 1m,
            grupoIvaProductoId = grupoSinSetup.Id
        });
        var problema = await AssertErrorAsync(sinSetup, HttpStatusCode.BadRequest, "No existe setup", "GrupoIvaProductoId");
        Assert.Contains(grupoSinSetup.Codigo, problema.RootElement.GetRawText());

        // Comentario: solo descripción, sin importes.
        var comentario = await CrearLineaOkAsync(client, borrador.Id, new { tipo = TipoLineaFactura.Comentario, descripcion = "Entregar por la tarde" });
        Assert.Equal((0m, 0m, 0m, (string?)null), (comentario.Cantidad, comentario.PrecioUnitario, comentario.ImporteLinea, comentario.IdentificadorIva));
        await AssertErrorAsync(await client.PostAsJsonAsync(url, new { tipo = TipoLineaFactura.Comentario }),
            HttpStatusCode.BadRequest, "requiere una descripción", "Descripcion");
        await AssertErrorAsync(await client.PostAsJsonAsync(url, new { tipo = TipoLineaFactura.Comentario, descripcion = "x", cantidad = 1m }),
            HttpStatusCode.BadRequest, "no lleva", "Cantidad");
        await AssertErrorAsync(await client.PostAsJsonAsync(url, new { tipo = TipoLineaFactura.Comentario, descripcion = new string('x', 201) }),
            HttpStatusCode.BadRequest, "200 caracteres", "Descripcion");
    }

    [Fact]
    public async Task Totales_IvaAgrupadoPorIdentificador_SinComentarios()
    {
        var client = Admin();
        var borrador = await CrearBorradorOkAsync(client, new { socioNegocioId = await CrearSocioAsync(client, "Totales") });
        var producto = await CrearProductoAsync(client, 10.03m);
        for (var i = 0; i < 3; i++)
        {
            await CrearLineaOkAsync(client, borrador.Id, LineaProducto(producto, 1m));
        }

        await CrearLineaOkAsync(client, borrador.Id, new { tipo = TipoLineaFactura.Comentario, descripcion = "Nota" });
        await CrearLineaOkAsync(client, borrador.Id, new
        {
            tipo = TipoLineaFactura.CuentaContable, cuentaContableId = CuentaContableIds.Ventas, cantidad = 1m, precioUnitario = 50m,
            grupoIvaProductoId = GrupoContableIds.IvaProductoExento
        });

        var totales = await client.GetFromJsonAsync<TotalesFactura>($"{Base}/borradores/{borrador.Id}/totales");

        // Por línea serían 1.81 × 3 = 5.43; agrupado ROUND(30.09 × 18 %, 2) = 5.42.
        Assert.Equal(
            [new GrupoIvaCalculado("EXENTO", 0m, 50m, 0m), new GrupoIvaCalculado("ITBIS18", 18m, 30.09m, 5.42m)],
            totales!.Grupos);
        Assert.Equal((80.09m, 5.42m, 85.51m), (totales.ImporteSinIva, totales.ImporteIva, totales.ImporteTotal));
        Assert.Equal(HttpStatusCode.OK, (await Rol("Ejecutor").GetAsync($"{Base}/borradores/{borrador.Id}/totales")).StatusCode);
    }

    [Fact]
    public async Task Lineas_ModificarConXmin_Borrar_YRutasInexistentes404()
    {
        var client = Admin();
        var borrador = await CrearBorradorOkAsync(client, new { socioNegocioId = await CrearSocioAsync(client, "Xmin") });
        var producto = await CrearProductoAsync(client, 5m);
        var linea = await CrearLineaOkAsync(client, borrador.Id, LineaProducto(producto, 1m));

        var put = await Rol("Supervisor").PutAsJsonAsync($"{Base}/lineas-borrador/{linea.Id}", new
        {
            tipo = TipoLineaFactura.Producto, productoId = producto, cantidad = 4m, precioUnitario = 2.5m, xmin = linea.Xmin
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var modificada = (await put.Content.ReadFromJsonAsync<LineaFacturaVentaBorradorResponse>())!;
        Assert.Equal((10000, 10m), (modificada.NumeroLinea, modificada.ImporteLinea));

        var conflicto = await Admin().PutAsJsonAsync($"{Base}/lineas-borrador/{linea.Id}", new
        {
            tipo = TipoLineaFactura.Producto, productoId = producto, cantidad = 1m, xmin = linea.Xmin
        });
        Assert.Equal(HttpStatusCode.Conflict, conflicto.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Rol("Ejecutor").PutAsJsonAsync($"{Base}/lineas-borrador/{linea.Id}", new
        {
            tipo = TipoLineaFactura.Producto, productoId = producto, cantidad = 1m, xmin = modificada.Xmin
        })).StatusCode);

        // Rutas con un borrador/línea inexistente -> 404 (el Id de la RUTA no es una referencia del cuerpo).
        var admin = Admin();
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync($"{Base}/borradores/{Guid.NewGuid()}/lineas", LineaProducto(producto, 1m))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync($"{Base}/lineas-borrador/{Guid.NewGuid()}", new
        {
            tipo = TipoLineaFactura.Producto, productoId = producto, cantidad = 1m, xmin = 1L
        })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{Base}/borradores/{Guid.NewGuid()}/lineas")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{Base}/borradores/{Guid.NewGuid()}/totales")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{Base}/lineas-borrador/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"{Base}/lineas-borrador/{Guid.NewGuid()}")).StatusCode);

        // Borrar la línea: solo CanDelete (Administrador).
        Assert.Equal(HttpStatusCode.Forbidden, (await Rol("Supervisor").DeleteAsync($"{Base}/lineas-borrador/{linea.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Admin().DeleteAsync($"{Base}/lineas-borrador/{linea.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin().GetAsync($"{Base}/lineas-borrador/{linea.Id}")).StatusCode);

        // La siguiente línea sigue numerando desde el máximo VIGENTE (sin líneas vivas -> 10000).
        Assert.Equal(10000, (await CrearLineaOkAsync(Admin(), borrador.Id, LineaProducto(producto, 1m))).NumeroLinea);
    }

    [Fact]
    public async Task LiberarYReabrir_BloqueaCambios_YRoles()
    {
        var client = Admin();
        var borrador = await CrearBorradorOkAsync(client, new { socioNegocioId = await CrearSocioAsync(client, "Liberar") });
        var producto = await CrearProductoAsync(client, 5m);

        await AssertErrorAsync(await client.PostAsync($"{Base}/borradores/{borrador.Id}/liberar", null),
            HttpStatusCode.BadRequest, "sin líneas", "Id");

        var linea = await CrearLineaOkAsync(client, borrador.Id, LineaProducto(producto, 1m));

        Assert.Equal(HttpStatusCode.Forbidden, (await Rol("Ejecutor").PostAsync($"{Base}/borradores/{borrador.Id}/liberar", null)).StatusCode);
        var liberar = await Rol("Supervisor").PostAsync($"{Base}/borradores/{borrador.Id}/liberar", null);
        Assert.Equal(HttpStatusCode.OK, liberar.StatusCode);
        var liberado = (await liberar.Content.ReadFromJsonAsync<FacturaVentaBorradorResponse>())!;
        Assert.Equal(EstadoFacturaBorrador.Liberada, liberado.Estado);

        var admin = Admin();
        await AssertErrorAsync(await admin.PostAsJsonAsync($"{Base}/borradores/{borrador.Id}/lineas", LineaProducto(producto, 1m)),
            HttpStatusCode.BadRequest, "liberado", "FacturaVentaBorradorId");
        await AssertErrorAsync(await admin.PutAsJsonAsync($"{Base}/lineas-borrador/{linea.Id}", new
        {
            tipo = TipoLineaFactura.Producto, productoId = producto, cantidad = 2m, xmin = linea.Xmin
        }), HttpStatusCode.BadRequest, "liberado", "FacturaVentaBorradorId");
        await AssertErrorAsync(await admin.DeleteAsync($"{Base}/lineas-borrador/{linea.Id}"),
            HttpStatusCode.BadRequest, "liberado", "FacturaVentaBorradorId");
        await AssertErrorAsync(await admin.PutAsJsonAsync($"{Base}/borradores/{borrador.Id}", new { xmin = liberado.Xmin, descripcion = "x" }),
            HttpStatusCode.BadRequest, "liberado", "Id");
        await AssertErrorAsync(await admin.DeleteAsync($"{Base}/borradores/{borrador.Id}"),
            HttpStatusCode.BadRequest, "liberado", "Id");
        await AssertErrorAsync(await admin.PostAsync($"{Base}/borradores/{borrador.Id}/liberar", null),
            HttpStatusCode.BadRequest, "liberado", "Id");

        var reabrir = await Rol("Supervisor").PostAsync($"{Base}/borradores/{borrador.Id}/reabrir", null);
        Assert.Equal(HttpStatusCode.OK, reabrir.StatusCode);
        Assert.Equal(EstadoFacturaBorrador.Abierta, (await reabrir.Content.ReadFromJsonAsync<FacturaVentaBorradorResponse>())!.Estado);
        await AssertErrorAsync(await Admin().PostAsync($"{Base}/borradores/{borrador.Id}/reabrir", null),
            HttpStatusCode.BadRequest, "ya está abierto", "Id");
        Assert.Equal(HttpStatusCode.NotFound, (await Admin().PostAsync($"{Base}/borradores/{Guid.NewGuid()}/liberar", null)).StatusCode);

        // Reabierto, vuelve a admitir líneas.
        Assert.Equal(20000, (await CrearLineaOkAsync(Admin(), borrador.Id, LineaProducto(producto, 1m))).NumeroLinea);
    }

    [Fact]
    public async Task Borrar_BorraLasLineas_ListadoYRoles()
    {
        var client = Admin();
        var socio = await CrearSocioAsync(client, "Borrar");
        var producto = await CrearProductoAsync(client, 5m);

        // Ejecutor tiene CanAdd; el anónimo no pasa.
        var creado = await Rol("Ejecutor").PostAsJsonAsync($"{Base}/borradores", new { socioNegocioId = socio });
        Assert.Equal(HttpStatusCode.Created, creado.StatusCode);
        var borrador = (await creado.Content.ReadFromJsonAsync<FacturaVentaBorradorResponse>())!;
        var anon = new HttpRequestMessage(HttpMethod.Get, $"{Base}/borradores");
        anon.Headers.Add("X-Test-Anonymous", "true");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(anon)).StatusCode);

        var l1 = await CrearLineaOkAsync(Rol("Ejecutor"), borrador.Id, LineaProducto(producto, 1m));
        await CrearLineaOkAsync(Admin(), borrador.Id, new { tipo = TipoLineaFactura.Comentario, descripcion = "Nota" });

        var pagina = await Rol("Ejecutor").GetFromJsonAsync<PagedResult<FacturaVentaBorradorResponse>>(
            $"{Base}/borradores?socioId={socio}&estado=1&numero={borrador.Numero}");
        var fila = Assert.Single(pagina!.Items);
        Assert.Equal((borrador.Id, 2, "Borrar"), (fila.Id, fila.NumeroLineas, fila.SocioNegocioNombre));

        Assert.Equal(HttpStatusCode.Forbidden, (await Rol("Supervisor").DeleteAsync($"{Base}/borradores/{borrador.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Admin().DeleteAsync($"{Base}/borradores/{borrador.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin().GetAsync($"{Base}/borradores/{borrador.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin().GetAsync($"{Base}/lineas-borrador/{l1.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin().DeleteAsync($"{Base}/borradores/{borrador.Id}")).StatusCode);

        await using var conexion = new NpgsqlConnection(_fixture.AppConnectionString);
        var (vivas, borradas) = await conexion.QuerySingleAsync<(long, long)>(
            """
            SELECT COUNT(*) FILTER (WHERE NOT "IsDeleted"), COUNT(*) FILTER (WHERE "IsDeleted" AND "DeletedBy" = 'administrador')
            FROM "LineasFacturaVentaBorrador" WHERE "FacturaVentaBorradorId" = @Id
            """, new { borrador.Id });
        Assert.Equal((0L, 2L), (vivas, borradas));
    }

    [Fact]
    public async Task GuardasDeUso_GrupoYCuentaUsadosEnUnBorrador_Devuelven409()
    {
        var client = Admin();
        var borrador = await CrearBorradorOkAsync(client, new { socioNegocioId = await CrearSocioAsync(client, "Guardas") });
        var grupoIva = await CrearGrupoAsync(client, "iva-producto");
        var crearSetup = await client.PostAsJsonAsync("/api/setups-contables/iva", new
        {
            grupoIvaNegocioId = GrupoContableIds.IvaNegocioItbis18, grupoIvaProductoId = grupoIva.Id, porcentajeIva = 16m,
            cuentaIvaVentasId = CuentaContableIds.IvaPorPagar, identificadorIva = "ITBIS16", tipoCalculoIva = TipoCalculoIva.Normal
        });
        Assert.Equal(HttpStatusCode.Created, crearSetup.StatusCode);
        var setupId = JsonDocument.Parse(await crearSetup.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var cuenta = await CrearCuentaAsync(client);
        await CrearLineaOkAsync(client, borrador.Id, new
        {
            tipo = TipoLineaFactura.CuentaContable, cuentaContableId = cuenta, cantidad = 1m, precioUnitario = 1m, grupoIvaProductoId = grupoIva.Id
        });

        // Solo el borrador usa el grupo (el setup se borra primero) y la cuenta: ambos borrados -> 409.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/setups-contables/iva/{setupId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/grupos-contables/iva-producto/{grupoIva.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/cuentas-contables/{cuenta}")).StatusCode);

        // Borrado el borrador (y sus líneas), ya no están en uso.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Base}/borradores/{borrador.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/grupos-contables/iva-producto/{grupoIva.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/cuentas-contables/{cuenta}")).StatusCode);
    }

    [Fact]
    public async Task Modificar_CambioDeSocioSinSetupDeIvaParaUnaLinea_Devuelve400ConLaLinea_YNoGuardaNada()
    {
        var client = Admin();
        var socio = await CrearSocioAsync(client, "Con setup");
        var exento = await CrearSocioAsync(client, "Sin setup", new { grupoIvaNegocioId = GrupoContableIds.IvaNegocioExento });
        var grupoIva = await CrearGrupoAsync(client, "iva-producto");
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/setups-contables/iva", new
        {
            grupoIvaNegocioId = GrupoContableIds.IvaNegocioItbis18, grupoIvaProductoId = grupoIva.Id, porcentajeIva = 16m,
            cuentaIvaVentasId = CuentaContableIds.IvaPorPagar, identificadorIva = "ITBIS16", tipoCalculoIva = TipoCalculoIva.Normal
        })).StatusCode);
        var borrador = await CrearBorradorOkAsync(client, new { socioNegocioId = socio });
        await CrearLineaOkAsync(client, borrador.Id, new { tipo = TipoLineaFactura.Comentario, descripcion = "Nota" });
        var linea = await CrearLineaOkAsync(client, borrador.Id, new
        {
            tipo = TipoLineaFactura.CuentaContable, cuentaContableId = CuentaContableIds.Ventas, cantidad = 1m, precioUnitario = 1m,
            grupoIvaProductoId = grupoIva.Id
        });
        Assert.Equal(("ITBIS16", 16m), (linea.IdentificadorIva, linea.PorcentajeIva));

        var put = await client.PutAsJsonAsync($"{Base}/borradores/{borrador.Id}", new { xmin = borrador.Xmin, socioNegocioId = exento });

        await AssertErrorAsync(put, HttpStatusCode.BadRequest, "No existe setup", "Lineas[20000].GrupoIvaProductoId");
        var releido = await GetBorradorAsync(client, borrador.Id);
        Assert.Equal((socio, GrupoContableIds.IvaNegocioItbis18, borrador.Xmin), (releido.SocioNegocioId, releido.GrupoIvaNegocioId, releido.Xmin));
    }

    [Fact]
    public async Task AltasDeLineaConcurrentes_EnElMismoBorrador_NumeranSinRepetir()
    {
        var client = Admin();
        var borrador = await CrearBorradorOkAsync(client, new { socioNegocioId = await CrearSocioAsync(client, "Concurrencia") });
        var producto = await CrearProductoAsync(client, 1m);

        var respuestas = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            client.PostAsJsonAsync($"{Base}/borradores/{borrador.Id}/lineas", LineaProducto(producto, 1m))));

        Assert.All(respuestas, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var lineas = await client.GetFromJsonAsync<List<LineaFacturaVentaBorradorResponse>>($"{Base}/borradores/{borrador.Id}/lineas");
        Assert.Equal(Enumerable.Range(1, 8).Select(i => i * 10000), lineas!.Select(l => l.NumeroLinea));
    }

    [Fact]
    public async Task Listado_SinEstadoIncluyeLosEnCurso_YEstado3SoloLosPosteados_AlPostearPasaDeUnoAOtro()
    {
        var client = Admin();
        var socio = await CrearSocioAsync(client, "Cliente filtro posteadas");
        var cuenta = await CrearCuentaAsync(client);
        var borrador = await CrearBorradorOkAsync(client, new { socioNegocioId = socio, fechaRegistro = D10 });
        await CrearLineaOkAsync(client, borrador.Id, LineaCuenta(cuenta));

        var porDefecto = await client.GetFromJsonAsync<PagedResult<FacturaVentaBorradorResponse>>($"{Base}/borradores?socioId={socio}");
        var posteados = await client.GetFromJsonAsync<PagedResult<FacturaVentaBorradorResponse>>($"{Base}/borradores?socioId={socio}&estado=3");

        Assert.Contains(porDefecto!.Items, b => b.Id == borrador.Id && b.SerieBorradorCodigo == "FV-BORR" && b.SerieRegistroCodigo == "FV");
        Assert.DoesNotContain(posteados!.Items, b => b.Id == borrador.Id);

        // Al postearlo por la API sale del listado por defecto y aparece con estado=3, enlazado a su factura.
        var posteo = await client.PostAsync($"{Base}/borradores/{borrador.Id}/postear", null);
        var cuerpo = await posteo.Content.ReadAsStringAsync();
        Assert.True(posteo.StatusCode == HttpStatusCode.OK, cuerpo);
        var numero = JsonDocument.Parse(cuerpo).RootElement.GetProperty("numero").GetString();

        porDefecto = await client.GetFromJsonAsync<PagedResult<FacturaVentaBorradorResponse>>($"{Base}/borradores?socioId={socio}");
        posteados = await client.GetFromJsonAsync<PagedResult<FacturaVentaBorradorResponse>>($"{Base}/borradores?socioId={socio}&estado=3");

        Assert.DoesNotContain(porDefecto!.Items, b => b.Id == borrador.Id);
        var fila = Assert.Single(posteados!.Items);
        Assert.Equal((borrador.Id, EstadoFacturaBorrador.Posteada, numero), (fila.Id, fila.Estado, fila.FacturaVentaNumero));
    }

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

    // ----- Helpers -----

    private static object LineaProducto(Guid productoId, decimal cantidad) =>
        new { tipo = TipoLineaFactura.Producto, productoId, cantidad };

    private static object LineaCuenta(Guid cuentaId) => new
    {
        tipo = TipoLineaFactura.CuentaContable, cuentaContableId = cuentaId, cantidad = 1m, precioUnitario = 1m,
        grupoIvaProductoId = GrupoContableIds.IvaProductoItbis18
    };

    private static string Unico(string prefijo) => $"{prefijo}{Guid.NewGuid():N}"[..11];

    private static async Task<FacturaVentaBorradorResponse> CrearBorradorOkAsync(HttpClient client, object cuerpo)
    {
        var response = await client.PostAsJsonAsync($"{Base}/borradores", cuerpo);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<FacturaVentaBorradorResponse>())!;
    }

    private static async Task<LineaFacturaVentaBorradorResponse> CrearLineaOkAsync(HttpClient client, Guid borradorId, object cuerpo)
    {
        var response = await client.PostAsJsonAsync($"{Base}/borradores/{borradorId}/lineas", cuerpo);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<LineaFacturaVentaBorradorResponse>())!;
    }

    private static async Task<FacturaVentaBorradorResponse> GetBorradorAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<FacturaVentaBorradorResponse>($"{Base}/borradores/{id}"))!;

    private static async Task<LineaFacturaVentaBorradorResponse> GetLineaAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<LineaFacturaVentaBorradorResponse>($"{Base}/lineas-borrador/{id}"))!;

    private static async Task<Guid> CrearSocioAsync(HttpClient client, string nombre, object? extra = null)
    {
        var cuerpo = new Dictionary<string, object?> { ["nombreComercial"] = nombre };
        if (extra is not null)
        {
            foreach (var p in extra.GetType().GetProperties())
            {
                cuerpo[p.Name] = p.GetValue(extra);
            }
        }

        var response = await client.PostAsJsonAsync("/api/socios-negocio", cuerpo);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CrearProductoAsync(HttpClient client, decimal precioVenta, BloqueoProducto bloqueado = BloqueoProducto.Ninguno)
    {
        var response = await client.PostAsJsonAsync(
            "/api/productos", new { codigo = Unico("PF"), nombre = "Producto factura", precioVenta, bloqueado });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CrearTerminoAsync(HttpClient client, int dias)
    {
        var response = await client.PostAsJsonAsync(
            "/api/terminos-pago", new { codigo = Unico("T"), descripcion = $"{dias} días", diasVencimiento = dias, diasDescuento = 0, porcentajeDescuento = 0m });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CrearGrupoClienteContableAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/grupos-cliente-contable", new { codigo = Unico("GC").ToUpperInvariant(), descripcion = "Grupo cliente", cuentaCxCId = CuentaContableIds.CxC });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<(Guid Id, string Codigo)> CrearGrupoAsync(HttpClient client, string tipo)
    {
        var codigo = Unico("G").ToUpperInvariant();
        var response = await client.PostAsJsonAsync($"/api/grupos-contables/{tipo}", new { codigo, descripcion = "Grupo de prueba" });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid(), codigo);
    }

    private static async Task<Guid> CrearCuentaAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/cuentas-contables", new
        {
            numero = $"49{Random.Shared.Next(10_000, 99_999)}", nombre = "Otros ingresos", tipoCuenta = TipoCuentaContable.Posteo,
            tipoResultado = TipoResultadoCuenta.Resultado, posteoDirecto = true, bloqueada = false, sangria = 1
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }

    private async Task<Guid> UnidadAsync(string codigo)
    {
        await using var conexion = new NpgsqlConnection(_fixture.AppConnectionString);
        return await conexion.QuerySingleAsync<Guid>("""SELECT "Id" FROM "UnidadesMedida" WHERE "Codigo" = @Codigo""", new { Codigo = codigo });
    }

    private async Task EjecutarSqlAsync(string sql, object parametros)
    {
        await using var conexion = new NpgsqlConnection(_fixture.AppConnectionString);
        await conexion.ExecuteAsync(sql, parametros);
    }

    private static async Task<JsonDocument> AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string? fragmentoMensaje, string campo)
    {
        var cuerpo = await response.Content.ReadAsStringAsync();
        Assert.True(status == response.StatusCode, $"Se esperaba {status} y llegó {response.StatusCode}: {cuerpo}");
        var problema = JsonDocument.Parse(cuerpo);
        Assert.True(
            problema.RootElement.GetProperty("errors").TryGetProperty(campo, out _),
            $"Se esperaba el campo '{campo}' en los errores: {cuerpo}");
        // La API no expone el código del error (solo el mensaje agrupado por campo): se comprueba un fragmento del mensaje.
        if (fragmentoMensaje is not null)
        {
            Assert.Contains(fragmentoMensaje, cuerpo, StringComparison.OrdinalIgnoreCase);
        }

        return problema;
    }

    private HttpClient Admin() => Rol("Administrador");

    private HttpClient Rol(string role)
    {
        var client = _client;
        client.DefaultRequestHeaders.Remove("X-Test-Anonymous");
        client.DefaultRequestHeaders.Remove("X-Test-User");
        client.DefaultRequestHeaders.Remove("X-Test-Roles");
        client.DefaultRequestHeaders.Add("X-Test-User", role.ToLowerInvariant());
        client.DefaultRequestHeaders.Add("X-Test-Roles", role);
        return client;
    }
}
