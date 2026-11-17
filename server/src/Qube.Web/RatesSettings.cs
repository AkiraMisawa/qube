using Qube.Samples.Rates;

namespace Qube.Web;

/// <summary>
/// The Rates configuration (section <c>Rates</c>). Convention profiles are configuration, never code: until the
/// owner confirms them none is configured by default, and a line on an unconfigured index is refused on its row.
/// </summary>
public sealed class RatesSettings
{
    public List<ProfileSetting> Conventions { get; set; } = [];
    public MarketSetting SyntheticMarket { get; set; } = new();
    /// <summary>Advance the synthetic market on a clock. Off for tests that advance it themselves.</summary>
    public bool Ticker { get; set; } = true;
    /// <summary>How long a live subscription runs with no page watching it before it stops (owner, 2026-10-27).</summary>
    public int LiveIdleStopSeconds { get; set; } = 300;
    /// <summary>
    /// How many live calculations run at once across the server (docs/reports/rates-live-30-users.html §6). Set it to the
    /// engine's parallelism: with an out-of-process engine a calculation mostly waits on the engine, not on this CPU.
    /// </summary>
    public int LiveMaxCalculations { get; set; } = 4;

    /// <summary>The pricing provider: <c>fake</c> (simulated, the default) or <c>alpha</c> (the resident worker, docs/reports/alpha-for-rates.html).</summary>
    public string Provider { get; set; } = "fake";
    public AlphaSetting Alpha { get; set; } = new();

    /// <summary>The provider chosen, after checking that it is fully configured; anything else stops startup (never a fallback to the fake).</summary>
    public string ProviderChoice()
    {
        switch (Provider)
        {
            case "fake": return Provider;
            case "alpha":
                if (string.IsNullOrWhiteSpace(Alpha.Command)) throw new InvalidOperationException("Rates:Provider is alpha but Rates:Alpha:Command is empty.");
                Alpha.CurveMap();
                return Provider;
            default: throw new InvalidOperationException($"Rates:Provider '{Provider}' is not a provider (fake or alpha).");
        }
    }

    public int LiveCalculationSlots() => LiveMaxCalculations >= 1 ? LiveMaxCalculations
        : throw new InvalidOperationException($"Rates:LiveMaxCalculations is {LiveMaxCalculations}; at least 1 is needed.");

    public ConventionRegistry Registry()
    {
        var calendars = new IBusinessCalendar[] { new WeekendsOnlyCalendar() };
        var profiles = Conventions.Select(p =>
        {
            if (string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Version) || string.IsNullOrWhiteSpace(p.Currency)
                || string.IsNullOrWhiteSpace(p.Index) || string.IsNullOrWhiteSpace(p.CalendarId) || p.DayCount is null || p.Adjustment is null
                || p.SpotLagDays is null || p.PaymentLagDays is null || p.Stub is null || p.EndOfMonth is null || p.Overnight is null)
                throw new InvalidOperationException($"Rates convention profile '{p.Id}' leaves a field unset; every field is required.");
            if (calendars.All(c => c.Id != p.CalendarId))
                throw new InvalidOperationException($"Rates convention profile '{p.Id}' names calendar '{p.CalendarId}', which is not available.");
            return new ConventionProfile(p.Id, p.Version, p.Currency, p.Index, p.DayCount.Value, p.CalendarId, p.Adjustment.Value,
                p.SpotLagDays.Value, p.PaymentLagDays.Value, p.Stub.Value, p.EndOfMonth.Value, p.Overnight.Value,
                p.Standard.Count == 0 ? null : p.Standard);
        }).ToList();
        return new ConventionRegistry(profiles, calendars);
    }
}

public sealed class ProfileSetting
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Currency { get; set; } = "";
    public string Index { get; set; } = "";
    public DayCountBasis? DayCount { get; set; }
    public string CalendarId { get; set; } = "";
    public BusinessDayAdjustment? Adjustment { get; set; }
    public int? SpotLagDays { get; set; }
    public int? PaymentLagDays { get; set; }
    public StubRule? Stub { get; set; }
    public bool? EndOfMonth { get; set; }
    public bool? Overnight { get; set; }
    /// <summary>The profile's product templates (ADR-0008 §4), such as <c>"irs.fixedFrequency": "12m"</c>.</summary>
    public Dictionary<string, string> Standard { get; set; } = [];
}

