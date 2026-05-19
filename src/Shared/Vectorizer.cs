namespace Shared;

public static class Vectorizer
{
    private static float Clamp01(float value)
    {
        if (value < 0f) return 0f;
        if (value > 1f) return 1f;
        return value;
    }

    public static Vector14F Vectorize(
        float amount,
        int installments,
        float customerAvgAmount,
        int hour,
        int dayOfWeek,
        float? minutesSinceLastTx,
        float? kmFromLastTx,
        float kmFromHome,
        int txCount24h,
        bool isOnline,
        bool cardPresent,
        bool unknownMerchant,
        float mccRisk,
        float merchantAvgAmount)
    {
        var v = new Vector14F();

        v.D0 = Clamp01(amount / Constants.MaxAmount);
        v.D1 = Clamp01(installments / Constants.MaxInstallments);
        v.D2 = customerAvgAmount > 0f
            ? Clamp01((amount / customerAvgAmount) / Constants.AmountVsAvgRatio)
            : 1f;
        v.D3 = hour / 23f;
        v.D4 = dayOfWeek / 6f;
        v.D5 = minutesSinceLastTx.HasValue
            ? Clamp01(minutesSinceLastTx.Value / Constants.MaxMinutes)
            : Constants.MissingSentinel;
        v.D6 = kmFromLastTx.HasValue
            ? Clamp01(kmFromLastTx.Value / Constants.MaxKm)
            : Constants.MissingSentinel;
        v.D7 = Clamp01(kmFromHome / Constants.MaxKm);
        v.D8 = Clamp01(txCount24h / Constants.MaxTxCount24h);
        v.D9 = isOnline ? 1f : 0f;
        v.D10 = cardPresent ? 1f : 0f;
        v.D11 = unknownMerchant ? 1f : 0f;
        v.D12 = mccRisk;
        v.D13 = Clamp01(merchantAvgAmount / Constants.MaxMerchantAvgAmount);
        v.Pad14 = 0f;
        v.Pad15 = 0f;

        return v;
    }
}
