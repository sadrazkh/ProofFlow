using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProofFlow.Application.Abstractions;
using ProofFlow.Domain.Authorization;
using ProofFlow.Domain.Runners;
using ProofFlow.Domain.Workspaces;
using ProofFlow.Infrastructure.Common;
using ProofFlow.Infrastructure.Persistence;
using ProofFlow.Infrastructure.Runners;
using ProofFlow.Infrastructure.Security;
using ProofFlow.Infrastructure.Tenancy;
using Microsoft.Extensions.Configuration;

namespace ProofFlow.IntegrationTests;

/// <summary>
/// A machine somewhere else becomes a runner, once.
///
/// Everything here is about a credential, so every test says both halves: what the agent receives,
/// and what the database is left holding. A test that only checked the first would pass just as
/// well if the token were stored in the clear.
/// </summary>
public sealed class RunnerEnrollmentTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;

    private readonly Guid _workspaceId = Guid.CreateVersion7();
    private readonly Guid _userId = Guid.CreateVersion7();

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        await using var context = Db();
        await context.Database.EnsureCreatedAsync();

        context.Workspaces.Add(new Workspace { Id = _workspaceId, Name = "W", Slug = "w" });
        await context.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task A_code_is_readable_and_the_row_keeps_only_its_hash()
    {
        await using var context = Db();

        var (runner, code) = await Service(context).CreateAsync(_workspaceId, null, "Behind the firewall", null);

        // Four groups of four, from an alphabet without 0/O or 1/I/L in it — because somebody reads
        // this off a screen and types it into a terminal on another machine.
        code.Should().MatchRegex("^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$");
        code.Should().NotContainAny("0", "O", "1", "I", "L");

        runner.EnrollmentHash.Should().NotBe(code);
        runner.StateAt(DateTimeOffset.UtcNow).Should().Be(RunnerState.Waiting);
        runner.TokenHash.Should().BeNull();
    }

    [Fact]
    public async Task Enrolling_hands_over_two_secrets_and_stores_neither()
    {
        await using var context = Db();
        var service = Service(context);

        var (created, code) = await service.CreateAsync(_workspaceId, null, "Agent", null);

        var enrolled = await service.EnrollAsync(code, "build-01.internal", "1.0.0");

        enrolled.Should().NotBeNull();
        enrolled!.Token.Should().NotBeNullOrWhiteSpace();
        enrolled.SigningKey.Should().NotBeNullOrWhiteSpace();

        var row = await context.Runners.FirstAsync(runner => runner.Id == created.Id);

        // The token exists only as a hash, with a few readable characters so two runners can be
        // told apart on a page.
        row.TokenHash.Should().NotBe(enrolled.Token);
        row.TokenPreview.Should().Be(enrolled.Token[..RunnerService.PreviewLength]);

        // And the signing key only as ciphertext.
        row.SigningKeyCipher.Should().NotBe(enrolled.SigningKey);
        row.SigningKeyNonce.Should().NotBeNull();
        row.SigningKeyTag.Should().NotBeNull();

        row.Hostname.Should().Be("build-01.internal");
        row.Version.Should().Be("1.0.0");
        row.StateAt(DateTimeOffset.UtcNow).Should().Be(RunnerState.Ready);
    }

    [Fact]
    public async Task A_code_works_once()
    {
        // What stops one leaked code becoming two machines that both believe they are this runner.
        await using var context = Db();
        var service = Service(context);

        var (_, code) = await service.CreateAsync(_workspaceId, null, "Agent", null);

        (await service.EnrollAsync(code, "first", "1.0")).Should().NotBeNull();
        (await service.EnrollAsync(code, "second", "1.0")).Should().BeNull();
    }

    [Theory]
    [InlineData("lower case works")]
    [InlineData("no dashes")]
    [InlineData("spaces in it")]
    public async Task A_code_is_read_the_way_a_person_types_it(string how)
    {
        await using var context = Db();
        var service = Service(context);

        var (_, code) = await service.CreateAsync(_workspaceId, null, "Agent", null);

        var typed = how switch
        {
            "lower case works" => code.ToLowerInvariant(),
            "no dashes" => code.Replace("-", ""),
            _ => code.Replace("-", " "),
        };

        (await service.EnrollAsync(typed, null, null)).Should().NotBeNull(how);
    }

    [Fact]
    public async Task An_expired_code_is_refused()
    {
        await using var context = Db();

        var (runner, code) = await Service(context).CreateAsync(_workspaceId, null, "Agent", null);

        runner.EnrollmentExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await context.SaveChangesAsync();

        (await Service(context).EnrollAsync(code, null, null)).Should().BeNull();

        runner.StateAt(DateTimeOffset.UtcNow).Should().Be(RunnerState.Expired);
    }

    [Fact]
    public async Task A_fresh_code_can_be_issued_for_one_nobody_used_in_time()
    {
        await using var context = Db();
        var service = Service(context);

        var (runner, first) = await service.CreateAsync(_workspaceId, null, "Agent", null);

        var second = await service.ReissueAsync(_workspaceId, runner.Id);

        second.Should().NotBeNull().And.NotBe(first);

        // And the old one stops working, rather than there being two ways in.
        (await service.EnrollAsync(first, null, null)).Should().BeNull();
        (await service.EnrollAsync(second, null, null)).Should().NotBeNull();
    }

    [Fact]
    public async Task No_second_code_for_a_runner_that_is_already_enrolled()
    {
        // Two live codes for one runner is two agents claiming to be the same machine.
        await using var context = Db();
        var service = Service(context);

        var (runner, code) = await service.CreateAsync(_workspaceId, null, "Agent", null);
        await service.EnrollAsync(code, null, null);

        (await service.ReissueAsync(_workspaceId, runner.Id)).Should().BeNull();
    }

    [Fact]
    public async Task The_token_identifies_the_runner_and_stamps_the_poll()
    {
        await using var context = Db();
        var service = Service(context);

        var (created, code) = await service.CreateAsync(_workspaceId, null, "Agent", null);
        var enrolled = await service.EnrollAsync(code, null, null);

        var found = await service.AuthenticateAsync(enrolled!.Token);

        found.Should().NotBeNull();
        found!.Id.Should().Be(created.Id);

        // Every call an agent makes is evidence it is alive; there is no separate heartbeat worth
        // building.
        found.LastSeenAt.Should().NotBeNull();

        (await service.AuthenticateAsync(enrolled.Token + "x")).Should().BeNull();
        (await service.AuthenticateAsync(null)).Should().BeNull();
    }

    [Fact]
    public async Task Revoking_takes_the_credentials_with_it()
    {
        await using var context = Db();
        var service = Service(context);

        var (created, code) = await service.CreateAsync(_workspaceId, null, "Agent", null);
        var enrolled = await service.EnrollAsync(code, null, null);

        (await service.RevokeAsync(_workspaceId, created.Id)).Should().BeTrue();

        (await service.AuthenticateAsync(enrolled!.Token)).Should().BeNull();

        // Cleared rather than merely flagged: a revocation that depends on every future query
        // remembering to check a flag is a revocation waiting to be forgotten.
        var row = await context.Runners.FirstAsync(runner => runner.Id == created.Id);

        row.TokenHash.Should().BeNull();
        row.SigningKeyCipher.Should().BeNull();
        row.StateAt(DateTimeOffset.UtcNow).Should().Be(RunnerState.Revoked);
    }

    [Fact]
    public async Task An_agent_that_stops_polling_is_called_missing()
    {
        await using var context = Db();
        var service = Service(context);

        var (created, code) = await service.CreateAsync(_workspaceId, null, "Agent", null);
        await service.EnrollAsync(code, null, null);

        var row = await context.Runners.FirstAsync(runner => runner.Id == created.Id);

        row.StateAt(DateTimeOffset.UtcNow).Should().Be(RunnerState.Ready);

        // Derived rather than stored, because a stored status goes stale exactly when "has not been
        // heard from" starts to matter.
        row.StateAt(DateTimeOffset.UtcNow + Runner.Missing + TimeSpan.FromMinutes(1))
            .Should().Be(RunnerState.Missing);
    }

    [Fact]
    public async Task The_signing_key_comes_back_out_the_way_it_went_in()
    {
        await using var context = Db();
        var service = Service(context);

        var (created, code) = await service.CreateAsync(_workspaceId, null, "Agent", null);
        var enrolled = await service.EnrollAsync(code, null, null);

        var row = await context.Runners.FirstAsync(runner => runner.Id == created.Id);

        service.SigningKey(row).Should().Be(enrolled!.SigningKey);
    }

    // ---- setup ---------------------------------------------------------------------------------

    private RunnerService Service(ProofFlowDbContext context) =>
        new(context, Cipher(), new SystemClock(), new FixedUser(_workspaceId, _userId));

    /// <summary>
    /// A real cipher with a test key, rather than a fake.
    ///
    /// The thing worth proving is that the key survives a round trip through the same encryption the
    /// product uses, and a stub that returns its input would prove nothing.
    /// </summary>
    private static AesGcmSecretCipher Cipher() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ProofFlow:MasterKey"] = Convert.ToBase64String(new byte[32]),
            })
            .Build(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AesGcmSecretCipher>.Instance);

    private ProofFlowDbContext Db()
    {
        var options = new DbContextOptionsBuilder<SqliteProofFlowDbContext>()
            .UseSqlite(_connection)
            .Options;

        return new SqliteProofFlowDbContext(options, new FixedWorkspaceScope(_workspaceId));
    }

    private sealed class FixedUser(Guid workspaceId, Guid userId) : ICurrentUser
    {
        public Guid? UserId => userId;
        public Guid? WorkspaceId => workspaceId;
        public string DisplayName => "Tester";
        public WorkspaceRole? Role => WorkspaceRole.Owner;
        public bool IsAuthenticated => true;
        public bool Can(Capability capability) => true;
    }
}
