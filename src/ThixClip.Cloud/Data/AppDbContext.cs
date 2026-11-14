using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ThixClip.Cloud.Data;

/// <summary>
/// The database (PostgreSQL). Tables are PascalCase and singular, the key is always <c>Id</c> (BIGINT), every record has CreateDate, CreateUserId,
/// UpdateDate and UpdateUserId (filled in <see cref="SaveChangesAsync(bool, CancellationToken)"/> with the person or device of the request), and a
/// record with an <c>UpdateState</c> is deleted softly (0 = deleted, 1 = created, 2 = updated).
/// Only the business layer (<c>ThixClip.Cloud.Business</c>) uses it.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options, IHttpContextAccessor? accessor = null) : DbContext(options)
{
    /// <summary>The item of the request that names the acting person (set by the cookie sign-in and by the device token).</summary>
    public const string ActorItem = "ThixClip.ActorId";

    /// <summary>The one sequence every change of a clip takes its revision from.</summary>
    public const string RevisionSequence = "ClipRevision";

    public DbSet<User> Users => Set<User>();

    public DbSet<UserSession> Sessions => Set<UserSession>();

    public DbSet<Device> Devices => Set<Device>();

    public DbSet<ClipEntry> Clips => Set<ClipEntry>();

    public DbSet<ClipImage> ClipImages => Set<ClipImage>();

    /// <summary>Who changes something outside of a request (tests, background work); a request names its person itself.</summary>
    public long? ActingUserId { get; set; }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasSequence<long>(RevisionSequence).StartsAt(1).IncrementsBy(1);

        b.Entity<User>(e =>
        {
            e.ToTable("User");
            e.Property(x => x.UserName).HasMaxLength(64);
            e.Property(x => x.DisplayName).HasMaxLength(120);
            e.Property(x => x.PasswordHash).HasMaxLength(200);
            e.Property(x => x.Theme).HasMaxLength(10);
            e.HasIndex(x => x.UserName).IsUnique().HasFilter("\"UpdateState\" <> 0");
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        b.Entity<UserSession>(e =>
        {
            e.ToTable("UserSession");
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.UserAgent).HasMaxLength(300);
            e.Property(x => x.IpAddress).HasMaxLength(64);
        });

        b.Entity<Device>(e =>
        {
            e.ToTable("Device");
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Platform).HasMaxLength(40);
            e.Property(x => x.AppVersion).HasMaxLength(60);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        // No query filter: the tombstones of deleted clips are part of the sync. The business layer asks for live clips itself.
        b.Entity<ClipEntry>(e =>
        {
            e.ToTable("ClipEntry");
            e.HasIndex(x => new { x.UserId, x.SyncId }).IsUnique();
            e.HasIndex(x => new { x.UserId, x.Revision });
            e.HasIndex(x => new { x.UserId, x.ContentHash });
            e.HasIndex(x => new { x.UserId, x.CopiedDate });
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Kind).HasConversion<int>();
            e.Property(x => x.ContentHash).HasMaxLength(64);
            e.Property(x => x.Source).HasMaxLength(100);
        });

        b.Entity<ClipImage>(e =>
        {
            e.ToTable("ClipImage");
            e.HasIndex(x => x.ClipEntryId).IsUnique();
            e.HasOne(x => x.ClipEntry).WithOne(x => x.Image).HasForeignKey<ClipImage>(x => x.ClipEntryId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        Stamp();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        Stamp();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>Fills the audit columns, and turns the delete of a soft record into UpdateState 0.</summary>
    private void Stamp()
    {
        var now = DateTime.UtcNow;
        var actor = ActingUserId ?? accessor?.HttpContext?.Items[ActorItem] as long?;
        foreach (var entry in ChangeTracker.Entries<Entity>().ToList())
        {
            var entity = entry.Entity;
            if (entry.State == EntityState.Deleted && entity is SoftEntity soft)
            {
                entry.State = EntityState.Modified;
                soft.UpdateState = SoftEntity.Deleted;
            }

            switch (entry.State)
            {
                case EntityState.Added:
                    if (entity.CreateDate == default)
                    {
                        entity.CreateDate = now;
                    }

                    entity.CreateUserId ??= actor;
                    entity.UpdateDate = now;
                    entity.UpdateUserId = actor ?? entity.CreateUserId;
                    break;
                case EntityState.Modified:
                    entity.UpdateDate = now;
                    entity.UpdateUserId = actor ?? entity.UpdateUserId;
                    if (entity is SoftEntity { UpdateState: not SoftEntity.Deleted } updated)
                    {
                        updated.UpdateState = SoftEntity.Updated;
                    }

                    break;
            }
        }
    }
}

/// <summary>For <c>dotnet ef migrations add</c>: no database is needed to create a migration.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Port=4952;Database=thixclip;Username=thixclip;Password=thixclip").Options);
}