/// <summary>A synthetic market's starting point. Its levels are inputs to a toy curve, never quotes of any market.</summary>
public sealed class MarketSetting
{
    public string ScenarioVersion { get; set; } = "empty-1";
    public int Seed { get; set; } = 1;
    public DateTimeOffset Start { get; set; } = new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);
    public int StepMilliseconds { get; set; } = 1000;
    public double MaxLevelStepBp { get; set; } = 0.5;
    public double MaxSlopeStepBp { get; set; } = 0.2;
    public double MaxFxLogStep { get; set; } = 0.0005;
    public List<CurveSetting> Curves { get; set; } = [];
    /// <summary>Units of each currency per unit of a common base.</summary>
    public Dictionary<string, double> Fx { get; set; } = [];

    public SyntheticMarketConfig Config()
    {
        if (StepMilliseconds < 50) throw new InvalidOperationException("The synthetic market steps at most every 50 ms.");
        return new(ScenarioVersion, Seed, Start, TimeSpan.FromMilliseconds(StepMilliseconds),
            [.. Curves.Select(c => new CurveQuotes(c.Currency, c.Index, [.. c.Pillars.Select(p => (Tenor.Parse(p.Key), p.Value)).OrderBy(p => SyntheticMarket.YearsOf(p.Item1))]))],
            Fx, MaxLevelStepBp, MaxSlopeStepBp, MaxFxLogStep);
    }
}

public sealed class CurveSetting
{
    public string Currency { get; set; } = "";
    public string Index { get; set; } = "";
    /// <summary>Pillar levels in percent by tenor, such as <c>"1y": 0.5</c>.</summary>
    public Dictionary<string, double> Pillars { get; set; } = [];
}

/// <summary>The alpha worker (section <c>Rates:Alpha</c>): the command pinned by the deployment and the engine's curve definitions.</summary>
public sealed class AlphaSetting
{
    public string Command { get; set; } = "";
    public List<string> Arguments { get; set; } = [];
    public int TimeoutSeconds { get; set; } = 10;
    public List<AlphaCurveSetting> Curves { get; set; } = [];
    /// <summary>Index (<c>JPY/TONA</c>) to the engine curve that projects it.</summary>
    public Dictionary<string, string> Forward { get; set; } = [];
    /// <summary>Currency under collateral currency (<c>USD@JPY</c>) to the engine curve that discounts it.</summary>
    public Dictionary<string, string> Discount { get; set; } = [];

    public Qube.Samples.Rates.Alpha.AlphaCurveMap CurveMap()
    {
        var curves = Curves.Select(c =>
        {
            if (string.IsNullOrWhiteSpace(c.Name) || string.IsNullOrWhiteSpace(c.Currency) || string.IsNullOrWhiteSpace(c.Role)
                || string.IsNullOrWhiteSpace(c.Interpolation) || string.IsNullOrWhiteSpace(c.Quotes))
                throw new InvalidOperationException($"Rates:Alpha:Curves '{c.Name}' leaves a field unset; name, currency, role, interpolation and quotes are required.");
            return new Qube.Samples.Rates.Alpha.AlphaCurve(c.Name, c.Currency, c.Role, c.IndexMonths, c.Base ?? "", c.Interpolation, c.Quotes);
        }).ToList();
        var names = curves.Select(c => c.Name).ToHashSet();
        foreach (var (key, curve) in Forward.Concat(Discount))
            if (!names.Contains(curve)) throw new InvalidOperationException($"Rates:Alpha maps {key} to {curve}, which Rates:Alpha:Curves does not define.");
        return new(curves, Forward, Discount);
    }
}

public sealed class AlphaCurveSetting
{
    public string Name { get; set; } = "";
    public string Currency { get; set; } = "";
    public string Role { get; set; } = "";
    public int IndexMonths { get; set; }
    public string? Base { get; set; }
    public string Interpolation { get; set; } = "";
    public string Quotes { get; set; } = "";
}
