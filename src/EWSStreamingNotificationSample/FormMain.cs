﻿/*
 * By David Barrett, Microsoft Ltd. 2013. Use at your own risk.  No warranties are given.
 * 
 * DISCLAIMER:
 * THIS CODE IS SAMPLE CODE. THESE SAMPLES ARE PROVIDED "AS IS" WITHOUT WARRANTY OF ANY KIND.
 * MICROSOFT FURTHER DISCLAIMS ALL IMPLIED WARRANTIES INCLUDING WITHOUT LIMITATION ANY IMPLIED WARRANTIES OF MERCHANTABILITY OR OF FITNESS FOR
 * A PARTICULAR PURPOSE. THE ENTIRE RISK ARISING OUT OF THE USE OR PERFORMANCE OF THE SAMPLES REMAINS WITH YOU. IN NO EVENT SHALL
 * MICROSOFT OR ITS SUPPLIERS BE LIABLE FOR ANY DAMAGES WHATSOEVER (INCLUDING, WITHOUT LIMITATION, DAMAGES FOR LOSS OF BUSINESS PROFITS,
 * BUSINESS INTERRUPTION, LOSS OF BUSINESS INFORMATION, OR OTHER PECUNIARY LOSS) ARISING OUT OF THE USE OF OR INABILITY TO USE THE
 * SAMPLES, EVEN IF MICROSOFT HAS BEEN ADVISED OF THE POSSIBILITY OF SUCH DAMAGES. BECAUSE SOME STATES DO NOT ALLOW THE EXCLUSION OR LIMITATION
 * OF LIABILITY FOR CONSEQUENTIAL OR INCIDENTAL DAMAGES, THE ABOVE LIMITATION MAY NOT APPLY TO YOU.
 * */

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.DirectoryServices;
using Microsoft.Exchange.WebServices.Data;
using Microsoft.Exchange.WebServices.Autodiscover;
using System.Threading;
using System.Net;

namespace EWSStreamingNotificationSample
{
    struct NotificationInfo
    {
        public string Mailbox;
        public object Event;
        public ExchangeService Service;
    }



    public partial class FormMain : Form
    {
        ClassLogger _logger=null;
        ClassTraceListener _traceListener = null;
        Dictionary<string,StreamingSubscriptionConnection> _connections = null;
        Dictionary<string, StreamingSubscription> _subscriptions = null;
        Dictionary<string, GroupInfo> _groups = null;
        Mailboxes _mailboxes = null;
        private bool _reconnect = false;
        private Object _reconnectLock = new Object();

        public FormMain()
        {
            InitializeComponent();


            // Create our logger
            _logger = new ClassLogger("Notifications.log");
            _logger.LogAdded += new ClassLogger.LoggerEventHandler(_logger_LogAdded);
            _traceListener = new ClassTraceListener("Trace.log");

            // Increase default connection limit - this is CRUCIAL when supporting multiple subscriptions
            ServicePointManager.DefaultConnectionLimit = 255;
            _logger.Log("Default connection limit increased to 255");

            comboBoxSubscribeTo.SelectedIndex = 5; // Set to Inbox first of all
            checkedListBoxEvents.SetItemChecked(0, true);
            buttonUnsubscribe.Enabled = false;

            _connections = new Dictionary<string, StreamingSubscriptionConnection>();
            ReadMailboxes();

            _mailboxes = new Mailboxes(null, _logger, _traceListener);

            comboBoxExchangeVersion.SelectedIndex = 0;
            //comboBoxExchangeVersion.Enabled = false;
        }

        private void ReadMailboxes(string MailboxFile="")
        {
            string sMailboxFile = MailboxFile;
            if (String.IsNullOrEmpty(MailboxFile))
                sMailboxFile = "Mailboxes " + Environment.MachineName.ToUpper() + ".txt";
            if (!System.IO.File.Exists(sMailboxFile))
                return;

            checkedListBoxMailboxes.Items.Clear();
            using (System.IO.StreamReader reader = new System.IO.StreamReader(sMailboxFile))
            {
                while (!reader.EndOfStream)
                {
                    string sMailbox = reader.ReadLine();
                    if (sMailbox.ToLower().StartsWith("admin="))
                    {
                        textBoxUsername.Text = sMailbox.Substring(6);
                    }
                    else if (sMailbox.ToLower().StartsWith("password="))
                    {
                        textBoxPassword.Text = sMailbox.Substring(9);
                    }
                    else
                        checkedListBoxMailboxes.Items.Add(sMailbox);
                }
            }
        }

