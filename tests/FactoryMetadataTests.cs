using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace PepperDash.Essentials.Plugin.WyreStorm.Tests;

public class FactoryMetadataTests
{
    // Concrete factories assign MinimumEssentialsFrameworkVersion = MinimumEssentialsVersion,
    // a const defined on the NhdBaseDeviceFactory<T> base. Verify the const is "3.0.0"...
    [Fact]
    public void Base_Factory_Defines_MinimumEssentialsVersion_As_3_0_0()
    {
        var content = AssemblyFixture.FindSourceForClass("NhdBaseDeviceFactory");
        content.Should().NotBeNull("NhdBaseDeviceFactory source should exist");
        Regex.IsMatch(content!, @"MinimumEssentialsVersion\s*=\s*""3\.0\.0""")
            .Should().BeTrue("base factory should define MinimumEssentialsVersion = \"3.0.0\"");
    }

    // ...and that each concrete factory assigns the framework version from it.
    [Theory]
    [InlineData("NhdCtlDeviceFactory")]
    [InlineData("NhdRxDeviceFactory")]
    [InlineData("NhdTxDeviceFactory")]
    public void Factory_Assigns_MinimumEssentialsFrameworkVersion(string factoryClassName)
    {
        var content = AssemblyFixture.FindSourceForClass(factoryClassName);
        content.Should().NotBeNull($"source for '{factoryClassName}' should exist");
        Regex.IsMatch(content!, @"MinimumEssentialsFrameworkVersion\s*=\s*MinimumEssentialsVersion")
            .Should().BeTrue($"{factoryClassName} should set MinimumEssentialsFrameworkVersion = MinimumEssentialsVersion");
    }

    [Theory]
    [InlineData("NhdCtlDeviceFactory")]
    [InlineData("NhdRxDeviceFactory")]
    [InlineData("NhdTxDeviceFactory")]
    [InlineData("NhdSinkDisplayFactory")]
    public void Factory_Sets_TypeNames(string factoryClassName)
    {
        var content = AssemblyFixture.FindSourceForClass(factoryClassName);
        content.Should().NotBeNull($"source for '{factoryClassName}' should exist");
        Regex.IsMatch(content!, @"TypeNames\s*=\s*new\s+List<string>")
            .Should().BeTrue($"{factoryClassName} should set TypeNames in the constructor");
    }

    [Theory]
    [InlineData("NhdCtlDeviceFactory", "nhd-ctl-pro")]
    [InlineData("NhdCtlDeviceFactory", "nhdctlpro")]
    [InlineData("NhdRxDeviceFactory", "nhd-150-rx")]
    [InlineData("NhdRxDeviceFactory", "nhd150rx")]
    [InlineData("NhdTxDeviceFactory", "nhd-120-tx")]
    [InlineData("NhdTxDeviceFactory", "nhd120tx")]
    [InlineData("NhdSinkDisplayFactory", "nhd-sink-display")]
    [InlineData("NhdSinkDisplayFactory", "nhdsinkdisplay")]
    public void Factory_Source_Contains_TypeName(string factoryClassName, string typeName)
    {
        var content = AssemblyFixture.FindSourceForClass(factoryClassName);
        content.Should().NotBeNull($"source for '{factoryClassName}' should exist");
        content!.Should().Contain($"\"{typeName}\"",
            $"{factoryClassName} should register type name \"{typeName}\"");
    }

    [Fact]
    public void No_Duplicate_TypeNames_Across_Factories()
    {
        var all = new List<string>();
        foreach (var factory in new[] { "NhdCtlDeviceFactory", "NhdRxDeviceFactory", "NhdTxDeviceFactory" })
        {
            var content = AssemblyFixture.FindSourceForClass(factory);
            content.Should().NotBeNull($"source for '{factory}' should exist");
            var match = Regex.Match(content!, @"TypeNames\s*=\s*new\s+List<string>\s*\{([^}]+)\}");
            if (!match.Success) continue;
            all.AddRange(Regex.Matches(match.Groups[1].Value, @"""([^""]+)""").Select(m => m.Groups[1].Value));
        }

        all.Should().OnlyHaveUniqueItems("TypeNames should be unique across all factories");
    }
}
