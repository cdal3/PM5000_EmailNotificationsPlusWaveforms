#region Using directives
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Mail;
using System.Xml.Linq;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.Core;
using UAManagedCore;
#endregion

public class PMNotification_Engine_Logic : BaseNetLogic
{
    IUAVariable emailServiceTest; //0 - not initated, 1 - ok, 2 - error
    IUANode devicesFolder;
    IUANode usersFolder;
    private PeriodicTask mainPeriodicTask;
    private bool isDownloadingFtp = false; // Flag to prevent periodic task overlap

    public override void Start()
    {
        devicesFolder = InformationModel.Get(LogicObject.GetVariable("devicesFolder").Value);
        usersFolder = InformationModel.Get(LogicObject.GetVariable("usersFolder").Value);
        addPMstoUsers();
        emailServiceTest = InformationModel.Get(LogicObject.GetVariable("generalSettings").Value).GetVariable("status");
        emailServiceTest.Value = 0;

        int logReadFrequency = InformationModel.Get(LogicObject.GetVariable("generalSettings").Value).GetVariable("LogReadFrequency").Value;
        if (logReadFrequency < 3 || logReadFrequency > 21600)
            Log.Info("PMNotification", $"LogReadFrequency value: {logReadFrequency} is out of range (3s�6h). Clamping applied.");

        logReadFrequency = 1000 * Math.Max(3, Math.Min(21600, logReadFrequency));
        mainPeriodicTask = new PeriodicTask(getEventsFromPM, logReadFrequency, LogicObject);
        mainPeriodicTask.Start();

    }

    public override void Stop()
    {
        mainPeriodicTask.Dispose();
    }
    #region PowerMonitorEvents    

