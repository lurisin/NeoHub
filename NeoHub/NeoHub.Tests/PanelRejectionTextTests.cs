using DSC.TLink.ITv2;
using DSC.TLink.ITv2.Enumerations;

namespace NeoHub.Tests;

public class PanelRejectionTextTests
{
    [Fact]
    public void Every_response_code_and_every_nack_code_has_its_own_text()
    {
        Assert.Equal(Enum.GetValues<CommandResponseCode>().OrderBy(c => c), PanelRejectionText.ResponseTexts.Keys.OrderBy(c => c));
        Assert.Equal(Enum.GetValues<ITv2NackCode>().OrderBy(c => c), PanelRejectionText.NackTexts.Keys.OrderBy(c => c));
        Assert.All(PanelRejectionText.ResponseTexts.Values.Concat(PanelRejectionText.NackTexts.Values),
            t => Assert.False(string.IsNullOrWhiteSpace(t)));
    }

    [Theory]
    [InlineData(17, "Invalid access code")]
    [InlineData(18, "Access code required")]
    [InlineData(19, "Panel busy")]
    [InlineData(20, "Invalid partition")]
    [InlineData(23, "Function not available")]
    [InlineData(25, "Panel command timed out")]
    [InlineData(1, "Panel rejected the command (code 1)")]
    [InlineData(5, "Panel rejected the command (code 5)")]
    [InlineData(200, "Panel rejected the command (code 200)")]
    [InlineData(300, "Panel rejected the command (code 300)")]
    [InlineData(-1, "Panel rejected the command (code -1)")]
    public void Response_code_texts_for_commands_other_than_arm(int code, string text)
    {
        Assert.Equal(text, PanelRejectionText.ForResponseCode(code, armCommand: false));
    }

    [Fact]
    public void Response_code_1_reads_not_ready_only_for_arm_commands()
    {
        Assert.Equal("Partition not ready to arm", PanelRejectionText.ForResponseCode(1, armCommand: true));
        Assert.Equal("Panel rejected the command (code 1)", PanelRejectionText.ForResponseCode(1, armCommand: false));
        Assert.Equal("Invalid access code", PanelRejectionText.ForResponseCode(17, armCommand: true));
    }

    [Theory]
    [InlineData(9, "Keypad locked out")]
    [InlineData(17, "Invalid access code")]
    [InlineData(18, "Access code required")]
    [InlineData(19, "Panel busy")]
    [InlineData(20, "Invalid partition")]
    [InlineData(23, "Function not available")]
    [InlineData(1, "Invalid command length")]
    [InlineData(12, "Panel refused the command (NACK 12)")]
    [InlineData(255, "Panel refused the command (NACK 255)")]
    [InlineData(256, "Panel refused the command (NACK 256)")]
    public void Nack_code_texts(int code, string text)
    {
        Assert.Equal(text, PanelRejectionText.ForNackCode(code));
    }

    [Fact]
    public void Nack_1_is_never_not_ready()
    {
        Assert.NotEqual(PanelRejectionText.NotReadyToArm, PanelRejectionText.ForNackCode(1));
    }
}
