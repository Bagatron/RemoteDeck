namespace RemoteDeck.Protocols.Vnc;

/// <summary>The VNC server did something this viewer cannot follow, or refused the connection.</summary>
public class RfbException : Exception
{
    public RfbException(string message)
        : base(message)
    {
    }
}

/// <summary>The server wants a password and none was given.</summary>
public sealed class VncPasswordRequiredException : RfbException
{
    public VncPasswordRequiredException()
        : base("This VNC server needs a password.")
    {
    }
}

/// <summary>The server refused the password.</summary>
public sealed class VncAuthenticationException : RfbException
{
    public VncAuthenticationException(string message)
        : base(message)
    {
    }
}

/// <summary>The server only offers a way of signing in (or an encoding) that this viewer does not have.</summary>
public sealed class VncUnsupportedException : RfbException
{
    public VncUnsupportedException(string message)
        : base(message)
    {
    }
}
