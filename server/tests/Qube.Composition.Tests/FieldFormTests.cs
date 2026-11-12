using System.Text.Json.Nodes;
using Qube.Composition.Application;

namespace Qube.Composition.Tests;

// ADR-0016 §1 and its note 5 of 2026-11-11: one vocabulary of forms, read and written in one place, so a product's
// term, a cell, a condition and the AI spell a value alike. Nothing here is financial but the examples' numbers.
public class FieldFormTests
{
    static readonly IReadOnlyDictionary<string, string> Stations = new Dictionary<string, string> { ["north"] = "NORTH", ["n"] = "NORTH", ["south"] = "SOUTH", ["east"] = "EAST" };

    static FieldSpec Of(FieldForm form, bool optional = false) => new(form, optional, form == FieldForm.Choice ? Stations : null);

    [Theory] // note 5: an amount as ADR-0008 writes it, a rate and a spread as fractions, a product's rate in percent
    [InlineData(FieldForm.Amount, "5", "5000000")]
    [InlineData(FieldForm.Amount, "5mm", "5000000")]
    [InlineData(FieldForm.Amount, "250K", "250000")]
    [InlineData(FieldForm.Amount, "1.5bn", "1500000000")]
    [InlineData(FieldForm.Amount, "1,250", "1250000000")]
    [InlineData(FieldForm.Amount, "0", "0")]
    [InlineData(FieldForm.SignedAmount, "-350k", "-350000")]
    [InlineData(FieldForm.Rate, "40%", "0.4")]
    [InlineData(FieldForm.Rate, "25bp", "0.0025")]
    [InlineData(FieldForm.Spread, "120bp", "0.012")]
    [InlineData(FieldForm.Spread, "1.2%", "0.012")]
    [InlineData(FieldForm.Percent, "1.25%", "1.25")]
    [InlineData(FieldForm.Percent, "-5BP", "-0.05")]
    [InlineData(FieldForm.Number, "1.25", "1.25")]
    [InlineData(FieldForm.Number, "-2k", "-2000")]
    [InlineData(FieldForm.Days, "10d", "10")]
    [InlineData(FieldForm.Days, "10 days", "10")]
    [InlineData(FieldForm.Days, "10", "10")]
    public void A_number_is_read_by_its_form(FieldForm form, string text, string value)
    {
        var read = FieldForms.Read(Of(form), text);

        Assert.Equal((FieldForms.Problem.None, decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)), (read.Problem, read.Value!.GetValue<decimal>()));
    }

    [Theory] // note 5: a period kept lower case, a date, a choice by any of its words, an id as written
    [InlineData(FieldForm.Period, "10Y", "10y")]
    [InlineData(FieldForm.PeriodOrDate, "3m", "3m")]
    [InlineData(FieldForm.PeriodOrDate, "2031-11-11", "2031-11-11")]
    [InlineData(FieldForm.Date, "2031-11-11", "2031-11-11")]
    [InlineData(FieldForm.Choice, "North", "NORTH")]
    [InlineData(FieldForm.Choice, "N", "NORTH")]
    [InlineData(FieldForm.Id, "CSA-B", "CSA-B")]
    public void A_word_is_read_by_its_form(FieldForm form, string text, string value) =>
        Assert.Equal(value, FieldForms.Read(Of(form), text).Value!.GetValue<string>());

    [Fact] // ADR-0006's note 4: a flag is read one way
    public void A_flag_is_read_as_everywhere()
    {
        Assert.Equal((true, false), (FieldForms.Read(Of(FieldForm.Flag), "Yes").Value!.GetValue<bool>(), FieldForms.Read(Of(FieldForm.Flag), "off").Value!.GetValue<bool>()));
    }

    [Theory] // note 5: a refusal says what the field is, in words that follow "is"; never a guess at what was meant
    [InlineData(FieldForm.Amount, "lots", "an amount: 5mm, 250k, 1bn, or a bare number of millions")]
    [InlineData(FieldForm.Amount, "-1mm", "an amount of 0 or more")]
    [InlineData(FieldForm.Amount, "5000000", "in millions; write a full amount with its scale, such as 1bn")]
    [InlineData(FieldForm.SignedAmount, "-5000000", "in millions; write a full amount with its scale, such as 1bn")]
    [InlineData(FieldForm.Rate, "0.4", "a rate with its unit, 40% or 25bp")]
    [InlineData(FieldForm.Spread, "wide", "a spread with its unit, 120bp or 1.2%")]
    [InlineData(FieldForm.Percent, "1.25", "a rate with its unit: 1.25% or 5bp")]
    [InlineData(FieldForm.Number, "many", "a number, with k, mm or bn for its scale, or bp")]
    [InlineData(FieldForm.Days, "ten", "a whole number of days, 10 or 10d")]
    [InlineData(FieldForm.Flag, "maybe", "on or off")]
    [InlineData(FieldForm.Period, "soon", "a period such as 3m or 10y")]
    [InlineData(FieldForm.Date, "11/11/2031", "a date, yyyy-mm-dd")]
    [InlineData(FieldForm.PeriodOrDate, "soon", "a period such as 10y, or a date")]
    [InlineData(FieldForm.Choice, "west", "one of NORTH, SOUTH, EAST")]
    [InlineData(FieldForm.Id, " ", "an identifier")]
    public void A_refusal_says_what_the_field_is(FieldForm form, string text, string what)
    {
        var read = FieldForms.Read(Of(form), text);

        Assert.Equal((null, what), (read.Value, read.What));
        Assert.NotEqual(FieldForms.Problem.None, read.Problem);
    }

    [Fact] // note 5: that a field may be empty is the field's, not a form
    public void Empty_and_none_are_no_value_only_where_the_field_may_be_empty()
    {
        var optional = FieldForms.Read(Of(FieldForm.Rate, optional: true), "Empty");
        var none = FieldForms.Read(Of(FieldForm.Id, optional: true), "none");
        var refused = FieldForms.Read(Of(FieldForm.Rate), "empty");
        var days = FieldForms.Read(Of(FieldForm.Days, optional: true), "ten");

        Assert.Equal((FieldForms.Problem.None, null, FieldForms.Problem.None, null), (optional.Problem, optional.Value, none.Problem, none.Value));
        Assert.Equal((FieldForms.Problem.NotOfForm, "a rate with its unit, 40% or 25bp"), (refused.Problem, refused.What));
        Assert.Equal("a whole number of days, 10 or 10d, or empty", days.What);
    }

    public static TheoryData<FieldForm, string> Written => new()
    {
        { FieldForm.Amount, "5000000" }, { FieldForm.Amount, "250000" }, { FieldForm.Amount, "1500000000" }, { FieldForm.Amount, "0" }, { FieldForm.Amount, "1234.5" },
        { FieldForm.SignedAmount, "-350000" }, { FieldForm.Rate, "0.4" }, { FieldForm.Rate, "0.00125" }, { FieldForm.Spread, "0.012" }, { FieldForm.Percent, "1.25" },
        { FieldForm.Number, "1.25" }, { FieldForm.Number, "-2000" }, { FieldForm.Days, "10" }, { FieldForm.Flag, "true" }, { FieldForm.Flag, "false" },
        { FieldForm.Period, "\"10y\"" }, { FieldForm.Date, "\"2031-11-11\"" }, { FieldForm.PeriodOrDate, "\"3m\"" }, { FieldForm.Choice, "\"SOUTH\"" }, { FieldForm.Id, "\"CSA-B\"" },
    };

    [Theory] // note 5: what Write writes, Read reads back to the same value
    [MemberData(nameof(Written))]
    public void What_is_written_reads_back_to_the_same_value(FieldForm form, string json)
    {
        var value = JsonNode.Parse(json);

        var back = FieldForms.Read(Of(form), FieldForms.Write(Of(form), value));

        Assert.Equal(FieldForms.Problem.None, back.Problem);
        if (value!.GetValueKind() == System.Text.Json.JsonValueKind.Number) Assert.Equal(value.GetValue<decimal>(), back.Value!.GetValue<decimal>());
        else Assert.True(JsonNode.DeepEquals(value, back.Value), $"{json} came back as {back.Value?.ToJsonString()}");
    }

    [Fact] // note 5: an amount is never written bare, since a bare number is millions; no value is written `empty`
    public void A_value_is_written_as_it_is_typed()
    {
        string W(FieldForm form, JsonNode? value) => FieldForms.Write(Of(form, optional: true), value);

        Assert.Equal(("5mm", "250k", "1.5bn", "0", "40%", "120bp", "10d", "off", "empty"),
            (W(FieldForm.Amount, 5_000_000m), W(FieldForm.Amount, 250_000m), W(FieldForm.Amount, 1_500_000_000m), W(FieldForm.Amount, 0m), W(FieldForm.Rate, 0.4m),
                W(FieldForm.Spread, 0.012m), W(FieldForm.Days, 10m), W(FieldForm.Flag, false), W(FieldForm.Rate, null)));
    }

    [Fact] // ADR-0016 note 44: a cell is written for a spreadsheet to read as the value held: a number plain, a rate and a spread in percent, a flag true or false, none empty
    public void A_value_is_written_as_a_cell_for_a_spreadsheet()
    {
        string C(FieldForm form, JsonNode? value) => FieldForms.Cell(Of(form, optional: true), value);

        Assert.Equal(("50000000", "1234.5", "-350000", "-2000.25", "10"),
            (C(FieldForm.Amount, 50_000_000m), C(FieldForm.Amount, 1234.5m), C(FieldForm.SignedAmount, -350_000m), C(FieldForm.Number, -2000.25m), C(FieldForm.Days, 10m)));
        // A rate held in percent, a rate held as a fraction and a spread are each the percent they mean.
        Assert.Equal(("1.25%", "40%", "0.125%", "1.2%"), (C(FieldForm.Percent, 1.25m), C(FieldForm.Rate, 0.4m), C(FieldForm.Rate, 0.00125m), C(FieldForm.Spread, 0.012m)));
        Assert.Equal(("true", "false", "2031-11-11", "10y", "3m", "SOUTH", "CSA-B"),
            (C(FieldForm.Flag, true), C(FieldForm.Flag, false), C(FieldForm.Date, "2031-11-11"), C(FieldForm.Period, "10y"), C(FieldForm.PeriodOrDate, "3m"), C(FieldForm.Choice, "SOUTH"), C(FieldForm.Id, "CSA-B")));
        Assert.Equal(("", "", ""), (C(FieldForm.Rate, null), C(FieldForm.Amount, null), C(FieldForm.Id, null)));
    }

    [Fact] // ADR-0016 notes 15 and 45: what a field has to choose from is a flag's two words and a choice's values, as a line writes them, and empty where it may be empty; any other form is typed freely
    public void What_a_field_has_to_choose_from_is_written_as_a_line_writes_it()
    {
        Assert.Equal(["on", "off"], FieldForms.ChoicesWritten(Of(FieldForm.Flag)));
        Assert.Equal(["NORTH", "SOUTH", "EAST"], FieldForms.ChoicesWritten(Of(FieldForm.Choice)));
        Assert.Equal(["NORTH", "SOUTH", "EAST", "empty"], FieldForms.ChoicesWritten(Of(FieldForm.Choice, optional: true)));
        Assert.Equal(["empty"], FieldForms.ChoicesWritten(Of(FieldForm.Rate, optional: true)));
        Assert.Empty(FieldForms.ChoicesWritten(Of(FieldForm.Amount)));
        // A choice with nothing to choose from is a registration's defect, as it is where one is read or written.
        Assert.Throws<InvalidOperationException>(() => FieldForms.ChoicesWritten(new FieldSpec(FieldForm.Choice)));
    }

    [Theory] // ADR-0016 note 44: a rate and a spread written as a cell read back here as the value held
    [InlineData(FieldForm.Percent, "1.25")]
    [InlineData(FieldForm.Rate, "0.00125")]
    [InlineData(FieldForm.Spread, "0.012")]
    [InlineData(FieldForm.Flag, "true")]
    [InlineData(FieldForm.Date, "\"2031-11-11\"")]
    public void A_cell_of_a_rate_a_flag_or_a_date_reads_back_to_the_same_value(FieldForm form, string json)
    {
        var value = JsonNode.Parse(json);

        var back = FieldForms.Read(Of(form), FieldForms.Cell(Of(form), value));

        Assert.Equal(FieldForms.Problem.None, back.Problem);
        if (value!.GetValueKind() == System.Text.Json.JsonValueKind.Number) Assert.Equal(value.GetValue<decimal>(), back.Value!.GetValue<decimal>());
        else Assert.True(JsonNode.DeepEquals(value, back.Value), $"{json} came back as {back.Value?.ToJsonString()}");
    }

    static readonly FieldSpec Load = new(FieldForm.Money, Choices: new Dictionary<string, string> { ["m3"] = "M3", ["gal"] = "GAL" });

    [Theory] // ADR-0016 note 47: a number followed by what it is in, one of the field's choices in any case, or the number alone
    [InlineData("-350,000 GAL", "-350000", "GAL")]
    [InlineData("-350k gal", "-350000", "GAL")]
    [InlineData("1.5mm   m3", "1500000", "M3")]
    [InlineData("-350000", "-350000", null)]
    public void Money_is_a_number_and_what_it_is_in(string text, string amount, string? unit)
    {
        var read = FieldForms.Read(Load, text);

        Assert.Equal(FieldForms.Problem.None, read.Problem);
        Assert.Equal((decimal.Parse(amount), unit), (FieldForms.Held(read.Value!["amount"]!), read.Value["unit"]?.GetValue<string>()));
        // What is written reads back to the same value.
        var again = FieldForms.Read(Load, FieldForms.Write(Load, read.Value));
        Assert.Equal((decimal.Parse(amount), unit), (FieldForms.Held(again.Value!["amount"]!), again.Value["unit"]?.GetValue<string>()));
    }

    [Theory] // ADR-0016 note 47: a unit that is none of the choices, and words that are no number, are refused with what the field takes
    [InlineData("350 XYZ")]
    [InlineData("GAL 350")]
    [InlineData("350 GAL more")]
    [InlineData("12%")]
    public void Money_that_is_not_one_is_refused_with_what_the_field_takes(string text)
    {
        var read = FieldForms.Read(Load, text);

        Assert.Equal((FieldForms.Problem.NotOfForm, "a number, alone or followed by what it is in: M3 or GAL"), (read.Problem, read.What));
    }

    [Fact] // ADR-0016 notes 44 and 47: money is written with every decimal and its unit, on a line and in a cell alike
    public void Money_is_written_as_its_number_and_its_unit()
    {
        var held = new JsonObject { ["amount"] = -350000.5m, ["unit"] = "GAL" };

        Assert.Equal("-350000.5 GAL", FieldForms.Write(Load, held));
        Assert.Equal("-350000.5 GAL", FieldForms.Cell(Load, held));
        Assert.Equal("-350000.5", FieldForms.Write(Load, new JsonObject { ["amount"] = -350000.5m }));
        Assert.True(FieldForms.Same(held, new JsonObject { ["amount"] = -350000.50m, ["unit"] = "GAL" }));
        Assert.False(FieldForms.Same(held, new JsonObject { ["amount"] = -350000.5m }));
    }
}
