using CSScriptNpp.Dialogs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;
using UltraSharp.Cecil;

namespace CSScriptNpp
{
    /*TODO:
     * - Outstanding features
     *  + Watch values are not updated on "step over" after the value has been changed
     *  + QuickWatch dialog doesn't allow changing the value in the grid
     *  + Locals doesn't allow changing the value
     *  - Button to update doc. breakpoints from the .dbg file content
     *  + QuickWatch should not allow changing the name in the grid
     *  - Debugger
     *      - Debugger does not treat DateTime members as primitives
     *      - Some objects cannot be inspected:
     *          - new FileInfo(this.GetType().Assembly.Location);
     *          - Process.GetCurrentProcess();
     *      - in CS-S.Npp allow calling object inspector and redirecting the output to the debug window.
     *  - Integrate surrogate hosting //css_host /version:v4.0 /platform:x86;
     *      - Debugging
     *
     * -------------------------------------------------------------------
     *
     *  - Desirable but not essential features:
     *
     *     - Debugger attach to process
     *          - check presence of dbg info and open source file if possible
     *          - integrate with OS (http://www.codeproject.com/Articles/132742/Writing-Windows-Debugger-Part)
     *
     *     - Rendering current step indicator sometimes (very rare occasions) is not reliable (e.g. at first breakpoint hit)
     *       Very hard to reproduce. Pressing "Break" fixes it anyway
     *
     *     - Debug panel
     *          - Locals panel cached update (not recommended as it requires asynch funcevals)
     *              - clear the tree on frame change (embedded in 'locals update' message)
     *              - reconstruct the tree branch by branch
     *          - QuickWatch panel
     *             - Handle method expressions like Console.WriteLine("test"). Currently only System.Console.WriteLine("test") works
     *     - Debugger: make handling Debug.Assert user friendlier
     */

    public partial class Plugin
    {
        public const string PluginName = "CS-Script";
        public static int projectPanelId = -1;
        public static int outputPanelId = -1;
        public static int debugPanelId = -1;

        public static Dictionary<ShortcutKey, Tuple<string, Action>> internalShortcuts = new Dictionary<ShortcutKey, Tuple<string, Action>>();

        static internal void CommandMenuInit()
        {
            Environment.SetEnvironmentVariable("CSSCRIPT_CONSOLE_ENCODING_OVERWRITE", Config.Instance.CsSConsoleEncoding);

            int index = 0;

            //'_' prefix in the shortcutName means "plugin action shortcut" as opposite to "plugin key interceptor action"
            SetCommand(projectPanelId = index++, "Build (validate)", Build, "_BuildFromMenu:Ctrl+Shift+B");
            SetCommand(projectPanelId = index++, "Run", Run, "_Run:F5");
            SetCommand(projectPanelId = index++, "Debug", Debug, "_Debug:Alt+F5");
            SetCommand(projectPanelId = index++, "Debug External Process", DebugEx, "_DebugExternal:Ctrl+Shift+F5");
            SetCommand(index++, "---", null);
            SetCommand(projectPanelId = index++, "Show Project Panel", InitProjectPanel);
            SetCommand(outputPanelId = index++, "Show Output Panel", InitOutputPanel);
            SetCommand(debugPanelId = index++, "Show Debug Panel", InitDebugPanel);
            SetCommand(index++, "---", null);
            LoadIntellisenseCommands(ref index);

            SetCommand(index++, "About", ShowAbout);

            IEnumerable<Keys> keysToIntercept = BindInteranalShortcuts();

            KeyInterceptor.Instance.Install();

            foreach (var key in keysToIntercept)
                KeyInterceptor.Instance.Add(key);
            KeyInterceptor.Instance.Add(Keys.Tab);
            KeyInterceptor.Instance.KeyDown += Instance_KeyDown;

            //setup dependency injection, which may be overwritten by other plugins (e.g. NppScripts)
            Plugin.RunScript = () => Plugin.ProjectPanel.Run();
            Plugin.RunScriptAsExternal = () => Plugin.ProjectPanel.RunAsExternal();
            Plugin.DebugScript = () =>
            {
                if (ProjectPanel == null)
                    InitProjectPanel();
                Plugin.ProjectPanel.Debug(false);
            };
        }

