using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FluentValidation;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using PosLedger.Api.Common;

namespace PosLedger.Api.Features.Auth;

public sealed record TokenRequest(string Username, string Password);

public sealed record TokenResponse(string AccessToken, string TokenType, int ExpiresInSeconds, string Role);

public sealed class TokenRequestValidator : AbstractValidator<TokenRequest>
{
    public TokenRequestValidator()
    {
        RuleFor(x => x.Username).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(200);
    }
}

public static class AuthEndpoints
{
    /// <summary>
    /// A hash of a password nobody knows, used to spend the same time on an unknown username as on
    /// a known one. Without it, response time answers "does this account exist?" for free.
    /// </summary>
    private static readonly string DecoyHash = PasswordHasher.Hash(Guid.NewGuid().ToString());

    public static IEndpointRouteBuilder MapAuth(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/auth/token", (
                TokenRequest request,
                IOptions<AuthOptions> authOptions,
                IOptions<JwtOptions> jwtOptions,
                ILoggerFactory loggerFactory) =>
            {
                var logger = loggerFactory.CreateLogger("Auth");

                var user = authOptions.Value.Users
                    .FirstOrDefault(u => string.Equals(u.Username, request.Username, StringComparison.OrdinalIgnoreCase));

                var valid = PasswordHasher.Verify(request.Password, user?.PasswordHash ?? DecoyHash) && user is not null;

                if (!valid)
                {
                    // The username is logged, the password is not, and the response says neither
                    // which of the two was wrong.
                    logger.LogWarning("Failed token request for {Username}", request.Username);
                    return Results.Problem(
                        title: "Invalid credentials",
                        detail: "Username or password is incorrect.",
                        statusCode: StatusCodes.Status401Unauthorized);
                }

                var jwt = jwtOptions.Value;
                var expires = DateTime.UtcNow.AddMinutes(jwt.ExpiryMinutes);

                var token = new JwtSecurityToken(
                    issuer: jwt.Issuer,
                    audience: jwt.Audience,
                    claims:
                    [
                        new Claim(JwtRegisteredClaimNames.Sub, user!.Username),
                        new Claim(ClaimTypes.Name, user.Username),
                        new Claim(ClaimTypes.Role, user.Role),
                        new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("n"))
                    ],
                    expires: expires,
                    signingCredentials: new SigningCredentials(
                        new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret)),
                        SecurityAlgorithms.HmacSha256));

                return Results.Ok(new TokenResponse(
                    new JwtSecurityTokenHandler().WriteToken(token),
                    "Bearer",
                    jwt.ExpiryMinutes * 60,
                    user.Role));
            })
            .Validate<TokenRequest>()
            .AllowAnonymous()
            .WithTags("Auth")
            .Produces<TokenResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .WithSummary("Exchange credentials for a bearer token")
            .WithDescription("Demo accounts are listed in the README. In a real deployment this endpoint is the client's identity provider.");

        return app;
    }
}
