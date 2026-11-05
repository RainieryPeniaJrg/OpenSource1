using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.Mvc;
using OpenSource1.Application.Services.Auth.Dtos;
using OpenSource1.Application.Security;
using OpenSource1.Application.Storage;
using OpenSource1.Blazor.Components;
using OpenSource1.Blazor.Navigation;
using OpenSource1.Blazor.Reporting;
using OpenSource1.Blazor.Security;
using OpenSource1.Blazor.Services;
using QuestPDF.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProductionHttpSecurity(builder.Configuration);
if (!builder.Environment.IsDevelopment())
    builder.Services.AddAntiforgery(options => options.Cookie.SecurePolicy = CookieSecurePolicy.Always);

QuestPDF.Settings.License = LicenseType.Community;

// Add services to the container.
builder.Services.AddRazorComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSingleton<ReporteEmpresarialDocumentService>();
builder.Services.AddHttpClient<ReportesEmpresarialesApiClient>((services, client) =>
    client.BaseAddress = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiClientOptions>>().Value.BaseAddress)
    .AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddSession(options =>
{
    options.Cookie.Name = "OpenSource1.Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    // Part 1: options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.IdleTimeout = TimeSpan.FromMinutes(30);
});

builder.Services.Configure<ApiClientOptions>(builder.Configuration.GetSection(ApiClientOptions.SectionName));
builder.Services.AddScoped<BearerTokenHandler>();
builder.Services.AddSingleton<IFileStorageService, LocalFileStorageService>();
builder.Services.AddSingleton<IReportDocumentService, QuestPdfReportDocumentService>();
builder.Services.AddHttpClient<IAuthApiClient, AuthApiClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiClientOptions>>().Value;
    client.BaseAddress = options.BaseAddress;
}).AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<IUserAdminApiClient, UserAdminApiClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiClientOptions>>().Value;
    client.BaseAddress = options.BaseAddress;
}).AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<ISocioNegocioApiClient, SocioNegocioApiClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiClientOptions>>().Value;
    client.BaseAddress = options.BaseAddress;
}).AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<IProductoApiClient, ProductoApiClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiClientOptions>>().Value;
    client.BaseAddress = options.BaseAddress;
}).AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<ITerminoPagoApiClient, TerminoPagoApiClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiClientOptions>>().Value;
    client.BaseAddress = options.BaseAddress;
}).AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<IUnidadMedidaApiClient, UnidadMedidaApiClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiClientOptions>>().Value;
    client.BaseAddress = options.BaseAddress;
}).AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<ICategoriaProductoApiClient, CategoriaProductoApiClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiClientOptions>>().Value;
    client.BaseAddress = options.BaseAddress;
}).AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<IAlmacenApiClient, AlmacenApiClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiClientOptions>>().Value;
    client.BaseAddress = options.BaseAddress;
}).AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<IDiarioInventarioApiClient, DiarioInventarioApiClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiClientOptions>>().Value;
    client.BaseAddress = options.BaseAddress;
}).AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<ICuentaContableApiClient, CuentaContableApiClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiClientOptions>>().Value;
    client.BaseAddress = options.BaseAddress;
}).AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<IGrupoContableApiClient, GrupoContableApiClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiClientOptions>>().Value;
    client.BaseAddress = options.BaseAddress;
}).AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<IGrupoClienteContableApiClient, GrupoClienteContableApiClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiClientOptions>>().Value;
    client.BaseAddress = options.BaseAddress;
}).AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<ISetupContableApiClient, SetupContableApiClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiClientOptions>>().Value;
    client.BaseAddress = options.BaseAddress;
}).AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<IContabilidadApiClient, ContabilidadApiClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiClientOptions>>().Value;
    client.BaseAddress = options.BaseAddress;
}).AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<IFacturaVentaApiClient, FacturaVentaApiClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiClientOptions>>().Value;
    client.BaseAddress = options.BaseAddress;
}).AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<INotaCreditoVentaApiClient, NotaCreditoVentaApiClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiClientOptions>>().Value;
    client.BaseAddress = options.BaseAddress;
}).AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<ICobroApiClient, CobroApiClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiClientOptions>>().Value;
    client.BaseAddress = options.BaseAddress;
}).AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<IInventarioConsultasApiClient, InventarioConsultasApiClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiClientOptions>>().Value;
    client.BaseAddress = options.BaseAddress;
}).AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<IClientesConsultasApiClient, ClientesConsultasApiClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiClientOptions>>().Value;
    client.BaseAddress = options.BaseAddress;
}).AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<IContabilidadConsultasApiClient, ContabilidadConsultasApiClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiClientOptions>>().Value;
    client.BaseAddress = options.BaseAddress;
}).AddHttpMessageHandler<BearerTokenHandler>();
builder.Services.AddHttpClient<IFechasRegistroApiClient, FechasRegistroApiClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiClientOptions>>().Value;
    client.BaseAddress = options.BaseAddress;
}).AddHttpMessageHandler<BearerTokenHandler>();