        static public Action RunScript;
        static public Action RunScriptAsExternal;
        static public Action DebugScript;

        static public void CheckNativeAutocompletionConflict()
        {
            if (!Config.Instance.NativeAutoCompletionChecked)
            {
                //<GUIConfig name="auto-completion" autoCAction="3" triggerFromNbChar="1" funcParams="yes"/>
                //<GUIConfig name="auto-completion" autoCAction="0" triggerFromNbChar="1" funcParams="no" />
                try
                {
                    var config = XDocument.Load(CSScriptIntellisense.Npp.GetNppConfigFile())
                                                        .Root
                                                        .Descendants("GUIConfig")
                                                        .Where(x => x.Attribute("name")?.Value == "auto-completion")
                                                        .FirstOrDefault();
                    if (config != null)
                    {
                        if (config.Attribute("autoCAction")?.Value == "3" ||
                            config.Attribute("autoCAction")?.Value == "2" ||
                            config.Attribute("autoCAction")?.Value == "1" ||
                            config.Attribute("funcParams")?.Value == "yes")
                        {
                            MessageBox.Show("CS-Script has detected that Notepad++ has its auto-completion configured to be auto-triggered 'on input' (as you type).\n\n" +
                                            "This will not prevent C# Intellisense (CS-Script) from working but it may affect your user experience " +
                                            "because these two solutions may get activated at the same time.\n\n" +
                                            "It is recommended that you disable Notepad++ 'auto-completion on input' via\n" +
                                            "Settings->Preferences->Auto-Completion\n\n" +
                                            "Note: Disabling 'auto-completion on input' will not disable auto-completion for other languages but only its automatic triggering. " +
                                            "The auto-completion can be triggered at any time manually by pressing Ctrl+Space key combination.", "CS-Script");

                            try { Process.Start("https://csscriptnpp.codeplex.com/wikipage?title=Dealing%20with%20Notepad%2b%2b%20native%20auto-complete"); }
                            catch { }
                            Config.Instance.NativeAutoCompletionChecked = true;
                            Config.Instance.Save();
                        }
                    }
                }
                catch (Exception e)
                {
                    e.LogAsError();
                }
            }
        }

        //must be in a separate method to allow proper assembly probing
        static void LoadIntellisenseCommands(ref int cmdIndex)
        {
            Task.Factory.StartNew(CheckNativeAutocompletionConflict);

            CSScriptIntellisense.Plugin.CommandMenuInit(ref cmdIndex,
                 (index, name, handler, shortcut) =>
                 {
                     if (name == "Settings")
                         Plugin.SetCommand(index, name, ShowConfig, shortcut);
                     else
                         Plugin.SetCommand(index, name, handler, shortcut);
                 });
        }

        static void AddInternalShortcuts(string shortcutSpec, string displayName, Action handler, Dictionary<Keys, int> uniqueKeys)
        {
            ShortcutKey shortcut = shortcutSpec.ParseAsShortcutKey(displayName);

            internalShortcuts.Add(shortcut, new Tuple<string, Action>(displayName, handler));

            var key = (Keys)shortcut._key;
            if (!uniqueKeys.ContainsKey(key))
                uniqueKeys.Add(key, 0);
        }