    [ExportMethod]
    private async void getEventsFromPM()
    {
        // Skip this cycle if FTP download is already in progress
        if (isDownloadingFtp)
        {
            Log.Info("PMNotification", "Skipping periodic task cycle - FTP download in progress");
            return;
        }

        foreach (var device in devicesFolder.GetNodesByType<PMNotification_PowerMonitor>().ToList())
        {

            bool connectionReestablished = false;

            if (device.DeviceStatus.connectionStatus as int? == 0)
            {
                connectionReestablished = true;
            }

            if (!device.httpEnable)
            {
                device.DeviceStatus.connectionStatus = 0;
                continue;
            }

            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            string csvData = string.Empty;
            string deviceIpAddress = device.GetVariable("httpEnable/ipAddress").Value;
            int devicePort = device.GetVariable("httpEnable/port").Value;
            string filePath = $"http://{deviceIpAddress}:{devicePort}/LoggingResults/Power_Quality_Log.csv";

            int connectionTimeout = device.GetVariable("httpEnable/connectionTimeout").Value;

            try
            {

                using (HttpClient httpClient = new HttpClient())

                {
                    httpClient.Timeout = TimeSpan.FromMilliseconds(connectionTimeout);
                    HttpResponseMessage response = await httpClient.GetAsync(filePath);
                    if (response.IsSuccessStatusCode)
                    {
                        csvData = await response.Content.ReadAsStringAsync();
                    }
                    else
                    {
                        device.DeviceStatus.lastError = "Device server error: " + $"HTTP error {(int)response.StatusCode} - {response.ReasonPhrase}";
                        device.DeviceStatus.connectionStatus = 2;
                        device.DeviceStatus.succesfullConnTime = DateTime.Now;
                        device.DeviceStatus.responeTimeMS = (int)stopwatch.ElapsedMilliseconds;
                        stopwatch.Stop();
                    }
                }
            }
            catch (Exception ex)
            {
                device.DeviceStatus.lastError = "Device server error: " + ex.Message;
                device.DeviceStatus.connectionStatus = 2;
                device.DeviceStatus.succesfullConnTime = DateTime.Now;
                stopwatch.Stop();
                device.DeviceStatus.responeTimeMS = (int)stopwatch.ElapsedMilliseconds;
                continue;
            }


            stopwatch.Stop();
            device.DeviceStatus.responeTimeMS = (int)stopwatch.ElapsedMilliseconds;

            device.DeviceStatus.connectionStatus = 1;
            device.DeviceStatus.succesfullConnTime = DateTime.Now;

            List<string> eventRecords = new List<string>(csvData.Split('\n'));

            PMNotification_eventObject lastSavedEvent = getLastSavedEvent(device);

            List<PowerMonitorEvent> eventsToAdd = new List<PowerMonitorEvent>();

            if (eventRecords.Count == 0) continue;

            foreach (var eventRecord in eventRecords.AsEnumerable().Reverse())
            {
                string[] eventRecordSplited = eventRecord.Split(',');
                if (eventRecord.Length == 0 || !int.TryParse(eventRecordSplited[0], out int number)) continue;

                try
                {
                    PowerMonitorEvent powerMonitorEvent = new PowerMonitorEvent
                    {
                        RecordIdentifier = int.Parse(eventRecordSplited[0]),
                        Event_Type = eventRecordSplited[1].Replace(" ", "_"),
                        Sub_Event_Code = int.Parse(eventRecordSplited[2]),
                        Local_Timestamp = PowerMonitorEvent.ParseTimestamp(
                                    int.Parse(eventRecordSplited[3]),  // Year
                                    int.Parse(eventRecordSplited[4]),  // Month & Day
                                    int.Parse(eventRecordSplited[5]),  // Hour & Minute
                                    int.Parse(eventRecordSplited[6]),  // Seconds & Milliseconds
                                    int.Parse(eventRecordSplited[7])   // Microseconds
                                ),
                        UTC_Timestamp = PowerMonitorEvent.ParseTimestamp(
                                    int.Parse(eventRecordSplited[8]),
                                    int.Parse(eventRecordSplited[9]),
                                    int.Parse(eventRecordSplited[10]),
                                    int.Parse(eventRecordSplited[11]),
                                    int.Parse(eventRecordSplited[12])
                                ),
                        Association_Timestamp = PowerMonitorEvent.ParseTimestamp(
                                    int.Parse(eventRecordSplited[13]),
                                    int.Parse(eventRecordSplited[14]),
                                    int.Parse(eventRecordSplited[15]),
                                    int.Parse(eventRecordSplited[16]),
                                    int.Parse(eventRecordSplited[17])
                                ),
                        Event_Duration_mS = float.Parse(eventRecordSplited[18]),
                        Min_or_Max = float.Parse(eventRecordSplited[19].Replace("%", "")),
                        Trip_Point = float.Parse(eventRecordSplited[20].Replace("%", "")),
                        WSB_Originator = int.Parse(eventRecordSplited[21]),
                        WaveformID = FormatWaveformID(
                            eventRecordSplited[13].PadLeft(4, '0') +
                            eventRecordSplited[14].PadLeft(4, '0') +
                            eventRecordSplited[15].PadLeft(4, '0') +
                            eventRecordSplited[16].PadLeft(5, '0') +
                            eventRecordSplited[17].PadLeft(3, '0')
                        )
                    };

                    static string FormatWaveformID(string raw)
                    {
                        if (raw.Length < 20)
                            raw = raw.PadLeft(20, '0');
                        // 8 chars, underscore, 6 chars, underscore, 6 chars
                        return $"{raw.Substring(0, 8)}_{raw.Substring(8, 6)}_{raw.Substring(14, 6)}";
                    }

                    if (lastSavedEvent != null && lastSavedEvent.UTC_Timestamp == powerMonitorEvent.UTC_Timestamp
                        && lastSavedEvent.Event_Type == powerMonitorEvent.Event_Type
                        && lastSavedEvent.Sub_Event_Code == powerMonitorEvent.Sub_Event_Code)
                    {
                        break;
                    }
                    else
                    {
                        eventsToAdd.Add(powerMonitorEvent);
                    }

                }
                catch (Exception ex)
                {
                    Log.Info("PMNotification", "Device: " + device.nameToDisplay + ". Couldn't parse the table: " + ex.Message);
                    device.DeviceStatus.lastError = "Couldn't parse the table: " + ex.Message;
                }
            }

            if (eventsToAdd != null)
            {
                foreach (var eventToAdd in eventsToAdd.AsEnumerable().Reverse())
                {
                    AddPowerMonitorEvent(device, eventToAdd, connectionReestablished);
                }
            }

        }

        if (emailServiceTest.Value != 1) checkEmialService();
        if (emailServiceTest.Value == 1) sendNotifications();

    }


