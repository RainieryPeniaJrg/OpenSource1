namespace OpenSource1.Application.Security;

public static class ApplicationPolicies
{
    public const string CanAdd = "CanAdd";
    public const string CanModify = "CanModify";
    public const string CanDelete = "CanDelete";
    public const string CanConsult = "CanConsult";
    // Invoice-scoped policies preserve legacy coarse grants without granting global writes to Vendedor.
    public const string CanCapturarFactura = "CanCapturarFactura";
    public const string CanEmitirFactura = "CanEmitirFactura";

    /// <summary>
    /// Configuración reservada al Administrador (Task 8.5: fechas de registro permitidas). En la API exige el rol
    /// <see cref="ApplicationRoles.Administrator"/>; en el token se emite como permiso coarse solo para ese rol (ver
    /// <c>AuthService.GetPermissions</c>), que es lo que evalúa Blazor.
    /// </summary>
    public const string CanAdministrar = "CanAdministrar";
}
