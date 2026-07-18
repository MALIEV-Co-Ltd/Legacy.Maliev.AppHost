using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Legacy.Maliev.AppHost.Topology;
using Legacy.Maliev.Intranet.Auth;
using Legacy.Maliev.Intranet.Server.Orders;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Legacy.Maliev.AppHost.Tests;

public sealed class RedisResp3CompatibilityTests(RedisResp3Fixture fixture)
    : IClassFixture<RedisResp3Fixture>
{
    [Fact]
    public async Task RedisWithoutTheAspirePassword_IsRejected()
    {
        await Assert.ThrowsAnyAsync<RedisException>(fixture.ConnectWithoutPasswordAsync);
    }

    [Fact]
    public async Task RetainedServiceCacheContracts_RunAuthenticatedOverResp3()
    {
        await using var connection = await fixture.ConnectAsync();
        var endpoint = Assert.Single(connection.GetEndPoints());
        Assert.Equal(RedisProtocol.Resp3, connection.GetServer(endpoint).Protocol);
        var database = connection.GetDatabase();

        foreach (var service in LegacyTopology.Resp3CacheServiceNames)
        {
            var prefix = $"legacy:{service}:integration:{Guid.NewGuid():N}:";
            var valueKey = prefix + "value";
            var counterKey = prefix + "counter";
            Assert.True(await database.StringSetAsync(valueKey, "redis-8.4-resp3", TimeSpan.FromMinutes(1)));
            Assert.Equal("redis-8.4-resp3", await database.StringGetAsync(valueKey));

            const string incrementScript = "local v=redis.call('INCR',KEYS[1]); if v==1 then redis.call('EXPIRE',KEYS[1],ARGV[1]); end; return v";
            Assert.Equal(1, (long)await database.ScriptEvaluateAsync(
                incrementScript,
                [counterKey],
                [60]));
            Assert.Equal(2, (long)await database.ScriptEvaluateAsync(
                incrementScript,
                [counterKey],
                [60]));
            Assert.True(await database.KeyTimeToLiveAsync(counterKey) > TimeSpan.Zero);

            var keys = new List<RedisKey>();
            await foreach (var key in connection.GetServer(endpoint).KeysAsync(pattern: prefix + "*"))
            {
                keys.Add(key);
            }

            Assert.Contains((RedisKey)valueKey, keys);
            Assert.Contains((RedisKey)counterKey, keys);
            Assert.Equal(2, await database.KeyDeleteAsync([valueKey, counterKey]));
        }
    }

    [Fact]
    public async Task IntranetSessionAndDataProtection_RoundTripAcrossResp3Providers()
    {
        var certificate = CreateCertificatePfxBase64();
        var ticket = CreateTicket();
        string protectedCookie;
        string ticketKey;

        using (var firstProvider = fixture.CreateIntranetProvider(certificate))
        {
            var resources = firstProvider.GetRequiredService<LegacyDataProtectionResources>();
            Assert.Equal(RedisProtocol.Resp3, resources.Redis.GetServer(Assert.Single(resources.Redis.GetEndPoints())).Protocol);
            protectedCookie = CreateCookieFormat(firstProvider).Protect(ticket);
            ticketKey = await firstProvider.GetRequiredService<DistributedTicketStore>().StoreAsync(ticket);

            var database = resources.Redis.GetDatabase();
            var rawKeyRing = string.Join(
                Environment.NewLine,
                (await database.ListRangeAsync("legacy:intranet:data-protection-keys")).Select(value => value.ToString()));
            var rawTicket = await database.HashGetAsync("legacy-intranet:" + ticketKey, "data");
            Assert.False(rawTicket.IsNull);
            var rawTicketText = Encoding.UTF8.GetString((byte[])rawTicket!);
            Assert.Contains("encryptedSecret", rawKeyRing, StringComparison.Ordinal);
            Assert.DoesNotContain("server-only-access-token", rawKeyRing, StringComparison.Ordinal);
            Assert.DoesNotContain("server-only-refresh-token", rawKeyRing, StringComparison.Ordinal);
            Assert.DoesNotContain("server-only-access-token", rawTicketText, StringComparison.Ordinal);
            Assert.DoesNotContain("server-only-refresh-token", rawTicketText, StringComparison.Ordinal);
        }

        using var secondProvider = fixture.CreateIntranetProvider(certificate);
        var restoredCookie = CreateCookieFormat(secondProvider).Unprotect(protectedCookie);
        var restoredTicket = await secondProvider.GetRequiredService<DistributedTicketStore>().RetrieveAsync(ticketKey);
        Assert.NotNull(restoredCookie);
        Assert.NotNull(restoredTicket);
        AssertTicket(restoredCookie);
        AssertTicket(restoredTicket);
    }

    [Fact]
    public async Task IntranetOrderStateAndLuaLease_RoundTripOverResp3()
    {
        using var provider = fixture.CreateIntranetProvider(CreateCertificatePfxBase64());
        var resources = provider.GetRequiredService<LegacyDataProtectionResources>();
        var store = new RedisOrderCreationStateStore(
            resources,
            NullLogger<RedisOrderCreationStateStore>.Instance);
        var workflowKey = Guid.NewGuid().ToString("N");
        var checkpoint = new OrderCreationCheckpoint(
            "request-fingerprint",
            "attempt-id",
            OrderCreationPhase.Active,
            null,
            [],
            [],
            0,
            null);

        await store.SetAsync(workflowKey, checkpoint, default);
        var restoredCheckpoint = await store.GetAsync(workflowKey, default);
        Assert.NotNull(restoredCheckpoint);
        Assert.Equal(checkpoint.Fingerprint, restoredCheckpoint.Fingerprint);
        Assert.Equal(checkpoint.DownstreamAttemptId, restoredCheckpoint.DownstreamAttemptId);
        Assert.Equal(checkpoint.Phase, restoredCheckpoint.Phase);
        Assert.Equal(checkpoint.OrderId, restoredCheckpoint.OrderId);
        Assert.Empty(restoredCheckpoint.StoredFiles);
        Assert.Empty(restoredCheckpoint.LinkedFileIds);
        Assert.Equal(checkpoint.LinkedFileCount, restoredCheckpoint.LinkedFileCount);
        Assert.Null(restoredCheckpoint.Result);
        Assert.Equal(
            "lease-completed",
            await store.ExecuteLockedAsync(
                workflowKey,
                _ => Task.FromResult("lease-completed"),
                default));

        var database = resources.Redis.GetDatabase();
        Assert.False(await database.KeyExistsAsync($"legacy:intranet:order-create:lock:{workflowKey}"));
        await store.RemoveAsync(workflowKey, default);
        Assert.Null(await store.GetAsync(workflowKey, default));
    }

    private static AuthenticationTicket CreateTicket()
    {
        var properties = new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) };
        properties.StoreTokens(
        [
            new AuthenticationToken { Name = "access_token", Value = "server-only-access-token" },
            new AuthenticationToken { Name = "refresh_token", Value = "server-only-refresh-token" },
        ]);
        return new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "employee-id")],
                CookieAuthenticationDefaults.AuthenticationScheme)),
            properties,
            CookieAuthenticationDefaults.AuthenticationScheme);
    }

    private static TicketDataFormat CreateCookieFormat(IServiceProvider services) =>
        new(services.GetRequiredService<IDataProtectionProvider>().CreateProtector(
            "Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationMiddleware",
            CookieAuthenticationDefaults.AuthenticationScheme,
            "v2"));

    private static void AssertTicket(AuthenticationTicket ticket)
    {
        Assert.Equal("employee-id", ticket.Principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal("server-only-access-token", ticket.Properties.GetTokenValue("access_token"));
        Assert.Equal("server-only-refresh-token", ticket.Properties.GetTokenValue("refresh_token"));
    }

    private static string CreateCertificatePfxBase64()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Legacy.Maliev.AppHost.RedisResp3CompatibilityTests",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        return Convert.ToBase64String(certificate.Export(X509ContentType.Pkcs12, RedisResp3Fixture.CertificatePassword));
    }
}