builder.Services.AddHttpClient<ISerieApiClient, SerieApiClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiClientOptions>>().Value;
    client.BaseAddress = options.BaseAddress;
}).AddHttpMessageHandler<BearerTokenHandler>();

builder.Services.AddHttpClient<IConfiguracionNumeracionApiClient, ConfiguracionNumeracionApiClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiClientOptions>>().Value;
    client.BaseAddress = options.BaseAddress;
}).AddHttpMessageHandler<BearerTokenHandler>();

builder.Services.AddHttpClient<IBusquedaApiClient, BusquedaApiClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiClientOptions>>().Value;
    client.BaseAddress = options.BaseAddress;
}).AddHttpMessageHandler<BearerTokenHandler>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "OpenSource1.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        // Part 1: options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        options.LoginPath = "/account/login";
        options.LogoutPath = "/account/logout";
        options.AccessDeniedPath = "/access-denied";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
    });

builder.Services.AddAuthorization(PoliticasBlazor.Configurar);
builder.Services.AddScoped<IRegistroModulos, RegistroModulos>();
builder.Services.AddScoped<IIndicadoresModulos, IndicadoresModulos>();

var app = builder.Build();
app.UseForwardedHeaders();
app.MapHealthChecks("/health/live");
app.MapReportesEmpresariales();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
var uploadsRoot = Path.Combine(app.Environment.ContentRootPath, "storage", "uploads");
Directory.CreateDirectory(uploadsRoot);

app.UseRouting();
app.UseSession();
app.UseAuthentication();
app.UseAuthorization();

// "/uploads" es contenido privado (avatares, imágenes de clientes y productos): solo para usuarios
// autenticados. El middleware de ficheros va DESPUÉS de UseAuthentication()/UseAuthorization() a propósito,
// y esta rama corta la petición antes de llegar a él. Sin autenticar -> mismo Challenge que usan las páginas
// (redirige a /account/login) y el fichero NO se sirve. Basta con estar autenticado: no se exige ningún
// permiso (CanConsult, etc.), porque el avatar del propio usuario debe poder verse siempre.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/uploads") && context.User.Identity?.IsAuthenticated != true)
    {
        await context.ChallengeAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return;
    }

    await next();
});
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(uploadsRoot),
    RequestPath = "/uploads",
    OnPrepareResponse = context =>
    {
        // Las subidas son contenido de usuario: el navegador no debe "adivinar" otro tipo distinto del declarado.
        context.Context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        // Contenido privado del usuario autenticado: ningún proxy/caché intermedio debe guardarlo.
        context.Context.Response.Headers.CacheControl = "private, no-store";
    }
});

app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true || context.Request.Path.StartsWithSegments("/account"))
    {
        context.Response.Headers.CacheControl = "no-store, no-cache, max-age=0, must-revalidate";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.Expires = "0";
    }

    await next();
});
app.UseAntiforgery();

app.MapPost("/account/logout", async (HttpContext httpContext) =>
{
    httpContext.Session.Clear();
    await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    httpContext.Response.Cookies.Delete("OpenSource1.Session", new CookieOptions { Path = "/" });
    httpContext.Response.Headers["Clear-Site-Data"] = "\"cache\", \"cookies\", \"storage\"";
    return Results.Redirect("/");
}).RequireAuthorization(ApplicationPolicies.CanConsult);

