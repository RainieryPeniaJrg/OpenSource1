using Dapper;
using OpenSource1.Application.Data;
using OpenSource1.Application.Services.Clientes;
using OpenSource1.Application.Services.Inventario;
using OpenSource1.Core.Common;
using OpenSource1.Core.Enums;

namespace OpenSource1.Infrastructure.Services.Clientes;

/// <summary>
/// Escritor único del libro de clientes (Task 6.4; pagos y aplicaciones en la Task 6.5). Dapper sobre la conexión y la transacción de <see cref="IDbSession"/> (la del
/// llamador: nunca abre ni confirma una propia). Solo <c>INSERT</c>: las dos tablas son append-only (trigger
/// <c>libro_inventario_append_only()</c>).
/// </summary>
public sealed class RegistroMovimientosCliente(IDbSession session, IUsuarioActual usuario) : IRegistroMovimientosCliente
{
    private const int LongitudNumeroDocumento = 20;
    private const int LongitudDescripcion = 200;
    private const int LongitudClaveOrigen = 50;
    private const int LongitudCreatedBy = 100;

    /// <summary>Máximo absoluto de numeric(18,4).</summary>
    private const decimal ImporteMaximo = 99_999_999_999_999.9999m;

    public async Task<Result<MovimientoClienteRegistrado>> RegistrarAsync(MovimientoClienteSolicitud solicitud, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(solicitud);

        if (!session.HayTransaccionActiva)
        {
            return Fallo(new Error("clientes.sin_transaccion", "El registro de movimientos de cliente requiere una transacción activa."));
        }

        var errores = Validar(solicitud);
        if (errores.Count > 0)
        {
            return Result<MovimientoClienteRegistrado>.Fallo([.. errores]);
        }

        await session.EnsureOpenAsync(ct);
        var tx = session.CurrentTransaction;
        var ahora = DateTimeOffset.UtcNow;
        var creadoPor = CreadoPor();

        var movimientoId = await session.Connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO "MovimientosCliente" (
                "SocioNegocioId", "FechaRegistro", "FechaDocumento", "FechaVencimiento", "TipoDocumento", "NumeroDocumento",
                "Descripcion", "ImporteOriginal", "GrupoClienteContableId", "CuentaCxCId", "TipoOrigen", "ClaveOrigen",
                "CreatedAtUtc", "CreatedBy", "UsuarioId")
            VALUES (
                @SocioNegocioId, @FechaRegistro, @FechaDocumento, @FechaVencimiento, @TipoDocumento, @NumeroDocumento,
                @Descripcion, @ImporteOriginal, @GrupoClienteContableId, @CuentaCxCId, @TipoOrigen, @ClaveOrigen,
                @CreatedAtUtc, @CreatedBy, @UsuarioId)
            RETURNING "Id"
            """,
            new
            {
                solicitud.SocioNegocioId,
                solicitud.FechaRegistro,
                solicitud.FechaDocumento,
                solicitud.FechaVencimiento,
                solicitud.TipoDocumento,
                solicitud.NumeroDocumento,
                solicitud.Descripcion,
                solicitud.ImporteOriginal,
                solicitud.GrupoClienteContableId,
                solicitud.CuentaCxCId,
                solicitud.TipoOrigen,
                solicitud.ClaveOrigen,
                CreatedAtUtc = ahora,
                CreatedBy = creadoPor,
                UsuarioId = usuario.Id,
            },
            tx, cancellationToken: ct));

        var detalleId = await session.Connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO "MovimientosClienteDetalle" (
                "MovimientoClienteId", "TipoMovimiento", "Importe", "FechaRegistro", "MovimientoClienteAplicadoId", "TipoOrigen",
                "ClaveOrigen", "CreatedAtUtc", "CreatedBy", "UsuarioId")
            VALUES (
                @MovimientoClienteId, @TipoMovimiento, @Importe, @FechaRegistro, NULL, @TipoOrigen,
                @ClaveOrigen, @CreatedAtUtc, @CreatedBy, @UsuarioId)
            RETURNING "Id"
            """,
            new
            {
                MovimientoClienteId = movimientoId,
                TipoMovimiento = solicitud.TipoDocumento == TipoDocumentoCliente.Pago
                    ? TipoDetalleCliente.Pago
                    : TipoDetalleCliente.ImporteInicial,
                Importe = solicitud.ImporteOriginal,
                solicitud.FechaRegistro,
                solicitud.TipoOrigen,
                solicitud.ClaveOrigen,
                CreatedAtUtc = ahora,
                CreatedBy = creadoPor,
                UsuarioId = usuario.Id,
            },
            tx, cancellationToken: ct));

        return Result<MovimientoClienteRegistrado>.Exito(new MovimientoClienteRegistrado(movimientoId, detalleId));
    }

    public async Task<IReadOnlyList<MovimientoClienteBloqueado>> BloquearMovimientosAsync(IEnumerable<long> ids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (!session.HayTransaccionActiva)
        {
            throw new InvalidOperationException("El bloqueo de movimientos de cliente requiere una transacción activa.");
        }

        var unicos = ids.Distinct().Order().ToArray();
        if (unicos.Length == 0)
        {
            return [];
        }

        await session.EnsureOpenAsync(ct);
        var tx = session.CurrentTransaction;

        // 1) Bloqueo, en orden de Id: dos aplicaciones sobre movimientos comunes se serializan sin interbloquearse.
        // Original projection: SELECT "Id", "SocioNegocioId", "TipoDocumento", "NumeroDocumento"
        var filas = (await session.Connection.QueryAsync<MovimientoFila>(new CommandDefinition(
            """
            SELECT "Id", "SocioNegocioId", "TipoDocumento", "NumeroDocumento", "FechaRegistro", "CuentaCxCId"
            FROM "MovimientosCliente"
            WHERE "Id" = ANY(@Ids)
            ORDER BY "Id"
            FOR UPDATE
            """,
            new { Ids = unicos }, tx, cancellationToken: ct))).AsList();
        if (filas.Count == 0)
        {
            return [];
        }

        // 2) Restantes en OTRA sentencia, posterior al bloqueo: en READ COMMITTED cada sentencia toma una instantánea nueva, así
        // que aquí se ve el detalle que confirmó quien tenía el bloqueo antes. Calcularlos en la misma sentencia del FOR UPDATE
        // usaría la instantánea anterior a la espera y dos aplicaciones concurrentes podrían exceder el restante.
        var restantes = (await session.Connection.QueryAsync<(long Id, decimal Restante)>(new CommandDefinition(
            """
            SELECT "MovimientoClienteId", COALESCE(SUM("Importe"), 0)
            FROM "MovimientosClienteDetalle"
            WHERE "MovimientoClienteId" = ANY(@Ids)
            GROUP BY "MovimientoClienteId"
            """,
            new { Ids = filas.Select(f => f.Id).ToArray() }, tx, cancellationToken: ct))).ToDictionary(r => r.Id, r => r.Restante);

        // Original:
        // return [.. filas.Select(f => new MovimientoClienteBloqueado(
        //     f.Id, f.SocioNegocioId, (TipoDocumentoCliente)f.TipoDocumento, f.NumeroDocumento, restantes.GetValueOrDefault(f.Id)))];
        return [.. filas.Select(f => new MovimientoClienteBloqueado(
            f.Id, f.SocioNegocioId, (TipoDocumentoCliente)f.TipoDocumento, f.NumeroDocumento, restantes.GetValueOrDefault(f.Id),
            f.FechaRegistro, f.CuentaCxCId))];
    }

    public async Task<Result<AplicacionClienteRegistrada>> AplicarAsync(AplicacionClienteSolicitud solicitud, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(solicitud);

        if (!session.HayTransaccionActiva)
        {
            return Result<AplicacionClienteRegistrada>.Fallo(
                new Error("clientes.sin_transaccion", "El registro de movimientos de cliente requiere una transacción activa."));
        }

        var errores = ValidarAplicacion(solicitud);
        if (errores.Count > 0)
        {
            return Result<AplicacionClienteRegistrada>.Fallo([.. errores]);
        }

        var bloqueados = await BloquearMovimientosAsync([solicitud.MovimientoFacturaId, solicitud.MovimientoPagoId], ct);
        var factura = bloqueados.FirstOrDefault(m => m.Id == solicitud.MovimientoFacturaId);
        var pago = bloqueados.FirstOrDefault(m => m.Id == solicitud.MovimientoPagoId);
        if (factura is null)
        {
            errores.Add(new Error("clientes.movimiento_inexistente", "El movimiento de cliente a pagar no existe.", "MovimientoFacturaId"));
        }

        if (pago is null)
        {
            errores.Add(new Error("clientes.movimiento_inexistente", "El movimiento de cliente del pago no existe.", "MovimientoPagoId"));
        }

        if (factura is null || pago is null)
        {
            return Result<AplicacionClienteRegistrada>.Fallo([.. errores]);
        }

        if (factura.SocioNegocioId != pago.SocioNegocioId)
        {
            return Result<AplicacionClienteRegistrada>.Fallo(new Error(
                "clientes.socios_distintos",
                $"El pago {pago.NumeroDocumento} y el documento {factura.NumeroDocumento} son de clientes distintos: solo se aplica entre " +
                "movimientos del mismo cliente.",
                "MovimientoPagoId"));
        }

        if (solicitud.FechaRegistro < factura.FechaRegistro || solicitud.FechaRegistro < pago.FechaRegistro)
        {
            return Result<AplicacionClienteRegistrada>.Fallo(new Error(
                "cobro.fecha_anterior_documentos",
                "La fecha de aplicación no puede ser anterior a la factura ni al pago.", "FechaRegistro"));
        }

        if (factura.ImporteRestante <= 0m)
        {
            errores.Add(new Error(
                "clientes.movimiento_sin_restante",
                $"El documento {factura.NumeroDocumento} no tiene importe pendiente de cobro (restante {factura.ImporteRestante:0.00##}).",
                "MovimientoFacturaId"));
        }

        if (pago.ImporteRestante >= 0m)
        {
            errores.Add(new Error(
                "clientes.movimiento_sin_restante",
                $"El pago {pago.NumeroDocumento} no tiene importe pendiente de aplicar (restante {pago.ImporteRestante:0.00##}).",
                "MovimientoPagoId"));
        }

        if (errores.Count > 0)
        {
            return Result<AplicacionClienteRegistrada>.Fallo([.. errores]);
        }

        var maximo = Math.Min(factura.ImporteRestante, -pago.ImporteRestante);
        if (solicitud.Importe > maximo)
        {
            return Result<AplicacionClienteRegistrada>.Fallo(new Error(
                "clientes.importe_excede_restante",
                $"El importe a aplicar ({solicitud.Importe:0.00##}) excede lo pendiente: {factura.ImporteRestante:0.00##} del documento " +
                $"{factura.NumeroDocumento} y {-pago.ImporteRestante:0.00##} del pago {pago.NumeroDocumento}.",
                "Importe"));
        }

        var tx = session.CurrentTransaction;
        var ahora = DateTimeOffset.UtcNow;
        var creadoPor = CreadoPor();

        async Task<long> InsertarAsync(long movimientoId, long aplicadoId, decimal importe) =>
            await session.Connection.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                INSERT INTO "MovimientosClienteDetalle" (
                    "MovimientoClienteId", "TipoMovimiento", "Importe", "FechaRegistro", "MovimientoClienteAplicadoId", "TipoOrigen",
                    "ClaveOrigen", "CreatedAtUtc", "CreatedBy", "UsuarioId")
                VALUES (
                    @MovimientoClienteId, @TipoMovimiento, @Importe, @FechaRegistro, @MovimientoClienteAplicadoId, @TipoOrigen,
                    @ClaveOrigen, @CreatedAtUtc, @CreatedBy, @UsuarioId)
                RETURNING "Id"
                """,
                new
                {
                    MovimientoClienteId = movimientoId,
                    TipoMovimiento = TipoDetalleCliente.Aplicacion,
                    Importe = importe,
                    solicitud.FechaRegistro,
                    MovimientoClienteAplicadoId = aplicadoId,
                    solicitud.TipoOrigen,
                    solicitud.ClaveOrigen,
                    CreatedAtUtc = ahora,
                    CreatedBy = creadoPor,
                    UsuarioId = usuario.Id,
                },
                tx, cancellationToken: ct));

        var detalleFactura = await InsertarAsync(factura.Id, pago.Id, -solicitud.Importe);
        var detallePago = await InsertarAsync(pago.Id, factura.Id, solicitud.Importe);

        return Result<AplicacionClienteRegistrada>.Exito(new AplicacionClienteRegistrada(
            detalleFactura, detallePago, factura.ImporteRestante - solicitud.Importe, pago.ImporteRestante + solicitud.Importe));
    }

    private static List<Error> ValidarAplicacion(AplicacionClienteSolicitud s)
    {
        var errores = new List<Error>();

        if (s.MovimientoFacturaId == s.MovimientoPagoId)
        {
            errores.Add(new Error(
                "clientes.aplicacion_invalida", "Un movimiento no se puede aplicar a sí mismo.", "MovimientoPagoId"));
        }

        if (s.Importe <= 0m || s.Importe > ImporteMaximo || decimal.Round(s.Importe, 4) != s.Importe)
        {
            errores.Add(new Error(
                "clientes.importe_invalido",
                "El importe a aplicar debe ser mayor que cero, menor que 1e14 y tener como máximo 4 decimales.", "Importe"));
        }

        if (s.FechaRegistro == default)
        {
            errores.Add(new Error("clientes.fecha_invalida", "La fecha de la aplicación es obligatoria.", "FechaRegistro"));
        }

        if (string.IsNullOrWhiteSpace(s.ClaveOrigen) || s.ClaveOrigen.Length > LongitudClaveOrigen)
        {
            errores.Add(new Error(
                "clientes.clave_origen_invalida",
                $"La clave de origen es obligatoria y admite como máximo {LongitudClaveOrigen} caracteres.", "ClaveOrigen"));
        }

        return errores;
    }

    private static List<Error> Validar(MovimientoClienteSolicitud s)
    {
        var errores = new List<Error>();

        if (!Enum.IsDefined(s.TipoDocumento))
        {
            errores.Add(new Error("clientes.tipo_documento_invalido", "El tipo de documento del movimiento de cliente no es válido.", "TipoDocumento"));
        }

        if (string.IsNullOrWhiteSpace(s.NumeroDocumento) || s.NumeroDocumento.Length > LongitudNumeroDocumento)
        {
            errores.Add(new Error(
                "clientes.numero_documento_invalido",
                $"El número de documento es obligatorio y admite como máximo {LongitudNumeroDocumento} caracteres.", "NumeroDocumento"));
        }

        if (s.Descripcion is { Length: > LongitudDescripcion })
        {
            errores.Add(new Error(
                "clientes.descripcion_invalida", $"La descripción admite como máximo {LongitudDescripcion} caracteres.", "Descripcion"));
        }

        if (string.IsNullOrWhiteSpace(s.ClaveOrigen) || s.ClaveOrigen.Length > LongitudClaveOrigen)
        {
            errores.Add(new Error(
                "clientes.clave_origen_invalida",
                $"La clave de origen es obligatoria y admite como máximo {LongitudClaveOrigen} caracteres.", "ClaveOrigen"));
        }

        if (s.ImporteOriginal == 0m || Math.Abs(s.ImporteOriginal) > ImporteMaximo || decimal.Round(s.ImporteOriginal, 4) != s.ImporteOriginal)
        {
            errores.Add(new Error(
                "clientes.importe_invalido",
                "El importe debe ser distinto de cero, menor que 1e14 en valor absoluto y tener como máximo 4 decimales.", "ImporteOriginal"));
        }
        else if (s.TipoDocumento == TipoDocumentoCliente.Factura && s.ImporteOriginal < 0m)
        {
            errores.Add(new Error("clientes.importe_invalido", "El importe de una factura debe ser positivo.", "ImporteOriginal"));
        }
        else if (s.TipoDocumento is TipoDocumentoCliente.Pago or TipoDocumentoCliente.NotaCredito && s.ImporteOriginal > 0m)
        {
            errores.Add(new Error("clientes.importe_invalido", "El importe de un pago o nota de crédito debe ser negativo.", "ImporteOriginal"));
        }

        if (s.FechaRegistro == default || s.FechaDocumento == default || s.FechaVencimiento == default)
        {
            errores.Add(new Error("clientes.fecha_invalida", "Las fechas del movimiento de cliente son obligatorias.", "FechaRegistro"));
        }

        return errores;
    }

    private string CreadoPor()
    {
        var nombre = string.IsNullOrWhiteSpace(usuario.Nombre) ? "system" : usuario.Nombre;
        return nombre.Length <= LongitudCreatedBy ? nombre : nombre[..LongitudCreatedBy];
    }

    private static Result<MovimientoClienteRegistrado> Fallo(params Error[] errores) => Result<MovimientoClienteRegistrado>.Fallo(errores);

    private sealed class MovimientoFila
    {
        public long Id { get; init; }
        public Guid SocioNegocioId { get; init; }
        public short TipoDocumento { get; init; }
        public string NumeroDocumento { get; init; } = string.Empty;
        public DateOnly FechaRegistro { get; init; }
        public Guid CuentaCxCId { get; init; }
    }
}
