#region Using directives
using FTOptix.NetLogic;
using FTOptix.Retentivity;
using UAManagedCore;
#endregion

public class PMNotification_ImportLogic : BaseNetLogic
{
    [ExportMethod]
    public void PrepareSolution()
    {

        IUANode rootFolder = Owner.Owner;
        IUANode devicesFolder = rootFolder.Get("Devices");
        IUANode usersFolder = rootFolder.Get("Users");
        IUANode engineLogic = Owner.Get("PMNotification_Engine_Logic");

        //updateRetentivityStorage

        var rsNodes = Owner.Get<RetentivityStorage>("PMNotification_RS").Nodes;
        rsNodes.Get("Users").Value = usersFolder.NodeId;
        rsNodes.Get("Devices").Value = devicesFolder.NodeId;
        rsNodes.Get("generalSettings").Value = rootFolder.Get("generalSettings").NodeId;
        Log.Info("PMNotification", "Retentivity storage updated successfully");

        //update Graphics

        rootFolder.GetVariable("UIObjects/PMNotification_UserManagement/objectSelector/objectsLocation").Value = usersFolder.NodeId;
        rootFolder.GetVariable("UIObjects/PMNotification_PMManagement/objectSelector/objectsLocation").Value = devicesFolder.NodeId;
        rootFolder.GetVariable("UIObjects/PMNotification_LogViewer/objectSelector/objectsLocation").Value = devicesFolder.NodeId;
        rootFolder.GetVariable("UIObjects/PMNotification_Main/emailServiceStatus").Value = rootFolder.Get("generalSettings/status").NodeId;
        Log.Info("PMNotification", "Graphic objects updated successfully");

        //update engineProperty

        engineLogic.GetVariable("usersFolder").Value = usersFolder.NodeId;
        engineLogic.GetVariable("devicesFolder").Value = devicesFolder.NodeId;
        engineLogic.GetVariable("emailLogic").Value = Owner.Get("PMNotification_Email_Logic").NodeId;
        engineLogic.GetVariable("generalSettings").Value = rootFolder.Get("generalSettings").NodeId;
        Log.Info("PMNotification", "Logic Engine parameters updated successfully");


        //update MethodsCalls

        rootFolder.GetVariable(
            "Engine/UI/PMNotification_AddUserPopup/Button1/MouseClickEventHandler1/MethodsToCall/MethodContainer1/ObjectPointer").Value
            = engineLogic.NodeId;


        rootFolder.GetVariable(
            "UIObjects/PMNotification_UserManagement/Button1/MouseClickEventHandler1/MethodsToCall/MethodContainer1/ObjectPointer").Value
            = engineLogic.NodeId;

        Log.Info("PMNotification", "Engine method's calls updated successfully");

    }
}
