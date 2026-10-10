using System.Net;
using System.Text;
using Countinghouse.SimpleFin;

namespace Countinghouse.Tests;

public class SimpleFinClientTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static string Token(string claimUrl) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(claimUrl));

    private const string AccessUrl = "https://user123:pass456@beta-bridge.simplefin.org/simplefin";

    // ---- Access URL -------------------------------------------------------------------

    [Fact]
    public void AccessUrlSplitsCredentialsOutOfTheUrl()
    {
        Assert.True(SimpleFinAccessUrl.TryParse(AccessUrl, out var parsed, out _));

        Assert.Equal("user123", parsed.UserName);
        Assert.Equal("pass456", parsed.Password);
        // The credentials must not survive into the URL we actually request, or they end up in
        // any log or exception message that echoes the request URI.
        Assert.DoesNotContain("user123", parsed.BaseUrl);
        Assert.DoesNotContain("pass456", parsed.BaseUrl);
        Assert.Equal("https://beta-bridge.simplefin.org/simplefin", parsed.BaseUrl);
    }

    [Fact]
    public void AccessUrlWithoutCredentialsIsRejected()
    {
        Assert.False(SimpleFinAccessUrl.TryParse(
            "https://beta-bridge.simplefin.org/simplefin", out _, out var error));
        Assert.Contains("credentials", error);
    }

    [Fact]
    public void AuthorizationParameterIsBase64UserColonPassword()
    {
        Assert.True(SimpleFinAccessUrl.TryParse(AccessUrl, out var parsed, out _));
        Assert.Equal("user123:pass456",
            Encoding.UTF8.GetString(Convert.FromBase64String(parsed.AuthorizationParameter)));
    }

    // ---- Setup token ------------------------------------------------------------------

    [Fact]
    public void SetupTokenDecodesToItsClaimUrl()
    {
        var url = "https://beta-bridge.simplefin.org/simplefin/claim/DEMO123";
        Assert.Equal(url, SimpleFinClient.DecodeSetupToken(Token(url)));
    }

    [Fact]
    public void SetupTokenSurvivesPastedWhitespace()
    {
        // The Bridge shows the token wrapped across lines, so a paste often carries newlines.
        var url = "https://beta-bridge.simplefin.org/simplefin/claim/DEMO123";
        var wrapped = Token(url).Insert(8, "\n  ");
        Assert.Equal(url, SimpleFinClient.DecodeSetupToken(wrapped));
    }

    [Theory]
    [InlineData("not base64 at all!")]
    [InlineData("")]
    public void MalformedSetupTokenIsRejectedBeforeAnythingIsSpent(string token)
    {
        var ex = Assert.Throws<SimpleFinException>(() => SimpleFinClient.DecodeSetupToken(token));
        Assert.Equal(SimpleFinFailure.InvalidResponse, ex.Failure);
    }

    [Fact]
    public void SetupTokenDecodingToNonUrlIsRejected()
    {
        var ex = Assert.Throws<SimpleFinException>(
            () => SimpleFinClient.DecodeSetupToken(Token("this is not a url")));
        Assert.Equal(SimpleFinFailure.InvalidResponse, ex.Failure);
    }

    // ---- Claim ------------------------------------------------------------------------

    [Fact]
    public async Task ClaimPostsToTheDecodedUrlAndReturnsTheAccessUrl()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, AccessUrl, "text/plain");
        var client = new SimpleFinClient(new HttpClient(handler));

        var result = await client.ClaimAccessUrlAsync(
            Token("https://beta-bridge.simplefin.org/simplefin/claim/DEMO123"));

        Assert.Equal(AccessUrl, result);
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Equal("https://beta-bridge.simplefin.org/simplefin/claim/DEMO123",
            handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task ClaimingAnAlreadyUsedTokenSaysToGenerateANewOne()
    {
        var client = new SimpleFinClient(StubHttpMessageHandler.Client(HttpStatusCode.Forbidden, ""));

        var ex = await Assert.ThrowsAsync<SimpleFinException>(
            () => client.ClaimAccessUrlAsync(Token("https://beta-bridge.simplefin.org/claim/X")));

        Assert.Equal(SimpleFinFailure.TokenAlreadyClaimed, ex.Failure);
        Assert.True(ex.RequiresRelink);
    }

    [Fact]
    public async Task ClaimResponseThatIsNotAnAccessUrlIsRejected()
    {
        var client = new SimpleFinClient(
            StubHttpMessageHandler.Client(HttpStatusCode.OK, "<html>nope</html>"));

        var ex = await Assert.ThrowsAsync<SimpleFinException>(
            () => client.ClaimAccessUrlAsync(Token("https://beta-bridge.simplefin.org/claim/X")));

        Assert.Equal(SimpleFinFailure.InvalidResponse, ex.Failure);
    }

    [Fact]
    public async Task NetworkFailureDuringClaimSaysTheTokenIsStillUnspent()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "")
        {
            ThrowOnSend = new HttpRequestException("no route to host"),
        };
        var client = new SimpleFinClient(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<SimpleFinException>(
            () => client.ClaimAccessUrlAsync(Token("https://beta-bridge.simplefin.org/claim/X")));

        Assert.Equal(SimpleFinFailure.Network, ex.Failure);
        Assert.Contains("NOT been used", ex.Message);
    }

    // ---- GET /accounts ----------------------------------------------------------------

    [Fact]
    public async Task AccountsRequestSendsBasicAuthAndTheExpectedQuery()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, Fixture("simplefin-accounts.json"));
        var client = new SimpleFinClient(new HttpClient(handler));

        await client.GetAccountsAsync(AccessUrl, new DateOnly(2026, 8, 10));

        var request = handler.LastRequest;
        Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
        Assert.Equal("user123:pass456", Encoding.UTF8.GetString(
            Convert.FromBase64String(request.Headers.Authorization.Parameter!)));

        var uri = request.RequestUri!.ToString();
        Assert.Contains("/accounts?", uri);
        Assert.Contains($"start-date={SimpleFinClient.ToUnixSeconds(new DateOnly(2026, 8, 10))}", uri);
        Assert.Contains("pending=1", uri);
    }

    [Fact]
    public void UnixSecondsIsMidnightUtcOfTheRequestedDay()
    {
        Assert.Equal(1786320000, SimpleFinClient.ToUnixSeconds(new DateOnly(2026, 8, 10)));
    }

    // Regression test: `posted` can date a purchase a day later than it actually happened, which
    // is not what the bank's own statement shows.
    [Fact]
    public void TheTransactionDateWinsOverThePostingDate()
    {
        var transaction = new SimpleFinTransaction
        {
            // transacted 08-08, posted 08-09, both midday UTC.
            TransactedAt = 1786276800 - 86400,
            Posted = 1786276800,
        };

        Assert.Equal(new DateOnly(2026, 8, 8), transaction.PostedDate);
    }

    [Fact]
    public void ThePostingDateIsUsedWhenNoTransactionDateIsGiven()
    {
        // `transacted_at` is optional in the protocol, so the fallback has to hold.
        var transaction = new SimpleFinTransaction { TransactedAt = null, Posted = 1786276800 };

        Assert.Equal(new DateOnly(2026, 8, 9), transaction.PostedDate);
    }

    [Fact]
    public async Task AmountsParseFromBothStringAndNumberForms()
    {
        var client = new SimpleFinClient(
            StubHttpMessageHandler.Client(HttpStatusCode.OK, Fixture("simplefin-accounts.json")));

        var set = await client.GetAccountsAsync(AccessUrl);

        var checking = set.Accounts[0];
        Assert.Equal(1284.37m, checking.Balance);          // "1284.37"
        Assert.Equal(1234.37m, checking.AvailableBalance);
        Assert.Equal(-52.18m, checking.Transactions[0].Amount);

        var card = set.Accounts[1];
        Assert.Equal(-430.5m, card.Balance);               // bare JSON number
        Assert.Null(card.AvailableBalance);
    }

    [Fact]
    public async Task PartialFailureIsVisibleOnAnOtherwiseSuccessfulResponse()
    {
        var client = new SimpleFinClient(
            StubHttpMessageHandler.Client(HttpStatusCode.OK, Fixture("simplefin-accounts.json")));

        var set = await client.GetAccountsAsync(AccessUrl);

        // A 200 with accounts in it can still mean one institution silently returned nothing.
        Assert.Single(set.Errors);
        Assert.Equal(2, set.Accounts.Count);
    }

    [Fact]
    public async Task TransactionFieldsMapIncludingPendingAndTheMissingPayeeFallback()
    {
        var client = new SimpleFinClient(
            StubHttpMessageHandler.Client(HttpStatusCode.OK, Fixture("simplefin-accounts.json")));

        var transactions = (await client.GetAccountsAsync(AccessUrl)).Accounts[0].Transactions;

        Assert.Equal("TXN-0001", transactions[0].Id);
        // Posted 08-10, transacted 08-09 — the transaction date is the one that lands.
        Assert.Equal(new DateOnly(2026, 8, 9), transactions[0].PostedDate);
        Assert.Equal("Sample Grocery", transactions[0].BestPayee);
        Assert.False(transactions[0].Pending);

        // No `payee` field on this one — the register still needs a non-empty payee.
        Assert.Equal("SAMPLE DIRECT DEPOSIT", transactions[1].BestPayee);
        // No `transacted_at` either, so this one falls back to its posting date.
        Assert.Equal(new DateOnly(2026, 8, 11), transactions[1].PostedDate);

        Assert.True(transactions[2].Pending);
    }

    [Fact]
    public async Task OrgLabelFallsBackToDomainWhenNameIsAbsent()
    {
        var client = new SimpleFinClient(
            StubHttpMessageHandler.Client(HttpStatusCode.OK, Fixture("simplefin-accounts.json")));

        var set = await client.GetAccountsAsync(AccessUrl);

        Assert.Equal("Example Bank", set.Accounts[0].Org!.DisplayName);
        Assert.Equal("examplecard.com", set.Accounts[1].Org!.DisplayName);
    }

    [Fact]
    public async Task PaymentRequiredIsDistinctFromAuthFailureAndDoesNotAskForARelink()
    {
        var client = new SimpleFinClient(
            StubHttpMessageHandler.Client(HttpStatusCode.PaymentRequired, ""));

        var ex = await Assert.ThrowsAsync<SimpleFinException>(() => client.GetAccountsAsync(AccessUrl));

        Assert.Equal(SimpleFinFailure.PaymentRequired, ex.Failure);
        Assert.False(ex.RequiresRelink);
    }

    [Fact]
    public async Task RateLimitingIsNamedAndDoesNotLookLikeABrokenConnection()
    {
        var client = new SimpleFinClient(
            StubHttpMessageHandler.Client(HttpStatusCode.TooManyRequests, ""));

        var ex = await Assert.ThrowsAsync<SimpleFinException>(() => client.GetAccountsAsync(AccessUrl));

        Assert.Equal(SimpleFinFailure.RateLimited, ex.Failure);
        // Re-linking would spend a setup token to fix a problem that fixes itself.
        Assert.False(ex.RequiresRelink);
        Assert.Contains("24 syncs", ex.Message);
    }

    [Fact]
    public async Task ForbiddenAsksForARelink()
    {
        var client = new SimpleFinClient(StubHttpMessageHandler.Client(HttpStatusCode.Forbidden, ""));

        var ex = await Assert.ThrowsAsync<SimpleFinException>(() => client.GetAccountsAsync(AccessUrl));

        Assert.Equal(SimpleFinFailure.AuthenticationFailed, ex.Failure);
        Assert.True(ex.RequiresRelink);
    }

    [Fact]
    public async Task UnparseableBodyDoesNotLeakTheResponseIntoTheErrorMessage()
    {
        var client = new SimpleFinClient(
            StubHttpMessageHandler.Client(HttpStatusCode.OK, "{ \"accounts\": [ nonsense "));

        var ex = await Assert.ThrowsAsync<SimpleFinException>(() => client.GetAccountsAsync(AccessUrl));

        Assert.Equal(SimpleFinFailure.InvalidResponse, ex.Failure);
        // Response bodies carry real balances and payee names; they must not end up in a
        // message that gets rendered on a page or written to a log.
        Assert.DoesNotContain("nonsense", ex.Message);
    }

    [Fact]
    public async Task StoredAccessUrlThatIsNoLongerUsableAsksForARelinkRatherThanCallingOut()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "{}");
        var client = new SimpleFinClient(new HttpClient(handler));

        // What a failed decryption looks like downstream: empty or garbled ciphertext.
        var ex = await Assert.ThrowsAsync<SimpleFinException>(
            () => client.GetAccountsAsync("not-a-url"));

        Assert.Equal(SimpleFinFailure.AuthenticationFailed, ex.Failure);
        Assert.True(ex.RequiresRelink);
        Assert.Empty(handler.Requests);
    }
}
