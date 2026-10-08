using System.Text;

namespace RemoteDeck.Protocols.Telnet.Tests;

public class TelnetParserTests
{
    private const byte Iac = 255;
    private const byte Dont = 254;
    private const byte Do = 253;
    private const byte Wont = 252;
    private const byte Will = 251;
    private const byte Sb = 250;
    private const byte Se = 240;

    private static (string Text, byte[] Reply) Feed(TelnetParser parser, params byte[] bytes)
    {
        var (output, reply) = parser.Feed(bytes);
        return (Encoding.Latin1.GetString(output), reply);
    }

    [Fact]
    public void Plain_text_passes_through()
    {
        var (text, reply) = Feed(new TelnetParser("xterm"), Encoding.ASCII.GetBytes("login: "));

        Assert.Equal("login: ", text);
        Assert.Empty(reply);
    }

    [Fact]
    public void Escaped_255_is_data()
    {
        var (output, _) = new TelnetParser("xterm").Feed(new byte[] { 65, Iac, Iac, 66 });

        Assert.Equal(new byte[] { 65, 255, 66 }, output);
    }

    [Fact]
    public void Cr_nul_becomes_a_plain_cr()
    {
        var (output, _) = new TelnetParser("xterm").Feed(new byte[] { 65, 13, 0, 66, 13, 10 });

        Assert.Equal(new byte[] { 65, 13, 66, 13, 10 }, output);
    }

    [Fact]
    public void Server_echo_and_suppress_go_ahead_are_accepted()
    {
        var parser = new TelnetParser("xterm");

        var (text, reply) = Feed(parser, Iac, Will, 1, Iac, Will, 3);

        Assert.Equal(string.Empty, text);
        Assert.Equal(new byte[] { Iac, Do, 1, Iac, Do, 3 }, reply);
        Assert.True(parser.ServerEchoes);
    }

    [Fact]
    public void A_repeated_will_is_not_answered_twice()
    {
        var parser = new TelnetParser("xterm");
        Feed(parser, Iac, Will, 1);

        var (_, reply) = Feed(parser, Iac, Will, 1);

        Assert.Empty(reply);
    }

    [Fact]
    public void Unknown_options_are_refused()
    {
        var parser = new TelnetParser("xterm");

        var (_, reply) = Feed(parser, Iac, Will, 39, Iac, Do, 39);

        Assert.Equal(new byte[] { Iac, Dont, 39, Iac, Wont, 39 }, reply);
    }

    [Fact]
    public void Wont_after_will_turns_echo_off()
    {
        var parser = new TelnetParser("xterm");
        Feed(parser, Iac, Will, 1);

        var (_, reply) = Feed(parser, Iac, Wont, 1);

        Assert.Equal(new byte[] { Iac, Dont, 1 }, reply);
        Assert.False(parser.ServerEchoes);
    }

    [Fact]
    public void Window_size_is_offered_with_the_current_size()
    {
        var parser = new TelnetParser("xterm");
        parser.SetSize(120, 40);

        var (_, reply) = Feed(parser, Iac, Do, 31);

        Assert.Equal(new byte[] { Iac, Will, 31, Iac, Sb, 31, 0, 120, 0, 40, Iac, Se }, reply);
        Assert.True(parser.SendsWindowSize);
    }

    [Fact]
    public void Resizing_sends_a_frame_only_when_the_server_asked()
    {
        var parser = new TelnetParser("xterm");
        Assert.Empty(parser.SetSize(100, 30));

        Feed(parser, Iac, Do, 31);
        var frame = parser.SetSize(132, 43);

        Assert.Equal(new byte[] { Iac, Sb, 31, 0, 132, 0, 43, Iac, Se }, frame);
    }

    [Fact]
    public void A_size_of_255_is_escaped()
    {
        var parser = new TelnetParser("xterm");
        Feed(parser, Iac, Do, 31);

        var frame = parser.SetSize(255, 24);

        Assert.Equal(new byte[] { Iac, Sb, 31, 0, 255, 255, 0, 24, Iac, Se }, frame);
    }

    [Fact]
    public void Terminal_type_is_sent_when_asked()
    {
        var parser = new TelnetParser("vt100");
        Feed(parser, Iac, Do, 24);

        var (_, reply) = Feed(parser, Iac, Sb, 24, 1, Iac, Se);

        var expected = new List<byte> { Iac, Sb, 24, 0 };
        expected.AddRange(Encoding.ASCII.GetBytes("vt100"));
        expected.AddRange(new[] { Iac, Se });
        Assert.Equal(expected.ToArray(), reply);
    }

    [Fact]
    public void Terminal_type_is_not_sent_if_it_was_never_agreed()
    {
        var (_, reply) = Feed(new TelnetParser("vt100"), Iac, Sb, 24, 1, Iac, Se);

        Assert.Empty(reply);
    }

    [Fact]
    public void Commands_split_across_reads_still_work()
    {
        var parser = new TelnetParser("xterm");

        var (first, firstReply) = Feed(parser, 72, Iac);
        var (second, secondReply) = Feed(parser, Will, 1, 105);

        Assert.Equal("H", first);
        Assert.Empty(firstReply);
        Assert.Equal("i", second);
        Assert.Equal(new byte[] { Iac, Do, 1 }, secondReply);
    }

    [Fact]
    public void Sub_negotiation_inside_text_is_removed()
    {
        var (text, _) = Feed(new TelnetParser("xterm"), 65, Iac, Sb, 99, 1, 2, 3, Iac, Se, 66);

        Assert.Equal("AB", text);
    }

    [Fact]
    public void Unterminated_sub_negotiation_cannot_grow_without_limit()
    {
        var parser = new TelnetParser("xterm");
        var junk = new byte[100_000];
        Array.Fill(junk, (byte)7);

        parser.Feed(new byte[] { Iac, Sb, 99 });
        var exception = Record.Exception(() => parser.Feed(junk));

        Assert.Null(exception);
    }

    [Fact]
    public void Typed_255_is_doubled()
    {
        Assert.Equal(new byte[] { 65, 255, 255 }, TelnetParser.EncodeInput(new byte[] { 65, 255 }));
    }

    [Fact]
    public void Return_alone_becomes_cr_nul_and_cr_lf_is_kept()
    {
        Assert.Equal(new byte[] { 97, 13, 0 }, TelnetParser.EncodeInput(new byte[] { 97, 13 }));
        Assert.Equal(new byte[] { 97, 13, 10, 98 }, TelnetParser.EncodeInput(new byte[] { 97, 13, 10, 98 }));
        Assert.Equal(new byte[] { 13, 0, 97 }, TelnetParser.EncodeInput(new byte[] { 13, 97 }));
    }
}