    private void AddPowerMonitorEvent(PMNotification_PowerMonitor device, PowerMonitorEvent powerMonitorEvent, bool connectionReestablished)
    {

        //Create new object
        PMNotification_eventObject newEvent = InformationModel.Make<PMNotification_eventObject>("PMEvent_" + powerMonitorEvent.Event_Type
            + powerMonitorEvent.Sub_Event_Code + powerMonitorEvent.UTC_Timestamp.ToString());

        newEvent.Event_Type = powerMonitorEvent.Event_Type;
        newEvent.Sub_Event_Code = powerMonitorEvent.Sub_Event_Code;
        newEvent.Local_Timestamp = powerMonitorEvent.Local_Timestamp;
        newEvent.UTC_Timestamp = powerMonitorEvent.UTC_Timestamp;
        newEvent.Association_Timestamp = powerMonitorEvent.Association_Timestamp;
        newEvent.Event_Duration_mS = powerMonitorEvent.Event_Duration_mS;
        newEvent.Min_or_Max = powerMonitorEvent.Min_or_Max;
        newEvent.Trip_Point = powerMonitorEvent.Trip_Point;
        newEvent.WSB_Originator = powerMonitorEvent.WSB_Originator;
        newEvent.WaveformID = powerMonitorEvent.WaveformID;
        newEvent.index = powerMonitorEvent.RecordIdentifier;
        newEvent.NotificationProcessed = connectionReestablished;

        //Add event to folder
        device.PowerQualityEvents.Add(newEvent);

        //Delete if there is more then 100 events.

        if(device.PowerQualityEvents.GetNodesByType<PMNotification_eventObject>().Count() > 100) device.PowerQualityEvents.Remove(device.PowerQualityEvents.GetNodesByType<PMNotification_eventObject>().First());

    }

    private PMNotification_eventObject getLastSavedEvent(PMNotification_PowerMonitor device)
    {
        return device.PowerQualityEvents?.Children?.LastOrDefault() as PMNotification_eventObject;
    }
    #endregion
    #region sendMail

    [ExportMethod]
    private void sendNotifications()
    {
        foreach (var device in devicesFolder.GetNodesByType<PMNotification_PowerMonitor>().ToList())
        {

            //No events, no point to going through
            if (!device.PowerQualityEvents.Children.Any()) continue;

            List<PMNotification_User> notificationRecipients = new List<PMNotification_User>();
            List<PMNotification_eventObject> eventsToSend = new List<PMNotification_eventObject>();

            List<PMNotification_eventObject> deviceEvents = device.PowerQualityEvents.GetNodesByType<PMNotification_eventObject>().ToList();

            for (int i = 0; i < deviceEvents.Count; i++)
            {

                eventsToSend.Clear();
                notificationRecipients.Clear();


                PMNotification_eventObject eventObject = deviceEvents[i];

                if (eventObject.NotificationProcessed == true) continue;

                eventsToSend.Add(eventObject);

                if (!device.emailEnable)
                {
                    markNotificationProcessed(eventsToSend);
                    continue;
                }

                while ((i < deviceEvents.Count - 1)
                    && eventObject.Event_Type == deviceEvents[i + 1].Event_Type
                    && eventObject.UTC_Timestamp == deviceEvents[i + 1].UTC_Timestamp)
                {
                    eventsToSend.Add(deviceEvents[i + 1]);
                    i++;
                }

                // Find the users to which the notification needs to be sent

                foreach (PMNotification_User user in usersFolder.Children)
                {
                    if (user.isActive
                        && user.eventsDefinitions.Get<PMNotification_Definition>(eventObject.Event_Type) != null
                        && user.eventsDefinitions.Get<PMNotification_Definition>(eventObject.Event_Type).Selected == true
                        && user.deviceDefinitions.Get<PMNotification_Definition>(device.nameToDisplay) != null
                        && user.deviceDefinitions.Get<PMNotification_Definition>(device.nameToDisplay).Selected == true)
                        notificationRecipients.Add(user);
                }

                if (notificationRecipients.Any())
                {
                    var status = sendEmail(notificationRecipients, eventsToSend, device);

                    if (!status)
                    {
                        emailServiceTest.Value = 2;
                        return;
                    }
                    else markNotificationProcessed(eventsToSend);
                }
                else markNotificationProcessed(eventsToSend);
            }
        }
    }

