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

        public HopperGroupComponent()
            : base(
                "Hopper Group",
                "HopperGroup",
                "Automatically adds dragged Grasshopper objects to the group region they occupy.",
                "Params",
                "Util")
        {
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddBooleanParameter("Enabled", "Enabled", "Enable automatic group membership updates.", GH_ParamAccess.item, true);
            pManager.AddNumberParameter("Exit Scale", "Exit", "Selection-size multiplier outside a group before an object is removed from it.", GH_ParamAccess.item, _exitScale);
            pManager.AddBooleanParameter("Refresh", "Refresh", "Toggle to rescan all groups and repair memberships for all objects.", GH_ParamAccess.item, false);
            pManager.AddBooleanParameter("Debug", "Debug", "Enable debug logging.", GH_ParamAccess.item, false);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Status", "Status", "Current HopperGroup status.", GH_ParamAccess.item);
            pManager.AddIntegerParameter("Groups", "Groups", "Number of cached group regions.", GH_ParamAccess.item);
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

            _manager.Configure(this, doc, _enabled, (float)_exitScale, _debug);

            if (refresh && !_lastRefresh)
            {
                _manager.RefreshAllObjects();
            }

            _lastRefresh = refresh;

            DA.SetData(0, _manager.Status);
            DA.SetData(1, _manager.GroupCount);
            DA.SetData(2, _manager.LastChangeCount);
            DA.SetData(3, _manager.DebugLog);
        }

        public override void AddedToDocument(GH_Document document)
        {
            base.AddedToDocument(document);
            _manager.Configure(this, document, _enabled, (float)_exitScale, _debug);
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            _manager.Dispose();
            base.RemovedFromDocument(document);
        }

        internal void ScheduleOutputRefresh()
        {
            try
            {
                ExpireSolution(true);
            }
            catch
            {
                // Grasshopper may be tearing down the canvas or document.
            }
        }

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
