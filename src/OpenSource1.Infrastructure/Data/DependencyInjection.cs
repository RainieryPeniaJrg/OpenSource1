using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenSource1.Application.Data;
using OpenSource1.Application.Data.Repositories;
using OpenSource1.Application.Data.UnitOfWork;
using OpenSource1.Application.Features.SociosNegocio;
using OpenSource1.Application.Features.Busqueda;
using OpenSource1.Application.Features.Almacenes;
using OpenSource1.Application.Features.Cobros;
using OpenSource1.Application.Features.CategoriasProducto;
using OpenSource1.Application.Features.ConfiguracionNumeracion;
using OpenSource1.Application.Features.Contabilidad;
using OpenSource1.Application.Features.FacturasVenta.Posteadas;
using OpenSource1.Application.Features.Inventario.Consultas;
using OpenSource1.Application.Features.MovimientosCliente;
using OpenSource1.Application.Features.CuentasContables;
using OpenSource1.Application.Features.DiariosInventario.Lineas;
using OpenSource1.Application.Features.DiariosInventario.Lotes;
using OpenSource1.Application.Features.DiariosInventario.Plantillas;
using OpenSource1.Application.Features.DiariosInventario.Registros;
using OpenSource1.Application.Features.FacturasVenta.Borradores;
using OpenSource1.Application.Features.FacturasVenta.Posteo;
using OpenSource1.Application.Features.FechasRegistro;
using OpenSource1.Application.Features.NotasCreditoVenta;
using OpenSource1.Application.Features.NotasCreditoVenta.Borradores;
using OpenSource1.Application.Features.NotasCreditoVenta.Posteadas;
using OpenSource1.Application.Features.GruposClienteContable;
using OpenSource1.Application.Features.GruposContables;
using OpenSource1.Application.Features.Productos;
using OpenSource1.Application.Features.Series;
using OpenSource1.Application.Features.SetupsContables;
using OpenSource1.Application.Features.TerminosPago;
using OpenSource1.Application.Features.UnidadesMedida;
using OpenSource1.Application.Features.Users;
using OpenSource1.Application.Services.Auth;
using OpenSource1.Application.Services.Clientes;
using OpenSource1.Application.Services.Contabilidad;
using OpenSource1.Application.Services.Inventario;
using OpenSource1.Application.Services.Registro;
using OpenSource1.Infrastructure.Data.Repositories;
using OpenSource1.Infrastructure.Data.Queries;
using OpenSource1.Infrastructure.Data.UnitOfWork;
using OpenSource1.Infrastructure.Identity;
using OpenSource1.Infrastructure.Services.Auth;
using OpenSource1.Infrastructure.Services.Clientes;
using OpenSource1.Infrastructure.Services.Contabilidad;
using OpenSource1.Infrastructure.Services.Inventario;
using OpenSource1.Infrastructure.Services.Registro;
using OpenSource1.Infrastructure.Services.Users;

namespace OpenSource1.Infrastructure.Data;

