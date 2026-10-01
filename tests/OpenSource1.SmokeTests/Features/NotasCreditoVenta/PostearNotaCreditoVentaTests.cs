using System.Collections.Concurrent;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using OpenSource1.Application.Features.Cobros;
using OpenSource1.Application.Features.FacturasVenta.Borradores.Commands;
using OpenSource1.Application.Features.FacturasVenta.Borradores.Handlers;
using OpenSource1.Application.Features.FacturasVenta.Posteo;
using OpenSource1.Application.Features.NotasCreditoVenta.Borradores;
using OpenSource1.Application.Features.NotasCreditoVenta.Borradores.Commands;
using OpenSource1.Application.Features.NotasCreditoVenta.Borradores.Dtos;
using OpenSource1.Application.Features.NotasCreditoVenta.Borradores.Handlers;
using OpenSource1.Application.Features.NotasCreditoVenta.Posteo;
using OpenSource1.Application.Features.SociosNegocio;
using OpenSource1.Application.Services.Contabilidad;
using OpenSource1.Application.Services.Inventario;
using OpenSource1.Core.Common;
using OpenSource1.Core.Entities;
using OpenSource1.Core.Entities.Contabilidad;
using OpenSource1.Core.Entities.Ventas;
using OpenSource1.Core.Enums;
using OpenSource1.SmokeTests.TestInfrastructure;

namespace OpenSource1.SmokeTests.Features.NotasCreditoVenta;