    private void markNotificationProcessed(List<PMNotification_eventObject> sentEvents)
    {
        foreach(var sentEvent in sentEvents)
        {
            sentEvent.NotificationProcessed = true;
        }
    }

    private void checkEmialService()
    {
        var emailLogicEngine = InformationModel.GetObject(LogicObject.GetVariable("emailLogic").Value);
        object[] arguments = new object[] { emailLogicEngine.GetVariable("SenderEmailAddress").Value.Value, "Power Monitor Event Notification - SeflTest", "This message was sent to test the EmailService. Do not respond." };
        emailLogicEngine.ExecuteMethod("SendEmail", arguments, out object[] outputArgs);
        var methodResponse = (string)outputArgs[0];
        emailServiceTest.Value = methodResponse.Contains("Email sent successfully") ? 1 : 2;
    }

    private Boolean sendEmail(List<PMNotification_User> recipients, List<PMNotification_eventObject> eventsToSend, PMNotification_PowerMonitor device)
    {
        // Prepare the body
        string message = $@"
            <html>
            <head>
                <style>
                    body {{ font-family: Arial, sans-serif; color: #000; background-color: #fff; padding: 20px; }}
                    .container {{ background: #f9f9f9; padding: 15px; border-radius: 8px; border: 1px solid #ddd; }}
                    h2 {{ color: #333; }}
                    table {{ width: 100%; border-collapse: collapse; margin-top: 15px; }}
                    th, td {{ border: 1px solid #ddd; padding: 8px; text-align: left; }}
                    th {{ background: #f1f1f1; }}
                    .additional-info {{ margin-top: 20px; padding: 10px; background-color: #eef; border-radius: 5px; }}
                </style>
            </head>
            <body>
                <div class='container'>
                    <h2>Power Quality Event Notification</h2>
                    <p><b>Device Name:</b> {device.nameToDisplay}</p>
                    <p><b>IP Address:</b> {device.GetVariable("httpEnable/ipAddress").Value.Value}</p>
                    <hr>
                    <p><b>Event Type:</b> {eventsToSend[0].Event_Type}</p>
                    <p><b>Start Time (Local):</b> {eventsToSend[0].Local_Timestamp.ToString()}</p>
                    <p><b>Nominal Voltage:</b> {device.NominalVoltage} V</p>
                    
                    <table>
                        <tr>
                            <th>Sub Event Code</th>
                            <th>Duration (ms)</th>
                            <th>Min or Max</th>
                            <th>Trip Point</th>
                            <th>WSB ID</th>
                        </tr>";

        foreach(PMNotification_eventObject eventObject in eventsToSend)
        {
            message += $@"
                        <tr>
                            <td>{eventObject.Sub_Event_Code}</td>
                            <td>{eventObject.Event_Duration_mS:F3}</td>
                            <td>{eventObject.Min_or_Max:F3}</td>
                            <td>{eventObject.Trip_Point:F2}%</td>
                            <td>{eventObject.WSB_Originator}</td>
                        </tr>";
        }

        message += $@"
                    </table>

                    <div class='additional-info'>
                        <h3>Additional Information</h3>
                        <p>{device.AdditionalInformationLine1}</p>
                        <p>{device.AdditionalInformationLine2}</p>
                        <p>{device.AdditionalInformationLine3}</p>
                    </div>
                </div>
            </body>
            </html>";

        // Attempt to retrieve waveform file via FTP if needed
        byte[] waveformFileBytes = null;
        string waveformFileName = null;
        string waveformID = eventsToSend[0].WaveformID;
        var attachmentURI = Project.Current.GetVariable("Model/PMNotification/Engine/PMNotification_Email_Logic/Attachment");
        if (!string.IsNullOrEmpty(waveformID) && waveformID != "00000000_000000_000000")
        {
            try
            {
                // Set flag to block periodic task during FTP download
                isDownloadingFtp = true;
                Log.Info("PMNotification", $"Starting FTP download for waveform {waveformID}");

                string ftpIp = device.GetVariable("httpEnable/ipAddress").Value.Value.ToString();
                string ftpDirectory = "/Waveform/";
                string ftpUrl = $"ftp://{ftpIp}{ftpDirectory}";
                string searchPattern = $"*{waveformID}*.wfm";

                // List files in the FTP directory
#pragma warning disable SYSLIB0014 // Type or member is obsolete
                System.Net.FtpWebRequest listRequest = (System.Net.FtpWebRequest)System.Net.WebRequest.Create(ftpUrl);
#pragma warning restore SYSLIB0014 // Type or member is obsolete
                listRequest.Method = System.Net.WebRequestMethods.Ftp.ListDirectory;
                listRequest.Timeout = 10000; // 10 second timeout
                var username = (string)device.GetVariable("Username").Value;
                var password = (string)device.GetVariable("Password").Value;
                listRequest.Credentials = new System.Net.NetworkCredential("admin", "admin");
                using (var listResponse = (System.Net.FtpWebResponse)listRequest.GetResponse())
                using (var listStream = listResponse.GetResponseStream())
                using (var reader = new System.IO.StreamReader(listStream))
                {
                    while (!reader.EndOfStream)
                    {
                        string file = reader.ReadLine();
                        if (file != null && file.IndexOf(waveformID, StringComparison.OrdinalIgnoreCase) >= 0 && file.EndsWith(".wfm", StringComparison.OrdinalIgnoreCase))
                        {
                            waveformFileName = file;
                            break;
                        }
                    }
                }

                // Download the file if found
                if (!string.IsNullOrEmpty(waveformFileName))
                {
                    string fileUrl = $"ftp://{ftpIp}{ftpDirectory}{waveformFileName}";
#pragma warning disable SYSLIB0014 // Type or member is obsolete
                    System.Net.FtpWebRequest downloadRequest = (System.Net.FtpWebRequest)System.Net.WebRequest.Create(fileUrl);
#pragma warning restore SYSLIB0014 // Type or member is obsolete
                    downloadRequest.Method = System.Net.WebRequestMethods.Ftp.DownloadFile;
                    downloadRequest.Timeout = 30000; // 30 second timeout for file download
                    downloadRequest.Credentials = new System.Net.NetworkCredential("admin", "admin");
                    using (var downloadResponse = (System.Net.FtpWebResponse)downloadRequest.GetResponse())
                    using (var fileStream = downloadResponse.GetResponseStream())
                    using (var ms = new System.IO.MemoryStream())
                    {
                        fileStream.CopyTo(ms);
                        waveformFileBytes = ms.ToArray();
                        // Store the file in a cross-platform temp directory
                        var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{waveformFileName}");
                        System.IO.File.WriteAllBytes(tempPath, waveformFileBytes);
                        attachmentURI.Value = tempPath;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Info("PMNotification", "FTP waveform file error: " + ex.Message);
                // Continue without attachment
            }
            finally
            {
                // Always clear the flag when FTP operation completes
                isDownloadingFtp = false;
                Log.Info("PMNotification", "FTP download completed");
            }
        }
        else {
            attachmentURI.Value = "";
        }

        try
        {
            foreach(PMNotification_User recipient in recipients)
            {
                var emailLogicEngine = InformationModel.GetObject(LogicObject.GetVariable("emailLogic").Value);

                object[] arguments = new object[] { recipient.email, "Power Monitor Event Notification", message };
                emailLogicEngine.ExecuteMethod("SendEmail", arguments, out object[] outputArgs);

                string statusMessage = (string)outputArgs[0];

                recipient.UserStatus.lastError = statusMessage;
                recipient.UserStatus.succesfullConnTime = DateTime.Now;

                if(!statusMessage.Contains("Email sent successfully")) return false;
            }
        }
        catch(Exception ex)
        {
            Log.Info("PMNotification", "Cannot send an email. " + ex.Message);
            return false;
        }

        return true;
    }
    #endregion

    private void addPMstoUsers()
    {
        foreach(var user in usersFolder.GetNodesByType<PMNotification_User>().ToList())
        {
            addPMstoUser(user);

        }
    }

    private void addPMstoUser(PMNotification_User user)
    {

        foreach(var definition in user.deviceDefinitions.GetNodesByType<PMNotification_Definition>().ToList())
        {
            bool stillExist = false;

            foreach(PMNotification_PowerMonitor device in devicesFolder.GetNodesByType<PMNotification_PowerMonitor>().ToList())
            {
                if(device.nameToDisplay == definition.BrowseName)
                {
                    stillExist = true;
                    break;
                }
            }

            if(!stillExist) user.deviceDefinitions.Remove(definition);
        }
        foreach(var device in devicesFolder.GetNodesByType<PMNotification_PowerMonitor>().ToList())
        {
            if(user.deviceDefinitions.Get(device.nameToDisplay) == null)
            {
                PMNotification_Definition newDefinition = InformationModel.Make<PMNotification_Definition>(device.nameToDisplay);
                user.deviceDefinitions.Add(newDefinition);
            }
        }

    }



    #region Users
    [ExportMethod]
    public void PMNotification_CreateUser(string name, string lastName, string email, out string status)

    {

        if(string.IsNullOrEmpty(name) || string.IsNullOrEmpty(lastName) || !IsValidEmail(email))
        {
            status = "Incorect values";
            return;
        }

        PMNotification_User newUser = InformationModel.Make<PMNotification_User>(name + lastName + Guid.NewGuid().ToString().Substring(0, 8));

        newUser.email = email;
        newUser.Name = name;
        newUser.LastName = lastName;
        newUser.nameToDisplay = name + " " + lastName;

        usersFolder.Add(newUser);
        addPMstoUser(newUser);
        status = "User added succesfully";

    }
    [ExportMethod]
    public void PMNotification_DeleteUser(NodeId nodeId, out string status)
    {
        var user = InformationModel.Get(nodeId);

        usersFolder.Remove(user);
        status = "User removed sucessfully";
    }





    static bool IsValidEmail(string email)
    {
        try
        {
            var mail = new MailAddress(email);
            return mail.Address == email; // Ensures no extra spaces
        }
        catch
        {
            return false;
        }
    }


    #endregion

}

public class PowerMonitorEvent
{
    public int RecordIdentifier { get; set; }
    public string Event_Type { get; set; }
    public int Sub_Event_Code { get; set; }

    public DateTime Local_Timestamp { get; set; }
    public DateTime UTC_Timestamp { get; set; }
    public DateTime Association_Timestamp { get; set; }
    public float Event_Duration_mS { get; set; }
    public float Min_or_Max { get; set; }
    public float Trip_Point { get; set; }
    public int WSB_Originator { get; set; }
    public string WaveformID { get; set; } 

    public static DateTime ParseTimestamp(int year, int monthDay, int hourMinute, int secMs, int microSec)
    {
        if (year == 0 || monthDay == 0 || hourMinute == 0) return DateTime.MinValue;

        string dateString = $"{year}-{monthDay / 100:D2}-{monthDay % 100:D2} {hourMinute / 100:D2}:{hourMinute % 100:D2}:{secMs / 1000:D2}.{secMs % 1000:D3}{microSec:D3}";
        DateTime dt = DateTime.ParseExact(dateString, "yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
        dt = dt.AddTicks(microSec * 10);
        return dt;
    }
}
