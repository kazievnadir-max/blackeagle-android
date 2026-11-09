namespace BlackEagle.Android;

internal static class TunnelRecoveryPolicy
{
    /// <summary>Считает соединение подтвержденным только при недавнем handshake без сдвига часов в прошлое.</summary>
    internal static bool IsFresh(long handshakeSeconds, long nowSeconds) =>
        handshakeSeconds > 0 && nowSeconds >= handshakeSeconds && nowSeconds - handshakeSeconds < 180;

    /// <summary>Дает движку время на handshake и увеличивает паузу повторов до двух минут.</summary>
    internal static long RetryDelayMilliseconds(int attempt) => attempt switch
    {
        <= 1 => 40_000,
        2 => 80_000,
        _ => 120_000
    };
}
