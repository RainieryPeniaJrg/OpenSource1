extern alias BlazorApp;
using System.ComponentModel.DataAnnotations;
using BlazorApp::OpenSource1.Blazor.Components;
using OpenSource1.Core.Enums;

namespace OpenSource1.SmokeTests.Blazor;

/// <summary>
/// <see cref="LineaFacturaForm"/> (Task 6.6): validación por tipo de línea y construcción del cuerpo hacia la API. La API rechaza
/// los campos que no aplican a un tipo (<c>factura.campo_no_aplica</c>), así que el formulario NO debe enviarlos aunque el POST
/// traiga valores sobrantes (p. ej. una cuenta elegida antes de cambiar el tipo).
/// </summary>
public sealed class LineaFacturaFormularioTests
{
    private static List<ValidationResult> Validar(LineaFacturaForm form)
    {
        var resultados = new List<ValidationResult>();
        Validator.TryValidateObject(form, new ValidationContext(form), resultados, validateAllProperties: true);
        return resultados;
    }

    [Fact]
    public void Comentario_SoloEnviaLaDescripcion_AunqueElFormularioTraigaOtrosCampos()
    {
        var form = new LineaFacturaForm
        {
            Tipo = (short)TipoLineaFactura.Comentario,
            Descripcion = "  Entrega en tienda  ",
            CantidadTexto = "3",
            PrecioUnitarioTexto = "10",
            PorcentajeDescuentoTexto = "5",
            AlmacenId = Guid.NewGuid(),
            CuentaContableId = Guid.NewGuid(),
            GrupoIvaProductoId = Guid.NewGuid(),
        };

        var input = form.ToInput(TipoLineaFactura.Comentario, productoId: Guid.NewGuid(), unidadMedidaId: Guid.NewGuid());

        Assert.Equal(TipoLineaFactura.Comentario, input.Tipo);
        Assert.Equal("Entrega en tienda", input.Descripcion);
        Assert.Null(input.ProductoId);
        Assert.Null(input.CuentaContableId);
        Assert.Null(input.AlmacenId);
        Assert.Null(input.UnidadMedidaId);
        Assert.Null(input.Cantidad);
        Assert.Null(input.PrecioUnitario);
        Assert.Null(input.PorcentajeDescuentoLinea);
        Assert.Null(input.GrupoIvaProductoId);
    }

    [Fact]
    public void Producto_SinAlmacen_EnviaNulo_YNoEnviaCuentaNiGrupoIva()
    {
        var productoId = Guid.NewGuid();
        var unidadId = Guid.NewGuid();
        var form = new LineaFacturaForm
        {
            Tipo = (short)TipoLineaFactura.Producto,
            CantidadTexto = "2,5",
            PrecioUnitarioTexto = "12,5",
            PorcentajeDescuentoTexto = "10",
            Descripcion = "",
            CuentaContableId = Guid.NewGuid(),
            GrupoIvaProductoId = Guid.NewGuid(),
        };

        Assert.Empty(Validar(form));
        var input = form.ToInput(TipoLineaFactura.Producto, productoId, unidadId);

        Assert.Equal(productoId, input.ProductoId);
        Assert.Equal(unidadId, input.UnidadMedidaId);
        Assert.Null(input.AlmacenId);
        Assert.Equal(2.5m, input.Cantidad);
        Assert.Equal(12.5m, input.PrecioUnitario);
        Assert.Equal(10m, input.PorcentajeDescuentoLinea);
        Assert.Null(input.Descripcion);
        Assert.Null(input.CuentaContableId);
        Assert.Null(input.GrupoIvaProductoId);
    }

    [Fact]
    public void CuentaContable_EnviaCuentaYGrupoIva_YNoEnviaProductoAlmacenNiUnidad()
    {
        var cuentaId = Guid.NewGuid();
        var grupoIvaId = Guid.NewGuid();
        var form = new LineaFacturaForm
        {
            Tipo = (short)TipoLineaFactura.CuentaContable,
            CuentaContableId = cuentaId,
            GrupoIvaProductoId = grupoIvaId,
            AlmacenId = Guid.NewGuid(),
            CantidadTexto = "1",
            PrecioUnitarioTexto = "1500.50",
        };

        Assert.Empty(Validar(form));
        var input = form.ToInput(TipoLineaFactura.CuentaContable, productoId: Guid.NewGuid(), unidadMedidaId: Guid.NewGuid());

        Assert.Equal(cuentaId, input.CuentaContableId);
        Assert.Equal(grupoIvaId, input.GrupoIvaProductoId);
        Assert.Equal(1m, input.Cantidad);
        Assert.Equal(1500.50m, input.PrecioUnitario);
        Assert.Null(input.PorcentajeDescuentoLinea);
        Assert.Null(input.ProductoId);
        Assert.Null(input.AlmacenId);
        Assert.Null(input.UnidadMedidaId);
    }

