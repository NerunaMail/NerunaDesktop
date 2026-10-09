using Microsoft.EntityFrameworkCore;
using Neruna.Core.Accounts;
using Neruna.Core.Mail;
using Neruna.Core.Security;

namespace Neruna.Storage;

/// <summary>
/// Local SQLite cache. Holds metadata only: raw MIME lives on disk (<see cref="MessageContentFiles"/>),
/// secrets live in the OS credential store.
/// </summary>
/// <remarks>
/// The schema is managed by EF Core migrations (folder <c>Migrations</c>). After changing the model:
/// <c>dotnet ef migrations add /// <remarks>Schema is created with EnsureCreated during the prototype phase; EF migrations follow before the first release.</remarks>lt;Name/// <remarks>Schema is created with EnsureCreated during the prototype phase; EF migrations follow before the first release.</remarks>gt; --project src/Client/Neruna.Storage</c> (run in <c>desktop/</c>).
/// </remarks>
public sealed class NerunaDbContext(DbContextOptions<NerunaDbContext> options) : DbContext(options)
{
    internal DbSet<AccountEntity> Accounts => Set<AccountEntity>();

    internal DbSet<ConnectionEntity> Connections => Set<ConnectionEntity>();

    internal DbSet<MailFolderEntity> MailFolders => Set<MailFolderEntity>();

    internal DbSet<MessageEntity> Messages => Set<MessageEntity>();

    internal DbSet<CalendarEntity> Calendars => Set<CalendarEntity>();

    internal DbSet<CalendarObjectEntity> CalendarObjects => Set<CalendarObjectEntity>();

    internal DbSet<AddressBookEntity> AddressBooks => Set<AddressBookEntity>();

    internal DbSet<ContactEntity> Contacts => Set<ContactEntity>();

    internal DbSet<CertificateEntity> Certificates => Set<CertificateEntity>();

    internal DbSet<SettingEntity> Settings => Set<SettingEntity>();

    internal DbSet<SignatureEntity> Signatures => Set<SignatureEntity>();

    internal DbSet<TextTemplateEntity> TextTemplates => Set<TextTemplateEntity>();

