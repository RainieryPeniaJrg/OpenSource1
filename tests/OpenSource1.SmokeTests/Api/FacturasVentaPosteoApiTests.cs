using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using OpenSource1.Application.Features.FacturasVenta.Borradores.Dtos;
using OpenSource1.Application.Features.FacturasVenta.Posteadas.Dtos;
using OpenSource1.Core.Entities.Contabilidad;
using OpenSource1.Core.Enums;
using OpenSource1.SmokeTests.TestInfrastructure;

namespace OpenSource1.SmokeTests.Api;

/// <summary>
/// <c>POST api/facturas-venta/borradores/{id}/postear</c> (Task 6.4) y la guarda de borrado de socios contra Postgres real:
/// 200 con número, total y registro contable; permisos (CanModify); 400 con el campo de la línea; 409 al repetir (el borrador queda Posteada); 409 al borrar un
/// socio con borradores o facturas. REQUIERE DOCKER. Los casos del motor están en <c>PostearFacturaVentaTests</c>.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FacturasVentaPosteoApiTests : IClassFixture<PostgresTestFixture>
{
    private const string Base = "/api/facturas-venta";
    private static readonly DateOnly D10 = new(2026, 9, 10);

    private readonly PostgresTestFixture _fixture;
    private readonly HttpClient _client;

    public FacturasVentaPosteoApiTests(PostgresTestFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateFactory().CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    [Fact]
    public async Task Vendedor_CapturaYPosteaVenta_SinAdministrarUsuariosNiBorrar()
    {
        var socio = await CrearSocioAsync(Admin(), "Cliente vendedor");
        var cuenta = await CrearCuentaAsync(Admin());
        var vendedor = Rol("Vendedor");
        var borrador = await CrearBorradorAsync(vendedor, socio);
        await CrearLineaCuentaAsync(vendedor, borrador.Id, cuenta, 100m);

        Assert.Equal(HttpStatusCode.Forbidden, (await vendedor.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await vendedor.DeleteAsync($"{Base}/borradores/{borrador.Id}")).StatusCode);

        var respuesta = await vendedor.PostAsync($"{Base}/borradores/{borrador.Id}/postear", null);
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, await respuesta.Content.ReadAsStringAsync());
        var json = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(118m, json.GetProperty("importeTotal").GetDecimal());
        var saldo = JsonDocument.Parse(await vendedor.GetStringAsync($"/api/clientes/{socio}/saldo")).RootElement;
        Assert.Equal(118m, saldo.GetProperty("saldo").GetDecimal());
    }

    [Fact]
    public async Task Postear_CanModify_200ConNumeroTotalYRegistro_LaFacturaSeConsulta_Y409AlRepetir()
    {
        var client = Admin();
        var socio = await CrearSocioAsync(client, "Cliente API");
        var cuenta = await CrearCuentaAsync(client);
        var borrador = await CrearBorradorAsync(client, socio);
        await CrearLineaCuentaAsync(client, borrador.Id, cuenta, 100m);

        // Ejecutor (sin CanModify) 403 y anónimo 401, sin tocar nada.
        Assert.Equal(HttpStatusCode.Forbidden, (await Rol("Ejecutor").PostAsync($"{Base}/borradores/{borrador.Id}/postear", null)).StatusCode);
        var anon = new HttpRequestMessage(HttpMethod.Post, $"{Base}/borradores/{borrador.Id}/postear");
        anon.Headers.Add("X-Test-Anonymous", "true");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(anon)).StatusCode);

        var respuesta = await Rol("Supervisor").PostAsync($"{Base}/borradores/{borrador.Id}/postear", null);

        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        var json = JsonDocument.Parse(cuerpo).RootElement;
        Assert.Equal(["numero", "importeTotal", "registroContable", "avisoNumeracion"], json.EnumerateObject().Select(p => p.Name));
        Assert.Equal(JsonValueKind.Null, json.GetProperty("avisoNumeracion").ValueKind);
        var numero = json.GetProperty("numero").GetString()!;
        Assert.Equal(118m, json.GetProperty("importeTotal").GetDecimal());
        var registro = json.GetProperty("registroContable").GetString()!;

        var detalle = (await Admin().GetFromJsonAsync<FacturaVentaDetalleResponse>($"{Base}/{numero}"))!;
        Assert.Equal((borrador.Numero, socio, 100m, 18m, 118m, registro),
            (detalle.Cabecera.NumeroBorrador, detalle.Cabecera.SocioNegocioId, detalle.Cabecera.ImporteSinIva, detalle.Cabecera.ImporteIva,
             detalle.Cabecera.ImporteTotal, detalle.Cabecera.NumeroRegistroContable));
        Assert.Equal(("ITBIS18", 100m, 18m), (detalle.LineasIva[0].IdentificadorIva, detalle.LineasIva[0].BaseImponible, detalle.LineasIva[0].ImporteIva));
        var saldo = JsonDocument.Parse(await Admin().GetStringAsync($"/api/clientes/{socio}/saldo")).RootElement;
        Assert.Equal(118m, saldo.GetProperty("saldo").GetDecimal());

        // El borrador queda Posteada (spec no-series): repetir el posteo -> 409 en Id; consultarlo -> 200 enlazado a la factura.
        await AssertErrorAsync(
            await Admin().PostAsync($"{Base}/borradores/{borrador.Id}/postear", null), HttpStatusCode.Conflict, "Id", "ya se posteó");
        var posteado = (await Admin().GetFromJsonAsync<FacturaVentaBorradorResponse>($"{Base}/borradores/{borrador.Id}"))!;
        Assert.Equal((EstadoFacturaBorrador.Posteada, numero), (posteado.Estado, posteado.FacturaVentaNumero));
        Assert.Equal(borrador.Id, detalle.Cabecera.FacturaVentaBorradorId);
    }

    [Fact]
    public async Task Postear_TotalCeroPorCienPorCientoDeDescuento_200SinRegistroContable_NiSaldoDelCliente()
    {
        var client = Admin();
        var socio = await CrearSocioAsync(client, "Cliente regalo");
        var cuenta = await CrearCuentaAsync(client);
        var borrador = await CrearBorradorAsync(client, socio);
        var linea = await client.PostAsJsonAsync($"{Base}/borradores/{borrador.Id}/lineas", new
        {
            tipo = TipoLineaFactura.CuentaContable, cuentaContableId = cuenta, cantidad = 1m, precioUnitario = 40m, porcentajeDescuentoLinea = 100m,
            grupoIvaProductoId = GrupoContableIds.IvaProductoItbis18
        });
        Assert.True(linea.StatusCode == HttpStatusCode.Created, await linea.Content.ReadAsStringAsync());

        var respuesta = await client.PostAsync($"{Base}/borradores/{borrador.Id}/postear", null);

        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        var json = JsonDocument.Parse(cuerpo).RootElement;
        Assert.Equal(0m, json.GetProperty("importeTotal").GetDecimal());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("registroContable").ValueKind);
        var detalle = (await client.GetFromJsonAsync<FacturaVentaDetalleResponse>($"{Base}/{json.GetProperty("numero").GetString()}"))!;
        Assert.Equal((0m, (long?)null, (string?)null),
            (detalle.Cabecera.ImporteTotal, detalle.Cabecera.RegistroContableId, detalle.Cabecera.NumeroRegistroContable));
        var saldo = JsonDocument.Parse(await client.GetStringAsync($"/api/clientes/{socio}/saldo")).RootElement;
        Assert.Equal(0m, saldo.GetProperty("saldo").GetDecimal());
    }

    [Fact]
    public async Task Postear_400ConElCampoDeLaLinea_OSinLineas_YNadaEscrito()
    {
        var client = Admin();
        var socio = await CrearSocioAsync(client, "Cliente 400");
        var cuenta = await CrearCuentaAsync(client);
        var borrador = await CrearBorradorAsync(client, socio);
        await CrearLineaCuentaAsync(client, borrador.Id, cuenta, 10m);
        await EjecutarSqlAsync("""UPDATE "CuentasContables" SET "PosteoDirecto" = false WHERE "Id" = @Id""", new { Id = cuenta });

        await AssertErrorAsync(
            await client.PostAsync($"{Base}/borradores/{borrador.Id}/postear", null), HttpStatusCode.BadRequest, "Lineas[10000].CuentaContableId",
            "Línea 10000");

        var vacio = await CrearBorradorAsync(client, socio);
        await AssertErrorAsync(await client.PostAsync($"{Base}/borradores/{vacio.Id}/postear", null), HttpStatusCode.BadRequest, "Id");

        await using var conexion = new NpgsqlConnection(_fixture.AppConnectionString);
        Assert.Equal(0L, await conexion.ExecuteScalarAsync<long>(
            """SELECT COUNT(*) FROM "FacturasVenta" WHERE "NumeroBorrador" IN (@A, @B)""", new { A = borrador.Numero, B = vacio.Numero }));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Base}/borradores/{borrador.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"{Base}/borradores/{Guid.NewGuid()}/postear", null)).StatusCode);
    }

    [Fact]
    public async Task BorrarSocio_409ConBorradorVivo_409ConFacturaPosteada_Y204SinUso()
    {
        var client = Admin();
        var socio = await CrearSocioAsync(client, "Con borrador");
        var cuenta = await CrearCuentaAsync(client);
        var borrador = await CrearBorradorAsync(client, socio);

        await AssertErrorAsync(await client.DeleteAsync($"/api/socios-negocio/{socio}"), HttpStatusCode.Conflict, "Id");

        await CrearLineaCuentaAsync(client, borrador.Id, cuenta, 10m);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"{Base}/borradores/{borrador.Id}/postear", null)).StatusCode);
        await AssertErrorAsync(await client.DeleteAsync($"/api/socios-negocio/{socio}"), HttpStatusCode.Conflict, "Id");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/socios-negocio/{socio}")).StatusCode);

        var libre = await CrearSocioAsync(client, "Sin uso");
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/socios-negocio/{libre}")).StatusCode);
    }

    // ----- Helpers -----

    private static async Task<FacturaVentaBorradorResponse> CrearBorradorAsync(HttpClient client, Guid socio)
    {
        var response = await client.PostAsJsonAsync($"{Base}/borradores", new { socioNegocioId = socio, fechaRegistro = D10 });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<FacturaVentaBorradorResponse>())!;
    }

    private static async Task CrearLineaCuentaAsync(HttpClient client, Guid borradorId, Guid cuenta, decimal precio)
    {
        var response = await client.PostAsJsonAsync($"{Base}/borradores/{borradorId}/lineas", new
        {
            tipo = TipoLineaFactura.CuentaContable, cuentaContableId = cuenta, cantidad = 1m, precioUnitario = precio,
            grupoIvaProductoId = GrupoContableIds.IvaProductoItbis18
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    private static async Task<Guid> CrearSocioAsync(HttpClient client, string nombre)
    {
        var response = await client.PostAsJsonAsync("/api/socios-negocio", new { nombreComercial = nombre });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CrearCuentaAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/cuentas-contables", new
        {
            numero = $"48{Random.Shared.Next(10_000, 99_999)}", nombre = "Otros ingresos", tipoCuenta = TipoCuentaContable.Posteo,
            tipoResultado = TipoResultadoCuenta.Resultado, posteoDirecto = true, bloqueada = false, sangria = 1
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }

    private async Task EjecutarSqlAsync(string sql, object parametros)
    {
        await using var conexion = new NpgsqlConnection(_fixture.AppConnectionString);
        await conexion.ExecuteAsync(sql, parametros);
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string campo, string? fragmento = null)
    {
        var cuerpo = await response.Content.ReadAsStringAsync();
        Assert.True(status == response.StatusCode, $"Se esperaba {status} y llegó {response.StatusCode}: {cuerpo}");
        Assert.True(
            JsonDocument.Parse(cuerpo).RootElement.GetProperty("errors").TryGetProperty(campo, out _),
            $"Se esperaba el campo '{campo}' en los errores: {cuerpo}");
        if (fragmento is not null)
        {
            Assert.Contains(fragmento, cuerpo);
        }
    }

    private HttpClient Admin() => Rol("Administrador");

    private HttpClient Rol(string role)
    {
        var client = _client;
        client.DefaultRequestHeaders.Remove("X-Test-Anonymous");
        client.DefaultRequestHeaders.Remove("X-Test-User");
        client.DefaultRequestHeaders.Remove("X-Test-Roles");
        client.DefaultRequestHeaders.Add("X-Test-User", role.ToLowerInvariant());
        client.DefaultRequestHeaders.Add("X-Test-Roles", role);
        return client;
    }
}
