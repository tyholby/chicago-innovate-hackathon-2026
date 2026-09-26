using System.Globalization;
using FamilyStudio.Core.Model;

namespace FamilyStudio.Core.Tests;

public class DimensionsTests
{
    public DimensionsTests() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData("650", LengthUnit.Millimetres, 0.65)]
    [InlineData("25.5", LengthUnit.Inches, 0.6477)]
    [InlineData("650 mm", LengthUnit.Inches, 0.65)]
    [InlineData("65cm", LengthUnit.Inches, 0.65)]
    [InlineData("0.65 m", LengthUnit.Inches, 0.65)]
    [InlineData("30\"", LengthUnit.Millimetres, 0.762)]
    [InlineData("2'-6\"", LengthUnit.Millimetres, 0.762)]
    [InlineData("2' 6 1/2\"", LengthUnit.Millimetres, 0.7747)]
    [InlineData("3'", LengthUnit.Millimetres, 0.9144)]
    [InlineData("2’-6”", LengthUnit.Millimetres, 0.762)]
    public void Lengths_parse_to_metres(string text, LengthUnit unit, double metres) =>
        Assert.Equal(metres, Dimensions.ParseLength(text, unit), 4);

    [Theory]
    [InlineData("abc")]
    [InlineData("12 parsecs")]
    [InlineData("")]
    public void Nonsense_is_rejected_with_a_readable_message(string text)
    {
        var error = Assert.Throws<ArgumentException>(() => Dimensions.ParseLength(text, LengthUnit.Millimetres));
        Assert.DoesNotContain("\u2014", error.Message);
    }

    [Fact]
    public void Blank_fields_mean_estimate()
    {
        Assert.Null(Dimensions.Parse("", " ", "", LengthUnit.Millimetres));
        Assert.Throws<ArgumentException>(() => Dimensions.Parse("600", "", "", LengthUnit.Millimetres));
    }

    [Fact]
    public void Sizes_must_fit_the_preview_room() =>
        Assert.Throws<ArgumentException>(() => Dimensions.Parse("9000", "600", "700", LengthUnit.Millimetres));
}
