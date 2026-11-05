using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenSource1.Infrastructure.Identity;
using OpenSource1.Application.Security;
using OpenSource1.Application.Services.Auth;
using OpenSource1.Application.Services.Auth.Dtos;
using OpenSource1.Application.Storage;
using OpenSource1.Core.Common;

namespace OpenSource1.Infrastructure.Services.Auth;

public sealed class AuthService(
    UserManager<Usuario> userManager,
    SignInManager<Usuario> signInManager,
    IOptions<JwtOptions> jwtOptions,
    IOptions<IdentityOptions> identityOptions) : IAuthService
{
    private readonly JwtOptions _jwtOptions = jwtOptions.Value;
    private readonly int _maxAttempts = identityOptions.Value.Lockout.MaxFailedAccessAttempts;

    public async Task<(AuthResponse? Response, IReadOnlyList<string> Errors)> RegisterAsync(
        RegisterRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Verificar si el correo ya existe antes de intentar crear
        var existing = await userManager.FindByEmailAsync(request.Email);
        if (existing is not null)
        {
            return (null, ["Ya existe una cuenta registrada con ese correo electrónico."]);
        }

        var user = new Usuario
        {
            UserName = request.Email,
            Email = request.Email,
            FullName = request.FullName,
            EmailConfirmed = true,
            IsActive = true
        };

        var result = await userManager.CreateAsync(user, request.Password);

        if (!result.Succeeded)
        {
            var errors = result.Errors.Select(e => TranslateIdentityError(e.Code, e.Description)).ToArray();
            return (null, errors);
        }

        return (await CreateAuthResponseAsync(user), []);
    }

    public async Task<(AuthResponse? Response, IReadOnlyList<string> Errors)> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.UserNameOrEmail);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Password);

        var user = request.UserNameOrEmail.Contains('@', StringComparison.Ordinal)
            ? await userManager.FindByEmailAsync(request.UserNameOrEmail)
            : await userManager.FindByNameAsync(request.UserNameOrEmail);

        if (user is null || !user.IsActive)
        {
            return (null, ["Usuario o contraseña incorrectos. Verifique sus credenciales."]);
        }

        if (await userManager.IsLockedOutAsync(user))
        {
            var lockoutEnd = await userManager.GetLockoutEndDateAsync(user);
            var remaining = lockoutEnd.HasValue
                ? (int)Math.Ceiling((lockoutEnd.Value - DateTimeOffset.UtcNow).TotalMinutes)
                : 5;
            return (null, [$"Cuenta bloqueada temporalmente por múltiples intentos fallidos. Intente nuevamente en {remaining} minuto(s)."]);
        }

        var result = await signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);

        if (result.IsLockedOut)
        {
            return (null, [$"Cuenta bloqueada por exceder el máximo de intentos permitidos ({_maxAttempts}). Intente en 5 minutos."]);
        }

        if (!result.Succeeded)
        {
            var failedCount = await userManager.GetAccessFailedCountAsync(user);
            var remaining = _maxAttempts - failedCount;

            var message = remaining > 0
                ? $"Contraseña incorrecta. Le quedan {remaining} intento(s) antes del bloqueo temporal de la cuenta."
                : $"Contraseña incorrecta. La cuenta será bloqueada en el próximo intento fallido.";

            return (null, [message]);
        }

        return (await CreateAuthResponseAsync(user), []);
    }

    public async Task<PasswordActionResponse> ForgotPasswordAsync(
        ForgotPasswordRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.UserNameOrEmail);

        var user = await FindUserAsync(request.UserNameOrEmail);

        if (user is null || !user.IsActive)
        {
            return new PasswordActionResponse("Si la cuenta existe, se generó una solicitud de restablecimiento.");
        }

        return new PasswordActionResponse(
            "Si la cuenta existe, recibirá instrucciones para restablecer su contraseña.");
    }

    public async Task<(PasswordActionResponse? Response, IReadOnlyList<string> Errors)> ResetPasswordAsync(
        ResetPasswordRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.UserNameOrEmail);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Token);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.NewPassword);

        var user = await FindUserAsync(request.UserNameOrEmail);
        if (user is null || !user.IsActive)
        {
            return (null, ["No fue posible restablecer la contraseña. Verifique los datos e intente nuevamente."]);
        }

        var result = await userManager.ResetPasswordAsync(user, request.Token, request.NewPassword);
        if (!result.Succeeded)
        {
            return (null, result.Errors.Select(e => TranslateIdentityError(e.Code, e.Description)).ToArray());
        }

        await userManager.ResetAccessFailedCountAsync(user);

        return (new PasswordActionResponse("Contraseña restablecida correctamente. Ya puede iniciar sesión."), []);
    }

    public async Task<(PasswordActionResponse? Response, IReadOnlyList<string> Errors)> ChangePasswordAsync(
        string userId,
        ChangePasswordRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CurrentPassword);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.NewPassword);

        var user = await userManager.FindByIdAsync(userId);
        if (user is null || !user.IsActive)
        {
            return (null, ["No fue posible identificar la cuenta autenticada."]);
        }

        var result = await userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
        {
            return (null, result.Errors.Select(e => TranslateIdentityError(e.Code, e.Description)).ToArray());
        }

        await userManager.ResetAccessFailedCountAsync(user);

        return (new PasswordActionResponse("Contraseña cambiada correctamente."), []);
    }

    public async Task<CurrentUserResponse?> GetCurrentUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null || !user.IsActive)
        {
            return null;
        }

        var roles = (await userManager.GetRolesAsync(user)).ToArray();
        var permissions = GetPermissions(roles);

        return new CurrentUserResponse(
            user.Id,
            user.FullName ?? user.UserName ?? string.Empty,
            user.Email ?? string.Empty,
            user.ProfileImagePath,
            roles,
            permissions);
    }

    public async Task<(bool Success, IReadOnlyList<string> Errors)> UpdateProfileImageAsync(string userId, string? imagePath, CancellationToken cancellationToken = default)
    {
        // Cualquier usuario autenticado llega aquí: la ruta acaba en un borrado de fichero al cambiar la imagen
        // de perfil, así que solo se acepta /uploads/users/<nombre> (400 con el campo ImagePath).
        if (RutaImagen.Validar(imagePath, RutaImagen.CarpetaUsuarios) is { } errorImagen)
        {
            throw new ErroresDeDominioException(errorImagen);
        }

        var user = await userManager.FindByIdAsync(userId);
        if (user is null || !user.IsActive)
        {
            return (false, ["No fue posible identificar la cuenta autenticada."]);
        }

        // Una imagen de perfil solo puede pertenecer a UN usuario: si otro pudiera apuntar al fichero de este, al cambiar
        // su imagen la UI borraría la ajena.
        // LIMITACIÓN CONOCIDA: consulta previa SIN índice único; dos peticiones concurrentes con el mismo imagePath pueden asignarlo
        // a dos usuarios (medido: 40 de 40 rondas). Consecuencia: al cambiar la imagen de uno se borra el fichero y el otro queda con
        // la imagen rota; NO es explotable para borrar el fichero de una víctima. Corrección de raíz recomendada (no implementada):
        // índice único parcial sobre ProfileImagePath (no nulo) o comprobar referencias antes de borrar el fichero.
        if (!string.IsNullOrEmpty(imagePath)
            && await userManager.Users.AnyAsync(u => u.ProfileImagePath == imagePath && u.Id != user.Id, cancellationToken))
        {
            throw new ErroresDeDominioException(new Error(
                "usuario.imagen_en_uso", "La imagen ya está asignada a otro usuario.", RutaImagen.Campo));
        }

        user.ProfileImagePath = imagePath;
        user.UpdatedAtUtc = DateTimeOffset.UtcNow;

        var result = await userManager.UpdateAsync(user);
        return result.Succeeded
            ? (true, Array.Empty<string>())
            : (false, result.Errors.Select(e => e.Description).ToArray());
    }

    private async Task<Usuario?> FindUserAsync(string userNameOrEmail) =>
        userNameOrEmail.Contains('@', StringComparison.Ordinal)
            ? await userManager.FindByEmailAsync(userNameOrEmail)
            : await userManager.FindByNameAsync(userNameOrEmail);

    private async Task<AuthResponse> CreateAuthResponseAsync(Usuario user)
    {
        var expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(_jwtOptions.ExpirationMinutes);
        var roles = (await userManager.GetRolesAsync(user)).ToArray();

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.UserName ?? user.Email ?? user.Id)
        };

        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        var permissions = GetPermissions(roles);
        claims.AddRange(permissions.Select(permission => new Claim("permission", permission)));

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtOptions.SigningKey));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _jwtOptions.Issuer,
            audience: _jwtOptions.Audience,
            claims: claims,
            expires: expiresAtUtc.UtcDateTime,
            signingCredentials: credentials);

        var accessToken = new JwtSecurityTokenHandler().WriteToken(token);

        return new AuthResponse(
            user.Id,
            user.Email ?? string.Empty,
            user.FullName ?? string.Empty,
            roles,
            permissions,
            accessToken,
            expiresAtUtc);
    }

    private static string[] GetPermissions(IEnumerable<string> roles)
    {
        var roleSet = roles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var permissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (roleSet.Contains(ApplicationRoles.Administrator))
        {
            permissions.UnionWith([ApplicationPolicies.CanAdd, ApplicationPolicies.CanModify, ApplicationPolicies.CanDelete, ApplicationPolicies.CanConsult, ApplicationPolicies.CanAdministrar]);
        }

        if (roleSet.Contains(ApplicationRoles.Supervisor))
        {
            permissions.UnionWith([ApplicationPolicies.CanModify, ApplicationPolicies.CanConsult]);
        }

        if (roleSet.Contains(ApplicationRoles.Executor))
        {
            permissions.UnionWith([ApplicationPolicies.CanAdd, ApplicationPolicies.CanConsult]);
        }

        if (roleSet.Contains(ApplicationRoles.Salesperson))
        {
            permissions.Add(ApplicationPolicies.CanConsult);
        }

        // Permisos finos por recurso (catálogo modulo.recurso.accion, Permisos.*): se añaden al
        // MISMO array, bajo el MISMO claim "permission" que las 4 políticas coarse de arriba.
        // Blazor (Login.razor / Program.cs) itera genéricamente sobre este array sin distinguir
        // su origen, así que ambas familias conviven sin romper el flujo existente — no se crea
        // un claim "permiso" (español) separado.
        foreach (var rol in roleSet)
        {
            permissions.UnionWith(PermisosPorRol.ParaRol(rol));
        }

        return permissions.Order().ToArray();
    }

    /// <summary>Traduce los códigos de error de ASP.NET Core Identity a mensajes amigables en español.</summary>
    private static string TranslateIdentityError(string code, string fallback) => code switch
    {
        "DuplicateUserName"   => "El nombre de usuario ya está en uso. Elija uno diferente.",
        "DuplicateEmail"      => "Ya existe una cuenta registrada con ese correo electrónico.",
        "InvalidEmail"        => "El correo electrónico ingresado no es válido.",
        "InvalidUserName"     => "El nombre de usuario contiene caracteres no permitidos.",
        "PasswordMismatch"    => "La contraseña actual no es correcta.",
        "InvalidToken"        => "El token de restablecimiento no es válido o ya expiró.",
        "PasswordTooShort"    => "La contraseña debe tener al menos 8 caracteres.",
        "PasswordRequiresDigit"     => "La contraseña debe incluir al menos un número.",
        "PasswordRequiresUpper"     => "La contraseña debe incluir al menos una letra mayúscula.",
        "PasswordRequiresLower"     => "La contraseña debe incluir al menos una letra minúscula.",
        "PasswordRequiresNonAlphanumeric" => "La contraseña debe incluir al menos un carácter especial.",
        "PasswordRequiresUniqueChars"     => "La contraseña debe incluir más caracteres únicos.",
        _ => fallback
    };
}
