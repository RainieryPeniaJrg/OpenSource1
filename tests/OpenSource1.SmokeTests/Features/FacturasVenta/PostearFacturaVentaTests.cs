using System.Collections.Concurrent;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using OpenSource1.Application.Data;
using OpenSource1.Application.Data.UnitOfWork;
using OpenSource1.Application.Features.FacturasVenta.Borradores;
using OpenSource1.Application.Features.FacturasVenta.Borradores.Commands;
using OpenSource1.Application.Features.FacturasVenta.Borradores.Dtos;
using OpenSource1.Application.Features.FacturasVenta.Borradores.Handlers;
using OpenSource1.Application.Features.FacturasVenta.Copia;
using OpenSource1.Application.Features.FacturasVenta.Posteo;
using OpenSource1.Application.Features.SociosNegocio.Commands;
using OpenSource1.Application.Features.SociosNegocio.Handlers;
using OpenSource1.Application.Services.Clientes;
using OpenSource1.Application.Services.Contabilidad;
using OpenSource1.Application.Services.Inventario;
using OpenSource1.Core.Common;
using OpenSource1.Core.Entities;
using OpenSource1.Core.Entities.Contabilidad;
using OpenSource1.Core.Entities.Ventas;
using OpenSource1.Core.Enums;
using OpenSource1.SmokeTests.TestInfrastructure;

namespace OpenSource1.SmokeTests.Features.FacturasVenta;

