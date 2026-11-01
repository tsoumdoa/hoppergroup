using System;
using System.Drawing;
using System.Reflection;
using Grasshopper.Kernel;

namespace HopperGroup
{
    public class HopperGroupComponent : GH_Component
    {
        private readonly GroupMembershipManager _manager = new GroupMembershipManager();
        private bool _enabled = true;
        private bool _debug;
        private bool _lastRefresh;
        private double _exitScale = 1.0;
        private GH_Document _sessionDocument;
        private bool _contextUnloaded;
        private bool _active;
        private bool _inactive;
        private bool _refreshingOutputs;
        private bool _outputRefreshPending;
        private int _outputRefreshVersion;
        private Tuple<string, int, int, string> _lastOutputValues;

        internal bool SessionEligible => _enabled && !Locked && !_contextUnloaded;
        internal GroupMembershipManager Manager => _manager;

        public HopperGroupComponent()
            : base(
                "Hopper Group",
                "HopperGroup",
                "Automatically adds dragged Grasshopper objects to the group region they occupy. Select objects and tap G twice, then click a destination group to add them manually. Esc cancels.",
                "Params",
                "Util")
        {
            ObjectChanged += OnComponentChanged;
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddBooleanParameter("Enabled", "Enabled", "Enable automatic group membership updates.", GH_ParamAccess.item, true);
            pManager.AddNumberParameter("Exit Scale", "Exit", "Scales the exit buffer: 1.5 times object size, clamped to 40–120 canvas units before scaling. Zero means no buffer.", GH_ParamAccess.item, _exitScale);
            pManager.AddBooleanParameter("Refresh", "Refresh", "Re-apply membership from the layout, overwriting manual membership edits. Skips [lock] groups. Ctrl+Z undoes.", GH_ParamAccess.item, false);
            pManager.AddBooleanParameter("Debug", "Debug", "Enable debug logging.", GH_ParamAccess.item, false);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Status", "Status", "Current HopperGroup status.", GH_ParamAccess.item);
            pManager.AddIntegerParameter("Groups", "Groups", "Number of groups being tracked", GH_ParamAccess.item);
            pManager.AddIntegerParameter("Changes", "Changes", "Number of membership changes from the last operation.", GH_ParamAccess.item);
            pManager.AddTextParameter("Log", "Log", "Debug log messages.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var enabled = _enabled;
            var exitScale = _exitScale;
            var refresh = false;
            var debug = _debug;

            DA.GetData(0, ref enabled);
            DA.GetData(1, ref exitScale);
            DA.GetData(2, ref refresh);
            DA.GetData(3, ref debug);

            _enabled = enabled;
            _exitScale = Math.Max(0.0, exitScale);
            _debug = debug;

            var doc = OnPingDocument();
            if (doc == null)
            {
                DA.SetData(0, "No document");
                DA.SetData(1, 0);
                DA.SetData(2, 0);
                DA.SetData(3, string.Empty);
                return;
            }

            ConfigureSession(doc);

            if (_active && refresh && !_lastRefresh)
            {
                _manager.RefreshAllObjects();
            }

            _lastRefresh = refresh;

            if (_inactive)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Inactive: another HopperGroup component controls this document");
            DA.SetData(0, OutputStatus);
            DA.SetData(1, _active ? _manager.GroupCount : 0);
            DA.SetData(2, _active ? _manager.LastChangeCount : 0);
            DA.SetData(3, _manager.DebugLog);
            _lastOutputValues = CaptureOutputValues();
        }

        public override void AddedToDocument(GH_Document document)
        {
            base.AddedToDocument(document);
            _manager.Guard("AddedToDocument", () =>
            {
                _contextUnloaded = false;
                ConfigureSession(document);
            });
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            _manager.Guard("RemovedFromDocument", () =>
            {
                if (_sessionDocument != null) DocumentSessionRegistry.Remove(this, _sessionDocument);
                ReleaseSession();
            });
            base.RemovedFromDocument(document);
        }

        public override void DocumentContextChanged(GH_Document document, GH_DocumentContext context)
        {
            base.DocumentContextChanged(document, context);
            _manager.Guard("DocumentContextChanged", () =>
            {
                if (context == GH_DocumentContext.Unloaded || context == GH_DocumentContext.Close)
                {
                    _contextUnloaded = true;
                    DocumentSessionRegistry.Close(document);
                    ReleaseSession();
                }
                else if (context == GH_DocumentContext.Loaded)
                {
                    _contextUnloaded = false;
                    ConfigureSession(document);
                }
            });
        }

        private void OnComponentChanged(IGH_DocumentObject sender, GH_ObjectChangedEventArgs e) =>
            _manager.Guard("Component.ObjectChanged", () =>
            {
                if (e.Type == GH_ObjectEventType.Enabled && _sessionDocument != null)
                    ConfigureSession(_sessionDocument);
            });

        private void ConfigureSession(GH_Document document)
        {
            if (_contextUnloaded) return;
            if (_sessionDocument != null && !ReferenceEquals(_sessionDocument, document))
                DocumentSessionRegistry.Remove(this, _sessionDocument);
            _contextUnloaded = false;
            _sessionDocument = document;
            DocumentSessionRegistry.Configure(this, document);
        }

        internal void ApplySession(GH_Document document, bool active, bool anotherOwner)
        {
            var inactive = SessionEligible && !active && anotherOwner;
            var changed = _active != active || _inactive != inactive;
            _active = active;
            _inactive = inactive;
            if (active) _manager.Configure(this, document, true, (float)_exitScale, _debug);
            else _manager.Dispose();
            if (changed)
            {
                ClearRuntimeMessages();
                if (inactive)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Inactive: another HopperGroup component controls this document");
                ScheduleOutputRefresh();
            }
        }

        internal void ReleaseSession()
        {
            _outputRefreshVersion++;
            _outputRefreshPending = false;
            _lastOutputValues = null;
            _sessionDocument = null;
            _contextUnloaded = true;
            _active = false;
            _inactive = false;
            _manager.Dispose();
            ClearRuntimeMessages();
        }

        internal void UpdateShortcutPrompt()
        {
            var prompt = _manager.ShortcutPrompt;
            if (Message == prompt) return;
            Message = prompt;
            Grasshopper.Instances.InvalidateCanvas();
        }

        internal void ScheduleOutputRefresh()
        {
            try
            {
                UpdateShortcutPrompt();
                var document = OnPingDocument();
                if (document == null || Locked || _contextUnloaded || _refreshingOutputs) return;
                if (_outputRefreshPending || Equals(_lastOutputValues, CaptureOutputValues())) return;
                _outputRefreshPending = true;
                var version = _outputRefreshVersion;
                document.ScheduleSolution(1, scheduledDocument =>
                {
                    if (version != _outputRefreshVersion) return;
                    _refreshingOutputs = true;
                    try
                    {
                        _manager.Guard("Scheduled output refresh", () =>
                        {
                            if (!Locked && !_contextUnloaded && ReferenceEquals(scheduledDocument, OnPingDocument())
                                && !Equals(_lastOutputValues, CaptureOutputValues()))
                            {
                                ExpireSolution(false);
                                _lastOutputValues = CaptureOutputValues();
                            }
                        });
                    }
                    finally { _refreshingOutputs = false; _outputRefreshPending = false; }
                });
            }
            catch (Exception error)
            {
                _outputRefreshPending = false;
                _manager.ReportError("ScheduleOutputRefresh", error);
            }
        }

        private string OutputStatus => _inactive ? "Inactive (another HopperGroup is active)"
            : _active ? _manager.Status : "Off";

        private Tuple<string, int, int, string> CaptureOutputValues() => Tuple.Create(OutputStatus,
            _active ? _manager.GroupCount : 0, _active ? _manager.LastChangeCount : 0, _manager.DebugLog);

        protected override Bitmap Icon
        {
            get
            {
                var assembly = Assembly.GetExecutingAssembly();
                foreach (var resourceName in assembly.GetManifestResourceNames())
                {
                    if (!resourceName.EndsWith("icon.png", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    using (var stream = assembly.GetManifestResourceStream(resourceName))
                    {
                        return stream == null ? null : new Bitmap(stream);
                    }
                }

                return null;
            }
        }

        public override Guid ComponentGuid => new Guid("2D21CCAF-7D8F-4CC7-8B84-679016530E17");
    }
}
