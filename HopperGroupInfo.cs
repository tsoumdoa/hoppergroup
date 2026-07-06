using System.Drawing;
using Grasshopper.Kernel;

namespace HopperGroup
{
    public class HopperGroupInfo : GH_AssemblyInfo
    {
        public override string Name => "HopperGroup";

        public override Bitmap Icon => null;

        public override string Description => "Automatic Grasshopper group membership manager";

        public override string AuthorName => "AEC Tooling";

        public override string AuthorContact => string.Empty;

        public override string AssemblyVersion => GetType().Assembly.GetName().Version.ToString();
    }
}
