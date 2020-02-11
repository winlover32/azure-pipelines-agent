namespace Microsoft.VisualStudio.Services.Agent.Listener.Diagnostics
{
    public interface IDiagnostic
    {
        bool Execute(ITerminal terminal);
    }
}