        void _logger_LogAdded(object sender, LoggerEventArgs a)
        {
            try
            {
                if (listBoxEvents.InvokeRequired)
                {
                    // Need to invoke
                    listBoxEvents.Invoke(new MethodInvoker(delegate()
                    {
                        listBoxEvents.Items.Add(a.LogDetails);
                        listBoxEvents.SelectedIndex = listBoxEvents.Items.Count - 1;
                    }));
                }
                else
                {
                    listBoxEvents.Items.Add(a.LogDetails);
                    listBoxEvents.SelectedIndex = listBoxEvents.Items.Count - 1;
                }
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show(ex.Message, "Error");
            }
        }

        void ProcessNotification(object e, StreamingSubscription Subscription)
        {
            // We have received a notification

            string sMailbox = Subscription.Service.ImpersonatedUserId.Id;

            if (String.IsNullOrEmpty(sMailbox))
                sMailbox = "Unknown mailbox";
            string sEvent = sMailbox + ": ";

            if (e is ItemEvent)
            {
                if (!checkBoxShowItemEvents.Checked) return; // We're ignoring item events
                sEvent += "Item " + (e as ItemEvent).EventType.ToString() + ": ";
            }
            else if (e is FolderEvent)
            {
                if (!checkBoxShowFolderEvents.Checked) return; // We're ignoring folder events
                sEvent += "Folder " + (e as FolderEvent).EventType.ToString() + ": ";
            }

            try
            {
                if (checkBoxQueryMore.Checked)
                {
                    // We want more information, we'll get this on a new thread
                    NotificationInfo info;
                    info.Mailbox = sMailbox;
                    info.Event = e;
                    info.Service = Subscription.Service;
                    
                    ThreadPool.QueueUserWorkItem(new WaitCallback(ShowMoreInfo), info);
                }
                else
                {
                    // Just log event and ID
                    if (e is ItemEvent)
                    {
                        sEvent += "ItemId = " + (e as ItemEvent).ItemId.UniqueId;
                    }
                    else if (e is FolderEvent)
                    {
                        sEvent += "FolderId = " + (e as FolderEvent).FolderId.UniqueId;
                    }
                }
            }
            catch { }

            if (checkBoxQueryMore.Checked)
                return;

            ShowEvent(sEvent);
        }

        void ShowMoreInfo(object e)
        {
            // Get more info for the given item.  This will run on it's own thread
            // so that the main program can continue as usual (we won't hold anything up)

            NotificationInfo n = (NotificationInfo)e;

            ExchangeService ewsMoreInfoService = new ExchangeService(n.Service.RequestedServerVersion);
            ewsMoreInfoService.Credentials = new WebCredentials(textBoxUsername.Text, textBoxPassword.Text);
            ewsMoreInfoService.UseDefaultCredentials = false;
            ewsMoreInfoService.ImpersonatedUserId = new ImpersonatedUserId(ConnectingIdType.SmtpAddress, n.Mailbox);
            ewsMoreInfoService.Url = n.Service.Url;
            ewsMoreInfoService.TraceListener = _traceListener;
            ewsMoreInfoService.TraceFlags = TraceFlags.All;
            ewsMoreInfoService.TraceEnabled = true;

            string sEvent = "";
            if (n.Event is ItemEvent)
            {
                sEvent = n.Mailbox + ": Item " + (n.Event as ItemEvent).EventType.ToString() + ": " + MoreItemInfo(n.Event as ItemEvent, ewsMoreInfoService);
            }
            else
                sEvent = n.Mailbox + ": Folder " + (n.Event as FolderEvent).EventType.ToString() + ": " + MoreFolderInfo(n.Event as FolderEvent, ewsMoreInfoService);

            ShowEvent(sEvent);
        }

        private string MoreItemInfo(ItemEvent e, ExchangeService service)
        {
            string sMoreInfo = "";
            if (e.EventType == EventType.Deleted)
            {
                // We cannot get more info for a deleted item by binding to it, so skip item details
            }
            else
                sMoreInfo += "Item subject=" + GetItemInfo(e.ItemId, service);
            if (e.ParentFolderId != null)
            {
                if (!String.IsNullOrEmpty(sMoreInfo)) sMoreInfo += ", ";
                sMoreInfo += "Parent Folder Name=" + GetFolderName(e.ParentFolderId, service);
            }
            return sMoreInfo;
        }