        static IEnumerable<Keys> BindInteranalShortcuts()
        {
            var uniqueKeys = new Dictionary<Keys, int>();

            AddInternalShortcuts("Build:F7",
                                 "Build (validate)",
                                  Build, uniqueKeys);

            AddInternalShortcuts("LoadCurrentDocument:Ctrl+F7",
                                 "Load Current Document", () =>
                                  {
                                      InitProjectPanel();
                                      ShowProjectPanel();
                                      ProjectPanel.LoadCurrentDoc();
                                  }, uniqueKeys);

            AddInternalShortcuts("Stop:Shift+F5",
                                  "Stop running script",
                                  Stop, uniqueKeys);

            AddInternalShortcuts("_Run:F5",
                                 "Run",
                                  Run, uniqueKeys);

            AddInternalShortcuts("_Debug:Alt+F5",
                                 "Debug", () =>
                                  {
                                      if (!Debugger.IsRunning && Npp.IsCurrentScriptFile())
                                          DebugScript();
                                  }, uniqueKeys);

            AddInternalShortcuts("ToggleBreakpoint:F9",
                                 "Toggle Breakpoint",
                                 () => Debugger.ToggleBreakpoint(), uniqueKeys);

            AddInternalShortcuts("QuickWatch:Shift+F9",
                                 "Show QuickWatch...",
                                  QuickWatchPanel.PopupDialog, uniqueKeys);

            AddInternalShortcuts("StepInto:F11",
                                 "Step Into",
                                  Debugger.StepIn, uniqueKeys);

            AddInternalShortcuts("StepOut:Shift+F11",
                                 "Step Out",
                                  Debugger.StepOut, uniqueKeys);

            AddInternalShortcuts("StepOver:F10",
                                 "Step Over",
                                  StepOver, uniqueKeys);

            AddInternalShortcuts("SetNextIP:Ctrl+Shift+F10",
                                 "Set Next Statement",
                                  Debugger.SetInstructionPointer, uniqueKeys);

            AddInternalShortcuts("RunToCursor:Ctrl+F10",
                                 "Run To Cursor",
                                  Debugger.RunToCursor, uniqueKeys);

            AddInternalShortcuts("RunAsExternal:Ctrl+F5",
                                  "Run As External Process", () =>
                                  {
                                      if (Npp.IsCurrentScriptFile())
                                          RunAsExternal();
                                  }, uniqueKeys);

            AddInternalShortcuts("ShowNextFileLocationFromOutput:F4",
                                 "Next File Location in Output", () =>
                                  {
                                      OutputPanel.TryNavigateToFileReference(toNext: true);
                                  }, uniqueKeys);

            AddInternalShortcuts("ShowPrevFileLocationFromOutput:Shift+F4",
                                 "Previous File Location in Output", () =>
                                  {
                                      OutputPanel.TryNavigateToFileReference(toNext: false);
                                  }, uniqueKeys);

            return uniqueKeys.Keys;
        }

        static void Instance_KeyDown(Keys key, int repeatCount, ref bool handled)
        {
            foreach (var shortcut in internalShortcuts.Keys)
                if ((byte)key == shortcut._key && !IsDocumentHotKeyExcluded())
                {
                    Modifiers modifiers = KeyInterceptor.GetModifiers();

                    if (modifiers.IsCtrl == shortcut.IsCtrl && modifiers.IsShift == shortcut.IsShift && modifiers.IsAlt == shortcut.IsAlt)
                    {
                        handled = true;
                        var handler = internalShortcuts[shortcut];
                        handler.Item2();
                    }
                }
        }

        static bool IsDocumentHotKeyExcluded()
        {
            foreach (string extension in Config.Instance.HotkeyDocumentsExclusions.Split(';'))
                if (extension.IsNotEmpty() && Npp.IsCurrentFileHasExtension(extension))
                    return true;
            return false;
        }

        static public void ShowConfig()
        {
            using (var form = new ConfigForm(Config.Instance))
            {
                bool oldUseContextMenu = CSScriptIntellisense.Config.Instance.UseCmdContextMenu;
                form.ShowDialog();

                ReflectorExtensions.IgnoreDocumentationExceptions = CSScriptIntellisense.Config.Instance.IgnoreDocExceptions;

                if (oldUseContextMenu != CSScriptIntellisense.Config.Instance.UseCmdContextMenu)
                {
                    CSScriptIntellisense.Config.Instance.ProcessContextMenuVisibility();
                    Config.Instance.Save(); //config may be updated as the result of ProcessContextMenu...
                    MessageBox.Show("You configure the context menu.\nThe changes will take effect only after Notepad++ is restarted.", "CS-Script");
                }
            }
        }

        static public void ShowAbout()
        {
            using (var dialog = new AboutBox())
            {
                dialog.ShowDialog();
                dialog.PostCloseAction();
            }
        }

