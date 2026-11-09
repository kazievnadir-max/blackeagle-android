using BlackEagle.Android;
using Xunit;

public sealed class TunnelRecoveryPolicyTests
{
    /// <summary>Не принимает отсутствующий, просроченный или будущий handshake за рабочее соединение.</summary>
    [Theory]
    [InlineData(0, 1000, false)]
    [InlineData(-1, 1000, false)]
    [InlineData(1000, 1000, true)]
    [InlineData(1000, 1179, true)]
    [InlineData(1000, 1180, false)]
    [InlineData(1000, 999, false)]
    public void FreshnessBoundary(long handshake, long now, bool expected) =>
        Assert.Equal(expected, TunnelRecoveryPolicy.IsFresh(handshake, now));

    /// <summary>Оставляет время на handshake и ограничивает паузу повторов двумя минутами.</summary>
    [Theory]
    [InlineData(1, 40_000)]
    [InlineData(2, 80_000)]
    [InlineData(3, 120_000)]
    [InlineData(int.MaxValue, 120_000)]
    public void RetryBackoff(int attempt, long expected) =>
        Assert.Equal(expected, TunnelRecoveryPolicy.RetryDelayMilliseconds(attempt));
}
