using Litos.Gui;

namespace Litos.Gui.Tests;

public class MainWindowPtcTests
{
    [Theory]
    [InlineData(true, true, "PTC: On", true)]
    [InlineData(true, false, "PTC: Off", false)]
    // Unavailable wins over a session's persisted ON state: the kernel can't run in this build.
    [InlineData(false, true, "PTC: unavailable", false)]
    [InlineData(false, false, "PTC: unavailable", false)]
    public void PtcStatusLabel_ReflectsAvailabilityThenState(bool available, bool enabled, string label, bool isOn) =>
        Assert.Equal((label, isOn), MainWindow.PtcStatusLabel(available, enabled));

    [Fact]
    public void SlashCommands_ListPtcAndItsReset() =>
        Assert.Contains(SlashCommands.All, c => c.Name == "/ptc");
}
