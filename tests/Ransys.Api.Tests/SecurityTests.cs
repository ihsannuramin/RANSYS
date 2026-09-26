using System.Net;
using System.Text;
using Ransys.Api.Security;
using Ransys.Application;
using Ransys.Testing.PostgreSql;
using static Ransys.Api.Tests.ApiHarness;
using static Ransys.Api.Tests.TransactionApiTests;

namespace Ransys.Api.Tests;

/// <summary>ADR-022: fail-closed production authentication and the Development/Test authenticator's checks.</summary>
[Collection(ApiCollection.Name)]
public sealed class SecurityTests(PostgresDatabaseFixture db, ApiFactory factory)
{
    private readonly ApiHarness _h = new(db, factory);

    [Fact]
    public async Task Production_without_development_authentication_rejects_every_request_with_401()
    {
        var s = await _h.NewScenario();
        await using var production = new CustomApiFactory("Production", TestDatabaseSettings.ConnectionString, developmentAuthentication: false);
        var client = new ApiHarness(db, production);

        await AssertError(await client.Post(s.Channel, "/api/v1/payments", Payment(s)), HttpStatusCode.Unauthorized, "3001");
        await AssertError(await client.Get(s.Channel, $"/api/v1/transactions/{Guid.CreateVersion7()}"), HttpStatusCode.Unauthorized, "3001");
        Assert.Equal(HttpStatusCode.OK, (await client.Http.GetAsync("/health")).StatusCode);
        Assert.Equal(0, s.Adapter.CallCount);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Development_authentication_outside_development_or_test_stops_the_host(string environment)
    {
        using var misconfigured = new CustomApiFactory(environment, TestDatabaseSettings.ConnectionString, developmentAuthentication: true);

        var error = Assert.ThrowsAny<Exception>(() => misconfigured.CreateClient());

        Assert.Contains("AllowDevelopmentAuthentication", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replayed_nonce_is_401_and_the_replay_never_reaches_processing()
    {
        var s = await _h.NewScenario();
        var nonce = Guid.NewGuid().ToString("N");

        var first = await _h.Post(s.Channel, "/api/v1/payments", Payment(s), tweak: r => Sign(r, s.Channel, nonce: nonce));
        var replay = await _h.Post(s.Channel, "/api/v1/payments", Payment(s), tweak: r => Sign(r, s.Channel, nonce: nonce));
        var upperCaseClient = await _h.Post(s.Channel, "/api/v1/payments", Payment(s), tweak: r =>
        {
            Sign(r, s.Channel, nonce: nonce);
            r.Headers.Remove("X-Ransys-Client-Id");
            r.Headers.Add("X-Ransys-Client-Id", s.Channel.ToString("D").ToUpperInvariant());
        });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await AssertError(replay, HttpStatusCode.Unauthorized, "3001");
        await AssertError(upperCaseClient, HttpStatusCode.Unauthorized, "3001");
        Assert.Equal(1, s.Adapter.CallCount);
    }

    [Theory]
    [InlineData(-10)]
    [InlineData(10)]
    public async Task Timestamp_outside_five_minutes_is_401(int minutes)
    {
        var s = await _h.NewScenario();

        var response = await _h.Post(s.Channel, "/api/v1/payments", Payment(s), tweak: r => Sign(r, s.Channel, timestamp: DateTimeOffset.UtcNow.AddMinutes(minutes)));

        await AssertError(response, HttpStatusCode.Unauthorized, "3001");
        Assert.Equal(0, s.Adapter.CallCount);
    }

    [Theory]
    [InlineData("X-Ransys-Client-Id")]
    [InlineData("X-Ransys-Timestamp")]
    [InlineData("X-Ransys-Nonce")]
    public async Task Missing_security_header_is_401(string header)
    {
        var s = await _h.NewScenario();

        var response = await _h.Post(s.Channel, "/api/v1/payments", Payment(s), tweak: r => r.Headers.Remove(header));

        await AssertError(response, HttpStatusCode.Unauthorized, "3001");
    }

    [Fact]
    public async Task Unknown_or_inactive_channel_and_non_uuid_client_id_are_401()
    {
        var s = await _h.NewScenario();
        await _h.Execute("UPDATE core.channels SET status = 'INACTIVE' WHERE channel_id = @id", new { id = s.Channel });

        await AssertError(await _h.Post(s.Channel, "/api/v1/payments", Payment(s)), HttpStatusCode.Unauthorized, "3001");
        await AssertError(await _h.Post(Guid.CreateVersion7(), "/api/v1/payments", Payment(s)), HttpStatusCode.Unauthorized, "3001");
        await AssertError(
            await _h.Post(s.Channel, "/api/v1/payments", Payment(s), tweak: r =>
            {
                r.Headers.Remove("X-Ransys-Client-Id");
                r.Headers.Add("X-Ransys-Client-Id", "merchant-a");
            }),
            HttpStatusCode.Unauthorized, "3001");
    }

    [Fact]
    public async Task Content_digest_mismatch_or_malformed_digest_is_401()
    {
        var s = await _h.NewScenario();

        var tampered = await _h.Post(s.Channel, "/api/v1/payments", Payment(s), tweak: r =>
        {
            r.Headers.Remove("Content-Digest");
            r.Headers.Add("Content-Digest", ContentDigest.Create(Encoding.UTF8.GetBytes("{}")));
        });
        var malformed = await _h.Post(s.Channel, "/api/v1/payments", Payment(s), tweak: r =>
        {
            r.Headers.Remove("Content-Digest");
            r.Headers.Add("Content-Digest", "sha-256=not-a-byte-sequence");
        });

        await AssertError(tampered, HttpStatusCode.Unauthorized, "3001");
        await AssertError(malformed, HttpStatusCode.Unauthorized, "3001");
        Assert.Equal(0, s.Adapter.CallCount);
    }

    [Fact]
    public void Content_digest_follows_rfc_9530_sha_256()
    {
        var body = Encoding.UTF8.GetBytes("{\"hello\": \"world\"}");

        // RFC 9530 §2 example.
        Assert.Equal("sha-256=:X48E9qOokqqrvdts8nOJRJN3OWDUoyWxBf7kbu9DBPE=:", ContentDigest.Create(body));
        Assert.True(ContentDigest.Verify("sha-512=:AAAA:, sha-256=:X48E9qOokqqrvdts8nOJRJN3OWDUoyWxBf7kbu9DBPE=:", body));
        Assert.False(ContentDigest.Verify("sha-512=:AAAA:", body));
        Assert.False(ContentDigest.Verify("sha-256=:X48E9qOokqqrvdts8nOJRJN3OWDUoyWxBf7kbu9DBPE=:, sha-256=:X48E9qOokqqrvdts8nOJRJN3OWDUoyWxBf7kbu9DBPE=:", body));
        Assert.False(ContentDigest.Verify("sha-256=:AAAA:", body));
        Assert.False(ContentDigest.Verify("", body));
    }

    [Fact]
    public void In_memory_replay_protection_rejects_a_live_nonce_and_accepts_it_after_expiry()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var replay = new InMemoryReplayProtectionService(clock);

        Assert.True(replay.TryRegister("client-a", "n1", clock.UtcNow.AddMinutes(10)));
        Assert.False(replay.TryRegister("client-a", "n1", clock.UtcNow.AddMinutes(10)));
        Assert.True(replay.TryRegister("client-b", "n1", clock.UtcNow.AddMinutes(10)));
        clock.UtcNow = clock.UtcNow.AddMinutes(11);
        Assert.True(replay.TryRegister("client-a", "n1", clock.UtcNow.AddMinutes(10)));
    }

    [Fact]
    public async Task Fail_closed_services_never_succeed()
    {
        var request = new AuthenticationRequest("POST", "/api/v1/payments", Guid.NewGuid().ToString(), "t", "n", null, "sig=()", "sig=:AA==:", null, ReadOnlyMemory<byte>.Empty);

        Assert.False((await new FailClosedRequestAuthenticationService().AuthenticateAsync(request, CancellationToken.None)).Succeeded);
        Assert.False(await new FailClosedSignatureVerifier().VerifyAsync(request, CancellationToken.None));
    }

    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }
}
