namespace RemoteDeck.Protocols.Ssh;

/// <summary>Lets the UI check the <c>forwards</c> option with the same rules the connection uses.</summary>
public static class PortForwardCheck
{
    /// <exception cref="SshConnectionException">The text is not a valid list of port forwards.</exception>
    public static void Validate(string forwards) => PortForward.ParseList(forwards);
}
