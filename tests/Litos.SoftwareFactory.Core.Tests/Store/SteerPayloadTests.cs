using Litos.SoftwareFactory.Core.Store;

namespace Litos.SoftwareFactory.Core.Tests.Store;

public class SteerPayloadTests
{
    [Theory]
    [InlineData(SteerStatus.Held, """{"steer":"held"}""")]
    [InlineData(SteerStatus.Delivered, """{"steer":"delivered"}""")]
    public void AStatus_IsWrittenAndReadBack(SteerStatus status, string json)
    {
        Assert.Equal(json, SteerPayload.For(status));
        Assert.Equal(status, SteerPayload.Read(json));
    }

    [Fact]
    public void ADroppedSteer_CarriesItsReason()
    {
        var json = SteerPayload.For(SteerStatus.Dropped, "The task was cancelled.");

        Assert.Equal("""{"steer":"dropped","reason":"The task was cancelled."}""", json);
        Assert.Equal(SteerStatus.Dropped, SteerPayload.Read(json));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("""{"plain":true}""")]
    [InlineData("""{"steer":"sideways"}""")]
    [InlineData("""{"steer":3}""")]
    [InlineData("[1]")]
    [InlineData("not json")]
    public void AnythingElse_IsNoSteer(string? payload)
    {
        Assert.Null(SteerPayload.Read(payload));
    }
}
