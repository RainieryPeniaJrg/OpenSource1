extern alias BlazorApp;
using BlazorApp::OpenSource1.Blazor.Components;

namespace OpenSource1.SmokeTests.Blazor;

public sealed class EntradaFechaTests
{
    [Theory]
    [InlineData("2026-09-25", 2026, 9, 25)]
    [InlineData("2000-01-01", 2000, 1, 1)]
    [InlineData("2026-12-31", 2026, 12, 31)]
    public void TryParse_AceptaElFormatoIsoDeUnInputDate(string texto, int anio, int mes, int dia)
    {
        Assert.True(EntradaFecha.TryParse(texto, out var valor, out var error), error);
        Assert.Equal(new DateOnly(anio, mes, dia), valor);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_VacioOEspacios_NoEsError_YDevuelveFalseSinValor(string? texto)
    {
        Assert.False(EntradaFecha.TryParse(texto, out var valor, out var error));
        Assert.Null(error);
        Assert.Equal(default, valor);
    }

    [Theory]
    [InlineData("25/09/2026")]
    [InlineData("09-25-2026")]
    [InlineData("2026/09/25")]
    [InlineData("abc")]
    [InlineData("2026-13-01")]
    [InlineData("2026-09-32")]
    public void TryParse_FormatoDistintoAIso_SeRechazaConMensaje(string texto)
    {
        Assert.False(EntradaFecha.TryParse(texto, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParse_ConEtiqueta_UsaElSujetoIndicadoEnElMensaje()
    {
        Assert.False(EntradaFecha.TryParse("no-es-fecha", out _, out var error, "La fecha de registro"));
        Assert.StartsWith("La fecha de registro", error);
    }

    [Fact]
    public void Formatear_ProduceElFormatoIsoQueEsperaUnInputDate()
    {
        Assert.Equal("2026-09-25", EntradaFecha.Formatear(new DateOnly(2026, 9, 25)));
    }

    [Fact]
    public void Formatear_Nullable_VacioCuandoNoHayValor()
    {
        Assert.Equal(string.Empty, EntradaFecha.Formatear((DateOnly?)null));
        Assert.Equal("2026-01-01", EntradaFecha.Formatear((DateOnly?)new DateOnly(2026, 1, 1)));
    }

    [Fact]
    public void Opcional_FechaValidaONull()
    {
        Assert.Equal(new DateOnly(2026, 10, 1), EntradaFecha.Opcional("2026-10-01"));
        Assert.Null(EntradaFecha.Opcional(null));
        Assert.Null(EntradaFecha.Opcional("  "));
        Assert.Null(EntradaFecha.Opcional("no-es-fecha"));
    }

    [Fact]
    public void Validar_SoloLasInformadasEInvalidas_ConElCampoYLaEtiqueta()
    {
        var errores = EntradaFecha.Validar(
            ("2026-10-01", "FechaRegistroTexto", "La fecha de registro"),
            (null, "FechaDocumentoTexto", "La fecha de documento"),
            ("31/10/2026", "FechaVencimientoTexto", "La fecha de vencimiento")).ToList();

        var error = Assert.Single(errores);
        Assert.Equal(["FechaVencimientoTexto"], error.MemberNames);
        Assert.Equal("La fecha de vencimiento no es válida.", error.ErrorMessage);
    }
}
