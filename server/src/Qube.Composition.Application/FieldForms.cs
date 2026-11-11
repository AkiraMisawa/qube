using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Qube.Composition.Application;

/// <summary>
/// How a value is written (ADR-0008 §2, ADR-0016 §1 and its note 5 of 2026-11-11). A form says how a word is written,
/// never what it means: one vocabulary for a product's term, a table's field, a condition and the AI.
/// </summary>
public enum FieldForm
{
    /// <summary>A period such as <c>3m</c> or <c>10y</c>, kept lower case.</summary>
    Period,
    /// <summary>A date, <c>yyyy-MM-dd</c>.</summary>
    Date,
    /// <summary>A period or a date.</summary>
    PeriodOrDate,
    /// <summary>
    /// An amount of 0 or more: <c>3bn</c>, <c>500mm</c>, <c>20k</c>, or a bare number in millions (owner, 2026-11-04). A bare
    /// number of a million or more is refused: it is almost surely a full amount typed out of habit, a million times too large.
    /// </summary>
    Amount,
    /// <summary>An amount of either sign, written as <see cref="Amount"/>.</summary>
    SignedAmount,
    /// <summary>A rate held in percent: <c>1.25%</c> is 1.25, <c>5bp</c> is 0.05. A bare number is not a rate.</summary>
    Percent,
    /// <summary>A rate held as a fraction: <c>40%</c> is 0.4, <c>25bp</c> is 0.0025. A bare number is not a rate.</summary>
    Rate,
    /// <summary>A spread held as a fraction and written in basis points: <c>120bp</c> is 0.012, as is <c>1.2%</c>.</summary>
    Spread,
    /// <summary>A plain number, with <c>k</c>, <c>mm</c> or <c>bn</c> for its scale; <c>bp</c> after it changes nothing.</summary>
    Number,
    /// <summary>A whole number of days: <c>10</c>, <c>10d</c> or <c>10 days</c>.</summary>
    Days,
    /// <summary>One of the field's choices, by any of its words, in any case.</summary>
    Choice,
    /// <summary>A flag, as <see cref="FlagWords"/> reads one (<c>on</c>, <c>off</c>, <c>yes</c>, <c>no</c>, <c>true</c>, <c>false</c>).</summary>
    Flag,
    /// <summary>An identifier, as written.</summary>
    Id,
}

/// <summary>
/// What a reader needs to know of a field: its form, whether it may be empty (the field's, not a form: ADR-0016's note
/// 5), and for a <see cref="FieldForm.Choice"/> each word that may be typed, lower case, with the value it reads as.
/// </summary>
public sealed record FieldSpec(FieldForm Form, bool Optional = false, IReadOnlyDictionary<string, string>? Choices = null);

/// <summary>
/// The one reader and writer of values as text (ADR-0016 §1 and its note 5). Pure: the same text and field read the
/// same. A number is read as a decimal; what <see cref="Write"/> writes, <see cref="Read"/> reads back to the same value.
/// </summary>
public static partial class FieldForms
{
    /// <summary>Why a text is not a value of its field: it is not written as the form is, or it is an amount the field refuses.</summary>
    public enum Problem
    {
        None,
        NotOfForm,
        /// <summary>Less than nothing, where the field is an <see cref="FieldForm.Amount"/>.</summary>
        Negative,
        /// <summary>A bare number of a million or more, where a bare number is millions.</summary>
        FullAmount,
    }

    /// <param name="Value">The value read; none for a refusal, and for an empty field.</param>
    /// <param name="What">For a refusal, what the field is, in words that follow "is": <c>a rate with its unit, 40% or 25bp</c>.</param>
    public readonly record struct Reading(Problem Problem, JsonNode? Value, string? What);

