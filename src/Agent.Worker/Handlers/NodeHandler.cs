// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Agent.Sdk;
using Agent.Sdk.Knob;
using Microsoft.VisualStudio.Services.Agent.Util;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;
using System.Linq;

namespace Microsoft.VisualStudio.Services.Agent.Worker.Handlers
{
    [ServiceLocator(Default = typeof(NodeHandler))]
    public interface INodeHandler : IHandler
    {
        // Data can be of these four types: NodeHandlerData, Node10HandlerData, Node16HandlerData and Node20_1HandlerData
        BaseNodeHandlerData Data { get; set; }
    }

    [ServiceLocator(Default = typeof(NodeHandlerHelper))]
    public interface INodeHandlerHelper
    {
        string[] GetFilteredPossibleNodeFolders(string nodeFolderName, string[] possibleNodeFolders);
        string GetNodeFolderPath(string nodeFolderName, IHostContext hostContext);
        bool IsNodeFolderExist(string nodeFolderName, IHostContext hostContext);
    }

    public class NodeHandlerHelper : INodeHandlerHelper
    {
        public bool IsNodeFolderExist(string nodeFolderName, IHostContext hostContext) => File.Exists(GetNodeFolderPath(nodeFolderName, hostContext));

        public string GetNodeFolderPath(string nodeFolderName, IHostContext hostContext) => Path.Combine(
            hostContext.GetDirectory(WellKnownDirectory.Externals),
            nodeFolderName,
            "bin",
            $"node{IOUtil.ExeExtension}");

        public string[] GetFilteredPossibleNodeFolders(string nodeFolderName, string[] possibleNodeFolders)
        {
            int nodeFolderIndex = Array.IndexOf(possibleNodeFolders, nodeFolderName);

            return nodeFolderIndex >= 0 ?
                possibleNodeFolders.Skip(nodeFolderIndex + 1).ToArray()
                : Array.Empty<string>();
        }
    }

    public sealed class NodeHandler : Handler, INodeHandler
    {
        private readonly INodeHandlerHelper nodeHandlerHelper;
        private const string nodeFolder = "node";
        private const string node10Folder = "node10";
        internal static readonly string Node16Folder = "node16";
        internal static readonly string Node20_1Folder = "node20_1";
        private static readonly string nodeLTS = Node16Folder;
        private const string useNodeKnobLtsKey = "LTS";
        private const string useNodeKnobUpgradeKey = "UPGRADE";
        private string[] possibleNodeFolders = { nodeFolder, node10Folder, Node16Folder, Node20_1Folder };

        public NodeHandler()
        {
            this.nodeHandlerHelper = new NodeHandlerHelper();
        }

        public NodeHandler(INodeHandlerHelper nodeHandlerHelper)
        {
            this.nodeHandlerHelper = nodeHandlerHelper;
        }

        public BaseNodeHandlerData Data { get; set; }

