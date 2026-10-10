namespace Countinghouse.SimpleFin;

// Why a sync failed, kept as a closed set because the UI has to react differently to each:
// PaymentRequired and AuthenticationFailed both look like "sync broken" but only one of them is
// fixed by re-linking, and telling the user the wrong one wastes a trip to the Bridge site.
public enum SimpleFinFailure
{
    Network,
    PaymentRequired,
    AuthenticationFailed,
    RateLimited,
    TokenAlreadyClaimed,
    InvalidResponse,
    ServerError,
}

public sealed class SimpleFinException(string message, SimpleFinFailure failure, Exception? inner = null)
    : Exception(message, inner)
{
    public SimpleFinFailure Failure { get; } = failure;

    // True when the stored Access URL can't be used again and the only fix is claiming a new
    // setup token. A connection that merely stops syncing is an invisible-staleness failure, so
    // this drives an explicit "re-link" prompt rather than a silent stop.
    public bool RequiresRelink =>
        Failure is SimpleFinFailure.AuthenticationFailed or SimpleFinFailure.TokenAlreadyClaimed;
}