        private string MoreFolderInfo(FolderEvent e, ExchangeService service)
        {
            string sMoreInfo = "";
            if (e.EventType == EventType.Deleted)
            {
                // We cannot get more info for a deleted item by binding to it, so skip item details
            }
            else
                sMoreInfo += "Folder name=" + GetFolderName(e.FolderId, service);
            if (e.ParentFolderId != null)
            {
                if (!String.IsNullOrEmpty(sMoreInfo)) sMoreInfo += ", ";
                sMoreInfo += "Parent Folder Name=" + GetFolderName(e.ParentFolderId, service);
            }
            return sMoreInfo;
        }

        private string GetItemInfo(ItemId itemId, ExchangeService service)
        {
            // Retrieve the subject for a given item
            string sItemInfo = "";
            Item oItem;
            PropertySet oPropertySet;

            if (checkBoxIncludeMime.Checked)
            {
                oPropertySet = new PropertySet(BasePropertySet.FirstClassProperties, ItemSchema.MimeContent);
            }
            else
                oPropertySet = new PropertySet(ItemSchema.Subject);

            try
            {
                oItem = Item.Bind(service, itemId, oPropertySet);
            }
            catch (Exception ex)
            {
                return ex.Message;
            }

            if (oItem is Appointment)
            {
                sItemInfo += "Appointment subject=" + oItem.Subject;
                // Show attendee information
                Appointment oAppt=oItem as Appointment;
                sItemInfo += ",RequiredAttendees=" + GetAttendees(oAppt.RequiredAttendees);
                sItemInfo += ",OptionalAttendees=" + GetAttendees(oAppt.OptionalAttendees);
            }
            else
                sItemInfo += "Item subject=" + oItem.Subject;
            if (checkBoxIncludeMime.Checked)
                sItemInfo += ", MIME length=" + oItem.MimeContent.Content.Length.ToString() + " bytes";
            return sItemInfo;
        }

        private string GetAttendees(AttendeeCollection attendees)
        {
            if (attendees.Count == 0) return "none";

            string sAttendees = "";
            foreach (Attendee attendee in attendees)
            {
                if (!String.IsNullOrEmpty(sAttendees))
                    sAttendees += ", ";
                sAttendees += attendee.Name;
            }

            return sAttendees;
        }