        public async Task RunAsync()
        {
            // Validate args.
            Trace.Entering();
            ArgUtil.NotNull(Data, nameof(Data));
            ArgUtil.NotNull(ExecutionContext, nameof(ExecutionContext));
            ArgUtil.NotNull(Inputs, nameof(Inputs));
            ArgUtil.Directory(TaskDirectory, nameof(TaskDirectory));

            // Update the env dictionary.
            AddInputsToEnvironment();
            AddEndpointsToEnvironment();
            AddSecureFilesToEnvironment();
            AddVariablesToEnvironment();
            AddTaskVariablesToEnvironment();
            AddPrependPathToEnvironment();

            // Resolve the target script.
            string target = Data.Target;
            ArgUtil.NotNullOrEmpty(target, nameof(target));
            target = Path.Combine(TaskDirectory, target);
            ArgUtil.File(target, nameof(target));

            // Resolve the working directory.
            string workingDirectory = Data.WorkingDirectory;
            if (string.IsNullOrEmpty(workingDirectory))
            {
                workingDirectory = ExecutionContext.Variables.Get(Constants.Variables.System.DefaultWorkingDirectory);
                if (string.IsNullOrEmpty(workingDirectory))
                {
                    workingDirectory = HostContext.GetDirectory(WellKnownDirectory.Work);
                }
            }

            StepHost.OutputDataReceived += OnDataReceived;
            StepHost.ErrorDataReceived += OnDataReceived;

            string file;
            if (!string.IsNullOrEmpty(ExecutionContext.StepTarget()?.CustomNodePath))
            {
                file = ExecutionContext.StepTarget().CustomNodePath;
            }
            else
            {
                file = GetNodeLocation();

                ExecutionContext.Debug("Using node path: " + file);
            }

            // Format the arguments passed to node.
            // 1) Wrap the script file path in double quotes.
            // 2) Escape double quotes within the script file path. Double-quote is a valid
            // file name character on Linux.
            string arguments = StepHost.ResolvePathForStepHost(StringUtil.Format(@"""{0}""", target.Replace(@"""", @"\""")));
            // Let .NET choose the default, except on Windows.
            Encoding outputEncoding = null;
            if (PlatformUtil.RunningOnWindows)
            {
                // It appears that node.exe outputs UTF8 when not in TTY mode.
                outputEncoding = Encoding.UTF8;
            }

            try
            {
                // Execute the process. Exit code 0 should always be returned.
                // A non-zero exit code indicates infrastructural failure.
                // Task failure should be communicated over STDOUT using ## commands.
                Task step = StepHost.ExecuteAsync(workingDirectory: StepHost.ResolvePathForStepHost(workingDirectory),
                                                  fileName: StepHost.ResolvePathForStepHost(file),
                                                  arguments: arguments,
                                                  environment: Environment,
                                                  requireExitCodeZero: true,
                                                  outputEncoding: outputEncoding,
                                                  killProcessOnCancel: false,
                                                  inheritConsoleHandler: !ExecutionContext.Variables.Retain_Default_Encoding,
                                                  continueAfterCancelProcessTreeKillAttempt: _continueAfterCancelProcessTreeKillAttempt,
                                                  cancellationToken: ExecutionContext.CancellationToken);

                // Wait for either the node exit or force finish through ##vso command
                await System.Threading.Tasks.Task.WhenAny(step, ExecutionContext.ForceCompleted);

                if (ExecutionContext.ForceCompleted.IsCompleted)
                {
                    ExecutionContext.Debug("The task was marked as \"done\", but the process has not closed after 5 seconds. Treating the task as complete.");
                }
                else
                {
                    await step;
                }
            }
            finally
            {
                StepHost.OutputDataReceived -= OnDataReceived;
                StepHost.ErrorDataReceived -= OnDataReceived;
            }
        }

        public string GetNodeLocation()
        {
            bool useNode10 = AgentKnobs.UseNode10.GetValue(ExecutionContext).AsBoolean();
            bool useNode16 = AgentKnobs.UseNode16.GetValue(ExecutionContext).AsBoolean();
            bool useNode20_1 = AgentKnobs.UseNode20_1.GetValue(ExecutionContext).AsBoolean();
            bool UseNode20InUnsupportedSystem = AgentKnobs.UseNode20InUnsupportedSystem.GetValue(ExecutionContext).AsBoolean();
            bool taskHasNode10Data = Data is Node10HandlerData;
            bool taskHasNode16Data = Data is Node16HandlerData;
            bool taskHasNode20_1Data = Data is Node20_1HandlerData;
            string useNodeKnob = AgentKnobs.UseNode.GetValue(ExecutionContext).AsString();

            string nodeFolder = NodeHandler.nodeFolder;

            if (taskHasNode20_1Data && !IsNode20SupportedSystems() && !UseNode20InUnsupportedSystem)
            {
                ExecutionContext.Warning($"The operating system the agent is running on doesn't support Node20. Using node16 runner instead. " +
                             "Please upgrade the operating system of this host to ensure compatibility with Node20 tasks: " +
                             "https://github.com/nodesource/distributions");
                Trace.Info($"Task.json has node20_1 handler data: {taskHasNode20_1Data}, but it's running in a unsupported system version. Using node16 for node tasks.");
                nodeFolder = NodeHandler.Node16Folder;
            }
            else if (taskHasNode20_1Data)
            {
                Trace.Info($"Task.json has node20_1 handler data: {taskHasNode20_1Data}");
                nodeFolder = NodeHandler.Node20_1Folder;
            }
            else if (taskHasNode16Data)
            {
                Trace.Info($"Task.json has node16 handler data: {taskHasNode16Data}");
                nodeFolder = NodeHandler.Node16Folder;
            }
            else if (taskHasNode10Data)
            {
                Trace.Info($"Task.json has node10 handler data: {taskHasNode10Data}");
                nodeFolder = NodeHandler.node10Folder;
            }
            else if (PlatformUtil.RunningOnAlpine)
            {
                Trace.Info($"Detected Alpine, using node10 instead of node (6)");
                nodeFolder = NodeHandler.node10Folder;
            }

            if (useNode20_1 && !IsNode20SupportedSystems() && !UseNode20InUnsupportedSystem) {
                ExecutionContext.Warning($"The operating system the agent is running on doesn't support Node20. Using node16 runner instead. " +
                             "Please upgrade the operating system of this host to ensure compatibility with Node20 tasks: " +
                             "https://github.com/nodesource/distributions");
                Trace.Info($"Found UseNode20_1 knob, but it's running in a unsupported system version. Using node16 for node tasks.");
                nodeFolder = NodeHandler.Node16Folder;
            } 
            else if (useNode20_1) {
                Trace.Info($"Found UseNode20_1 knob, using node20_1 for node tasks {useNode20_1}");
                nodeFolder = NodeHandler.Node20_1Folder;
            }

            if (useNode16)
            {
                Trace.Info($"Found UseNode16 knob, using node16 for node tasks {useNode16}");
                nodeFolder = NodeHandler.Node16Folder;
            }

            if (useNode10)
            {
                Trace.Info($"Found UseNode10 knob, use node10 for node tasks: {useNode10}");
                nodeFolder = NodeHandler.node10Folder;
            }
            if (nodeFolder == NodeHandler.nodeFolder && 
                AgentKnobs.AgentDeprecatedNodeWarnings.GetValue(ExecutionContext).AsBoolean() == true)
            {
                ExecutionContext.Warning(StringUtil.Loc("DeprecatedRunner", Task.Name.ToString()));
            }

            if (!nodeHandlerHelper.IsNodeFolderExist(nodeFolder, HostContext))
            {
                string[] filteredPossibleNodeFolders = nodeHandlerHelper.GetFilteredPossibleNodeFolders(nodeFolder, possibleNodeFolders);

                if (!String.IsNullOrWhiteSpace(useNodeKnob) && filteredPossibleNodeFolders.Length > 0)
                {
                    Trace.Info($"Found UseNode knob with value \"{useNodeKnob}\", will try to find appropriate Node Runner");

                    switch (useNodeKnob.ToUpper())
                    {
                        case NodeHandler.useNodeKnobLtsKey:
                            if (nodeHandlerHelper.IsNodeFolderExist(NodeHandler.nodeLTS, HostContext))
                            {
                                ExecutionContext.Warning($"Configured runner {nodeFolder} is not available, latest LTS version {NodeHandler.nodeLTS} will be used. See http://aka.ms/azdo-node-runner");
                                Trace.Info($"Found LTS version of node installed");
                                return nodeHandlerHelper.GetNodeFolderPath(NodeHandler.nodeLTS, HostContext);
                            }
                            break;
                        case NodeHandler.useNodeKnobUpgradeKey:
                            string firstExistedNodeFolder = filteredPossibleNodeFolders.FirstOrDefault(nf => nodeHandlerHelper.IsNodeFolderExist(nf, HostContext));

                            if (firstExistedNodeFolder != null)
                            {
                                ExecutionContext.Warning($"Configured runner {nodeFolder} is not available, next available version will be used. See http://aka.ms/azdo-node-runner");
                                Trace.Info($"Found {firstExistedNodeFolder} installed");
                                return nodeHandlerHelper.GetNodeFolderPath(firstExistedNodeFolder, HostContext);
                            }
                            break;
                        default:
                            Trace.Error($"Value of UseNode knob cannot be recognized");
                            break;
                    }
                }

                throw new FileNotFoundException(StringUtil.Loc("MissingNodePath", nodeHandlerHelper.GetNodeFolderPath(nodeFolder, HostContext)));
            }

            return nodeHandlerHelper.GetNodeFolderPath(nodeFolder, HostContext);
        }

        private void OnDataReceived(object sender, ProcessDataReceivedEventArgs e)
        {
            // drop any outputs after the task get force completed.
            if (ExecutionContext.ForceCompleted.IsCompleted)
            {
                return;
            }

            // This does not need to be inside of a critical section.
            // The logging queues and command handlers are thread-safe.
            if (!CommandManager.TryProcessCommand(ExecutionContext, e.Data))
            {
                ExecutionContext.Output(e.Data);
            }
        }

        private bool IsNode20SupportedSystems() {
            var systemName = PlatformUtil.GetSystemId() ?? "";
            var systemVersion = PlatformUtil.GetSystemVersion()?.Name?.ToString() ?? "";
            if (systemName.Equals("ubuntu") &&
                int.TryParse(systemVersion, out int ubuntuVersion) &&
                ubuntuVersion <= 18.04) {
                Trace.Info($"Detected Ubuntu version: " + ubuntuVersion);
                return false;
            }
            if (systemName.Equals("debian") &&
                int.TryParse(systemVersion, out int debianVersion) &&
                debianVersion <= 9) {
                Trace.Info($"Detected Debian version: " + debianVersion);
                return false;
            } 
            if (PlatformUtil.RunningOnRHEL6) {
                Trace.Info($"Detected RedHat 6");
                return false;
            }
            if (PlatformUtil.RunningOnRHEL7) {
                Trace.Info($"Detected RedHat 7");
                return false;
            }
            return true;
        }
    }
}
