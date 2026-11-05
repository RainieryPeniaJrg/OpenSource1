using System.Collections.Concurrent;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using OpenSource1.Application.Data.UnitOfWork;
using OpenSource1.Application.Features.Cobros;
using OpenSource1.Application.Features.FacturasVenta.Borradores.Commands;
using OpenSource1.Application.Features.FacturasVenta.Borradores.Handlers;
using OpenSource1.Application.Features.FacturasVenta.Posteo;
using OpenSource1.Application.Features.MovimientosCliente.Dtos;
using OpenSource1.Application.Features.MovimientosCliente.Handlers;
using OpenSource1.Application.Features.MovimientosCliente.Queries;
using OpenSource1.Core.Common;
using OpenSource1.Core.Entities;
using OpenSource1.Core.Entities.Clientes;
using OpenSource1.Core.Entities.Contabilidad;
using OpenSource1.Core.Enums;
using OpenSource1.SmokeTests.TestInfrastructure;

namespace OpenSource1.SmokeTests.Features.Cobros;

/// <summary>
/// Cobros de clientes (Task 6.5) contra Postgres real: el pago (movimiento Pago con su detalle, número de la serie <c>COBRO</c> y
/// asiento débito caja / crédito CxC) y la aplicación de un pago a una factura (dos filas de detalle Aplicación que se apuntan
/// mutuamente, sin asiento), con el saldo derivado del detalle (Review Focus 4). Cada fallo se comprueba con la "foto" de nada
/// escrito: filas de los libros de clientes y contable, números de las series COBRO y CONTAB y el último valor de las secuencias
/// de identidad (un INSERT intentado y deshecho las movería). REQUIERE DOCKER. Los números de serie se comprueban RELATIVOS al
/// último usado (la base de datos se comparte).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CobrosTests(PostgresTestFixture fixture) : IClassFixture<PostgresTestFixture>, IAsyncLifetime
{
    private static readonly DateOnly D10 = new(2026, 9, 10);
    private static readonly DateOnly D12 = new(2026, 9, 12);
    private static readonly DateOnly D15 = new(2026, 9, 15);

    private readonly LibroInventarioPrueba _prueba = new(fixture);

    public Task InitializeAsync() => _prueba.InicializarAsync();

    public async Task DisposeAsync() => await _prueba.DisposeAsync();

    // ----- Review Focus 4: saldo tras factura + pago parcial + aplicación -----

    [Fact]
    public async Task ReviewFocus4_Factura118_Pago50_Aplicacion50_Restantes68Y0_Saldo68_YAplicarDeMas400SinEscribir()
    {
        var socio = await SocioAsync();
        var factura = await FacturaPosteadaAsync(socio, 100m); // 100 + 18 % = 118
        var pago = await PagarOkAsync(new RegistrarPagoClienteCommand(socio, 50m, D12));
        var pagoId = pago.MovimientoClienteId;
        Assert.Equal((118m, -50m), (await RestanteAsync(factura), await RestanteAsync(pagoId)));
        Assert.Equal(68m, await SaldoAsync(socio));

        // Aplicar 60 de un pago de 50 -> 400, sin escribir nada.
        var antes = await FotoAsync();
        var excede = await AplicarAsync(new AplicarPagoCommand(factura, pagoId, 60m, D15));
        Assert.Equal(("clientes.importe_excede_restante", "Importe"), Unico(excede));
        Assert.Contains("50", excede.Errores[0].Mensaje);
        Assert.Equal(antes, await FotoAsync());

        var aplicacion = Ok(await AplicarAsync(new AplicarPagoCommand(factura, pagoId, 50m, D15)));

        Assert.Equal((factura, pagoId, 50m, 68m, 0m),
            (aplicacion.MovimientoFacturaId, aplicacion.MovimientoPagoId, aplicacion.Importe, aplicacion.RestanteFactura, aplicacion.RestantePago));
        Assert.Equal((68m, 0m), (await RestanteAsync(factura), await RestanteAsync(pagoId)));

        // Dos filas Aplicación de signo opuesto que se apuntan mutuamente; ningún asiento nuevo.
        Assert.Equal(
            [(TipoDetalleCliente.ImporteInicial, 118m, (long?)null, D10), (TipoDetalleCliente.Aplicacion, -50m, pagoId, D15)],
            await DetalleAsync(factura));
        Assert.Equal(
            [(TipoDetalleCliente.Pago, -50m, (long?)null, D12), (TipoDetalleCliente.Aplicacion, 50m, factura, D15)],
            await DetalleAsync(pagoId));
        Assert.Equal(0L, await EscalarAsync<long>(
            """SELECT COUNT(*) FROM "MovimientosContables" WHERE "FechaRegistro" = @F AND "SocioNegocioId" = @S""", new { F = D15, S = socio }));

        // Saldo derivado = suma del detalle (la consulta y el SQL directo coinciden) y solo la factura sigue abierta.
        Assert.Equal(68m, await SaldoAsync(socio));
        Assert.Equal(68m, await EscalarAsync<decimal>(
            """
            SELECT SUM(d."Importe") FROM "MovimientosClienteDetalle" d
            JOIN "MovimientosCliente" m ON m."Id" = d."MovimientoClienteId" WHERE m."SocioNegocioId" = @S
            """,
            new { S = socio }));
        var saldo = Ok(await ConsultarAsync<GetSaldoClienteQueryHandler, GetSaldoClienteQuery, SaldoClienteResponse>(new GetSaldoClienteQuery(socio)));
        Assert.Equal((68m, 1), (saldo.Saldo, saldo.MovimientosAbiertos));
        var abiertos = Ok(await ConsultarAsync<ListMovimientosAbiertosClienteQueryHandler, ListMovimientosAbiertosClienteQuery,
            IReadOnlyList<MovimientoClienteResponse>>(new ListMovimientosAbiertosClienteQuery(socio)));
        var abierto = Assert.Single(abiertos);
        Assert.Equal((factura, 118m, 68m, true), (abierto.Id, abierto.ImporteOriginal, abierto.ImporteRestante, abierto.Abierta));

        // El pago ya no tiene restante: cualquier aplicación más -> 400, sin escribir.
        antes = await FotoAsync();
        Assert.Equal(("clientes.movimiento_sin_restante", "MovimientoPagoId"), Unico(await AplicarAsync(new AplicarPagoCommand(factura, pagoId, 1m))));
        Assert.Equal(antes, await FotoAsync());
    }

    // ----- Task 8.5: fechas de registro permitidas (Review Focus 4 de la Fase 8) -----

    [Theory]
    [InlineData(9)]
    [InlineData(11)]
    public async Task AplicarAntesDeFacturaOPago_RechazaSinCambiarLibros(int dia)
    {
        var socio = await SocioAsync();
        var factura = await FacturaPosteadaAsync(socio, 100m);
        var pago = await PagarOkAsync(new RegistrarPagoClienteCommand(socio, 50m, D12));
        var antes = await FotoAsync();

        var resultado = await AplicarAsync(new AplicarPagoCommand(factura, pago.MovimientoClienteId, 50m, new(2026, 9, dia)));

        Assert.Equal(("cobro.fecha_anterior_documentos", "FechaRegistro"), Unico(resultado));
        Assert.Equal(antes, await FotoAsync());
        Assert.Equal(118m, await RestanteAsync(factura));
        Assert.Equal(-50m, await RestanteAsync(pago.MovimientoClienteId));
    }

    [Fact]
    public async Task AplicarConCambioDeGrupo_ReclasificaSoloImporteAplicadoEntreCuentasCongeladas()
    {
        var socio = await SocioAsync();
        var factura = await FacturaPosteadaAsync(socio, 100m);
        var cuentaFactura = await EscalarAsync<Guid>("""SELECT "CuentaCxCId" FROM "MovimientosCliente" WHERE "Id" = @Id""", new { Id = factura });
        var cuentaPago = await CuentaAsync(false);
        var grupo = await GrupoClienteAsync(cuentaPago);
        await EjecutarSqlAsync("""UPDATE "SociosNegocio" SET "GrupoClienteContableId" = @Grupo WHERE "Id" = @Id""", new { Grupo = grupo, Id = socio });
        var pago = await PagarOkAsync(new RegistrarPagoClienteCommand(socio, 50m, D12));

        Ok(await AplicarAsync(new AplicarPagoCommand(factura, pago.MovimientoClienteId, 30m, D15)));

        Assert.Equal(88m, await RestanteAsync(factura));
        Assert.Equal(-20m, await RestanteAsync(pago.MovimientoClienteId));
        Assert.Equal(-30m, await EscalarAsync<decimal>("""SELECT SUM("Importe") FROM "MovimientosContables" WHERE "SocioNegocioId" = @Socio AND "FechaRegistro" = @Fecha AND "CuentaContableId" = @Cuenta""", new { Socio = socio, Fecha = D15, Cuenta = cuentaFactura }));
        Assert.Equal(30m, await EscalarAsync<decimal>("""SELECT SUM("Importe") FROM "MovimientosContables" WHERE "SocioNegocioId" = @Socio AND "FechaRegistro" = @Fecha AND "CuentaContableId" = @Cuenta""", new { Socio = socio, Fecha = D15, Cuenta = cuentaPago }));
        Assert.Equal(0m, await EscalarAsync<decimal>("""SELECT SUM("Importe") FROM "MovimientosContables" WHERE "SocioNegocioId" = @Socio AND "FechaRegistro" = @Fecha""", new { Socio = socio, Fecha = D15 }));

        // A second partial application of the same pair adds only its own reclassification.
        Ok(await AplicarAsync(new AplicarPagoCommand(factura, pago.MovimientoClienteId, 20m, D15)));
        Assert.Equal(68m, await RestanteAsync(factura));
        Assert.Equal(0m, await RestanteAsync(pago.MovimientoClienteId));
        Assert.Equal(-50m, await EscalarAsync<decimal>("""SELECT SUM("Importe") FROM "MovimientosContables" WHERE "SocioNegocioId" = @Socio AND "FechaRegistro" = @Fecha AND "CuentaContableId" = @Cuenta""", new { Socio = socio, Fecha = D15, Cuenta = cuentaFactura }));
        Assert.Equal(50m, await EscalarAsync<decimal>("""SELECT SUM("Importe") FROM "MovimientosContables" WHERE "SocioNegocioId" = @Socio AND "FechaRegistro" = @Fecha AND "CuentaContableId" = @Cuenta""", new { Socio = socio, Fecha = D15, Cuenta = cuentaPago }));
    }

    [Fact]
    public async Task AplicarEnFechaDelPago_AdmiteLimiteDeFechaInclusivo()
    {
        var socio = await SocioAsync();
        var factura = await FacturaPosteadaAsync(socio, 100m);
        var pago = await PagarOkAsync(new RegistrarPagoClienteCommand(socio, 50m, D12));

        var resultado = Ok(await AplicarAsync(new AplicarPagoCommand(factura, pago.MovimientoClienteId, 50m, D12)));

        Assert.Equal((68m, 0m), (resultado.RestanteFactura, resultado.RestantePago));
    }

    [Fact]
    public async Task ReclasificacionConCuentaBloqueada_RevierteAplicacionCompleta()
    {
        var socio = await SocioAsync();
        var factura = await FacturaPosteadaAsync(socio, 100m);
        var cuentaPago = await CuentaAsync(false);
        var grupo = await GrupoClienteAsync(cuentaPago);
        await EjecutarSqlAsync("""UPDATE "SociosNegocio" SET "GrupoClienteContableId" = @Grupo WHERE "Id" = @Id""", new { Grupo = grupo, Id = socio });
        var pago = await PagarOkAsync(new RegistrarPagoClienteCommand(socio, 50m, D12));
        await EjecutarSqlAsync("""UPDATE "CuentasContables" SET "Bloqueada" = true WHERE "Id" = @Id""", new { Id = cuentaPago });
        var antes = await FotoAsync();

        var resultado = await AplicarAsync(new AplicarPagoCommand(factura, pago.MovimientoClienteId, 30m, D15));

        Assert.True(resultado.EsFallo);
        Assert.Equal(antes, await FotoAsync());
        Assert.Equal(118m, await RestanteAsync(factura));
        Assert.Equal(-50m, await RestanteAsync(pago.MovimientoClienteId));
    }

    [Fact]
    public async Task ReviewFocus4_Pago_FechaFueraDelRangoPermitido_400EnFechaRegistro_SinEscribir_YExcepcionesDeUsuario()
    {
        var fechas = new FechasRegistroPrueba(_prueba);
        var socio = await SocioAsync();
        var amplio = Guid.NewGuid();
        var estrecho = Guid.NewGuid();
        var otro = Guid.NewGuid();
        try
        {
            // Sin rango: cualquier fecha.
            Ok(await PagarAsync(new RegistrarPagoClienteCommand(socio, 1m, D15), otro));

            // General 10/09-12/09: límites inclusivos dentro; el 15/09 falla (sistema o usuario sin fila) sin escribir.
            await fechas.GeneralAsync(D10, D12);
            Ok(await PagarAsync(new RegistrarPagoClienteCommand(socio, 1m, D10), null));
            Ok(await PagarAsync(new RegistrarPagoClienteCommand(socio, 1m, D12), otro));
            var antes = await FotoAsync();
            foreach (var usuario in new Guid?[] { null, otro })
            {
                var fallo = await PagarAsync(new RegistrarPagoClienteCommand(socio, 1m, D15), usuario);
                Assert.Equal(("registro.fecha_no_permitida", "FechaRegistro"), Unico(fallo));
                Assert.Contains("15/09/2026", fallo.Errores[0].Mensaje);
                Assert.Contains("general", fallo.Errores[0].Mensaje);
                Assert.Contains("del 10/09/2026 al 12/09/2026", fallo.Errores[0].Mensaje);
            }

            Assert.Equal(antes, await FotoAsync());

            // Excepción más amplia: el usuario registra el 15/09.
            await fechas.UsuarioAsync(amplio, D10, null);
            Ok(await PagarAsync(new RegistrarPagoClienteCommand(socio, 1m, D15), amplio));

            // Excepción más estrecha: el 10/09 (permitido en general) le falla al usuario con SU rango; al sistema no.
            await fechas.UsuarioAsync(estrecho, D12, D12);
            antes = await FotoAsync();
            var falloEstrecho = await PagarAsync(new RegistrarPagoClienteCommand(socio, 1m, D10), estrecho);
            Assert.Equal(("registro.fecha_no_permitida", "FechaRegistro"), Unico(falloEstrecho));
            Assert.Contains("usuario", falloEstrecho.Errores[0].Mensaje);
            Assert.Contains("del 12/09/2026 al 12/09/2026", falloEstrecho.Errores[0].Mensaje);
            Assert.Equal(antes, await FotoAsync());
            Ok(await PagarAsync(new RegistrarPagoClienteCommand(socio, 1m, D10), null));
            Ok(await PagarAsync(new RegistrarPagoClienteCommand(socio, 1m, D12), estrecho));
        }
        finally
        {
            await fechas.LimpiarAsync();
        }
    }

    [Fact]
    public async Task ReviewFocus4_Aplicacion_FechaFueraDelRangoPermitido_400EnFechaRegistro_SinEscribir_YExcepcionesDeUsuario()
    {
        var fechas = new FechasRegistroPrueba(_prueba);
        var socio = await SocioAsync();
        var factura = await FacturaPosteadaAsync(socio, 100m);
        var pago = (await PagarOkAsync(new RegistrarPagoClienteCommand(socio, 100m, D12))).MovimientoClienteId;
        var amplio = Guid.NewGuid();
        var estrecho = Guid.NewGuid();
        try
        {
            // Sin rango.
            Ok(await AplicarAsync(new AplicarPagoCommand(factura, pago, 1m, D15), null));

            // General 10/09-12/09: el 15/09 y la fecha por defecto (hoy, posterior) fallan sin escribir; el 12/09 no.
            await fechas.GeneralAsync(D10, D12);
            var antes = await FotoAsync();
            var fallo = await AplicarAsync(new AplicarPagoCommand(factura, pago, 1m, D15), null);
            Assert.Equal(("registro.fecha_no_permitida", "FechaRegistro"), Unico(fallo));
            Assert.Contains("general", fallo.Errores[0].Mensaje);
            Assert.Equal(("registro.fecha_no_permitida", "FechaRegistro"), Unico(await AplicarAsync(new AplicarPagoCommand(factura, pago, 1m), null)));
            Assert.Equal(antes, await FotoAsync());
            Ok(await AplicarAsync(new AplicarPagoCommand(factura, pago, 1m, D12), null));

            // Excepción más amplia (sin "hasta"): el usuario aplica el 15/09 y con la fecha por defecto.
            await fechas.UsuarioAsync(amplio, D10, null);
            Ok(await AplicarAsync(new AplicarPagoCommand(factura, pago, 1m, D15), amplio));
            Ok(await AplicarAsync(new AplicarPagoCommand(factura, pago, 1m), amplio));

            // Original: range 11/09-11/09 accepted an application before the payment dated 12/09.
            // The user's narrow range still applies, subject to the document chronology invariant.
            // await fechas.UsuarioAsync(estrecho, new DateOnly(2026, 9, 11), new DateOnly(2026, 9, 11));
            await fechas.UsuarioAsync(estrecho, D12, D12);
            antes = await FotoAsync();
            // Original: var falloEstrecho = await AplicarAsync(new AplicarPagoCommand(factura, pago, 1m, D12), estrecho);
            var falloEstrecho = await AplicarAsync(new AplicarPagoCommand(factura, pago, 1m, D15), estrecho);
            Assert.Equal(("registro.fecha_no_permitida", "FechaRegistro"), Unico(falloEstrecho));
            Assert.Contains("usuario", falloEstrecho.Errores[0].Mensaje);
            Assert.Equal(antes, await FotoAsync());
            // Original: Ok(await AplicarAsync(new AplicarPagoCommand(factura, pago, 1m, new DateOnly(2026, 9, 11)), estrecho));
            Ok(await AplicarAsync(new AplicarPagoCommand(factura, pago, 1m, D12), estrecho));

            Assert.Equal(118m - 5m, await RestanteAsync(factura));
        }
        finally
        {
            await fechas.LimpiarAsync();
        }
    }

    [Fact]
    public async Task Aplicacion_ExcedeElRestanteDeLaFactura_400_YAplicarElRestanteExactoCierraLaFactura()
    {
        var socio = await SocioAsync();
        var factura = await FacturaPosteadaAsync(socio, 100m);
        var pago = (await PagarOkAsync(new RegistrarPagoClienteCommand(socio, 200m, D12))).MovimientoClienteId;

        var antes = await FotoAsync();
        var excede = await AplicarAsync(new AplicarPagoCommand(factura, pago, 150m));
        Assert.Equal(("clientes.importe_excede_restante", "Importe"), Unico(excede));
        Assert.Contains("118", excede.Errores[0].Mensaje);
        Assert.Equal(antes, await FotoAsync());

        var aplicacion = Ok(await AplicarAsync(new AplicarPagoCommand(factura, pago, 118m)));

        Assert.Equal((0m, -82m), (aplicacion.RestanteFactura, aplicacion.RestantePago));
        Assert.Equal(-82m, await SaldoAsync(socio));
        var abierto = Assert.Single(Ok(await ConsultarAsync<ListMovimientosAbiertosClienteQueryHandler, ListMovimientosAbiertosClienteQuery,
            IReadOnlyList<MovimientoClienteResponse>>(new ListMovimientosAbiertosClienteQuery(socio))));
        Assert.Equal((pago, TipoDocumentoCliente.Pago, -82m), (abierto.Id, abierto.TipoDocumento, abierto.ImporteRestante));

        // Un pago parcial a una segunda factura desde el resto del pago.
        var factura2 = await FacturaPosteadaAsync(socio, 50m); // 59
        var parcial = Ok(await AplicarAsync(new AplicarPagoCommand(factura2, pago, 20.5m)));
        Assert.Equal((38.5m, -61.5m), (parcial.RestanteFactura, parcial.RestantePago));
        Assert.Equal(-23m, await SaldoAsync(socio));
    }

    [Fact]
    public async Task ReviewFocus4_AplicarEntreSociosDistintos_400_SinEscribir()
    {
        var socioA = await SocioAsync();
        var socioB = await SocioAsync();
        var factura = await FacturaPosteadaAsync(socioA, 100m);
        var pagoB = (await PagarOkAsync(new RegistrarPagoClienteCommand(socioB, 50m, D12))).MovimientoClienteId;
        var antes = await FotoAsync();

        var resultado = await AplicarAsync(new AplicarPagoCommand(factura, pagoB, 10m));

        Assert.Equal(("clientes.socios_distintos", "MovimientoPagoId"), Unico(resultado));
        Assert.Equal(antes, await FotoAsync());
        Assert.Equal((118m, -50m), (await SaldoAsync(socioA), await SaldoAsync(socioB)));
    }

    [Fact]
    public async Task Aplicacion_Invalida_400ConElCampo_YNadaEscrito()
    {
        var socio = await SocioAsync();
        var factura = await FacturaPosteadaAsync(socio, 100m);
        var pago = (await PagarOkAsync(new RegistrarPagoClienteCommand(socio, 50m, D12))).MovimientoClienteId;
        var factura2 = await FacturaPosteadaAsync(socio, 10m);
        var antes = await FotoAsync();

        // Signos invertidos: la "factura" es el pago (restante < 0) y el "pago" es la factura (restante > 0).
        var invertido = await AplicarAsync(new AplicarPagoCommand(pago, factura, 10m));
        Assert.Equal(
            [("clientes.movimiento_sin_restante", "MovimientoFacturaId"), ("clientes.movimiento_sin_restante", "MovimientoPagoId")],
            invertido.Errores.Select(e => (e.Codigo, e.Campo)));
        // Dos facturas: el "pago" tiene restante positivo.
        Assert.Equal(("clientes.movimiento_sin_restante", "MovimientoPagoId"), Unico(await AplicarAsync(new AplicarPagoCommand(factura, factura2, 10m))));
        // El mismo movimiento en los dos lados.
        Assert.Equal(("cobro.aplicacion_invalida", "MovimientoPagoId"), Unico(await AplicarAsync(new AplicarPagoCommand(factura, factura, 10m))));
        // Movimientos inexistentes: referencia inválida en el cuerpo -> 400, no 404.
        var inexistente = await AplicarAsync(new AplicarPagoCommand(long.MaxValue, long.MaxValue - 1, 10m));
        Assert.Equal(
            [("cobro.movimiento_invalido", "MovimientoFacturaId"), ("cobro.movimiento_invalido", "MovimientoPagoId")],
            inexistente.Errores.Select(e => (e.Codigo, e.Campo)));
        Assert.Equal(("cobro.movimiento_invalido", "MovimientoPagoId"), Unico(await AplicarAsync(new AplicarPagoCommand(factura, 0, 10m))));
        // Importes.
        foreach (var importe in new[] { 0m, -5m, 1.005m, 100_000_000_000_000m })
        {
            Assert.Equal(("cobro.importe_invalido", "Importe"), Unico(await AplicarAsync(new AplicarPagoCommand(factura, pago, importe))));
        }

        Assert.Equal(antes, await FotoAsync());
        Assert.Equal(118m + 11.8m - 50m, await SaldoAsync(socio));
    }

    [Fact]
    public async Task Aplicacion_SocioBloqueadoTodo_400_YBloqueadoFacturacionSiPuede()
    {
        var socio = await SocioAsync();
        var factura = await FacturaPosteadaAsync(socio, 100m);
        var pago = (await PagarOkAsync(new RegistrarPagoClienteCommand(socio, 50m, D12))).MovimientoClienteId;

        await BloquearSocioAsync(socio, BloqueoSocioNegocio.Todo);
        var antes = await FotoAsync();
        Assert.Equal(("cobro.socio_bloqueado", "MovimientoFacturaId"), Unico(await AplicarAsync(new AplicarPagoCommand(factura, pago, 10m))));
        Assert.Equal(antes, await FotoAsync());

        await BloquearSocioAsync(socio, BloqueoSocioNegocio.Facturacion);
        Assert.Equal(108m, Ok(await AplicarAsync(new AplicarPagoCommand(factura, pago, 10m))).RestanteFactura);
    }

    [Fact]
    public async Task ReviewFocus4_DosAplicacionesConcurrentesDe40SobreUnRestanteDe50_UnaFalla_SinExcederElRestante()
    {
        for (var ronda = 0; ronda < 3; ronda++)
        {
            var socio = await SocioAsync();
            var factura = await FacturaPosteadaAsync(socio, 100m);
            var pago = (await PagarOkAsync(new RegistrarPagoClienteCommand(socio, 50m, D12))).MovimientoClienteId;

            var resultados = await EnParaleloAsync(
                () => AplicarAsync(new AplicarPagoCommand(factura, pago, 40m)),
                () => AplicarAsync(new AplicarPagoCommand(factura, pago, 40m)));

            Assert.All(resultados, r => Assert.Null(r.Excepcion));
            var ganadora = Assert.Single(resultados, r => r.Resultado!.EsExito);
            Assert.Equal((78m, -10m), (ganadora.Resultado!.Valor.RestanteFactura, ganadora.Resultado.Valor.RestantePago));
            var perdedora = Assert.Single(resultados, r => r.Resultado!.EsFallo);
            Assert.Equal(("clientes.importe_excede_restante", "Importe"), Unico(perdedora.Resultado!));
            Assert.Equal((78m, -10m), (await RestanteAsync(factura), await RestanteAsync(pago)));
            Assert.Equal(1L, await EscalarAsync<long>(
                """SELECT COUNT(*) FROM "MovimientosClienteDetalle" WHERE "MovimientoClienteId" = @P AND "TipoMovimiento" = 3""", new { P = pago }));
            Assert.Equal(68m, await SaldoAsync(socio));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Aplicacion_QueEsperaElBloqueoDeOtra_VeLoQueLaOtraAplico_Y400(bool porElHandler)
    {
        // Determinista (sin depender de que las dos coincidan en el tiempo): una transacción aplica 40 y NO confirma; la segunda
        // aplicación queda esperando el FOR UPDATE; al confirmar la primera, la segunda debe leer el restante DESPUÉS del bloqueo
        // (−10) y fallar. Si lo leyera con la instantánea de antes de esperar (−50), aplicaría otros 40.
        var socio = await SocioAsync();
        var factura = await FacturaPosteadaAsync(socio, 100m);
        var pago = (await PagarOkAsync(new RegistrarPagoClienteCommand(socio, 50m, D12))).MovimientoClienteId;

        await using var scope = _prueba.Provider.CreateAsyncScope();
        var sesion = scope.ServiceProvider.GetRequiredService<OpenSource1.Application.Data.IDbSession>();
        await using var tx = await sesion.BeginTransactionAsync();
        Ok(await scope.ServiceProvider.GetRequiredService<OpenSource1.Application.Services.Clientes.IRegistroMovimientosCliente>().AplicarAsync(
            new OpenSource1.Application.Services.Clientes.AplicacionClienteSolicitud(factura, pago, 40m, D15, TipoOrigenMovimiento.Cobro, "PRIMERA")));

        // Por el handler (que ya bloquea antes de llamar al escritor) o directamente por el escritor: el escritor garantiza por sí
        // solo que no se excede el restante.
        var segunda = porElHandler
            ? Task.Run(async () => (Result)await AplicarAsync(new AplicarPagoCommand(factura, pago, 40m)))
            : Task.Run(async () =>
            {
                await using var otro = _prueba.Provider.CreateAsyncScope();
                var otraSesion = otro.ServiceProvider.GetRequiredService<OpenSource1.Application.Data.IDbSession>();
                await using var otraTx = await otraSesion.BeginTransactionAsync();
                var r = await otro.ServiceProvider.GetRequiredService<OpenSource1.Application.Services.Clientes.IRegistroMovimientosCliente>()
                    .AplicarAsync(new OpenSource1.Application.Services.Clientes.AplicacionClienteSolicitud(
                        factura, pago, 40m, D15, TipoOrigenMovimiento.Cobro, "SEGUNDA"));
                if (r.EsExito)
                {
                    await otraSesion.CommitAsync();
                }

                return (Result)r;
            });
        await EsperarBloqueoAsync(segunda);
        await sesion.CommitAsync();

        var resultado = await segunda.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(("clientes.importe_excede_restante", "Importe"), Unico(resultado));
        Assert.Equal((78m, -10m), (await RestanteAsync(factura), await RestanteAsync(pago)));
    }

    /// <summary>Espera a que alguna sesión de esta base de datos esté bloqueada esperando un lock (la segunda aplicación).</summary>
    private async Task EsperarBloqueoAsync(Task tarea)
    {
        var limite = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < limite)
        {
            Assert.False(tarea.IsCompleted, "La segunda aplicación terminó sin esperar el bloqueo de la primera.");
            if (await EscalarAsync<long>(
                    """SELECT COUNT(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'""") > 0)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail("La segunda aplicación no llegó a esperar el bloqueo.");
    }

    // ----- Pago -----

    [Fact]
    public async Task Pago_MovimientoPagoConSuDetalle_NumeroCobro_YAsientoCajaContraCxCCuadrado()
    {
        var socio = await SocioAsync();
        var ultimoCobro = await UltimoNumeroAsync(SerieCobroIds.LineaSerieId);
        var ultimoContab = await UltimoNumeroAsync(SerieContabilidadIds.LineaSerieId);

        var pago = await PagarOkAsync(new RegistrarPagoClienteCommand(socio, 50.25m, D12, D10, null, "Transferencia 123"));

        Assert.Equal(Siguiente(ultimoCobro), pago.Numero);
        Assert.Equal(Siguiente(ultimoContab), pago.RegistroContable);
        Assert.Equal(long.Parse(pago.Numero), long.Parse(await UltimoNumeroAsync(SerieCobroIds.LineaSerieId)));

        var movimiento = await MovimientoAsync(pago.MovimientoClienteId);
        Assert.Equal((socio, TipoDocumentoCliente.Pago, pago.Numero, -50.25m, "Transferencia 123"),
            (movimiento.SocioNegocioId, movimiento.TipoDocumento, movimiento.NumeroDocumento, movimiento.ImporteOriginal, movimiento.Descripcion));
        Assert.Equal((D12, D10, D10), (movimiento.FechaRegistro, movimiento.FechaDocumento, movimiento.FechaVencimiento));
        Assert.Equal((GrupoContableIds.ClienteContableGeneral, CuentaContableIds.CxC, TipoOrigenMovimiento.Cobro, pago.Numero),
            (movimiento.GrupoClienteContableId, movimiento.CuentaCxCId, movimiento.TipoOrigen, movimiento.ClaveOrigen));
        Assert.Equal([(TipoDetalleCliente.Pago, -50.25m, (long?)null, D12)], await DetalleAsync(pago.MovimientoClienteId));

        // Asiento: débito caja (1101 por defecto) / crédito CxC, ambos del socio del pago; suma 0.
        var registro = await EscalarAsync<long>(
            """SELECT "Id" FROM "RegistrosContables" WHERE "NumeroRegistro" = @N""", new { N = pago.RegistroContable });
        var asiento = await AsientoAsync(registro);
        Assert.Equal(
            [(CuentaContableIds.Caja, 50.25m, (Guid?)socio), (CuentaContableIds.CxC, -50.25m, (Guid?)socio)],
            asiento.Select(m => (m.CuentaContableId, m.Importe, m.SocioNegocioId)));
        Assert.Equal(0m, asiento.Sum(m => m.Importe));
        Assert.All(asiento, m => Assert.Equal(
            (TipoDocumentoContable.Cobro, pago.Numero, D12, D10, TipoOrigenMovimiento.Cobro, pago.Numero),
            (m.TipoDocumento, m.NumeroDocumento, m.FechaRegistro, m.FechaDocumento, m.TipoOrigen, m.ClaveOrigen)));
        Assert.Equal(-50.25m, await SaldoAsync(socio));
    }

    [Fact]
    public async Task Pago_ConCuentaDeBanco_YGrupoDeClientePropio_UsaLaCxCDelGrupoVigente_YSocioBloqueadoFacturacionPuedePagar()
    {
        var banco = await CuentaAsync(posteoDirecto: true);
        var cxcPropia = await CuentaAsync(posteoDirecto: false);
        var grupo = await GrupoClienteAsync(cxcPropia);
        var socio = await SocioAsync(grupoCliente: grupo);
        await BloquearSocioAsync(socio, BloqueoSocioNegocio.Facturacion);

        var pago = await PagarOkAsync(new RegistrarPagoClienteCommand(socio, 10m, D12, CuentaCajaId: banco));

        var movimiento = await MovimientoAsync(pago.MovimientoClienteId);
        Assert.Equal((grupo, cxcPropia, D12, D12), (movimiento.GrupoClienteContableId, movimiento.CuentaCxCId, movimiento.FechaDocumento, movimiento.FechaVencimiento));
        Assert.Equal($"Cobro {pago.Numero}", movimiento.Descripcion);
        var registro = await EscalarAsync<long>(
            """SELECT "Id" FROM "RegistrosContables" WHERE "NumeroRegistro" = @N""", new { N = pago.RegistroContable });
        Assert.Equal([(banco, 10m), (cxcPropia, -10m)], (await AsientoAsync(registro)).Select(m => (m.CuentaContableId, m.Importe)));
    }

    [Theory]
    [InlineData("socio_inexistente")]
    [InlineData("socio_bloqueado_todo")]
    [InlineData("sin_grupo_cliente")]
    [InlineData("cxc_bloqueada")]
    [InlineData("caja_sin_posteo_directo")]
    [InlineData("caja_bloqueada")]
    [InlineData("caja_titulo")]
    [InlineData("caja_inexistente")]
    [InlineData("importe_cero")]
    [InlineData("importe_negativo")]
    [InlineData("importe_tres_decimales")]
    [InlineData("importe_fuera_de_cota")]
    [InlineData("sin_fecha")]
    [InlineData("descripcion_larga")]
    public async Task Pago_Invalido_400ConElCampo_YNingunaFilaNiNumeroNiIntentoDeEscritura(string caso)
    {
        Guid? grupoCliente = null;
        if (caso == "cxc_bloqueada")
        {
            var cxc = await CuentaAsync(posteoDirecto: false);
            grupoCliente = await GrupoClienteAsync(cxc);
            await EjecutarSqlAsync("""UPDATE "CuentasContables" SET "Bloqueada" = true WHERE "Id" = @Id""", new { Id = cxc });
        }

        var socio = await SocioAsync(grupoCliente: grupoCliente);
        if (caso == "sin_grupo_cliente")
        {
            await EjecutarSqlAsync("""UPDATE "SociosNegocio" SET "GrupoClienteContableId" = NULL WHERE "Id" = @Id""", new { Id = socio });
        }

        if (caso == "socio_bloqueado_todo")
        {
            await BloquearSocioAsync(socio, BloqueoSocioNegocio.Todo);
        }

        Guid? caja = caso switch
        {
            "caja_sin_posteo_directo" => await CuentaAsync(posteoDirecto: false),
            "caja_bloqueada" => await CuentaAsync(posteoDirecto: true),
            "caja_titulo" => CuentaContableIds.Activos,
            "caja_inexistente" => Guid.NewGuid(),
            _ => null,
        };
        if (caso == "caja_bloqueada")
        {
            await EjecutarSqlAsync("""UPDATE "CuentasContables" SET "Bloqueada" = true WHERE "Id" = @Id""", new { Id = caja });
        }

        var comando = new RegistrarPagoClienteCommand(
            caso == "socio_inexistente" ? Guid.NewGuid() : socio,
            caso switch
            {
                "importe_cero" => 0m,
                "importe_negativo" => -10m,
                "importe_tres_decimales" => 10.005m,
                "importe_fuera_de_cota" => 100_000_000_000_000m,
                _ => 10m,
            },
            caso == "sin_fecha" ? default : D12,
            null,
            caja,
            caso == "descripcion_larga" ? new string('x', 201) : null);
        var esperado = caso switch
        {
            "socio_inexistente" => ("cobro.socio_invalido", "SocioNegocioId"),
            "socio_bloqueado_todo" => ("cobro.socio_bloqueado", "SocioNegocioId"),
            "sin_grupo_cliente" => ("setup_contable.grupo_faltante", "SocioNegocioId"),
            "cxc_bloqueada" => ("setup_contable.cuenta_invalida", "SocioNegocioId"),
            "caja_sin_posteo_directo" or "caja_bloqueada" or "caja_titulo" or "caja_inexistente" => ("cobro.cuenta_invalida", "CuentaCajaId"),
            "sin_fecha" => ("cobro.fecha_invalida", "FechaRegistro"),
            "descripcion_larga" => ("cobro.descripcion_invalida", "Descripcion"),
            _ => ("cobro.importe_invalido", "Importe"),
        };
        var antes = await FotoAsync();

        var resultado = await PagarAsync(comando);

        Assert.Equal(esperado, Unico(resultado));
        Assert.Equal(antes, await FotoAsync());
    }

    [Fact]
    public async Task Pago_VariosErrores_SeDevuelvenTodos_SinEscribir()
    {
        var socio = await SocioAsync();
        await EjecutarSqlAsync("""UPDATE "SociosNegocio" SET "GrupoClienteContableId" = NULL WHERE "Id" = @Id""", new { Id = socio });
        var antes = await FotoAsync();

        var resultado = await PagarAsync(new RegistrarPagoClienteCommand(socio, 10m, D12, CuentaCajaId: CuentaContableIds.CxC));

        Assert.Equal(
            [("cobro.cuenta_invalida", "CuentaCajaId"), ("setup_contable.grupo_faltante", "SocioNegocioId")],
            resultado.Errores.Select(e => (e.Codigo, e.Campo)).Order());
        Assert.Equal(antes, await FotoAsync());
    }

    [Fact]
    public async Task Pago_DosPagosConcurrentes_NumerosCobroConsecutivosSinHuecos()
    {
        var socioA = await SocioAsync();
        var socioB = await SocioAsync();
        var ultimo = long.Parse(await UltimoNumeroAsync(SerieCobroIds.LineaSerieId));

        var resultados = await EnParaleloAsync(
            () => PagarAsync(new RegistrarPagoClienteCommand(socioA, 5m, D12)),
            () => PagarAsync(new RegistrarPagoClienteCommand(socioB, 7m, D12)),
            () => PagarAsync(new RegistrarPagoClienteCommand(socioA, 9m, D12)));

        Assert.All(resultados, r =>
        {
            Assert.Null(r.Excepcion);
            Assert.True(r.Resultado!.EsExito, r.Resultado.EsFallo ? r.Resultado.Errores[0].Mensaje : null);
        });
        Assert.Equal([ultimo + 1, ultimo + 2, ultimo + 3], resultados.Select(r => long.Parse(r.Resultado!.Valor.Numero)).Order());
        Assert.Equal((-14m, -7m), (await SaldoAsync(socioA), await SaldoAsync(socioB)));
    }

    [Fact]
    public async Task DentroDeOtraTransaccion_Lanza()
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var pagar = ActivatorUtilities.CreateInstance<RegistrarPagoClienteCommandHandler>(scope.ServiceProvider);
        var aplicar = ActivatorUtilities.CreateInstance<AplicarPagoCommandHandler>(scope.ServiceProvider);
        await using var tx = await unitOfWork.BeginTransactionAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => pagar.Handle(new RegistrarPagoClienteCommand(Guid.NewGuid(), 1m, D12), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => aplicar.Handle(new AplicarPagoCommand(1, 2, 1m), default));
    }

    [Fact]
    public async Task MovimientosAbiertos_SocioInexistente404()
    {
        var resultado = await ConsultarAsync<ListMovimientosAbiertosClienteQueryHandler, ListMovimientosAbiertosClienteQuery,
            IReadOnlyList<MovimientoClienteResponse>>(new ListMovimientosAbiertosClienteQuery(Guid.NewGuid()));

        Assert.Equal(("socio_negocio.no_encontrado", "Id"), Unico(resultado));
    }

    [Fact]
    public async Task SinSerieConfiguradaParaCobros_400EnTipoDocumento_SinEscribirNada()
    {
        var socio = await SocioAsync();
        await FacturaPosteadaAsync(socio, 100m);
        var antes = await FotoAsync();
        try
        {
            await EjecutarSqlAsync("""UPDATE "ConfiguracionesNumeracion" SET "IsDeleted" = true WHERE "TipoDocumento" = @Tipo""", new { Tipo = (short)TipoDocumentoSerie.Cobro });

            var resultado = await PagarAsync(new RegistrarPagoClienteCommand(socio, 10m, D12));

            Assert.Equal(("numeracion.sin_configuracion", "TipoDocumento"), (resultado.Errores[0].Codigo, resultado.Errores[0].Campo));
            Assert.Equal(antes, await FotoAsync());
        }
        finally
        {
            await EjecutarSqlAsync("""UPDATE "ConfiguracionesNumeracion" SET "IsDeleted" = false WHERE "TipoDocumento" = @Tipo""", new { Tipo = (short)TipoDocumentoSerie.Cobro });
        }
    }

    // ----- Helpers: siembra -----

    private async Task<Guid> SocioAsync(Guid? grupoCliente = null)
    {
        await using var contexto = _prueba.NuevoContexto();
        var socio = new SocioNegocio
        {
            Codigo = $"CB{Guid.NewGuid():N}"[..10].ToUpperInvariant(),
            NombreComercial = "Cliente cobros",
            RazonSocial = "Cliente cobros SRL",
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

    private Task BloquearSocioAsync(Guid socio, BloqueoSocioNegocio bloqueo) =>
        EjecutarSqlAsync("""UPDATE "SociosNegocio" SET "Bloqueado" = @B WHERE "Id" = @Id""", new { B = (short)bloqueo, Id = socio });

    private async Task<Guid> GrupoClienteAsync(Guid cuentaCxC)
    {
        await using var contexto = _prueba.NuevoContexto();
        var grupo = new GrupoClienteContable
        {
            Codigo = $"GC{Guid.NewGuid():N}"[..12].ToUpperInvariant(), Descripcion = "Grupo cliente cobros", CuentaCxCId = cuentaCxC, CreatedBy = "test",
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
            Nombre = posteoDirecto ? "Banco / otros ingresos" : "CxC propia",
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

    /// <summary>
    /// Factura REAL posteada por el motor de la Task 6.4 (borrador con una línea de cuenta contable al ITBIS 18 %): devuelve el Id de
    /// su movimiento de cliente (restante = base × 1.18).
    /// </summary>
    private async Task<long> FacturaPosteadaAsync(Guid socio, decimal baseImponible)
    {
        var ingresos = await CuentaAsync(posteoDirecto: true);
        Guid borradorId;
        await using (var scope = _prueba.Provider.CreateAsyncScope())
        {
            var crear = ActivatorUtilities.CreateInstance<CreateFacturaVentaBorradorCommandHandler>(scope.ServiceProvider);
            borradorId = Ok(await crear.Handle(new CreateFacturaVentaBorradorCommand(socio, null, D10, D10, null, null, null), default)).Id;
        }

        await using (var scope = _prueba.Provider.CreateAsyncScope())
        {
            var linea = ActivatorUtilities.CreateInstance<CreateLineaFacturaVentaBorradorCommandHandler>(scope.ServiceProvider);
            Ok(await linea.Handle(new CreateLineaFacturaVentaBorradorCommand(
                borradorId, TipoLineaFactura.CuentaContable, null, ingresos, null, null, null, 1m, baseImponible, null,
                GrupoContableIds.IvaProductoItbis18), default));
        }

        string numero;
        await using (var scope = _prueba.Provider.CreateAsyncScope())
        {
            var postear = ActivatorUtilities.CreateInstance<PostearFacturaVentaCommandHandler>(scope.ServiceProvider);
            numero = Ok(await postear.Handle(new PostearFacturaVentaCommand(borradorId), default)).Numero;
        }

        return await EscalarAsync<long>(
            """SELECT "Id" FROM "MovimientosCliente" WHERE "TipoDocumento" = 1 AND "NumeroDocumento" = @N""", new { N = numero });
    }

    // ----- Helpers: comandos (handlers reales, un scope por llamada) -----

    private async Task<Result<ResultadoPagoCliente>> PagarAsync(RegistrarPagoClienteCommand comando)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<RegistrarPagoClienteCommandHandler>(scope.ServiceProvider);
        return await handler.Handle(comando, default);
    }

    private async Task<ResultadoPagoCliente> PagarOkAsync(RegistrarPagoClienteCommand comando) => Ok(await PagarAsync(comando));

    /// <summary>Pago con el validador de fechas de registro del usuario indicado (null = proceso del sistema).</summary>
    private async Task<Result<ResultadoPagoCliente>> PagarAsync(RegistrarPagoClienteCommand comando, Guid? usuario)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<RegistrarPagoClienteCommandHandler>(
            scope.ServiceProvider, FechasRegistroPrueba.Validador(scope.ServiceProvider, usuario));
        return await handler.Handle(comando, default);
    }

    /// <summary>Aplicación con el validador de fechas de registro del usuario indicado (null = proceso del sistema).</summary>
    private async Task<Result<ResultadoAplicacionPago>> AplicarAsync(AplicarPagoCommand comando, Guid? usuario)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<AplicarPagoCommandHandler>(
            scope.ServiceProvider, FechasRegistroPrueba.Validador(scope.ServiceProvider, usuario));
        return await handler.Handle(comando, default);
    }

    private async Task<Result<ResultadoAplicacionPago>> AplicarAsync(AplicarPagoCommand comando)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<AplicarPagoCommandHandler>(scope.ServiceProvider);
        return await handler.Handle(comando, default);
    }

    private async Task<Result<TRespuesta>> ConsultarAsync<THandler, TQuery, TRespuesta>(TQuery consulta)
        where THandler : MediatR.IRequestHandler<TQuery, Result<TRespuesta>>
        where TQuery : MediatR.IRequest<Result<TRespuesta>>
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<THandler>(scope.ServiceProvider);
        return await handler.Handle(consulta, default);
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
        Assert.True(resultado.EsFallo, "Se esperaba un fallo");
        var error = Assert.Single(resultado.Errores);
        return (error.Codigo, error.Campo);
    }

    private static async Task<List<(Result<T>? Resultado, Exception? Excepcion)>> EnParaleloAsync<T>(params Func<Task<Result<T>>>[] acciones)
    {
        var barrera = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listas = 0;
        var resultados = new ConcurrentBag<(Result<T>?, Exception?)>();

        async Task EjecutarAsync(Func<Task<Result<T>>> accion)
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

    // ----- Helpers: lectura -----

    private static string Siguiente(string ultimo) => (long.Parse(ultimo) + 1).ToString().PadLeft(8, '0');

    private Task<string> UltimoNumeroAsync(Guid lineaSerieId) => EscalarAsync<string>(
        """SELECT "UltimoNumeroUsado" FROM "LineasSerie" WHERE "Id" = @Id""", new { Id = lineaSerieId });

    private Task<decimal> RestanteAsync(long movimientoId) => EscalarAsync<decimal>(
        """SELECT COALESCE(SUM("Importe"), 0) FROM "MovimientosClienteDetalle" WHERE "MovimientoClienteId" = @Id""", new { Id = movimientoId });

    private async Task<decimal> SaldoAsync(Guid socio) =>
        Ok(await ConsultarAsync<GetSaldoClienteQueryHandler, GetSaldoClienteQuery, SaldoClienteResponse>(new GetSaldoClienteQuery(socio))).Saldo;

    private async Task<MovimientoCliente> MovimientoAsync(long id)
    {
        await using var conexion = _prueba.NuevaConexion();
        return await conexion.QuerySingleAsync<MovimientoCliente>("""SELECT * FROM "MovimientosCliente" WHERE "Id" = @Id""", new { Id = id });
    }

    private async Task<List<(TipoDetalleCliente, decimal, long?, DateOnly)>> DetalleAsync(long movimientoId)
    {
        await using var conexion = _prueba.NuevaConexion();
        var filas = await conexion.QueryAsync<MovimientoClienteDetalle>(
            """SELECT * FROM "MovimientosClienteDetalle" WHERE "MovimientoClienteId" = @Id ORDER BY "Id" """, new { Id = movimientoId });
        return [.. filas.Select(d => (d.TipoMovimiento, d.Importe, d.MovimientoClienteAplicadoId, d.FechaRegistro))];
    }

    private async Task<List<MovimientoContable>> AsientoAsync(long registroId)
    {
        await using var conexion = _prueba.NuevaConexion();
        return [.. await conexion.QueryAsync<MovimientoContable>(
            """SELECT * FROM "MovimientosContables" WHERE "RegistroContableId" = @Id ORDER BY "Id" """, new { Id = registroId })];
    }

    private static readonly string[] TablasEscritas =
        ["MovimientosCliente", "MovimientosClienteDetalle", "RegistrosContables", "MovimientosContables"];

    /// <summary>
    /// Filas de las tablas que escriben el pago y la aplicación, números de las series COBRO y CONTAB y el último valor de las
    /// secuencias de identidad de esas tablas (NO transaccionales: un INSERT intentado y deshecho las movería).
    /// </summary>
    private async Task<Dictionary<string, string?>> FotoAsync()
    {
        await using var conexion = _prueba.NuevaConexion();
        var foto = new Dictionary<string, string?>();
        foreach (var tabla in TablasEscritas)
        {
            foto[tabla] = (await conexion.ExecuteScalarAsync<long>($"""SELECT COUNT(*) FROM "{tabla}" """)).ToString();
            foto[$"secuencia {tabla}"] = (await conexion.ExecuteScalarAsync<long?>(
                $"""SELECT pg_sequence_last_value(pg_get_serial_sequence('"{tabla}"', 'Id')::regclass)"""))?.ToString();
        }

        foto["serie COBRO"] = await conexion.ExecuteScalarAsync<string>(
            """SELECT "UltimoNumeroUsado" FROM "LineasSerie" WHERE "Id" = @Id""", new { Id = SerieCobroIds.LineaSerieId });
        foto["serie CONTAB"] = await conexion.ExecuteScalarAsync<string>(
            """SELECT "UltimoNumeroUsado" FROM "LineasSerie" WHERE "Id" = @Id""", new { Id = SerieContabilidadIds.LineaSerieId });
        return foto;
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
}