/// <summary>
/// Notas de crédito de venta (Task 8.6) contra Postgres real, sobre facturas posteadas por el motor real: borradores y sus reglas de
/// captura; posteo con documento e IVA agrupado, devolución de inventario al costo de la venta (Review Focus 2), movimiento de
/// cliente con aplicación automática, asiento inverso; revalidación bajo el bloqueo de la factura (Review Focus 1); nota de total 0
/// (Ruling FE); "foto" de nada escrito en cada fallo (filas, números de serie y secuencias). REQUIERE DOCKER. Los tests de la clase
/// comparten base de datos: los números de serie se comprueban relativos.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostearNotaCreditoVentaTests(PostgresTestFixture fixture) : IClassFixture<PostgresTestFixture>, IAsyncLifetime
{
    private static readonly DateOnly D1 = new(2026, 9, 1);
    private static readonly DateOnly D10 = new(2026, 9, 10);
    private static readonly DateOnly D11 = new(2026, 9, 11);
    private static readonly DateOnly D12 = new(2026, 9, 12);

    private readonly LibroInventarioPrueba _prueba = new(fixture);

    public Task InitializeAsync() => _prueba.InicializarAsync();

    public async Task DisposeAsync() => await _prueba.DisposeAsync();

    // ----- Nota total y parcial -----

    [Fact]
    public async Task NotaTotal_ConDevolucion_Documento_Entrada_ClienteAplicado_AsientoInverso_YFacturaSinPendiente()
    {
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        var otrosIngresos = await CuentaAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 10m, 4m, D1));
        // 3 × 50 (ITBIS 18 %) + 25 exento: 150 + 27 + 25 = 202.
        var factura = await FacturaAsync(socio, almacen, P(producto, 3m, 50m), C(otrosIngresos, 1m, 25m, GrupoContableIds.IvaProductoExento));
        var movimientoFactura = Assert.Single(await MovimientosClienteAsync(TipoDocumentoCliente.Factura, factura));
        var ultimo = await UltimoNumeroAsync(SerieNotaCreditoVentaIds.LineaSeriePosteadaId);

        var borrador = Ok(await CrearBorradorAsync(factura, copiar: true, devolver: true));
        Assert.Equal((factura, socio, 2, CuentaContableIds.CxC), (borrador.FacturaVentaNumero, borrador.SocioNegocioId, borrador.NumeroLineas, borrador.CuentaCxCId));
        var lineasBorrador = await LineasBorradorAsync(borrador.Id);
        Assert.Equal([(10000, 3m, true, 150m), (20000, 1m, false, 25m)], lineasBorrador.Select(l => (l.NumeroLinea, l.Cantidad, l.DevolverInventario, l.ImporteLinea)));

        var resultado = Ok(await PostearAsync(borrador.Id));

        Assert.Equal((Siguiente(ultimo), 202m, 202m), (resultado.Numero, resultado.ImporteTotal, resultado.ImporteAplicado));
        var nota = await NotaAsync(resultado.Numero);
        Assert.Equal((factura, borrador.Numero, 175m, 27m, 202m, CuentaContableIds.CxC),
            (nota.FacturaVentaNumero, nota.NumeroBorrador, nota.ImporteSinIva, nota.ImporteIva, nota.ImporteTotal, nota.CuentaCxCId));
        Assert.Equal(
            [("EXENTO", 0m, 25m, 0m, CuentaContableIds.IvaPorPagar), ("ITBIS18", 18m, 150m, 27m, CuentaContableIds.IvaPorPagar)],
            (await LineasIvaNotaAsync(resultado.Numero)).Select(l => (l.IdentificadorIva, l.PorcentajeIva, l.BaseImponible, l.ImporteIva, l.CuentaIvaId)));
        var lineasNota = await LineasNotaAsync(resultado.Numero);
        Assert.Equal([(10000, true), (20000, false)], lineasNota.Select(l => (l.NumeroLinea, l.MovimientoProductoId is not null)));

        // Devolución: entrada Venta +3 en el almacén de la línea al costo de la venta (3 × 4 = 12).
        var entrada = Assert.Single(await MovimientosInventarioNotaAsync(resultado.Numero));
        Assert.Equal((TipoMovimientoInventario.Venta, TipoDocumentoInventario.NotaCreditoVenta, TipoOrigenMovimiento.NotaCreditoVenta, 3m, 3m, 12m, almacen, socio),
            (entrada.TipoMovimiento, entrada.TipoDocumento, entrada.TipoOrigen, entrada.Cantidad, entrada.CantidadRestante!.Value, entrada.ImporteCosto,
             entrada.AlmacenId, entrada.SocioNegocioId!.Value));
        Assert.Equal(lineasNota[0].MovimientoProductoId, entrada.Id);
        Assert.Equal(10m, await _prueba.ConsultarAsync(c => c.ExistenciaAsync(producto, almacen, null)));

        // Cliente: nota −202 del facturar-a con la CxC congelada, aplicada entera a la factura (ambos restantes 0).
        var movimientoNota = Assert.Single(await MovimientosClienteAsync(TipoDocumentoCliente.NotaCredito, resultado.Numero));
        Assert.Equal((socio, -202m, CuentaContableIds.CxC, TipoOrigenMovimiento.NotaCreditoVenta),
            (movimientoNota.SocioNegocioId, movimientoNota.ImporteOriginal, movimientoNota.CuentaCxCId, movimientoNota.TipoOrigen));
        Assert.Equal([(TipoDetalleCliente.ImporteInicial, -202m), (TipoDetalleCliente.Aplicacion, 202m)], await DetalleClienteAsync(movimientoNota.Id));
        Assert.Equal([(TipoDetalleCliente.ImporteInicial, 202m), (TipoDetalleCliente.Aplicacion, -202m)], await DetalleClienteAsync(movimientoFactura.Id));

        // Asiento inverso: crédito CxC 202; débito Ventas 150, cuenta 25 e IVA 27.
        var asiento = await AsientoAsync(nota.RegistroContableId!.Value);
        Assert.Equal(
            [(CuentaContableIds.CxC, -202m), (CuentaContableIds.Ventas, 150m), (otrosIngresos, 25m), (CuentaContableIds.IvaPorPagar, 27m)],
            asiento.Select(m => (m.CuentaContableId, m.Importe)));
        Assert.All(asiento, m => Assert.Equal((TipoDocumentoContable.NotaCreditoVenta, resultado.Numero, D12, TipoOrigenMovimiento.NotaCreditoVenta),
            (m.TipoDocumento, m.NumeroDocumento, m.FechaRegistro, m.TipoOrigen)));

        Assert.Equal((false, 2L, 0L), await EstadoBorradorAsync(borrador.Id));
        Assert.Equal((EstadoNotaCreditoBorrador.Posteada, resultado.Numero), await EstadoYNotaAsync(borrador.Id));

        // Todo acreditado: no admite otra nota.
        Assert.Equal(("nota_credito.factura_sin_pendiente", "FacturaVentaNumero"), Unico(await CrearBorradorAsync(factura)));
    }

    [Fact]
    public async Task NotaParcial_SinDevolucion_IvaAgrupadoDeLaNota_AplicaSoloLaNota_YLoAcreditadoLimitaLasSiguientes()
    {
        // Tres líneas de 1 × 10.03 (factura 35.51). La nota acredita dos: base 20.06, IVA del GRUPO 3.61 (no 2 × 1.81), total 23.67.
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 10m, 4m, D1));
        var factura = await FacturaAsync(socio, almacen, P(producto, 1m, 10.03m), P(producto, 1m, 10.03m), P(producto, 1m, 10.03m));
        var lineasFactura = await LineasFacturaAsync(factura);
        var movimientoFactura = Assert.Single(await MovimientosClienteAsync(TipoDocumentoCliente.Factura, factura));

        var borrador = Ok(await CrearBorradorAsync(factura));
        Ok(await LineaAsync(borrador.Id, lineasFactura[0].Id, 1m));
        Ok(await LineaAsync(borrador.Id, lineasFactura[1].Id, 1m));
        var totales = Assert.IsType<OpenSource1.Application.Features.FacturasVenta.Calculo.TotalesFactura>(await TotalesAsync(borrador.Id));
        Assert.Equal((20.06m, 3.61m, 23.67m), (totales.ImporteSinIva, totales.ImporteIva, totales.ImporteTotal));

        var resultado = Ok(await PostearAsync(borrador.Id));

        Assert.Equal((23.67m, 23.67m), (resultado.ImporteTotal, resultado.ImporteAplicado));
        Assert.Empty(await MovimientosInventarioNotaAsync(resultado.Numero));
        Assert.Equal(7m, await _prueba.ConsultarAsync(c => c.ExistenciaAsync(producto, almacen, null)));
        Assert.Equal(11.84m, await RestanteAsync(movimientoFactura.Id));
        var asiento = await AsientoAsync((await NotaAsync(resultado.Numero)).RegistroContableId!.Value);
        Assert.Equal(
            [(CuentaContableIds.CxC, -23.67m), (CuentaContableIds.Ventas, 20.06m), (CuentaContableIds.IvaPorPagar, 3.61m)],
            asiento.Select(m => (m.CuentaContableId, m.Importe)));

        // Lo acreditado por notas POSTEADAS limita las siguientes: la línea 10000 ya no admite nada; la 30000 sí, entera.
        var segunda = Ok(await CrearBorradorAsync(factura));
        var excede = Unico(await LineaAsync(segunda.Id, lineasFactura[0].Id, 0.5m));
        Assert.Equal(("nota_credito.cantidad_excede", "Cantidad"), excede);
        var acreditables = await AcreditablesAsync(segunda.Id);
        Assert.Equal([(10000, 1m, 0m), (20000, 1m, 0m), (30000, 0m, 1m)],
            acreditables.Select(a => (a.NumeroLinea, a.CantidadAcreditada, a.CantidadPendiente)));
        Ok(await LineaAsync(segunda.Id, lineasFactura[2].Id, 1m));
        Assert.Equal((11.84m, 11.84m), Resumen(Ok(await PostearAsync(segunda.Id))));
        Assert.Equal(0m, await RestanteAsync(movimientoFactura.Id));
    }

    [Fact]
    public async Task FacturaYaPagada_LaNotaQuedaComoSaldoAFavor_YFacturaParcialmentePagada_AplicaSoloElRestante()
    {
        var socio = await SocioAsync();
        var ingresos = await CuentaAsync();

        // Pagada del todo: la nota no se aplica (restante −59 = saldo a favor).
        var pagada = await FacturaAsync(socio, null, C(ingresos, 1m, 100m));
        var movimientoPagada = Assert.Single(await MovimientosClienteAsync(TipoDocumentoCliente.Factura, pagada)).Id;
        await PagarYAplicarAsync(socio, movimientoPagada, 118m);
        var borrador = Ok(await CrearBorradorAsync(pagada));
        Ok(await LineaAsync(borrador.Id, (await LineasFacturaAsync(pagada))[0].Id, 0.5m));
        var resultado = Ok(await PostearAsync(borrador.Id));
        Assert.Equal((59m, 0m), Resumen(resultado));
        var nota = Assert.Single(await MovimientosClienteAsync(TipoDocumentoCliente.NotaCredito, resultado.Numero));
        Assert.Equal([(TipoDetalleCliente.ImporteInicial, -59m)], await DetalleClienteAsync(nota.Id));
        Assert.Equal(0m, await RestanteAsync(movimientoPagada));

        // Pagada en parte (100 de 118): la nota de 59 aplica 18 y deja −41 a favor.
        var parcial = await FacturaAsync(socio, null, C(ingresos, 1m, 100m));
        var movimientoParcial = Assert.Single(await MovimientosClienteAsync(TipoDocumentoCliente.Factura, parcial)).Id;
        await PagarYAplicarAsync(socio, movimientoParcial, 100m);
        var otro = Ok(await CrearBorradorAsync(parcial));
        Ok(await LineaAsync(otro.Id, (await LineasFacturaAsync(parcial))[0].Id, 0.5m));
        var aplicada = Ok(await PostearAsync(otro.Id));
        Assert.Equal((59m, 18m), Resumen(aplicada));
        Assert.Equal(0m, await RestanteAsync(movimientoParcial));
        Assert.Equal(-41m, await RestanteAsync(Assert.Single(await MovimientosClienteAsync(TipoDocumentoCliente.NotaCredito, aplicada.Numero)).Id));
    }

    [Fact]
    public async Task LaCxCEsLaCongeladaEnLaFactura_NoLaVigenteDelGrupoDeCliente()
    {
        var cxcFactura = await CuentaAsync(posteoDirecto: false);
        var cxcNueva = await CuentaAsync(posteoDirecto: false);
        Guid grupo;
        await using (var contexto = _prueba.NuevoContexto())
        {
            var nuevo = new GrupoClienteContable
            {
                Codigo = $"GC{Guid.NewGuid():N}"[..12].ToUpperInvariant(), Descripcion = "Grupo cliente", CuentaCxCId = cxcFactura, CreatedBy = "test",
            };
            contexto.Set<GrupoClienteContable>().Add(nuevo);
            await contexto.SaveChangesAsync();
            grupo = nuevo.Id;
        }

        var socio = await SocioAsync(grupo);
        var factura = await FacturaAsync(socio, null, C(await CuentaAsync(), 1m, 100m));
        await EjecutarSqlAsync("""UPDATE "GruposClienteContable" SET "CuentaCxCId" = @C WHERE "Id" = @G""", new { C = cxcNueva, G = grupo });

        var resultado = Ok(await PostearAsync(Ok(await CrearBorradorAsync(factura, copiar: true)).Id));

        var nota = await NotaAsync(resultado.Numero);
        Assert.Equal(cxcFactura, nota.CuentaCxCId);
        Assert.Equal(cxcFactura, Assert.Single(await MovimientosClienteAsync(TipoDocumentoCliente.NotaCredito, resultado.Numero)).CuentaCxCId);
        Assert.Equal((cxcFactura, -118m), (await AsientoAsync(nota.RegistroContableId!.Value)).Select(m => (m.CuentaContableId, m.Importe)).First());
    }

    // ----- Review Focus 1: nunca se acredita de más -----

    [Fact]
    public async Task ReviewFocus1_DosNotasConcurrentesSobreLaMismaLinea_UnaFalla_YNuncaSeAcreditaDeMas()
    {
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 10m, 4m, D1));
        var factura = await FacturaAsync(socio, almacen, P(producto, 2m, 50m));
        var linea = Assert.Single(await LineasFacturaAsync(factura));

        // Dos notas concurrentes por 1 cada una (las notas en borrador no cuentan como acreditado): caben las dos.
        var a = Ok(await CrearBorradorAsync(factura));
        var b = Ok(await CrearBorradorAsync(factura));
        Ok(await LineaAsync(a.Id, linea.Id, 1m, devolver: true));
        Ok(await LineaAsync(b.Id, linea.Id, 1m, devolver: true));
        var ambas = await EnParaleloAsync(() => PostearAsync(a.Id), () => PostearAsync(b.Id));
        Assert.All(ambas, r => Assert.True(r.Resultado?.EsExito, r.Excepcion?.ToString() ?? string.Join("; ", r.Resultado!.Errores.Select(e => e.Mensaje))));
        Assert.Equal(2m, await AcreditadoAsync(linea.Id));

        // Pendiente 0: una nota más sobre la línea (forzada por SQL, la captura ya la rechazaría) falla al postear, sin escribir.
        var tardia = Ok(await CrearBorradorSinPendienteAsync(factura));
        var antes = await FotoAsync();
        Assert.Equal(("nota_credito.cantidad_excede", "Lineas[10000].Cantidad"), Unico(await PostearAsync(tardia)));
        Assert.Equal(antes, await FotoAsync());

        // Dos notas concurrentes por TODO lo pendiente de otra factura: exactamente una se postea; la otra, cantidad_excede.
        var otra = await FacturaAsync(socio, almacen, P(producto, 2m, 50m));
        var lineaOtra = Assert.Single(await LineasFacturaAsync(otra));
        var x = Ok(await CrearBorradorAsync(otra));
        var y = Ok(await CrearBorradorAsync(otra));
        Ok(await LineaAsync(x.Id, lineaOtra.Id, 2m, devolver: true));
        Ok(await LineaAsync(y.Id, lineaOtra.Id, 2m, devolver: true));
        var existencia = await _prueba.ConsultarAsync(c => c.ExistenciaAsync(producto, almacen, null));

        var resultados = await EnParaleloAsync(() => PostearAsync(x.Id), () => PostearAsync(y.Id));

        Assert.All(resultados, r => Assert.Null(r.Excepcion));
        Assert.Single(resultados, r => r.Resultado!.EsExito);
        var fallo = Assert.Single(resultados, r => r.Resultado!.EsFallo).Resultado!;
        Assert.Equal(("nota_credito.cantidad_excede", "Lineas[10000].Cantidad"), Unico(fallo));
        Assert.Equal(2m, await AcreditadoAsync(lineaOtra.Id));
        Assert.Equal(existencia + 2m, await _prueba.ConsultarAsync(c => c.ExistenciaAsync(producto, almacen, null)));
        Assert.Equal(1, await EscalarAsync<int>("""SELECT COUNT(*)::int FROM "NotasCreditoVenta" WHERE "FacturaVentaNumero" = @F""", new { F = otra }));
    }

    [Fact]
    public async Task ReviewFocus1_FacturaDeTotalCero_SinMovimientoDeCliente_DosNotasConcurrentes_UnaFalla()
    {
        // Sin movimiento de cliente que bloquear, serializa la fila de la factura (y, como toda nota de total 0 devuelve
        // inventario, también el bloqueo del producto).
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 10m, 4m, D1));
        var factura = await FacturaAsync(socio, almacen, P(producto, 2m, 10m, descuento: 100m));
        var x = Ok(await CrearBorradorAsync(factura, copiar: true, devolver: true));
        var y = Ok(await CrearBorradorAsync(factura, copiar: true, devolver: true));

        var resultados = await EnParaleloAsync(() => PostearAsync(x.Id), () => PostearAsync(y.Id));

        Assert.All(resultados, r => Assert.Null(r.Excepcion));
        Assert.Single(resultados, r => r.Resultado!.EsExito);
        Assert.Equal(("nota_credito.cantidad_excede", "Lineas[10000].Cantidad"), Unico(Assert.Single(resultados, r => r.Resultado!.EsFallo).Resultado!));
        Assert.Equal(2m, await AcreditadoAsync(Assert.Single(await LineasFacturaAsync(factura)).Id));
        Assert.Equal(10m, await _prueba.ConsultarAsync(c => c.ExistenciaAsync(producto, almacen, null)));
    }

    // ----- Review Focus 2: devolución al costo de la venta -----

    [Fact]
    public async Task ReviewFocus2_DevolucionAlCostoVigenteDeLaSalida_ConSusAjustes_Proporcional_YElBatchLaContabilizaContraCostoDeVentas()
    {
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 4m, 3m, D1));
        var factura = await FacturaAsync(socio, almacen, P(producto, 3m, 50m)); // salida D10 a 3: −9
        var salida = Assert.Single(await LineasFacturaAsync(factura)).MovimientoProductoId!.Value;
        Assert.Equal(-9m, await CostoSalidaAsync(salida));

        // Entrada retroactiva (2 × 6 el D1): el promedio del D10 pasa a 4 y el ajuste lleva la salida a −12 (−9 − 3).
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 2m, 6m, D1));
        await _prueba.AjustarOkAsync(producto);
        Assert.Equal(-12m, await CostoSalidaAsync(salida));
        // Entrada posterior (3 × 10 el D11): el promedio VIGENTE del D12 es 7, distinto del de la venta.
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 3m, 10m, D11));
        Assert.Empty((await PostearCostoAsync(producto)).Pendientes);

        // Nota por 2 de 3 con devolución: vuelve 2 × 12 / 3 = 8 (ni 6 del costo original sin ajuste, ni 14 al promedio vigente).
        var borrador = Ok(await CrearBorradorAsync(factura));
        Ok(await LineaAsync(borrador.Id, Assert.Single(await LineasFacturaAsync(factura)).Id, 2m, devolver: true));
        var resultado = Ok(await PostearAsync(borrador.Id));

        var entrada = Assert.Single(await MovimientosInventarioNotaAsync(resultado.Numero));
        Assert.Equal((2m, 8m, 4m, TipoMovimientoInventario.Venta), (entrada.Cantidad, entrada.ImporteCosto, entrada.CostoPorUnidad, entrada.TipoMovimiento));

        // El batch de costo de la Fase 5 contabiliza esa entrada (Venta) contra costo de ventas: Inventario +8 / Costo de ventas −8.
        var batch = await PostearCostoAsync(producto);
        Assert.Empty(batch.Pendientes);
        Assert.Equal([(CuentaContableIds.Inventario, 8m), (CuentaContableIds.CostoVentas, -8m)], await CostoContabilizadoAsync(producto, D12));
    }

    [Fact]
    public async Task ReviewFocus2_CostoNoExacto_LasDevolucionesParcialesSumanExactamenteLaSalida()
    {
        // Promedio del D10: (3 × 3.3333 + 1 × 5) / 4 = 3.749975; la venta de 3 sale por −11.2499 (costo no exacto con 4 decimales).
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 3m, 3.3333m, D1));
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 1m, 5m, D1));
        var factura = await FacturaAsync(socio, almacen, P(producto, 3m, 50m));
        var linea = Assert.Single(await LineasFacturaAsync(factura));
        Assert.Equal(-11.2499m, await CostoSalidaAsync(linea.MovimientoProductoId!.Value));

        var importes = new List<decimal>();
        foreach (var cantidad in new[] { 2m, 1m })
        {
            var borrador = Ok(await CrearBorradorAsync(factura));
            Ok(await LineaAsync(borrador.Id, linea.Id, cantidad, devolver: true));
            var resultado = Ok(await PostearAsync(borrador.Id));
            importes.Add(Assert.Single(await MovimientosInventarioNotaAsync(resultado.Numero)).ImporteCosto);
        }

        // 2 × 11.2499 / 3 = 7.49993… → 7.4999; 1 × 11.2499 / 3 = 3.74996… → 3.7500; suman exactamente 11.2499.
        Assert.Equal([7.4999m, 3.75m], importes);
        Assert.Equal(11.2499m, importes.Sum());
    }

    // ----- Review Focus 3 (en el libro): factura + nota parcial + pago -----

    [Fact]
    public async Task ReviewFocus3_FacturaNotaParcialYPago_SaldoDerivadoExacto_ConElLibroYLaCxC()
    {
        var socio = await SocioAsync();
        var ingresos = await CuentaAsync();
        var factura = await FacturaAsync(socio, null, C(ingresos, 1m, 100m)); // 118
        var borrador = Ok(await CrearBorradorAsync(factura));
        Ok(await LineaAsync(borrador.Id, (await LineasFacturaAsync(factura))[0].Id, 0.25m)); // 25 + 4.5 = 29.5
        Assert.Equal((29.5m, 29.5m), Resumen(Ok(await PostearAsync(borrador.Id))));
        var movimientoFactura = Assert.Single(await MovimientosClienteAsync(TipoDocumentoCliente.Factura, factura)).Id;
        await PagarYAplicarAsync(socio, movimientoFactura, 88.5m);

        // Saldo derivado del detalle = 118 − 29.5 − 88.5 = 0; y la CxC del socio en el libro contable también.
        Assert.Equal(0m, await EscalarAsync<decimal>(
            """
            SELECT COALESCE(SUM(d."Importe"), 0) FROM "MovimientosClienteDetalle" d
            JOIN "MovimientosCliente" m ON m."Id" = d."MovimientoClienteId" WHERE m."SocioNegocioId" = @S
            """, new { S = socio }));
        Assert.Equal(0m, await EscalarAsync<decimal>(
            """SELECT COALESCE(SUM("Importe"), 0) FROM "MovimientosContables" WHERE "CuentaContableId" = @C AND "SocioNegocioId" = @S""",
            new { C = CuentaContableIds.CxC, S = socio }));
        Assert.Equal(0m, await RestanteAsync(movimientoFactura));
    }

    // ----- Ruling FI: nunca se acredita de más en importe, IVA ni costo -----

    [Fact]
    public async Task RulingFI_TresNotasDe1Sobre3x333Al10_SumanExactamenteLoFacturado_EnImporteDescuentoEIva()
    {
        // Factura: 3 × 3.33 al 10 % = 9.99 − 1.00 = 8.99; IVA 18 % = 1.62; total 10.61. Cada nota de 1 calcula 3.33 − 0.33 = 3.00.
        var socio = await SocioAsync();
        var factura = await FacturaAsync(socio, null, C(await CuentaAsync(), 3m, 3.33m, descuento: 10m));
        var linea = Assert.Single(await LineasFacturaAsync(factura));
        var movimientoFactura = Assert.Single(await MovimientosClienteAsync(TipoDocumentoCliente.Factura, factura)).Id;

        var notas = new List<NotaCreditoVenta>();
        for (var i = 0; i < 3; i++)
        {
            var borrador = Ok(await CrearBorradorAsync(factura));
            Ok(await LineaAsync(borrador.Id, linea.Id, 1m));
            var vistaPrevia = Assert.IsType<OpenSource1.Application.Features.FacturasVenta.Calculo.TotalesFactura>(await TotalesAsync(borrador.Id));
            var resultado = Ok(await PostearAsync(borrador.Id));
            Assert.Equal(vistaPrevia.ImporteTotal, resultado.ImporteTotal);
            notas.Add(await NotaAsync(resultado.Numero));
            Assert.Equal(0m, (await AsientoAsync(notas[^1].RegistroContableId!.Value)).Sum(m => m.Importe));
        }

        var lineasNotas = new List<LineaNotaCreditoVenta>();
        foreach (var nota in notas)
        {
            lineasNotas.Add(Assert.Single(await LineasNotaAsync(nota.Numero)));
        }

        Assert.Equal([3.00m, 3.00m, 2.99m], lineasNotas.Select(l => l.ImporteLinea));
        Assert.Equal([0.33m, 0.33m, 0.34m], lineasNotas.Select(l => l.ImporteDescuentoLinea));
        Assert.Equal([0.54m, 0.54m, 0.54m], notas.Select(n => n.ImporteIva));
        Assert.Equal((8.99m, 1.62m, 10.61m), (notas.Sum(n => n.ImporteSinIva), notas.Sum(n => n.ImporteIva), notas.Sum(n => n.ImporteTotal)));
        Assert.Equal(0m, await RestanteAsync(movimientoFactura));
    }

    [Fact]
    public async Task RulingFI_SinDescuento_0335x3_TresNotasSuman101_YElIvaDeBasesPequenasNuncaExcedeElDeLaFactura()
    {
        var socio = await SocioAsync();
        var cuenta = await CuentaAsync();

        // 3 × 0.335 = 1.005 -> 1.01; cada nota de 1 calcula 0.34.
        Assert.Equal([0.34m, 0.34m, 0.33m], await TresNotasDe1Async(await FacturaAsync(socio, null, C(cuenta, 3m, 0.335m)), n => n.ImporteSinIva));

        // 3 × 0.03 al 18 %: IVA de la factura ROUND(0.0162) = 0.02; cada nota calcula ROUND(0.0054) = 0.01 -> 0.01, 0.01, 0.00.
        Assert.Equal([0.01m, 0.01m, 0m], await TresNotasDe1Async(await FacturaAsync(socio, null, C(cuenta, 3m, 0.03m)), n => n.ImporteIva));

        // 3 × 0.01 al 18 %: IVA de la factura 0.01; cada nota calcula 0.00; la que agota el grupo toma el remanente 0.01.
        Assert.Equal([0m, 0m, 0.01m], await TresNotasDe1Async(await FacturaAsync(socio, null, C(cuenta, 3m, 0.01m)), n => n.ImporteIva));
    }

    [Fact]
    public async Task RulingFI_DevolucionEnTresNotasDeUnaSalidaDe10_0001_SumaExacta_YElValorDelProductoVuelveAlOriginal()
    {
        // Entradas 1 × 3.3333 + 2 × 3.3334 = 10.0001; la venta de 3 sale por −10.0001; cada devolución de 1 calcula 3.3334.
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 1m, 3.3333m, D1));
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 2m, 3.3334m, D1));
        var factura = await FacturaAsync(socio, almacen, P(producto, 3m, 50m));
        var linea = Assert.Single(await LineasFacturaAsync(factura));
        Assert.Equal(-10.0001m, await CostoSalidaAsync(linea.MovimientoProductoId!.Value));

        var importes = new List<decimal>();
        for (var i = 0; i < 3; i++)
        {
            var borrador = Ok(await CrearBorradorAsync(factura));
            Ok(await LineaAsync(borrador.Id, linea.Id, 1m, devolver: true));
            var resultado = Ok(await PostearAsync(borrador.Id));
            importes.Add(Assert.Single(await MovimientosInventarioNotaAsync(resultado.Numero)).ImporteCosto);
            Assert.Equal(0m, (await AsientoAsync((await NotaAsync(resultado.Numero)).RegistroContableId!.Value)).Sum(m => m.Importe));
        }

        Assert.Equal([3.3334m, 3.3334m, 3.3333m], importes);
        await using var conexion = _prueba.NuevaConexion();
        Assert.Equal((10.0001m, 3m), await conexion.QuerySingleAsync<(decimal, decimal)>(
            """SELECT SUM("ImporteCosto"), SUM("CantidadValorada") FROM "MovimientosValor" WHERE "ProductoId" = @P""", new { P = producto }));
    }

    [Fact]
    public async Task RulingFI_EntradaVentaDeOtroOrigen_SigueExigiendo4DecimalesEnElCosto()
    {
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        var venta = LibroInventarioPrueba.Entrada(producto, almacen, 1m, 1.00001m, D1, tipo: TipoMovimientoInventario.Venta);

        var otroOrigen = await _prueba.RegistrarAsync(venta);
        Assert.Equal(("inventario.costo_invalido", "CostoUnitario"), Unico(otroOrigen));
        Assert.True((await _prueba.RegistrarAsync(venta with { TipoOrigen = TipoOrigenMovimiento.NotaCreditoVenta })).EsExito);
    }

    [Fact]
    public async Task RulingFI_ElDocumentoUsaImportesRecalculadosDesdeLaFactura_NoLosGuardadosEnElBorrador()
    {
        var socio = await SocioAsync();
        var factura = await FacturaAsync(socio, null, C(await CuentaAsync(), 2m, 50m));
        var borrador = Ok(await CrearBorradorAsync(factura));
        var linea = Ok(await LineaAsync(borrador.Id, Assert.Single(await LineasFacturaAsync(factura)).Id, 1m));
        await EjecutarSqlAsync(
            """UPDATE "LineasNotaCreditoVentaBorrador" SET "ImporteLinea" = 999, "PrecioUnitario" = 999 WHERE "Id" = @Id""", new { Id = linea.Id });

        var resultado = Ok(await PostearAsync(borrador.Id));

        Assert.Equal(59m, resultado.ImporteTotal);
        Assert.Equal((50m, 50m), (Assert.Single(await LineasNotaAsync(resultado.Numero)) is var l ? (l.ImporteLinea, l.PrecioUnitario) : default));
    }

    /// <summary>Tres notas de 1 (sin devolución) sobre la única línea de la factura; devuelve el valor elegido de cada nota posteada.</summary>
    private async Task<List<decimal>> TresNotasDe1Async(string factura, Func<NotaCreditoVenta, decimal> valor)
    {
        var linea = Assert.Single(await LineasFacturaAsync(factura));
        var valores = new List<decimal>();
        for (var i = 0; i < 3; i++)
        {
            var borrador = Ok(await CrearBorradorAsync(factura));
            Ok(await LineaAsync(borrador.Id, linea.Id, 1m));
            var nota = await NotaAsync(Ok(await PostearAsync(borrador.Id)).Numero);
            valores.Add(valor(nota));
            if (nota.RegistroContableId is { } registro)
            {
                Assert.Equal(0m, (await AsientoAsync(registro)).Sum(m => m.Importe));
            }
        }

        return valores;
    }

    // ----- Total 0 (Ruling FE) -----

    [Fact]
    public async Task FacturaDeTotalCero_NotaDeDevolucionDelObsequio_DocumentoEInventario_SinClienteNiAsiento()
    {
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 10m, 4m, D1));
        var factura = await FacturaAsync(socio, almacen, P(producto, 2m, 10m, descuento: 100m));
        Assert.Empty(await MovimientosClienteAsync(TipoDocumentoCliente.Factura, factura));

        // Sin devolución: total 0 -> importe_cero y nada escrito.
        var sinDevolucion = Ok(await CrearBorradorAsync(factura, copiar: true));
        Assert.Null(sinDevolucion.CuentaCxCId);
        var antes = await FotoAsync();
        Assert.Equal(("nota_credito.importe_cero", "Lineas"), Unico(await PostearAsync(sinDevolucion.Id)));
        Assert.Equal(antes, await FotoAsync());

        // Con devolución de todas sus líneas: documento de total 0 y la entrada al costo de la salida; ni cliente ni asiento.
        var conDevolucion = Ok(await CrearBorradorAsync(factura, copiar: true, devolver: true));
        antes = await FotoFilasAsync();
        var resultado = Ok(await PostearAsync(conDevolucion.Id));

        Assert.Equal((0m, 0m, (string?)null), (resultado.ImporteTotal, resultado.ImporteAplicado, resultado.RegistroContable));
        var despues = await FotoFilasAsync();
        Assert.Equal(
            [("NotasCreditoVenta", 1L), ("LineasNotaCreditoVenta", 1L), ("LineasIvaNotaCreditoVenta", 1L), ("MovimientosCliente", 0L),
             ("MovimientosClienteDetalle", 0L), ("RegistrosContables", 0L), ("MovimientosContables", 0L), ("MovimientosProducto", 1L),
             ("MovimientosValor", 1L), ("AplicacionesMovimientoProducto", 0L)],
            TablasEscritas.Select(t => (t, long.Parse(despues[t]!) - long.Parse(antes[t]!))));
        Assert.Equal(antes["serie CONTAB"], despues["serie CONTAB"]);
        var nota = await NotaAsync(resultado.Numero);
        Assert.Equal((0m, (long?)null, (Guid?)null), (nota.ImporteTotal, nota.RegistroContableId, nota.CuentaCxCId));
        var entrada = Assert.Single(await MovimientosInventarioNotaAsync(resultado.Numero));
        Assert.Equal((2m, 8m), (entrada.Cantidad, entrada.ImporteCosto));
    }

    [Fact]
    public async Task NotaDeTotalCero_SobreFacturaConImporte_SoloLaLineaAl100_SinDevolucion_ImporteCero()
    {
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 10m, 4m, D1));
        var factura = await FacturaAsync(socio, almacen, P(producto, 1m, 10m), P(producto, 1m, 10m, descuento: 100m));
        var regalo = (await LineasFacturaAsync(factura))[1];

        var borrador = Ok(await CrearBorradorAsync(factura));
        Ok(await LineaAsync(borrador.Id, regalo.Id, 1m));
        var antes = await FotoAsync();
        Assert.Equal(("nota_credito.importe_cero", "Lineas"), Unico(await PostearAsync(borrador.Id)));
        Assert.Equal(antes, await FotoAsync());

        // Con la devolución del regalo sí se postea (total 0: sin cliente ni asiento).
        var linea = Assert.Single(await LineasBorradorAsync(borrador.Id));
        Ok(await ModificarLineaAsync(linea.Id, 1m, devolver: true, linea.Xmin));
        Assert.Equal((0m, 0m), Resumen(Ok(await PostearAsync(borrador.Id))));
    }

    // ----- Aviso de numeración (spec no-series, NS4) -----

    [Fact]
    public async Task NumeroDeAviso_AlcanzadoEnLaSerieNc_ElResultadoTraeLaAdvertencia_YSinAvisoEsNull()
    {
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 10m, 4m, D1));
        var factura = await FacturaAsync(socio, almacen, P(producto, 2m, 50m));
        var sinAviso = Ok(await PostearAsync(Ok(await CrearBorradorAsync(factura, copiar: true)).Id));
        Assert.Null(sinAviso.AvisoNumeracion);

        var otra = await FacturaAsync(socio, almacen, P(producto, 2m, 50m));
        var borrador = Ok(await CrearBorradorAsync(otra, copiar: true));
        try
        {
            // El aviso en el número que se va a emitir: siguiente = último + 1.
            var siguiente = Siguiente(await UltimoNumeroAsync(SerieNotaCreditoVentaIds.LineaSeriePosteadaId));
            await EjecutarSqlAsync("""UPDATE "LineasSerie" SET "NumeroAviso" = @A WHERE "Id" = @Id""",
                new { A = siguiente, Id = SerieNotaCreditoVentaIds.LineaSeriePosteadaId });

            var resultado = Ok(await PostearAsync(borrador.Id));

            Assert.Equal(siguiente, resultado.Numero);
            Assert.Contains("número de aviso", resultado.AvisoNumeracion);
        }
        finally
        {
            await EjecutarSqlAsync("""UPDATE "LineasSerie" SET "NumeroAviso" = NULL WHERE "Id" = @Id""",
                new { Id = SerieNotaCreditoVentaIds.LineaSeriePosteadaId });
        }
    }

    // ----- Nada escrito -----

    [Fact]
    public async Task SetupDeVentasFaltante_FalloConLaLinea_YNingunaFilaNiNumeroNiIntentoDeEscritura()
    {
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var grupo = await GrupoProductoConSetupAsync();
        var bueno = await ProductoAsync();
        var malo = await ProductoAsync(grupo);
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(bueno, almacen, 5m, 1m, D1));
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(malo, almacen, 5m, 1m, D1));
        var factura = await FacturaAsync(socio, almacen, P(bueno, 1m, 10m), P(malo, 1m, 10m));
        var borrador = Ok(await CrearBorradorAsync(factura, copiar: true, devolver: true));
        await EjecutarSqlAsync("""UPDATE "SetupsContableGeneral" SET "IsDeleted" = true WHERE "GrupoProductoId" = @G""", new { G = grupo });
        var antes = await FotoAsync();

        var error = Assert.Single((await PostearAsync(borrador.Id)).Errores);

        Assert.Equal(("setup_contable.inexistente", "Lineas[20000].GrupoProductoId"), (error.Codigo, error.Campo));
        Assert.StartsWith("Línea 20000: ", error.Mensaje);
        Assert.Equal(antes, await FotoAsync());
        Assert.Equal((false, 2L, 0L), await EstadoBorradorAsync(borrador.Id));
    }

    [Fact]
    public async Task FechaDeRegistroNoPermitida_400EnFechaRegistro_SinEscribir_YDentroDelRangoSePostea()
    {
        var fechas = new FechasRegistroPrueba(_prueba);
        var socio = await SocioAsync();
        var factura = await FacturaAsync(socio, null, C(await CuentaAsync(), 1m, 100m));
        var borrador = Ok(await CrearBorradorAsync(factura, copiar: true)); // FechaRegistro D12
        try
        {
            await fechas.GeneralAsync(D1, D11);
            var antes = await FotoAsync();
            var fallo = await PostearComoAsync(borrador.Id, null);
            Assert.Equal(("registro.fecha_no_permitida", "FechaRegistro"), Unico(fallo));
            Assert.Contains("12/09/2026", fallo.Errores[0].Mensaje);
            Assert.Equal(antes, await FotoAsync());

            await fechas.GeneralAsync(D12, D12);
            Assert.True((await PostearComoAsync(borrador.Id, null)).EsExito);
        }
        finally
        {
            await fechas.LimpiarAsync();
        }
    }

    [Fact]
    public async Task CantidadNoExactaEnLaUnidadBase_SeRechazaAlCapturarYAlPostear_SinEscribir()
    {
        // Factura de 1 caja (CJA × 12 UND, sin decimales). Devolver 0.1 CJA = 1.2 UND no es exacto; 0.25 CJA = 3 UND sí.
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 24m, 1m, D1));
        var factura = await FacturaAsync(socio, almacen, P(producto, 1m, 120m, unidad: _prueba.UnidadCja));
        var linea = Assert.Single(await LineasFacturaAsync(factura));
        var borrador = Ok(await CrearBorradorAsync(factura));

        Assert.Equal(("conversion.cantidad_no_exacta", "Cantidad"), Unico(await LineaAsync(borrador.Id, linea.Id, 0.1m, devolver: true)));
        // Sin devolución la cantidad no toca el inventario: se admite.
        var sinDevolucion = Ok(await LineaAsync(borrador.Id, linea.Id, 0.1m));
        Ok(await ModificarLineaAsync(sinDevolucion.Id, 0.25m, devolver: true, sinDevolucion.Xmin));

        // Revalidación al postear (cantidad tocada por SQL): mismo error, con la línea, y nada escrito.
        await EjecutarSqlAsync("""UPDATE "LineasNotaCreditoVentaBorrador" SET "Cantidad" = 0.1 WHERE "Id" = @Id""", new { Id = sinDevolucion.Id });
        var antes = await FotoAsync();
        Assert.Equal(("conversion.cantidad_no_exacta", "Lineas[10000].Cantidad"), Unico(await PostearAsync(borrador.Id)));
        Assert.Equal(antes, await FotoAsync());

        await EjecutarSqlAsync("""UPDATE "LineasNotaCreditoVentaBorrador" SET "Cantidad" = 0.25 WHERE "Id" = @Id""", new { Id = sinDevolucion.Id });
        var resultado = Ok(await PostearAsync(borrador.Id));
        var entrada = Assert.Single(await MovimientosInventarioNotaAsync(resultado.Numero));
        Assert.Equal((3m, 3m, LibroInventarioPrueba.UnidadUnd), (entrada.Cantidad, entrada.ImporteCosto, entrada.UnidadMedidaId));
    }

    // ----- Captura -----

    [Fact]
    public async Task Captura_ReglasDeLaCabeceraYDeLasLineas()
    {
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        var ingresos = await CuentaAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 10m, 4m, D1));
        var factura = await FacturaAsync(socio, almacen, P(producto, 2m, 10m), C(ingresos, 1m, 1m), Comentario("Gracias"));
        var otra = await FacturaAsync(socio, null, C(ingresos, 1m, 5m));
        var lineas = await LineasFacturaAsync(factura);

        Assert.Equal(("nota_credito.factura_invalida", "FacturaVentaNumero"), Unico(await CrearBorradorAsync("NOEXISTE")));
        Assert.Equal(("nota_credito.fecha_invalida", "FechaRegistro"), Unico(await CrearBorradorAsync(factura, fecha: D1)));

        var borrador = Ok(await CrearBorradorAsync(factura));
        Assert.Equal(("nota_credito_borrador.no_encontrado", "NotaCreditoVentaBorradorId"), Unico(await LineaAsync(Guid.NewGuid(), lineas[0].Id, 1m)));
        Assert.Equal(("nota_credito.linea_factura_invalida", "LineaFacturaVentaId"),
            Unico(await LineaAsync(borrador.Id, (await LineasFacturaAsync(otra))[0].Id, 1m)));
        Assert.Equal(("nota_credito.linea_no_acreditable", "LineaFacturaVentaId"), Unico(await LineaAsync(borrador.Id, lineas[2].Id, 1m)));
        Assert.Equal(("nota_credito.cantidad_invalida", "Cantidad"), Unico(await LineaAsync(borrador.Id, lineas[0].Id, 0m)));
        Assert.Equal(("nota_credito.cantidad_excede", "Cantidad"), Unico(await LineaAsync(borrador.Id, lineas[0].Id, 3m)));
        Assert.Equal(("nota_credito.devolucion_invalida", "DevolverInventario"), Unico(await LineaAsync(borrador.Id, lineas[1].Id, 1m, devolver: true)));
        Assert.Equal(("nota_credito.importe_invalido", "Cantidad"), Unico(await LineaAsync(borrador.Id, lineas[1].Id, 0.001m)));
        var creada = Ok(await LineaAsync(borrador.Id, lineas[0].Id, 1.5m));
        Assert.Equal((10000, 10m, 15m, 2m, 0m), (creada.NumeroLinea, creada.PrecioUnitario, creada.ImporteLinea, creada.CantidadFacturada, creada.CantidadAcreditada));
        Assert.Equal(("nota_credito.linea_duplicada", "LineaFacturaVentaId"), Unico(await LineaAsync(borrador.Id, lineas[0].Id, 0.5m)));
        Assert.Equal(("nota_credito.cantidad_excede", "Cantidad"), Unico(await ModificarLineaAsync(creada.Id, 2.5m, true, creada.Xmin)));
    }

    // ----- Guardas de uso -----

    [Fact]
    public async Task GuardasDeUso_SocioCuentaYGrupos_VenLosBorradoresYLasNotasPosteadas()
    {
        var socio = await SocioAsync();
        var factura = await FacturaAsync(socio, null, C(await CuentaAsync(), 1m, 100m));
        var otroSocio = await SocioAsync();
        var cxc = await CuentaAsync();
        var cuentaLinea = await CuentaAsync();
        var grupoProducto = await GrupoAsync<GrupoProducto>();
        var borrador = Ok(await CrearBorradorAsync(factura, copiar: true));
        var linea = Assert.Single(await LineasBorradorAsync(borrador.Id));

        await using var scope = _prueba.Provider.CreateAsyncScope();
        var socios = scope.ServiceProvider.GetRequiredService<ISocioNegocioUsoService>();
        var cuentas = scope.ServiceProvider.GetRequiredService<ICuentaContableUsoService>();
        var grupos = scope.ServiceProvider.GetRequiredService<IGrupoContableUsoService>();
        Assert.False(await socios.EstaEnUsoAsync(otroSocio));
        Assert.False(await cuentas.EstaEnUsoAsync(cxc));
        Assert.False(await cuentas.EstaEnUsoAsync(cuentaLinea));
        Assert.False(await grupos.EstaEnUsoAsync(TipoGrupoContable.Producto, grupoProducto));

        // Referencias solo desde el borrador de la nota (por SQL: la captura las copia de la factura).
        await EjecutarSqlAsync(
            """
            UPDATE "NotasCreditoVentaBorrador" SET "SocioNegocioFacturarAId" = @S, "CuentaCxCId" = @Cxc WHERE "Id" = @B;
            UPDATE "LineasNotaCreditoVentaBorrador" SET "CuentaContableId" = @Cl WHERE "Id" = @L;
            """,
            new { S = otroSocio, Cxc = cxc, B = borrador.Id, Cl = cuentaLinea, L = linea.Id });
        await EjecutarSqlAsync(
            """UPDATE "LineasNotaCreditoVentaBorrador" SET "GrupoProductoId" = @G WHERE "Id" = @L""", new { G = grupoProducto, L = linea.Id });
        Assert.True(await socios.EstaEnUsoAsync(otroSocio));
        Assert.True(await cuentas.EstaEnUsoAsync(cxc));
        Assert.True(await cuentas.EstaEnUsoAsync(cuentaLinea));
        Assert.True(await grupos.EstaEnUsoAsync(TipoGrupoContable.Producto, grupoProducto));

        // Borrado el borrador, ya no; una nota POSTEADA del socio sí (insertada por SQL: el documento solo admite INSERT).
        Ok(await BorrarBorradorAsync(borrador.Id));
        Assert.False(await socios.EstaEnUsoAsync(otroSocio));
        Assert.False(await cuentas.EstaEnUsoAsync(cxc));
        await EjecutarSqlAsync(
            """
            INSERT INTO "NotasCreditoVenta" ("Numero", "NumeroBorrador", "FacturaVentaNumero", "SocioNegocioId", "SocioNegocioFacturarAId",
                "NombreFacturacion", "TipoDocumentoFiscal", "FechaRegistro", "FechaDocumento", "GrupoNegocioId", "GrupoIvaNegocioId",
                "GrupoClienteContableId", "Moneda", "ImporteSinIva", "ImporteIva", "ImporteTotal", "CreatedAtUtc", "CreatedBy")
            SELECT @N, @N, "Numero", "SocioNegocioId", @S, "NombreFacturacion", "TipoDocumentoFiscal", "FechaRegistro", "FechaDocumento",
                   "GrupoNegocioId", "GrupoIvaNegocioId", "GrupoClienteContableId", "Moneda", 0, 0, 0, now(), 'test'
            FROM "FacturasVenta" WHERE "Numero" = @F
            """,
            new { N = $"T{Guid.NewGuid():N}"[..20], S = otroSocio, F = factura });
        Assert.True(await socios.EstaEnUsoAsync(otroSocio));

        // El documento posteado es append-only.
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => EjecutarSqlAsync(
            """UPDATE "NotasCreditoVenta" SET "Descripcion" = 'x' WHERE "SocioNegocioFacturarAId" = @S""", new { S = otroSocio }));
        Assert.Equal("P0001", ex.SqlState);
    }

    // ----- Series y borrador conservado (spec no-series) -----

    [Fact]
    public async Task Postear_ConSerieDeRegistroDelAdministrador_NumeraConElla_YElBorradorQuedaPosteada()
    {
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 10m, 4m, D1));
        var factura = await FacturaAsync(socio, almacen, P(producto, 2m, 50m));
        var (serieRegistro, lineaRegistro, prefijoRegistro) = await SeriesPrueba.CrearAsync(fixture.AppConnectionString, TipoDocumentoSerie.NotaCreditoVenta);
        var ultimoNc = await UltimoNumeroAsync(SerieNotaCreditoVentaIds.LineaSeriePosteadaId);

        var borrador = Ok(await CrearBorradorConSeriesAsync(factura, null, serieRegistro));
        var resultado = Ok(await PostearAsync(borrador.Id));

        Assert.Equal((SerieNotaCreditoVentaIds.SerieBorradorId, EstadoNotaCreditoBorrador.Abierta), (borrador.SerieBorradorId, borrador.Estado));
        Assert.Equal($"{prefijoRegistro}000001", resultado.Numero);
        Assert.Equal($"{prefijoRegistro}000001", await SeriesPrueba.UltimoAsync(fixture.AppConnectionString, lineaRegistro));
        Assert.Equal(ultimoNc, await UltimoNumeroAsync(SerieNotaCreditoVentaIds.LineaSeriePosteadaId));
        Assert.Equal((EstadoNotaCreditoBorrador.Posteada, resultado.Numero), await EstadoYNotaAsync(borrador.Id));
        Assert.Equal(borrador.Numero, (await NotaAsync(resultado.Numero)).NumeroBorrador);
    }

    [Fact]
    public async Task Alta_SinSeries_UsaLasConfiguradas_YSerieDeOtroTipo_400()
    {
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 10m, 4m, D1));
        var factura = await FacturaAsync(socio, almacen, P(producto, 2m, 50m));
        var (deFactura, _, _) = await SeriesPrueba.CrearAsync(fixture.AppConnectionString, TipoDocumentoSerie.FacturaVenta);

        var porDefecto = Ok(await CrearBorradorConSeriesAsync(factura, null, null));
        var otroTipo = await CrearBorradorConSeriesAsync(factura, null, deFactura);

        Assert.Equal(("NC-BORR", "NC"), (porDefecto.SerieBorradorCodigo, porDefecto.SerieRegistroCodigo));
        Assert.Equal(("numeracion.tipo_incorrecto", "SerieRegistroId"), Unico(otroTipo));
    }

    [Fact]
    public async Task Alta_SerieDeBorradorOSerieDeRegistroSinAdministrador_400NoEditable_YModificarIgual()
    {
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 10m, 4m, D1));
        var factura = await FacturaAsync(socio, almacen, P(producto, 2m, 50m));
        var (serieBorrador, _, _) = await SeriesPrueba.CrearAsync(fixture.AppConnectionString, TipoDocumentoSerie.BorradorNotaCreditoVenta);
        var (serieRegistro, _, _) = await SeriesPrueba.CrearAsync(fixture.AppConnectionString, TipoDocumentoSerie.NotaCreditoVenta);
        var ultimoNcBorr = await UltimoNumeroAsync(SerieNotaCreditoVentaIds.LineaSerieBorradorId);

        Assert.Equal(("serie.no_editable", "SerieBorradorId"), Unico(await CrearBorradorConSeriesAsync(factura, serieBorrador, null)));
        Assert.Equal(("serie.no_editable", "SerieRegistroId"), Unico(await CrearBorradorConSeriesAsync(factura, null, serieRegistro, administrador: false)));
        Assert.Equal(ultimoNcBorr, await UltimoNumeroAsync(SerieNotaCreditoVentaIds.LineaSerieBorradorId));

        var borrador = Ok(await CrearBorradorConSeriesAsync(factura, null, null, administrador: false));
        Assert.Equal(("serie.no_editable", "SerieRegistroId"), Unico(await ModificarCabeceraAsync(borrador.Id, borrador.Xmin, serieRegistro, administrador: false)));
        Assert.Equal(serieRegistro, Ok(await ModificarCabeceraAsync(borrador.Id, borrador.Xmin, serieRegistro)).SerieRegistroId);
    }

    [Fact]
    public async Task ReviewFocus2_SerieRegistroDesactivadaAntesDePostear_400_SinEscribirNada()
    {
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 10m, 4m, D1));
        var factura = await FacturaAsync(socio, almacen, P(producto, 2m, 50m));
        var (serieRegistro, lineaRegistro, _) = await SeriesPrueba.CrearAsync(fixture.AppConnectionString, TipoDocumentoSerie.NotaCreditoVenta);
        var borrador = Ok(await CrearBorradorConSeriesAsync(factura, null, serieRegistro));
        await SeriesPrueba.EjecutarAsync(fixture.AppConnectionString, """UPDATE "Series" SET "Activa" = false WHERE "Id" = @Id""", new { Id = serieRegistro });
        var antes = await FotoAsync();

        var resultado = await PostearAsync(borrador.Id);

        Assert.Equal(("numeracion.serie_inactiva", "SerieRegistroId"), Unico(resultado));
        Assert.Equal(antes, await FotoAsync());
        Assert.Equal("", await SeriesPrueba.UltimoAsync(fixture.AppConnectionString, lineaRegistro));
        Assert.Equal((EstadoNotaCreditoBorrador.Abierta, (string?)null), await EstadoYNotaAsync(borrador.Id));
    }

    [Fact]
    public async Task ReviewFocus3_ElMismoBorradorDosVecesEnParalelo_UnoGanaYElOtro409()
    {
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 10m, 4m, D1));
        var factura = await FacturaAsync(socio, almacen, P(producto, 2m, 50m));
        var borrador = Ok(await CrearBorradorConSeriesAsync(factura, null, null));

        var resultados = await EnParaleloAsync(() => PostearAsync(borrador.Id), () => PostearAsync(borrador.Id));

        Assert.All(resultados, r => Assert.Null(r.Excepcion));
        Assert.Single(resultados, r => r.Resultado!.EsExito);
        Assert.Equal(("nota_credito_borrador.posteada.conflicto", "Id"), Unico(Assert.Single(resultados, r => r.Resultado!.EsFallo).Resultado!));
        Assert.Equal(1, await EscalarAsync<int>("""SELECT COUNT(*)::int FROM "NotasCreditoVenta" WHERE "NumeroBorrador" = @N""", new { N = borrador.Numero }));
        var ganador = Assert.Single(resultados, r => r.Resultado!.EsExito).Resultado!.Valor!;
        Assert.Equal((EstadoNotaCreditoBorrador.Posteada, ganador.Numero), await EstadoYNotaAsync(borrador.Id));
        Assert.Equal((false, 1L, 0L), await EstadoBorradorAsync(borrador.Id));
    }

    [Fact]
    public async Task BorradorPosteada_EsDeSoloLectura_YElListadoPorDefectoNoLoMuestra()
    {
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 10m, 4m, D1));
        var factura = await FacturaAsync(socio, almacen, P(producto, 3m, 50m));
        var borrador = Ok(await CrearBorradorConSeriesAsync(factura, null, null));
        var linea = (await LineasBorradorAsync(borrador.Id))[0];
        Ok(await PostearAsync(borrador.Id));
        var antes = await FotoAsync();

        Assert.Equal(("nota_credito_borrador.posteada.conflicto", "Id"), Unico(await ModificarCabeceraAsync(borrador.Id, borrador.Xmin)));
        Assert.Equal("nota_credito_borrador.posteada.conflicto", Unico(await ModificarLineaAsync(linea.Id, 1m, false, linea.Xmin)).Codigo);
        Assert.Equal("nota_credito_borrador.posteada.conflicto", Unico(await LineaAsync(borrador.Id, linea.LineaFacturaVentaId, 1m)).Codigo);
        Assert.Equal(("nota_credito_borrador.posteada.conflicto", "Id"), Unico(await BorrarBorradorAsync(borrador.Id)));
        Assert.Equal(("nota_credito_borrador.posteada.conflicto", "Id"), Unico(await PostearAsync(borrador.Id)));
        Assert.Equal(antes, await FotoAsync());

        Assert.DoesNotContain((await ListarBorradoresAsync(factura, null)).Items, b => b.Id == borrador.Id);
        Assert.Contains((await ListarBorradoresAsync(factura, EstadoNotaCreditoBorrador.Posteada)).Items, b => b.Id == borrador.Id);
    }

    [Fact]
    public async Task ReviewFocus2_SerieRegistroCambiadaDeTipoAntesDePostear_400_SinEscribirNada()
    {
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 10m, 4m, D1));
        var factura = await FacturaAsync(socio, almacen, P(producto, 2m, 50m));
        var (serieRegistro, lineaRegistro, _) = await SeriesPrueba.CrearAsync(fixture.AppConnectionString, TipoDocumentoSerie.NotaCreditoVenta);
        var borrador = Ok(await CrearBorradorConSeriesAsync(factura, null, serieRegistro));
        await SeriesPrueba.EjecutarAsync(fixture.AppConnectionString, """UPDATE "Series" SET "TipoDocumento" = @Tipo WHERE "Id" = @Id""",
            new { Id = serieRegistro, Tipo = (short)TipoDocumentoSerie.FacturaVenta });
        var antes = await FotoAsync();

        var resultado = await PostearAsync(borrador.Id);

        Assert.Equal(("numeracion.tipo_incorrecto", "SerieRegistroId"), Unico(resultado));
        Assert.Equal(antes, await FotoAsync());
        Assert.Equal("", await SeriesPrueba.UltimoAsync(fixture.AppConnectionString, lineaRegistro));
        Assert.Equal((EstadoNotaCreditoBorrador.Abierta, (string?)null), await EstadoYNotaAsync(borrador.Id));
    }

    [Fact]
    public async Task BorradorPosteada_SusTotalesYLineasSonLosDeSuNota_NotaTotalYParcial()
    {
        var socio = await SocioAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var producto = await ProductoAsync();
        var otrosIngresos = await CuentaAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 10m, 4m, D1));

        // Nota total: 3 × 50 (ITBIS 18 %) + 25 exento = 202. Sin excluir la propia nota, la vista previa saldría a 0.
        var facturaTotal = await FacturaAsync(socio, almacen, P(producto, 3m, 50m), C(otrosIngresos, 1m, 25m, GrupoContableIds.IvaProductoExento));
        var total = Ok(await CrearBorradorAsync(facturaTotal, copiar: true, devolver: true));
        var notaTotal = Ok(await PostearAsync(total.Id)).Numero;

        await TotalesSonLosDeLaNotaAsync(total.Id, notaTotal, (175m, 27m, 202m));
        Assert.Equal([(10000, 3m, 3m, 0m), (20000, 1m, 1m, 0m)],
            (await LineasBorradorAsync(total.Id)).Select(l => (l.NumeroLinea, l.Cantidad, l.CantidadFacturada, l.CantidadAcreditada)));
        Assert.Equal([(10000, 0m, 3m, 3m), (20000, 0m, 1m, 1m)],
            (await AcreditablesAsync(total.Id)).Select(a => (a.NumeroLinea, a.CantidadAcreditada, a.CantidadPendiente, a.CantidadEnBorrador!.Value)));

        // Nota parcial (dos de tres líneas de 1 × 10.03) y después otra nota por la tercera: la primera sigue con los de su nota.
        var facturaParcial = await FacturaAsync(socio, almacen, P(producto, 1m, 10.03m), P(producto, 1m, 10.03m), P(producto, 1m, 10.03m));
        var lineasFactura = await LineasFacturaAsync(facturaParcial);
        var parcial = Ok(await CrearBorradorAsync(facturaParcial));
        Ok(await LineaAsync(parcial.Id, lineasFactura[0].Id, 1m));
        Ok(await LineaAsync(parcial.Id, lineasFactura[1].Id, 1m));
        var notaParcial = Ok(await PostearAsync(parcial.Id)).Numero;
        var segunda = Ok(await CrearBorradorAsync(facturaParcial));
        Ok(await LineaAsync(segunda.Id, lineasFactura[2].Id, 1m));
        var notaSegunda = Ok(await PostearAsync(segunda.Id)).Numero;

        await TotalesSonLosDeLaNotaAsync(parcial.Id, notaParcial, (20.06m, 3.61m, 23.67m));
        await TotalesSonLosDeLaNotaAsync(segunda.Id, notaSegunda, (10.03m, 1.81m, 11.84m));
        Assert.Equal([(10000, 0m), (20000, 0m)], (await LineasBorradorAsync(parcial.Id)).Select(l => (l.NumeroLinea, l.CantidadAcreditada)));
        Assert.Equal([(10000, 0m, 1m), (20000, 0m, 1m), (30000, 1m, 0m)],
            (await AcreditablesAsync(parcial.Id)).Select(a => (a.NumeroLinea, a.CantidadAcreditada, a.CantidadPendiente)));
    }

    private async Task TotalesSonLosDeLaNotaAsync(Guid borradorId, string numeroNota, (decimal SinIva, decimal Iva, decimal Total) esperado)
    {
        var totales = Assert.IsType<OpenSource1.Application.Features.FacturasVenta.Calculo.TotalesFactura>(await TotalesAsync(borradorId));
        var nota = await NotaAsync(numeroNota);
        Assert.Equal(esperado, (nota.ImporteSinIva, nota.ImporteIva, nota.ImporteTotal));
        Assert.Equal(esperado, (totales.ImporteSinIva, totales.ImporteIva, totales.ImporteTotal));
        Assert.Equal(
            (await LineasIvaNotaAsync(numeroNota)).Select(l => (l.IdentificadorIva, l.PorcentajeIva, l.BaseImponible, l.ImporteIva)),
            totales.Grupos.Select(g => (g.IdentificadorIva, g.PorcentajeIva, g.BaseImponible, g.ImporteIva)));
    }

    // ----- Helpers: siembra -----

    private async Task<Guid> SocioAsync(Guid? grupoCliente = null)
    {
        await using var contexto = _prueba.NuevoContexto();
        var socio = new SocioNegocio
        {
            Codigo = $"NC{Guid.NewGuid():N}"[..10].ToUpperInvariant(),
            NombreComercial = "Cliente nota",
            RazonSocial = "Cliente nota SRL",
            TipoDocumentoFiscal = TipoDocumentoFiscal.Rnc,
            NumeroDocumentoFiscal = $"1{Random.Shared.Next(10_000_000, 99_999_999)}",
            GrupoNegocioId = GrupoContableIds.NegocioNacional,
            GrupoIvaNegocioId = GrupoContableIds.IvaNegocioItbis18,
            GrupoClienteContableId = grupoCliente ?? GrupoContableIds.ClienteContableGeneral,
            CreatedBy = "test",
        };
        contexto.Set<SocioNegocio>().Add(socio);
        await contexto.SaveChangesAsync();
        return socio.Id;
    }

    /// <summary>Producto con base UND (y CJA × 12) y los grupos semilla BIENES / ITBIS18 / GENERAL salvo otro grupo de producto.</summary>
    private async Task<Guid> ProductoAsync(Guid? grupoProducto = null)
    {
        var id = await _prueba.SembrarProductoAsync(costoUnitario: 1m);
        await EjecutarSqlAsync(
            """
            UPDATE "Productos" SET "GrupoProductoId" = @Gp, "GrupoIvaProductoId" = @Gi, "GrupoInventarioId" = @Ginv, "PrecioVenta" = 10
            WHERE "Id" = @Id
            """,
            new { Id = id, Gp = grupoProducto ?? GrupoContableIds.ProductoBienes, Gi = GrupoContableIds.IvaProductoItbis18, Ginv = GrupoContableIds.InventarioGeneral });
        return id;
    }

    private async Task<Guid> GrupoAsync<T>() where T : GrupoContable, new()
    {
        await using var contexto = _prueba.NuevoContexto();
        var grupo = new T { Codigo = $"G{Guid.NewGuid():N}"[..12].ToUpperInvariant(), Descripcion = "Grupo de prueba", CreatedBy = "test" };
        contexto.Set<T>().Add(grupo);
        await contexto.SaveChangesAsync();
        return grupo.Id;
    }

    /// <summary>Grupo de producto con su setup general (para poder facturar); el test lo borra después.</summary>
    private async Task<Guid> GrupoProductoConSetupAsync()
    {
        var grupo = await GrupoAsync<GrupoProducto>();
        await using var contexto = _prueba.NuevoContexto();
        contexto.Set<SetupContableGeneral>().Add(new SetupContableGeneral
        {
            GrupoNegocioId = null,
            GrupoProductoId = grupo,
            CuentaVentasId = CuentaContableIds.Ventas,
            CuentaCostoVentasId = CuentaContableIds.CostoVentas,
            CuentaDescuentoVentasId = CuentaContableIds.DescuentoVentas,
            CuentaAjusteInventarioId = CuentaContableIds.AjusteInventario,
            CreatedBy = "test",
        });
        await contexto.SaveChangesAsync();
        return grupo;
    }

    private async Task<Guid> CuentaAsync(bool posteoDirecto = true)
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

    // ----- Helpers: facturas (motor real) -----

    private sealed record LineaFactura(
        TipoLineaFactura Tipo, Guid Referencia, decimal Cantidad, decimal Precio, decimal? Descuento, Guid? Unidad, Guid? GrupoIva)
    {
        public string? Texto { get; init; }
    }

    private static LineaFactura P(Guid producto, decimal cantidad, decimal precio, decimal? descuento = null, Guid? unidad = null) =>
        new(TipoLineaFactura.Producto, producto, cantidad, precio, descuento, unidad, null);

    private static LineaFactura C(Guid cuenta, decimal cantidad, decimal precio, Guid? grupoIva = null, decimal? descuento = null) =>
        new(TipoLineaFactura.CuentaContable, cuenta, cantidad, precio, descuento, null, grupoIva ?? GrupoContableIds.IvaProductoItbis18);

    private static LineaFactura Comentario(string texto) => new(TipoLineaFactura.Comentario, Guid.Empty, 0m, 0m, null, null, null) { Texto = texto };

    /// <summary>Factura del D10 creada y posteada con los handlers reales; devuelve su número.</summary>
    private async Task<string> FacturaAsync(Guid socio, Guid? almacen, params LineaFactura[] lineas)
    {
        Guid borradorId;
        await using (var scope = _prueba.Provider.CreateAsyncScope())
        {
            var handler = ActivatorUtilities.CreateInstance<CreateFacturaVentaBorradorCommandHandler>(scope.ServiceProvider);
            borradorId = Ok(await handler.Handle(new CreateFacturaVentaBorradorCommand(socio, null, D10, D10, null, null, null), default)).Id;
        }

        foreach (var l in lineas)
        {
            await using var scope = _prueba.Provider.CreateAsyncScope();
            var handler = ActivatorUtilities.CreateInstance<CreateLineaFacturaVentaBorradorCommandHandler>(scope.ServiceProvider);
            Ok(await handler.Handle(l.Tipo switch
            {
                TipoLineaFactura.Producto => new CreateLineaFacturaVentaBorradorCommand(
                    borradorId, l.Tipo, l.Referencia, null, null, almacen, l.Unidad, l.Cantidad, l.Precio, l.Descuento, null),
                TipoLineaFactura.CuentaContable => new CreateLineaFacturaVentaBorradorCommand(
                    borradorId, l.Tipo, null, l.Referencia, null, null, null, l.Cantidad, l.Precio, l.Descuento, l.GrupoIva),
                _ => new CreateLineaFacturaVentaBorradorCommand(borradorId, l.Tipo, null, null, l.Texto, null, null, null, null, null, null),
            }, default));
        }

        await using (var scope = _prueba.Provider.CreateAsyncScope())
        {
            var handler = ActivatorUtilities.CreateInstance<PostearFacturaVentaCommandHandler>(scope.ServiceProvider);
            return Ok(await handler.Handle(new PostearFacturaVentaCommand(borradorId), default)).Numero;
        }
    }

    /// <summary>Pago del socio por el importe (D11) y su aplicación al movimiento de la factura.</summary>
    private async Task PagarYAplicarAsync(Guid socio, long movimientoFactura, decimal importe)
    {
        long pago;
        await using (var scope = _prueba.Provider.CreateAsyncScope())
        {
            var handler = ActivatorUtilities.CreateInstance<RegistrarPagoClienteCommandHandler>(scope.ServiceProvider);
            pago = Ok(await handler.Handle(new RegistrarPagoClienteCommand(socio, importe, D11), default)).MovimientoClienteId;
        }

        await using (var scope = _prueba.Provider.CreateAsyncScope())
        {
            var handler = ActivatorUtilities.CreateInstance<AplicarPagoCommandHandler>(scope.ServiceProvider);
            Ok(await handler.Handle(new AplicarPagoCommand(movimientoFactura, pago, importe, D11), default));
        }
    }

    // ----- Helpers: notas (handlers reales, un scope por llamada) -----

    private async Task<Result<NotaCreditoVentaBorradorResponse>> CrearBorradorAsync(
        string factura, bool copiar = false, bool devolver = false, DateOnly? fecha = null)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<CreateNotaCreditoVentaBorradorCommandHandler>(scope.ServiceProvider);
        return await handler.Handle(new CreateNotaCreditoVentaBorradorCommand(factura, fecha ?? D12, null, null, copiar, devolver), default);
    }

    /// <summary>Borrador con una línea por la factura ENTERA aunque ya no quede pendiente (insertada por SQL).</summary>
    private async Task<Result<Guid>> CrearBorradorSinPendienteAsync(string factura)
    {
        await using var contexto = _prueba.NuevoContexto();
        var fila = await contexto.FacturasVenta.FindAsync(factura);
        var linea = contexto.LineasFacturaVenta.Single(l => l.FacturaVentaNumero == factura);
        var borrador = new NotaCreditoVentaBorrador
        {
            Numero = $"T{Guid.NewGuid():N}"[..20],
            SerieBorradorId = SerieNotaCreditoVentaIds.SerieBorradorId,
            SerieRegistroId = SerieNotaCreditoVentaIds.SeriePosteadaId,
            FacturaVentaNumero = factura,
            SocioNegocioId = fila!.SocioNegocioId,
            SocioNegocioFacturarAId = fila.SocioNegocioFacturarAId,
            NombreFacturacion = fila.NombreFacturacion,
            FechaRegistro = D12,
            FechaDocumento = D12,
            GrupoNegocioId = fila.GrupoNegocioId,
            GrupoIvaNegocioId = fila.GrupoIvaNegocioId,
            GrupoClienteContableId = fila.GrupoClienteContableId,
            CreatedBy = "test",
        };
        contexto.NotasCreditoVentaBorrador.Add(borrador);
        contexto.LineasNotaCreditoVentaBorrador.Add(new LineaNotaCreditoVentaBorrador
        {
            NotaCreditoVentaBorradorId = borrador.Id, LineaFacturaVentaId = linea.Id, NumeroLinea = linea.NumeroLinea, Tipo = linea.Tipo,
            ProductoId = linea.ProductoId, AlmacenId = linea.AlmacenId, UnidadMedidaId = linea.UnidadMedidaId,
            CantidadPorUnidadMedida = linea.CantidadPorUnidadMedida, Cantidad = linea.Cantidad, PrecioUnitario = linea.PrecioUnitario,
            ImporteLinea = linea.ImporteLinea, GrupoProductoId = linea.GrupoProductoId, GrupoIvaProductoId = linea.GrupoIvaProductoId,
            GrupoInventarioId = linea.GrupoInventarioId, IdentificadorIva = linea.IdentificadorIva, PorcentajeIva = linea.PorcentajeIva,
            CreatedBy = "test",
        });
        await contexto.SaveChangesAsync();
        return Result<Guid>.Exito(borrador.Id);
    }

    private async Task<Result<LineaNotaCreditoVentaBorradorResponse>> LineaAsync(Guid borradorId, long lineaFactura, decimal cantidad, bool devolver = false)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<CreateLineaNotaCreditoVentaBorradorCommandHandler>(scope.ServiceProvider);
        return await handler.Handle(new CreateLineaNotaCreditoVentaBorradorCommand(borradorId, lineaFactura, cantidad, devolver), default);
    }

    private async Task<Result<LineaNotaCreditoVentaBorradorResponse>> ModificarLineaAsync(Guid lineaId, decimal cantidad, bool devolver, long xmin)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<UpdateLineaNotaCreditoVentaBorradorCommandHandler>(scope.ServiceProvider);
        return await handler.Handle(new UpdateLineaNotaCreditoVentaBorradorCommand(lineaId, cantidad, devolver, xmin), default);
    }

    private async Task<Result> BorrarBorradorAsync(Guid borradorId)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<DeleteNotaCreditoVentaBorradorCommandHandler>(scope.ServiceProvider);
        return await handler.Handle(new DeleteNotaCreditoVentaBorradorCommand(borradorId), default);
    }

    private async Task<IReadOnlyList<LineaNotaCreditoVentaBorradorResponse>> LineasBorradorAsync(Guid borradorId)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<ListLineasNotaCreditoVentaBorradorQueryHandler>(scope.ServiceProvider);
        return Ok(await handler.Handle(new(borradorId), default));
    }

    private async Task<IReadOnlyList<LineaFacturaAcreditableResponse>> AcreditablesAsync(Guid borradorId)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<ListLineasAcreditablesNotaCreditoVentaQueryHandler>(scope.ServiceProvider);
        return Ok(await handler.Handle(new(borradorId), default));
    }

    private async Task<object> TotalesAsync(Guid borradorId)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<GetTotalesNotaCreditoVentaBorradorQueryHandler>(scope.ServiceProvider);
        return Ok(await handler.Handle(new(borradorId), default));
    }

    private Task<Result<ResultadoPosteoNotaCredito>> PostearAsync(Result<Guid> borrador) => PostearAsync(Ok(borrador));

    private async Task<Result<ResultadoPosteoNotaCredito>> PostearAsync(Guid borradorId)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<PostearNotaCreditoVentaCommandHandler>(scope.ServiceProvider);
        return await handler.Handle(new PostearNotaCreditoVentaCommand(borradorId), default);
    }

    private async Task<Result<ResultadoPosteoNotaCredito>> PostearComoAsync(Guid borradorId, Guid? usuario)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<PostearNotaCreditoVentaCommandHandler>(
            scope.ServiceProvider, FechasRegistroPrueba.Validador(scope.ServiceProvider, usuario));
        return await handler.Handle(new PostearNotaCreditoVentaCommand(borradorId), default);
    }

    private async Task<ResultadoPosteoCostoInventario> PostearCostoAsync(Guid productoId)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPosteoCostoInventario>().PostearAsync(productoId);
    }

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

    private static (decimal Total, decimal Aplicado) Resumen(ResultadoPosteoNotaCredito r) => (r.ImporteTotal, r.ImporteAplicado);

    private static T Ok<T>(Result<T> resultado)
    {
        Assert.True(resultado.EsExito, resultado.EsFallo
            ? string.Join("; ", resultado.Errores.Select(e => $"{e.Codigo} [{e.Campo}]: {e.Mensaje}"))
            : string.Empty);
        return resultado.Valor;
    }

    private static Result Ok(Result resultado)
    {
        Assert.True(resultado.EsExito, string.Join("; ", resultado.Errores.Select(e => $"{e.Codigo}: {e.Mensaje}")));
        return resultado;
    }

    private static (string Codigo, string? Campo) Unico(Result resultado)
    {
        Assert.True(resultado.EsFallo, "Se esperaba un fallo.");
        var error = Assert.Single(resultado.Errores);
        return (error.Codigo, error.Campo);
    }

    private static async Task<List<(Result<ResultadoPosteoNotaCredito>? Resultado, Exception? Excepcion)>> EnParaleloAsync(
        params Func<Task<Result<ResultadoPosteoNotaCredito>>>[] acciones)
    {
        var barrera = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listas = 0;
        var resultados = new ConcurrentBag<(Result<ResultadoPosteoNotaCredito>?, Exception?)>();

        async Task EjecutarAsync(Func<Task<Result<ResultadoPosteoNotaCredito>>> accion)
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

    // ----- Helpers: lectura y foto -----

    private static string Siguiente(string ultimo) => (long.Parse(ultimo) + 1).ToString().PadLeft(8, '0');

    private Task<string> UltimoNumeroAsync(Guid lineaSerie) =>
        EscalarAsync<string>("""SELECT "UltimoNumeroUsado" FROM "LineasSerie" WHERE "Id" = @Id""", new { Id = lineaSerie });

    private static readonly string[] TablasEscritas =
    [
        "NotasCreditoVenta", "LineasNotaCreditoVenta", "LineasIvaNotaCreditoVenta", "MovimientosCliente", "MovimientosClienteDetalle",
        "RegistrosContables", "MovimientosContables", "MovimientosProducto", "MovimientosValor", "AplicacionesMovimientoProducto",
    ];

    private static readonly string[] Secuencias =
    [
        "LineasNotaCreditoVenta", "LineasIvaNotaCreditoVenta", "MovimientosCliente", "MovimientosClienteDetalle", "MovimientosProducto",
        "MovimientosValor", "MovimientosContables",
    ];

    private async Task<Dictionary<string, string?>> FotoFilasAsync()
    {
        await using var conexion = _prueba.NuevaConexion();
        var foto = new Dictionary<string, string?>();
        foreach (var tabla in TablasEscritas)
        {
            foto[tabla] = (await conexion.ExecuteScalarAsync<long>($"""SELECT COUNT(*) FROM "{tabla}" """)).ToString();
        }

        // Borradores y sus líneas: cualquier escritura (aunque no cambie el número de filas) mueve el xmin de la fila.
        foto["borradores NC"] = await conexion.ExecuteScalarAsync<string>(
            """SELECT md5(COALESCE(string_agg(b::text || '/' || b.xmin::text, '|' ORDER BY b."Id"), '')) FROM "NotasCreditoVentaBorrador" b""");
        foto["lineas borrador NC"] = await conexion.ExecuteScalarAsync<string>(
            """SELECT md5(COALESCE(string_agg(l::text || '/' || l.xmin::text, '|' ORDER BY l."Id"), '')) FROM "LineasNotaCreditoVentaBorrador" l""");
        foto["serie NC"] = await conexion.ExecuteScalarAsync<string>(
            """SELECT "UltimoNumeroUsado" FROM "LineasSerie" WHERE "Id" = @Id""", new { Id = SerieNotaCreditoVentaIds.LineaSeriePosteadaId });
        foto["serie CONTAB"] = await conexion.ExecuteScalarAsync<string>(
            """SELECT l."UltimoNumeroUsado" FROM "LineasSerie" l JOIN "Series" s ON s."Id" = l."SerieId" WHERE s."Codigo" = 'CONTAB'""");
        return foto;
    }

    /// <summary>Filas, números de serie y secuencias de identidad (no transaccionales: un INSERT intentado las movería).</summary>
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

    private async Task<(EstadoNotaCreditoBorrador Estado, string? Nota)> EstadoYNotaAsync(Guid borradorId)
    {
        await using var conexion = _prueba.NuevaConexion();
        var fila = await conexion.QuerySingleAsync<(short Estado, string? Nota)>(
            """SELECT "Estado", "NotaCreditoVentaNumero" FROM "NotasCreditoVentaBorrador" WHERE "Id" = @Id""", new { Id = borradorId });
        return ((EstadoNotaCreditoBorrador)fila.Estado, fila.Nota);
    }

    private async Task<Result<NotaCreditoVentaBorradorResponse>> CrearBorradorConSeriesAsync(
        string factura, Guid? serieBorrador, Guid? serieRegistro, bool administrador = true)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<CreateNotaCreditoVentaBorradorCommandHandler>(
            scope.ServiceProvider, (IUsuarioActual)new FechasRegistroPrueba.Usuario(null, administrador));
        return await handler.Handle(
            new CreateNotaCreditoVentaBorradorCommand(factura, D12, null, null, true, false, serieBorrador, serieRegistro), default);
    }

    private async Task<Result<NotaCreditoVentaBorradorResponse>> ModificarCabeceraAsync(
        Guid borradorId, long xmin, Guid? serieRegistro = null, bool administrador = true)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<UpdateNotaCreditoVentaBorradorCommandHandler>(
            scope.ServiceProvider, (IUsuarioActual)new FechasRegistroPrueba.Usuario(null, administrador));
        return await handler.Handle(new UpdateNotaCreditoVentaBorradorCommand(borradorId, null, null, "x", xmin, serieRegistro), default);
    }

    private async Task<PagedResult<NotaCreditoVentaBorradorResponse>> ListarBorradoresAsync(string factura, EstadoNotaCreditoBorrador? estado)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var lectura = scope.ServiceProvider.GetRequiredService<INotaCreditoVentaBorradorReadRepository>();
        return await lectura.ListAsync(new NotaCreditoVentaBorradorSearchCriteria(FacturaVentaNumero: factura, Estado: estado),
            new PageRequest(1, 50, "Numero", Descendente: false), default);
    }

    private async Task<(bool Borrado, long LineasVivas, long LineasBorradas)> EstadoBorradorAsync(Guid borradorId)
    {
        await using var conexion = _prueba.NuevaConexion();
        return await conexion.QuerySingleAsync<(bool, long, long)>(
            """
            SELECT b."IsDeleted",
                   (SELECT COUNT(*) FROM "LineasNotaCreditoVentaBorrador" l WHERE l."NotaCreditoVentaBorradorId" = b."Id" AND NOT l."IsDeleted"),
                   (SELECT COUNT(*) FROM "LineasNotaCreditoVentaBorrador" l WHERE l."NotaCreditoVentaBorradorId" = b."Id" AND l."IsDeleted")
            FROM "NotasCreditoVentaBorrador" b WHERE b."Id" = @Id
            """,
            new { Id = borradorId });
    }

    private async Task<List<LineaFacturaVenta>> LineasFacturaAsync(string numero)
    {
        await using var conexion = _prueba.NuevaConexion();
        return [.. await conexion.QueryAsync<LineaFacturaVenta>(
            """SELECT * FROM "LineasFacturaVenta" WHERE "FacturaVentaNumero" = @N ORDER BY "NumeroLinea" """, new { N = numero })];
    }

    private Task<decimal> AcreditadoAsync(long lineaFactura) => EscalarAsync<decimal>(
        """SELECT COALESCE(SUM("Cantidad"), 0) FROM "LineasNotaCreditoVenta" WHERE "LineaFacturaVentaId" = @L""", new { L = lineaFactura });

    private async Task<NotaCreditoVenta> NotaAsync(string numero)
    {
        await using var conexion = _prueba.NuevaConexion();
        return await conexion.QuerySingleAsync<NotaCreditoVenta>("""SELECT * FROM "NotasCreditoVenta" WHERE "Numero" = @N""", new { N = numero });
    }

    private async Task<List<LineaNotaCreditoVenta>> LineasNotaAsync(string numero)
    {
        await using var conexion = _prueba.NuevaConexion();
        return [.. await conexion.QueryAsync<LineaNotaCreditoVenta>(
            """SELECT * FROM "LineasNotaCreditoVenta" WHERE "NotaCreditoVentaNumero" = @N ORDER BY "NumeroLinea" """, new { N = numero })];
    }

    private async Task<List<LineaIvaNotaCreditoVenta>> LineasIvaNotaAsync(string numero)
    {
        await using var conexion = _prueba.NuevaConexion();
        return [.. await conexion.QueryAsync<LineaIvaNotaCreditoVenta>(
            """SELECT * FROM "LineasIvaNotaCreditoVenta" WHERE "NotaCreditoVentaNumero" = @N ORDER BY "IdentificadorIva" """, new { N = numero })];
    }

    private async Task<List<MovimientoContable>> AsientoAsync(long registroId)
    {
        await using var conexion = _prueba.NuevaConexion();
        return [.. await conexion.QueryAsync<MovimientoContable>(
            """SELECT * FROM "MovimientosContables" WHERE "RegistroContableId" = @Id ORDER BY "Id" """, new { Id = registroId })];
    }

    private async Task<List<OpenSource1.Core.Entities.Clientes.MovimientoCliente>> MovimientosClienteAsync(TipoDocumentoCliente tipo, string numero)
    {
        await using var conexion = _prueba.NuevaConexion();
        return [.. await conexion.QueryAsync<OpenSource1.Core.Entities.Clientes.MovimientoCliente>(
            """SELECT * FROM "MovimientosCliente" WHERE "TipoDocumento" = @T AND "NumeroDocumento" = @N""", new { T = (short)tipo, N = numero })];
    }

    private async Task<List<(TipoDetalleCliente, decimal)>> DetalleClienteAsync(long movimientoId)
    {
        await using var conexion = _prueba.NuevaConexion();
        return [.. await conexion.QueryAsync<(TipoDetalleCliente, decimal)>(
            """SELECT "TipoMovimiento", "Importe" FROM "MovimientosClienteDetalle" WHERE "MovimientoClienteId" = @Id ORDER BY "Id" """,
            new { Id = movimientoId })];
    }

    private Task<decimal> RestanteAsync(long movimientoId) => EscalarAsync<decimal>(
        """SELECT COALESCE(SUM("Importe"), 0) FROM "MovimientosClienteDetalle" WHERE "MovimientoClienteId" = @Id""", new { Id = movimientoId });

    /// <summary>Σ ImporteCosto CostoDirecto (original + ajustes) de un movimiento de producto.</summary>
    private Task<decimal> CostoSalidaAsync(long movimientoProductoId) => EscalarAsync<decimal>(
        """SELECT SUM("ImporteCosto") FROM "MovimientosValor" WHERE "MovimientoProductoId" = @Id AND "TipoValor" = 1""", new { Id = movimientoProductoId });

    private async Task<List<MovimientoNotaFila>> MovimientosInventarioNotaAsync(string numero)
    {
        await using var conexion = _prueba.NuevaConexion();
        return [.. await conexion.QueryAsync<MovimientoNotaFila>(
            """
            SELECT m."Id", m."TipoMovimiento", m."TipoDocumento", m."TipoOrigen", m."Cantidad", m."CantidadRestante", m."AlmacenId",
                   m."SocioNegocioId", m."UnidadMedidaId", v."ImporteCosto", v."CostoPorUnidad"
            FROM "MovimientosProducto" m JOIN "MovimientosValor" v ON v."MovimientoProductoId" = m."Id"
            WHERE m."TipoOrigen" = 6 AND m."NumeroDocumento" = @N
            ORDER BY m."Id"
            """,
            new { N = numero })];
    }

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

    private sealed class MovimientoNotaFila
    {
        public long Id { get; init; }
        public TipoMovimientoInventario TipoMovimiento { get; init; }
        public TipoDocumentoInventario TipoDocumento { get; init; }
        public TipoOrigenMovimiento TipoOrigen { get; init; }
        public decimal Cantidad { get; init; }
        public decimal? CantidadRestante { get; init; }
        public Guid AlmacenId { get; init; }
        public Guid? SocioNegocioId { get; init; }
        public Guid UnidadMedidaId { get; init; }
        public decimal ImporteCosto { get; init; }
        public decimal CostoPorUnidad { get; init; }
    }
}
