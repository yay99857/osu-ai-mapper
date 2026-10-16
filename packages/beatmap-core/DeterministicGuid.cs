using System.Security.Cryptography;
using System.Text;

namespace OsuAi.BeatmapCore;

/// <summary>
/// Name-based UUIDs (RFC 9562 version 8, SHA-256). Importing the same bytes twice yields the
/// same object IDs, which keeps imports reproducible without tying an ID to a list index at runtime.
/// </summary>
public static class DeterministicGuid
{
    public static Guid Create(string name)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(name), hash);

        Span<byte> bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }
}
