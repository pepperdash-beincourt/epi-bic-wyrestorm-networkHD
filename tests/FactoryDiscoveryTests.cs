using FluentAssertions;
using Xunit;

namespace PepperDash.Essentials.Plugin.WyreStorm.Tests;

public class FactoryDiscoveryTests
{
    [Fact]
    public void Assembly_Loads_Successfully()
    {
        AssemblyFixture.PluginAssembly.Should().NotBeNull();
    }

    [Fact]
    public void Assembly_Name_Is_Expected()
    {
        AssemblyFixture.PluginAssembly.GetName().Name.Should().Be("epi-wyrestorm-networkHD.4Series");
    }

    [Fact]
    public void Factory_Count_Is_Seven()
    {
        // NhdCtl (controller), NhdRx (decoder), NhdTx (encoder), NhdSinkDisplay (a display driven
        // through its decoder), plus the mock equivalents (MockNhdCtl, MockNhdRx, MockNhdTx) for
        // local dev/testing without real hardware. The abstract NhdBaseDeviceFactory<T> base is
        // excluded.
        AssemblyFixture.FindFactoryTypes().Should().HaveCount(7);
    }

    [Theory]
    [InlineData("NhdCtlDeviceFactory")]
    [InlineData("NhdRxDeviceFactory")]
    [InlineData("NhdTxDeviceFactory")]
    [InlineData("NhdSinkDisplayFactory")]
    [InlineData("MockNhdCtlFactory")]
    [InlineData("MockNhdRxFactory")]
    [InlineData("MockNhdTxFactory")]
    public void Factory_Exists_ByName(string factoryClassName)
    {
        AssemblyFixture.FindFactoryTypes()
            .Should().Contain(t => t.Name == factoryClassName,
                $"factory '{factoryClassName}' should be discoverable");
    }

    [Fact]
    public void All_Factories_Have_Parameterless_Constructor()
    {
        foreach (var factory in AssemblyFixture.FindFactoryTypes())
        {
            factory.GetConstructor(Type.EmptyTypes).Should()
                .NotBeNull($"Factory '{factory.Name}' must have a parameterless constructor");
        }
    }
}
