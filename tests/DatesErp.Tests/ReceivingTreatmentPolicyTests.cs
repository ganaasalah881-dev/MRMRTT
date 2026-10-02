using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;

namespace DatesErp.Tests;

public class ReceivingTreatmentPolicyTests
{
    private static TreatmentPartDto P(double qty, int packages = 0, string level = "Medium", double? hours = null)
        => new() { QtyKg = qty, PackageCount = packages, InfestationLevel = level, DurationHours = hours };

    [Fact]
    public void Exact_5000_Basket_Scenario_Has_Three_Immutable_Grade_Durations()
    {
        var result = ReceivingTreatmentPolicy.NormalizeParts(100000, 5000,
            new[] { P(80000, 4000, "Light"), P(10000, 500), P(10000, 500, "High") });
        Assert.Equal(new double?[] { 120, 168, 240 }, result.Select(p => p.DurationHours));
        Assert.Equal(5000, result.Sum(p => p.PackageCount));
        Assert.Equal(100000, result.Sum(p => p.QtyKg));
    }

    [Fact]
    public void Largest_Remainders_Avoids_Rounding_Three_Packages_Into_Two()
    {
        var result = ReceivingTreatmentPolicy.NormalizeParts(30, 2, new[] { P(10), P(10), P(10) });
        Assert.Equal(new[] { 1, 1, 0 }, result.Select(p => p.PackageCount));
    }

    [Fact]
    public void Explicit_Packages_Are_Reserved_Before_Automatic_Shares()
    {
        var result = ReceivingTreatmentPolicy.NormalizeParts(100, 10, new[] { P(30), P(30), P(40, 6) });
        Assert.Equal(new[] { 2, 2, 6 }, result.Select(p => p.PackageCount));
    }

    [Fact]
    public void Normalization_Does_Not_Mutate_The_Input_Or_Lose_Notes()
    {
        var input = new[] { P(25), P(75) }; input[0].Notes = "عزل الجزء الأول";
        var result = ReceivingTreatmentPolicy.NormalizeParts(100, 8, input);
        Assert.Equal(new[] { 2, 6 }, result.Select(p => p.PackageCount));
        Assert.Equal(0, input[0].PackageCount);
        Assert.Null(input[0].DurationHours);
        Assert.NotSame(input[0], result[0]);
        Assert.Equal(input[0].Notes, result[0].Notes);
    }

    [Fact]
    public void No_Explicit_Split_Is_A_Single_Medium_Part()
    {
        var part = Assert.Single(ReceivingTreatmentPolicy.NormalizeParts(123.5, 4, null));
        Assert.Equal(123.5, part.QtyKg); Assert.Equal(4, part.PackageCount);
        Assert.Equal(168, part.DurationHours); Assert.Equal("Medium", part.InfestationLevel);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void Invalid_Part_Is_Rejected_Not_Silently_Filtered(double qty)
    {
        Assert.Throws<DomainException>(() => ReceivingTreatmentPolicy.NormalizeParts(100, 10, new[] { P(100, 10), P(qty) }));
    }

    [Theory]
    [InlineData(-1, 11)] [InlineData(7, 7)] [InlineData(4, 5)]
    public void Incorrect_Explicit_Package_Counts_Are_Rejected(int first, int second)
    {
        Assert.Throws<DomainException>(() => ReceivingTreatmentPolicy.NormalizeParts(100, 10, new[] { P(50, first), P(50, second) }));
    }

    [Theory]
    [InlineData(1)] [InlineData(0)] [InlineData(500)] [InlineData(double.NaN)]
    public void Shipment_Grade_Duration_Cannot_Be_Overridden(double hours)
    {
        Assert.Equal("INVALID_DURATION", Assert.Throws<DomainException>(() =>
            ReceivingTreatmentPolicy.NormalizeParts(100, 10, new[] { P(100, 10, "Light", hours) })).Code);
    }

    [Theory]
    [InlineData(null, "WRM")] [InlineData("", "WRM")] [InlineData(" wrm ", "WRM")] [InlineData("wtrt", "WTRT")]
    public void Legacy_Empty_Destination_Remains_Raw(string? value, string expected)
        => Assert.Equal(expected, ReceivingTreatmentPolicy.Destination(value));

    [Fact]
    public void Unknown_Destination_And_Grade_Are_Not_Silently_Normalized()
    {
        Assert.Throws<DomainException>(() => ReceivingTreatmentPolicy.Destination("WFG"));
        Assert.Throws<DomainException>(() => ReceivingTreatmentPolicy.NormalizeParts(100, 10, new[] { P(100, 10, "unknown") }));
    }

    [Fact]
    public void Weight_Only_Receipt_With_No_Packages_Remains_Supported()
    {
        var result = ReceivingTreatmentPolicy.NormalizeParts(100, 0, new[] { P(40), P(60) });
        Assert.All(result, p => Assert.Equal(0, p.PackageCount));
    }

    [Fact]
    public void Many_Automatic_Splits_Always_Conserve_Whole_Packages()
    {
        var random = new Random(107);
        for (int n = 1; n <= 50; n++)
        {
            var input = Enumerable.Range(0, n).Select(_ => P(random.Next(1, 10000))).ToArray();
            int packages = random.Next(0, 5001);
            var result = ReceivingTreatmentPolicy.NormalizeParts(input.Sum(p => p.QtyKg), packages, input);
            Assert.Equal(packages, result.Sum(p => p.PackageCount));
            Assert.All(result, p => Assert.True(p.PackageCount >= 0));
        }
    }
}
