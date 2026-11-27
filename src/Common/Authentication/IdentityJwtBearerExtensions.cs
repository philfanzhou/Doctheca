using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Doctheca.Common.Authentication;

/// <summary>
/// Shared JWT Bearer configuration for all services that trust the configured identity domain.
/// </summary>
public static class IdentityJwtBearerExtensions
{
    /// <summary>
    /// Registers JWT Bearer authentication and authorization using the standard
    /// IdentityService configuration keys (IdentityService:Authority, etc.).
    /// The signing key is resolved automatically via OIDC discovery (JWKS).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration containing the IdentityService section.</param>
    /// <param name="configureAuthorization">Optional callback to add authorization policies.</param>
    public static IServiceCollection AddIdentityJwtBearer(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        Action<JwtBearerConsumerOptions>? configureConsumer = null,
        Action<AuthorizationOptions>? configureAuthorization = null)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        if (configuration is null) throw new ArgumentNullException(nameof(configuration));
        if (environment is null) throw new ArgumentNullException(nameof(environment));

        var identityOptions = configuration
            .GetSection(IdentityOptions.SectionName)
            .Get<IdentityOptions>() ?? new IdentityOptions();
        var validation = new IdentityOptionsValidator()
            .Validate(Options.DefaultName, identityOptions);
        if (validation.Failed)
        {
            throw new OptionsValidationException(
                IdentityOptions.SectionName,
                typeof(IdentityOptions),
                validation.Failures);
        }

        services.AddOptions<IdentityOptions>()
            .Bind(configuration.GetSection(IdentityOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<IdentityOptions>>(
            new IdentityOptionsValidator());

        var consumerOptions = new JwtBearerConsumerOptions();
        configureConsumer?.Invoke(consumerOptions);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = identityOptions.Authority.Trim().TrimEnd('/');
                options.Audience = identityOptions.Audience;
                options.RequireHttpsMetadata = identityOptions.RequireHttpsMetadata;
                options.MapInboundClaims = consumerOptions.MapInboundClaims;
                options.TokenValidationParameters = TokenValidationParametersFactory.Create(
                    identityOptions,
                    nameClaimType: consumerOptions.NameClaimType,
                    roleClaimType: consumerOptions.RoleClaimType);


            });

        services.AddAuthorization(options =>
        {
            // Default fallback policy: any authenticated user.
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();

            configureAuthorization?.Invoke(options);
        });

        return services;
    }
}
