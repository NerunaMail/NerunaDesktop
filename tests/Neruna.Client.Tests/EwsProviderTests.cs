using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Neruna.Contracts.Discovery;
using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Calendar;
using Neruna.Core.Contacts;
using Neruna.Core.Mail;
using Neruna.Core.Providers;
using Neruna.Core.Security;
using Neruna.Providers.Ews;

namespace Neruna.Client.Tests;

/// <summary>
/// Exchange Web Services without an Exchange server: a fake that answers per operation with responses shaped as
/// documented for EWS (Exchange 2013 schema). Checks what is sent (fields, order-sensitive parts) and how answers are read.
/// </summary>
public class EwsProviderTests
{
    private static readonly XNamespace M = "http://schemas.microsoft.com/exchange/services/2006/messages";
    private static readonly XNamespace T = "http://schemas.microsoft.com/exchange/services/2006/types";

    private static (ServiceConnection Connection, EwsConnectionFactory Factory, FakeEws Server, MemoryCredentialStore Credentials) Connect(
        ServiceKind kind = ServiceKind.Mail, EwsAuthentication auth = EwsAuthentication.Automatic)
    {
        var server = new FakeEws();
        var credentials = new MemoryCredentialStore();
        var settings = new EwsSettings(new Uri("https://mail.example.com/EWS/Exchange.asmx"), "EXAMPLE\\anna", "anna@example.com", auth).ToDictionary();
        var connection = new ServiceConnection(Guid.NewGuid(), kind, ProviderIds.Ews, settings);
        credentials.Secrets[connection.Id] = "geheim";
        var factory = new EwsConnectionFactory(credentials, new ClientCertificates(credentials), NullLoggerFactory.Instance) { HandlerOverride = () => server };
        return (connection, factory, server, credentials);
    }

    [Fact]
    public void The_server_can_be_given_as_host_or_address()
    {
        Assert.Equal("https://mail.example.com/EWS/Exchange.asmx", EwsSettings.ParseServer("mail.example.com")!.AbsoluteUri);
        Assert.Equal("https://mail.example.com/EWS/Exchange.asmx", EwsSettings.ParseServer(" https://mail.example.com/ ")!.AbsoluteUri);
        Assert.Equal("https://proxy.example.com/owa-ews/Exchange.asmx", EwsSettings.ParseServer("https://proxy.example.com/owa-ews/Exchange.asmx")!.AbsoluteUri);
        Assert.Null(EwsSettings.ParseServer("ftp://mail.example.com"));
        Assert.Null(EwsSettings.ParseServer("  "));
    }

    [Fact]
    public void An_exchange_account_gets_mail_calendar_and_contacts_with_its_options()
    {
        var credentials = new MemoryCredentialStore();
        var factory = new EwsConnectionFactory(credentials, new ClientCertificates(credentials), NullLoggerFactory.Instance);
        var registry = new ProviderRegistry([new EwsMailProviderFactory(factory)], [new EwsCalendarProviderFactory(factory)], [new EwsContactProviderFactory(factory)]);
        var setup = new AccountSetupService(registry, null!, credentials, []);
        var config = new MailProviderConfig("example.com", null, [], [], [new DavServerSettings(ServerProtocol.Ews, new Uri("https://mail.example.com/EWS/Exchange.asmx"), "EXAMPLE\\anna")]);

        var account = setup.BuildAccount("Anna", "anna@example.com", config, options: new Dictionary<string, string>
        {
            [EwsSettings.AuthenticationKey] = nameof(EwsAuthentication.Basic),
            [TlsCertificates.ClientCertificateKey] = "ABCDEF",
        });

        Assert.Equal([ServiceKind.Mail, ServiceKind.Calendar, ServiceKind.Contacts], account.Connections.Select(c => c.Kind));
        Assert.All(account.Connections, c =>
        {
            Assert.Equal(ProviderIds.Ews, c.ProviderId);
            Assert.Equal(new EwsSettings(new Uri("https://mail.example.com/EWS/Exchange.asmx"), "EXAMPLE\\anna", "anna@example.com", EwsAuthentication.Basic), EwsSettings.FromDictionary(c.Settings));
            Assert.Equal("ABCDEF", TlsCertificates.ClientCertificateOf(c.Settings));
        });

        // "Konto bearbeiten" shows the address again; the account list says it uses a client certificate.
        Assert.Equal("https://mail.example.com/EWS/Exchange.asmx", registry.MailProviders.Single().DescribeSettings(account.Connections[0].Settings)!.Ews!.ServerUrl.AbsoluteUri);
        Assert.Equal("mail.example.com (EWS) · EXAMPLE\\anna · Client-Zertifikat", registry.Summary(account.Connections[0]));
    }

    [Fact]
    public async Task The_out_of_office_reply_is_read_and_set_with_its_period()
    {
        var ct = TestContext.Current.CancellationToken;
        var (connection, factory, server, _) = Connect();
        server.On("GetUserOofSettingsRequest", _ => """
            <m:GetUserOofSettingsResponse><m:ResponseMessage ResponseClass="Success"><m:ResponseCode>NoError</m:ResponseCode></m:ResponseMessage>
              <OofSettings xmlns="http://schemas.microsoft.com/exchange/services/2006/types"><OofState>Scheduled</OofState><ExternalAudience>All</ExternalAudience>
                <Duration><StartTime>2026-10-20T07:00:00Z</StartTime><EndTime>2026-10-27T07:00:00Z</EndTime></Duration>
                <InternalReply><Message>&lt;html&gt;&lt;body&gt;Bin in den Ferien.&lt;br&gt;Gruss Anna&lt;/body&gt;&lt;/html&gt;</Message></InternalReply>
                <ExternalReply><Message/></ExternalReply></OofSettings>
              <m:AllowExternalOof>All</m:AllowExternalOof></m:GetUserOofSettingsResponse>
            """);
        server.On("SetUserOofSettingsRequest", _ => """<m:SetUserOofSettingsResponse><m:ResponseMessage ResponseClass="Success"><m:ResponseCode>NoError</m:ResponseCode></m:ResponseMessage></m:SetUserOofSettingsResponse>""");
        await using var mail = new EwsMailProviderFactory(factory).Create(connection);
        var replies = Assert.IsAssignableFrom<IAutoReplyProvider>(mail);

        var (reply, features) = await replies.GetAutoReplyAsync(ct);
        Assert.True(reply.IsEnabled);
        Assert.Contains("Bin in den Ferien.", reply.Message, StringComparison.Ordinal);
        Assert.Equal(new DateTimeOffset(2026, 10, 27, 7, 0, 0, TimeSpan.Zero), reply.End);
        Assert.True(features.HasFlag(AutoReplyFeatures.Schedule));

        // Set with a period: Exchange keeps it ("Scheduled"); the text goes as HTML with its line breaks, to everyone.
        await replies.SetAutoReplyAsync(new AutoReply(true, "Bis 27. Oktober weg.\nDringend: Lea", new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 27, 0, 0, 0, TimeSpan.Zero)), [], ct);
        var set = server.Operations("SetUserOofSettingsRequest").Single();
        Assert.Equal(["OofState", "ExternalAudience", "Duration", "InternalReply", "ExternalReply"], set.Descendants(T + "UserOofSettings").Single().Elements().Select(e => e.Name.LocalName));
        Assert.Equal(("Scheduled", "All", "2026-10-20T00:00:00Z"), (set.Descendants(T + "OofState").Single().Value, set.Descendants(T + "ExternalAudience").Single().Value, set.Descendants(T + "StartTime").Single().Value));
        Assert.Contains("weg.<br>Dringend: Lea", set.Descendants(T + "InternalReply").Single().Value, StringComparison.Ordinal);

