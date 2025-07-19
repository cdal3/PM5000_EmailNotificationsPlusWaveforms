#region Using directives
using System.Linq;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using UAManagedCore;
#endregion

public class PMNotification_UserSelectedOptions_Logic : BaseNetLogic
{

    PMNotification_User user;
    int selectedOption; //0 - show devices, 1 - show Event Types

    public override void Start()
    {
        selectedOption = Owner.GetVariable("selectedOption").Value;
        refreshList();
        Owner.GetVariable("userNodeId").VariableChange += PMNotification_UserEventList_Logic_VariableChange;
    }

    public override void Stop()
    {
        Owner.GetVariable("userNodeId").VariableChange -= PMNotification_UserEventList_Logic_VariableChange;
    }

    private void PMNotification_UserEventList_Logic_VariableChange(object sender, VariableChangeEventArgs e)
    {
        refreshList();
    }
    [ExportMethod]
    public void refreshList()
    {
        user = InformationModel.Get<PMNotification_User>(Owner.GetVariable("userNodeId").Value);
        var bodyContainer = Owner.Get("body/vertical");
        bodyContainer.GetNodesByType<PMNotification_SelectListRow>().ToList().ForEach(bodyContainer.Remove);

        if(user == null) return;

        IUAObject location = null;

        if(selectedOption == 0) location = user.deviceDefinitions; else location = user.eventsDefinitions;

        foreach(PMNotification_Definition eventType in location.GetNodesByType<PMNotification_Definition>())
        {
            PMNotification_SelectListRow newRow = InformationModel.Make<PMNotification_SelectListRow>(eventType.BrowseName + Owner.BrowseName);
            newRow.SetAlias("PMNotification_SelectListRowAlias", eventType);
            bodyContainer.Add(newRow);
        }

    }
}
