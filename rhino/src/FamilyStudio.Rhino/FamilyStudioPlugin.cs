using System;
using Rhino;
using Rhino.PlugIns;

namespace FamilyStudio.Rhino
{
    /// <summary>
    /// Family Studio for Rhino. For now it registers one docked panel, a hello-world cover sheet
    /// drawn in the same design language as the Revit plug-in, and the FamilyStudio command.
    /// </summary>
    public sealed class FamilyStudioPlugin : PlugIn
    {
        public FamilyStudioPlugin()
        {
            Instance = this;
        }

        public static FamilyStudioPlugin? Instance { get; private set; }

        public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            try
            {
                global::Rhino.UI.Panels.RegisterPanel(this, typeof(HelloPanel), HelloPanel.Caption, PanelIcon.Create());
            }
            catch (Exception ex)
            {
                // A panel problem should never stop Rhino from loading the plug-in or its command.
                RhinoApp.WriteLine($"Family Studio could not register its panel: {ex.Message}");
            }
            return LoadReturnCode.Success;
        }
    }
}
