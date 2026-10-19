namespace Novalist.Sync.Server.Access;

public sealed class AccessDeniedException(string message) : Exception(message);