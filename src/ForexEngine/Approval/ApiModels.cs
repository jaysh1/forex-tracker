namespace ForexHitl;

public sealed record LoginRequest(string? Password);

public sealed record LoginResponse(string Token);

public sealed record DecisionResult(bool Executed, string Message);
