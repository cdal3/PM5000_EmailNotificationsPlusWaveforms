#region Using directives
using System.Linq;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using UAManagedCore;
#endregion

public class PMNotification_ObjectSelector_Logic : BaseNetLogic
{
    public override void Start()
    {
        refreshList();
        // Insert code to be executed when the user-defined logic is started
    }

    public override void Stop()
    {
        // Insert code to be executed when the user-defined logic is stopped
    }
    [ExportMethod]
    public void refreshList()
    {
        var bodyContainer = Owner.Get("body/vertical");
        var firstRow = false;
        Owner.GetVariable("selectedObject").Value = NodeId.Empty;

        bodyContainer.Children.ToList().ForEach(bodyContainer.Remove);

        foreach(IUAObject pmObject in InformationModel.Get(Owner.GetVariable("objectsLocation").Value).Children)
        {
            PMNotification_ObjectListRow newRow = InformationModel.Make<PMNotification_ObjectListRow>(pmObject.BrowseName + Owner.BrowseName);
            newRow.GetVariable("mainWindow").Value = Owner.NodeId;
            newRow.GetVariable("objectPointer").Value = pmObject.NodeId;

            bodyContainer.Add(newRow);
            if(!firstRow)
            {
                firstRow = true;
                newRow.GetVariable("isSelected").Value = true;
                Owner.GetVariable("selectedObject").Value = pmObject.NodeId;
            }
        }

    }
}
