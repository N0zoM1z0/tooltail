using System.Security.Cryptography;
using System.Text;
using Tooltail.Application.Abstractions;

namespace Tooltail.SkillFixtureCli;

internal static class FixtureIdentity
{
    public static Guid Derive(Guid workspaceId, string label)
    {
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("A fixture workspace identity cannot be empty.", nameof(workspaceId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        byte[] labelBytes = Encoding.UTF8.GetBytes(label);
        byte[] material = new byte[16 + labelBytes.Length];
        workspaceId.TryWriteBytes(material);
        labelBytes.CopyTo(material, 16);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(material, digest);
        Span<byte> value = digest[..16];
        value[7] = (byte)((value[7] & 0x0f) | 0x40);
        value[8] = (byte)((value[8] & 0x3f) | 0x80);
        return new Guid(value);
    }
}

internal sealed class FixtureSequenceIdGenerator(Guid workspaceId, string scope) : IIdGenerator
{
    private int sequence;

    public Guid NewId() => FixtureIdentity.Derive(
        workspaceId,
        $"{scope}:{Interlocked.Increment(ref sequence)}");
}

internal sealed class FixtureClock(DateTimeOffset utcNow) : IClock
{
    public DateTimeOffset UtcNow { get; } = utcNow.Offset == TimeSpan.Zero
        ? utcNow
        : throw new ArgumentException("Fixture time must use UTC.", nameof(utcNow));
}
