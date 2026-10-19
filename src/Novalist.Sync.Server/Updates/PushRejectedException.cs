namespace Novalist.Sync.Server.Updates;

public sealed class PushRejectedException(string message) : Exception(message);