using System.Data.Common;
using Microsoft.Extensions.Configuration;
using Doctheca.Consul;
using Xunit;

namespace Doctheca.Tests;

public sealed class PostgreSqlPoolConfigurationTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(p => p.Key, p => p.Value)).Build();
    private static readonly (string, string?)[] Shared =
    [
        ("PostgreSql:Host", "shared-host"), ("PostgreSql:Username", "shared-user"),
        ("PostgreSql:Password", "mock-shared-value"), ("Database:Name", "shared-db"),
    ];
    private static DbConnectionStringBuilder Build(IConfiguration config, string? fallback = null) =>
        new() { ConnectionString = SharedPostgreSqlConnectionStringFactory.BuildOrFallback(config, fallback)! };

    [Fact]
    public void SharedCredentialsPreserveOnlyFallbackPoolTunables()
    {
        var result = Build(Config(Shared), "Host=fallback;Username=old;Password=mock-old-value;Database=old;Maximum Pool Size=3;Connection Idle Lifetime=60;Application Name=reader;SSL Mode=Disable");
        Assert.Equal("shared-host", result["Host"]);
        Assert.Equal("shared-user", result["Username"]);
        Assert.Equal("mock-shared-value", result["Password"]);
        Assert.Equal("shared-db", result["Database"]);
        Assert.Equal("3", result["Maximum Pool Size"].ToString());
        Assert.Equal("60", result["Connection Idle Lifetime"].ToString());
        Assert.Equal("reader", result["Application Name"]);
        Assert.False(result.ContainsKey("SSL Mode"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ServiceBudgetOverridesFallbackInBothConfigurationPaths(bool shared)
    {
        var values = new List<(string, string?)>(shared ? Shared : []);
        values.AddRange([("Database:Pool:MaximumSize", "6"), ("Database:Pool:MinimumSize", "0"),
            ("Database:Pool:IdleLifetimeSeconds", "60"), ("Database:Pool:PruningIntervalSeconds", "10"),
            ("Database:Pool:ConnectionTimeoutSeconds", "5"), ("Database:Pool:CommandTimeoutSeconds", "30"),
            ("Database:Pool:ConnectionLifetimeSeconds", "600"), ("Database:ApplicationName", "student")]);
        var result = Build(Config(values.ToArray()), "Host=localhost;Database=local;Maximum Pool Size=3");
        Assert.Equal("6", result["Maximum Pool Size"].ToString());
        Assert.Equal("0", result["Minimum Pool Size"].ToString());
        Assert.Equal("60", result["Connection Idle Lifetime"].ToString());
        Assert.Equal("10", result["Connection Pruning Interval"].ToString());
        Assert.Equal("5", result["Timeout"].ToString());
        Assert.Equal("30", result["Command Timeout"].ToString());
        Assert.Equal("600", result["Connection Lifetime"].ToString());
        Assert.Equal("student", result["Application Name"]);
    }

    [Theory]
    [InlineData("MaximumSize", "0")]
    [InlineData("MaximumSize", "private-invalid-value")]
    [InlineData("MaximumSize", "2147483648")]
    [InlineData("MinimumSize", "-1")]
    [InlineData("MinimumSize", "101")]
    [InlineData("IdleLifetimeSeconds", "-1")]
    [InlineData("PruningIntervalSeconds", "0")]
    [InlineData("ConnectionTimeoutSeconds", "-1")]
    [InlineData("CommandTimeoutSeconds", "")]
    public void InvalidBudgetIsRejectedWithoutValuesOrInnerExceptions(string key, string value)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Build(Config([.. Shared, ("Database:Pool:" + key, value)])));
        Assert.Equal("PostgreSQL connection pool configuration is invalid.", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("mock-shared-value", error.ToString());
        Assert.DoesNotContain("private-invalid-value", error.ToString());
    }

    [Fact]
    public void MinimumCannotExceedTheEffectiveMaximum()
    {
        Assert.Throws<InvalidOperationException>(() => Build(Config([.. Shared, ("Database:Pool:MaximumSize", "2"), ("Database:Pool:MinimumSize", "3")])));
    }

    [Fact]
    public void CanonicalFallbackKeysAndMissingConfigurationRemainCompatible()
    {
        var result = Build(Config(Shared), "MaximumPoolSize=4;MinimumPoolSize=1;Pooling=true");
        Assert.Equal("4", result["Maximum Pool Size"].ToString());
        Assert.Equal("1", result["Minimum Pool Size"].ToString());
        Assert.Equal("True", result["Pooling"].ToString());
        Assert.Null(SharedPostgreSqlConnectionStringFactory.BuildOrFallback(Config()));
        var fallback = "Host=localhost;Database=local";
        Assert.Equal(Build(Config(), fallback).ConnectionString, new DbConnectionStringBuilder { ConnectionString = fallback }.ConnectionString);
    }
}
