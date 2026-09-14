using FiGet.Application.Accounts;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;

namespace FiGet.Unit.Tests;

public sealed class AccountSignInTests
{
    /// <summary>
    /// Found by the 2026-09-14 review: an unknown name hashed a decoy on every attempt and then verified against it, twice the
    /// work of a wrong password (96 ms against 50 ms measured), which told an outsider which names exist. The decoy is
    /// hashed once; every attempt after that verifies only.
    /// </summary>
    [Fact]
    public async Task An_unknown_name_costs_one_verification_like_a_wrong_password()
    {
        var hasher = new CountingHasher();
        var accounts = new AccountService(new NoUsers(), hasher, TimeProvider.System);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.Equal(SignInStatus.Invalid, (await accounts.SignInAsync("nobody-" + attempt, "guess", CancellationToken.None)).Status);
        }

        Assert.Equal(5, hasher.Verifies);
        Assert.True(hasher.Hashes <= 1, $"Hashed {hasher.Hashes} times for five attempts.");
    }

    private sealed class CountingHasher : IPasswordHasher
    {
        public int Hashes { get; private set; }

        public int Verifies { get; private set; }

        public string Hash(string password)
        {
            Hashes++;
            return "hash:" + password;
        }

        public PasswordCheck Verify(string hash, string password)
        {
            Verifies++;
            return PasswordCheck.Failed;
        }
    }

    private sealed class NoUsers : IUserStore
    {
        public Task<User?> FindByUserNameAsync(string userName, CancellationToken cancellationToken) => Task.FromResult<User?>(null);

        public Task<User?> FindAsync(int key, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> EmailInUseAsync(string email, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<User>> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> AnyAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> CountActiveSuperAdminsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> AddAsync(User user, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> UpdateAsync(User user, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> RecordFailedSignInAsync(int key, int maxFailures, DateTime lockedUntilUtc, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(int key, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
