using Rhino;
using Rhino.Commands;

namespace FamilyStudio.Rhino
{
    /// <summary>FamilyStudio: shows or hides the Family Studio panel.</summary>
    public sealed class FamilyStudioCommand : Command
    {
        public override string EnglishName => "FamilyStudio";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var id = HelloPanel.PanelId;
            if (global::Rhino.UI.Panels.IsPanelVisible(id))
                global::Rhino.UI.Panels.ClosePanel(id);
            else
                global::Rhino.UI.Panels.OpenPanel(id);
            return Result.Success;
        }
    }
}
