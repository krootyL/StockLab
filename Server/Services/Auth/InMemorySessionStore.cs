using System.Collections.Concurrent;

namespace StockLab.Services.Auth;

/// <summary>
/// 서버 인메모리 세션 저장소 (sid -> 절대 만료 시각).
/// - 발급 시 30분 만료 시각 기록
/// - 요청마다 단순 시간 비교로 유효성 검증
/// - 연장 시 요청 시점 기준 30분 타이머 리셋
/// - 로그아웃 시 즉시 제거, 서버 재시작 시 메모리 초기화로 모든 기존 세션 자동 무효화
/// </summary>
public sealed class InMemorySessionStore
{
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new(StringComparer.Ordinal);

    public (string SessionId, DateTimeOffset ExpiresAtUtc) IssueSession()
    {
        var sid = Guid.NewGuid().ToString("N");
        var expiresAtUtc = DateTimeOffset.UtcNow.Add(SessionLifetime);
        _sessions[sid] = expiresAtUtc;

        if (_sessions.Count > 1000)
        {
            CleanupExpired();
        }

        return (sid, expiresAtUtc);
    }

    public (string SessionId, DateTimeOffset ExpiresAtUtc) ResetTimer(string? existingSessionId)
    {
        var now = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(existingSessionId) &&
            _sessions.TryGetValue(existingSessionId, out var prevExpiry) &&
            now < prevExpiry)
        {
            var nextExpiry = now.Add(SessionLifetime);
            _sessions[existingSessionId] = nextExpiry;
            return (existingSessionId, nextExpiry);
        }

        return IssueSession();
    }

    public bool Validate(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        if (!_sessions.TryGetValue(sessionId, out var expiresAtUtc)) return false;

        if (DateTimeOffset.UtcNow >= expiresAtUtc)
        {
            _sessions.TryRemove(sessionId, out _);
            return false;
        }

        return true;
    }

    public void Revoke(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        _sessions.TryRemove(sessionId, out _);
    }

    private void CleanupExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in _sessions)
        {
            if (now >= kv.Value)
            {
                _sessions.TryRemove(kv.Key, out _);
            }
        }
    }
}

