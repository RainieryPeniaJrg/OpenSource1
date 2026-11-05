"""Verify real downloaded XLSX business figures using only Python's standard library."""
from decimal import Decimal
from pathlib import Path
import sys
import xml.etree.ElementTree as ET
from zipfile import ZipFile

kind, filename, invoice_total = sys.argv[1:]
namespace = {"s": "http://schemas.openxmlformats.org/spreadsheetml/2006/main"}
with ZipFile(Path(filename)) as workbook:
    sheet = ET.fromstring(workbook.read("xl/worksheets/sheet1.xml"))
    assert not sheet.findall(".//s:f", namespace), "Export contains executable formulas"
    expected = {
        "ventas": {"H5": Decimal(invoice_total)},
        "inventario": {"E5": Decimal(8)},
        "top-productos": {"F5": Decimal(2), "I5": Decimal(200)},
        "cuentas-por-cobrar": {"J5": Decimal(invoice_total)},
    }[kind]
    for reference, amount in expected.items():
        cell = sheet.find(f".//s:c[@r='{reference}']", namespace)
        assert cell is not None, f"Missing cell {reference}"
        assert cell.get("t", "n") == "n", f"{reference} must be numeric"
        actual = Decimal(cell.find("s:v", namespace).text)
        assert actual == amount, f"{kind} {reference}: {actual} != {amount}"
print(f"Verified {kind}: numeric business figures and no formulas")
