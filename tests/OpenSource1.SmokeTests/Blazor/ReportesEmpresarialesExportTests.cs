extern alias BlazorApp;
using BlazorApp::OpenSource1.Blazor.Reporting;
using ClosedXML.Excel;

namespace OpenSource1.SmokeTests.Blazor;

public sealed class ReportesEmpresarialesExportTests
{
    [Fact]
    public void Excel_PreservaNumerosFiltrosYTextoSinFormulas()
    {
        var tabla = new ReporteTabla("Ventas por período", "Desde: 2026-01-01", ["Cliente", "Neto"],
            [["=1+1", 123.45m]], "Neto: 123.45");
        var archivo = new ReporteEmpresarialDocumentService().Excel(tabla);
        using var libro = new XLWorkbook(new MemoryStream(archivo.Content));
        var hoja = libro.Worksheet(1);
        Assert.Equal("Desde: 2026-01-01", hoja.Cell(2, 1).GetString());
        Assert.Equal("=1+1", hoja.Cell(5, 1).GetString());
        Assert.False(hoja.Cell(5, 1).HasFormula);
        Assert.Equal(123.45m, hoja.Cell(5, 2).GetValue<decimal>());
        Assert.Equal("Neto: 123.45", hoja.Cell(7, 1).GetString());
    }

    [Fact]
    public void Excel_IncluyeTodasLasFilasAunqueSuperenUnaPagina()
    {
        var filas = Enumerable.Range(1, 501).Select(i => new object?[] { i, i * 2m }).ToArray();
        var archivo = new ReporteEmpresarialDocumentService().Excel(new("Inventario", "Corte", ["Producto", "Valor"], filas, "Total"));
        using var libro = new XLWorkbook(new MemoryStream(archivo.Content));
        Assert.Equal(501, libro.Worksheet(1).Cell(505, 1).GetValue<int>());
        Assert.Equal(1002m, libro.Worksheet(1).Cell(505, 2).GetValue<decimal>());
    }

    [Fact]
    public void Pdf_Vacio_EsDocumentoValido()
    {
        var archivo = new ReporteEmpresarialDocumentService().Pdf(new("Cuentas por cobrar", "Corte: 2026-01-01", ["Cliente", "Saldo"], [], "Saldo: 0"));
        Assert.Equal("application/pdf", archivo.ContentType);
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(archivo.Content, 0, 5));
    }
}