    /// <summary>A value as written for a field, or what the field is where the text is not one; never a guess at what was meant.</summary>
    public static Reading Read(FieldSpec spec, string text)
    {
        var t = text.Trim();
        if (spec.Form == FieldForm.Choice)
        {
            var choices = spec.Choices ?? throw new InvalidOperationException("A choice with nothing to choose from cannot be read.");
            if (choices.TryGetValue(t.ToLowerInvariant(), out var chosen)) return Take(chosen);
            return IsEmpty(spec, t) ? Take(null) : Refuse(spec);
        }
        if (IsEmpty(spec, t)) return Take(null);
        switch (spec.Form)
        {
            case FieldForm.Period:
                return PeriodPattern().IsMatch(t) ? Take(t.ToLowerInvariant()) : Refuse(spec);
            case FieldForm.Date:
                return IsDate(t) ? Take(t) : Refuse(spec);
            case FieldForm.PeriodOrDate:
                return PeriodPattern().IsMatch(t) ? Take(t.ToLowerInvariant()) : IsDate(t) ? Take(t) : Refuse(spec);
            case FieldForm.Amount or FieldForm.SignedAmount:
            {
                var m = AmountPattern().Match(t);
                if (!m.Success || Number(m.Groups[1].Value) is not { } n) return Refuse(spec);
                if (spec.Form == FieldForm.Amount && n < 0) return new(Problem.Negative, null, "an amount of 0 or more");
                var scale = m.Groups[2].Value.ToLowerInvariant();
                // As ADR-0008: a bare number is millions, and a bare one of a million or more was typed in full.
                if (scale.Length == 0 && Math.Abs(n) >= 1_000_000m) return new(Problem.FullAmount, null, "in millions; write a full amount with its scale, such as 1bn");
                return Scaled(n, scale.Length == 0 ? "mm" : scale) is { } amount ? Take(amount) : Refuse(spec);
            }
            case FieldForm.Percent:
                return WithUnit(t) is (var percent, var unit) ? Take(unit == "bp" ? percent / 100m : percent) : Refuse(spec);
            case FieldForm.Rate:
                return Fraction(t) is { } rate ? Take(rate) : Refuse(spec);
            case FieldForm.Spread:
                return Fraction(t) is { } spread ? Take(spread) : Refuse(spec);
            case FieldForm.Number:
            {
                var m = NumberPattern().Match(t);
                return m.Success && Number(m.Groups[1].Value) is { } n && Scaled(n, m.Groups[2].Value.ToLowerInvariant()) is { } number ? Take(number)
                    : Refuse(spec);
            }
            case FieldForm.Days:
            {
                var m = DaysPattern().Match(t);
                return m.Success && decimal.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var days) ? Take(days)
                    : Refuse(spec);
            }
            case FieldForm.Flag:
                return FlagWords.Read(t) is { } flag ? Take(flag) : Refuse(spec);
            case FieldForm.Id:
                return t.Length > 0 ? Take(t) : Refuse(spec);
            default:
                throw new InvalidOperationException($"No reading of {spec.Form}.");
        }
    }

    /// <summary>
    /// What a field of the form has to choose from, each as a line writes it: a flag's two words, a choice's values, and
    /// <c>empty</c> where the field may be empty. A field of any other form is typed freely. What a line offers for a
    /// value, and what a value that cannot be read could have been (ADR-0016 notes 15 and 45).
    /// </summary>
    public static IReadOnlyList<string> ChoicesWritten(FieldSpec spec)
    {
        IEnumerable<string> values = spec.Form switch
        {
            FieldForm.Flag => [Write(spec, true), Write(spec, false)],
            FieldForm.Choice => (spec.Choices ?? throw new InvalidOperationException("A choice with nothing to choose from cannot be offered.")).Values.Distinct().Select(v => Write(spec, v)),
            _ => [],
        };
        return [.. spec.Optional ? values.Append(Write(spec, null)) : values];
    }

    /// <summary>
    /// A value as it is typed, in the form <see cref="Read"/> reads back to the same value: an amount with its scale (5mm,
    /// 250k, 1.5bn), never bare, since a bare number is read as millions, and with every decimal it has; a rate in %; a
    /// spread in bp; a flag on or off; <c>empty</c> for none.
    /// </summary>
    public static string Write(FieldSpec spec, JsonNode? value)
    {
        if (value is null) return "empty";
        switch (spec.Form)
        {
            case FieldForm.Amount or FieldForm.SignedAmount:
            {
                var n = Held(value);
                if (n == 0) return "0";
                var (scale, unit) = Math.Abs(n) >= 1_000_000_000m ? (1_000_000_000m, "bn") : Math.Abs(n) >= 1_000_000m ? (1_000_000m, "mm") : (1_000m, "k");
                return $"{Exact(n / scale)}{unit}";
            }
            case FieldForm.Percent:
                return $"{Exact(Held(value))}%";
            case FieldForm.Rate:
                return $"{Exact(Held(value) * 100m)}%";
            case FieldForm.Spread:
                return $"{Exact(Held(value) * 10_000m)}bp";
            case FieldForm.Number:
                return Exact(Held(value));
            case FieldForm.Days:
                return $"{Exact(Held(value))}d";
            case FieldForm.Flag:
                return value.GetValue<bool>() ? "on" : "off";
            case FieldForm.Choice:
            {
                // The value itself where it is one of its own words, as a code is; else a word that reads as it.
                var held = value.GetValue<string>();
                var choices = spec.Choices ?? throw new InvalidOperationException("A choice with nothing to choose from cannot be written.");
                if (choices.TryGetValue(held.ToLowerInvariant(), out var own) && own == held) return held;
                return choices.FirstOrDefault(c => c.Value == held).Key ?? held;
            }
            case FieldForm.Period or FieldForm.Date or FieldForm.PeriodOrDate or FieldForm.Id:
                return value.GetValue<string>();
            default:
                throw new InvalidOperationException($"No writing of {spec.Form}.");
        }
    }

    /// <summary>
    /// A value as a cell a spreadsheet reads (ADR-0016 note 44): an amount, a number and a count of days plain, in
    /// full, with every decimal and no grouping; a rate and a spread in percent with the sign, which a spreadsheet
    /// reads as the number meant whichever way the field holds it; a flag <c>true</c> or <c>false</c>; the rest as
    /// <see cref="Write"/> writes it; and nothing for no value. Nothing is rounded.
    /// </summary>
    public static string Cell(FieldSpec spec, JsonNode? value)
    {
        if (value is null) return "";
        return spec.Form switch
        {
            FieldForm.Amount or FieldForm.SignedAmount or FieldForm.Number or FieldForm.Days => Exact(Held(value)),
            FieldForm.Percent => $"{Exact(Held(value))}%",
            FieldForm.Rate or FieldForm.Spread => $"{Exact(Held(value) * 100m)}%",
            FieldForm.Flag => value.GetValue<bool>() ? "true" : "false",
            _ => Write(spec, value),
        };
    }

    /// <summary>Two values say the same thing: numbers by value (1.0 is 1), the rest as JSON; no value is the same as no value.</summary>
    public static bool Same(JsonNode? a, JsonNode? b) =>
        a is JsonValue x && b is JsonValue y && x.GetValueKind() == JsonValueKind.Number && y.GetValueKind() == JsonValueKind.Number ? Held(x) == Held(y) : JsonNode.DeepEquals(a, b);

    /// <summary>The number a value holds, however it was made: a decimal, a double, or a number read from JSON.</summary>
    public static decimal Held(JsonNode value) =>
        value is JsonValue v && v.TryGetValue<decimal>(out var n) ? n : decimal.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    static Reading Take(JsonNode? value) => new(Problem.None, value, null);

    static Reading Refuse(FieldSpec spec) => new(Problem.NotOfForm, null, Takes(spec));

    /// <summary>
    /// What a field takes, in words that follow "is" (<c>a rate with its unit, 40% or 25bp</c>): what a refusal of a
    /// value says the field is, and what a description of the field says before any value is tried (ADR-0016 note 38).
    /// A refusal names a choice's values; a description that lists them beside the sentence (<paramref name="choicesBeside"/>)
    /// says "one of its choices", since a field may take thousands.
    /// </summary>
    public static string Takes(FieldSpec spec, bool choicesBeside = false)
    {
        var what = spec.Form switch
        {
            FieldForm.Period => "a period such as 3m or 10y",
            FieldForm.Date => "a date, yyyy-mm-dd",
            FieldForm.PeriodOrDate => "a period such as 10y, or a date",
            FieldForm.Amount or FieldForm.SignedAmount => "an amount: 5mm, 250k, 1bn, or a bare number of millions",
            FieldForm.Percent => "a rate with its unit: 1.25% or 5bp",
            FieldForm.Rate => "a rate with its unit, 40% or 25bp",
            FieldForm.Spread => "a spread with its unit, 120bp or 1.2%",
            FieldForm.Number => "a number, with k, mm or bn for its scale, or bp",
            FieldForm.Days => "a whole number of days, 10 or 10d",
            FieldForm.Flag => "on or off",
            FieldForm.Id => "an identifier",
            FieldForm.Choice when choicesBeside => "one of its choices",
            FieldForm.Choice => $"one of {string.Join(", ", (spec.Choices ?? throw new InvalidOperationException("A choice with nothing to choose from cannot be read.")).Values.Distinct())}",
            _ => throw new InvalidOperationException($"No reading of {spec.Form}."),
        };
        return spec.Optional ? $"{what}, or empty" : what;
    }

    // No value, where the field may have none: said as `empty` or `none`.
    static bool IsEmpty(FieldSpec spec, string t) =>
        spec.Optional && (t.Equals("empty", StringComparison.OrdinalIgnoreCase) || t.Equals("none", StringComparison.OrdinalIgnoreCase));

    // Digits with their grouping commas, as a decimal; none where they are more than a decimal holds.
    static decimal? Number(string digits) =>
        decimal.TryParse(digits.Replace(",", "", StringComparison.Ordinal), NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : null;

    static decimal? Scaled(decimal n, string scale)
    {
        var by = scale switch { "k" => 1_000m, "mm" => 1_000_000m, "bn" => 1_000_000_000m, _ => 1m };
        return Math.Abs(n) <= decimal.MaxValue / by ? n * by : null;
    }

    // A number with its unit, % or bp, as written.
    static (decimal Number, string Unit)? WithUnit(string t)
    {
        var m = UnitPattern().Match(t);
        return m.Success && Number(m.Groups[1].Value) is { } n ? (n, m.Groups[2].Value.ToLowerInvariant()) : null;
    }

    // A rate or a spread with its unit, as a fraction (40% is 0.4, 25bp 0.0025), or none where it is not one.
    static decimal? Fraction(string t) => WithUnit(t) is (var n, var unit) ? unit == "bp" ? n / 10_000m : n / 100m : null;

    // Every decimal a value has, and no trailing zero: written back, it reads as the same value.
    static string Exact(decimal n) => n.ToString("0.############################", CultureInfo.InvariantCulture);

    static bool IsDate(string t) => DateOnly.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    [GeneratedRegex("^[1-9][0-9]{0,2}[dwmy]$", RegexOptions.IgnoreCase)]
    private static partial Regex PeriodPattern();

    [GeneratedRegex("^(-?[0-9][0-9,]*(?:\\.[0-9]+)?)(k|mm|bn)?$", RegexOptions.IgnoreCase)]
    private static partial Regex AmountPattern();

    [GeneratedRegex("^(-?[0-9]+(?:\\.[0-9]+)?)(%|bp)$", RegexOptions.IgnoreCase)]
    private static partial Regex UnitPattern();

    [GeneratedRegex("^(-?[0-9][0-9,]*(?:\\.[0-9]+)?)(k|mm|bn|bp)?$", RegexOptions.IgnoreCase)]
    private static partial Regex NumberPattern();

    [GeneratedRegex("^([0-9]+)\\s*(?:d|days?)?$", RegexOptions.IgnoreCase)]
    private static partial Regex DaysPattern();
}
