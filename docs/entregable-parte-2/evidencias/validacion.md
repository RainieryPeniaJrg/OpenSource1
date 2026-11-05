# Validation record

Status: closed on 2026-11-04. Fresh results and SHA-256 receipts are in [resultados.json](resultados.json).

## Exact final results

| Execution | Result |
|---|---|
| Complete Release .NET run | 2093 executed: 2092 passed, 1 failed; 35m38s |
| Corrected historical-data fixture: complete `ClientesVistasApiTests` class | 20 passed, 0 failed; 42s |
| Production binary identity before/after the fixture-only retest | All 5 production assemblies and their 5 test-runner copies have identical SHA-256 |
| Verified unique .NET cases | 2093, by reconciling the full run with the corrected fixture retest; not a single all-green full-suite run |
| Complete Chromium regression | 16 passed, 1 intentionally skipped API-outage case; 2.8m |
| Separate controlled API-outage execution | 1 passed; 14.8s; lab API recovered afterward |
| Verified unique browser scenarios | 17; the skipped outage case was executed separately, not counted twice |

The only full-run failure was `AplicacionFechadaAntesDelPago_NoReduceLaFacturaHastaQueElPagoExiste`.
Its setup attempted to create an application before the payment date through the API, which Part2 now correctly rejects.
The corrected test seeds those two historical application rows directly in its dedicated test database and retains the original historical-balance assertions. All 20 tests in its class passed. Runtime application code did not change for this correction, and the binary identity check confirms that the full run and retest used the same production assemblies.

This distinction is deliberate: the evidence proves all 2093 unique cases through the full run plus the fixture-only retest. It does **not** claim a later single full-suite green execution.

```bash
dotnet test test.slnx --configuration Release --no-build --no-restore -m:1 --logger 'trx;LogFileName=parte2-regresion.trx' --results-directory /home/ray/test/.local/parte2-test-results --verbosity minimal
dotnet test tests/OpenSource1.SmokeTests/OpenSource1.SmokeTests.csproj --configuration Release --no-restore -m:1 --filter FullyQualifiedName~ClientesVistasApiTests --logger 'trx;LogFileName=parte2-correccion-fixture.trx' --results-directory /home/ray/test/.local/parte2-test-results --verbosity minimal
python3 scripts/parte2-binarios.py
python3 scripts/parte2-resultados.py
python3 scripts/parte2-evidence-audit.py
```

Full TRX files remain in the private, untracked `.local/parte2-test-results/` directory; the published receipt includes their hashes and raw counters without test payloads.

| Requirement | Evidence |
|---|---|
| Vendedor least privilege | `Vendedor_PuedeCapturarYEmitirFactura_SinEscriturasGlobales`, `Api_Vendedor_PermiteSoloOperacionesDeVenta`, `Ssr_Vendedor_UsaPermisosEspecificosSinModificarGlobal` |
| Existing role compatibility | `Api_PoliticasFactura_ConservanRolesAnteriores` |
| Vendedor upgrade without demo seeding and preserved memberships | `Migracion_SinSemilla_AgregaVendedorSinReemplazarRolesUsuariosOAsignaciones` (both cases) |
| API report authorization | `Api_ProtegeLosCuatroReportes_YRechazaFiltrosInvalidos` |
| Report filters | `RangoInvertido_DevuelveErrorDeDesde`, `EstadoAjenoAlReporte_DevuelveError`, `VistaAcumulada_RechazaDesde`, `MonedaInvalida_ImpideMezclarTotales` |
| Sales totals and dates | `Ventas_IncluyeExtremosYDevoluciones_TotalesSuperanPagina` |
| Inventory history | `Inventario_DerivaCantidadYValorHistoricos_ConFiltros` |
| Product ranking | `TopProductos_NormalizaUnidadYNeteaDevolucion_SinIncluirCuentas` |
| Historical receivables | `CuentasPorCobrar_ConservaCorteClienteYSaldosHistoricos` |
| Numeric XLSX and formula safety | `Excel_PreservaNumerosFiltrosYTextoSinFormulas` |
| Export completeness | `Excel_IncluyeTodasLasFilasAunqueSuperenUnaPagina`, `Exportacion_UsaSnapshotServidorYPreservaFiltros`, `Exportacion_SnapshotIncompleto_RechazaArchivoTruncado` |
| Export consistency during concurrent posting | `Exportacion_ConFacturaConcurrenteEntrePaginas_ConservaSnapshotSinDuplicarNiOmitir` |
| Empty PDF | `Pdf_Vacio_EsDocumentoValido` |
| CxC reclassification | `CuentasDistintas_RevierteCreditoDePagoYAcreditaCuentaFactura`, `MismaCuenta_NoGeneraAsiento` and `CobrosTests` integration cases |
| Overselling rollback | `ReviewFocus5_ExistenciaInsuficienteEnUnaLinea_FallaConSuNumero_YNoDejaNingunaFila` |
| Historical applications remain readable | `AplicacionFechadaAntesDelPago_NoReduceLaFacturaHastaQueElPagoExiste` |
| No hidden200-note/draft cap and exact invoice match | `Resumen_TraeMasDe200NotasYBorradores_Exactos_YAgregaCreditoYDevolucion` |
| Residual inventory value is retained | `Existencias_SoloConExistencia_ConservaValorResidualSinCantidad` |
| Cost batch idempotence | `ReviewFocus3_DosEjecucionesSeguidas_LaSegundaNoInsertaNada` |
| Proxy trust | `Proxy_ConfiaSoloEnDireccionesExplicitasYUnSalto`, `Proxy_DireccionInvalida_FallaCerrado` |
| Real browser workflow and all report downloads | `Parte 2: vendedor, venta, inventario, CxC, rollback, cuatro reportes y exportación` |

## Additional verification

- First targeted report/export/proxy execution: 23 passed, 0 failed.
- Payment/reclassification/vendor-authorization execution: 69 passed, 0 failed.
- Module/navigation and invoice actions after integration: 68 passed, 0 failed.
- Late role migration and credit-note summary: 3 passed, 0 failed.
- Independent read-only source review and separate migration review: no material findings.
- Production Compose parsed successfully; Caddy configuration validated in a disposable container without network access. No public deployment or TLS issuance was performed.
- PowerPoint passed package validation and all 9 slides were rendered and visually inspected; PDF slides included.
- Evidence audit expanded workbook/presentation archives and the HTML report's embedded ZIP; no private lab credentials were found.
- The new GitHub workflow is delivered and its local command sequence was exercised. A remote GitHub Actions execution is not claimed.
