using System.Threading.Channels;

namespace Docovee.BLL.Services;

public enum VoiceCallRetryKind
{
    Book,
    Intent
}

public sealed class VoiceCallRetryJob
{
    public VoiceCallRetryKind Kind { get; init; }
    public int CompletedCallId { get; init; }
    public int SearchSessionId { get; init; }
    public int DoctorId { get; init; }
    public TimeSpan Delay { get; init; }
}

public interface IVoiceCallRetryQueue
{
    void Enqueue(VoiceCallRetryJob job);
    ValueTask<VoiceCallRetryJob> DequeueAsync(CancellationToken cancellationToken);
}

public sealed class VoiceCallRetryQueue : IVoiceCallRetryQueue
{
    private readonly Channel<VoiceCallRetryJob> _channel = Channel.CreateUnbounded<VoiceCallRetryJob>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public void Enqueue(VoiceCallRetryJob job) => _channel.Writer.TryWrite(job);

    public ValueTask<VoiceCallRetryJob> DequeueAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAsync(cancellationToken);
}
