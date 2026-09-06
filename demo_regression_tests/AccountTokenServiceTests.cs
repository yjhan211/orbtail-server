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
