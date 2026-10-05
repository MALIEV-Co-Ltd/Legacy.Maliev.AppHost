namespace Legacy.Maliev.AppHost.Tests;

internal static class PostgreSql18FixtureSelection
{
    internal static bool UsesExternalFixture(string? archivePath, string? administrativeConnection)
    {
        bool archiveSupplied = !string.IsNullOrWhiteSpace(archivePath);
        bool connectionSupplied = !string.IsNullOrWhiteSpace(administrativeConnection);
        if (archiveSupplied != connectionSupplied)
        {
            throw new InvalidOperationException("External PG18 fixture archive and restore connection must be supplied together; partial input cannot select a local container.");
        }
        return archiveSupplied;
    }
}
