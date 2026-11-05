using System.Globalization;
using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace OpenSource1.Blazor.Reporting;

public sealed record ReporteTabla(string Titulo, string Filtros, IReadOnlyList<string> Columnas,
    IReadOnlyList<object?[]> Filas, string Resumen, long TotalRegistros = 0);

/// <summary>Both downloads use the same filtered rows and authoritative totals as the screen.</summary>
public sealed class ReporteEmpresarialDocumentService
{
    public ReportFile Excel(ReporteTabla tabla)
    {
        using var libro = new XLWorkbook();
        var hoja = libro.Worksheets.Add("Reporte");
        hoja.Cell(1, 1).Value = tabla.Titulo;
        hoja.Cell(2, 1).Value = tabla.Filtros;
        for (var c = 0; c < tabla.Columnas.Count; c++) hoja.Cell(4, c + 1).Value = tabla.Columnas[c];
        for (var r = 0; r < tabla.Filas.Count; r++)
        for (var c = 0; c < tabla.Filas[r].Length; c++)
        {
            var celda = hoja.Cell(r + 5, c + 1);
            switch (tabla.Filas[r][c])
            {
                case decimal numero: celda.Value = numero; celda.Style.NumberFormat.Format = "#,##0.####"; break;
                case int numero: celda.Value = numero; break;
                case long numero: celda.Value = numero; break;
                default: celda.Value = Texto(tabla.Filas[r][c]); break;
            }
        }
        hoja.Cell(tabla.Filas.Count + 6, 1).Value = tabla.Resumen;
        hoja.Row(4).Style.Font.Bold = true;
        hoja.SheetView.FreezeRows(4);
        hoja.Columns().AdjustToContents(1, 80);
        using var buffer = new MemoryStream();
        libro.SaveAs(buffer);
        return new("reporte.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", buffer.ToArray());
    }

    public ReportFile Pdf(ReporteTabla tabla)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var bytes = Document.Create(document => document.Page(page =>
        {
            page.Size(PageSizes.A4.Landscape());
            page.Margin(24);
            page.DefaultTextStyle(x => x.FontSize(8));
            page.Header().Column(c =>
            {
                c.Item().Text(tabla.Titulo).FontSize(18).Bold();
                c.Item().Text(tabla.Filtros);
            });
            page.Content().PaddingVertical(12).Column(c =>
            {
                if (tabla.Filas.Count == 0) c.Item().Text("No hay resultados para estos filtros.");
                else c.Item().Table(t =>
                {
                    t.ColumnsDefinition(columns => { foreach (var _ in tabla.Columnas) columns.RelativeColumn(); });
                    t.Header(h => { foreach (var nombre in tabla.Columnas) h.Cell().Padding(3).Text(nombre).Bold(); });
                    foreach (var fila in tabla.Filas)
                    foreach (var valor in fila) t.Cell().BorderBottom(0.3f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(Texto(valor));
                });
                c.Item().PaddingTop(10).Text(tabla.Resumen).Bold();
            });
            page.Footer().AlignRight().Text(x => { x.Span("Página "); x.CurrentPageNumber(); x.Span(" de "); x.TotalPages(); });
        })).GeneratePdf();
        return new("reporte.pdf", "application/pdf", bytes);
    }

    public static string Texto(object? valor) => valor switch
    {
        decimal numero => numero.ToString("N4", CultureInfo.GetCultureInfo("es-DO")),
        DateOnly fecha => fecha.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => Convert.ToString(valor, CultureInfo.InvariantCulture) ?? ""
    };
}
