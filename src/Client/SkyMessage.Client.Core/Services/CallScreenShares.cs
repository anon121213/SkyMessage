namespace SkyMessage.Client.Core.Services;

/// <summary>
/// Which shared screen a group call puts on the stage when several people share at once (the call screen keeps one of
/// these per call; kept here, UI-free, so it is unit-tested). People are media user ids ("N" format). The newest share
/// takes the stage; the viewer can pick another one in the strip, and that pick holds until it ends or someone else
/// starts sharing.
/// </summary>
public sealed class CallScreenShares
{
    public static readonly CallScreenShares None = new([], null);

    private CallScreenShares(IReadOnlyList<string> order, string? picked)
    {
        Order = order;
        Picked = picked;
    }

    /// <summary>The people sharing now, in the order they started (the newest last).</summary>
    public IReadOnlyList<string> Order { get; }

    /// <summary>The share the viewer picked, while it lasts.</summary>
    public string? Picked { get; }

    /// <summary>The share on the stage: the picked one, else the newest; null when nobody shares.</summary>
    public string? Shown => Picked ?? (Order.Count > 0 ? Order[^1] : null);

    /// <summary>
    /// The people sharing now (<paramref name="sharingNow"/>, any order): those still sharing keep their place, new ones
    /// join at the end (several at once in the order given) and take the stage over a pick.
    /// </summary>
    public CallScreenShares Update(IEnumerable<string> sharingNow)
    {
        var now = sharingNow.Distinct(StringComparer.Ordinal).ToList();
        var order = Order.Where(id => now.Contains(id, StringComparer.Ordinal)).ToList();
        var added = now.Where(id => !order.Contains(id, StringComparer.Ordinal)).ToList();
        order.AddRange(added);
        var picked = added.Count == 0 && Picked is not null && order.Contains(Picked, StringComparer.Ordinal) ? Picked : null;
        return order.SequenceEqual(Order, StringComparer.Ordinal) && picked == Picked ? this : new CallScreenShares(order, picked);
    }

    /// <summary>Puts <paramref name="userId"/>'s share on the stage (ignored when they do not share).</summary>
    public CallScreenShares Pick(string userId) =>
        Order.Contains(userId, StringComparer.Ordinal) ? new CallScreenShares(Order, userId == Order[^1] ? null : userId) : this;
}