        static internal void Log(string format, params object[] args)
        {
#if DEBUG
            try
            {
                if (OutputPanel.PluginLogOutput != null)
                    OutputPanel.PluginLogOutput.WriteLine(string.Format(format, args));
            }
            catch { }
#endif
        }

        static OutputPanel outputPanel;

        static public OutputPanel OutputPanel
        {
            get
            {
                InitOutputPanel();
                return outputPanel;
            }
        }

        static public ProjectPanel ProjectPanel;

        static public CodeMapPanel CodeMapPanel
        {
            get { return ProjectPanel?.mapPanel; }
        }

        static DebugPanel debugPanel;

        static public bool DebugPanelVisible
        {
            get
            {
                return debugPanel != null && debugPanel.Visible;
            }
        }

        static public bool OutputPanelVisible
        {
            get
            {
                return outputPanel != null && outputPanel.Visible;
            }
        }

        static public DebugPanel DebugPanel
        {
            get
            {
                InitDebugPanel();
                return debugPanel;
            }
        }

        static public void InitOutputPanel()
        {
            if (Plugin.outputPanel == null)
            {
                Plugin.outputPanel = ShowDockablePanel<OutputPanel>("Output", outputPanelId, NppTbMsg.DWS_DF_CONT_BOTTOM | NppTbMsg.DWS_ICONTAB | NppTbMsg.DWS_ICONBAR);
                Application.DoEvents();
            }
        }

        static public void InitDebugPanel()
        {
            if (Plugin.debugPanel == null)
            {
                Plugin.debugPanel = ShowDockablePanel<DebugPanel>("Debug", debugPanelId, NppTbMsg.DWS_DF_CONT_RIGHT | NppTbMsg.DWS_ICONTAB | NppTbMsg.DWS_ICONBAR);
                Application.DoEvents();
            }
        }

        static public void Repaint()
        {
            if (CSScriptNpp.Plugin.ProjectPanel != null)
                CSScriptNpp.Plugin.ProjectPanel.Refresh();
            if (CSScriptNpp.Plugin.CodeMapPanel != null)
                CSScriptNpp.Plugin.CodeMapPanel.Refresh();
            if (CSScriptNpp.Plugin.debugPanel != null)
                CSScriptNpp.Plugin.debugPanel.Refresh();
            if (CSScriptNpp.Plugin.outputPanel != null)
                CSScriptNpp.Plugin.outputPanel.Refresh();
        }

        static public DebugPanel GetDebugPanel()
        {
            return Plugin.DebugPanel;
        }

        static public ProjectPanel GetProjectPanel()
        {
            if (Plugin.ProjectPanel == null)
                Plugin.InitProjectPanel();
            return Plugin.ProjectPanel;
        }

        static public void ShowSecondaryPanels()
        {
            Plugin.SetDockedPanelVisible(Plugin.OutputPanel, outputPanelId, true);
            Plugin.SetDockedPanelVisible(Plugin.DebugPanel, debugPanelId, true);
        }

        static public void EnsureOutputPanelVisible()
        {
            Plugin.SetDockedPanelVisible(Plugin.OutputPanel, outputPanelId, true);
        }

        static public void HideSecondaryPanels()
        {
            if (Plugin.outputPanel != null)
                Plugin.SetDockedPanelVisible(Plugin.outputPanel, outputPanelId, false);

            if (Plugin.debugPanel != null)
                Plugin.SetDockedPanelVisible(Plugin.debugPanel, debugPanelId, false);
        }

        static public void InitProjectPanel()
        {
            if (ProjectPanel == null)
                ProjectPanel = ShowDockablePanel<ProjectPanel>("CS-Script", projectPanelId, NppTbMsg.DWS_DF_CONT_LEFT | NppTbMsg.DWS_ICONTAB | NppTbMsg.DWS_ICONBAR);
            else
                SetDockedPanelVisible(dockedManagedPanels[projectPanelId], projectPanelId, true);
            ProjectPanel.Focus();
            Application.DoEvents();
        }

        static public void ShowProjectPanel()
        {
            SetDockedPanelVisible(dockedManagedPanels[projectPanelId], projectPanelId, true);
        }

