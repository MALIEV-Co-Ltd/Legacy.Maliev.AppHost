using Aspire.Hosting.ApplicationModel;
using Npgsql;

namespace Legacy.Maliev.AppHost.Tests;

public sealed class LegacyPoolerConnectionStringTests
{
    [Fact]
    public async Task LocalCredentialsRemainDeferredAndRetainConnectionSettings()
    {
        int reads = 0;
        var username = new ParameterResource("fixture-user", _ => { reads++; return "fixture_user"; }, false);
        var password = new ParameterResource("fixture-password", _ => { reads++; return "fixture_password"; }, true);
        string host = "127.0.0.1";
        string port = "15432";
        var expression = LegacyPoolerConnectionString.ForLocal(
            ReferenceExpression.Create($"{host}"), ReferenceExpression.Create($"{port}"), "Country", username, password);

        Assert.Equal(0, reads);
        Assert.Contains("{fixture-user.value}", expression.ValueExpression, StringComparison.Ordinal);
        Assert.Contains("{fixture-password.value}", expression.ValueExpression, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture_password", expression.ValueExpression, StringComparison.Ordinal);
        var parsed = new NpgsqlConnectionStringBuilder(await expression.GetValueAsync(CancellationToken.None));
        Assert.Equal(2, reads);
        Assert.Equal("fixture_user", parsed.Username);
        Assert.Equal("fixture_password", parsed.Password);
        Assert.Equal("Country", parsed.Database);
        AssertConnectionSettings(parsed);
    }

    [Fact]
    public async Task GkeCredentialsWithDelimitersRoundTripWithoutAddingConnectionProperties()
    {
        string username = "fixture;user";
        string password = "fixture;Database=other;\"quoted\"";
        var expression = LegacyPoolerConnectionString.ForGke("Material", username, password);
        var parsed = new NpgsqlConnectionStringBuilder(await expression.GetValueAsync(CancellationToken.None));
        Assert.Equal(username, parsed.Username);
        Assert.Equal(password, parsed.Password);
        Assert.Equal("Material", parsed.Database);
        AssertConnectionSettings(parsed);
    }

    private static void AssertConnectionSettings(NpgsqlConnectionStringBuilder parsed)
    {
        Assert.Equal("127.0.0.1", parsed.Host);
        Assert.Equal(15432, parsed.Port);
        Assert.Equal(SslMode.Disable, parsed.SslMode);
        Assert.Equal(10, parsed.MaxPoolSize);
        Assert.Equal(60, parsed.ConnectionIdleLifetime);
        Assert.Equal(15, parsed.Timeout);
        Assert.Equal(30, parsed.CommandTimeout);
    }
}
