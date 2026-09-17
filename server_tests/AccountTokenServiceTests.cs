using user_server.accounts;

public sealed class AccountTokenServiceTests
{
    [Theory]
    [InlineData("101")]
    [InlineData(" 101 ")]
    [InlineData("0")]
    [InlineData("-1")]
    public async Task NumericTokenIsRejected(string token)
    {
        var service = new AccountTokenService(new CredentialStore());

        await Assert.ThrowsAsync<AccountAuthenticationException>(() => service.ResolveAsync(token));
    }

    [Fact]
    public async Task NewAccountTokenCanBeUsedForLoginAgain()
    {
        var service = new AccountTokenService(new CredentialStore());
        var created = await service.ResolveAsync(null);
        var existing = await service.ResolveAsync(created.AccountToken);

        Assert.True(created.IsNewAccount);
        Assert.StartsWith("acct_", created.AccountToken);
        Assert.False(existing.IsNewAccount);
        Assert.Equal(created.PlayerId, existing.PlayerId);
        Assert.Equal(created.AccountToken, existing.AccountToken);
    }

    [Theory]
    [InlineData(false, AccountRegistrationStatus.TokenAlreadyRegistered)]
    [InlineData(false, AccountRegistrationStatus.PlayerAlreadyExists)]
    [InlineData(true, AccountRegistrationStatus.TokenAlreadyRegistered)]
    [InlineData(true, AccountRegistrationStatus.PlayerAlreadyExists)]
    public async Task FailedRegistrationIsNotRetried(bool suppliedToken, AccountRegistrationStatus status)
    {
        var store = new FailedRegistrationStore(status);
        var service = new AccountTokenService(store);
        string? token = suppliedToken ? network.helpers.OpaqueToken.Create("acct_") : null;

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResolveAsync(token));

        Assert.Equal(1, store.Allocations);
        Assert.Equal(1, store.Registrations);
    }

    [Fact]
    public async Task ConcurrentTokenRegistrationUsesExistingAccountWithoutRetryingRegistration()
    {
        var store = new FailedRegistrationStore(AccountRegistrationStatus.TokenAlreadyRegistered, true);
        var service = new AccountTokenService(store);
        string token = network.helpers.OpaqueToken.Create("acct_");

        var result = await service.ResolveAsync(token);

        Assert.Equal(202, result.PlayerId);
        Assert.False(result.IsNewAccount);
        Assert.Equal(1, store.Allocations);
        Assert.Equal(1, store.Registrations);
    }

    private sealed class FailedRegistrationStore(AccountRegistrationStatus status, bool concurrentRegistration = false)
        : IAccountCredentialStore
    {
        public int Allocations { get; private set; }
        public int Registrations { get; private set; }

        public Task<long> AllocatePlayerIdAsync()
        {
            Allocations++;
            return Task.FromResult(101L);
        }

        public Task<AccountCredential?> FindByTokenHashAsync(string tokenHash) =>
            Task.FromResult<AccountCredential?>(concurrentRegistration && Registrations > 0
                ? new AccountCredential(202, tokenHash) : null);

        public Task<AccountRegistrationResult> RegisterAsync(long playerId, string token, string tokenHash)
        {
            Registrations++;
            return Task.FromResult(new AccountRegistrationResult(status));
        }
    }

    private sealed class CredentialStore : IAccountCredentialStore
    {
        private AccountCredential? _credential;

        public Task<long> AllocatePlayerIdAsync() => Task.FromResult(101L);

        public Task<AccountCredential?> FindByTokenHashAsync(string tokenHash) =>
            Task.FromResult(_credential?.TokenHash == tokenHash ? _credential : null);

        public Task<AccountRegistrationResult> RegisterAsync(
            long playerId, string token, string tokenHash)
        {
            _credential = new AccountCredential(playerId, tokenHash);
            return Task.FromResult(new AccountRegistrationResult(
                AccountRegistrationStatus.Created, token));
        }
    }
}