        static public void ToggleScondaryPanels()
        {
            bool simple = false;
            if (simple)
            {
                Plugin.ShowSecondaryPanels();
            }
            else
            {
                if (!OutputPanelVisible && !DebugPanelVisible)
                {
                    InitOutputPanel();
                    SetDockedPanelVisible(Plugin.OutputPanel, outputPanelId, true);
                }
                else if (OutputPanelVisible && !DebugPanelVisible)
                {
                    InitDebugPanel();
                    SetDockedPanelVisible(Plugin.DebugPanel, debugPanelId, true);
                }
                else if (OutputPanelVisible && DebugPanelVisible)
                {
                    SetDockedPanelVisible(Plugin.OutputPanel, outputPanelId, false);
                    SetDockedPanelVisible(Plugin.DebugPanel, debugPanelId, false);
                }
                else
                {
                    //Config.Instance.ShowOutputPanel
                    InitOutputPanel();
                    SetDockedPanelVisible(Plugin.OutputPanel, outputPanelId, true);
                    InitDebugPanel();
                    SetDockedPanelVisible(Plugin.DebugPanel, debugPanelId, true);
                }
            }
        }

        static public void Build()
        {
            if (runningScript == null)
            {
                if (Plugin.ProjectPanel == null)
                    InitProjectPanel();
                Plugin.ProjectPanel.Build();
            }
        }

        static public void StepOver()
        {
            if (Debugger.IsRunning)
                Debugger.StepOver();
            else
                GetProjectPanel().Debug(breakOnFirstStep: true);
        }

        static public void Run()
        {
            if (Debugger.IsRunning)
            {
                Debugger.Go();
            }
            else if (Npp.IsCurrentScriptFile() && runningScript == null)
            {
                if (Plugin.ProjectPanel == null)
                    InitProjectPanel();
                Plugin.RunScript();
            }
        }

        static public void DebugEx()
        {
            if (!Debugger.IsRunning)
            {
                if (Plugin.ProjectPanel == null)
                    InitProjectPanel();
                DebugExternal.ShowModal();
            }
        }

        static public void Debug()
        {
            if (!Debugger.IsRunning)
            {
                if (Plugin.ProjectPanel == null)
                    InitProjectPanel();
                Plugin.DebugScript();
            }
        }

        static public void Stop()
        {
            if (Debugger.IsRunning)
            {
                Debugger.Exit();
            }
            else
            {
                try
                {
                    if (Plugin.RunningScript != null)
                        Plugin.RunningScript.Kill();
                }
                catch (Exception ex)
                {
                    Plugin.OutputPanel.DebugOutput.WriteLine(null)
                                                  .WriteLine(ex.Message);
                }
            }
        }

        static public void RunAsExternal()
        {
            if (runningScript == null)
            {
                if (Plugin.ProjectPanel == null)
                    InitProjectPanel();
                Plugin.RunScriptAsExternal();
            }
        }

        static public OutputPanel ShowOutputPanel()
        {
            InitOutputPanel();
            SetDockedPanelVisible(Plugin.OutputPanel, outputPanelId, true);

            UpdateLocalDebugInfo();
            return Plugin.OutputPanel;
        }

        static Process runningScript;

        public static Process RunningScript
        {
            get
            {
                return runningScript;
            }
            set
            {
                runningScript = value;
                UpdateLocalDebugInfo();
            }
        }

        static void UpdateLocalDebugInfo()
        {
            if (runningScript == null)
                Plugin.OutputPanel.localDebugPrefix = null;
            else
                Plugin.OutputPanel.localDebugPrefix = runningScript.Id.ToString() + ": ";
        }

        internal static void StopVBCSCompilers()
        {
            try
            {
                if (Config.Instance.UseRoslynProvider)
                    foreach (var p in Process.GetProcessesByName("VBCSCompiler"))
                        try { p.Kill(); }
                        catch { } //cannot analyse main module as it may not be accessible for x86 vs. x64 reasons
            }
            catch { }
        }