public static class DependencyInjection
{
    public static IServiceCollection AddApplicationData(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.SectionName));

        // Registro global (a nivel de proceso) del mapeo Dapper para DateOnly: Dapper no lo infiere
        // por sí solo. Vive aquí, y no en el constructor estático de una clase de negocio concreta,
        // para que cualquier repositorio Dapper con un parámetro DateOnly lo tenga disponible sin
        // depender de cuál se resuelva primero. Ver DapperDateOnlyTypeHandler.
        SqlMapper.AddTypeHandler(new DapperDateOnlyTypeHandler());

        services.AddScoped<DbSession>();
        services.AddScoped<IDbSession>(sp => sp.GetRequiredService<DbSession>());

        services.AddDbContext<ApplicationDbContext>((sp, options) =>
            options.UseNpgsql(sp.GetRequiredService<DbSession>().Connection));

        // La base de Identity mantiene su propia conexión: es otra base de datos.
        // La cadena de conexión se lee vía IConfiguration resuelto de DI en la fábrica del
        // DbContext (no en una variable capturada aquí durante el registro): igual que
        // DbSession con "DefaultConnection", esto difiere la lectura hasta que el DbContext se
        // construye realmente, después de que WebApplicationFactory haya fusionado su
        // configuración de prueba. Leer "IdentityConnection" de forma eager en este método (como
        // hacía antes) capturaba siempre el valor real de appsettings.json, igual que el defecto
        // de validación eager de Jwt:SigningKey en Identity/DependencyInjection.cs.
        services.AddDbContext<AppIdentityDbContext>((sp, options) =>
        {
            var identityConnectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString("IdentityConnection")
                ?? throw new InvalidOperationException("Connection string 'IdentityConnection' was not found.");

            options.UseNpgsql(identityConnectionString);
        });

        services.AddScoped<ISocioNegocioReadRepository, DapperSocioNegocioReadRepository>();
        services.AddScoped<IProductoReadRepository, DapperProductoReadRepository>();
        services.AddScoped<ITerminoPagoReadRepository, DapperTerminoPagoReadRepository>();
        services.AddScoped<IUnidadMedidaReadRepository, DapperUnidadMedidaReadRepository>();
        services.AddScoped<ICategoriaProductoReadRepository, DapperCategoriaProductoReadRepository>();
        services.AddScoped<IAlmacenReadRepository, DapperAlmacenReadRepository>();
        services.AddScoped<ICuentaContableReadRepository, DapperCuentaContableReadRepository>();
        services.AddScoped<ICuentaContableUsoService, CuentaContableUsoService>();
        services.AddScoped<IGrupoContableReadRepository, DapperGrupoContableReadRepository>();
        services.AddScoped<IGrupoClienteContableReadRepository, DapperGrupoClienteContableReadRepository>();
        services.AddScoped<IGrupoContableUsoService, GrupoContableUsoService>();
        services.AddScoped<IDerivadorCuentas, DerivadorCuentas>();
        services.AddScoped<IRegistroContable, RegistroContable>();
        services.AddScoped<IPosteoCostoInventario, PosteoCostoInventario>();
        services.AddScoped<IContabilidadReadRepository, DapperContabilidadReadRepository>();
        services.AddScoped<ISetupContableReadRepository, DapperSetupContableReadRepository>();
        services.AddScoped<IPlantillaDiarioReadRepository, DapperPlantillaDiarioReadRepository>();
        services.AddScoped<ILoteDiarioReadRepository, DapperLoteDiarioReadRepository>();
        services.AddScoped<ILineaDiarioReadRepository, DapperLineaDiarioReadRepository>();
        services.AddScoped<ILoteDiarioBloqueoService, LoteDiarioBloqueoService>();
        services.AddScoped<IRegistroLoteDiarioDatos, RegistroLoteDiarioDatos>();
        services.AddScoped<IRegistroDiarioReadRepository, DapperRegistroDiarioReadRepository>();
        services.AddScoped<IFacturaVentaBorradorReadRepository, DapperFacturaVentaBorradorReadRepository>();
        services.AddScoped<ILineaFacturaVentaBorradorReadRepository, DapperLineaFacturaVentaBorradorReadRepository>();
        services.AddScoped<IFacturaVentaBorradorBloqueoService, FacturaVentaBorradorBloqueoService>();
        services.AddScoped<IFacturaVentaBorradorDatos, FacturaVentaBorradorDatos>();
        services.AddScoped<IPosteoFacturaVentaDatos, PosteoFacturaVentaDatos>();
        services.AddScoped<IRegistroMovimientosCliente, RegistroMovimientosCliente>();
        services.AddScoped<ICobroDatos, CobroDatos>();
        services.AddScoped<ISocioNegocioUsoService, SocioNegocioUsoService>();
        services.AddScoped<IFacturaVentaReadRepository, DapperFacturaVentaReadRepository>();
        services.AddScoped<IBusquedaGlobalRepository, DapperBusquedaGlobalRepository>();
        services.AddScoped<INotaCreditoVentaDatos, NotaCreditoVentaDatos>();
        services.AddScoped<INotaCreditoVentaBorradorReadRepository, DapperNotaCreditoVentaBorradorReadRepository>();
        services.AddScoped<INotaCreditoVentaReadRepository, DapperNotaCreditoVentaReadRepository>();
        services.AddScoped<OpenSource1.Application.Features.FacturasVenta.Notas.INotasFacturaReadRepository, DapperNotasFacturaReadRepository>();
        services.AddScoped<IMovimientoClienteReadRepository, DapperMovimientoClienteReadRepository>();
        services.AddScoped<IInventarioConsultasReadRepository, DapperInventarioConsultasRepository>();
        services.AddScoped<OpenSource1.Application.Features.Reportes.IReportesReadRepository, DapperReportesReadRepository>();
        services.AddScoped<OpenSource1.Application.Features.Reportes.IReportesExportacion, OpenSource1.Infrastructure.Services.Reportes.ReportesExportacionService>();
        services.AddScoped<IConversionUnidadMedidaService, ConversionUnidadMedidaService>();
        services.AddScoped<IGeneradorNumeroDocumento, GeneradorNumeroDocumento>();
        services.AddScoped<ISerieReadRepository, DapperSerieReadRepository>();
        services.AddScoped<IConfiguracionNumeracionReadRepository, DapperConfiguracionNumeracionReadRepository>();
        services.AddScoped<IConsultaInventario, ConsultaInventario>();
        services.AddScoped<IRegistroMovimientosInventario, RegistroMovimientosInventario>();
        services.AddScoped<IAjusteCostoInventario, AjusteCostoInventario>();
        services.AddScoped<IFechasRegistroReadRepository, DapperFechasRegistroReadRepository>();
        services.AddScoped<IValidadorFechaRegistro, ValidadorFechaRegistro>();

        // Usuario del libro de inventario fuera de HTTP (tests, batch): la API registra antes su UsuarioActualHttp y
        // este TryAdd no lo pisa.
        services.TryAddScoped<IUsuarioActual, UsuarioActualSistema>();
        services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));
        services.AddScoped<IUnitOfWork, OpenSource1.Infrastructure.Data.UnitOfWork.UnitOfWork>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IUserAdminService, UserAdminService>();
        services.AddHostedService<DatabaseMigrationHostedService>();

        return services;
    }
}
