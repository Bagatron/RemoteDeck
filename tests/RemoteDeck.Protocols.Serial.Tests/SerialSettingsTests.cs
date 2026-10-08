using System.IO.Ports;

namespace RemoteDeck.Protocols.Serial.Tests;

public class SerialSettingsTests
{
    private static SerialSettings Read(string host = "COM3", params (string Key, string Value)[] options) =>
        SerialSettings.From(host, options.ToDictionary(o => o.Key, o => o.Value));

    [Fact]
    public void Defaults_are_9600_8N1_without_flow_control()
    {
        var settings = Read();

        Assert.Equal("COM3", settings.PortName);
        Assert.Equal(9600, settings.BaudRate);
        Assert.Equal(8, settings.DataBits);
        Assert.Equal(Parity.None, settings.Parity);
        Assert.Equal(StopBits.One, settings.StopBits);
        Assert.Equal(Handshake.None, settings.Handshake);
        Assert.True(settings.TranslateLf);
    }

    [Theory]
    [InlineData("8N1", 8, Parity.None, StopBits.One)]
    [InlineData("7E1", 7, Parity.Even, StopBits.One)]
    [InlineData("8o2", 8, Parity.Odd, StopBits.Two)]
    [InlineData("5M1.5", 5, Parity.Mark, StopBits.OnePointFive)]
    [InlineData("6S1", 6, Parity.Space, StopBits.One)]
    public void Format_is_read(string format, int dataBits, Parity parity, StopBits stopBits)
    {
        var settings = Read(options: ("format", format));

        Assert.Equal(dataBits, settings.DataBits);
        Assert.Equal(parity, settings.Parity);
        Assert.Equal(stopBits, settings.StopBits);
    }

    [Theory]
    [InlineData("9N1")]
    [InlineData("8X1")]
    [InlineData("8N3")]
    [InlineData("eight")]
    public void A_bad_format_is_explained(string format)
    {
        var error = Assert.Throws<SerialConnectionException>(() => Read(options: ("format", format)));

        Assert.Contains("8N1", error.Message);
    }

    [Theory]
    [InlineData("115200", 115200)]
    [InlineData(" 19200 ", 19200)]
    public void Baud_is_read(string text, int expected)
    {
        Assert.Equal(expected, Read(options: ("baud", text)).BaudRate);
    }

    [Theory]
    [InlineData("fast")]
    [InlineData("0")]
    [InlineData("-9600")]
    [InlineData("99999999")]
    public void A_bad_baud_rate_is_explained(string text)
    {
        var error = Assert.Throws<SerialConnectionException>(() => Read(options: ("baud", text)));

        Assert.Contains("baud rate", error.Message);
    }

    [Theory]
    [InlineData("none", Handshake.None)]
    [InlineData("XonXoff", Handshake.XOnXOff)]
    [InlineData("rtscts", Handshake.RequestToSend)]
    public void Flow_control_is_read(string text, Handshake expected)
    {
        Assert.Equal(expected, Read(options: ("flow", text)).Handshake);
    }

    [Fact]
    public void An_unknown_flow_control_is_explained()
    {
        var error = Assert.Throws<SerialConnectionException>(() => Read(options: ("flow", "magic")));

        Assert.Contains("flow control", error.Message);
    }

    [Fact]
    public void Translating_line_feeds_can_be_turned_off()
    {
        Assert.False(Read(options: ("translateLf", "false")).TranslateLf);
    }

    [Fact]
    public void A_missing_port_name_is_explained()
    {
        var error = Assert.Throws<SerialConnectionException>(() => Read(" "));

        Assert.Contains("COM3", error.Message);
    }

    [Fact]
    public void Check_uses_the_same_rules()
    {
        Assert.Throws<SerialConnectionException>(() =>
            SerialSettingsCheck.Validate("COM1", new Dictionary<string, string> { ["baud"] = "x" }));
        SerialSettingsCheck.Validate("COM1", null);
    }
}
