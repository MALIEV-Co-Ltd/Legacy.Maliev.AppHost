using Aspire.Hosting.ApplicationModel;
using Npgsql;

internal static class LegacyPoolerConnectionString
{
    internal static ReferenceExpression ForGke(string database, string username, string password)
    {
        var connection = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = 15432,
            Database = database,
            Username = username,
            Password = password,
            SslMode = SslMode.Disable,
            MaxPoolSize = 10,
            ConnectionIdleLifetime = 60,
            Timeout = 15,
            CommandTimeout = 30
        };
        return ReferenceExpression.Create($"{connection.ConnectionString}");
    }

    internal static ReferenceExpression ForLocal(
        ReferenceExpression host, ReferenceExpression port, string database,
        ParameterResource username, ParameterResource password)
    {
        // Retain deferred endpoint/parameter references in both the manifest and runtime expression.
        var expression = new ReferenceExpressionBuilder();
        expression.Append($"Host={host};Port={port};Database={database};Username={username};");
        expression.AppendLiteral("Password=");
        expression.AppendFormatted(password);
        expression.AppendLiteral(";SSL Mode=Disable;Maximum Pool Size=10;Connection Idle Lifetime=60;Timeout=15;Command Timeout=30");
        return expression.Build();
    }
}
