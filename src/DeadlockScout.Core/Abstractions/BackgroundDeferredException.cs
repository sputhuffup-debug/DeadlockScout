namespace DeadlockScout.Core.Abstractions;

/// <summary>
/// A background request asked for while a match is live. Background work waits for the match to end (it never competes
/// with the lobby's calls), so the request is refused at once instead of being held until the HTTP timeout kills it;
/// the caller tries again later, usually from the between-match prefetch. Not a failure, so it is logged quietly.
/// </summary>
public sealed class BackgroundDeferredException(string message) : Exception(message);
