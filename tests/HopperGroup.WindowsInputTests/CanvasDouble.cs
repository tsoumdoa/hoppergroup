using System;
using System.ComponentModel;
using System.Windows.Forms;
using Grasshopper.Kernel;

namespace Grasshopper.GUI.Canvas
{
    // Real Control/WndProc/KeyPreview/message queue; only GH document state is fake.
    public class GH_Canvas : Control
    {
        public static Keys NavigationPanLeft, NavigationPanRight, NavigationPanUp,
            NavigationPanDown, NavigationZoomIn, NavigationZoomOut;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool ModifiersEnabled { get; set; } = true;
        public bool IsActiveInteraction, IsActiveWidget, IsActiveObject;
        public GH_Document Document;
        public event EventHandler<GH_CanvasDocumentChangedEventArgs> DocumentChanged;
        public void ChangeDocument(GH_Document document)
        {
            var previous = Document;
            Document = document;
            DocumentChanged?.Invoke(this, new GH_CanvasDocumentChangedEventArgs { OldDocument = previous, NewDocument = document });
        }
        public void Recreate() => RecreateHandle();
        public void Wheel() => OnMouseWheel(new MouseEventArgs(MouseButtons.None, 0, 0, 0, 120));
    }
}
