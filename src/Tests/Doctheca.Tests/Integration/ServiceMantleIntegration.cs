using System.Data.Common;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Doctheca.Common.Oss;
using Doctheca.Domain.Repositories;
using Doctheca.Host.Authentication;
using Doctheca.Service.StructaDoc;
using Moq;
using Testcontainers.PostgreSql;
using Xunit;

namespace Doctheca.Tests.Integration;

/// <summary>
/// One shared PostgreSQL container for the ServiceMantle integration collection. The real host
/// startup path runs <c>DocthecaMigrationExecutor</c> (EF migrations from the InitialCreate
/// baseline), so the tests exercise the actual EF/Npgsql stack instead of an in-memory fake.
/// </summary>
/// <remarks>
/// Every value the container hands out is a synthetic, per-run credential: the password is a
/// random GUID that never leaves the test process. Nothing here connects to a production
/// Consul, Identity, StructaDoc, OpenSearch, or Loki endpoint.
/// </remarks>
public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container;
    private readonly string? _previousConsulHost;
    private readonly string? _previousConsulPort;
    private readonly string? _previousConsulEnableCache;

    public PostgreSqlFixture()
    {
        // The Consul KV source connects eagerly while Program.cs runs (unreachable -> fallback
        // to appsettings, by design). Point it at a closed loopback port through the supported
        // environment overrides and disable the on-disk cache so the fallback is deterministic,
        // fast, and side-effect free on every machine.
        _previousConsulHost = Environment.GetEnvironmentVariable("CONSUL_HOST");
        _previousConsulPort = Environment.GetEnvironmentVariable("CONSUL_PORT");
        _previousConsulEnableCache = Environment.GetEnvironmentVariable("CONSUL_ENABLE_CACHE");
        Environment.SetEnvironmentVariable("CONSUL_HOST", "127.0.0.1");
        Environment.SetEnvironmentVariable("CONSUL_PORT", "1");
        Environment.SetEnvironmentVariable("CONSUL_ENABLE_CACHE", "false");

        _container = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("doctheca")
            .WithUsername("postgres")
            .WithPassword(Guid.NewGuid().ToString("N"))
            .Build();
    }

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
        Environment.SetEnvironmentVariable("CONSUL_HOST", _previousConsulHost);
        Environment.SetEnvironmentVariable("CONSUL_PORT", _previousConsulPort);
        Environment.SetEnvironmentVariable("CONSUL_ENABLE_CACHE", _previousConsulEnableCache);
    }

    /// <summary>
    /// Configuration overrides pointing <c>PostgreSql:*</c> / <c>Database:Name</c> at the
    /// container, in the shape <c>SharedPostgreSqlConnectionStringFactory</c> reads.
    /// </summary>
    public IReadOnlyDictionary<string, string?> ToConfigOverrides()
    {
        var connection = new DbConnectionStringBuilder
        {
            ConnectionString = _container.GetConnectionString()
        };
        return new Dictionary<string, string?>
        {
            ["PostgreSql:Host"] = Convert.ToString(connection["Host"]),
            ["PostgreSql:Port"] = Convert.ToString(connection["Port"]),
            ["PostgreSql:Username"] = Convert.ToString(connection["Username"]),
            ["PostgreSql:Password"] = Convert.ToString(connection["Password"]),
            ["Database:Name"] = "doctheca"
        };
    }
}

/// <summary>
/// All container-backed tests live in this single collection: xunit runs collections in
/// parallel, and grouping them keeps concurrent hosts from racing the first migration run.
/// The rest of the suite can still be run without Docker via
/// <c>dotnet test --filter "FullyQualifiedName!~Doctheca.Tests.Integration"</c>.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ServiceMantleIntegrationCollection : ICollectionFixture<PostgreSqlFixture>
{
    public const string Name = "service-mantle-integration";
}

public abstract class ServiceMantleIntegrationTestBase
{
    protected const string CorrelationHeaderName = "x-correlation-id";
    protected const string ProtectedApiRoute = "/admin/document-files";

    // Mirrors the synthetic identity values of appsettings.Testing.json: a fake authority used
    // only for JWT issuer/audience validation through the static configuration manager below.
    // No test ever reaches a network identity provider.
    protected const string TestIssuer = "http://localhost:5002";
    protected const string TestAudience = "QuantumZhou.microservices";