    internal DbSet<ReminderStateEntity> ReminderStates => Set<ReminderStateEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AccountEntity>(e =>
        {
            e.HasMany(a => a.Connections).WithOne().HasForeignKey(c => c.AccountId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MailFolderEntity>(e =>
        {
            e.HasIndex(f => new { f.ConnectionId, f.RemoteId }).IsUnique();
            e.HasOne<ConnectionEntity>().WithMany().HasForeignKey(f => f.ConnectionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MessageEntity>(e =>
        {
            e.HasIndex(m => new { m.FolderId, m.RemoteId }).IsUnique();
            e.HasIndex(m => new { m.FolderId, m.DateUnixMs });
            e.HasOne<MailFolderEntity>().WithMany().HasForeignKey(m => m.FolderId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CalendarEntity>(e =>
        {
            e.HasIndex(c => new { c.ConnectionId, c.RemoteId }).IsUnique();
            e.HasOne<ConnectionEntity>().WithMany().HasForeignKey(c => c.ConnectionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CalendarObjectEntity>(e =>
        {
            e.HasIndex(o => new { o.CalendarId, o.RemoteId }).IsUnique();
            e.HasOne<CalendarEntity>().WithMany().HasForeignKey(o => o.CalendarId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AddressBookEntity>(e =>
        {
            e.HasIndex(b => new { b.ConnectionId, b.RemoteId }).IsUnique();
            e.HasOne<ConnectionEntity>().WithMany().HasForeignKey(b => b.ConnectionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CertificateEntity>(e => e.HasKey(c => c.Thumbprint));
        modelBuilder.Entity<SettingEntity>(e => e.HasKey(s => s.Key));
        modelBuilder.Entity<ReminderStateEntity>(e => e.HasKey(r => r.Key));

        modelBuilder.Entity<ContactEntity>(e =>
        {
            e.HasIndex(c => new { c.AddressBookId, c.RemoteId }).IsUnique();
            e.HasOne<AddressBookEntity>().WithMany().HasForeignKey(c => c.AddressBookId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

internal sealed class AccountEntity
{
    public Guid Id { get; set; }

    public required string DisplayName { get; set; }

    public string? EmailAddress { get; set; }

    /// <summary>Position chosen by the user (folder tree, settings); equal values fall back to the name.</summary>
    public int SortOrder { get; set; }

    public List<ConnectionEntity> Connections { get; set; } = [];
}

internal sealed class ConnectionEntity
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    public ServiceKind Kind { get; set; }

    public required string ProviderId { get; set; }

    /// <summary>JSON object of non-secret, provider-specific settings.</summary>
    public required string SettingsJson { get; set; }
}

internal sealed class MailFolderEntity
{
    public long Id { get; set; }

    public Guid ConnectionId { get; set; }

    public required string RemoteId { get; set; }

    public required string Name { get; set; }

    public string? ParentRemoteId { get; set; }

    public FolderRole Role { get; set; }

    public int TotalCount { get; set; }

    public int UnreadCount { get; set; }

    public string? SyncState { get; set; }
}

internal sealed class MessageEntity
{
    public long Id { get; set; }

    public long FolderId { get; set; }

    public required string RemoteId { get; set; }

    public string? MessageId { get; set; }

    public string? InReplyTo { get; set; }

    public required string Subject { get; set; }

    public string? FromName { get; set; }

    public string? FromAddress { get; set; }

    /// <summary>JSON array of addresses.</summary>
    public required string ToJson { get; set; }

    /// <summary>Unix milliseconds (UTC), sortable in SQLite unlike DateTimeOffset text.</summary>
    public long DateUnixMs { get; set; }

    public MessageFlags Flags { get; set; }

    public long Size { get; set; }

    public bool HasAttachments { get; set; }

    public string? Preview { get; set; }

    public MessageSecurity Security { get; set; }
}

internal sealed class CalendarEntity
{
    public long Id { get; set; }

    public Guid ConnectionId { get; set; }

    public required string RemoteId { get; set; }

    public required string Name { get; set; }

    public string? Color { get; set; }

    public bool IsReadOnly { get; set; }

    public string? SyncState { get; set; }
}

internal sealed class CalendarObjectEntity
{
    public long Id { get; set; }

    public long CalendarId { get; set; }

    public required string RemoteId { get; set; }

    public string? ETag { get; set; }

    public required string Data { get; set; }
}

internal sealed class AddressBookEntity
{
    public long Id { get; set; }

    public Guid ConnectionId { get; set; }

    public required string RemoteId { get; set; }

    public required string Name { get; set; }

    public bool IsReadOnly { get; set; }

    public string? SyncState { get; set; }
}

internal sealed class ContactEntity
{
    public long Id { get; set; }

    public long AddressBookId { get; set; }

    public required string RemoteId { get; set; }

    public string? ETag { get; set; }

    public required string Data { get; set; }
}

internal sealed class CertificateEntity
{
    public required string Thumbprint { get; set; }

    public required byte[] Der { get; set; }

    /// <summary>The original PKCS#12 container (encrypted with its own password) for own certificates.</summary>
    public byte[]? Pkcs12 { get; set; }

    public CertificateSource Source { get; set; }

    public long AddedUnixMs { get; set; }
}

internal sealed class SettingEntity
{
    public required string Key { get; set; }

    public string? Value { get; set; }
}

/// <summary>A dismissed or snoozed reminder (times as Unix milliseconds, so SQLite can compare them).</summary>
internal sealed class ReminderStateEntity
{
    public required string Key { get; set; }

    public long? SnoozedUntilUnixMs { get; set; }

    public bool Dismissed { get; set; }

    public long ExpiresUnixMs { get; set; }
}

internal sealed class TextTemplateEntity
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    /// <summary>"tel" → typing "tel::" while writing inserts the template.</summary>
    public string? Shortcut { get; set; }

    public required string Html { get; set; }

    public required string Source { get; set; }

    public long UpdatedUnixMs { get; set; }
}

internal sealed class SignatureEntity
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public required string Html { get; set; }

    public required string Source { get; set; }

    public long UpdatedUnixMs { get; set; }
}
