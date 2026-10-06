namespace Sleepyshark.Officina.Claude.Tests;

/// <summary>A response body that ends in a dropped connection once its bytes are read.</summary>
internal sealed class DroppingStream(byte[] bytes) : MemoryStream(bytes)
{
    public override int Read(byte[] buffer, int offset, int count) =>
        Position < Length ? base.Read(buffer, offset, count) : throw new IOException("The connection was reset.");

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Position < Length ? base.ReadAsync(buffer, cancellationToken) : ValueTask.FromException<int>(new IOException("The connection was reset."));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
}
