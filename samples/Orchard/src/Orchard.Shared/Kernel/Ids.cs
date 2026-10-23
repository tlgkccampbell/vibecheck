namespace Orchard.Shared.Kernel;

public readonly record struct OrderId(Guid Value)
{
    public static OrderId New() => new(Guid.NewGuid());
}

public readonly record struct SessionId(Guid Value);

public readonly record struct ChargeId(string Value);

/// <summary>An amount of money in minor units of a currency.</summary>
public readonly record struct Money(long Minor, string Currency)
{
    public override string ToString() => $"{Minor / 100m:F2} {Currency}";
}
