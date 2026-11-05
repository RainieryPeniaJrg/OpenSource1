extern alias BlazorApp;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;

namespace OpenSource1.SmokeTests.TestInfrastructure;

/// <summary>
/// Host real de OpenSource1.Blazor (pipeline, router, layout, antiforgery) sin API ni Postgres: los clientes tipados que la
/// prueba necesita se sustituyen por <see cref="Mock{T}"/>. Llamar a <see cref="Simular{T}"/> ANTES del primer
/// <see cref="Cliente"/> (el host se construye con la primera petición). Sin Docker.
/// <para>
/// Navegación (R17): producción fija <c>BlazorDisableThrowNavigationException=true</c> en OpenSource1.Blazor.csproj, pero esa
/// propiedad MSBuild solo llega al runtimeconfig del propio host; este proceso de pruebas corre con el runtimeconfig de
/// OpenSource1.SmokeTests, sin el switch, así que aquí <c>Nav.NavigateTo</c> en SSR LANZA <c>NavigationException</c> (el camino
/// antiguo) y en producción no lanza (redirige al terminar el render). R17 hace el código correcto en ambos: el destino se
/// calcula dentro del try, <c>NavigateTo</c> va DESPUÉS del try/catch (un catch no convierte la redirección en error) y va
/// seguido de <c>return</c> (sin lanzar, el código siguiente se ejecutaría). Límite conocido: estos tests no detectan un
/// <c>return</c> olvidado tras <c>NavigateTo</c>, porque aquí la excepción corta el flujo igualmente; se revisa en código.
/// </para>
/// </summary>
public sealed class BlazorSsrFactory : WebApplicationFactory<BlazorApp::Program>
{
    /// <summary>
    /// Dirección muerta (puerto 9, "discard") para <c>Api:BaseAddress</c>: un cliente tipado no simulado falla rápido en
    /// lugar de llamar a una API real que pudiera estar levantada en la máquina.
    /// </summary>
    public const string DireccionApiMuerta = "http://127.0.0.1:9/";

    private readonly Dictionary<Type, Mock> _simulados = [];
    private bool _iniciado;

    public Mock<T> Simular<T>() where T : class
    {
        if (_simulados.TryGetValue(typeof(T), out var existente))
        {
            return (Mock<T>)existente;
        }

        if (_iniciado)
        {
            throw new InvalidOperationException("Simular<T>() debe llamarse antes de construir el host (primer Cliente() o Services).");
        }

        var mock = new Mock<T>();
        _simulados[typeof(T)] = mock;
        return mock;
    }

    /// <summary>
    /// Cliente del host. <paramref name="permisos"/>: <see langword="null"/> = los permisos coarse del rol
    /// (<see cref="PermisosTestAuthHandler.PermisosDeRol"/>); <c>""</c> = ningún permiso; si no, la lista separada por comas.
    /// </summary>
    public HttpClient Cliente(string roles = "Administrador", string? permisos = null, bool anonimo = false)
    {
        _iniciado = true;
        // Part 1: var cliente = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        // Testing uses the production cookie policy; use a secure virtual TestServer request.
        var cliente = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        if (anonimo)
        {
            cliente.DefaultRequestHeaders.Add(PermisosTestAuthHandler.CabeceraAnonimo, "true");
        }

        cliente.DefaultRequestHeaders.Add(PermisosTestAuthHandler.CabeceraRoles, roles);
        // HttpClient no envía una cabecera con valor vacío: "" viaja como el centinela explícito de "sin permisos".
        var valorPermisos = permisos ?? PermisosTestAuthHandler.PermisosDeRol(roles);
        cliente.DefaultRequestHeaders.Add(
            PermisosTestAuthHandler.CabeceraPermisos,
            valorPermisos.Length == 0 ? PermisosTestAuthHandler.SinPermisos : valorPermisos);
        return cliente;
    }

    /// <summary>El host se construye aquí (primer cliente o primer acceso a <c>Services</c>): a partir de ahí no se admiten simulaciones.</summary>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        _iniciado = true;
        return base.CreateHost(builder);
    }

    /// <summary>
    /// Raíz del repositorio (carpeta que contiene <c>test.slnx</c>), subiendo desde <see cref="AppContext.BaseDirectory"/>.
    /// Helper único de TestInfrastructure: no duplicarlo en otros tests.
    /// </summary>
    public static string RaizRepositorio()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "test.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException(
            $"No se encontró la raíz del repositorio (test.slnx) subiendo desde {AppContext.BaseDirectory}.");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // Raíz de contenido explícita (wwwroot, appsettings del host): no depende del manifiesto de Mvc.Testing ni del alias.
        builder.UseContentRoot(Path.Combine(RaizRepositorio(), "src", "OpenSource1.Blazor"));
        // ApiClientOptions.SectionName = "Api": ningún cliente real puede alcanzar un stack levantado.
        builder.UseSetting("Api:BaseAddress", DireccionApiMuerta);
        builder.ConfigureServices(services =>
        {
            services.AddAuthentication(PermisosTestAuthHandler.Esquema)
                .AddScheme<AuthenticationSchemeOptions, PermisosTestAuthHandler>(PermisosTestAuthHandler.Esquema, _ => { });

            // Autentica con el esquema de prueba; el Challenge/Forbid siguen siendo los de la cookie real (302 a
            // /account/login y /access-denied), igual que en producción.
            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = PermisosTestAuthHandler.Esquema;
                options.DefaultScheme = PermisosTestAuthHandler.Esquema;
                options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultForbidScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            });
        });

        // ConfigureTestServices corre DESPUÉS de los registros de Program.cs: el mock reemplaza al AddHttpClient<…>.
        builder.ConfigureTestServices(services =>
        {
            foreach (var (tipo, mock) in _simulados)
            {
                services.RemoveAll(tipo);
                services.AddSingleton(tipo, mock.Object);
            }
        });
    }
}
