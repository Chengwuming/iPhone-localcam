using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace LocalCam.Server.Pairing;

public sealed record PairingSession(string Id, string Token, DateTimeOffset ExpiresAt);

public sealed class PairingManager(TimeProvider timeProvider)
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, PairingSessionState> sessions = new();

    public PairingSession Create()
    {
        RemoveExpired();
        var session = new PairingSession(
            ToUrlSafeToken(RandomNumberGenerator.GetBytes(16)),
            ToUrlSafeToken(RandomNumberGenerator.GetBytes(32)),
            timeProvider.GetUtcNow().Add(TokenLifetime));
        sessions[session.Id] = new PairingSessionState(session);
        return session;
    }

    public bool IsPending(string sessionId, string token)
    {
        return TryGetValid(sessionId, token, out var state) && !state.Consumed;
    }

    public bool Consume(string sessionId, string token)
    {
        if (!TryGetValid(sessionId, token, out var state))
        {
            return false;
        }

        lock (state)
        {
            if (state.Consumed || timeProvider.GetUtcNow() >= state.Session.ExpiresAt)
            {
                return false;
            }

            state.Consumed = true;
            return true;
        }
    }

    public int RemoveExpired()
    {
        var now = timeProvider.GetUtcNow();
        var removed = 0;
        foreach (var item in sessions)
        {
            if (item.Value.Session.ExpiresAt < now && sessions.TryRemove(item.Key, out _))
            {
                removed++;
            }
        }

        return removed;
    }

    private bool TryGetValid(string sessionId, string token, out PairingSessionState state)
    {
        state = null!;
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(token) ||
            !sessions.TryGetValue(sessionId, out var found) || found is null)
        {
            return false;
        }

        state = found;

        return timeProvider.GetUtcNow() < state.Session.ExpiresAt &&
               CryptographicOperations.FixedTimeEquals(
                   System.Text.Encoding.UTF8.GetBytes(state.Session.Token),
                   System.Text.Encoding.UTF8.GetBytes(token));
    }

    private static string ToUrlSafeToken(byte[] bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private sealed class PairingSessionState(PairingSession session)
    {
        public PairingSession Session { get; } = session;
        public bool Consumed { get; set; }
    }
}
