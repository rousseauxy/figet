using FiGet.Application.Tokens;
using FiGet.Domain.Entities;

namespace FiGet.Unit.Tests;

public sealed class TokenExpiryTests
{
    private static readonly DateTime Now = new(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(23, 1)]
    [InlineData(24, 1)]
    [InlineData(25, 2)]
    [InlineData(14 * 24, 14)]
    [InlineData((14 * 24) + 1, 15)]
    public void Days_left_are_whole_days_rounded_up(int hoursLeft, int expected) =>
        Assert.Equal(expected, TokenExpiry.DaysLeft(Token(Now.AddHours(hoursLeft)), Now));

    [Fact]
    public void A_token_that_does_not_end_is_revoked_or_has_ended_has_no_days_left()
    {
        Assert.Null(TokenExpiry.DaysLeft(Token(expires: null), Now));
        Assert.Null(TokenExpiry.DaysLeft(Token(Now), Now));
        Assert.Null(TokenExpiry.DaysLeft(Token(Now.AddHours(-1)), Now));

        var revoked = Token(Now.AddDays(5));
        revoked.RevokedUtc = Now.AddDays(-1);
        Assert.Null(TokenExpiry.DaysLeft(revoked, Now));
        Assert.False(TokenExpiry.EndsSoon(revoked, Now));
    }

    [Fact]
    public void Ending_soon_is_within_the_widest_threshold()
    {
        Assert.True(TokenExpiry.EndsSoon(Token(Now.AddDays(14)), Now));
        Assert.True(TokenExpiry.EndsSoon(Token(Now.AddHours(1)), Now));
        Assert.False(TokenExpiry.EndsSoon(Token(Now.AddDays(14).AddHours(1)), Now));
        Assert.False(TokenExpiry.EndsSoon(Token(expires: null), Now));
    }

    /// <summary>A token's last two weeks, a day at a time: warned at fourteen days and at three, and at no other.</summary>
    [Fact]
    public void A_token_is_warned_about_twice_in_its_last_two_weeks()
    {
        int? warnedAt = null;
        var warnings = new List<(int DaysLeft, int Threshold)>();
        for (var daysLeft = 30; daysLeft >= 1; daysLeft--)
        {
            if (TokenExpiry.Due(daysLeft, warnedAt) is { } threshold)
            {
                warnings.Add((daysLeft, threshold));
                warnedAt = threshold;
            }
        }

        Assert.Equal([(14, 14), (3, 3)], warnings);
    }

    [Theory]
    [InlineData(15, null, null)]
    [InlineData(14, null, 14)]
    [InlineData(5, null, 14)]
    [InlineData(5, 14, null)]
    [InlineData(3, 14, 3)]
    [InlineData(2, null, 3)]
    [InlineData(2, 3, null)]
    [InlineData(1, 3, null)]
    public void What_is_due_depends_on_what_was_already_said(int daysLeft, int? alreadyWarnedAt, int? expected) =>
        Assert.Equal(expected, TokenExpiry.Due(daysLeft, alreadyWarnedAt));

    private static AccessToken Token(DateTime? expires) =>
        new() { Name = "t", Hash = "h", Prefix = "figet_", Scopes = TokenScopes.Read, CreatedUtc = Now.AddDays(-30), ExpiresUtc = expires };
}
