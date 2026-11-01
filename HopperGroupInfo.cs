using System;
using System.Drawing;
using Grasshopper.Kernel;

namespace HopperGroup
{
    public class HopperGroupInfo : GH_AssemblyInfo
    {
        public override string Name => "HopperGroup";

        public override Bitmap Icon
        {
            get
            {
                using (var stream = typeof(HopperGroupInfo).Assembly.GetManifestResourceStream("HopperGroup.icon.png"))
                {
                    if (stream == null) return null;
                    // Detach the returned bitmap from the resource stream's lifetime.
                    using (var bitmap = new Bitmap(stream)) return new Bitmap(bitmap);
                }
            }
        }

        public override Guid Id => new Guid("c284e418-d750-4956-a56a-02762a8cab09");

        public override string Description => "Automatic Grasshopper group membership manager";

        public override string AuthorName => "AEC Tooling";

        public override string AuthorContact => string.Empty;

        public override string AssemblyVersion => GetType().Assembly.GetName().Version.ToString();
    }
}