app.MapPost("/account/profile/image", async (
    HttpContext httpContext,
    IAntiforgery antiforgery,
    IAuthApiClient authApiClient,
    IFileStorageService fileStorageService) =>
{
    // Este handler lee el formulario a mano (no enlaza [FromForm]/IFormFile), así que el middleware de antiforgery no lo valida
    // por sí solo: se valida el token explícitamente. Sin token (o inválido) -> 400, sin tocar el perfil.
    try
    {
        await antiforgery.ValidateRequestAsync(httpContext);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.BadRequest();
    }

    var currentUser = await authApiClient.GetCurrentUserAsync();
    if (currentUser is null)
    {
        return Results.Redirect("/account/login");
    }

    string? nueva = null;
    try
    {
        // La imagen vigente viene del perfil guardado en el servidor (no del formulario).
        var anterior = currentUser.ProfileImagePath;
        nueva = await fileStorageService.SaveProfileImageAsync(httpContext, "ProfileImage", anterior);
        var (success, _) = await authApiClient.UpdateProfileImageAsync(new UpdateProfileImageRequest(nueva));
        if (success)
        {
            // Solo tras confirmar el cambio se retira la imagen anterior; si la API lo rechazó, se descarta la nueva.
            if (nueva != anterior) await fileStorageService.DeleteIfExistsAsync(anterior, RutaImagen.CarpetaUsuarios);
        }
        else if (nueva is not null && nueva != anterior)
        {
            await fileStorageService.DeleteIfExistsAsync(nueva, RutaImagen.CarpetaUsuarios);
        }

        return Results.Redirect("/account/profile");
    }
    catch
    {
        if (nueva is not null && nueva != currentUser.ProfileImagePath)
        {
            await fileStorageService.DeleteIfExistsAsync(nueva, RutaImagen.CarpetaUsuarios);
        }

        return Results.Redirect("/account/profile");
    }
}).RequireAuthorization(ApplicationPolicies.CanConsult);

app.MapPost("/reports/clientes/selected", async (
    [FromForm] ClienteSelectionReportForm? form,
    ISocioNegocioApiClient clienteApiClient,
    IReportDocumentService reportDocumentService) =>
{
    var selectedIds = (form?.SelectedIds ?? []).Where(id => id != Guid.Empty).Distinct().ToArray();
    if (selectedIds.Length == 0)
    {
        return Results.Redirect("/clientes?ok=report-select-required");
    }

    var clientes = new List<OpenSource1.Application.Features.SociosNegocio.Dtos.SocioNegocioResponse>();
    foreach (var id in selectedIds)
    {
        var item = await clienteApiClient.GetByIdAsync(id);
        if (item is not null)
        {
            clientes.Add(item);
        }
    }

    if (clientes.Count == 0)
    {
        return Results.Redirect("/clientes?ok=report-no-data");
    }

    var file = reportDocumentService.GenerateClientesReport(clientes, "Reporte de Clientes Seleccionados");
    return Results.File(file.Content, file.ContentType, file.FileName);
}).RequireAuthorization(ApplicationPolicies.CanConsult);

app.MapPost("/reports/clientes/selected.xlsx", async (
    [FromForm] ClienteSelectionReportForm? form,
    ISocioNegocioApiClient clienteApiClient,
    IReportDocumentService reportDocumentService) =>
{
    var selectedIds = (form?.SelectedIds ?? []).Where(id => id != Guid.Empty).Distinct().ToArray();
    if (selectedIds.Length == 0)
    {
        return Results.Redirect("/clientes?ok=report-select-required");
    }

    var clientes = new List<OpenSource1.Application.Features.SociosNegocio.Dtos.SocioNegocioResponse>();
    foreach (var id in selectedIds)
    {
        var item = await clienteApiClient.GetByIdAsync(id);
        if (item is not null)
        {
            clientes.Add(item);
        }
    }

    if (clientes.Count == 0)
    {
        return Results.Redirect("/clientes?ok=report-no-data");
    }

    var file = reportDocumentService.GenerateClientesExcel(clientes, "Reporte de Clientes Seleccionados");
    return Results.File(file.Content, file.ContentType, file.FileName);
}).RequireAuthorization(ApplicationPolicies.CanConsult);