public sealed class RedisResp3Fixture : IAsyncLifetime
{
    public const string CertificatePassword = "redis-resp3-integration";
    private const string RedisPassword = "local-redis-resp3-only";
    private readonly RedisContainer redis = new RedisBuilder("redis:8.4-alpine")
        .WithCommand("--requirepass", RedisPassword)
        .Build();

    public Task InitializeAsync() => redis.StartAsync();

    public Task DisposeAsync() => redis.DisposeAsync().AsTask();

    public async Task<ConnectionMultiplexer> ConnectAsync()
    {
        var options = ConfigurationOptions.Parse(redis.GetConnectionString());
        options.Password = RedisPassword;
        options.Protocol = RedisProtocol.Resp3;
        options.AbortOnConnectFail = true;
        return await ConnectionMultiplexer.ConnectAsync(options);
    }

    public async Task<ConnectionMultiplexer> ConnectWithoutPasswordAsync()
    {
        var options = ConfigurationOptions.Parse(redis.GetConnectionString());
        options.Protocol = RedisProtocol.Resp3;
        options.AbortOnConnectFail = true;
        return await ConnectionMultiplexer.ConnectAsync(options);
    }

    public ServiceProvider CreateIntranetProvider(string certificatePfxBase64)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Production",
            ApplicationName = "Legacy.Maliev.AppHost.RedisResp3CompatibilityTests",
        });
        builder.Configuration["ConnectionStrings:redis"] =
            $"{redis.GetConnectionString()},password={RedisPassword},protocol=resp3";
        builder.Configuration["DataProtection:CertificatePfxBase64"] = certificatePfxBase64;
        builder.Configuration["DataProtection:CertificatePassword"] = CertificatePassword;
        builder.AddLegacyIntranetDataProtection();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<DistributedTicketStore>();
        return builder.Services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }
}
