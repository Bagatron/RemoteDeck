using RemoteDeck.Plugin;

namespace RemoteDeck.Samples.Hello;

/// <summary>
/// The smallest useful plugin: one command in the Ctrl+K palette and one new connection type.
/// Copy this folder to start your own.
/// </summary>
public sealed class HelloPlugin : IPlugin
{
    public void Initialize(IPluginContext context)
    {
        context.Commands.Register(new PluginCommand(
            Id: "hello.say",
            Title: "Hello: say hello",
            Execute: _ =>
            {
                context.Log.Log(PluginLogLevel.Information, "Hello from a plugin!");
                return ValueTask.CompletedTask;
            },
            DefaultKeybinding: "Ctrl+Alt+H"));

        context.ConnectionTypes.Register(new EchoConnectionFactory());
    }

    public void Dispose()
    {
    }
}
