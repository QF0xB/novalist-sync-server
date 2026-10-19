namespace Novalist.Sync.Server.Access;

public sealed class ConflictException(string message) : Exception(message);