        await replies.SetAutoReplyAsync(AutoReply.Off, [], ct);
        Assert.Equal("Disabled", server.Operations("SetUserOofSettingsRequest").Last().Descendants(T + "OofState").Single().Value);
        Assert.Empty(server.Operations("SetUserOofSettingsRequest").Last().Descendants(T + "Duration"));
    }

    [Fact]
    public async Task Inbox_rules_are_read_and_changed_while_Outlooks_own_stay()
    {
        var ct = TestContext.Current.CancellationToken;
        var (connection, factory, server, _) = Connect();
        server.On("GetInboxRules", _ => """
            <m:GetInboxRulesResponse ResponseClass="Success"><m:ResponseCode>NoError</m:ResponseCode><m:OutlookRuleBlobExists>false</m:OutlookRuleBlobExists>
              <m:InboxRules>
                <t:Rule><t:RuleId>R1</t:RuleId><t:DisplayName>Rechnungen</t:DisplayName><t:Priority>1</t:Priority><t:IsEnabled>true</t:IsEnabled>
                  <t:Conditions><t:ContainsSenderStrings><t:String>rechnung@lieferant.ch</t:String></t:ContainsSenderStrings></t:Conditions><t:Exceptions/>
                  <t:Actions><t:MarkAsRead>true</t:MarkAsRead><t:MoveToFolder><t:FolderId Id="F-RECH"/></t:MoveToFolder></t:Actions></t:Rule>
                <t:Rule><t:RuleId>R2</t:RuleId><t:DisplayName>Wichtig</t:DisplayName><t:Priority>2</t:Priority><t:IsEnabled>true</t:IsEnabled>
                  <t:Conditions><t:ImportanceIs>High</t:ImportanceIs></t:Conditions><t:Exceptions/>
                  <t:Actions><t:AssignCategories><t:String>Rot</t:String></t:AssignCategories></t:Actions></t:Rule>
              </m:InboxRules></m:GetInboxRulesResponse>
            """);
        server.On("UpdateInboxRules", _ => """<m:UpdateInboxRulesResponse ResponseClass="Success"><m:ResponseCode>NoError</m:ResponseCode></m:UpdateInboxRulesResponse>""");
        await using var mail = new EwsMailProviderFactory(factory).Create(connection);
        var rules = Assert.IsAssignableFrom<IServerRulesProvider>(mail);

        var set = await rules.GetRulesAsync(ct);
        var invoice = Assert.Single(set.Rules);
        Assert.Equal(("R1", "Rechnungen", "F-RECH", true), (invoice.Id, invoice.Name, invoice.MoveTo, invoice.MarkRead));
        Assert.Equal([new RuleCondition(RuleField.From, "rechnung@lieferant.ch")], invoice.Conditions);
        Assert.Equal(["Wichtig"], set.Others); // categories and importance: Outlook's, kept
        Assert.False(set.Features.HasFlag(ServerRuleFeatures.Flag));

        // R1 changed, a new one added; Outlook's moves behind them.
        await rules.SaveRulesAsync([invoice with { IsEnabled = false }, new ServerRule(null, "Chef", true, [new RuleCondition(RuleField.Recipient, "chef@firma.ch")], ForwardTo: "anna@privat.ch", Stop: true)], ct);
        var update = server.Operations("UpdateInboxRules").Single();
        Assert.Equal(["SetRuleOperation", "CreateRuleOperation", "SetRuleOperation"], update.Descendants(M + "Operations").Single().Elements().Select(e => e.Name.LocalName));
        var created = update.Descendants(T + "CreateRuleOperation").Single().Element(T + "Rule")!;
        Assert.Equal(["DisplayName", "Priority", "IsEnabled", "Conditions", "Exceptions", "Actions"], created.Elements().Select(e => e.Name.LocalName));
        Assert.Equal(["ForwardToRecipients", "StopProcessingRules"], created.Element(T + "Actions")!.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("anna@privat.ch", created.Descendants(T + "EmailAddress").Single().Value);
        Assert.Equal("false", update.Descendants(M + "RemoveOutlookRuleBlob").Single().Value); // Outlook's own rules are never thrown away
        var moved = update.Descendants(T + "SetRuleOperation").Last().Element(T + "Rule")!;
        Assert.Equal(("R2", "3"), (moved.Element(T + "RuleId")!.Value, moved.Element(T + "Priority")!.Value));
    }

    [Fact]
    public async Task Folders_have_their_roles_and_only_visible_mail_folders_are_listed()
    {
        var ct = TestContext.Current.CancellationToken;
        var (connection, factory, server, _) = Connect();
        server.On("GetFolder", op => op.Descendants(T + "DistinguishedFolderId").Any(d => d.Attribute("Id")!.Value == "msgfolderroot")
            ? Ok("GetFolder", "<m:Folders><t:Folder><t:FolderId Id=\"ROOT\"/></t:Folder></m:Folders>")
            : Many("GetFolder", op.Descendants(T + "DistinguishedFolderId").Select(d => d.Attribute("Id")!.Value switch
            {
                "inbox" => Message("GetFolder", "<m:Folders><t:Folder><t:FolderId Id=\"INBOX\"/></t:Folder></m:Folders>"),
                "sentitems" => Message("GetFolder", "<m:Folders><t:Folder><t:FolderId Id=\"SENT\"/></t:Folder></m:Folders>"),
                _ => Error("GetFolder", "ErrorFolderNotFound"),
            })));
        server.On("FindFolder", _ => Ok("FindFolder", """
            <m:RootFolder IndexedPagingOffset="6" TotalItemsInView="6" IncludesLastItemInRange="true"><t:Folders>
              <t:Folder><t:FolderId Id="INBOX"/><t:ParentFolderId Id="ROOT"/><t:FolderClass>IPF.Note</t:FolderClass><t:DisplayName>Posteingang</t:DisplayName><t:TotalCount>3</t:TotalCount><t:UnreadCount>1</t:UnreadCount></t:Folder>
              <t:Folder><t:FolderId Id="PROJ"/><t:ParentFolderId Id="INBOX"/><t:FolderClass>IPF.Note</t:FolderClass><t:DisplayName>Projekte</t:DisplayName></t:Folder>
              <t:Folder><t:FolderId Id="SENT"/><t:ParentFolderId Id="ROOT"/><t:FolderClass>IPF.Note</t:FolderClass><t:DisplayName>Gesendete Elemente</t:DisplayName></t:Folder>
              <t:Folder><t:FolderId Id="ARCH"/><t:ParentFolderId Id="ROOT"/><t:DisplayName>Archiv</t:DisplayName></t:Folder>
              <t:CalendarFolder><t:FolderId Id="CAL"/><t:ParentFolderId Id="ROOT"/><t:FolderClass>IPF.Appointment</t:FolderClass><t:DisplayName>Kalender</t:DisplayName></t:CalendarFolder>
              <t:Folder><t:FolderId Id="HIDDEN"/><t:ParentFolderId Id="ROOT"/><t:FolderClass>IPF.Note</t:FolderClass><t:DisplayName>Sync</t:DisplayName>
                <t:ExtendedProperty><t:ExtendedFieldURI PropertyTag="0x10f4" PropertyType="Boolean"/><t:Value>true</t:Value></t:ExtendedProperty></t:Folder>
              <t:Folder><t:FolderId Id="UNDERHIDDEN"/><t:ParentFolderId Id="HIDDEN"/><t:FolderClass>IPF.Note</t:FolderClass><t:DisplayName>Darunter</t:DisplayName></t:Folder>
            </t:Folders></m:RootFolder>
            """));

        await using var mail = new EwsMailProviderFactory(factory).Create(connection);
        var folders = await mail.GetFoldersAsync(ct);

        Assert.Equal(["Posteingang", "Projekte", "Gesendete Elemente", "Archiv"], folders.Select(f => f.Name));
        Assert.Equal([FolderRole.Inbox, FolderRole.None, FolderRole.Sent, FolderRole.Archive], folders.Select(f => f.Role));
        Assert.Equal([null, "INBOX", null, null], folders.Select(f => f.ParentRemoteId));
        Assert.Equal((3, 1), (folders[0].TotalCount, folders[0].UnreadCount));

        // The deep search starts at the mailbox root and asks for the hidden flag.
        var find = server.Operations("FindFolder").Single();
        Assert.Equal("Deep", find.Attribute("Traversal")!.Value);
        Assert.Contains(find.Descendants(T + "ExtendedFieldURI"), e => e.Attribute("PropertyTag")!.Value == "0x10F4");
    }

    [Fact]
    public async Task First_sync_takes_the_newest_messages_and_later_ones_bring_only_the_changes()
    {
        var ct = TestContext.Current.CancellationToken;
        var (connection, factory, server, _) = Connect();
        server.On("SyncFolderItems", op => op.Element(M + "SyncState") is null
            ? Ok("SyncFolderItems", "<m:SyncState>STATE-1</m:SyncState><m:IncludesLastItemInRange>true</m:IncludesLastItemInRange><m:Changes><t:Create><t:Message><t:ItemId Id=\"M1\" ChangeKey=\"a\"/></t:Message></t:Create></m:Changes>")
            : Ok("SyncFolderItems", """
                <m:SyncState>STATE-2</m:SyncState><m:IncludesLastItemInRange>true</m:IncludesLastItemInRange><m:Changes>
                  <t:Create><t:Message><t:ItemId Id="M3" ChangeKey="c"/></t:Message></t:Create>
                  <t:ReadFlagChange><t:ItemId Id="M1" ChangeKey="a2"/><t:IsRead>true</t:IsRead></t:ReadFlagChange>
                  <t:Update><t:Message><t:ItemId Id="OLD" ChangeKey="x"/></t:Message></t:Update>
                  <t:Delete><t:ItemId Id="M2" ChangeKey="b"/></t:Delete>
                </m:Changes>
                """));
        server.On("FindItem", _ => Ok("FindItem", """
            <m:RootFolder IndexedPagingOffset="2" TotalItemsInView="2" IncludesLastItemInRange="true"><t:Items>
              <t:Message><t:ItemId Id="M1" ChangeKey="a"/></t:Message><t:Message><t:ItemId Id="M2" ChangeKey="b"/></t:Message>
            </t:Items></m:RootFolder>
            """));
        server.On("GetItem", op => Many("GetItem", op.Descendants(T + "ItemId").Select(i => i.Attribute("Id")!.Value switch
        {
            "M1" => Message("GetItem", """
                <m:Items><t:Message><t:ItemId Id="M1" ChangeKey="a2"/><t:ItemClass>IPM.Note.SMIME.MultipartSigned</t:ItemClass><t:Subject>Offerte</t:Subject>
                  <t:DateTimeReceived>2026-10-09T08:00:00Z</t:DateTimeReceived><t:Size>2048</t:Size><t:HasAttachments>true</t:HasAttachments><t:IsDraft>false</t:IsDraft>
                  <t:Preview>Anbei die Offerte</t:Preview><t:Flag><t:FlagStatus>Flagged</t:FlagStatus></t:Flag>
                  <t:ExtendedProperty><t:ExtendedFieldURI PropertyTag="0x1081" PropertyType="Integer"/><t:Value>102</t:Value></t:ExtendedProperty>
                  <t:ToRecipients><t:Mailbox><t:Name>Anna</t:Name><t:EmailAddress>anna@example.com</t:EmailAddress><t:RoutingType>SMTP</t:RoutingType></t:Mailbox>
                    <t:Mailbox><t:Name>Intern</t:Name><t:EmailAddress>/O=EXAMPLE/OU=EXCHANGE/CN=RECIPIENTS/CN=BEAT</t:EmailAddress><t:RoutingType>EX</t:RoutingType></t:Mailbox></t:ToRecipients>
                  <t:From><t:Mailbox><t:Name>Marco</t:Name><t:EmailAddress>marco@example.com</t:EmailAddress><t:RoutingType>SMTP</t:RoutingType></t:Mailbox></t:From>
                  <t:InternetMessageId>&lt;1@example.com&gt;</t:InternetMessageId><t:IsRead>true</t:IsRead></t:Message></m:Items>
                """),
            "M2" when op.Descendants(T + "BodyType").Any(b => b.Value == "Text") => Message("GetItem",
                "<m:Items><t:Message><t:ItemId Id=\"M2\" ChangeKey=\"b\"/><t:Body BodyType=\"Text\">Grüezi mitenand</t:Body><t:Attachments><t:FileAttachment><t:AttachmentId Id=\"A1\"/><t:Name>Plan.pdf</t:Name></t:FileAttachment></t:Attachments></t:Message></m:Items>"),
            "M2" when op.Descendants(T + "IncludeMimeContent").Any() => Message("GetItem",
                $"<m:Items><t:Message><t:ItemId Id=\"M2\" ChangeKey=\"b\"/><t:MimeContent CharacterSet=\"UTF-8\">{Convert.ToBase64String(Encoding.UTF8.GetBytes("Subject: Hallo\r\nFrom: lea@example.com\r\n\r\nGrüezi"))}</t:MimeContent></t:Message></m:Items>"),
            "M2" => Message("GetItem", "<m:Items><t:Message><t:ItemId Id=\"M2\" ChangeKey=\"b\"/><t:Subject>Hallo</t:Subject><t:DateTimeReceived>2026-10-08T08:00:00Z</t:DateTimeReceived><t:IsRead>false</t:IsRead></t:Message></m:Items>"),
            "M3" => Message("GetItem", "<m:Items><t:MeetingRequest><t:ItemId Id=\"M3\" ChangeKey=\"c\"/><t:Subject>Sitzung</t:Subject><t:DateTimeReceived>2026-10-10T08:00:00Z</t:DateTimeReceived><t:IsRead>false</t:IsRead></t:MeetingRequest></m:Items>"),
            _ => Error("GetItem", "ErrorItemNotFound"),
        })));
        server.On("GetFolder", _ => Ok("GetFolder", "<m:Folders><t:Folder><t:FolderId Id=\"INBOX\"/><t:TotalCount>2</t:TotalCount><t:UnreadCount>1</t:UnreadCount></t:Folder></m:Folders>"));

        await using var mail = new EwsMailProviderFactory(factory).Create(connection);
        var inbox = new MailFolder(connection.Id, "INBOX", "Posteingang", null, FolderRole.Inbox);
        var first = await mail.SyncFolderAsync(inbox, [], ct);

        Assert.True(first.IsFullResync);
        Assert.Equal("STATE-1", first.NewSyncState);
        Assert.Equal((2, 1), (first.TotalCount, first.UnreadCount));
        var offer = first.AddedOrChanged.Single(m => m.RemoteId == "M1");
        Assert.Equal(("Offerte", "marco@example.com", "<1@example.com>", "Anbei die Offerte"), (offer.Subject, offer.From!.Address, offer.MessageId, offer.Preview));
        Assert.Equal(MessageFlags.Seen | MessageFlags.Flagged | MessageFlags.Answered, offer.Flags);
        Assert.Equal((true, 2048L, MessageSecurity.Signed), (offer.HasAttachments, offer.Size, offer.Security));
        Assert.Equal(["anna@example.com"], offer.To.Select(t => t.Address)); // the X.500 address is no mail address

        // The whole message as MIME (only the MIME content is asked for – no empty list of further properties).
        var opened = await mail.GetMessageAsync(inbox, "M2", ct);
        Assert.Equal(("Hallo", "Grüezi"), (opened.Subject, opened.TextBody?.Trim()));

        // For the search index: the body as text (converted by Exchange) and the attachments' names, no content.
        var texts = await mail.GetSearchTextsAsync(inbox, ["M2"], ct);
        Assert.Equal(new MessageSearchText("Grüezi mitenand", "Plan.pdf"), Assert.Single(texts).Value);
        Assert.True(mail.Capabilities.HasFlag(MailProviderCapabilities.SearchText));

        // The newest first, by received date.
        var find = server.Operations("FindItem").Single();
        Assert.Equal("Descending", find.Descendants(T + "FieldOrder").Single().Attribute("Order")!.Value);

        var second = await mail.SyncFolderAsync(inbox with { SyncState = first.NewSyncState }, ["M1", "M2"], ct);
        Assert.False(second.IsFullResync);
        Assert.Equal("STATE-2", second.NewSyncState);
        Assert.Equal(["M3"], second.AddedOrChanged.Select(m => m.RemoteId)); // a meeting request is mail too
        Assert.Equal(MessageFlags.Seen | MessageFlags.Flagged | MessageFlags.Answered, second.FlagUpdates["M1"]);
        Assert.Equal(["M2"], second.RemovedRemoteIds);
        Assert.Equal("STATE-1", server.Operations("SyncFolderItems").Last().Element(M + "SyncState")!.Value);

        // A forgotten sync state: everything again.
        server.On("SyncFolderItems", op => op.Element(M + "SyncState") is null
            ? Ok("SyncFolderItems", "<m:SyncState>STATE-NEW</m:SyncState><m:IncludesLastItemInRange>true</m:IncludesLastItemInRange><m:Changes/>")
            : Many("SyncFolderItems", [Error("SyncFolderItems", "ErrorInvalidSyncStateData")]));
        var again = await mail.SyncFolderAsync(inbox with { SyncState = "STATE-2" }, ["M1", "M2", "M3"], ct);
        Assert.True(again.IsFullResync);
        Assert.Equal("STATE-NEW", again.NewSyncState);
        Assert.Equal(["M3"], again.RemovedRemoteIds);
    }

    [Fact]
    public async Task Flags_sending_and_storing_mail_send_what_exchange_expects()
    {
        var ct = TestContext.Current.CancellationToken;
        var (connection, factory, server, _) = Connect();
        server.On("UpdateItem", op => Many("UpdateItem", op.Descendants(T + "ItemChange").Select(_ => Message("UpdateItem", "<m:Items/>"))));
        server.On("CreateItem", _ => Ok("CreateItem", "<m:Items/>"));
        await using var mail = new EwsMailProviderFactory(factory).Create(connection);
        var inbox = new MailFolder(connection.Id, "INBOX", "Posteingang", null, FolderRole.Inbox);

        await mail.SetFlagsAsync(inbox, ["M1", "M2"], MessageFlags.Seen | MessageFlags.Flagged | MessageFlags.Forwarded, add: true, ct);
        var update = server.Operations("UpdateItem").Single();
        Assert.Equal("true", update.Attribute("SuppressReadReceipts")!.Value);
        Assert.Equal(2, update.Descendants(T + "ItemChange").Count());
        var fields = update.Descendants(T + "ItemChange").First().Descendants(T + "SetItemField").ToList();
        Assert.Equal("true", fields[0].Descendants(T + "IsRead").Single().Value);
        Assert.Equal("Flagged", fields[1].Descendants(T + "FlagStatus").Single().Value);
        Assert.Equal(["104", "262"], fields.Skip(2).Select(f => f.Descendants(T + "Value").Single().Value));

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Anna", "anna@example.com"));
        message.To.Add(new MailboxAddress("Marco", "marco@example.com"));
        message.Bcc.Add(new MailboxAddress("Chefin", "chefin@example.com"));
        message.Subject = "Offerte";
        message.Body = new TextPart("plain") { Text = "Hallo" };
        await mail.SendAsync(message, ct);
        var send = server.Operations("CreateItem").Single();
        Assert.Equal("SendAndSaveCopy", send.Attribute("MessageDisposition")!.Value);
        Assert.Equal("sentitems", send.Element(M + "SavedItemFolderId")!.Elements().Single().Attribute("Id")!.Value);
        var sent = send.Descendants(T + "Message").Single().Elements().ToList();
        Assert.Equal(["MimeContent", "BccRecipients"], sent.Select(e => e.Name.LocalName));
        Assert.Contains("Subject: Offerte", Encoding.UTF8.GetString(Convert.FromBase64String(sent[0].Value)), StringComparison.Ordinal);
        Assert.Equal("chefin@example.com", sent[1].Descendants(T + "EmailAddress").Single().Value);

        // Stored as read, not as a draft, with its received date.
        await mail.AppendAsync(new MailFolder(connection.Id, "ARCH", "Archiv", null, FolderRole.Archive), message, MessageFlags.Seen, new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero), ct);
        var stored = server.Operations("CreateItem").Last();
        Assert.Equal("SaveOnly", stored.Attribute("MessageDisposition")!.Value);
        Assert.Equal(["MimeContent", "ExtendedProperty", "ExtendedProperty", "IsRead"], stored.Descendants(T + "Message").Single().Elements().Select(e => e.Name.LocalName));
        Assert.Equal(["1", "2026-10-01T08:00:00Z", "true"], stored.Descendants(T + "Message").Single().Elements().Skip(1).Select(e => e.Element(T + "Value")?.Value ?? e.Value));
    }

    [Fact]
    public async Task Errors_are_told_plainly_and_exchange_is_asked_again_when_busy()
    {
        var ct = TestContext.Current.CancellationToken;
        var (connection, factory, server, _) = Connect();
        await using var mail = new EwsMailProviderFactory(factory).Create(connection);

        server.Raw = (HttpStatusCode.Unauthorized, "");
        var denied = await Assert.ThrowsAsync<EwsException>(() => mail.TestConnectionAsync(ct));
        Assert.Contains("Benutzername oder Passwort", denied.Message, StringComparison.Ordinal);

        server.Raw = (HttpStatusCode.BadRequest, "<html><body>400 No required SSL certificate was sent</body></html>");
        Assert.Equal("ClientCertificate", (await Assert.ThrowsAsync<EwsException>(() => mail.TestConnectionAsync(ct))).Code);

        // Busy: once more after the pause Exchange names.
        server.Raw = (HttpStatusCode.InternalServerError, """
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><s:Fault><faultcode>a:ErrorServerBusy</faultcode><faultstring>The server cannot service this request right now.</faultstring>
              <detail><e:ResponseCode xmlns:e="http://schemas.microsoft.com/exchange/services/2006/errors">ErrorServerBusy</e:ResponseCode>
              <e:MessageXml xmlns:e="http://schemas.microsoft.com/exchange/services/2006/errors"><t:Value xmlns:t="http://schemas.microsoft.com/exchange/services/2006/types" Name="BackOffMilliseconds">10</t:Value></e:MessageXml></detail></s:Fault></s:Body></s:Envelope>
            """);
        server.RawOnce = true;
        server.On("GetFolder", _ => Ok("GetFolder", "<m:Folders><t:Folder><t:FolderId Id=\"INBOX\"/></t:Folder></m:Folders>"));
        var before = server.Requests;
        await mail.TestConnectionAsync(ct);
        Assert.Equal(before + 2, server.Requests);

        // A changed item is a conflict, not an error.
        var (calendarConnection, calendarFactory, calendarServer, _) = Connect(ServiceKind.Calendar);
        calendarServer.On("GetItem", _ => Ok("GetItem", "<m:Items><t:CalendarItem><t:ItemId Id=\"E1\" ChangeKey=\"new\"/><t:Subject>X</t:Subject><t:Start>2026-10-14T08:00:00Z</t:Start><t:End>2026-10-14T09:00:00Z</t:End></t:CalendarItem></m:Items>"));
        await using var calendar = new EwsCalendarProviderFactory(calendarFactory).Create(calendarConnection);
        await Assert.ThrowsAsync<RemoteConflictException>(() => calendar.SaveAsync(new CalendarInfo(calendarConnection.Id, "CAL", "Kalender", null, false),
            new CalendarObject("E1", "old", EventIcs("Neu")), ct));
    }

    [Fact]
    public async Task Basic_is_sent_right_away_and_a_certificate_alone_sends_no_password()
    {
        var ct = TestContext.Current.CancellationToken;
        var (connection, factory, server, _) = Connect(auth: EwsAuthentication.Basic);
        server.On("GetFolder", _ => Ok("GetFolder", "<m:Folders><t:Folder><t:FolderId Id=\"INBOX\"/></t:Folder></m:Folders>"));
        await using (var mail = new EwsMailProviderFactory(factory).Create(connection))
        {
            await mail.TestConnectionAsync(ct);
        }

        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("EXAMPLE\\anna:geheim")), server.Authorization);

        (connection, factory, server, _) = Connect(auth: EwsAuthentication.CertificateOnly);
        server.On("GetFolder", _ => Ok("GetFolder", "<m:Folders><t:Folder><t:FolderId Id=\"INBOX\"/></t:Folder></m:Folders>"));
        await using (var mail = new EwsMailProviderFactory(factory).Create(connection))
        {
            await mail.TestConnectionAsync(ct);
        }

        Assert.Null(server.Authorization);
    }

    [Fact]
    public void A_series_with_a_moved_and_a_removed_occurrence_becomes_icalendar()
    {
        var item = Item("""
            <t:CalendarItem><t:ItemId Id="E1" ChangeKey="k1"/><t:Subject>Teamsitzung</t:Subject><t:Body BodyType="Text">Agenda folgt</t:Body>
              <t:ReminderIsSet>true</t:ReminderIsSet><t:ReminderMinutesBeforeStart>10</t:ReminderMinutesBeforeStart>
              <t:UID>040000008200E00074C5B7101A82E008</t:UID><t:Start>2026-10-05T07:00:00Z</t:Start><t:End>2026-10-05T08:00:00Z</t:End>
              <t:IsAllDayEvent>false</t:IsAllDayEvent><t:LegacyFreeBusyStatus>Busy</t:LegacyFreeBusyStatus><t:Location>Sitzungszimmer</t:Location>
              <t:IsMeeting>true</t:IsMeeting><t:IsCancelled>false</t:IsCancelled><t:MyResponseType>Accept</t:MyResponseType>
              <t:Organizer><t:Mailbox><t:Name>Marco</t:Name><t:EmailAddress>marco@example.com</t:EmailAddress><t:RoutingType>SMTP</t:RoutingType></t:Mailbox></t:Organizer>
              <t:RequiredAttendees><t:Attendee><t:Mailbox><t:Name>Anna</t:Name><t:EmailAddress>anna@example.com</t:EmailAddress><t:RoutingType>SMTP</t:RoutingType></t:Mailbox><t:ResponseType>Unknown</t:ResponseType></t:Attendee></t:RequiredAttendees>
              <t:OptionalAttendees><t:Attendee><t:Mailbox><t:Name>Beat</t:Name><t:EmailAddress>beat@example.com</t:EmailAddress><t:RoutingType>SMTP</t:RoutingType></t:Mailbox><t:ResponseType>Tentative</t:ResponseType></t:Attendee></t:OptionalAttendees>
              <t:Recurrence><t:WeeklyRecurrence><t:Interval>1</t:Interval><t:DaysOfWeek>Monday Wednesday</t:DaysOfWeek><t:FirstDayOfWeek>Monday</t:FirstDayOfWeek></t:WeeklyRecurrence>
                <t:EndDateRecurrence><t:StartDate>2026-10-05+02:00</t:StartDate><t:EndDate>2026-12-21+01:00</t:EndDate></t:EndDateRecurrence></t:Recurrence>
              <t:ModifiedOccurrences><t:Occurrence><t:ItemId Id="OCC1"/><t:Start>2026-10-07T12:00:00Z</t:Start><t:End>2026-10-07T13:00:00Z</t:End><t:OriginalStart>2026-10-07T07:00:00Z</t:OriginalStart></t:Occurrence></t:ModifiedOccurrences>
              <t:DeletedOccurrences><t:DeletedOccurrence><t:Start>2026-10-12T07:00:00Z</t:Start></t:DeletedOccurrence></t:DeletedOccurrences>
              <t:StartTimeZone Id="W. Europe Standard Time" Name="(UTC+01:00) Amsterdam, Berlin, Bern"/>
            </t:CalendarItem>
            """);
        var moved = Item("""
            <t:CalendarItem><t:ItemId Id="OCC1"/><t:Subject>Teamsitzung (verschoben)</t:Subject><t:Start>2026-10-07T12:00:00Z</t:Start><t:End>2026-10-07T13:00:00Z</t:End>
              <t:OriginalStart>2026-10-07T07:00:00Z</t:OriginalStart><t:StartTimeZone Id="W. Europe Standard Time"/></t:CalendarItem>
            """);

        var ics = EwsCalendarConverter.ToICalendar(item, [moved], "anna@example.com");
        var calendar = Ical.Net.Calendar.Load(ics)!;
        var master = calendar.Events.Single(e => e.RecurrenceIdentifier is null);
        Assert.Equal(("040000008200E00074C5B7101A82E008", "Teamsitzung", "Sitzungszimmer", "Agenda folgt"), (master.Uid, master.Summary, master.Location, master.Description));
        Assert.Equal((9, 0, "Europe/Berlin"), (master.DtStart!.Hour, master.DtStart.Minute, master.DtStart.TzId));
        Assert.Contains("FREQ=WEEKLY", ics, StringComparison.Ordinal);
        Assert.Contains("BYDAY=MO,WE", ics, StringComparison.Ordinal);
        Assert.Contains("UNTIL=20261221T225959Z", ics, StringComparison.Ordinal);
        Assert.Contains("EXDATE;TZID=Europe/Berlin:20261012T090000", ics, StringComparison.Ordinal);
        Assert.Equal("mailto:marco@example.com", master.Organizer!.Value!.OriginalString);
        Assert.Equal([("anna@example.com", "ACCEPTED", "REQ-PARTICIPANT"), ("beat@example.com", "TENTATIVE", "OPT-PARTICIPANT")],
            master.Attendees.Select(a => (EventDraft.AddressOf(a.Value)!, a.ParticipationStatus, a.Role)));
        Assert.Equal(10, EventDraft.FromICalendar(ics).ReminderMinutes);

        var exception = calendar.Events.Single(e => e.RecurrenceIdentifier is not null);
        Assert.Equal((master.Uid, "Teamsitzung (verschoben)", 14), (exception.Uid, exception.Summary, exception.DtStart!.Hour));
        Assert.Equal((2026, 10, 7, 9), (exception.RecurrenceIdentifier!.StartTime.Year, exception.RecurrenceIdentifier.StartTime.Month, exception.RecurrenceIdentifier.StartTime.Day, exception.RecurrenceIdentifier.StartTime.Hour));

        // All-day: the date in the item's zone, not in UTC.
        var holiday = EwsCalendarConverter.ToICalendar(Item("""
            <t:CalendarItem><t:ItemId Id="E2"/><t:Subject>Ferien</t:Subject><t:Start>2026-10-11T22:00:00Z</t:Start><t:End>2026-10-13T22:00:00Z</t:End>
              <t:IsAllDayEvent>true</t:IsAllDayEvent><t:LegacyFreeBusyStatus>Free</t:LegacyFreeBusyStatus><t:StartTimeZone Id="W. Europe Standard Time"/></t:CalendarItem>
            """), [], null);
        Assert.Contains("DTSTART;VALUE=DATE:20261012", holiday, StringComparison.Ordinal);
        Assert.Contains("DTEND;VALUE=DATE:20261014", holiday, StringComparison.Ordinal);
        Assert.Contains("TRANSP:TRANSPARENT", holiday, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Saving_an_event_sends_only_what_changed_and_answers_invitations_through_exchange()
    {
        var ct = TestContext.Current.CancellationToken;
        var (connection, factory, server, _) = Connect(ServiceKind.Calendar);
        var calendarInfo = new CalendarInfo(connection.Id, "CAL", "Kalender", null, false);
        var current = """
            <t:CalendarItem><t:ItemId Id="E1" ChangeKey="k1"/><t:Subject>Planung</t:Subject><t:Body BodyType="Text"></t:Body>
              <t:ReminderIsSet>true</t:ReminderIsSet><t:ReminderMinutesBeforeStart>15</t:ReminderMinutesBeforeStart><t:UID>U1</t:UID>
              <t:Start>2026-10-14T08:00:00Z</t:Start><t:End>2026-10-14T09:00:00Z</t:End><t:IsAllDayEvent>false</t:IsAllDayEvent>
              <t:IsMeeting>false</t:IsMeeting><t:MyResponseType>Organizer</t:MyResponseType><t:StartTimeZone Id="W. Europe Standard Time"/></t:CalendarItem>
            """;
        server.On("GetItem", _ => Ok("GetItem", "<m:Items>" + current + "</m:Items>"));
        server.On("UpdateItem", _ => Ok("UpdateItem", "<m:Items/>"));
        await using var calendar = new EwsCalendarProviderFactory(factory).Create(connection);

        // Read as Neruna stores it, renamed in the editor, saved: one field.
        var stored = (await calendar.SyncCalendarAsync(calendarInfo, new Dictionary<string, string?>(), ct)).AddedOrChanged;
        Assert.Empty(stored); // FindItem was not answered: nothing listed

        server.On("FindItem", _ => Ok("FindItem", "<m:RootFolder IncludesLastItemInRange=\"true\"><t:Items><t:CalendarItem><t:ItemId Id=\"E1\" ChangeKey=\"k1\"/></t:CalendarItem></t:Items></m:RootFolder>"));
        var item = Assert.Single((await calendar.SyncCalendarAsync(calendarInfo, new Dictionary<string, string?>(), ct)).AddedOrChanged);
        Assert.Equal(("E1", "k1"), (item.RemoteId, item.ETag));
        Assert.Empty((await calendar.SyncCalendarAsync(calendarInfo, new Dictionary<string, string?> { ["E1"] = "k1" }, ct)).AddedOrChanged);

        var draft = EventDraft.FromICalendar(item.ICalendarData) with { Summary = "Planung 2027" };
        await calendar.SaveAsync(calendarInfo, item with { ICalendarData = draft.ToICalendar(item.ICalendarData) }, ct);
        var update = server.Operations("UpdateItem").Single();
        Assert.Equal(("NeverOverwrite", "SendToNone", "k1"), (update.Attribute("ConflictResolution")!.Value, update.Attribute("SendMeetingInvitationsOrCancellations")!.Value,
            update.Descendants(T + "ItemId").Single().Attribute("ChangeKey")!.Value));
        Assert.Equal(["item:Subject"], update.Descendants(T + "SetItemField").Select(f => f.Element(T + "FieldURI")!.Attribute("FieldURI")!.Value));

        // Someone else's meeting, accepted in Neruna: Exchange answers the organizer.
        current = """
            <t:CalendarItem><t:ItemId Id="E1" ChangeKey="k1"/><t:Subject>Workshop</t:Subject><t:UID>U2</t:UID>
              <t:Start>2026-10-14T08:00:00Z</t:Start><t:End>2026-10-14T09:00:00Z</t:End><t:IsMeeting>true</t:IsMeeting><t:IsCancelled>false</t:IsCancelled><t:MyResponseType>NoResponseReceived</t:MyResponseType>
              <t:Organizer><t:Mailbox><t:Name>Marco</t:Name><t:EmailAddress>marco@example.com</t:EmailAddress></t:Mailbox></t:Organizer>
              <t:RequiredAttendees><t:Attendee><t:Mailbox><t:EmailAddress>anna@example.com</t:EmailAddress></t:Mailbox><t:ResponseType>NoResponseReceived</t:ResponseType></t:Attendee></t:RequiredAttendees>
              <t:StartTimeZone Id="W. Europe Standard Time"/></t:CalendarItem>
            """;
        server.On("CreateItem", _ => Ok("CreateItem", "<m:Items/>"));
        var invitation = Assert.Single((await calendar.SyncCalendarAsync(calendarInfo, new Dictionary<string, string?>(), ct)).AddedOrChanged);
        var accepted = ITip.ForOwnCalendar(invitation.ICalendarData, "anna@example.com", Participation.Accepted, null);
        await calendar.SaveAsync(calendarInfo, invitation with { ICalendarData = accepted }, ct);
        var answer = server.Operations("CreateItem").Single();
        Assert.Equal("SendAndSaveCopy", answer.Attribute("MessageDisposition")!.Value);
        Assert.Equal("E1", answer.Descendants(T + "AcceptItem").Single().Element(T + "ReferenceItemId")!.Attribute("Id")!.Value);

        // Deleting it declines it; a new event with someone else as organizer is refused (it would invite as the own).
        await calendar.DeleteAsync(calendarInfo, invitation, ct);
        Assert.Single(server.Operations("CreateItem").Last().Descendants(T + "DeclineItem"));
        Assert.Empty(server.Operations("DeleteItem"));
        await Assert.ThrowsAsync<EwsException>(() => calendar.SaveAsync(calendarInfo, invitation with { RemoteId = string.Empty }, ct));
    }

    [Fact]
    public void A_new_event_is_written_in_schema_order_with_its_time_zone()
    {
        var fields = EwsCalendarConverter.FieldsOf("""
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:new@neruna
            SUMMARY:Monatsrapport
            DTSTART;VALUE=DATE:20261102
            DTEND;VALUE=DATE:20261103
            RRULE:FREQ=MONTHLY;BYDAY=MO;BYSETPOS=1;COUNT=6
            ORGANIZER:mailto:anna@example.com
            ATTENDEE;ROLE=OPT-PARTICIPANT;CN=Beat:mailto:beat@example.com
            ATTENDEE;ROLE=REQ-PARTICIPANT:mailto:marco@example.com
            END:VEVENT
            END:VCALENDAR
            """.Replace("\n", "\r\n", StringComparison.Ordinal), out var attendees);
        var present = fields.Where(f => f.Value is not null).ToList();
        Assert.Equal(["Subject", "Body", "ReminderIsSet", "Start", "End", "IsAllDayEvent", "LegacyFreeBusyStatus", "RequiredAttendees", "OptionalAttendees", "Recurrence", "StartTimeZone", "EndTimeZone"],
            present.Select(f => f.Value!.Name.LocalName));
        Assert.Equal([("beat@example.com", true), ("marco@example.com", false)], attendees.Select(a => (a.Email, a.Optional)));
        var recurrence = present.Single(f => f.Field == "calendar:Recurrence").Value!;
        Assert.Equal(["RelativeMonthlyRecurrence", "NumberedRecurrence"], recurrence.Elements().Select(e => e.Name.LocalName));
        Assert.Equal(["1", "Monday", "First"], recurrence.Elements().First().Elements().Select(e => e.Value));
        Assert.Equal(["2026-11-02", "6"], recurrence.Elements().Last().Elements().Select(e => e.Value));
        Assert.Equal("true", present.Single(f => f.Field == "calendar:IsAllDayEvent").Value!.Value);
    }

    [Fact]
    public async Task Contacts_become_vcards_and_only_changed_fields_are_written_back()
    {
        var ct = TestContext.Current.CancellationToken;
        var (connection, factory, server, _) = Connect(ServiceKind.Contacts);
        var contact = """
            <t:Contact><t:ItemId Id="C1" ChangeKey="k1"/><t:Body BodyType="Text">Kennt sich mit Exchange aus</t:Body><t:DisplayName>Marco Rossi</t:DisplayName>
              <t:GivenName>Marco</t:GivenName><t:CompanyName>Muster AG</t:CompanyName>
              <t:EmailAddresses><t:Entry Key="EmailAddress1">marco@example.com</t:Entry><t:Entry Key="EmailAddress2">m.rossi@example.org</t:Entry></t:EmailAddresses>
              <t:PhysicalAddresses><t:Entry Key="Business"><t:Street>Bahnhofstrasse 1</t:Street><t:City>Bern</t:City><t:PostalCode>3011</t:PostalCode><t:CountryOrRegion>Schweiz</t:CountryOrRegion></t:Entry></t:PhysicalAddresses>
              <t:PhoneNumbers><t:Entry Key="BusinessPhone">+41 31 000 00 00</t:Entry><t:Entry Key="MobilePhone">+41 79 000 00 00</t:Entry><t:Entry Key="HomePhone"/></t:PhoneNumbers>
              <t:Birthday>1985-04-14T22:00:00Z</t:Birthday><t:JobTitle>Projektleiter</t:JobTitle><t:Surname>Rossi</t:Surname></t:Contact>
            """;
        server.On("FindItem", _ => Ok("FindItem", "<m:RootFolder IncludesLastItemInRange=\"true\"><t:Items><t:Contact><t:ItemId Id=\"C1\" ChangeKey=\"k1\"/></t:Contact><t:DistributionList><t:ItemId Id=\"L1\"/></t:DistributionList></t:Items></m:RootFolder>"));
        server.On("GetItem", _ => Ok("GetItem", "<m:Items>" + contact + "</m:Items>"));
        server.On("UpdateItem", _ => Ok("UpdateItem", "<m:Items/>"));
        await using var contacts = new EwsContactProviderFactory(factory).Create(connection);
        var book = new AddressBookInfo(connection.Id, "CONTACTS", "Kontakte", false);

        var synced = Assert.Single((await contacts.SyncAddressBookAsync(book, new Dictionary<string, string?>(), ct)).AddedOrChanged);
        Assert.Equal(["C1"], server.Operations("GetItem").Single().Descendants(T + "ItemId").Select(i => i.Attribute("Id")!.Value)); // no distribution list
        var card = VCardReader.Read(synced.VCardData);
        Assert.Equal(("Marco", "Rossi", "Muster AG", "Projektleiter"), (card.GivenName, card.FamilyName, card.Organization, card.Title));
        Assert.Equal(["marco@example.com", "m.rossi@example.org"], card.Emails.Select(e => e.Value));
        Assert.Equal([("+41 31 000 00 00", "work"), ("+41 79 000 00 00", "cell")], card.Phones.Select(p => (p.Value, p.Kind!)));
        Assert.Equal(new ContactBirthday(1985, 4, 15), card.Birthday);
        Assert.Contains("ADR;TYPE=WORK:;;Bahnhofstrasse 1;Bern;;3011;Schweiz", synced.VCardData, StringComparison.Ordinal);

        // A new mobile number and no second address: three changes, nothing else.
        var draft = ContactDraft.FromVCard(synced.VCardData);
        draft = draft with { Emails = [draft.Emails[0]], Phones = [draft.Phones[0], new ContactField("+41 78 111 11 11", "cell")] };
        await contacts.SaveAsync(book, synced with { VCardData = draft.ToVCard(synced.VCardData) }, ct);
        var changes = server.Operations("UpdateItem").Single().Descendants(T + "Updates").Single().Elements().ToList();
        Assert.Equal(["DeleteItemField contacts:EmailAddress/EmailAddress2", "SetItemField contacts:PhoneNumber/MobilePhone"],
            changes.Select(c => c.Name.LocalName + " " + c.Elements().First().Attribute("FieldURI")!.Value + "/" + c.Elements().First().Attribute("FieldIndex")!.Value));
        Assert.Equal("+41 78 111 11 11", changes[1].Descendants(T + "Entry").Single().Value);
    }

    [Fact]
    public async Task Contact_certificates_are_read_from_and_written_to_the_outlook_property()
    {
        var ct = TestContext.Current.CancellationToken;
        var (connection, factory, server, _) = Connect(ServiceKind.Contacts);
        var old = TestPki.User("Lea Keller", "lea@example.com").Certificate.GetEncoded();
        var contact = $"""
            <t:Contact><t:ItemId Id="C1" ChangeKey="k1"/>
              <t:ExtendedProperty><t:ExtendedFieldURI PropertyTag="0x3a70" PropertyType="BinaryArray"/><t:Values><t:Value>{Convert.ToBase64String(old)}</t:Value></t:Values></t:ExtendedProperty>
              <t:DisplayName>Lea Keller</t:DisplayName><t:GivenName>Lea</t:GivenName>
              <t:EmailAddresses><t:Entry Key="EmailAddress1">lea@example.com</t:Entry></t:EmailAddresses><t:Surname>Keller</t:Surname></t:Contact>
            """;
        server.On("FindItem", _ => Ok("FindItem", "<m:RootFolder IncludesLastItemInRange=\"true\"><t:Items><t:Contact><t:ItemId Id=\"C1\" ChangeKey=\"k1\"/></t:Contact></t:Items></m:RootFolder>"));
        server.On("GetItem", _ => Ok("GetItem", "<m:Items>" + contact + "</m:Items>"));
        server.On("UpdateItem", _ => Ok("UpdateItem", "<m:Items/>"));
        await using var contacts = new EwsContactProviderFactory(factory).Create(connection);
        var book = new AddressBookInfo(connection.Id, "CONTACTS", "Kontakte", false);

        var synced = Assert.Single((await contacts.SyncAddressBookAsync(book, new Dictionary<string, string?>(), ct)).AddedOrChanged);
        Assert.Equal(old, Assert.Single(ContactCertificates.Read(synced.VCardData)));

        // A second certificate: the property gets both, nothing else changes.
        var added = TestPki.User("Lea Keller", "lea@example.com").Certificate.GetEncoded();
        await contacts.SaveAsync(book, synced with { VCardData = ContactCertificates.Add(synced.VCardData, added) }, ct);
        var change = Assert.Single(server.Operations("UpdateItem").Single().Descendants(T + "Updates").Single().Elements());
        Assert.Equal("SetItemField", change.Name.LocalName);
        Assert.Equal([Convert.ToBase64String(old), Convert.ToBase64String(added)], change.Descendants(T + "Value").Select(v => v.Value));
    }

    // ── fake server ──

    private static XElement Item(string xml) =>
        XElement.Parse(xml.Replace("<t:CalendarItem>", $"<t:CalendarItem xmlns:t=\"{T.NamespaceName}\">", StringComparison.Ordinal));

    private static string EventIcs(string summary) => $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:U1\r\nSUMMARY:{summary}\r\nDTSTART:20261014T080000Z\r\nDTEND:20261014T090000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

    private static string Message(string operation, string inner) =>
        $"<m:{operation}ResponseMessage ResponseClass=\"Success\"><m:ResponseCode>NoError</m:ResponseCode>{inner}</m:{operation}ResponseMessage>";

    private static string Error(string operation, string code) =>
        $"<m:{operation}ResponseMessage ResponseClass=\"Error\"><m:MessageText>{code}</m:MessageText><m:ResponseCode>{code}</m:ResponseCode></m:{operation}ResponseMessage>";

    private static string Many(string operation, IEnumerable<string> messages) =>
        $"<m:{operation}Response><m:ResponseMessages>{string.Concat(messages)}</m:ResponseMessages></m:{operation}Response>";

    private static string Ok(string operation, string inner) => Many(operation, [Message(operation, inner)]);

    private sealed class FakeEws : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<XElement, string>> _answers = new(StringComparer.Ordinal);
        private readonly List<XElement> _operations = [];

        public (HttpStatusCode Status, string Body)? Raw { get; set; }

        /// <summary>The raw answer only for the next request.</summary>
        public bool RawOnce { get; set; }

        public int Requests { get; private set; }

        public string? Authorization { get; private set; }

        public void On(string operation, Func<XElement, string> answer) => _answers[operation] = answer;

        public IEnumerable<XElement> Operations(string name) => _operations.Where(o => o.Name.LocalName == name);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Authorization = request.Headers.Authorization?.ToString();
            if (Raw is { } raw)
            {
                if (RawOnce)
                {
                    Raw = null;
                    RawOnce = false;
                }

                return new HttpResponseMessage(raw.Status) { Content = new StringContent(raw.Body) };
            }

            var envelope = XDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("Exchange2013", envelope.Descendants(T + "RequestServerVersion").Single().Attribute("Version")!.Value);
            var operation = envelope.Descendants(XName.Get("Body", "http://schemas.xmlsoap.org/soap/envelope/")).Single().Elements().Single();
            _operations.Add(operation);

            // As Exchange: lists the schema wants at least one entry in may not be empty.
            if (operation.Descendants(T + "AdditionalProperties").Any(p => !p.HasElements))
            {
                const string Fault = """<?xml version="1.0" encoding="utf-8"?><s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><s:Fault><faultcode xmlns:a="http://schemas.microsoft.com/exchange/services/2006/types">a:ErrorSchemaValidation</faultcode><faultstring xml:lang="en-US">The request failed schema validation: The element 'AdditionalProperties' has incomplete content.</faultstring></s:Fault></s:Body></s:Envelope>""";
                return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent(Fault, Encoding.UTF8, "text/xml") };
            }

            var body = _answers.TryGetValue(operation.Name.LocalName, out var answer)
                ? answer(operation)
                : Ok(operation.Name.LocalName, "<m:RootFolder IncludesLastItemInRange=\"true\"><t:Items/><t:Folders/></m:RootFolder>");
            var xml = $"""<?xml version="1.0" encoding="utf-8"?><s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" xmlns:m="{M.NamespaceName}" xmlns:t="{T.NamespaceName}"><s:Body>{body}</s:Body></s:Envelope>""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "text/xml") };
        }
    }
}

/// <summary>Secrets in memory, for tests that need no keychain.</summary>
internal sealed class MemoryCredentialStore : ICredentialStore
{
    public Dictionary<Guid, string> Secrets { get; } = [];

    public string Location => "memory";

    public Task<string?> GetSecretAsync(Guid connectionId, CancellationToken cancellationToken = default) => Task.FromResult(Secrets.GetValueOrDefault(connectionId));

    public Task SetSecretAsync(Guid connectionId, string secret, CancellationToken cancellationToken = default)
    {
        Secrets[connectionId] = secret;
        return Task.CompletedTask;
    }

    public Task DeleteSecretAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        Secrets.Remove(connectionId);
        return Task.CompletedTask;
    }
}