/// <summary>
/// Motor de posteo de facturas de venta (Task 6.4) contra Postgres real: documento con el IVA agrupado, inventario, libro de
/// clientes y asiento en una transacción; revalidación y derivación de TODAS las cuentas antes de escribir (un fallo no deja
/// ninguna fila, no consume el número de la serie <c>FV</c> y ni siquiera intenta un INSERT: las secuencias de identidad no se
/// mueven); concurrencia; y la guarda de borrado de socios. REQUIERE DOCKER. Los tests de la clase comparten base de datos: los
/// números de la serie se comprueban RELATIVOS al último usado.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostearFacturaVentaTests(PostgresTestFixture fixture) : IClassFixture<PostgresTestFixture>, IAsyncLifetime
{
    private static readonly DateOnly D1 = new(2026, 9, 1);
    private static readonly DateOnly D10 = new(2026, 9, 10);

    private readonly LibroInventarioPrueba _prueba = new(fixture);

    public Task InitializeAsync() => _prueba.InicializarAsync();

    public async Task DisposeAsync() => await _prueba.DisposeAsync();

    // ----- Documento, inventario, libro de clientes y asiento -----

    [Fact]
    public async Task ReviewFocus1_DocumentoConIvaAgrupado_AsientoCuadrado_Inventario_LibroDeClientes_YBorradorPosteada()
    {
        // Tres líneas de 10.03 al 18 %: por línea el IVA sería 3 × 1.81 = 5.43; el del grupo es ROUND(30.09 × 0.18, 2) = 5.42.
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 10m, 4m, D1));
        Assert.True(await CostoAjustadoAsync(producto));
        var borrador = await BorradorAsync(socio, almacen: almacen);
        await LineaProductoAsync(borrador.Id, producto, 1m, 10.03m);
        await LineaProductoAsync(borrador.Id, producto, 1m, 10.03m);
        await LineaProductoAsync(borrador.Id, producto, 1m, 10.03m);
        var ultimo = await UltimoNumeroFvAsync();

        var resultado = await PostearOkAsync(borrador.Id);

        Assert.Equal(Siguiente(ultimo), resultado.Numero);
        Assert.Equal(35.51m, resultado.ImporteTotal);

        // Documento legal: totales = suma de los grupos (una línea de IVA con el IVA del grupo, no la suma por línea).
        var factura = await FacturaAsync(resultado.Numero);
        Assert.Equal((borrador.Numero, socio, socio), (factura.NumeroBorrador, factura.SocioNegocioId, factura.SocioNegocioFacturarAId));
        Assert.Equal((30.09m, 5.42m, 35.51m), (factura.ImporteSinIva, factura.ImporteIva, factura.ImporteTotal));
        Assert.Equal((D10, D10, almacen, "DOP"), (factura.FechaRegistro, factura.FechaDocumento, factura.AlmacenId, factura.Moneda));
        Assert.Equal(("Cliente posteo", "Cliente posteo SRL"), (factura.NombreFacturacion, factura.RazonSocialFacturacion));
        var iva = Assert.Single(await LineasIvaAsync(resultado.Numero));
        Assert.Equal(("ITBIS18", 18m, 30.09m, 5.42m, CuentaContableIds.IvaPorPagar),
            (iva.IdentificadorIva, iva.PorcentajeIva, iva.BaseImponible, iva.ImporteIva, iva.CuentaIvaId));
        var lineas = await LineasAsync(resultado.Numero);
        Assert.Equal([10000, 20000, 30000], lineas.Select(l => l.NumeroLinea));
        Assert.All(lineas, l => Assert.NotNull(l.MovimientoProductoId));

        // Asiento: débito CxC 35.51, crédito Ventas 30.09, crédito IVA 5.42; suma 0.
        var registroId = await EscalarAsync<long>(
            """SELECT "Id" FROM "RegistrosContables" WHERE "NumeroRegistro" = @N""", new { N = resultado.RegistroContable });
        Assert.Equal(registroId, factura.RegistroContableId);
        var asiento = await AsientoAsync(registroId);
        Assert.Equal(
            [(CuentaContableIds.CxC, 35.51m, socio), (CuentaContableIds.Ventas, -30.09m, socio), (CuentaContableIds.IvaPorPagar, -5.42m, socio)],
            asiento.Select(m => (m.CuentaContableId, m.Importe, m.SocioNegocioId!.Value)));
        Assert.Equal(0m, asiento.Sum(m => m.Importe));
        Assert.All(asiento, m => Assert.Equal((TipoDocumentoContable.FacturaVenta, resultado.Numero, D10, TipoOrigenMovimiento.FacturaVenta),
            (m.TipoDocumento, m.NumeroDocumento, m.FechaRegistro, m.TipoOrigen)));

        // Libro de clientes: factura del facturar-a por el total, con la CxC congelada, y su detalle ImporteInicial.
        var cliente = Assert.Single(await MovimientosClienteAsync(resultado.Numero));
        Assert.Equal((socio, TipoDocumentoCliente.Factura, 35.51m, CuentaContableIds.CxC, GrupoContableIds.ClienteContableGeneral),
            (cliente.SocioNegocioId, cliente.TipoDocumento, cliente.ImporteOriginal, cliente.CuentaCxCId, cliente.GrupoClienteContableId));
        Assert.Equal((TipoOrigenMovimiento.FacturaVenta, resultado.Numero), (cliente.TipoOrigen, cliente.ClaveOrigen));
        Assert.Equal([(TipoDetalleCliente.ImporteInicial, 35.51m)], await DetalleClienteAsync(cliente.Id));

        // Inventario: tres salidas Venta del vender-a con el importe de venta y el costo promedio; existencia y valor.
        var salidas = await SalidasAsync(resultado.Numero);
        Assert.Equal(3, salidas.Count);
        Assert.All(salidas, s =>
        {
            Assert.Equal((TipoMovimientoInventario.Venta, TipoDocumentoInventario.FacturaVenta, TipoOrigenMovimiento.FacturaVenta),
                (s.TipoMovimiento, s.TipoDocumento, s.TipoOrigen));
            Assert.Equal((-1m, socio, resultado.Numero, 10.03m, -4m), (s.Cantidad, s.SocioNegocioId, s.ClaveOrigen, s.ImporteVenta, s.ImporteCosto));
        });
        Assert.Equal(lineas.Select(l => l.MovimientoProductoId!.Value).Order(), salidas.Select(s => s.Id).Order());
        Assert.Equal(7m, await _prueba.ConsultarAsync(c => c.ExistenciaAsync(producto, almacen, null)));
        Assert.Equal(28m, await EscalarAsync<decimal>(
            """SELECT SUM("ImporteCosto") FROM "MovimientosValor" WHERE "ProductoId" = @P""", new { P = producto }));
        Assert.False(await CostoAjustadoAsync(producto));

        // El borrador se conserva Posteada, enlazado a la factura, con sus líneas vivas (spec no-series).
        Assert.Equal((EstadoFacturaBorrador.Posteada, false, 3L, 0L), await EstadoBorradorAsync(borrador.Id));
        Assert.Equal((EstadoFacturaBorrador.Posteada, resultado.Numero), await EstadoYFacturaAsync(borrador.Id));
    }

    [Fact]
    public async Task FacturarADistintoDeVenderA_LibroYCxCDelFacturarA_InventarioYVentasDelVenderA()
    {
        var cxcPropia = await CuentaAsync(posteoDirecto: false);
        var grupoCliente = await GrupoClienteAsync(cxcPropia);
        var venderA = await SocioAsync();
        var facturarA = await SocioAsync(grupoCliente: grupoCliente, nombre: "Casa matriz");
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 5m, 2m, D1));
        var borrador = await BorradorAsync(venderA, facturarA, almacen);
        await LineaProductoAsync(borrador.Id, producto, 2m, 50m);

        var resultado = await PostearOkAsync(borrador.Id);

        Assert.Equal(118m, resultado.ImporteTotal);
        var factura = await FacturaAsync(resultado.Numero);
        Assert.Equal((venderA, facturarA, "Casa matriz", grupoCliente),
            (factura.SocioNegocioId, factura.SocioNegocioFacturarAId, factura.NombreFacturacion, factura.GrupoClienteContableId));
        var cliente = Assert.Single(await MovimientosClienteAsync(resultado.Numero));
        Assert.Equal((facturarA, cxcPropia, grupoCliente, 118m),
            (cliente.SocioNegocioId, cliente.CuentaCxCId, cliente.GrupoClienteContableId, cliente.ImporteOriginal));
        Assert.Equal(venderA, Assert.Single(await SalidasAsync(resultado.Numero)).SocioNegocioId);
        var asiento = await AsientoAsync(factura.RegistroContableId!.Value);
        Assert.Equal(
            [(cxcPropia, 118m, facturarA), (CuentaContableIds.Ventas, -100m, venderA), (CuentaContableIds.IvaPorPagar, -18m, venderA)],
            asiento.Select(m => (m.CuentaContableId, m.Importe, m.SocioNegocioId!.Value)));
    }

    [Fact]
    public async Task BorradorLiberado_ConLineaCuentaContable_Comentario_YDescuento_SinPataDeDescuentoNiIvaCero()
    {
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        var otrosIngresos = await CuentaAsync(posteoDirecto: true);
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 5m, 3m, D1));
        var borrador = await BorradorAsync(socio, almacen: almacen);
        // 2 × 50 con 10 % de descuento: descuento 10, importe neto 90 (Ventas se acredita neto, sin pata de descuento).
        await LineaProductoAsync(borrador.Id, producto, 2m, 50m, descuento: 10m);
        await LineaCuentaAsync(borrador.Id, otrosIngresos, GrupoContableIds.IvaProductoExento, 1m, 25m);
        await ComentarioAsync(borrador.Id, "Gracias por su compra");
        await LiberarAsync(borrador.Id);

        var resultado = await PostearOkAsync(borrador.Id);

        Assert.Equal(131.2m, resultado.ImporteTotal);
        var factura = await FacturaAsync(resultado.Numero);
        Assert.Equal((115m, 16.2m, 131.2m), (factura.ImporteSinIva, factura.ImporteIva, factura.ImporteTotal));
        Assert.Equal(
            [("EXENTO", 0m, 25m, 0m), ("ITBIS18", 18m, 90m, 16.2m)],
            (await LineasIvaAsync(resultado.Numero)).Select(l => (l.IdentificadorIva, l.PorcentajeIva, l.BaseImponible, l.ImporteIva)));

        var lineas = await LineasAsync(resultado.Numero);
        Assert.Equal(
            [(10000, TipoLineaFactura.Producto, 10m, 90m, true), (20000, TipoLineaFactura.CuentaContable, 0m, 25m, false),
             (30000, TipoLineaFactura.Comentario, 0m, 0m, false)],
            lineas.Select(l => (l.NumeroLinea, l.Tipo, l.ImporteDescuentoLinea, l.ImporteLinea, l.MovimientoProductoId is not null)));
        Assert.Equal("Gracias por su compra", lineas[2].Descripcion);

        // Sin pata de IVA para el grupo exento (importe 0) ni de descuento.
        var asiento = await AsientoAsync(factura.RegistroContableId!.Value);
        Assert.Equal(
            [(CuentaContableIds.CxC, 131.2m), (CuentaContableIds.Ventas, -90m), (otrosIngresos, -25m), (CuentaContableIds.IvaPorPagar, -16.2m)],
            asiento.Select(m => (m.CuentaContableId, m.Importe)));

        var salida = Assert.Single(await SalidasAsync(resultado.Numero));
        Assert.Equal((-2m, 90m, -6m), (salida.Cantidad, salida.ImporteVenta, salida.ImporteCosto));
        Assert.Equal((EstadoFacturaBorrador.Posteada, false, 3L, 0L), await EstadoBorradorAsync(borrador.Id));
    }

    [Fact]
    public async Task RedondeoVentasVsIva_LaDiferenciaSeAjustaEnLaPataDeVentasMayor_YElAsientoCuadra()
    {
        // Importes de línea con más de 2 decimales (solo posibles por datos, no por la API): Ventas por grupo de producto
        // ROUND(1.005) + ROUND(2.005) = 1.01 + 2.01 = 3.02, pero la base del IVA agrupado es ROUND(3.01) = 3.01. El 0.01 de más se
        // quita de la pata de Ventas mayor (2.01 -> 2.00). Sin el ajuste, el asiento descuadra y el posteo falla.
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var bienes = await ProductoAsync();
        var servicios = await ProductoAsync(grupoProducto: GrupoContableIds.ProductoServicios);
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(bienes, almacen, 5m, 1m, D1));
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(servicios, almacen, 5m, 1m, D1));
        var borrador = await BorradorAsync(socio, almacen: almacen);
        var l1 = await LineaProductoAsync(borrador.Id, bienes, 1m, 1m);
        var l2 = await LineaProductoAsync(borrador.Id, servicios, 1m, 2m);
        await EjecutarSqlAsync(
            """UPDATE "LineasFacturaVentaBorrador" SET "ImporteLinea" = CASE WHEN "Id" = @L1 THEN 1.005 ELSE 2.005 END WHERE "Id" IN (@L1, @L2)""",
            new { L1 = l1.Id, L2 = l2.Id });

        var resultado = await PostearOkAsync(borrador.Id);

        var factura = await FacturaAsync(resultado.Numero);
        Assert.Equal((3.01m, 0.54m, 3.55m), (factura.ImporteSinIva, factura.ImporteIva, factura.ImporteTotal));
        var asiento = await AsientoAsync(factura.RegistroContableId!.Value);
        Assert.Equal(
            [(CuentaContableIds.CxC, 3.55m, (Guid?)null), (CuentaContableIds.Ventas, -1.01m, GrupoContableIds.ProductoBienes),
             (CuentaContableIds.Ventas, -2.00m, GrupoContableIds.ProductoServicios), (CuentaContableIds.IvaPorPagar, -0.54m, (Guid?)null)],
            asiento.Select(m => (m.CuentaContableId, m.Importe, m.GrupoProductoId)));
        Assert.Equal(0m, asiento.Sum(m => m.Importe));
    }

    // ----- Nada escrito -----

    [Theory]
    [InlineData("general")]
    [InlineData("iva")]
    [InlineData("inventario")]
    [InlineData("cxc")]
    public async Task ReviewFocus2_SinSetup_FalloConLaCombinacion_YNingunaFilaNiNumeroNiIntentoDeEscritura(string setupFaltante)
    {
        var almacen = await _prueba.SembrarAlmacenAsync();
        Guid? grupoCliente = null;
        Guid? cxc = null;
        if (setupFaltante == "cxc")
        {
            cxc = await CuentaAsync(posteoDirecto: false);
            grupoCliente = await GrupoClienteAsync(cxc.Value);
        }

        var socio = await SocioAsync(grupoCliente: grupoCliente);
        // Línea 10000 correcta y con existencia: si algo se escribiera antes de derivar las cuentas, se escribiría esta salida.
        var bueno = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(bueno, almacen, 5m, 1m, D1));
        Guid? setupIvaABorrar = null;
        Guid? malo = setupFaltante switch
        {
            "general" => await ProductoAsync(grupoProducto: await GrupoAsync<GrupoProducto>()),
            "inventario" => await ProductoAsync(grupoInventario: await GrupoAsync<GrupoInventario>()),
            "iva" => await ProductoAsync(grupoIva: await GrupoIvaConSetupAsync(id => setupIvaABorrar = id)),
            _ => null,
        };
        if (malo is { } m)
        {
            await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(m, almacen, 5m, 1m, D1));
        }

        var borrador = await BorradorAsync(socio, almacen: almacen);
        await LineaProductoAsync(borrador.Id, bueno, 1m, 10m);
        if (malo is { } productoMalo)
        {
            await LineaProductoAsync(borrador.Id, productoMalo, 1m, 10m);
        }

        // El setup desaparece DESPUÉS de guardar las líneas (el alta de la línea ya exige el de IVA).
        if (setupIvaABorrar is { } setupIva)
        {
            await EjecutarSqlAsync("""UPDATE "SetupsIva" SET "IsDeleted" = true WHERE "Id" = @Id""", new { Id = setupIva });
        }

        if (cxc is { } cuentaCxC)
        {
            await EjecutarSqlAsync("""UPDATE "CuentasContables" SET "Bloqueada" = true WHERE "Id" = @Id""", new { Id = cuentaCxC });
        }

        var antes = await FotoAsync();

        var resultado = await PostearAsync(borrador.Id);

        Assert.True(resultado.EsFallo);
        var error = Assert.Single(resultado.Errores);
        var esperado = setupFaltante switch
        {
            "general" => ("setup_contable.inexistente", "Lineas[20000].GrupoProductoId"),
            "iva" => ("setup_contable.inexistente", "Lineas[20000].GrupoIvaProductoId"),
            "inventario" => ("setup_contable.inexistente", "Lineas[20000].GrupoInventarioId"),
            _ => ("setup_contable.cuenta_invalida", "GrupoClienteContableId"),
        };
        Assert.Equal(esperado, (error.Codigo, error.Campo));
        if (malo is not null)
        {
            Assert.StartsWith("Línea 20000: ", error.Mensaje);
        }

        Assert.Equal(antes, await FotoAsync());
        Assert.Equal((EstadoFacturaBorrador.Abierta, false, 2L - (malo is null ? 1 : 0), 0L), await EstadoBorradorAsync(borrador.Id));
    }

    [Fact]
    public async Task Revalidacion_ContraElEstadoActual_DevuelveTodosLosErroresConNumeroDeLinea_SinEscribir()
    {
        var termino = await TerminoAsync();
        var socio = await SocioAsync(termino: termino);
        var almacen = await _prueba.SembrarAlmacenAsync();
        var otroAlmacen = await _prueba.SembrarAlmacenAsync();
        var p1 = await ProductoAsync();
        var p2 = await ProductoAsync();
        var p3 = await ProductoAsync();
        var p4 = await ProductoAsync();
        var cuenta = await CuentaAsync(posteoDirecto: true);
        var borrador = await BorradorAsync(socio, almacen: almacen);
        await LineaProductoAsync(borrador.Id, p1, 1m, 10m);
        await LineaProductoAsync(borrador.Id, p2, 1m, 10m, unidad: _prueba.UnidadCja);
        await LineaCuentaAsync(borrador.Id, cuenta, GrupoContableIds.IvaProductoItbis18, 1m, 10m);
        await LineaProductoAsync(borrador.Id, p3, 1m, 10m, almacen: otroAlmacen);
        await LineaProductoAsync(borrador.Id, p4, 1m, 10m);

        await EjecutarSqlAsync("""UPDATE "SociosNegocio" SET "Bloqueado" = 1 WHERE "Id" = @Id""", new { Id = socio });
        await EjecutarSqlAsync("""UPDATE "TerminosPago" SET "IsDeleted" = true WHERE "Id" = @Id""", new { Id = termino });
        await EjecutarSqlAsync("""UPDATE "Productos" SET "Bloqueado" = 1 WHERE "Id" = @Id""", new { Id = p1 });
        await EjecutarSqlAsync("""UPDATE "UnidadesMedidaProducto" SET "CantidadPorUnidadMedida" = 10 WHERE "ProductoId" = @Id""", new { Id = p2 });
        await EjecutarSqlAsync("""UPDATE "CuentasContables" SET "PosteoDirecto" = false WHERE "Id" = @Id""", new { Id = cuenta });
        await EjecutarSqlAsync("""UPDATE "Almacenes" SET "Bloqueado" = true WHERE "Id" = @Id""", new { Id = otroAlmacen });
        await EjecutarSqlAsync("""UPDATE "Productos" SET "IsDeleted" = true WHERE "Id" = @Id""", new { Id = p4 });
        var antes = await FotoAsync();

        var resultado = await PostearAsync(borrador.Id);

        Assert.True(resultado.EsFallo);
        Assert.Equal(
            [
                ("factura.socio_invalido", "SocioNegocioId"),
                ("factura.termino_invalido", "TerminoPagoId"),
                ("factura.producto_invalido", "Lineas[10000].ProductoId"),
                ("factura.factor_cambiado", "Lineas[20000].UnidadMedidaId"),
                ("factura.cuenta_invalida", "Lineas[30000].CuentaContableId"),
                ("factura.almacen_invalido", "Lineas[40000].AlmacenId"),
                ("factura.producto_invalido", "Lineas[50000].ProductoId"),
            ],
            resultado.Errores.Select(e => (e.Codigo, e.Campo)));
        Assert.Contains("Línea 20000", resultado.Errores[3].Mensaje);
        Assert.Equal(antes, await FotoAsync());
        Assert.Equal((EstadoFacturaBorrador.Abierta, false, 5L, 0L), await EstadoBorradorAsync(borrador.Id));
    }

    [Fact]
    public async Task IvaInconsistente_MismoIdentificadorConPorcentajesDistintos_400ConLasLineas_SinEscribir()
    {
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 5m, 1m, D1));
        var borrador = await BorradorAsync(socio, almacen: almacen);
        await LineaProductoAsync(borrador.Id, producto, 1m, 10m);
        var segunda = await LineaProductoAsync(borrador.Id, producto, 1m, 10m);
        await EjecutarSqlAsync("""UPDATE "LineasFacturaVentaBorrador" SET "PorcentajeIva" = 16 WHERE "Id" = @Id""", new { Id = segunda.Id });
        var antes = await FotoAsync();

        var resultado = await PostearAsync(borrador.Id);

        var error = Assert.Single(resultado.Errores);
        Assert.Equal(("factura.iva_inconsistente", "Lineas"), (error.Codigo, error.Campo));
        Assert.Contains("ITBIS18", error.Mensaje);
        Assert.Contains("10000", error.Mensaje);
        Assert.Contains("20000", error.Mensaje);
        Assert.Equal(antes, await FotoAsync());
    }

    // ----- Task 8.5: fechas de registro permitidas (Review Focus 4 de la Fase 8) -----

    [Fact]
    public async Task ReviewFocus4_FechaDeRegistroFueraDelRangoPermitido_400EnFechaRegistro_SinEscribir_YExcepcionesDeUsuario()
    {
        var fechas = new FechasRegistroPrueba(_prueba);
        var amplio = Guid.NewGuid();
        var estrecho = Guid.NewGuid();
        var otro = Guid.NewGuid();
        var socio = await SocioAsync();
        var ingresos = await CuentaAsync(posteoDirecto: true);

        async Task<Guid> BorradorConLineaAsync()
        {
            var borrador = await BorradorAsync(socio); // FechaRegistro = D10
            await LineaCuentaAsync(borrador.Id, ingresos, GrupoContableIds.IvaProductoItbis18, 1m, 100m);
            return borrador.Id;
        }

        try
        {
            // Sin rango (la semilla): se postea en cualquier fecha.
            PostearOk(await PostearComoAsync(await BorradorConLineaAsync(), otro));

            // Rango general que excluye el 10/09: falla en FechaRegistro con el rango y su origen, sin escribir nada.
            await fechas.GeneralAsync(new DateOnly(2026, 9, 11), null);
            var fuera = await BorradorConLineaAsync();
            var antes = await FotoAsync();
            foreach (var usuario in new Guid?[] { null, otro })
            {
                var fallo = await PostearComoAsync(fuera, usuario);
                Assert.Equal(("registro.fecha_no_permitida", "FechaRegistro"), Unico(fallo));
                Assert.Contains("10/09/2026", fallo.Errores[0].Mensaje);
                Assert.Contains("general", fallo.Errores[0].Mensaje);
                Assert.Contains("desde el 11/09/2026", fallo.Errores[0].Mensaje);
            }

            Assert.Equal(antes, await FotoAsync());
            Assert.Equal((EstadoFacturaBorrador.Abierta, false, 1L, 0L), await EstadoBorradorAsync(fuera));

            // Excepción de usuario MÁS AMPLIA que la general: ese usuario sí postea el mismo borrador.
            await fechas.UsuarioAsync(amplio, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 10));
            PostearOk(await PostearComoAsync(fuera, amplio));

            // Excepción MÁS ESTRECHA que la general (sin límites): el usuario falla con SU rango; el sistema postea.
            await fechas.GeneralAsync(null, null);
            await fechas.UsuarioAsync(estrecho, new DateOnly(2026, 9, 11), new DateOnly(2026, 9, 20));
            var estrechoFuera = await BorradorConLineaAsync();
            antes = await FotoAsync();
            var falloEstrecho = await PostearComoAsync(estrechoFuera, estrecho);
            Assert.Equal(("registro.fecha_no_permitida", "FechaRegistro"), Unico(falloEstrecho));
            Assert.Contains("usuario", falloEstrecho.Errores[0].Mensaje);
            Assert.Contains("del 11/09/2026 al 20/09/2026", falloEstrecho.Errores[0].Mensaje);
            Assert.Equal(antes, await FotoAsync());
            PostearOk(await PostearComoAsync(estrechoFuera, null));

            // Dentro del rango general, con los dos límites inclusivos.
            await fechas.GeneralAsync(D10, D10);
            PostearOk(await PostearComoAsync(await BorradorConLineaAsync(), null));
        }
        finally
        {
            await fechas.LimpiarAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SinFilaGeneralDeFechasRegistro_FallaCerrado_LanzaSinEscribir_AunqueElUsuarioTengaExcepcion(bool borradoFisico)
    {
        var fechas = new FechasRegistroPrueba(_prueba);
        var conExcepcion = Guid.NewGuid();
        var socio = await SocioAsync();
        var ingresos = await CuentaAsync(posteoDirecto: true);
        var borrador = await BorradorAsync(socio);
        await LineaCuentaAsync(borrador.Id, ingresos, GrupoContableIds.IvaProductoItbis18, 1m, 100m);

        try
        {
            // Excepción de usuario que permitiría la fecha: aun así la falta de la fila general falla cerrado (Ruling FJ).
            await fechas.UsuarioAsync(conExcepcion, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30));
            await fechas.QuitarGeneralAsync(borradoFisico);
            var antes = await FotoAsync();

            foreach (var usuario in new Guid?[] { null, Guid.NewGuid(), conExcepcion })
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => PostearComoAsync(borrador.Id, usuario));
                Assert.Equal("Falta la fila sembrada de la configuración general de fechas de registro.", ex.Message);
            }

            Assert.Equal(antes, await FotoAsync());
            Assert.Equal((EstadoFacturaBorrador.Abierta, false, 1L, 0L), await EstadoBorradorAsync(borrador.Id));
        }
        finally
        {
            await fechas.LimpiarAsync();
        }

        // Restaurada la fila, el mismo borrador se postea.
        PostearOk(await PostearComoAsync(borrador.Id, conExcepcion));
    }

    [Fact]
    public async Task SinLineasConImporte_400_SinEscribirNiConsumirNumero()
    {
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var vacio = await BorradorAsync(socio, almacen: almacen);
        var soloComentario = await BorradorAsync(socio, almacen: almacen);
        await ComentarioAsync(soloComentario.Id, "Solo texto");
        var antes = await FotoAsync();

        Assert.Equal(("factura.sin_lineas", "Id"), Unico(await PostearAsync(vacio.Id)));
        Assert.Equal(("factura.sin_lineas", "Id"), Unico(await PostearAsync(soloComentario.Id)));

        Assert.Equal(antes, await FotoAsync());
        Assert.Equal((EstadoFacturaBorrador.Abierta, false, 1L, 0L), await EstadoBorradorAsync(soloComentario.Id));
    }

    // ----- Regla de importes (Task 8.4): total 0 solo con 100 % de descuento -----

    [Fact]
    public async Task ReviewFocus5_TotalCero_TodasAl100_DocumentoEInventario_SinClienteNiAsiento_YElBatchDeCostoContabilizaLaSalida()
    {
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        var otrosIngresos = await CuentaAsync(posteoDirecto: true);
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 10m, 4m, D1));
        // La entrada queda contabilizada antes: el batch de después solo verá la salida de la factura.
        Assert.Empty((await PostearCostoAsync(producto)).Pendientes);
        var borrador = await BorradorAsync(socio, almacen: almacen);
        await LineaProductoAsync(borrador.Id, producto, 2m, 10m, descuento: 100m);
        await LineaCuentaAsync(borrador.Id, otrosIngresos, GrupoContableIds.IvaProductoExento, 1m, 25m, descuento: 100m);
        await ComentarioAsync(borrador.Id, "Obsequio");
        var antes = await FotoFilasAsync();

        var resultado = await PostearOkAsync(borrador.Id);

        // Foto: número FV consumido, CONTAB no; documento (3 líneas, 2 de IVA) e inventario (1 salida); cero filas de cliente y contables.
        Assert.Equal(Siguiente(antes["serie FV"]!), resultado.Numero);
        Assert.Equal((0m, (string?)null), (resultado.ImporteTotal, resultado.RegistroContable));
        var despues = await FotoFilasAsync();
        Assert.Equal(antes["serie CONTAB"], despues["serie CONTAB"]);
        foreach (var (tabla, filas) in new (string, long)[]
                 {
                     ("FacturasVenta", 1), ("LineasFacturaVenta", 3), ("LineasIvaFacturaVenta", 2), ("MovimientosProducto", 1),
                     ("MovimientosValor", 1), ("MovimientosCliente", 0), ("MovimientosClienteDetalle", 0), ("RegistrosContables", 0),
                     ("MovimientosContables", 0),
                 })
        {
            Assert.True(long.Parse(antes[tabla]!) + filas == long.Parse(despues[tabla]!), $"{tabla}: {antes[tabla]} -> {despues[tabla]}");
        }

        var factura = await FacturaAsync(resultado.Numero);
        Assert.Equal((0m, 0m, 0m, (long?)null), (factura.ImporteSinIva, factura.ImporteIva, factura.ImporteTotal, factura.RegistroContableId));
        Assert.Equal(
            [(10000, 100m, 20m, 0m, true), (20000, 100m, 25m, 0m, false), (30000, 0m, 0m, 0m, false)],
            (await LineasAsync(resultado.Numero)).Select(l =>
                (l.NumeroLinea, l.PorcentajeDescuentoLinea, l.ImporteDescuentoLinea, l.ImporteLinea, l.MovimientoProductoId is not null)));
        Assert.Equal(
            [("EXENTO", 0m, 0m, 0m, CuentaContableIds.IvaPorPagar), ("ITBIS18", 18m, 0m, 0m, CuentaContableIds.IvaPorPagar)],
            (await LineasIvaAsync(resultado.Numero)).Select(l => (l.IdentificadorIva, l.PorcentajeIva, l.BaseImponible, l.ImporteIva, l.CuentaIvaId)));
        Assert.Empty(await MovimientosClienteAsync(resultado.Numero));
        var salida = Assert.Single(await SalidasAsync(resultado.Numero));
        Assert.Equal((-2m, 0m, -8m, socio), (salida.Cantidad, salida.ImporteVenta, salida.ImporteCosto, salida.SocioNegocioId!.Value));
        Assert.Equal(8m, await _prueba.ConsultarAsync(c => c.ExistenciaAsync(producto, almacen, null)));
        Assert.Equal((EstadoFacturaBorrador.Posteada, false, 3L, 0L), await EstadoBorradorAsync(borrador.Id));

        // Otra factura de total 0: el índice único sobre RegistroContableId admite varios NULL.
        var otro = await BorradorAsync(socio, almacen: almacen);
        await LineaProductoAsync(otro.Id, producto, 1m, 10m, descuento: 100m);
        var segunda = await PostearOkAsync(otro.Id);
        Assert.Equal(Siguiente(resultado.Numero), segunda.Numero);
        Assert.Null((await FacturaAsync(segunda.Numero)).RegistroContableId);
        Assert.Equal(despues["serie CONTAB"], (await FotoFilasAsync())["serie CONTAB"]);

        // El batch de costo de la Fase 5 contabiliza las salidas (costo 8 + 4) contra costo de ventas, aunque la factura no tenga asiento.
        var batch = await PostearCostoAsync(producto);
        Assert.Empty(batch.Pendientes);
        Assert.Equal(2, batch.MovimientosValorContabilizados);
        Assert.Equal(
            [(CuentaContableIds.Inventario, -12m), (CuentaContableIds.CostoVentas, 12m)],
            await CostoContabilizadoAsync(producto, D10));
    }

    [Fact]
    public async Task ReviewFocus5_FacturaMixta_UnaLineaAl100_ElAsientoYElClienteSoloLlevanLasLineasConImporte()
    {
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        var otrosIngresos = await CuentaAsync(posteoDirecto: true);
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 10m, 4m, D1));
        var borrador = await BorradorAsync(socio, almacen: almacen);
        await LineaProductoAsync(borrador.Id, producto, 1m, 10m, descuento: 100m);
        await LineaProductoAsync(borrador.Id, producto, 2m, 50m);
        await LineaCuentaAsync(borrador.Id, otrosIngresos, GrupoContableIds.IvaProductoExento, 1m, 25m, descuento: 100m);

        var resultado = await PostearOkAsync(borrador.Id);

        Assert.Equal(118m, resultado.ImporteTotal);
        var factura = await FacturaAsync(resultado.Numero);
        Assert.Equal(resultado.RegistroContable, await EscalarAsync<string>(
            """SELECT "NumeroRegistro" FROM "RegistrosContables" WHERE "Id" = @Id""", new { Id = factura.RegistroContableId }));
        Assert.Equal((100m, 18m, 118m), (factura.ImporteSinIva, factura.ImporteIva, factura.ImporteTotal));
        Assert.Equal(
            [("EXENTO", 0m, 0m), ("ITBIS18", 100m, 18m)],
            (await LineasIvaAsync(resultado.Numero)).Select(l => (l.IdentificadorIva, l.BaseImponible, l.ImporteIva)));

        // Sin pata para la cuenta al 100 % ni para el IVA exento de base 0.
        var asiento = await AsientoAsync(factura.RegistroContableId!.Value);
        Assert.Equal(
            [(CuentaContableIds.CxC, 118m), (CuentaContableIds.Ventas, -100m), (CuentaContableIds.IvaPorPagar, -18m)],
            asiento.Select(m => (m.CuentaContableId, m.Importe)));
        var cliente = Assert.Single(await MovimientosClienteAsync(resultado.Numero));
        Assert.Equal(118m, cliente.ImporteOriginal);

        // Las dos líneas de producto salen del inventario, la regalada con importe de venta 0.
        Assert.Equal(
            [(-1m, 0m, -4m), (-2m, 100m, -8m)],
            (await SalidasAsync(resultado.Numero)).Select(s => (s.Cantidad, s.ImporteVenta, s.ImporteCosto)));
    }

    [Fact]
    public async Task ReviewFocus5_Revalidacion_PrecioCeroOImporteCeroSinDescuentoTotal_400ConLaLinea_SinEscribir()
    {
        // Borradores guardados antes de la regla (o tocados por datos): el posteo vuelve a exigirla.
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        var otrosIngresos = await CuentaAsync(posteoDirecto: true);
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 10m, 4m, D1));
        var borrador = await BorradorAsync(socio, almacen: almacen);
        var precioCero = await LineaProductoAsync(borrador.Id, producto, 1m, 10m);
        var importeCero = await LineaCuentaAsync(borrador.Id, otrosIngresos, GrupoContableIds.IvaProductoItbis18, 1m, 5m, descuento: 50m);
        var cuentaPrecioCero = await LineaCuentaAsync(borrador.Id, otrosIngresos, GrupoContableIds.IvaProductoItbis18, 1m, 5m, descuento: 100m);
        await LineaProductoAsync(borrador.Id, producto, 1m, 10m, descuento: 100m);
        await LineaProductoAsync(borrador.Id, producto, 1m, 10m);
        await EjecutarSqlAsync(
            """
            UPDATE "LineasFacturaVentaBorrador" SET "PrecioUnitario" = 0, "ImporteDescuentoLinea" = 0, "ImporteLinea" = 0 WHERE "Id" IN (@A, @C);
            UPDATE "LineasFacturaVentaBorrador" SET "ImporteDescuentoLinea" = 5, "ImporteLinea" = 0 WHERE "Id" = @B;
            """,
            new { A = precioCero.Id, B = importeCero.Id, C = cuentaPrecioCero.Id });
        var antes = await FotoAsync();

        var resultado = await PostearAsync(borrador.Id);

        Assert.True(resultado.EsFallo);
        Assert.Equal(
            [("factura.precio_invalido", "Lineas[10000].PrecioUnitario"), ("factura.importe_invalido", "Lineas[20000].Cantidad"),
             ("factura.precio_invalido", "Lineas[30000].PrecioUnitario")],
            resultado.Errores.Select(e => (e.Codigo, e.Campo)));
        Assert.All(resultado.Errores, e => Assert.StartsWith("Línea ", e.Mensaje));
        Assert.Contains("100 %", resultado.Errores[0].Mensaje);
        Assert.Equal(antes, await FotoAsync());
        Assert.Equal((EstadoFacturaBorrador.Abierta, false, 5L, 0L), await EstadoBorradorAsync(borrador.Id));
    }

    [Fact]
    public async Task ReviewFocus5_ExistenciaInsuficienteEnUnaLinea_FallaConSuNumero_YNoDejaNingunaFila()
    {
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var p1 = await ProductoAsync();
        var p2 = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(p1, almacen, 5m, 1m, D1));
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(p2, almacen, 3m, 1m, D1));
        var borrador = await BorradorAsync(socio, almacen: almacen);
        await LineaProductoAsync(borrador.Id, p1, 2m, 10m);
        await LineaProductoAsync(borrador.Id, p2, 9m, 10m);
        var antes = await FotoFilasAsync();
        var ultimo = await UltimoNumeroFvAsync();

        var resultado = await PostearAsync(borrador.Id);

        var error = Assert.Single(resultado.Errores);
        Assert.Equal(("inventario.existencia_insuficiente", "Lineas[20000].Cantidad"), (error.Codigo, error.Campo));
        Assert.StartsWith("Línea 20000: ", error.Mensaje);
        Assert.Equal(antes, await FotoFilasAsync());
        Assert.Equal(ultimo, await UltimoNumeroFvAsync());
        Assert.Equal((1L, 1L, 0L), await _prueba.ContarFilasAsync(p1));
        Assert.Equal((EstadoFacturaBorrador.Abierta, false, 2L, 0L), await EstadoBorradorAsync(borrador.Id));
    }

    [Fact]
    public async Task CantidadNoExactaEnLaUnidadBase_FallaEnLineasNCantidad_SinRedondearNiEscribir()
    {
        // Líneas guardadas válidas y alteradas después (dato legado / cambio directo): el posteo las revalida con el factor
        // congelado y los decimales actuales de la unidad base, en vez de redondear la salida de inventario.
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var und = await ProductoAsync();
        var metro = await ProductoMetroConCajaDeTresAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(und, almacen, 10m, 1m, D1));
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(metro, almacen, 4m, 1m, D1, unidadId: _prueba.UnidadCja));
        var borrador = await BorradorAsync(socio, almacen: almacen);
        await LineaProductoAsync(borrador.Id, und, 1m, 10m);
        var dosYMedio = await LineaProductoAsync(borrador.Id, und, 2m, 10m);
        var medioCaja = await LineaProductoAsync(borrador.Id, metro, 0.5m, 30m, unidad: _prueba.UnidadCja);
        var tercioCaja = await LineaProductoAsync(borrador.Id, metro, 1m, 30m, unidad: _prueba.UnidadCja);
        Assert.Equal(3m, medioCaja.CantidadPorUnidadMedida);
        await EjecutarSqlAsync("""UPDATE "LineasFacturaVentaBorrador" SET "Cantidad" = 2.5 WHERE "Id" = @Id""", new { dosYMedio.Id });
        await EjecutarSqlAsync("""UPDATE "LineasFacturaVentaBorrador" SET "Cantidad" = 0.333333 WHERE "Id" = @Id""", new { tercioCaja.Id });
        var antes = await FotoAsync();

        var resultado = await PostearAsync(borrador.Id);

        Assert.True(resultado.EsFallo);
        Assert.Equal(
            [("conversion.cantidad_no_exacta", "Lineas[20000].Cantidad"), ("conversion.cantidad_no_exacta", "Lineas[40000].Cantidad")],
            resultado.Errores.Select(e => (e.Codigo, e.Campo)));
        Assert.StartsWith("Línea 20000: ", resultado.Errores[0].Mensaje);
        Assert.Contains("no admite decimales", resultado.Errores[0].Mensaje);
        Assert.Contains("como máximo 2 decimales", resultado.Errores[1].Mensaje);
        Assert.Equal(antes, await FotoAsync());
        Assert.Equal((EstadoFacturaBorrador.Abierta, false, 4L, 0L), await EstadoBorradorAsync(borrador.Id));

        // Corregidas, la caja de 3 con 0.5 sale 1.5 MT exactos del inventario (entrada 4 CJA = 12 MT; salen 1.5 + 3 MT).
        await EjecutarSqlAsync("""UPDATE "LineasFacturaVentaBorrador" SET "Cantidad" = 2 WHERE "Id" = @Id""", new { dosYMedio.Id });
        await EjecutarSqlAsync("""UPDATE "LineasFacturaVentaBorrador" SET "Cantidad" = 1 WHERE "Id" = @Id""", new { tercioCaja.Id });
        await PostearOkAsync(borrador.Id);
        Assert.Equal(7m, await _prueba.ConsultarAsync(c => c.ExistenciaAsync(und, almacen, null)));
        Assert.Equal(7.5m, await _prueba.ConsultarAsync(c => c.ExistenciaAsync(metro, almacen, null)));
    }

    [Fact]
    public async Task LineaConErrorDeValidacion_NoSeDerivanSusCuentas_ComoMuchoUnErrorPorLinea()
    {
        // Producto bloqueado para la venta Y con un grupo de producto sin setup general: solo el error de validación de la línea.
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync(grupoProducto: await GrupoAsync<GrupoProducto>());
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 5m, 1m, D1));
        var borrador = await BorradorAsync(socio, almacen: almacen);
        await LineaProductoAsync(borrador.Id, producto, 1m, 10m);
        await EjecutarSqlAsync("""UPDATE "Productos" SET "Bloqueado" = 1 WHERE "Id" = @Id""", new { Id = producto });
        var antes = await FotoAsync();

        var resultado = await PostearAsync(borrador.Id);

        Assert.Equal(("factura.producto_invalido", "Lineas[10000].ProductoId"), Unico(resultado));
        Assert.Equal(antes, await FotoAsync());
    }

    [Fact]
    public async Task MismoIdentificadorDeIvaConCuentasDistintas_IvaInconsistente_SinEscribir()
    {
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var otraCuentaIva = await CuentaAsync(posteoDirecto: false);
        var normal = await ProductoAsync();
        var conOtraCuenta = await ProductoAsync(grupoIva: await GrupoIvaConSetupAsync(_ => { }, otraCuentaIva));
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(normal, almacen, 5m, 1m, D1));
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(conOtraCuenta, almacen, 5m, 1m, D1));
        var borrador = await BorradorAsync(socio, almacen: almacen);
        await LineaProductoAsync(borrador.Id, normal, 1m, 10m);
        await LineaProductoAsync(borrador.Id, conOtraCuenta, 1m, 10m);
        var antes = await FotoAsync();

        var resultado = await PostearAsync(borrador.Id);

        var error = Assert.Single(resultado.Errores);
        Assert.Equal(("factura.iva_inconsistente", "Lineas"), (error.Codigo, error.Campo));
        Assert.Contains("ITBIS18", error.Mensaje);
        Assert.Contains("cuentas de IVA distintas", error.Mensaje);
        Assert.Contains("10000, 20000", error.Mensaje);
        Assert.Equal(antes, await FotoAsync());
        Assert.Equal((EstadoFacturaBorrador.Abierta, false, 2L, 0L), await EstadoBorradorAsync(borrador.Id));
    }

    [Fact]
    public async Task FacturarABloqueadoParaFacturacion_FallaEnSocioNegocioFacturarAId_SinEscribir()
    {
        var venderA = await SocioAsync();
        var facturarA = await SocioAsync(nombre: "Casa matriz bloqueada");
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 5m, 1m, D1));
        var borrador = await BorradorAsync(venderA, facturarA, almacen);
        await LineaProductoAsync(borrador.Id, producto, 1m, 10m);
        await EjecutarSqlAsync("""UPDATE "SociosNegocio" SET "Bloqueado" = 1 WHERE "Id" = @Id""", new { Id = facturarA });
        var antes = await FotoAsync();

        var resultado = await PostearAsync(borrador.Id);

        Assert.Equal(("factura.socio_invalido", "SocioNegocioFacturarAId"), Unico(resultado));
        Assert.Equal(antes, await FotoAsync());
        Assert.Equal((EstadoFacturaBorrador.Abierta, false, 1L, 0L), await EstadoBorradorAsync(borrador.Id));
    }

    [Fact]
    public async Task UnidadDeLaLineaBorrada_FallaEnLineasNUnidadMedidaId_SinEscribir()
    {
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 50m, 1m, D1));
        var unidad = await UnidadPropiaAsync(producto, factor: 6m);
        var borrador = await BorradorAsync(socio, almacen: almacen);
        await LineaProductoAsync(borrador.Id, producto, 1m, 10m);
        await LineaProductoAsync(borrador.Id, producto, 1m, 60m, unidad: unidad);
        await EjecutarSqlAsync("""UPDATE "UnidadesMedida" SET "IsDeleted" = true WHERE "Id" = @Id""", new { Id = unidad });
        var antes = await FotoAsync();

        var resultado = await PostearAsync(borrador.Id);

        Assert.Equal(("factura.unidad_invalida", "Lineas[20000].UnidadMedidaId"), Unico(resultado));
        Assert.Equal(antes, await FotoAsync());
        Assert.Equal((EstadoFacturaBorrador.Abierta, false, 2L, 0L), await EstadoBorradorAsync(borrador.Id));
    }

    // ----- Concurrencia -----

    [Fact]
    public async Task ReviewFocus3_CuatroPosteosConcurrentesDeFacturasDistintas_NumerosConsecutivosSinHuecos()
    {
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 100m, 1m, D1));
        var borradores = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            var borrador = await BorradorAsync(await SocioAsync(), almacen: almacen);
            await LineaProductoAsync(borrador.Id, producto, 1m + i, 10m);
            borradores.Add(borrador.Id);
        }

        var ultimo = long.Parse(await UltimoNumeroFvAsync());

        var resultados = await EnParaleloAsync([.. borradores.Select(id => (Func<Task<Result<ResultadoPosteoFactura>>>)(() => PostearAsync(id)))]);

        Assert.All(resultados, r =>
        {
            Assert.Null(r.Excepcion);
            Assert.True(r.Resultado!.EsExito, r.Resultado.EsFallo ? r.Resultado.Errores[0].Mensaje : null);
        });
        Assert.Equal(
            [ultimo + 1, ultimo + 2, ultimo + 3, ultimo + 4],
            resultados.Select(r => long.Parse(r.Resultado!.Valor.Numero)).Order());
        Assert.Equal(ultimo + 4, long.Parse(await UltimoNumeroFvAsync()));
        Assert.Equal(90m, await _prueba.ConsultarAsync(c => c.ExistenciaAsync(producto, almacen, null)));
    }

    [Fact]
    public async Task ReviewFocus3_CuatroPosteosConcurrentesConProductosDistintos_ContiendaRealDeLaSerieFv_SinHuecos()
    {
        // Cada factura con su propio producto (y almacén): los posteos no se serializan en el bloqueo de producto sino en la
        // línea de la serie FV, que es la contienda que debe dar números consecutivos sin huecos.
        var borradores = new List<(Guid Borrador, Guid Producto, Guid Almacen)>();
        for (var i = 0; i < 4; i++)
        {
            var almacen = await _prueba.SembrarAlmacenAsync();
            var producto = await ProductoAsync();
            await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 10m, 1m, D1));
            var borrador = await BorradorAsync(await SocioAsync(), almacen: almacen);
            await LineaProductoAsync(borrador.Id, producto, 1m + i, 10m);
            borradores.Add((borrador.Id, producto, almacen));
        }

        var ultimo = long.Parse(await UltimoNumeroFvAsync());

        var resultados = await EnParaleloAsync([.. borradores.Select(b => (Func<Task<Result<ResultadoPosteoFactura>>>)(() => PostearAsync(b.Borrador)))]);

        Assert.All(resultados, r =>
        {
            Assert.Null(r.Excepcion);
            Assert.True(r.Resultado!.EsExito, r.Resultado.EsFallo ? r.Resultado.Errores[0].Mensaje : null);
        });
        Assert.Equal(
            [ultimo + 1, ultimo + 2, ultimo + 3, ultimo + 4],
            resultados.Select(r => long.Parse(r.Resultado!.Valor.Numero)).Order());
        Assert.Equal(ultimo + 4, long.Parse(await UltimoNumeroFvAsync()));

        for (var i = 0; i < borradores.Count; i++)
        {
            var (_, producto, almacen) = borradores[i];
            Assert.Equal(9m - i, await _prueba.ConsultarAsync(c => c.ExistenciaAsync(producto, almacen, null)));
        }
    }

    [Fact]
    public async Task AltaYCambioDeSocioDelBorrador_EsperanAlBorradoConcurrenteDelSocio_YLoVenBorrado()
    {
        // Una transacción ajena tiene el socio FOR UPDATE (como el borrado del socio antes de evaluar su uso) y lo borra. El alta
        // (y el cambio de socio) del borrador toman el socio FOR SHARE ANTES de validarlo: esperan al commit y lo ven borrado.
        // Sin ese bloqueo, leerían la versión anterior (viva) y dejarían un borrador de un socio borrado.
        var vivo = await SocioAsync();
        var borrador = await BorradorAsync(vivo);
        foreach (var caso in new[] { "alta", "cambio" })
        {
            var aBorrar = await SocioAsync();
            await using var conexion = _prueba.NuevaConexion();
            await conexion.OpenAsync();
            await using var tx = await conexion.BeginTransactionAsync();
            await conexion.ExecuteAsync("""SELECT 1 FROM "SociosNegocio" WHERE "Id" = @Id FOR UPDATE""", new { Id = aBorrar }, tx);

            Task<Result<FacturaVentaBorradorResponse>> operacion = caso == "alta"
                ? Task.Run(() => CrearBorradorAsync(vivo, aBorrar))
                : Task.Run(async () =>
                {
                    var actual = Ok(await ObtenerBorradorAsync(borrador.Id));
                    return await ActualizarBorradorAsync(borrador.Id, aBorrar, actual.Xmin);
                });
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            Assert.False(operacion.IsCompleted, $"{caso}: debió esperar al bloqueo del socio");

            await conexion.ExecuteAsync("""UPDATE "SociosNegocio" SET "IsDeleted" = true WHERE "Id" = @Id""", new { Id = aBorrar }, tx);
            await tx.CommitAsync();

            var resultado = await operacion.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(("factura.socio_invalido", "SocioNegocioFacturarAId"), Unico(resultado));
            Assert.Equal(0L, await EscalarAsync<long>(
                """SELECT COUNT(*) FROM "FacturasVentaBorrador" WHERE "SocioNegocioFacturarAId" = @Id AND NOT "IsDeleted" """, new { Id = aBorrar }));
        }
    }

    [Fact]
    public async Task ReviewFocus3_ElMismoBorradorDosVecesEnParalelo_UnoGanaYElOtro409_SinDuplicar()
    {
        for (var ronda = 0; ronda < 3; ronda++)
        {
            var almacen = await _prueba.SembrarAlmacenAsync();
            var producto = await ProductoAsync();
            await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 10m, 1m, D1));
            var borrador = await BorradorAsync(await SocioAsync(), almacen: almacen);
            await LineaProductoAsync(borrador.Id, producto, 2m, 10m);
            await LineaProductoAsync(borrador.Id, producto, 3m, 10m);

            var resultados = await EnParaleloAsync(() => PostearAsync(borrador.Id), () => PostearAsync(borrador.Id));

            Assert.All(resultados, r => Assert.Null(r.Excepcion));
            Assert.Single(resultados, r => r.Resultado!.EsExito);
            var perdedora = Assert.Single(resultados, r => r.Resultado!.EsFallo);
            Assert.Equal(("factura_borrador.posteada.conflicto", "Id"), Unico(perdedora.Resultado!));
            Assert.Equal(1L, await EscalarAsync<long>(
                """SELECT COUNT(*) FROM "FacturasVenta" WHERE "NumeroBorrador" = @N""", new { N = borrador.Numero }));
            Assert.Equal((3L, 3L, 2L), await _prueba.ContarFilasAsync(producto));
            Assert.Equal(5m, await _prueba.ConsultarAsync(c => c.ExistenciaAsync(producto, almacen, null)));
        }
    }

    [Fact]
    public async Task DentroDeOtraTransaccion_Lanza()
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var handler = ActivatorUtilities.CreateInstance<PostearFacturaVentaCommandHandler>(scope.ServiceProvider);
        await using var tx = await unitOfWork.BeginTransactionAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.Handle(new PostearFacturaVentaCommand(Guid.NewGuid()), default));
    }

    // ----- Guarda de borrado de socios -----

    [Fact]
    public async Task BorrarSocio_409_ConBorradorVivo_FacturaComoVenderAOFacturarA_OMovimientos_YSinUsoSeBorra()
    {
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 10m, 1m, D1));

        // Borrador vivo (vender-a y facturar-a).
        var conBorradorV = await SocioAsync();
        var conBorradorF = await SocioAsync();
        var borrador = await BorradorAsync(conBorradorV, conBorradorF, almacen);
        Assert.Equal("socio_negocio.conflicto", Unico(await BorrarSocioAsync(conBorradorV)).Codigo);
        Assert.Equal("socio_negocio.conflicto", Unico(await BorrarSocioAsync(conBorradorF)).Codigo);

        // Factura posteada (el borrador queda Posteada): sigue en uso por la factura, el libro de clientes, el contable y el inventario.
        await LineaProductoAsync(borrador.Id, producto, 1m, 10m);
        await PostearOkAsync(borrador.Id);
        Assert.Equal("socio_negocio.conflicto", Unico(await BorrarSocioAsync(conBorradorV)).Codigo);
        Assert.Equal("socio_negocio.conflicto", Unico(await BorrarSocioAsync(conBorradorF)).Codigo);

        // Un borrador BORRADO no cuenta.
        var conBorradorBorrado = await SocioAsync();
        var otro = await BorradorAsync(conBorradorBorrado, almacen: almacen);
        await using (var scope = _prueba.Provider.CreateAsyncScope())
        {
            var borrar = ActivatorUtilities.CreateInstance<DeleteFacturaVentaBorradorCommandHandler>(scope.ServiceProvider);
            Assert.True((await borrar.Handle(new DeleteFacturaVentaBorradorCommand(otro.Id), default)).EsExito);
        }

        Assert.True((await BorrarSocioAsync(conBorradorBorrado)).EsExito);

        // Cada libro por separado.
        var soloInventario = await SocioAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Salida(producto, almacen, 1m, D10) with { SocioNegocioId = soloInventario });
        Assert.Equal("socio_negocio.conflicto", Unico(await BorrarSocioAsync(soloInventario)).Codigo);

        var soloCliente = await SocioAsync();
        await EnTransaccionAsync(sp => sp.GetRequiredService<IRegistroMovimientosCliente>().RegistrarAsync(new MovimientoClienteSolicitud(
            soloCliente, D10, D10, D10, TipoDocumentoCliente.Factura, $"G{Guid.NewGuid():N}"[..20], null, 5m,
            GrupoContableIds.ClienteContableGeneral, CuentaContableIds.CxC, TipoOrigenMovimiento.FacturaVenta, "GUARDA")));
        Assert.Equal("socio_negocio.conflicto", Unico(await BorrarSocioAsync(soloCliente)).Codigo);

        var soloContable = await SocioAsync();
        await EnTransaccionAsync(sp => sp.GetRequiredService<IRegistroContable>().RegistrarAsync(new AsientoContable(
            D10, D10, TipoDocumentoContable.Ninguno, null, "Asiento de la guarda", TipoOrigenMovimiento.FacturaVenta, "GUARDA",
            [new LineaAsiento(CuentaContableIds.Caja, 1m, null, soloContable), new LineaAsiento(CuentaContableIds.Ventas, -1m, null)])));
        Assert.Equal("socio_negocio.conflicto", Unico(await BorrarSocioAsync(soloContable)).Codigo);

        // Sin uso: se borra.
        var libre = await SocioAsync();
        Assert.True((await BorrarSocioAsync(libre)).EsExito);
        Assert.True(await EscalarAsync<bool>("""SELECT "IsDeleted" FROM "SociosNegocio" WHERE "Id" = @Id""", new { Id = libre }));
    }

    // ----- Series del borrador (spec no-series) -----

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

    [Fact]
    public async Task Modificar_SerieRegistro_SoloElAdministrador_ValidaTipoYActividad_YUnLiberadoNoLaCambia()
    {
        var socio = await SocioAsync();
        var (otra, _, _) = await SeriesPrueba.CrearAsync(fixture.AppConnectionString, TipoDocumentoSerie.FacturaVenta);
        var (deBorrador, _, _) = await SeriesPrueba.CrearAsync(fixture.AppConnectionString, TipoDocumentoSerie.BorradorFacturaVenta);
        var borrador = Ok(await CrearBorradorConSeriesAsync(socio, null, null, null));
        Assert.Equal(("serie.no_editable", "SerieRegistroId"), Unico(await CambiarSerieRegistroAsync(borrador.Id, otra, borrador.Xmin, administrador: false)));

        Assert.Equal(("numeracion.tipo_incorrecto", "SerieRegistroId"), Unico(await CambiarSerieRegistroAsync(borrador.Id, deBorrador, borrador.Xmin)));
        var cambiado = Ok(await CambiarSerieRegistroAsync(borrador.Id, otra, borrador.Xmin));
        Assert.Equal(otra, cambiado.SerieRegistroId);

        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        await LineaProductoAsync(borrador.Id, producto, 1m, 10m, almacen: almacen);
        await LiberarAsync(borrador.Id);
        var liberado = Ok(await ObtenerBorradorAsync(borrador.Id));
        Assert.Equal(("factura.liberada", "Id"), Unico(await CambiarSerieRegistroAsync(borrador.Id, SerieFacturaVentaIds.SeriePosteadaId, liberado.Xmin)));
    }

    [Fact]
    public async Task Postear_UsaLaSerieDeRegistroDelBorrador_YElBorradorQuedaPosteadaConSusLineas()
    {
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 5m, 1m, D1));
        var (serieRegistro, lineaRegistro, prefijo) = await SeriesPrueba.CrearAsync(fixture.AppConnectionString, TipoDocumentoSerie.FacturaVenta, aviso: 1);
        var borrador = Ok(await CrearBorradorConSeriesAsync(await SocioAsync(), almacen, null, serieRegistro));
        await LineaProductoAsync(borrador.Id, producto, 2m, 10m);
        var ultimoFv = await UltimoNumeroFvAsync();

        var resultado = await PostearOkAsync(borrador.Id);

        Assert.Equal($"{prefijo}000001", resultado.Numero);
        Assert.Contains("número de aviso", resultado.AvisoNumeracion);
        Assert.Equal($"{prefijo}000001", await SeriesPrueba.UltimoAsync(fixture.AppConnectionString, lineaRegistro));
        Assert.Equal(ultimoFv, await UltimoNumeroFvAsync());
        Assert.Equal(borrador.Numero, (await FacturaAsync(resultado.Numero)).NumeroBorrador);
        Assert.Equal((EstadoFacturaBorrador.Posteada, resultado.Numero), await EstadoYFacturaAsync(borrador.Id));
        Assert.Equal((EstadoFacturaBorrador.Posteada, false, 1L, 0L), await EstadoBorradorAsync(borrador.Id));
    }

    [Theory]
    [InlineData("inactiva", "numeracion.serie_inactiva")]
    [InlineData("otro_tipo", "numeracion.tipo_incorrecto")]
    public async Task ReviewFocus2_SerieRegistroInvalidaAlPostear_400EnSerieRegistroId_SinEscribirNada(string cambio, string codigo)
    {
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 5m, 1m, D1));
        var (serieRegistro, lineaRegistro, _) = await SeriesPrueba.CrearAsync(fixture.AppConnectionString, TipoDocumentoSerie.FacturaVenta);
        var borrador = Ok(await CrearBorradorConSeriesAsync(await SocioAsync(), almacen, null, serieRegistro));
        await LineaProductoAsync(borrador.Id, producto, 1m, 10m);
        await SeriesPrueba.EjecutarAsync(fixture.AppConnectionString, cambio == "inactiva"
            ? """UPDATE "Series" SET "Activa" = false WHERE "Id" = @Id"""
            : """UPDATE "Series" SET "TipoDocumento" = 5 WHERE "Id" = @Id""", new { Id = serieRegistro });
        var antes = await FotoAsync();
        var estadoAntes = await EstadoYFacturaAsync(borrador.Id);

        var resultado = await PostearAsync(borrador.Id);

        Assert.Equal((codigo, "SerieRegistroId"), Unico(resultado));
        Assert.Equal(antes, await FotoAsync());
        Assert.Equal("", await SeriesPrueba.UltimoAsync(fixture.AppConnectionString, lineaRegistro));
        Assert.Equal(estadoAntes, await EstadoYFacturaAsync(borrador.Id));
        Assert.Equal((EstadoFacturaBorrador.Abierta, false, 1L, 0L), await EstadoBorradorAsync(borrador.Id));
    }

    [Fact]
    public async Task BorradorPosteada_EsDeSoloLectura_TodaEscrituraDevuelveConflicto()
    {
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 5m, 1m, D1));
        var borrador = await BorradorAsync(await SocioAsync(), almacen: almacen);
        var linea = await LineaProductoAsync(borrador.Id, producto, 1m, 10m);
        await PostearOkAsync(borrador.Id);
        var posteado = Ok(await ObtenerBorradorAsync(borrador.Id));
        var antes = await FotoAsync();

        var estadoAntes = await EstadoYFacturaAsync(borrador.Id);

        // Un scope por handler, como en una petición real (un fallo deshace y cierra la transacción de su unidad de trabajo).
        async Task<Result> EnScopeAsync(Func<IServiceProvider, Task<Result>> accion)
        {
            await using var scope = _prueba.Provider.CreateAsyncScope();
            return await accion(scope.ServiceProvider);
        }

        var resultados = new Result[]
        {
            await EnScopeAsync(async sp => await ActivatorUtilities.CreateInstance<UpdateFacturaVentaBorradorCommandHandler>(sp).Handle(
                new UpdateFacturaVentaBorradorCommand(borrador.Id, null, null, null, null, null, null, "x", posteado.Xmin), default)),
            // La serie de registro solo la envía el Administrador (spec alta rápida): así llega a la comprobación de Posteada.
            await EnScopeAsync(async sp => await ActivatorUtilities.CreateInstance<UpdateFacturaVentaBorradorCommandHandler>(
                sp, (IUsuarioActual)new FechasRegistroPrueba.Usuario(null, EsAdministrador: true)).Handle(
                new UpdateFacturaVentaBorradorCommand(borrador.Id, null, null, null, null, null, null, null, posteado.Xmin,
                    SerieFacturaVentaIds.SeriePosteadaId), default)),
            await EnScopeAsync(async sp => await ActivatorUtilities.CreateInstance<CreateLineaFacturaVentaBorradorCommandHandler>(sp).Handle(
                new CreateLineaFacturaVentaBorradorCommand(borrador.Id, TipoLineaFactura.Comentario, null, null, "x", null, null, null, null, null, null), default)),
            await EnScopeAsync(async sp => await ActivatorUtilities.CreateInstance<UpdateLineaFacturaVentaBorradorCommandHandler>(sp).Handle(
                new UpdateLineaFacturaVentaBorradorCommand(linea.Id, TipoLineaFactura.Producto, producto, null, null, null, null, 2m, 10m, null, null, linea.Xmin), default)),
            await EnScopeAsync(async sp => await ActivatorUtilities.CreateInstance<DeleteLineaFacturaVentaBorradorCommandHandler>(sp).Handle(
                new DeleteLineaFacturaVentaBorradorCommand(linea.Id), default)),
            await EnScopeAsync(async sp => await ActivatorUtilities.CreateInstance<LiberarFacturaVentaBorradorCommandHandler>(sp).Handle(
                new LiberarFacturaVentaBorradorCommand(borrador.Id), default)),
            await EnScopeAsync(async sp => await ActivatorUtilities.CreateInstance<ReabrirFacturaVentaBorradorCommandHandler>(sp).Handle(
                new ReabrirFacturaVentaBorradorCommand(borrador.Id), default)),
            await EnScopeAsync(async sp => await ActivatorUtilities.CreateInstance<DeleteFacturaVentaBorradorCommandHandler>(sp).Handle(
                new DeleteFacturaVentaBorradorCommand(borrador.Id), default)),
            await PostearAsync(borrador.Id),
        };

        Assert.All(resultados, r => Assert.Equal("factura_borrador.posteada.conflicto", Assert.Single(r.Errores).Codigo));
        Assert.Equal(antes, await FotoAsync());
        Assert.Equal(estadoAntes, await EstadoYFacturaAsync(borrador.Id));
        Assert.Equal(posteado.Xmin, Ok(await ObtenerBorradorAsync(borrador.Id)).Xmin);
        Assert.Equal((EstadoFacturaBorrador.Posteada, false, 1L, 0L), await EstadoBorradorAsync(borrador.Id));
    }

    [Fact]
    public async Task Listado_PorDefectoSinPosteadas_YConFiltroPosteada()
    {
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 5m, 1m, D1));
        var abierto = await BorradorAsync(socio, almacen: almacen);
        var posteado = await BorradorAsync(socio, almacen: almacen);
        await LineaProductoAsync(posteado.Id, producto, 1m, 10m);
        await PostearOkAsync(posteado.Id);

        var porDefecto = await ListarBorradoresAsync(socio, null);
        var soloPosteadas = await ListarBorradoresAsync(socio, EstadoFacturaBorrador.Posteada);

        Assert.Equal([abierto.Id], porDefecto.Items.Select(b => b.Id));
        var fila = Assert.Single(soloPosteadas.Items);
        Assert.Equal((posteado.Id, EstadoFacturaBorrador.Posteada), (fila.Id, fila.Estado));
        Assert.NotNull(fila.FacturaVentaNumero);
    }

    // ----- Copiar a borrador (spec no-series, Parte 2) -----

    [Fact]
    public async Task Copia_CabeceraConDatosActualesDelCliente_LineasYSeriesConfiguradas()
    {
        var terminoOriginal = await TerminoAsync();
        var socio = await SocioAsync(termino: terminoOriginal);
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 10m, 1m, D1));
        var borrador = await BorradorAsync(socio, almacen: almacen);
        await LineaProductoAsync(borrador.Id, producto, 2m, 10m, descuento: 5m);
        await ComentarioAsync(borrador.Id, "Entregar por la mañana");
        var factura = await PostearOkAsync(borrador.Id);
        // Después de facturar, el cliente cambia de nombre y de término de pago (30 días).
        var terminoNuevo = await TerminoAsync();
        await EjecutarSqlAsync("""UPDATE "SociosNegocio" SET "NombreComercial" = 'Renombrado', "TerminoPagoId" = @T WHERE "Id" = @Id""",
            new { T = terminoNuevo, Id = socio });
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var copia = Ok(await CopiarAsync(factura.Numero));

        Assert.Empty(copia.Avisos);
        var nuevo = Ok(await ObtenerBorradorAsync(copia.BorradorId));
        Assert.Equal(copia.Numero, nuevo.Numero);
        Assert.Equal(("Renombrado", terminoNuevo, EstadoFacturaBorrador.Abierta), (nuevo.NombreFacturacion, nuevo.TerminoPagoId, nuevo.Estado));
        Assert.Equal((hoy, hoy, hoy.AddDays(30), almacen), (nuevo.FechaRegistro, nuevo.FechaDocumento, nuevo.FechaVencimiento, nuevo.AlmacenId));
        Assert.Equal((SerieFacturaVentaIds.SerieBorradorId, SerieFacturaVentaIds.SeriePosteadaId), (nuevo.SerieBorradorId, nuevo.SerieRegistroId));
        Assert.NotEqual(borrador.Numero, nuevo.Numero);
        var lineas = await LineasBorradorAsync(copia.BorradorId);
        Assert.Equal([TipoLineaFactura.Producto, TipoLineaFactura.Comentario], lineas.Select(l => l.Tipo));
        Assert.Equal((producto, 2m, 10m, 5m), (lineas[0].ProductoId!.Value, lineas[0].Cantidad, lineas[0].PrecioUnitario, lineas[0].PorcentajeDescuentoLinea));
        Assert.Equal("Entregar por la mañana", lineas[1].Descripcion);
    }

    [Fact]
    public async Task Copia_LineaDeCuentaContable_SeCopiaConSuGrupoDeIva()
    {
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var cuenta = await CuentaAsync(posteoDirecto: true);
        var borrador = await BorradorAsync(socio, almacen: almacen);
        await LineaCuentaAsync(borrador.Id, cuenta, GrupoContableIds.IvaProductoItbis18, 3m, 20m, descuento: 10m);
        var factura = await PostearOkAsync(borrador.Id);

        var copia = Ok(await CopiarAsync(factura.Numero));

        Assert.Empty(copia.Avisos);
        var linea = Assert.Single(await LineasBorradorAsync(copia.BorradorId));
        Assert.Equal((TipoLineaFactura.CuentaContable, cuenta, GrupoContableIds.IvaProductoItbis18, 3m, 20m, 10m),
            (linea.Tipo, linea.CuentaContableId!.Value, linea.GrupoIvaProductoId!.Value, linea.Cantidad, linea.PrecioUnitario, linea.PorcentajeDescuentoLinea));
    }

    [Fact]
    public async Task Copia_ProductoBloqueadoOBorrado_SeOmiteConAviso()
    {
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var bueno = await ProductoAsync();
        var bloqueado = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(bueno, almacen, 10m, 1m, D1));
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(bloqueado, almacen, 10m, 1m, D1));
        var borrador = await BorradorAsync(socio, almacen: almacen);
        await LineaProductoAsync(borrador.Id, bueno, 1m, 10m);
        await LineaProductoAsync(borrador.Id, bloqueado, 1m, 10m);
        var factura = await PostearOkAsync(borrador.Id);
        await EjecutarSqlAsync("""UPDATE "Productos" SET "Bloqueado" = @B WHERE "Id" = @Id""", new { B = (short)BloqueoProducto.Todo, Id = bloqueado });

        var copia = Ok(await CopiarAsync(factura.Numero));

        var aviso = Assert.Single(copia.Avisos);
        Assert.Contains("Línea 20000", aviso);
        Assert.Equal([bueno], (await LineasBorradorAsync(copia.BorradorId)).Select(l => l.ProductoId!.Value));
    }

    [Fact]
    public async Task Copia_AlmacenDeLaFacturaBloqueado_UsaElPredeterminadoConAviso()
    {
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 10m, 1m, D1));
        var borrador = await BorradorAsync(socio, almacen: almacen);
        await LineaProductoAsync(borrador.Id, producto, 1m, 10m);
        var factura = await PostearOkAsync(borrador.Id);
        await EjecutarSqlAsync("""UPDATE "Almacenes" SET "Bloqueado" = true WHERE "Id" = @Id""", new { Id = almacen });
        try
        {
            var copia = Ok(await CopiarAsync(factura.Numero));

            var aviso = Assert.Single(copia.Avisos);
            Assert.Contains("almacén predeterminado", aviso);
            var predeterminado = await EscalarAsync<Guid>("""SELECT "Id" FROM "Almacenes" WHERE "EsPredeterminado" AND NOT "IsDeleted" """);
            Assert.Equal(predeterminado, Ok(await ObtenerBorradorAsync(copia.BorradorId)).AlmacenId);
            var linea = Assert.Single(await LineasBorradorAsync(copia.BorradorId));
            Assert.Equal((producto, predeterminado), (linea.ProductoId!.Value, linea.AlmacenId!.Value));
        }
        finally
        {
            await EjecutarSqlAsync("""UPDATE "Almacenes" SET "Bloqueado" = false WHERE "Id" = @Id""", new { Id = almacen });
        }
    }

    [Fact]
    public async Task Copia_ClienteBloqueado_400_SinCrearNadaNiConsumirNumero()
    {
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 10m, 1m, D1));
        var borrador = await BorradorAsync(socio, almacen: almacen);
        await LineaProductoAsync(borrador.Id, producto, 1m, 10m);
        var factura = await PostearOkAsync(borrador.Id);
        await EjecutarSqlAsync("""UPDATE "SociosNegocio" SET "Bloqueado" = @B WHERE "Id" = @Id""", new { B = (short)BloqueoSocioNegocio.Facturacion, Id = socio });
        var antes = await FotoCopiaAsync();

        var resultado = await CopiarAsync(factura.Numero);

        Assert.Equal(("factura.copia_cliente_invalido", "SocioNegocioId"), Unico(resultado));
        Assert.Equal(antes, await FotoCopiaAsync());
    }

    [Fact]
    public async Task Copia_ClienteBorrado_400_SinCrearNada()
    {
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 10m, 1m, D1));
        var borrador = await BorradorAsync(socio, almacen: almacen);
        await LineaProductoAsync(borrador.Id, producto, 1m, 10m);
        var factura = await PostearOkAsync(borrador.Id);
        await EjecutarSqlAsync("""UPDATE "SociosNegocio" SET "IsDeleted" = true WHERE "Id" = @Id""", new { Id = socio });
        var antes = await FotoCopiaAsync();

        var resultado = await CopiarAsync(factura.Numero);

        Assert.Equal(("factura.copia_cliente_invalido", "SocioNegocioId"), Unico(resultado));
        Assert.Equal(antes, await FotoCopiaAsync());
    }

    [Fact]
    public async Task Copia_FacturarABloqueado_400_EnSuCampo_SinCrearNada()
    {
        var socio = await SocioAsync();
        var facturarA = await SocioAsync(nombre: "Facturar a");
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 10m, 1m, D1));
        var borrador = await BorradorAsync(socio, facturarA, almacen);
        await LineaProductoAsync(borrador.Id, producto, 1m, 10m);
        var factura = await PostearOkAsync(borrador.Id);
        await EjecutarSqlAsync("""UPDATE "SociosNegocio" SET "Bloqueado" = @B WHERE "Id" = @Id""", new { B = (short)BloqueoSocioNegocio.Facturacion, Id = facturarA });
        var antes = await FotoCopiaAsync();

        var resultado = await CopiarAsync(factura.Numero);

        Assert.Equal(("factura.copia_cliente_invalido", "SocioNegocioFacturarAId"), Unico(resultado));
        Assert.Equal(antes, await FotoCopiaAsync());
    }

    [Fact]
    public async Task Copia_AlmacenPropioDeLineaBloqueado_UsaElDeLaCabeceraConAviso()
    {
        var socio = await SocioAsync();
        var cabecera = await _prueba.SembrarAlmacenAsync();
        var propio = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, propio, 10m, 1m, D1));
        var borrador = await BorradorAsync(socio, almacen: cabecera);
        await LineaProductoAsync(borrador.Id, producto, 1m, 10m, almacen: propio);
        var factura = await PostearOkAsync(borrador.Id);
        await EjecutarSqlAsync("""UPDATE "Almacenes" SET "Bloqueado" = true WHERE "Id" = @Id""", new { Id = propio });
        try
        {
            var copia = Ok(await CopiarAsync(factura.Numero));

            var aviso = Assert.Single(copia.Avisos);
            Assert.Equal("Línea 10000: el almacén de la línea ya no está disponible: usa el almacén del borrador.", aviso);
            Assert.Equal(cabecera, Ok(await ObtenerBorradorAsync(copia.BorradorId)).AlmacenId);
            var linea = Assert.Single(await LineasBorradorAsync(copia.BorradorId));
            Assert.Equal((producto, cabecera), (linea.ProductoId!.Value, linea.AlmacenId!.Value));
        }
        finally
        {
            await EjecutarSqlAsync("""UPDATE "Almacenes" SET "Bloqueado" = false WHERE "Id" = @Id""", new { Id = propio });
        }
    }

    [Fact]
    public async Task Copia_SerieDeBorradoresConfiguradaInactiva_400_SinCrearNada()
    {
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 10m, 1m, D1));
        var borrador = await BorradorAsync(socio, almacen: almacen);
        await LineaProductoAsync(borrador.Id, producto, 1m, 10m);
        var factura = await PostearOkAsync(borrador.Id);
        var (inactiva, lineaInactiva, _) = await SeriesPrueba.CrearAsync(fixture.AppConnectionString, TipoDocumentoSerie.BorradorFacturaVenta, activa: false);
        try
        {
            await EjecutarSqlAsync("""UPDATE "ConfiguracionesNumeracion" SET "SerieId" = @S WHERE "TipoDocumento" = 1""", new { S = inactiva });
            var antes = await FotoCopiaAsync();

            var resultado = await CopiarAsync(factura.Numero);

            Assert.Equal("numeracion.serie_inactiva", Unico(resultado).Codigo);
            Assert.Equal(antes, await FotoCopiaAsync());
            Assert.Equal("", await SeriesPrueba.UltimoAsync(fixture.AppConnectionString, lineaInactiva));
        }
        finally
        {
            await EjecutarSqlAsync("""UPDATE "ConfiguracionesNumeracion" SET "SerieId" = @S WHERE "TipoDocumento" = 1""",
                new { S = SerieFacturaVentaIds.SerieBorradorId });
        }
    }

    [Fact]
    public async Task Copia_FacturaInexistente_NoEncontrada()
    {
        Assert.Equal(("factura.no_encontrado", "Numero"), Unico(await CopiarAsync("NO-EXISTE")));
    }

    private async Task<Result<CopiaFacturaResponse>> CopiarAsync(string numero)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<CopiarFacturaABorradorCommandHandler>(scope.ServiceProvider);
        return await handler.Handle(new CopiarFacturaABorradorCommand(numero), default);
    }

    private async Task<IReadOnlyList<LineaFacturaVentaBorradorResponse>> LineasBorradorAsync(Guid borradorId)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var lectura = scope.ServiceProvider.GetRequiredService<ILineaFacturaVentaBorradorReadRepository>();
        return (await lectura.ListByBorradorAsync(borradorId, default)).OrderBy(l => l.NumeroLinea).ToList();
    }

    /// <summary>Borradores y líneas (vivos y totales) y el contador de la serie de borradores configurada.</summary>
    private async Task<Dictionary<string, string?>> FotoCopiaAsync()
    {
        await using var conexion = _prueba.NuevaConexion();
        return new Dictionary<string, string?>
        {
            ["borradores"] = (await conexion.ExecuteScalarAsync<long>("""SELECT COUNT(*) FROM "FacturasVentaBorrador" """)).ToString(),
            ["lineas"] = (await conexion.ExecuteScalarAsync<long>("""SELECT COUNT(*) FROM "LineasFacturaVentaBorrador" """)).ToString(),
            ["serie FV-BORR"] = await conexion.ExecuteScalarAsync<string>(
                """SELECT "UltimoNumeroUsado" FROM "LineasSerie" WHERE "Id" = @Id""", new { Id = SerieFacturaVentaIds.LineaSerieBorradorId }),
        };
    }

    // ----- Helpers: siembra -----

    private async Task<Guid> SocioAsync(
        Guid? grupoCliente = null, string nombre = "Cliente posteo", Guid? termino = null)
    {
        await using var contexto = _prueba.NuevoContexto();
        var socio = new SocioNegocio
        {
            Codigo = $"PF{Guid.NewGuid():N}"[..10].ToUpperInvariant(),
            NombreComercial = nombre,
            RazonSocial = $"{nombre} SRL",
            TipoDocumentoFiscal = TipoDocumentoFiscal.Rnc,
            NumeroDocumentoFiscal = $"1{Random.Shared.Next(10_000_000, 99_999_999)}",
            TerminoPagoId = termino,
            GrupoNegocioId = GrupoContableIds.NegocioNacional,
            GrupoIvaNegocioId = GrupoContableIds.IvaNegocioItbis18,
            GrupoClienteContableId = grupoCliente ?? GrupoContableIds.ClienteContableGeneral,
            CreatedBy = "test",
        };
        contexto.Set<SocioNegocio>().Add(socio);
        await contexto.SaveChangesAsync();
        return socio.Id;
    }

    /// <summary>Producto con base UND (y CJA × 12) y los grupos semilla BIENES / ITBIS18 / GENERAL salvo que se indique otro.</summary>
    private async Task<Guid> ProductoAsync(Guid? grupoProducto = null, Guid? grupoIva = null, Guid? grupoInventario = null)
    {
        var id = await _prueba.SembrarProductoAsync(costoUnitario: 1m);
        await EjecutarSqlAsync(
            """
            UPDATE "Productos" SET "GrupoProductoId" = @Gp, "GrupoIvaProductoId" = @Gi, "GrupoInventarioId" = @Ginv, "PrecioVenta" = 10
            WHERE "Id" = @Id
            """,
            new
            {
                Id = id,
                Gp = grupoProducto ?? GrupoContableIds.ProductoBienes,
                Gi = grupoIva ?? GrupoContableIds.IvaProductoItbis18,
                Ginv = grupoInventario ?? GrupoContableIds.InventarioGeneral,
            });
        return id;
    }

    /// <summary>Producto con base MT (2 decimales) y la caja (CJA) asociada con factor 3.</summary>
    private async Task<Guid> ProductoMetroConCajaDeTresAsync()
    {
        var id = await ProductoAsync();
        await EjecutarSqlAsync(
            """
            UPDATE "Productos" SET "UnidadMedidaBaseId" = (SELECT "Id" FROM "UnidadesMedida" WHERE "Codigo" = 'MT') WHERE "Id" = @Id;
            UPDATE "UnidadesMedidaProducto" SET "CantidadPorUnidadMedida" = 3 WHERE "ProductoId" = @Id;
            """,
            new { Id = id });
        return id;
    }

    /// <summary>Unidad de medida propia del test (para poder borrarla sin tocar la semilla) asociada al producto.</summary>
    private async Task<Guid> UnidadPropiaAsync(Guid productoId, decimal factor)
    {
        await using var contexto = _prueba.NuevoContexto();
        var unidad = new UnidadMedida { Codigo = $"U{Guid.NewGuid():N}"[..8].ToUpperInvariant(), Nombre = "Unidad de prueba", CreatedBy = "test" };
        contexto.Set<UnidadMedida>().Add(unidad);
        contexto.Set<UnidadMedidaProducto>().Add(new UnidadMedidaProducto
        {
            ProductoId = productoId, UnidadMedidaId = unidad.Id, CantidadPorUnidadMedida = factor, CreatedBy = "test",
        });
        await contexto.SaveChangesAsync();
        return unidad.Id;
    }

    private async Task<Guid> GrupoAsync<T>() where T : GrupoContable, new()
    {
        await using var contexto = _prueba.NuevoContexto();
        var grupo = new T { Codigo = $"G{Guid.NewGuid():N}"[..12].ToUpperInvariant(), Descripcion = "Grupo sin setup", CreatedBy = "test" };
        contexto.Set<T>().Add(grupo);
        await contexto.SaveChangesAsync();
        return grupo.Id;
    }

    /// <summary>Grupo de IVA de producto con su setup ITBIS18 (para poder guardar la línea); devuelve el grupo y avisa del setup.</summary>
    private async Task<Guid> GrupoIvaConSetupAsync(Action<Guid> setupCreado, Guid? cuentaIva = null)
    {
        var grupo = await GrupoAsync<GrupoIvaProducto>();
        await using var contexto = _prueba.NuevoContexto();
        var setup = new SetupIva
        {
            GrupoIvaNegocioId = GrupoContableIds.IvaNegocioItbis18,
            GrupoIvaProductoId = grupo,
            PorcentajeIva = 18m,
            CuentaIvaVentasId = cuentaIva ?? CuentaContableIds.IvaPorPagar,
            IdentificadorIva = "ITBIS18",
            CreatedBy = "test",
        };
        contexto.Set<SetupIva>().Add(setup);
        await contexto.SaveChangesAsync();
        setupCreado(setup.Id);
        return grupo;
    }

    private async Task<Guid> GrupoClienteAsync(Guid cuentaCxC)
    {
        await using var contexto = _prueba.NuevoContexto();
        var grupo = new GrupoClienteContable
        {
            Codigo = $"GC{Guid.NewGuid():N}"[..12].ToUpperInvariant(), Descripcion = "Grupo cliente", CuentaCxCId = cuentaCxC, CreatedBy = "test",
        };
        contexto.Set<GrupoClienteContable>().Add(grupo);
        await contexto.SaveChangesAsync();
        return grupo.Id;
    }

    private async Task<Guid> CuentaAsync(bool posteoDirecto)
    {
        await using var contexto = _prueba.NuevoContexto();
        var cuenta = new CuentaContable
        {
            Numero = $"9{Guid.NewGuid().GetHashCode() & 0x7FFFFFFF}",
            Nombre = posteoDirecto ? "Otros ingresos" : "CxC propia",
            TipoCuenta = TipoCuentaContable.Posteo,
            TipoResultado = posteoDirecto ? TipoResultadoCuenta.Resultado : TipoResultadoCuenta.Balance,
            PosteoDirecto = posteoDirecto,
            Sangria = 1,
            CreatedBy = "test",
        };
        contexto.Set<CuentaContable>().Add(cuenta);
        await contexto.SaveChangesAsync();
        return cuenta.Id;
    }

    private async Task<Guid> TerminoAsync()
    {
        await using var contexto = _prueba.NuevoContexto();
        var termino = new TerminoPago
        {
            Codigo = $"T{Guid.NewGuid():N}"[..10].ToUpperInvariant(), Descripcion = "30 días", DiasVencimiento = 30, CreatedBy = "test",
        };
        contexto.Set<TerminoPago>().Add(termino);
        await contexto.SaveChangesAsync();
        return termino.Id;
    }

    // ----- Helpers: borradores y posteo (handlers reales, un scope por llamada) -----

    private async Task<FacturaVentaBorradorResponse> BorradorAsync(Guid venderA, Guid? facturarA = null, Guid? almacen = null)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<CreateFacturaVentaBorradorCommandHandler>(scope.ServiceProvider);
        var resultado = await handler.Handle(
            new CreateFacturaVentaBorradorCommand(venderA, facturarA, D10, D10, null, almacen, null), default);
        return Ok(resultado);
    }

    private async Task<Result<FacturaVentaBorradorResponse>> CrearBorradorAsync(Guid venderA, Guid? facturarA)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<CreateFacturaVentaBorradorCommandHandler>(scope.ServiceProvider);
        return await handler.Handle(new CreateFacturaVentaBorradorCommand(venderA, facturarA, D10, D10, null, null, null), default);
    }

    private async Task<Result<FacturaVentaBorradorResponse>> ActualizarBorradorAsync(Guid borradorId, Guid facturarA, long xmin)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<UpdateFacturaVentaBorradorCommandHandler>(scope.ServiceProvider);
        return await handler.Handle(
            new UpdateFacturaVentaBorradorCommand(borradorId, null, facturarA, null, null, null, null, null, xmin), default);
    }

    private async Task<Result<FacturaVentaBorradorResponse>> ObtenerBorradorAsync(Guid borradorId)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var lectura = scope.ServiceProvider.GetRequiredService<IFacturaVentaBorradorReadRepository>();
        var borrador = await lectura.GetByIdAsync(borradorId, default);
        return borrador is null
            ? Result<FacturaVentaBorradorResponse>.Fallo(new Error("test.no_encontrado", "Borrador no encontrado"))
            : Result<FacturaVentaBorradorResponse>.Exito(borrador);
    }

    private Task<LineaFacturaVentaBorradorResponse> LineaProductoAsync(
        Guid borradorId, Guid producto, decimal cantidad, decimal precio, decimal? descuento = null, Guid? almacen = null, Guid? unidad = null) =>
        LineaAsync(new CreateLineaFacturaVentaBorradorCommand(
            borradorId, TipoLineaFactura.Producto, producto, null, null, almacen, unidad, cantidad, precio, descuento, null));

    private Task<LineaFacturaVentaBorradorResponse> LineaCuentaAsync(
        Guid borradorId, Guid cuenta, Guid grupoIva, decimal cantidad, decimal precio, decimal? descuento = null) =>
        LineaAsync(new CreateLineaFacturaVentaBorradorCommand(
            borradorId, TipoLineaFactura.CuentaContable, null, cuenta, null, null, null, cantidad, precio, descuento, grupoIva));

    private Task<LineaFacturaVentaBorradorResponse> ComentarioAsync(Guid borradorId, string texto) =>
        LineaAsync(new CreateLineaFacturaVentaBorradorCommand(
            borradorId, TipoLineaFactura.Comentario, null, null, texto, null, null, null, null, null, null));

    private async Task<LineaFacturaVentaBorradorResponse> LineaAsync(CreateLineaFacturaVentaBorradorCommand comando)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<CreateLineaFacturaVentaBorradorCommandHandler>(scope.ServiceProvider);
        return Ok(await handler.Handle(comando, default));
    }

    private async Task LiberarAsync(Guid borradorId)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<LiberarFacturaVentaBorradorCommandHandler>(scope.ServiceProvider);
        Ok(await handler.Handle(new LiberarFacturaVentaBorradorCommand(borradorId), default));
    }

    private async Task<Result<ResultadoPosteoFactura>> PostearAsync(Guid borradorId)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<PostearFacturaVentaCommandHandler>(scope.ServiceProvider);
        return await handler.Handle(new PostearFacturaVentaCommand(borradorId), default);
    }

    private async Task<ResultadoPosteoFactura> PostearOkAsync(Guid borradorId) => Ok(await PostearAsync(borradorId));

    /// <summary>Posteo con el validador de fechas de registro del usuario indicado (null = proceso del sistema).</summary>
    private async Task<Result<ResultadoPosteoFactura>> PostearComoAsync(Guid borradorId, Guid? usuario)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<PostearFacturaVentaCommandHandler>(
            scope.ServiceProvider, FechasRegistroPrueba.Validador(scope.ServiceProvider, usuario));
        return await handler.Handle(new PostearFacturaVentaCommand(borradorId), default);
    }

    private static ResultadoPosteoFactura PostearOk(Result<ResultadoPosteoFactura> resultado) => Ok(resultado);

    private async Task<ResultadoPosteoCostoInventario> PostearCostoAsync(Guid productoId)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPosteoCostoInventario>().PostearAsync(productoId);
    }

    /// <summary>Neto por cuenta (orden de número de cuenta) de lo que el batch de costo contabilizó para el producto en la fecha.</summary>
    private async Task<List<(Guid, decimal)>> CostoContabilizadoAsync(Guid productoId, DateOnly fecha)
    {
        await using var conexion = _prueba.NuevaConexion();
        return [.. await conexion.QueryAsync<(Guid, decimal)>(
            """
            SELECT m."CuentaContableId", SUM(m."Importe") FROM "MovimientosContables" m
            JOIN "CuentasContables" c ON c."Id" = m."CuentaContableId"
            WHERE m."ProductoId" = @P AND m."FechaRegistro" = @F AND m."TipoOrigen" = @T
            GROUP BY m."CuentaContableId", c."Numero" ORDER BY c."Numero"
            """,
            new { P = productoId, F = fecha, T = (short)TipoOrigenMovimiento.CostoInventario })];
    }

    private async Task<Result> BorrarSocioAsync(Guid socioId)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<DeleteSocioNegocioCommandHandler>(scope.ServiceProvider);
        return await handler.Handle(new DeleteSocioNegocioCommand(socioId), default);
    }

    private async Task EnTransaccionAsync<T>(Func<IServiceProvider, Task<Result<T>>> accion)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var sesion = scope.ServiceProvider.GetRequiredService<IDbSession>();
        await using var tx = await sesion.BeginTransactionAsync();
        Ok(await accion(scope.ServiceProvider));
        await sesion.CommitAsync();
    }

    private static T Ok<T>(Result<T> resultado)
    {
        Assert.True(resultado.EsExito, resultado.EsFallo
            ? string.Join("; ", resultado.Errores.Select(e => $"{e.Codigo} [{e.Campo}]: {e.Mensaje}"))
            : string.Empty);
        return resultado.Valor;
    }

    private static (string Codigo, string? Campo) Unico(Result resultado)
    {
        Assert.True(resultado.EsFallo);
        var error = Assert.Single(resultado.Errores);
        return (error.Codigo, error.Campo);
    }

    private static async Task<List<(Result<ResultadoPosteoFactura>? Resultado, Exception? Excepcion)>> EnParaleloAsync(
        params Func<Task<Result<ResultadoPosteoFactura>>>[] acciones)
    {
        var barrera = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listas = 0;
        var resultados = new ConcurrentBag<(Result<ResultadoPosteoFactura>?, Exception?)>();

        async Task EjecutarAsync(Func<Task<Result<ResultadoPosteoFactura>>> accion)
        {
            if (Interlocked.Increment(ref listas) == acciones.Length)
            {
                barrera.SetResult();
            }

            await barrera.Task;
            try
            {
                resultados.Add((await accion(), null));
            }
            catch (Exception ex)
            {
                resultados.Add((null, ex));
            }
        }

        await Task.WhenAll(acciones.Select(a => Task.Run(() => EjecutarAsync(a)))).WaitAsync(TimeSpan.FromSeconds(60));
        return [.. resultados];
    }

    [Fact]
    public async Task NumeroDeAviso_AlcanzadoEnLaSerieFv_ElResultadoTraeLaAdvertencia()
    {
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 5m, 1m, D1));
        var borrador = await BorradorAsync(await SocioAsync(), almacen: almacen);
        await LineaProductoAsync(borrador.Id, producto, 1m, 10m);
        try
        {
            // El aviso en el número que se va a emitir: siguiente = último + 1.
            var siguiente = Siguiente(await UltimoNumeroFvAsync());
            await EjecutarSqlAsync("""UPDATE "LineasSerie" SET "NumeroAviso" = @A WHERE "Id" = @Id""",
                new { A = siguiente, Id = SerieFacturaVentaIds.LineaSeriePosteadaId });

            var resultado = await PostearOkAsync(borrador.Id);

            Assert.Equal(siguiente, resultado.Numero);
            Assert.Contains("número de aviso", resultado.AvisoNumeracion);
        }
        finally
        {
            await EjecutarSqlAsync("""UPDATE "LineasSerie" SET "NumeroAviso" = NULL WHERE "Id" = @Id""",
                new { Id = SerieFacturaVentaIds.LineaSeriePosteadaId });
        }
    }

    // ----- Helpers: lectura -----

    private static string Siguiente(string ultimo) => (long.Parse(ultimo) + 1).ToString().PadLeft(8, '0');

    private Task<string> UltimoNumeroFvAsync() => EscalarAsync<string>(
        """SELECT "UltimoNumeroUsado" FROM "LineasSerie" WHERE "Id" = @Id""", new { Id = SerieFacturaVentaIds.LineaSeriePosteadaId });

    private static readonly string[] TablasEscritas =
    [
        "FacturasVenta", "LineasFacturaVenta", "LineasIvaFacturaVenta", "MovimientosCliente", "MovimientosClienteDetalle",
        "RegistrosContables", "MovimientosContables", "MovimientosProducto", "MovimientosValor", "AplicacionesMovimientoProducto",
    ];

    private static readonly string[] Secuencias =
        ["LineasFacturaVenta", "MovimientosCliente", "MovimientosClienteDetalle", "MovimientosProducto", "MovimientosValor", "MovimientosContables"];

    /// <summary>Filas de todas las tablas que escribe el posteo y los números de las series FV y CONTAB.</summary>
    private async Task<Dictionary<string, string?>> FotoFilasAsync()
    {
        await using var conexion = _prueba.NuevaConexion();
        var foto = new Dictionary<string, string?>();
        foreach (var tabla in TablasEscritas)
        {
            foto[tabla] = (await conexion.ExecuteScalarAsync<long>($"""SELECT COUNT(*) FROM "{tabla}" """)).ToString();
        }

        foto["serie FV"] = await conexion.ExecuteScalarAsync<string>(
            """SELECT "UltimoNumeroUsado" FROM "LineasSerie" WHERE "Id" = @Id""", new { Id = SerieFacturaVentaIds.LineaSeriePosteadaId });
        foto["serie CONTAB"] = await conexion.ExecuteScalarAsync<string>(
            """SELECT l."UltimoNumeroUsado" FROM "LineasSerie" l JOIN "Series" s ON s."Id" = l."SerieId" WHERE s."Codigo" = 'CONTAB'""");
        return foto;
    }

    /// <summary>
    /// <see cref="FotoFilasAsync"/> más el último valor de las secuencias de identidad de los libros. Las secuencias NO son
    /// transaccionales: si el posteo hubiera llegado a intentar un INSERT (aunque luego se deshiciera), avanzarían.
    /// </summary>
    private async Task<Dictionary<string, string?>> FotoAsync()
    {
        var foto = await FotoFilasAsync();
        await using var conexion = _prueba.NuevaConexion();
        foreach (var tabla in Secuencias)
        {
            foto[$"secuencia {tabla}"] = (await conexion.ExecuteScalarAsync<long?>(
                $"""SELECT pg_sequence_last_value(pg_get_serial_sequence('"{tabla}"', 'Id')::regclass)"""))?.ToString();
        }

        return foto;
    }

    /// <summary>Estado, borrado lógico y líneas vivas/borradas del borrador: cubre en un solo punto que ningún fallo lo marque Posteada.</summary>
    private async Task<(EstadoFacturaBorrador Estado, bool Borrado, long LineasVivas, long LineasBorradas)> EstadoBorradorAsync(Guid borradorId)
    {
        await using var conexion = _prueba.NuevaConexion();
        var (estado, borrado, vivas, borradas) = await conexion.QuerySingleAsync<(short, bool, long, long)>(
            """
            SELECT b."Estado", b."IsDeleted",
                   (SELECT COUNT(*) FROM "LineasFacturaVentaBorrador" l WHERE l."FacturaVentaBorradorId" = b."Id" AND NOT l."IsDeleted"),
                   (SELECT COUNT(*) FROM "LineasFacturaVentaBorrador" l WHERE l."FacturaVentaBorradorId" = b."Id" AND l."IsDeleted")
            FROM "FacturasVentaBorrador" b WHERE b."Id" = @Id
            """,
            new { Id = borradorId });
        return ((EstadoFacturaBorrador)estado, borrado, vivas, borradas);
    }

    private async Task<FacturaVenta> FacturaAsync(string numero)
    {
        await using var conexion = _prueba.NuevaConexion();
        return await conexion.QuerySingleAsync<FacturaVenta>("""SELECT * FROM "FacturasVenta" WHERE "Numero" = @N""", new { N = numero });
    }

    private async Task<List<LineaFacturaVenta>> LineasAsync(string numero)
    {
        await using var conexion = _prueba.NuevaConexion();
        return [.. await conexion.QueryAsync<LineaFacturaVenta>(
            """SELECT * FROM "LineasFacturaVenta" WHERE "FacturaVentaNumero" = @N ORDER BY "NumeroLinea" """, new { N = numero })];
    }

    private async Task<List<LineaIvaFacturaVenta>> LineasIvaAsync(string numero)
    {
        await using var conexion = _prueba.NuevaConexion();
        return [.. await conexion.QueryAsync<LineaIvaFacturaVenta>(
            """SELECT * FROM "LineasIvaFacturaVenta" WHERE "FacturaVentaNumero" = @N ORDER BY "IdentificadorIva" """, new { N = numero })];
    }

    private async Task<List<MovimientoContable>> AsientoAsync(long registroId)
    {
        await using var conexion = _prueba.NuevaConexion();
        return [.. await conexion.QueryAsync<MovimientoContable>(
            """SELECT * FROM "MovimientosContables" WHERE "RegistroContableId" = @Id ORDER BY "Id" """, new { Id = registroId })];
    }

    private async Task<List<OpenSource1.Core.Entities.Clientes.MovimientoCliente>> MovimientosClienteAsync(string numero)
    {
        await using var conexion = _prueba.NuevaConexion();
        return [.. await conexion.QueryAsync<OpenSource1.Core.Entities.Clientes.MovimientoCliente>(
            """SELECT * FROM "MovimientosCliente" WHERE "TipoDocumento" = 1 AND "NumeroDocumento" = @N""", new { N = numero })];
    }

    private async Task<List<(TipoDetalleCliente, decimal)>> DetalleClienteAsync(long movimientoId)
    {
        await using var conexion = _prueba.NuevaConexion();
        return [.. await conexion.QueryAsync<(TipoDetalleCliente, decimal)>(
            """SELECT "TipoMovimiento", "Importe" FROM "MovimientosClienteDetalle" WHERE "MovimientoClienteId" = @Id ORDER BY "Id" """,
            new { Id = movimientoId })];
    }

    private async Task<List<SalidaFila>> SalidasAsync(string numero)
    {
        await using var conexion = _prueba.NuevaConexion();
        return [.. await conexion.QueryAsync<SalidaFila>(
            """
            SELECT m."Id", m."TipoMovimiento", m."TipoDocumento", m."TipoOrigen", m."Cantidad", m."SocioNegocioId", m."ClaveOrigen",
                   v."ImporteVenta", v."ImporteCosto"
            FROM "MovimientosProducto" m JOIN "MovimientosValor" v ON v."MovimientoProductoId" = m."Id"
            WHERE m."TipoOrigen" = 2 AND m."NumeroDocumento" = @N
            ORDER BY m."Id"
            """,
            new { N = numero })];
    }

    private Task<bool> CostoAjustadoAsync(Guid productoId) =>
        EscalarAsync<bool>("""SELECT "CostoAjustado" FROM "Productos" WHERE "Id" = @Id""", new { Id = productoId });

    private async Task<T> EscalarAsync<T>(string sql, object? parametros = null)
    {
        await using var conexion = _prueba.NuevaConexion();
        return (await conexion.ExecuteScalarAsync<T>(sql, parametros))!;
    }

    private async Task EjecutarSqlAsync(string sql, object parametros)
    {
        await using var conexion = _prueba.NuevaConexion();
        await conexion.ExecuteAsync(sql, parametros);
    }

    private async Task<(EstadoFacturaBorrador Estado, string? Factura)> EstadoYFacturaAsync(Guid borradorId)
    {
        await using var conexion = _prueba.NuevaConexion();
        var fila = await conexion.QuerySingleAsync<(short Estado, string? Factura)>(
            """SELECT "Estado", "FacturaVentaNumero" FROM "FacturasVentaBorrador" WHERE "Id" = @Id""", new { Id = borradorId });
        return ((EstadoFacturaBorrador)fila.Estado, fila.Factura);
    }

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

    private async Task<PagedResult<FacturaVentaBorradorResponse>> ListarBorradoresAsync(Guid socio, EstadoFacturaBorrador? estado)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var lectura = scope.ServiceProvider.GetRequiredService<IFacturaVentaBorradorReadRepository>();
        return (await lectura.ListAsync(new FacturaVentaBorradorSearchCriteria(null, null, socio, estado),
            new PageRequest(1, 50, "Numero", Descendente: false), default)).Valor;
    }

    private Task<string> UltimoNumeroAsync(Guid lineaId) =>
        EscalarAsync<string>("""SELECT "UltimoNumeroUsado" FROM "LineasSerie" WHERE "Id" = @Id""", new { Id = lineaId });

    private sealed class SalidaFila
    {
        public long Id { get; init; }
        public TipoMovimientoInventario TipoMovimiento { get; init; }
        public TipoDocumentoInventario TipoDocumento { get; init; }
        public TipoOrigenMovimiento TipoOrigen { get; init; }
        public decimal Cantidad { get; init; }
        public Guid? SocioNegocioId { get; init; }
        public string ClaveOrigen { get; init; } = string.Empty;
        public decimal ImporteVenta { get; init; }
        public decimal ImporteCosto { get; init; }
    }
}