    [Fact]
    public void CuentaContable_SinCuentaGrupoIvaNiPrecio_SeñalaLosTresCampos()
    {
        var form = new LineaFacturaForm { Tipo = (short)TipoLineaFactura.CuentaContable, CantidadTexto = "1" };

        var campos = Validar(form).SelectMany(r => r.MemberNames).ToList();

        Assert.Contains(nameof(LineaFacturaForm.CuentaContableId), campos);
        Assert.Contains(nameof(LineaFacturaForm.GrupoIvaProductoId), campos);
        Assert.Contains(nameof(LineaFacturaForm.PrecioUnitarioTexto), campos);
    }

    [Fact]
    public void Comentario_SinDescripcion_EsInvalido()
    {
        var form = new LineaFacturaForm { Tipo = (short)TipoLineaFactura.Comentario, Descripcion = "  " };

        Assert.Contains(Validar(form), r => r.MemberNames.Contains(nameof(LineaFacturaForm.Descripcion)));
    }

    [Theory]
    [InlineData("", "La cantidad es obligatoria.")]
    [InlineData("abc", "INVALIDO")]
    [InlineData("0", "La cantidad debe ser mayor que cero.")]
    public void Producto_ConCantidadInvalida_SeñalaLaCantidad(string cantidad, string mensaje)
    {
        var form = new LineaFacturaForm { Tipo = (short)TipoLineaFactura.Producto, CantidadTexto = cantidad };

        var errores = Validar(form).Where(r => r.MemberNames.Contains(nameof(LineaFacturaForm.CantidadTexto))).ToList();

        // "INVALIDO" = el mensaje de EntradaDecimal para texto no numérico, con la etiqueta de la cantidad.
        var esperado = mensaje == "INVALIDO"
            ? EntradaDecimal.MensajeInvalido.Replace(EntradaDecimal.EtiquetaPorDefecto, "La cantidad", StringComparison.Ordinal)
            : mensaje;
        Assert.Contains(errores, e => e.ErrorMessage == esperado);
    }

    /// <summary>Task 8.4: el precio es obligatorio y mayor que cero en Producto y CuentaContable (para regalar, 100 % de descuento).</summary>
    [Theory]
    [InlineData(TipoLineaFactura.Producto, "", "El precio unitario es obligatorio")]
    [InlineData(TipoLineaFactura.CuentaContable, "", "El precio unitario es obligatorio")]
    [InlineData(TipoLineaFactura.Producto, "0", "100 % de descuento")]
    [InlineData(TipoLineaFactura.CuentaContable, "0.00", "100 % de descuento")]
    [InlineData(TipoLineaFactura.Producto, "-1", "mayor que cero")]
    public void Precio_VacioCeroONegativo_EsInvalido_ConLaAyudaDelDescuento(TipoLineaFactura tipo, string precio, string fragmento)
    {
        var form = new LineaFacturaForm
        {
            Tipo = (short)tipo, CantidadTexto = "1", PrecioUnitarioTexto = precio, PorcentajeDescuentoTexto = "100",
            CuentaContableId = Guid.NewGuid(), GrupoIvaProductoId = Guid.NewGuid(),
        };

        var error = Assert.Single(Validar(form));
        Assert.Equal([nameof(LineaFacturaForm.PrecioUnitarioTexto)], error.MemberNames);
        Assert.Contains(fragmento, error.ErrorMessage);
    }

    [Fact]
    public void Descuento_FueraDeRango_EsInvalido()
    {
        var form = new LineaFacturaForm { Tipo = (short)TipoLineaFactura.Producto, CantidadTexto = "1", PorcentajeDescuentoTexto = "150" };

        Assert.Contains(Validar(form), r => r.MemberNames.Contains(nameof(LineaFacturaForm.PorcentajeDescuentoTexto)));
    }

    [Fact]
    public void AltaFactura_ValidaLaLineaYLasFechas_YEnviaLaCabeceraConLaPrimeraLinea()
    {
        var form = new AltaFacturaForm
        {
            SocioNegocioId = Guid.NewGuid(), Tipo = (short)TipoLineaFactura.Comentario, Descripcion = "Servicio",
            FechaRegistroTexto = "2026-10-01", FechaVencimientoTexto = "no-es-fecha", DescripcionDocumento = " Pedido 7 ", SerieRegistroId = Guid.NewGuid(),
        };

        var errores = Validar(form);
        form.FechaVencimientoTexto = null;
        var usuario = form.ToAlta(TipoLineaFactura.Comentario, null, null, esAdministrador: false);
        var administrador = form.ToAlta(TipoLineaFactura.Comentario, null, null, esAdministrador: true);

        Assert.Contains(errores, e => e.MemberNames.Contains(nameof(AltaFacturaForm.FechaVencimientoTexto)));
        Assert.Equal(("Pedido 7", (Guid?)null, (Guid?)null), (usuario.Descripcion, usuario.AlmacenId, usuario.SerieRegistroId));
        Assert.Null(usuario.SerieBorradorId);
        Assert.Equal(form.SerieRegistroId, administrador.SerieRegistroId);
        Assert.Equal((TipoLineaFactura.Comentario, "Servicio"), (usuario.PrimeraLinea!.Tipo, usuario.PrimeraLinea.Descripcion));
        Assert.Equal(new DateOnly(2026, 10, 1), usuario.FechaRegistro);
    }
}
