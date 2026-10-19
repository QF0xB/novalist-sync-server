namespace Novalist.Sync.Contracts;

public static class Protocol
{
    public const byte Version = 1;
    public const string HubPath = "/sync";
}

public enum MsgType : byte
{
    Update = 1,
    Snapshot = 2,
    Presence = 3
}