namespace RemoteDeck.Protocols.Serial.Tests;

public class SerialPortDetectorTests
{
    [Theory]
    [InlineData("USB Serial Port (COM3)", "COM3")]
    [InlineData("Prolific USB-to-Serial Comm Port (COM12)", "COM12")]
    [InlineData("Standard Serial over Bluetooth link (com4)", "COM4")]
    [InlineData("Communications Port (COM1) ", "COM1")]
    public void PortOf_FindsThePortInTheDeviceName(string name, string expected)
    {
        Assert.Equal(expected, SerialPortDetector.PortOf(name));
    }

    [Theory]
    [InlineData("USB Serial Port")]
    [InlineData("Intel(R) Active Management Technology - SOL")]
    [InlineData("(COM3) at the start")]
    [InlineData("")]
    public void PortOf_IsNullWhenNoPortIsNamed(string name)
    {
        Assert.Null(SerialPortDetector.PortOf(name));
    }

    [Theory]
    [InlineData("USB Serial Port (COM3)", "USB Serial Port")]
    [InlineData("Communications Port (COM1)", "Communications Port")]
    [InlineData("Plain name", "Plain name")]
    public void Describe_DropsThePortFromTheName(string name, string expected)
    {
        Assert.Equal(expected, SerialPortDetector.Describe(name));
    }

    [Theory]
    [InlineData("@oem12.inf,%ftdi_ser.devicedesc%;USB Serial Converter", "USB Serial Converter")]
    [InlineData("USB Serial Port", "USB Serial Port")]
    [InlineData("  ", "")]
    [InlineData(null, "")]
    public void ReadableDeviceText_TakesTheTextAfterTheLastSemicolon(string? text, string expected)
    {
        Assert.Equal(expected, SerialPortDetector.ReadableDeviceText(text));
    }

    [Fact]
    public void InOrder_PutsComPortsInNumberOrder()
    {
        var ordered = SerialPortDetector.InOrder(new[] { "COM10", "COM2", "COM1", "com2", "COM3" }).ToList();

        Assert.Equal(new[] { "COM1", "COM2", "COM3", "COM10" }, ordered);
    }

    [Fact]
    public void InOrder_PutsOtherNamesLastAndSkipsBlanks()
    {
        var ordered = SerialPortDetector.InOrder(new[] { "ttyUSB0", "COM4", " ", "" }).ToList();

        Assert.Equal(new[] { "COM4", "ttyUSB0" }, ordered);
    }

    [Fact]
    public void Combine_AttachesDescriptionsIgnoringCase()
    {
        var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["COM3"] = "USB Serial Port",
            ["COM9"] = "A port that is not plugged in",
        };

        var ports = SerialPortDetector.Combine(new[] { "COM4", "com3" }, descriptions);

        Assert.Equal(2, ports.Count);
        Assert.Equal("com3", ports[0].Name);
        Assert.Equal("USB Serial Port", ports[0].Description);
        Assert.Equal("com3 - USB Serial Port", ports[0].Label);
        Assert.Equal("COM4", ports[1].Name);
        Assert.Equal(string.Empty, ports[1].Description);
        Assert.Equal("COM4", ports[1].Label);
    }

    [Fact]
    public void Detect_DoesNotThrow()
    {
        // The result depends on the computer; it only has to come back, ordered, with no blank names.
        var ports = SerialPortDetector.Detect();

        Assert.All(ports, port => Assert.False(string.IsNullOrWhiteSpace(port.Name)));
    }
}
