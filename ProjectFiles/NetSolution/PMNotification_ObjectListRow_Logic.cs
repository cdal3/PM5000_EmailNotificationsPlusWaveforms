#region Using directives
using System.Linq;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using UAManagedCore;
#endregion

public class PMNotification_ObjectListRow_Logic : BaseNetLogic
{
    public override void Start()
    {
        // Insert code to be executed when the user-defined logic is started
    }

    public override void Stop()
    {
        // Insert code to be executed when the user-defined logic is stopped
    }
    [ExportMethod]
    public void rowClicked()
    {

        //Clear selections

        var mainWindow = InformationModel.Get(Owner.GetVariable("mainWindow").Value);
        foreach(var row in mainWindow.Get("body/vertical").GetNodesByType<PMNotification_ObjectListRow>().ToList())
        {
            row.GetVariable("isSelected").Value = false;
        }

        mainWindow.GetVariable("selectedObject").Value = Owner.GetVariable("objectPointer").Value;
        Owner.GetVariable("isSelected").Value = true;

    }
}
