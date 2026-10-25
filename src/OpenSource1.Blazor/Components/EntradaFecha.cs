using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace OpenSource1.Blazor.Components;

/// <summary>
/// Lectura de fechas de un <c>&lt;input type="date"&gt;</c>. Mismo motivo que <see cref="EntradaDecimal"/>: el
/// binder de formularios SSR de Blazor interpreta los tipos primitivos (incluido <see cref="DateOnly"/>) con la
/// cultura del servidor, y un <c>&lt;input type="date"&gt;</c> siempre envía <c>yyyy-MM-dd</c> (formato HTML,
/// independiente del idioma mostrado en el navegador) sin importar la cultura configurada en el servidor; por
/// eso la fecha viaja como texto y se interpreta aquí con <see cref="CultureInfo.InvariantCulture"/> y el formato
/// exacto, en vez de depender del binder.
/// </summary>
public static class EntradaFecha
{
    public const string MensajePorDefecto = "La fecha no es válida.";

    /// <summary>Vacío o solo espacios = no informada (false, sin error: la obligatoriedad la valida el llamador).</summary>
    public static bool TryParse(string? texto, out DateOnly valor, out string? error, string etiqueta = "La fecha")
    {
        valor = default;
        error = null;

        if (string.IsNullOrWhiteSpace(texto))
        {
            return false;
        }

        if (DateOnly.TryParseExact(texto.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out valor))
        {
            return true;
        }

        error = $"{etiqueta} no es válida.";
        return false;
    }

    /// <summary>Fecha opcional de un formulario: la fecha si el texto es válido; vacío o inválido = <see langword="null"/>.</summary>
    public static DateOnly? Opcional(string? texto) => TryParse(texto, out var valor, out _) ? valor : null;

    /// <summary>
    /// Validación de las fechas de un formulario (<c>IValidatableObject.Validate</c>): un <see cref="ValidationResult"/> por cada
    /// fecha informada que no es válida ("{etiqueta} no es válida.", sobre <c>campo</c>). Las vacías no fallan (la obligatoriedad
    /// la valida el formulario).
    /// </summary>
    public static IEnumerable<ValidationResult> Validar(params (string? Texto, string Campo, string Etiqueta)[] fechas)
    {
        foreach (var (texto, campo, etiqueta) in fechas)
        {
            if (!string.IsNullOrWhiteSpace(texto) && !TryParse(texto, out _, out var error, etiqueta))
            {
                yield return new ValidationResult(error ?? $"{etiqueta} no es válida.", [campo]);
            }
        }
    }

    /// <summary>Formato de edición esperado por <c>&lt;input type="date"&gt;</c>.</summary>
    public static string Formatear(DateOnly valor) => valor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Formatear(DateOnly? valor) => valor is { } v ? Formatear(v) : string.Empty;
}
