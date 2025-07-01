using System.Diagnostics;
using Microsoft;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.DebuggerVisualizers;
using Microsoft.VisualStudio.Extensibility.Shell;
using Microsoft.VisualStudio.Extensibility.VSSdkCompatibility;
using Microsoft.VisualStudio.RpcContracts.RemoteUI;
using Microsoft.VisualStudio.Shell;

namespace MarkMpn.ScriptDom.DebugVisualizer.DebuggerSide
{
    /// <summary>
    /// Debugger visualizer provider for <see cref="IRootExecutionPlanNode"/>.
    /// </summary>
    [VisualStudioContribution]
    internal class ExecutionPlanDebuggerVisualizerProvider : DebuggerVisualizerProvider
    {
        private const string DisplayName = "MarkMpn.ScriptDom.DebugVisualizer.DisplayName";

        public override DebuggerVisualizerProviderConfiguration DebuggerVisualizerProviderConfiguration => new(
            new VisualizerTargetType($"%{DisplayName}%", typeof(SelectStatement)))
        {
            VisualizerObjectSourceType = new("MarkMpn.ScriptDom.DebugVisualizer.DebugeeSide.ScriptDomObjectSource, MarkMpn.ScriptDom.DebugVisualizer.DebugeeSide")
        };

        public override async Task<IRemoteUserControl> CreateVisualizerAsync(VisualizerTarget visualizerTarget, CancellationToken cancellationToken)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var wrapper = new WpfControlWrapper(new ScriptDomUserControl(visualizerTarget));
            return wrapper;
        }
    }
}
