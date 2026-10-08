using Renci.SshNet;
using SshNet.Agent;

namespace RemoteDeck.Protocols.Ssh;

/// <summary>
/// The keys held by a running SSH agent. The agent does the signing, so the private keys never enter this program
/// and a key protected by a passphrase is unlocked once, in the agent, instead of each time you connect.
/// </summary>
internal static class AgentKeys
{
    /// <summary>Tries the Windows OpenSSH agent first, then Pageant.</summary>
    public static IPrivateKeySource[] Load()
    {
        var tried = new List<string>();

        var keys = TryLoad("the Windows OpenSSH agent", () => new SshAgent().RequestIdentities().Cast<IPrivateKeySource>().ToArray(), tried)
                   ?? TryLoad("Pageant", () => new Pageant().RequestIdentities().Cast<IPrivateKeySource>().ToArray(), tried);
        if (keys is not null)
        {
            return keys;
        }

        throw new SshConnectionException(
            "No SSH agent keys are available (" + string.Join("; ", tried) + "). "
            + "Start the 'OpenSSH Authentication Agent' service and run ssh-add, or start Pageant and load a key.");
    }

    private static IPrivateKeySource[]? TryLoad(string name, Func<IPrivateKeySource[]> request, List<string> tried)
    {
        try
        {
            var keys = request();
            if (keys.Length > 0)
            {
                return keys;
            }

            tried.Add($"{name} has no keys loaded");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException
                                       or UnauthorizedAccessException or System.ComponentModel.Win32Exception
                                       or Renci.SshNet.Common.SshException or NotSupportedException)
        {
            tried.Add($"{name} is not running");
        }

        return null;
    }
}