app.MapPost("/reports/productos/selected", async (
    [FromForm] ProductoSelectionReportForm? form,
    IProductoApiClient productoApiClient,
    IReportDocumentService reportDocumentService) =>
{
    var selectedIds = (form?.SelectedIds ?? []).Where(id => id != Guid.Empty).Distinct().ToArray();
    if (selectedIds.Length == 0)
    {
        return Results.Redirect("/productos?ok=report-select-required");
    }

    var productos = new List<OpenSource1.Application.Features.Productos.Dtos.ProductoResponse>();
    foreach (var id in selectedIds)
    {
        var item = await productoApiClient.GetByIdAsync(id);
        if (item is not null)
        {
            productos.Add(item);
        }
    }

    if (productos.Count == 0)
    {
        return Results.Redirect("/productos?ok=report-no-data");
    }

    productos = form?.StockState switch
    {
        "with" => productos.Where(x => x.Existencia > 0).ToList(),
        "without" => productos.Where(x => x.Existencia <= 0).ToList(),
        _ => productos
    };

    if (productos.Count == 0)
    {
        return Results.Redirect("/productos?ok=report-no-data");
    }

    var file = reportDocumentService.GenerateProductosReport(productos, "Reporte de Productos Seleccionados");
    return Results.File(file.Content, file.ContentType, file.FileName);
}).RequireAuthorization(ApplicationPolicies.CanConsult);

app.MapPost("/reports/productos/selected.xlsx", async (
    [FromForm] ProductoSelectionReportForm? form,
    IProductoApiClient productoApiClient,
    IReportDocumentService reportDocumentService) =>
{
    var selectedIds = (form?.SelectedIds ?? []).Where(id => id != Guid.Empty).Distinct().ToArray();
    if (selectedIds.Length == 0)
    {
        return Results.Redirect("/productos?ok=report-select-required");
    }

    var productos = new List<OpenSource1.Application.Features.Productos.Dtos.ProductoResponse>();
    foreach (var id in selectedIds)
    {
        var item = await productoApiClient.GetByIdAsync(id);
        if (item is not null)
        {
            productos.Add(item);
        }
    }

    if (productos.Count == 0)
    {
        return Results.Redirect("/productos?ok=report-no-data");
    }

    productos = form?.StockState switch
    {
        "with" => productos.Where(x => x.Existencia > 0).ToList(),
        "without" => productos.Where(x => x.Existencia <= 0).ToList(),
        _ => productos
    };

    if (productos.Count == 0)
    {
        return Results.Redirect("/productos?ok=report-no-data");
    }

    var file = reportDocumentService.GenerateProductosExcel(productos, "Reporte de Productos Seleccionados");
    return Results.File(file.Content, file.ContentType, file.FileName);
}).RequireAuthorization(ApplicationPolicies.CanConsult);

app.MapPost("/reports/clientes/history", async (
    [FromForm] ClienteHistoricalReportForm? form,
    ISocioNegocioApiClient clienteApiClient,
    IReportDocumentService reportDocumentService) =>
{
    var clientes = await clienteApiClient.ListAllAsync();
    if (form?.Status == "inactive")
    {
        clientes = [];
    }
    if (clientes.Count == 0)
    {
        return Results.Redirect("/reporteria?entity=clientes&ok=report-no-data");
    }

    var file = reportDocumentService.GenerateClientesReport(clientes, "Reporte Histórico de Clientes");
    return Results.File(file.Content, file.ContentType, file.FileName);
}).RequireAuthorization(ApplicationPolicies.CanConsult);

app.MapPost("/reports/clientes/history.xlsx", async (
    [FromForm] ClienteHistoricalReportForm? form,
    ISocioNegocioApiClient clienteApiClient,
    IReportDocumentService reportDocumentService) =>
{
    var clientes = await clienteApiClient.ListAllAsync();
    if (form?.Status == "inactive")
    {
        clientes = [];
    }
    if (clientes.Count == 0)
    {
        return Results.Redirect("/reporteria?entity=clientes&ok=report-no-data");
    }

    var file = reportDocumentService.GenerateClientesExcel(clientes, "Reporte Histórico de Clientes");
    return Results.File(file.Content, file.ContentType, file.FileName);
}).RequireAuthorization(ApplicationPolicies.CanConsult);

