namespace Legacy.Maliev.AppHost.Tests;

public sealed class PostgreSql18FixtureSelectionTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", " ")]
    public void NoExternalInputs_SelectsTheExplicitLocalTestFixture(string? archive, string? connection) =>
        Assert.False(PostgreSql18FixtureSelection.UsesExternalFixture(archive, connection));

    [Fact]
    public void CompleteExternalInputs_SelectThePreparedExternalFixture() =>
        Assert.True(PostgreSql18FixtureSelection.UsesExternalFixture("prepared-custom.dump", "Host=fixture-only;Database=postgres"));

    [Theory]
    [InlineData("prepared-custom.dump", null)]
    [InlineData(null, "Host=fixture-only;Database=postgres")]
    [InlineData("prepared-custom.dump", " ")]
    [InlineData(" ", "Host=fixture-only;Database=postgres")]
    public void PartialExternalInputs_RejectBeforeAnyLocalContainerOrRestore(string? archive, string? connection) =>
        Assert.Throws<InvalidOperationException>(() => PostgreSql18FixtureSelection.UsesExternalFixture(archive, connection));
}
