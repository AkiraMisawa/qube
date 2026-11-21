using System.Collections.Concurrent;
using System.Globalization;
using Qube.Samples.Portfolio;

namespace Qube.Samples.Xva;

/// <summary>What a new trade's PV01 is asked on (ADR-0010 §6): the terms it depends on, and the value date.</summary>
public sealed record Pv01Request(string TradeId, string Product, string Currency, decimal Notional, string Maturity, decimal? FixedRate, string? Schedule,
    string Curve, DateOnly ValueDate)
{
    public static Pv01Request Of(EffectiveTrade trade, DateOnly valueDate) =>
        new(trade.TradeId, trade.Product, trade.Currency, trade.Notional, trade.Maturity, trade.FixedRate, trade.Schedule, trade.Curve, valueDate);
}

/// <summary>A PV01 as it stands: pending, its figure, or why it failed. Never 0 for a figure not had.</summary>
public sealed record Pv01Answer(string State, decimal? Value, string? Reason)
{
    public static readonly Pv01Answer Pending = new(TradeCell.Pending, null, null);
}

/// <summary>
/// New trades' PV01 as the engine answers it (ADR-0010 §6): asked once for a trade's terms and value date, the first
/// time the trade is read, and kept. Terms that change are another request; a failed one stays failed until it is
/// asked again (<see cref="Retry"/>). A PV01 is a result looked up, not a business submission, so it is no Run.
/// An engine is asked off the reader's thread, unless <paramref name="onReaderThread"/> says to call it on the reader's,
/// as the simulated one with no delay is, which computes and waits on nothing: its PV01 is then there on the first read.
/// One that has not answered within <paramref name="timeout"/> has failed, saying so, and is asked to stop (ADR-0010
/// §6's note of 2026-11-21, DEFAULT): a PV01 is never pending for good, so Price never waits on it for good.
/// </summary>
/// <param name="time">The clock the timeout is timed on.</param>
public sealed class Pv01Provider(Pv01Provider.Engine engine, bool onReaderThread = false, TimeSpan? timeout = null, TimeProvider? time = null)
{
    /// <summary>The engine's PV01 of one trade, in the trade's currency per basis point. A failure says why.</summary>
    public delegate Task<decimal> Engine(Pv01Request request, CancellationToken cancellationToken);

    /// <summary>Thirty seconds: seventy-five times what the simulated engine takes, and three times what Price waits.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    readonly TimeSpan limit = timeout is not { } given ? DefaultTimeout
        : given > TimeSpan.Zero ? given : throw new ArgumentOutOfRangeException(nameof(timeout), given, "A PV01 timeout is a positive time.");
    readonly TimeProvider clock = time ?? TimeProvider.System;
    readonly ConcurrentDictionary<Pv01Request, Lazy<Task<decimal>>> asked = new();

    /// <summary>
    /// The simulated engine: a toy figure over notional and time to maturity, chosen only so that terms visibly move
    /// it. It is not a model of anything. It answers after <paramref name="delay"/>, as an engine would, and fails for
    /// a curve its market does not have.
    /// </summary>
    public static Pv01Provider Simulated(TimeSpan delay, TimeProvider? time = null, TimeSpan? timeout = null) => delay > TimeSpan.Zero
        ? new(async (request, cancellationToken) =>
        {
            await Task.Delay(delay, time ?? TimeProvider.System, cancellationToken);
            return SimulatedPv01(request);
        }, timeout: timeout, time: time)
        : new((request, _) => Task.FromResult(SimulatedPv01(request)), onReaderThread: true, timeout: timeout, time: time);

    public static decimal SimulatedPv01(Pv01Request request)
    {
        if (request.Curve == "") throw new InvalidOperationException("The trade has no discount curve.");
        if (!PortfolioCatalog.Curves.Contains(request.Curve)) throw new InvalidOperationException($"The simulated market has no {request.Curve} curve.");
        var maturity = DateOnly.ParseExact(request.Maturity, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var years = Math.Max(0, (decimal)(maturity.DayNumber - request.ValueDate.DayNumber) / 365m);
        // A swap's value moves against the rate it pays; a forward's barely does.
        var perBasisPoint = request.Product == "IRS" ? -0.000095m : 0.000005m;
        return Math.Round(request.Notional * years * perBasisPoint, 2);
    }

    /// <summary>The PV01 as it stands, asking for it the first time these terms are read.</summary>
    public Pv01Answer Of(Pv01Request request)
    {
        var task = Asked(request);
        return task.Status switch
        {
            TaskStatus.RanToCompletion => new(TradeCell.Shown, task.Result, null),
            TaskStatus.Faulted => new(TradeCell.Failed, null,
                (task.Exception?.InnerException ?? task.Exception ?? throw new InvalidOperationException("A faulted task carries its exception.")).Message),
            TaskStatus.Canceled => new(TradeCell.Failed, null, "The request was cancelled."),
            _ => Pv01Answer.Pending,
        };
    }

    /// <summary>Asks again for each of these whose request failed; answers how many it asked again.</summary>
    public int Retry(IEnumerable<Pv01Request> requests)
    {
        var again = 0;
        foreach (var request in requests.Distinct())
            if (asked.TryGetValue(request, out var lazy) && lazy.IsValueCreated && lazy.Value.Status is TaskStatus.Faulted or TaskStatus.Canceled
                && asked.TryRemove(KeyValuePair.Create(request, lazy)))
            {
                Of(request);
                again++;
            }
        return again;
    }

    /// <summary>Completes once every request asked so far has answered or failed; for a caller that must read them settled.</summary>
    public Task Settled() => Task.WhenAll(asked.Values.Select(l => l.Value)).ContinueWith(_ => { }, TaskScheduler.Default);

    /// <summary>
    /// Completes once each of these requests has answered or failed, asking any not asked yet as a read would: what Price
    /// waits on for the trades it reads (ADR-0010 §6's note of 2026-11-21), and no other request. Never faults.
    /// </summary>
    public Task Answered(IEnumerable<Pv01Request> requests) =>
        Task.WhenAll(requests.Distinct().Select(Asked)).ContinueWith(_ => { }, TaskScheduler.Default);

    // The request's answer, asked the first time these terms are read.
    Task<decimal> Asked(Pv01Request request) => asked.GetOrAdd(request, r => new Lazy<Task<decimal>>(() => Ask(r))).Value;

    // Off the reader's thread unless told otherwise: a page read does not wait for an engine. An engine that throws as it
    // is called says why as a failed PV01, as one that fails later does; one that does not answer in time has failed.
    async Task<decimal> Ask(Pv01Request request)
    {
        if (!onReaderThread) await Task.Yield();
        // One timer both asks the engine to stop and ends the wait, so an engine is never left running unasked once
        // its PV01 has failed, as one disposed before a second timer fired would be.
        using var stop = new CancellationTokenSource(limit, clock);
        try
        {
            return await engine(request, stop.Token).WaitAsync(stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            throw new TimeoutException($"The engine did not answer within {limit.TotalSeconds:0.#} s.");
        }
    }
}
