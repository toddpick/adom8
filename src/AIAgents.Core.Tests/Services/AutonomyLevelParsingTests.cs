using AIAgents.Core.Services;

namespace AIAgents.Core.Tests.Services;

/// <summary>
/// Tests for parsing autonomy level from picklist strings and legacy integer values.
/// </summary>
public sealed class AutonomyLevelParsingTests
{
    [Theory]
    [InlineData("1 - Plan Only", 1)]
    [InlineData("2 - Code Only", 2)]
    [InlineData("3 - Review & Pause", 3)]
    [InlineData("4 - Auto-Merge", 4)]
    [InlineData("5 - Full Autonomy", 5)]
    public void ParsePicklistValues_ReturnsCorrectLevel(string value, int expected)
    {
        Assert.Equal(expected, AzureDevOpsClient.ParseAutonomyLevel(value));
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData("3", 3)]
    [InlineData("4", 4)]
    [InlineData("5", 5)]
    public void ParsePlainIntegers_ReturnsCorrectLevel(string value, int expected)
    {
        Assert.Equal(expected, AzureDevOpsClient.ParseAutonomyLevel(value));
    }

    [Theory]
    [InlineData("1.0", 1)]
    [InlineData("3.0", 3)]
    [InlineData("5.0", 5)]
    public void ParseDoubleStrings_ReturnsCorrectLevel(string value, int expected)
    {
        Assert.Equal(expected, AzureDevOpsClient.ParseAutonomyLevel(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseNullOrEmpty_ReturnsDefault3(string? value)
    {
        Assert.Equal(3, AzureDevOpsClient.ParseAutonomyLevel(value));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("6")]
    [InlineData("99")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("Plan Only")]
    public void ParseInvalidValues_ReturnsDefault3(string value)
    {
        Assert.Equal(3, AzureDevOpsClient.ParseAutonomyLevel(value));
    }

    [Fact]
    public void ParseWithLeadingWhitespace_Works()
    {
        Assert.Equal(4, AzureDevOpsClient.ParseAutonomyLevel("  4 - Auto-Merge"));
    }
}
