namespace Novalist.Sync.Contracts;

public sealed record Envelope(
    byte[] Header,
    byte[] Nonce,
    byte[] Ciphertext,
    byte[] Signature);

public sealed record SequencedEnvelope(long ServerSeq, Envelope Envelope);