        static internal void OnNppReady()
        {
            //System.Diagnostics.Debug.Assert(false);
            if (Config.Instance.RestorePanelsAtStartup)
            {
                if (Config.Instance.ShowProjectPanel)
                    InitProjectPanel();

                if (Config.Instance.ShowOutputPanel)
                    InitOutputPanel();

                if (Config.Instance.ShowDebugPanel)
                    InitDebugPanel();
            }

            StartCheckForUpdates();

            OpenAutomationChannel();

            //if (Config.Instance.UseRoslynProvider && Config.Instance.StartRoslynServerAtStartup)
            //{
            //    //unfortunately InitRoslyn is ineffective
            //    Task.Factory.StartNew(CSScriptHelper.InitRoslyn);
            //}
        }

        static internal void OnDocumentSaved()
        {
            if (Plugin.ProjectPanel != null)
                Plugin.ProjectPanel.RefreshProjectStructure();
        }

        static internal void CleanUp()
        {
            Config.Instance.ShowProjectPanel = (dockedManagedPanels.ContainsKey(projectPanelId) && dockedManagedPanels[projectPanelId].Visible);
            Config.Instance.ShowOutputPanel = (dockedManagedPanels.ContainsKey(outputPanelId) && dockedManagedPanels[outputPanelId].Visible);
            Config.Instance.ShowDebugPanel = (dockedManagedPanels.ContainsKey(debugPanelId) && dockedManagedPanels[debugPanelId].Visible);
            Config.Instance.Save();
            OutputPanel.Clean();
            CloseAutomationChannel();
        }

        internal static string HomeUrl = "https://csscriptnpp.codeplex.com/";

        static void StartCheckForUpdates()
        {
            lock (typeof(Plugin))
            {
                if (Config.Instance.CheckUpdatesOnStartup)
                {
                    string date = DateTime.Now.ToString("yyyy-MM-dd");
                    if (Config.Instance.LastUpdatesCheckDate != date)
                    {
                        Config.Instance.LastUpdatesCheckDate = date;
                        Config.Instance.Save();

                        Task.Factory.StartNew(CheckForUpdates);
                    }
                }
            }
        }

        static void CheckForUpdates()
        {
            Thread.Sleep(2000); //let Notepad++ to complete all initialization

            string version = CSScriptHelper.GetLatestAvailableVersion();

            if (version != null && version != Config.Instance.SkipUpdateVersion)
            {
                var latestVersion = new Version(version);
                var nppVersion = Assembly.GetExecutingAssembly().GetName().Version;

                if (nppVersion < latestVersion)
                {
                    using (var dialog = new UpdateOptionsPanel(version))
                        dialog.ShowDialog();
                }
            }
        }

        public static void OnNotification(SCNotification data)
        {
        }

        static public void OnCurrentFileChanged()
        {
            if (CodeMapPanel != null)
                CodeMapPanel.RefreshContent();

            if (Npp.IsCurrentScriptFile() && Config.Instance.UseRoslynProvider && Config.Instance.StartRoslynServerAtNppStartup)
            {
                CSScriptHelper.InitRoslyn();
            }
        }

        public static void OnToolbarUpdate()
        {
            Plugin.FuncItems.RefreshItems();
            SetToolbarImage(Resources.Resources.css_logo_16x16_tb, projectPanelId);
        }

        public static void OnFileSavedAs(string oldName, string newName)
        {
            if (ProjectPanel.currentScript != null && ProjectPanel.currentScript == oldName) //script is loaded and renamed
                ProjectPanel.LoadCurrentDoc();
        }

        static void CloseAutomationChannel()
        {
            MessageQueue.AddAutomationCommand("automation.exit");
        }

        static void OpenAutomationChannel()
        {
            Task.Factory.StartNew(() =>
                {
                    try
                    {
                        while (true)
                        {
                            string message = MessageQueue.WaitForAutomationCommand();
                            if (message == "automation.exit")
                                break;

                            MessageBox.Show(message);
                        }
                    }
                    catch { };
                });
        }

        static public void ProcessCommandArgs(string args)
        {
            //System.Diagnostics.Debug.Assert(false);
            if (args.StartsWith("/css.attach:")) //attach to external process
            {
                try
                {
                    var id = int.Parse(args.Substring("/css.attach:".Length));
                    DebugExternal.AttachTo(id);
                }
                catch { }
            }
        }
    }
}