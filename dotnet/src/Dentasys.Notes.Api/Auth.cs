using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Dentasys.Notes.Api;

/// <summary>
/// Who is calling. Standard JWT bearer tokens carrying four claims:
///   sub       the user, or the workstation for a capture agent
///   role      clinician | capture_agent
///   practice  the one practice this caller may touch
///   prov_cd   the provider code a clinician signs as
///
/// In production the tokens come from the company's identity provider (Entra
/// ID, or AD through a token service) via Notes:Auth:Authority. The lab mints
/// its own with a symmetric key. There is deliberately no default for either:
/// a service started without authentication configured refuses to start,
/// rather than trusting tokens signed with a key that sits in a repository.
/// </summary>
public static class Auth
{
    public const string ClinicianPolicy = "clinician";
    public const string CaptureAgentPolicy = "capture_agent";

    public static void AddNotesAuth(this IServiceCollection services, IConfiguration config)
    {
        var authority = config["Notes:Auth:Authority"];
        var labKey = config["Notes:Auth:LabSigningKey"];
        if (string.IsNullOrEmpty(authority) && string.IsNullOrEmpty(labKey))
            throw new InvalidOperationException(
                "no authentication configured: set Notes:Auth:Authority (production) or Notes:Auth:LabSigningKey (lab)");

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            o.MapInboundClaims = false;
            if (!string.IsNullOrEmpty(authority))
            {
                o.Authority = authority;
                o.Audience = config["Notes:Auth:Audience"] ?? "dentasys-notes";
            }
            else
            {
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = LabIssuer,
                    ValidAudience = LabAudience,
                    IssuerSigningKey = LabKey(labKey!),
                    RoleClaimType = "role",
                    NameClaimType = "sub",
                };
            }
        });

        services.AddAuthorizationBuilder()
            .AddPolicy(ClinicianPolicy, p => p.RequireClaim("role", "clinician").RequireClaim("practice").RequireClaim("prov_cd"))
            .AddPolicy(CaptureAgentPolicy, p => p.RequireClaim("role", "capture_agent").RequireClaim("practice"));
    }

    public static string Practice(this ClaimsPrincipal user) => user.FindFirstValue("practice")!;

    public static Clinician AsClinician(this ClaimsPrincipal user) =>
        new(user.FindFirstValue("sub")!, user.FindFirstValue("prov_cd")!);

    public const string LabIssuer = "dentasys-lab";
    public const string LabAudience = "dentasys-notes";

    /// <summary>Lab only: mint a token the lab-configured service accepts.</summary>
    public static string IssueLabToken(string labKey, string subject, string role, string practice,
                                       string? providerCode = null, TimeSpan? lifetime = null)
    {
        var claims = new Dictionary<string, object> { ["sub"] = subject, ["role"] = role, ["practice"] = practice };
        if (providerCode is not null) claims["prov_cd"] = providerCode;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = LabIssuer,
            Audience = LabAudience,
            Claims = claims,
            Expires = DateTime.UtcNow + (lifetime ?? TimeSpan.FromHours(10)),
            SigningCredentials = new SigningCredentials(LabKey(labKey), SecurityAlgorithms.HmacSha256),
        });
    }

    private static SymmetricSecurityKey LabKey(string key)
    {
        var bytes = Encoding.UTF8.GetBytes(key);
        if (bytes.Length < 32) throw new InvalidOperationException("Notes:Auth:LabSigningKey must be at least 32 bytes");
        return new SymmetricSecurityKey(bytes);
    }
}
