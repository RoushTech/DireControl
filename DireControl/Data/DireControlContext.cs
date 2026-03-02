using DireControl.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace DireControl.Data;

public class DireControlContext(DbContextOptions<DireControlContext> options) : DbContext(options)
{
    public DbSet<Station> Stations => Set<Station>();
    public DbSet<Packet> Packets => Set<Packet>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<Geofence> Geofences => Set<Geofence>();
    public DbSet<ProximityRule> ProximityRules => Set<ProximityRule>();
    public DbSet<StationStatistic> StationStatistics => Set<StationStatistic>();
    public DbSet<Radio> Radios => Set<Radio>();
    public DbSet<OwnBeacon> OwnBeacons => Set<OwnBeacon>();
    public DbSet<DigiConfirmation> DigiConfirmations => Set<DigiConfirmation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DireControlContext).Assembly);
    }
}
