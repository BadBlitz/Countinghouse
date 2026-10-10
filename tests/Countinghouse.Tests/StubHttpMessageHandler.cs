using System.Net;
using System.Text;

namespace Countinghouse.Tests;

// Canned HTTP responses for the SimpleFIN client tests. No live network and no real Bridge
// account is ever touched: the point of these tests is the parsing and the status-code handling,
// both of which are cheaper to get right against recorded shapes than against a live sync that
// can only be run once against real money data.
public sealed class StubHttpMessageHandler(
    HttpStatusCode statusCode,
    string body,
    string contentType = "application/json") : HttpMessageHandler
{
    private readonly List<HttpRequestMessage> _requests = [];

    public IReadOnlyList<HttpRequestMessage> Requests => _requests;
    public HttpRequestMessage LastRequest => _requests[^1];

    // Set to throw a transport-level failure instead of returning a response.
    public Exception? ThrowOnSend { get; init; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _requests.Add(request);

        if (ThrowOnSend is not null)
            return Task.FromException<HttpResponseMessage>(ThrowOnSend);

        return Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
            RequestMessage = request,
        });
    }

    public static HttpClient Client(HttpStatusCode statusCode, string body) =>
        new(new StubHttpMessageHandler(statusCode, body));
}