        private string GetFolderName(FolderId folderId, ExchangeService service)
        {
            // Retrieve display name of the given folder
            try
            {
                Folder oFolder = Folder.Bind(service, folderId, new PropertySet(FolderSchema.DisplayName));
                return oFolder.DisplayName;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private void ShowEvent(string eventDetails)
        {
            try
            {
                if (listBoxEvents.InvokeRequired)
                {
                    // Need to invoke
                    listBoxEvents.Invoke(new MethodInvoker(delegate()
                    {
                        listBoxEvents.Items.Add(eventDetails);
                        listBoxEvents.SelectedIndex = listBoxEvents.Items.Count - 1;
                    }));
                }
                else
                {
                    listBoxEvents.Items.Add(eventDetails);
                    listBoxEvents.SelectedIndex = listBoxEvents.Items.Count - 1;
                }
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show(ex.Message, "Error");
            }
        }

        private void buttonClose_Click(object sender, EventArgs e)
        {
            this.Dispose();
        }

        private EventType[] SelectedEvents()
        {
            // Read the selected events
            EventType[] events = null;
            if (comboBoxSubscribeTo.InvokeRequired)
            {
                checkedListBoxEvents.Invoke(new MethodInvoker(delegate()
                {
                    if (checkedListBoxEvents.CheckedItems.Count > 0)
                    {
                        events = new EventType[checkedListBoxEvents.CheckedItems.Count];

                        for (int i = 0; i < checkedListBoxEvents.CheckedItems.Count; i++)
                        {
                            switch (checkedListBoxEvents.CheckedItems[i].ToString())
                            {
                                case "NewMail": { events[i] = EventType.NewMail; break; }
                                case "Deleted": { events[i] = EventType.Deleted; break; }
                                case "Modified": { events[i] = EventType.Modified; break; }
                                case "Moved": { events[i] = EventType.Moved; break; }
                                case "Copied": { events[i] = EventType.Copied; break; }
                                case "Created": { events[i] = EventType.Created; break; }
                                case "FreeBusyChanged": { events[i] = EventType.FreeBusyChanged; break; }
                            }
                        }
                    }
                }));
            }
            else
            {
                if (checkedListBoxEvents.CheckedItems.Count < 1)
                    return null;
                events = new EventType[checkedListBoxEvents.CheckedItems.Count];

                for (int i = 0; i < checkedListBoxEvents.CheckedItems.Count; i++)
                {
                    switch (checkedListBoxEvents.CheckedItems[i].ToString())
                    {
                        case "NewMail": { events[i] = EventType.NewMail; break; }
                        case "Deleted": { events[i] = EventType.Deleted; break; }
                        case "Modified": { events[i] = EventType.Modified; break; }
                        case "Moved": { events[i] = EventType.Moved; break; }
                        case "Copied": { events[i] = EventType.Copied; break; }
                        case "Created": { events[i] = EventType.Created; break; }
                        case "FreeBusyChanged": { events[i] = EventType.FreeBusyChanged; break; }
                    }
                }
            }

            return events;
        }

        private void buttonSubscribe_Click(object sender, EventArgs e)
        {
            CreateGroups();
            if (ConnectToSubscriptions())
            {
                buttonUnsubscribe.Enabled = true;
                buttonSubscribe.Enabled = false;
                _logger.Log("Connected to subscription(s)");
            }
            else
                _logger.Log("Failed to create any subscriptions");
        }


        private FolderId[] SelectedFolders()
        {
            FolderId[] folders = new FolderId[1];
            string sSubscribeFolder = "";
            if (comboBoxSubscribeTo.InvokeRequired)
            {
                comboBoxSubscribeTo.Invoke(new MethodInvoker(delegate()
                {
                    sSubscribeFolder = comboBoxSubscribeTo.SelectedItem.ToString();
                }));
            }
            else
                sSubscribeFolder = comboBoxSubscribeTo.SelectedItem.ToString();

            switch (sSubscribeFolder)
            {
                case "Calendar":
                    folders[0] = new FolderId(WellKnownFolderName.Calendar); break;

                case "Contacts":
                    folders[0] = new FolderId(WellKnownFolderName.Contacts); break;

                case "DeletedItems":
                    folders[0] = new FolderId(WellKnownFolderName.DeletedItems); break;

                case "Drafts":
                    folders[0] = new FolderId(WellKnownFolderName.Drafts); break;

                case "Inbox":
                    folders[0] = new FolderId(WellKnownFolderName.Inbox); break;

                case "Journal":
                    folders[0] = new FolderId(WellKnownFolderName.Journal); break;

                case "Notes":
                    folders[0] = new FolderId(WellKnownFolderName.Notes); break;

                case "Outbox":
                    folders[0] = new FolderId(WellKnownFolderName.Outbox); break;

                case "SentItems":
                    folders[0] = new FolderId(WellKnownFolderName.SentItems); break;

                case "Tasks":
                    folders[0] = new FolderId(WellKnownFolderName.Tasks); break;

                case "MsgFolderRoot":
                    folders[0] = new FolderId(WellKnownFolderName.MsgFolderRoot); break;

                case "All Folders":
                    folders[0] = new FolderId("AllFolders"); break;
            }
            return folders;
        }

        private void SubscribeConnectionEvents(StreamingSubscriptionConnection connection)
        {
            // Subscribe to events for this connection

            connection.OnNotificationEvent += connection_OnNotificationEvent;
            connection.OnDisconnect += connection_OnDisconnect;
            connection.OnSubscriptionError += connection_OnSubscriptionError;
        }

        void connection_OnSubscriptionError(object sender, SubscriptionErrorEventArgs args)
        {
            try
            {
                _logger.Log(String.Format("OnSubscriptionError received for {0}: {1}", args.Subscription.Service.ImpersonatedUserId.Id, args.Exception.Message));
            }
            catch
            {
                _logger.Log("OnSubscriptionError received");
            }
        }

        void connection_OnDisconnect(object sender, SubscriptionErrorEventArgs args)
        {
            try
            {
                _logger.Log(String.Format("OnDisconnection received for {0}", args.Subscription.Service.ImpersonatedUserId.Id));
            }
            catch
            {
                _logger.Log("OnDisconnection received");
            }
            _reconnect = true;  // We can't reconnect in the disconnect event, so we set a flag for the timer to pick this up and check all the connections
        }

        void connection_OnNotificationEvent(object sender, NotificationEventArgs args)
        {
            foreach (NotificationEvent e in args.Events)
            {
                ProcessNotification(e, args.Subscription);
            }
        }

        private StreamingSubscription AddSubscription(string Mailbox, GroupInfo Group)
        {
            // Return the subscription, or create a new one if we don't already have one

            if (_subscriptions == null)
                _subscriptions = new Dictionary<string, StreamingSubscription>();

            if (_subscriptions.ContainsKey(Mailbox))
                _subscriptions.Remove(Mailbox);

            ExchangeService exchange = Group.ExchangeService;
            exchange.Credentials = new WebCredentials(textBoxUsername.Text, textBoxPassword.Text);
            exchange.ImpersonatedUserId = new ImpersonatedUserId(ConnectingIdType.SmtpAddress, Mailbox);
            FolderId[] selectedFolders = SelectedFolders();
            StreamingSubscription subscription;
            if (comboBoxSubscribeTo.SelectedItem.ToString().Equals("All Folders"))
            {
                subscription = exchange.SubscribeToStreamingNotificationsOnAllFolders(SelectedEvents());
            }
            else
                subscription = exchange.SubscribeToStreamingNotifications(SelectedFolders(), SelectedEvents());
            _subscriptions.Add(Mailbox, subscription);
            return subscription;
        }

        private void AddGroupSubscriptions(string sGroup)
        {
            if (!_groups.ContainsKey(sGroup))
                return;

            if (_connections.ContainsKey(sGroup))
            {
                foreach (StreamingSubscription subscription in _connections[sGroup].CurrentSubscriptions)
                {
                    try
                    {
                        subscription.Unsubscribe();
                    }
                    catch { }
                }
                try
                {
                    _connections[sGroup].Close();
                }
                catch { }
            }

            try
            {
                // Create the connection for this group, and the primary mailbox subscription
                GroupInfo group = _groups[sGroup];
                StreamingSubscription subscription = AddSubscription(group.PrimaryMailbox, group);

                if (_connections.ContainsKey(sGroup))
                {
                    _connections[sGroup] = new StreamingSubscriptionConnection(subscription.Service, (int)numericUpDownTimeout.Value);
                }
                else
                    _connections.Add(sGroup, new StreamingSubscriptionConnection(subscription.Service, (int)numericUpDownTimeout.Value));

                SubscribeConnectionEvents(_connections[sGroup]);
                _connections[sGroup].AddSubscription(subscription);
                _logger.Log(String.Format("{0} (primary mailbox) subscription created in group {1}", group.PrimaryMailbox, sGroup));

                // Now add any further subscriptions in this group
                foreach (string sMailbox in group.Mailboxes)
                {
                    if (!sMailbox.Equals(group.PrimaryMailbox))
                    {
                        try
                        {
                            subscription = AddSubscription(sMailbox, group);
                            _connections[sGroup].AddSubscription(subscription);
                            _logger.Log(String.Format("{0} subscription created in group {1}", sMailbox, sGroup));
                        }
                        catch (Exception ex)
                        {
                            _logger.Log(String.Format("ERROR when subscribing {0} in group {1}: {2}", sMailbox, sGroup, ex.Message));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Log(String.Format("ERROR when creating subscription connection group {0}: {1}", sGroup, ex.Message));
            }

        }

        private void AddAllSubscriptions()
        {
            foreach (string sGroup in _groups.Keys)
            {
                AddGroupSubscriptions(sGroup);
            }
        }

        private void CreateGroups()
        {
            // Go through all the mailboxes and organise into groups based on grouping information
            _groups = new Dictionary<string, GroupInfo>();  // Clear any existing groups
            _mailboxes.Credentials = new WebCredentials(textBoxUsername.Text, textBoxPassword.Text);

            foreach (string sMailbox in checkedListBoxMailboxes.CheckedItems)
            {
                _mailboxes.AddMailbox(sMailbox);
                MailboxInfo mailboxInfo = _mailboxes.Mailbox(sMailbox);
                if (mailboxInfo != null)
                {
                    GroupInfo groupInfo = null;
                    if (_groups.ContainsKey(mailboxInfo.GroupName))
                    {
                        groupInfo = _groups[mailboxInfo.GroupName];
                    }
                    else
                    {
                        groupInfo = new GroupInfo(mailboxInfo.GroupName, mailboxInfo.SMTPAddress, mailboxInfo.EwsUrl, _traceListener);
                        _groups.Add(mailboxInfo.GroupName, groupInfo);
                    }
                    if (groupInfo.Mailboxes.Count > 199)
                    {
                        // We already have enough mailboxes in this group, so we rename it and create a new one
                        // Renaming it means that we can still put new mailboxes into the correct group based on GroupingInformation
                        int i = 1;
                        while (_groups.ContainsKey(String.Format("{0}{1}", groupInfo.Name, i)))
                            i++;
                        _groups.Remove(groupInfo.Name);
                        _groups.Add(String.Format("{0}{1}", groupInfo.Name, i), groupInfo);
                        groupInfo = new GroupInfo(mailboxInfo.GroupName, mailboxInfo.SMTPAddress, mailboxInfo.EwsUrl, _traceListener);
                        _groups.Add(mailboxInfo.GroupName, groupInfo);
                    }

                    groupInfo.Mailboxes.Add(sMailbox);
                }
            }
        }

        private bool ConnectToSubscriptions()
        {
            AddAllSubscriptions();
            foreach (StreamingSubscriptionConnection connection in _connections.Values)
            {
                connection.Open();
            }
            timerMonitorConnections.Start();
            
            return true;
        }

        private void ReconnectToSubscriptions()
        {
            // Go through our connections and reconnect any that have closed
            _reconnect = false;
            lock (_reconnectLock)  // Prevent this code being run concurrently (i.e. if an event fires in the middle of the processing)
            {
                foreach (string sConnectionGroup in _connections.Keys)
                {
                    StreamingSubscriptionConnection connection = _connections[sConnectionGroup];
                    if (!connection.IsOpen)
                    {
                        try
                        {
                            try
                            {
                                connection.Open();
                                _logger.Log(String.Format("Re-opened connection for group {0}", sConnectionGroup));
                            }
                            catch (Exception ex)
                            {
                                if (ex.Message.StartsWith("You must add at least one subscription to this connection before it can be opened"))
                                {
                                    // Try recreating this group
                                    AddGroupSubscriptions(sConnectionGroup);
                                }
                                else
                                    _logger.Log(String.Format("Failed to reopen connection: {0}", ex.Message));

                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Log(String.Format("Failed to reopen connection: {0}", ex.Message));
                        }
                    }
                }
            }
        }

        private void CloseConnections()
        {
            foreach (StreamingSubscriptionConnection connection in _connections.Values)
            {
                if (connection.IsOpen) connection.Close();
            }
        }

        void _connection_OnSubscriptionError(object sender, SubscriptionErrorEventArgs args)
        {
            if (args.Exception == null)
                return;

            _logger.Log("Subscription error: " + args.Exception.Message);
        }

        void _connection_OnDisconnect(object sender, SubscriptionErrorEventArgs args)
        {
            // Subscription disconnected, so reconnect

            foreach (StreamingSubscriptionConnection connection in _connections.Values)
            {
                if (!connection.IsOpen)
                {
                    try
                    {
                        connection.Open();
                        _logger.Log("Reconnected");
                    }
                    catch (Exception ex)
                    {
                        _logger.Log("Failed to reconnect: " + ex.Message);
                    }
                }
            }
        }

        void _connection_OnNotificationEvent(object sender, NotificationEventArgs args)
        {
            foreach (NotificationEvent e in args.Events)
            {
                ProcessNotification(e, args.Subscription);
            }
        }

        private void comboBoxSubscribeTo_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (comboBoxSubscribeTo.SelectedIndex != 0) return;
        }

        private void checkBoxSelectAll_CheckedChanged(object sender, EventArgs e)
        {
            bool bChecked = true;
            if (checkBoxSelectAll.CheckState == CheckState.Unchecked)
                bChecked=false;

            for (int i = 0; i < checkedListBoxEvents.Items.Count; i++)
                checkedListBoxEvents.SetItemChecked(i, bChecked);
            if (bChecked)
            {
                checkBoxSelectAll.CheckState = CheckState.Checked;
            }
            else
                checkBoxSelectAll.CheckState = CheckState.Unchecked;
        }

        private void checkedListBoxEvents_ItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (checkedListBoxEvents.Items.Count != checkedListBoxEvents.SelectedItems.Count)
                checkBoxSelectAll.CheckState = CheckState.Indeterminate;
        }

        private void checkBoxQueryMore_CheckedChanged(object sender, EventArgs e)
        {
            checkBoxIncludeMime.Enabled = checkBoxQueryMore.Checked;
        }

        private void listBoxEvents_DoubleClick(object sender, EventArgs e)
        {
            try
            {
                string sInfo = listBoxEvents.SelectedItem.ToString();
                System.Windows.Forms.MessageBox.Show(sInfo, "Event", MessageBoxButtons.OK);
            }
            catch { }
        }

        private void UnsubscribeAll()
        {
            // Unsubscribe all
            if (_subscriptions == null)
                return;

            for (int i = _subscriptions.Keys.Count - 1; i>=0; i-- )
            {
                string sMailbox = _subscriptions.Keys.ElementAt<string>(i);
                StreamingSubscription subscription = _subscriptions[sMailbox];
                try
                {
                    subscription.Unsubscribe();
                    _logger.Log(String.Format("Unsubscribed from {0}", sMailbox));
                }
                catch (Exception ex)
                {
                    _logger.Log(String.Format("Error when unsubscribing {0}: {1}", sMailbox, ex.Message));
                }
                _subscriptions.Remove(sMailbox);
            }
        }

        private void buttonUnsubscribe_Click(object sender, EventArgs e)
        {
            timerMonitorConnections.Stop();
            CloseConnections();
            UnsubscribeAll();
            _reconnect = false;
            buttonUnsubscribe.Enabled = false;
            buttonSubscribe.Enabled = true;
        }


        private void buttonDeselectAllMailboxes_Click(object sender, EventArgs e)
        {
            for (int i = 0; i < checkedListBoxMailboxes.Items.Count; i++)
                checkedListBoxMailboxes.SetItemChecked(i, false);
        }

        private void buttonSelectAllMailboxes_Click(object sender, EventArgs e)
        {
            for (int i = 0; i < checkedListBoxMailboxes.Items.Count; i++)
                checkedListBoxMailboxes.SetItemChecked(i, true);
        }

        private void timerMonitorConnections_Tick(object sender, EventArgs e)
        {
            if (!_reconnect)
                return;

            timerMonitorConnections.Stop();
            ReconnectToSubscriptions();
            timerMonitorConnections.Start();
        }

        private void FormMain_FormClosing(object sender, FormClosingEventArgs e)
        {
            timerMonitorConnections.Stop();
            CloseConnections();
            UnsubscribeAll();
        }

        private void buttonLoadMailboxes_Click(object sender, EventArgs e)
        {
            OpenFileDialog oDialog = new OpenFileDialog();
            oDialog.Filter = "Text files (*.txt)|*.txt|All Files|*.*";
            oDialog.DefaultExt = "txt";
            oDialog.Title = "Select mailbox file";
            oDialog.CheckFileExists = true;
            if (oDialog.ShowDialog() != DialogResult.OK)
                return;

            ReadMailboxes(oDialog.FileName);
        }

        private void buttonEditMailboxes_Click(object sender, EventArgs e)
        {
            // Allow the list of mailboxes to be edited

            StringBuilder allMailboxes = new StringBuilder();
            foreach (object mbx in checkedListBoxMailboxes.Items)
            {
                allMailboxes.AppendLine(mbx.ToString());
            }
            FormEditMailboxes form = new FormEditMailboxes();
            string mailboxes = form.EditMailboxes(allMailboxes.ToString());

            // Were there any changes?
            if (mailboxes.Equals(allMailboxes.ToString()))
                return;

            checkedListBoxMailboxes.Items.Clear();
            string[] mbxList = mailboxes.Split(new string[]{Environment.NewLine},StringSplitOptions.RemoveEmptyEntries);
            foreach (string mbx in mbxList)
                checkedListBoxMailboxes.Items.Add(mbx);
        }

    }
}