    private static readonly RSA TestRsa = RSA.Create(2048);
    protected static readonly RsaSecurityKey TestSigningKey = new(TestRsa)
    {
        KeyId = "doctheca-integration-key"
    };

    // Hermetic overrides for every outbound dependency of the real Program.cs: Consul points
    // at a closed loopback port with the disk cache disabled, the empty Loki:Uri disables the
    // ServiceMantle Grafana Loki remote sink (Console stays on), OpenSearch/StructaDoc are
    // replaced by doubles (see CreateFactory), and the
    // LLM stays disabled because its ApiKey is empty. The IdentityService values mirror the
    // synthetic entries of appsettings.Testing.json and are supplied explicitly so every
    // factory — including ones pointed at a temporary content root without any appsettings
    // files — starts deterministically. None of them are real credentials.
    private static readonly IReadOnlyDictionary<string, string?> RequiredSettings =
        new Dictionary<string, string?>
        {
            ["Consul:Host"] = "127.0.0.1",
            ["Consul:Port"] = "1",
            ["Consul:EnableCache"] = "false",
            ["Loki:Uri"] = "",
            ["OpenSearch:Url"] = "http://127.0.0.1:1",
            ["StructaDoc:BaseUrl"] = "",
            ["LlmDocumentAnalysis:ApiKey"] = "",
            ["IdentityService:Authority"] = "http://localhost:5002",
            ["IdentityService:Issuer"] = TestIssuer,
            ["IdentityService:AdditionalValidIssuers:0"] = "QuantumZhou.Identity",
            ["IdentityService:Audience"] = TestAudience,
            ["IdentityService:RequireHttpsMetadata"] = "false",
            ["IdentityService:ClockSkewSeconds"] = "30",
            ["IdentityService:AppId"] = "doctheca-test-app",
            ["IdentityService:AppSecret"] = "doctheca-test-secret"
        };

    protected ServiceMantleIntegrationTestBase(PostgreSqlFixture database)
    {
        Database = database;
    }

    protected PostgreSqlFixture Database { get; }

