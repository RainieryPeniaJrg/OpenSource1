using OpenSource1.Core.Common;
using OpenSource1.Core.Enums;

namespace OpenSource1.Application.Data;

/// <summary>Series y número de un borrador nuevo (<see cref="NumeracionBorrador.NumerarBorradorAsync"/>).</summary>
/// <param name="SerieBorradorId">Serie que dio <see cref="Numero"/> (siempre la configurada del tipo de borrador).</param>
/// <param name="SerieRegistroId">Serie con la que se numerará el documento al postear (la elegida o la configurada); solo validada.</param>
/// <param name="Numero">Número emitido por la serie de borradores (su aviso no se devuelve al usuario: NS4).</param>
public sealed record SeriesBorradorNumeradas(Guid SerieBorradorId, Guid SerieRegistroId, NumeroGenerado Numero);

/// <summary>
/// Alta de borradores con series (spec no-series, ruling F10; spec alta rápida): el único punto que resuelve las series de un
/// borrador nuevo y le da número. Lo usan las altas de borradores de factura y de nota y la copia de factura a borrador. La serie de
/// borradores es SIEMPRE la configurada del tipo; la de registro, la configurada o la que elija el Administrador
/// (<see cref="ValidarSeriesSolicitadas"/>, que los llamadores aplican ANTES de abrir la transacción).
/// </summary>
public static class NumeracionBorrador
{
    /// <summary>Código del rechazo de una serie que el usuario no puede elegir (400).</summary>
    public const string CodigoNoEditable = "serie.no_editable";

    /// <summary>
    /// Series que llegan en una petición de alta o de modificación: <paramref name="serieBorradorId"/> nunca se admite (la del
    /// documento en preparación es siempre la configurada) y <paramref name="serieRegistroId"/> solo si <paramref name="esAdministrador"/>.
    /// <see langword="null"/> = la petición es válida en cuanto a series.
    /// </summary>
    public static Error? ValidarSeriesSolicitadas(Guid? serieBorradorId, Guid? serieRegistroId, bool esAdministrador)
    {
        if (serieBorradorId is not null)
        {
            return new Error(
                CodigoNoEditable,
                "La serie del documento en preparación no se elige: se usa la configurada para el tipo de documento.",
                "SerieBorradorId");
        }

        return serieRegistroId is not null && !esAdministrador
            ? new Error(
                CodigoNoEditable,
                "Solo un administrador puede cambiar la serie de registro: se usa la configurada para el tipo de documento.",
                "SerieRegistroId")
            : null;
    }

    /// <summary>
    /// Resuelve y valida las series de un borrador nuevo y numera el borrador, dentro de la transacción del alta:
    /// <list type="number">
    /// <item>Serie de borradores: la configurada para <paramref name="tipoBorrador"/>.</item>
    /// <item>Serie de registro: <paramref name="serieRegistroId"/> o, si es <c>null</c>, la configurada para <paramref name="tipoRegistro"/>.</item>
    /// <item>La de registro se valida (existe, es de <paramref name="tipoRegistro"/> y está activa) ANTES de numerar: un fallo no consume número.</item>
    /// <item>La de borradores numera con <see cref="IGeneradorNumeroDocumento.SiguienteAsync"/> (que valida tipo y actividad) a <paramref name="fecha"/>.</item>
    /// </list>
    /// Errores: sin configuración, <c>numeracion.sin_configuracion</c> en <c>TipoDocumento</c> (tal cual el motor); los errores del motor
    /// sobre una serie (campo <c>SerieId</c>) se devuelven en <c>SerieRegistroId</c> o <c>SerieBorradorId</c>; los demás
    /// (p. ej. <c>numeracion.sin_transaccion</c>) sin cambios. Exige transacción activa (la del alta).
    /// </summary>
    public static async Task<Result<SeriesBorradorNumeradas>> NumerarBorradorAsync(
        this IGeneradorNumeroDocumento generador,
        TipoDocumentoSerie tipoBorrador,
        TipoDocumentoSerie tipoRegistro,
        Guid? serieRegistroId,
        DateOnly fecha,
        CancellationToken cancellationToken = default)
    {
        var configuradaBorrador = await generador.SerieConfiguradaAsync(tipoBorrador, cancellationToken);
        if (!configuradaBorrador.TryObtenerValor(out var serieBorradorId))
        {
            return Result<SeriesBorradorNumeradas>.Fallo(configuradaBorrador);
        }

        if (serieRegistroId is null)
        {
            var configurada = await generador.SerieConfiguradaAsync(tipoRegistro, cancellationToken);
            if (!configurada.TryObtenerValor(out var id))
            {
                return Result<SeriesBorradorNumeradas>.Fallo(configurada);
            }

            serieRegistroId = id;
        }

        var registroValida = await generador.ValidarSerieAsync(serieRegistroId.Value, tipoRegistro, cancellationToken);
        if (registroValida.EsFallo)
        {
            return Result<SeriesBorradorNumeradas>.Fallo(EnCampo(registroValida, "SerieRegistroId"));
        }

        var numero = await generador.SiguienteAsync(serieBorradorId, tipoBorrador, fecha, cancellationToken);
        return numero.TryObtenerValor(out var generado)
            ? Result<SeriesBorradorNumeradas>.Exito(new SeriesBorradorNumeradas(serieBorradorId, serieRegistroId.Value, generado))
            : Result<SeriesBorradorNumeradas>.Fallo(EnCampo(numero, "SerieBorradorId"));
    }

    private static Error[] EnCampo(Result fallo, string campo) =>
        [.. fallo.Errores.Select(e => e.Campo == "SerieId" ? e with { Campo = campo } : e)];
}
