using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Countinghouse.SimpleFin;

// Every network call to SimpleFIN goes through here. Nothing in this class touches the database
// or decrypts anything: it takes an already-decrypted access URL as a parameter so the caller
// controls how long the plaintext lives, and so this is testable against a stub handler.
public sealed class SimpleFinClient(HttpClient http)
{
    // Claims a setup token, returning the raw Access URL.
    //
    // This is single-use and destructive: the token is spent whether or not we manage to store
    // the result, and a second attempt returns 403. Callers must persist the return value before
    // doing anything else that can fail.
    public async Task<string> ClaimAccessUrlAsync(string setupToken, CancellationToken cancellationToken = default)
    {
        var claimUrl = DecodeSetupToken(setupToken);

        HttpResponseMessage response;
        try
        {
            // The claim POST has no body; some servers still object to a missing Content-Length.
            using var request = new HttpRequestMessage(HttpMethod.Post, claimUrl)
            {
                Content = new StringContent("", Encoding.UTF8),
            };
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new SimpleFinException(
                "Couldn't reach the SimpleFIN Bridge to claim the token. The token has NOT been used — check the network and try again.",
                SimpleFinFailure.Network, ex);
        }

        using (response)
        {
            var body = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new SimpleFinException(
                    "The Bridge rejected this setup token. Setup tokens can only be claimed once — generate a new one at bridge.simplefin.org and paste that instead.",
                    SimpleFinFailure.TokenAlreadyClaimed);
            }
            if (response.StatusCode == HttpStatusCode.PaymentRequired)
            {
                throw new SimpleFinException(
                    "The Bridge reports payment required. Check the subscription at bridge.simplefin.org, then claim a fresh setup token.",
                    SimpleFinFailure.PaymentRequired);
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new SimpleFinException(
                    $"The Bridge returned {(int)response.StatusCode} when claiming the token.",
                    SimpleFinFailure.ServerError);
            }

            if (!SimpleFinAccessUrl.TryParse(body, out _, out var error))
            {
                throw new SimpleFinException(
                    $"The Bridge returned something that isn't a usable access URL ({error}) — the token may already be spent.",
                    SimpleFinFailure.InvalidResponse);
            }

            return body;
        }
    }

    // balancesOnly is what the mapping screen wants: it needs the remote account ids and names,
    // and pulling every transaction just to render a list of checkboxes is a lot of real
    // financial data to move around for nothing.
    public async Task<SimpleFinAccountSet> GetAccountsAsync(
        string accessUrl,
        DateOnly? startDate = null,
        bool includePending = true,
        bool balancesOnly = false,
        CancellationToken cancellationToken = default)
    {
        if (!SimpleFinAccessUrl.TryParse(accessUrl, out var parsed, out var parseError))
        {
            throw new SimpleFinException(
                $"The stored access URL is unusable ({parseError}). Re-link this institution.",
                SimpleFinFailure.AuthenticationFailed);
        }

        var query = new List<string>();
        if (startDate is { } start)
            query.Add($"start-date={ToUnixSeconds(start)}");
        if (includePending && !balancesOnly)
            query.Add("pending=1");
        if (balancesOnly)
            query.Add("balances-only=1");

        var uri = $"{parsed.BaseUrl}/accounts{(query.Count > 0 ? "?" + string.Join("&", query) : "")}";

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", parsed.AuthorizationParameter);
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new SimpleFinException(
                "Couldn't reach SimpleFIN. Nothing was changed locally.", SimpleFinFailure.Network, ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.PaymentRequired)
            {
                throw new SimpleFinException(
                    "SimpleFIN returned 402 Payment Required — the Bridge subscription has lapsed. Renew it at bridge.simplefin.org; no re-linking is needed.",
                    SimpleFinFailure.PaymentRequired);
            }
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                throw new SimpleFinException(
                    "SimpleFIN rejected the stored credentials. Re-link this institution to get a working connection.",
                    SimpleFinFailure.AuthenticationFailed);
            }
            if (response.StatusCode is HttpStatusCode.TooManyRequests)
            {
                // The Bridge allows 24 syncs per rolling 24 hours. Worth naming explicitly: it is
                // the one failure where the fix is to stop and wait rather than change anything,
                // and a bare "SimpleFIN returned 429" invites exactly the wrong response.
                throw new SimpleFinException(
                    "SimpleFIN's rate limit has been reached — it allows 24 syncs per rolling 24 hours. " +
                    "Nothing is wrong with the connection; wait a while and sync again.",
                    SimpleFinFailure.RateLimited);
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new SimpleFinException(
                    $"SimpleFIN returned {(int)response.StatusCode}.", SimpleFinFailure.ServerError);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            try
            {
                return JsonSerializer.Deserialize<SimpleFinAccountSet>(json, SimpleFinJson.Options)
                       ?? new SimpleFinAccountSet();
            }
            catch (JsonException ex)
            {
                // Deliberately not including the body: it holds real balances and payee names.
                throw new SimpleFinException(
                    "SimpleFIN returned a response that couldn't be parsed.", SimpleFinFailure.InvalidResponse, ex);
            }
        }
    }

    // The setup token is Base64 of the claim URL and nothing else. Validating it here means a
    // mistyped paste fails before the single-use POST rather than after it.
    internal static string DecodeSetupToken(string setupToken)
    {
        if (string.IsNullOrWhiteSpace(setupToken))
            throw new SimpleFinException("Paste the setup token from bridge.simplefin.org first.", SimpleFinFailure.InvalidResponse);

        var trimmed = new string(setupToken.Where(c => !char.IsWhiteSpace(c)).ToArray());

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(trimmed));
        }
        catch (FormatException ex)
        {
            throw new SimpleFinException(
                "That doesn't look like a SimpleFIN setup token — it should be one long Base64 string with no spaces.",
                SimpleFinFailure.InvalidResponse, ex);
        }

        if (!Uri.TryCreate(decoded.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new SimpleFinException(
                "The setup token decoded to something that isn't a claim URL. Generate a new token at bridge.simplefin.org.",
                SimpleFinFailure.InvalidResponse);
        }

        return uri.ToString();
    }

    // start-date is "on or after", and the protocol counts in whole seconds from the epoch.
    //
    // Midnight UTC of the requested day. The server filters on `posted`, which comes back
    // normalized to midday UTC, so this cleanly includes everything posted that day onward. Note
    // it is deliberately a touch wider than needed: rows are selected by `posted` but dated by
    // `transacted_at`, which is earlier, and erring wide is safe because each account discards
    // what it already has.
    internal static long ToUnixSeconds(DateOnly date) =>
        new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
}