    /// <summary>
    /// Builds a factory around the real Program.cs entry point. Accessing <see cref="WebApplicationFactory{TEntryPoint}.Services"/>
    /// or creating a client runs the full host startup, including <c>DocthecaMigrationExecutor</c>.
    /// All overrides go through <c>UseSetting</c>: with the minimal-hosting replay used by
    /// WebApplicationFactory, settings registered this way take precedence over the appsettings
    /// sources. External clients (OpenSearch, StructaDoc, OSS, Identity) are replaced by test
    /// doubles, and JWT validation is pinned to a static in-memory configuration manager with a
    /// synthetic signing key, so no test request reaches a real external service.
    /// </summary>
    protected WebApplicationFactory<Program> CreateFactory(
        string? contentRoot = null,
        Action<IServiceCollection>? configureTestServices = null,
        IReadOnlyDictionary<string, string?>? settings = null)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(webHostBuilder =>
        {
            webHostBuilder.UseEnvironment("Testing");
            if (contentRoot is not null)
            {
                webHostBuilder.UseSetting(WebHostDefaults.ContentRootKey, contentRoot);
            }

            foreach (var (key, value) in Database.ToConfigOverrides())
            {
                webHostBuilder.UseSetting(key, value);
            }

            foreach (var (key, value) in RequiredSettings)
            {
                webHostBuilder.UseSetting(key, value);
            }

            if (settings is not null)
            {
                foreach (var (key, value) in settings) webHostBuilder.UseSetting(key, value);
            }

            webHostBuilder.ConfigureTestServices(services =>
            {
                // External client doubles: nothing in the integration suite talks to a real
                // OpenSearch, StructaDoc, S3, or Identity endpoint.
                services.RemoveAll<ISearchIndexService>();
                services.AddSingleton(Mock.Of<ISearchIndexService>());
                services.RemoveAll<IStructaDocClient>();
                services.AddSingleton(Mock.Of<IStructaDocClient>());
                services.RemoveAll<IOssService>();
                services.AddSingleton(Mock.Of<IOssService>());

                // Replace the OIDC-discovery-backed JwtBearer configuration manager with a
                // static in-memory one carrying the synthetic signing key, so admin Bearer and
                // cookie tokens validate without any network identity provider. The handler
                // only builds a discovery-backed manager when ConfigurationManager is null,
                // so this override fully removes the network path.
                services.PostConfigure<JwtBearerOptions>(
                    JwtBearerDefaults.AuthenticationScheme,
                    options =>
                    {
                        var configuration = new OpenIdConnectConfiguration
                        {
                            Issuer = TestIssuer
                        };
                        configuration.SigningKeys.Add(TestSigningKey);
                        options.ConfigurationManager =
                            new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                    });

                configureTestServices?.Invoke(services);
            });
        });
    }

    /// <summary>
    /// An explicit temporary content root, so the SPA branch under test is chosen by this test
    /// and not by any leftover wwwroot in the local Host project directory.
    /// </summary>
    protected static string CreateTempContentRoot(bool withWwwrootStub)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "doctheca-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        if (withWwwrootStub)
        {
            Directory.CreateDirectory(Path.Combine(path, "wwwroot"));
            File.WriteAllText(
                Path.Combine(path, "wwwroot", "index.html"),
                "<!doctype html><html><head><title>stub</title></head><body>doctheca-stub-index-marker</body></html>");
            File.WriteAllText(
                Path.Combine(path, "wwwroot", "probe.txt"),
                "doctheca-static-probe");
        }

        return path;
    }

    /// <summary>
    /// Mints an RS256 admin/user token the static configuration manager validates. The claims
    /// mirror the SignaCore contract the host consumes: <c>unique_name</c>, <c>role</c>, no
    /// claim remapping.
    /// </summary>
    protected static string CreateToken(string role)
    {
        var token = new JwtSecurityToken(
            issuer: TestIssuer,
            audience: TestAudience,
            claims:
            [
                new Claim("sub", Guid.NewGuid().ToString()),
                new Claim("unique_name", "integration-tester"),
                new Claim("role", role)
            ],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(TestSigningKey, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static string? Field(
        IReadOnlyList<KeyValuePair<string, object?>> fields,
        string name)
    {
        foreach (var field in fields)
        {
            if (string.Equals(field.Key, name, StringComparison.Ordinal))
            {
                return field.Value?.ToString();
            }
        }

        return null;
    }
}

/// <summary>
/// Records the ILogger scopes opened during a request, including whether each scope was
/// disposed. The ServiceMantle request scope is an <c>IReadOnlyList&lt;KeyValuePair&lt;string, object?&gt;&gt;</c>
/// of named fields; replacing the logger factory with a plain one captures that scope state
/// directly — the same scope surface the Serilog pipeline consumes. Actual Console/Loki
/// delivery is asserted separately in <c>ServiceMantleLoggingTests</c>.
/// </summary>
public sealed class RequestScopeCapture : ILoggerProvider
{
    private readonly object _gate = new();

    public List<CapturedScope> Scopes { get; } = [];

    public List<CapturedLog> Logs { get; } = [];

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(this, categoryName);

    public void Dispose()
    {
    }

    public sealed class CapturedScope
    {
        internal CapturedScope(IReadOnlyList<KeyValuePair<string, object?>> fields)
        {
            Fields = fields;
        }

        public IReadOnlyList<KeyValuePair<string, object?>> Fields { get; }

        public bool Disposed { get; internal set; }

        public string? Field(string name) =>
            ServiceMantleIntegrationTestBase.Field(Fields, name);
    }

    public sealed record CapturedLog(string Category, string Message);

    private sealed class CaptureLogger(RequestScopeCapture owner, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            if (state is IReadOnlyList<KeyValuePair<string, object?>> fields)
            {
                CapturedScope scope = new(fields);
                lock (owner._gate)
                {
                    owner.Scopes.Add(scope);
                }

                return new TrackedScope(scope);
            }

            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (owner._gate)
            {
                owner.Logs.Add(new CapturedLog(categoryName, formatter(state, exception)));
            }
        }
    }

    private sealed class TrackedScope(CapturedScope scope) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                scope.Disposed = true;
            }
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
