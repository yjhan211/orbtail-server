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
    [InlineData(false, AccountCredentialProvisionStatus.TokenCollision)]
    [InlineData(false, AccountCredentialProvisionStatus.PlayerAlreadyExists)]
    [InlineData(true, AccountCredentialProvisionStatus.TokenCollision)]
    [InlineData(true, AccountCredentialProvisionStatus.PlayerAlreadyExists)]
    public async Task FailedProvisionIsNotRetried(bool suppliedToken, AccountCredentialProvisionStatus status)
    {
        var store = new FailedProvisionStore(status);
        var service = new AccountTokenService(store);
        string? token = suppliedToken ? network.helpers.OpaqueToken.Create("acct_") : null;

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResolveAsync(token));

        Assert.Equal(1, store.Allocations);
        Assert.Equal(1, store.Provisions);
    }

    [Fact]
    public async Task ConcurrentTokenRegistrationUsesExistingAccountWithoutRetryingProvision()
    {
        var store = new FailedProvisionStore(AccountCredentialProvisionStatus.TokenCollision, true);
        var service = new AccountTokenService(store);
        string token = network.helpers.OpaqueToken.Create("acct_");

        var result = await service.ResolveAsync(token);

        Assert.Equal(202, result.PlayerId);
        Assert.False(result.IsNewAccount);
        Assert.Equal(1, store.Allocations);
        Assert.Equal(1, store.Provisions);
    }

    private sealed class FailedProvisionStore(AccountCredentialProvisionStatus status, bool concurrentRegistration = false)
        : IAccountCredentialStore
    {
        public int Allocations { get; private set; }
        public int Provisions { get; private set; }

        public Task<long> AllocatePlayerIdAsync()
        {
            Allocations++;
            return Task.FromResult(101L);
        }

        public Task<AccountCredential?> FindByTokenHashAsync(string tokenHash) =>
            Task.FromResult<AccountCredential?>(concurrentRegistration && Provisions > 0
                ? new AccountCredential(202, tokenHash) : null);

        public Task<AccountCredentialProvisionResult> ProvisionAsync(long playerId, string proposedToken, string proposedTokenHash)
        {
            Provisions++;
            return Task.FromResult(new AccountCredentialProvisionResult(status));
        }
    }

    private sealed class CredentialStore : IAccountCredentialStore
    {
        private AccountCredential? _credential;

        public Task<long> AllocatePlayerIdAsync() => Task.FromResult(101L);

        public Task<AccountCredential?> FindByTokenHashAsync(string tokenHash) =>
            Task.FromResult(_credential?.TokenHash == tokenHash ? _credential : null);

        public Task<AccountCredentialProvisionResult> ProvisionAsync(
            long playerId, string proposedToken, string proposedTokenHash)
        {
            _credential = new AccountCredential(playerId, proposedTokenHash);
            return Task.FromResult(new AccountCredentialProvisionResult(
                AccountCredentialProvisionStatus.Created, proposedToken));
        }
    }
}