app.MapPost("/reports/productos/history", async (
    [FromForm] ProductoHistoricalReportForm? form,
    IProductoApiClient productoApiClient,
    IReportDocumentService reportDocumentService) =>
{
    var productos = await productoApiClient.ListAllAsync();
    productos = form?.StockState switch
    {
        "with" => productos.Where(x => x.Existencia > 0).ToList(),
        "without" => productos.Where(x => x.Existencia <= 0).ToList(),
        _ => productos
    };
    if (productos.Count == 0)
    {
        return Results.Redirect("/reporteria?entity=productos&ok=report-no-data");
    }

    var file = reportDocumentService.GenerateProductosReport(productos, "Reporte Histórico de Productos");
    return Results.File(file.Content, file.ContentType, file.FileName);
}).RequireAuthorization(ApplicationPolicies.CanConsult);

app.MapPost("/reports/productos/history.xlsx", async (
    [FromForm] ProductoHistoricalReportForm? form,
    IProductoApiClient productoApiClient,
    IReportDocumentService reportDocumentService) =>
{
    var productos = await productoApiClient.ListAllAsync();
    productos = form?.StockState switch
    {
        "with" => productos.Where(x => x.Existencia > 0).ToList(),
        "without" => productos.Where(x => x.Existencia <= 0).ToList(),
        _ => productos
    };
    if (productos.Count == 0)
    {
        return Results.Redirect("/reporteria?entity=productos&ok=report-no-data");
    }

    var file = reportDocumentService.GenerateProductosExcel(productos, "Reporte Histórico de Productos");
    return Results.File(file.Content, file.ContentType, file.FileName);
}).RequireAuthorization(ApplicationPolicies.CanConsult);

app.MapGet("/reports/clientes/raw.xlsx", async (
    ISocioNegocioApiClient clienteApiClient,
    IReportDocumentService reportDocumentService) =>
{
    var clientes = await clienteApiClient.ListAllAsync();
    var file = reportDocumentService.GenerateClientesRawExcel(clientes);
    return Results.File(file.Content, file.ContentType, file.FileName);
}).RequireAuthorization();

app.MapGet("/reports/productos/raw.xlsx", async (
    IProductoApiClient productoApiClient,
    IReportDocumentService reportDocumentService) =>
{
    var productos = await productoApiClient.ListAllAsync();
    var file = reportDocumentService.GenerateProductosRawExcel(productos);
    return Results.File(file.Content, file.ContentType, file.FileName);
}).RequireAuthorization();

// Paleta Ctrl+K (Fix-Features A4): JSON mínimo del host. El navegador nunca habla con la API: el host la llama con la sesión
// (BearerTokenHandler) y devuelve solo los resultados. 400 = consulta fuera de 2–100; 502 = la API no respondió.
app.MapGet("/buscar/sugerencias", async (string? q, HttpContext http, IBusquedaApiClient busqueda, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    // Nombres y RNC/cédula de clientes: no-store explícito en el propio endpoint (no depende solo del middleware global de
    // caché para usuarios autenticados, que podría cambiar).
    http.Response.Headers.CacheControl = "no-store";
    var termino = q?.Trim() ?? string.Empty;
    if (termino.Length is < 2 or > 100)
    {
        return Results.BadRequest();
    }

    try
    {
        var resultado = await busqueda.BuscarAsync(termino, 5, cancellationToken);
        return resultado.Succeeded ? Results.Json(resultado.Valor) : Results.StatusCode(StatusCodes.Status502BadGateway);
    }
    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
    {
        logger.LogWarning(ex, "No fue posible obtener sugerencias de búsqueda.");
        return Results.StatusCode(StatusCodes.Status502BadGateway);
    }
}).RequireAuthorization(ApplicationPolicies.CanConsult);

app.MapStaticAssets();
app.MapRazorComponents<App>();

app.Run();

public partial class Program { }
